// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.Immutable;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Periphery;
using Periphery.Bootloader.Efm8.Usb;
using Periphery.Treehopper.Firmware;
using Periphery.Usb;

namespace Periphery.Treehopper.Control;

/// <summary>
/// The imperative shell of the control app (ADR-0052): it owns all hardware — the
/// hotplug <see cref="DeviceWatcher"/>, the single open <see cref="TreehopperBoard"/>
/// session for the selected board, the reflash flow, and I2C scans — and translates
/// between that messy async world and the pure core. Hardware callbacks become
/// <see cref="AppEvent"/>s folded into <see cref="State"/> by <see cref="AppReducer"/>;
/// <see cref="AppIntent"/>s are executed against the metal.
/// </summary>
/// <remarks>
/// <para>
/// One front-end-agnostic brain: the CLI and the Avalonia GUI both read
/// <see cref="State"/>, subscribe to <see cref="StateChanged"/>, and call
/// <see cref="DispatchAsync"/>. Neither contains hardware logic.
/// </para>
/// <para>
/// <b>Serialization.</b> Every hardware-touching operation (discovery handling, version
/// reads, session open/close, intent execution, reflash) runs under one gate, so the
/// board is never touched concurrently. The report pump runs outside the gate and only
/// folds state (which is independently locked).
/// </para>
/// <para>
/// <b>Single session.</b> Only the selected board is held open (to stream its reports
/// and accept pin / I2C ops). Selecting another board closes the previous session and
/// opens the new one. Opening a board sends <c>ConfigureDevice</c> (resets its transient
/// config) — expected for a control app taking ownership.
/// </para>
/// </remarks>
public sealed class TreehopperControlService : IAsyncDisposable
{
    private readonly TreehopperControlOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);          // serializes hardware ops
    private readonly object _stateLock = new();                // guards _state + StateChanged
    private readonly CancellationTokenSource _cts = new();

    private AppState _state = AppState.Empty;
    private DeviceWatcher? _watcher;
    private Session? _session;                                  // the one open board handle (or null)
    private bool _streamSelected;                               // auto-stream the selected board's reports
    private bool _disposed;

    public TreehopperControlService(TreehopperControlOptions? options = null)
        => _options = options ?? new TreehopperControlOptions();

    /// <summary>The current application state. Thread-safe snapshot.</summary>
    public AppState State { get { lock (_stateLock) return _state; } }

    /// <summary>Raised after every state change with the new <see cref="AppState"/>.</summary>
    public event EventHandler<AppState>? StateChanged;

    // ── Lifecycle ────────────────────────────────────────────────────────

    /// <summary>
    /// Seeds the firmware target, enumerates the currently connected boards, and starts
    /// the hotplug watcher. Call once before dispatching intents.
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_options.FirmwareTargetVersion is int target)
            Apply(new AppEvent.FirmwareTargetSet(target));

        await RunExclusiveAsync(async () =>
        {
            var boards = await TreehopperBoard.EnumerateAsync(ct).ConfigureAwait(false);
            foreach (var info in boards)
                Apply(new AppEvent.BoardDiscovered(ToIdentity(info)));
            foreach (var info in boards)
                await ReadVersionAsync(info.Id, ct).ConfigureAwait(false);
            await ReconcileSessionAsync(ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        _watcher = Devices.Watch().WithUsbId(TreehopperBoard.Vid, TreehopperBoard.Pid);
        _watcher.Appeared += OnAppeared;
        _watcher.Disappeared += OnDisappeared;
        await _watcher.StartAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Executes a user intent against the hardware, emitting the resulting events.</summary>
    public Task DispatchAsync(AppIntent intent, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(intent);
        return RunExclusiveAsync(() => ExecuteAsync(intent, ct), ct);
    }

    /// <summary>
    /// Turns live report streaming for the selected board on or off. Off by default, so
    /// a read-only consumer (e.g. <c>treehopper list</c>) never opens a board handle (which
    /// would send <c>ConfigureDevice</c>). The GUI / a <c>watch</c> view turns it on to show
    /// the live pin grid. Pin / I2C intents still open a session on demand regardless.
    /// </summary>
    public Task SetLiveStreamingAsync(bool enabled, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return RunExclusiveAsync(async () =>
        {
            _streamSelected = enabled;
            if (enabled) await ReconcileSessionAsync(ct).ConfigureAwait(false);
            else await CloseSessionAsync().ConfigureAwait(false);
        }, ct);
    }

    // ── Intent execution (under the gate) ────────────────────────────────

    private async Task ExecuteAsync(AppIntent intent, CancellationToken ct)
    {
        switch (intent)
        {
            case AppIntent.RefreshBoards:
                var boards = await TreehopperBoard.EnumerateAsync(ct).ConfigureAwait(false);
                foreach (var info in boards) Apply(new AppEvent.BoardDiscovered(ToIdentity(info)));
                foreach (var info in boards) await ReadVersionAsync(info.Id, ct).ConfigureAwait(false);
                await ReconcileSessionAsync(ct).ConfigureAwait(false);
                break;

            case AppIntent.SelectBoard sel:
                Apply(new AppEvent.SelectionChanged(sel.Id));
                await ReconcileSessionAsync(ct).ConfigureAwait(false);
                break;

            case AppIntent.SetPinMode pm:
                await WithSessionAsync(pm.Id, async board =>
                {
                    await board.ReconcileAsync(c => c with
                    {
                        Pins = pm.Mode == PinMode.Reserved
                            ? c.Pins.Remove((byte)pm.Pin)
                            : c.Pins.SetItem((byte)pm.Pin, new PinConfig(pm.Mode))
                    }, ct).ConfigureAwait(false);
                    Apply(new AppEvent.PinModeChanged(pm.Id, pm.Pin, pm.Mode));
                }, ct).ConfigureAwait(false);
                break;

            case AppIntent.DriveOutput drive:
                await DriveAsync(drive.Id, drive.Pin, drive.High, ct).ConfigureAwait(false);
                break;

            case AppIntent.ToggleOutput tog:
                bool next = !(State.Find(tog.Id)?.Pins is { } pins && tog.Pin >= 0 && tog.Pin < pins.Length && pins[tog.Pin].High);
                await DriveAsync(tog.Id, tog.Pin, next, ct).ConfigureAwait(false);
                break;

            case AppIntent.ScanI2c scan:
                await ScanI2cAsync(scan.Id, ct).ConfigureAwait(false);
                break;

            case AppIntent.UpdateFirmware uf:
                await UpdateFirmwareAsync(uf.Id, _options.FirmwareImage, ct).ConfigureAwait(false);
                break;
        }
    }

    private Task DriveAsync(DeviceId id, int pin, bool high, CancellationToken ct) =>
        WithSessionAsync(id, async board =>
        {
            await board.ReconcileAsync(c => c with
            {
                Pins = c.Pins.SetItem((byte)pin, new PinConfig(PinMode.PushPullOutput, high))
            }, ct).ConfigureAwait(false);
            // The firmware does not report host-driven output changes, so the level is
            // host-authoritative — record it directly rather than waiting for a report.
            Apply(new AppEvent.OutputDriven(id, pin, high));
        }, ct);

    private async Task ScanI2cAsync(DeviceId id, CancellationToken ct)
    {
        var board = await EnsureSessionAsync(id, ct).ConfigureAwait(false);
        if (board is null)
        {
            Apply(new AppEvent.OperationFailed(id, "Could not open the board for an I2C scan."));
            return;
        }

        Apply(new AppEvent.I2cScanStarted(id));
        try
        {
            var responders = ImmutableArray.CreateBuilder<byte>();
            await using var i2c = await board.UseI2cAsync(ct: ct).ConfigureAwait(false);
            // Standard 7-bit address range (0x08–0x77); the rest are reserved.
            for (byte addr = 0x08; addr <= 0x77; addr++)
                if (await i2c.PingAsync(addr, ct).ConfigureAwait(false))
                    responders.Add(addr);
            Apply(new AppEvent.I2cScanFinished(id, responders.ToImmutable()));
        }
        catch (Exception ex)
        {
            Apply(new AppEvent.I2cScanFinished(id, ImmutableArray<byte>.Empty));
            Apply(new AppEvent.OperationFailed(id, $"I2C scan failed: {ex.Message}"));
        }
    }

    /// <summary>
    /// Flashes a board with an explicitly-supplied firmware file (e.g. one the GUI picked),
    /// bypassing the configured <see cref="TreehopperControlOptions.FirmwareImage"/>. The
    /// format is inferred from <paramref name="sourceName"/>'s extension and verified
    /// against <paramref name="firmware"/>'s content (a <c>.hex</c> image is converted to
    /// boot records, a mismatched file is refused via <see cref="AppEvent.OperationFailed"/>).
    /// Emits the same firmware events as the <see cref="AppIntent.UpdateFirmware"/> intent.
    /// </summary>
    /// <param name="id">The board id.</param>
    /// <param name="firmware">The raw firmware file bytes.</param>
    /// <param name="sourceName">The file name (used to infer the format from its extension).</param>
    /// <param name="ct">Cancellation token.</param>
    public Task FlashAsync(DeviceId id, byte[] firmware, string sourceName, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(firmware);
        ArgumentException.ThrowIfNullOrEmpty(sourceName);
        return RunExclusiveAsync(() => FlashResolvedAsync(id, firmware, sourceName, ct), ct);
    }

    private async Task FlashResolvedAsync(DeviceId id, byte[] firmware, string sourceName, CancellationToken ct)
    {
        byte[] records;
        try
        {
            // Infer + verify the format and convert (.hex -> boot records) on the file
            // bytes, before any board is touched. A mismatched/malformed file is refused.
            records = Efm8FirmwareImage.ToBootRecords(firmware, sourceName, Efm8BootOptions.Ub1);
        }
        catch (Efm8BootFormatException ex)
        {
            Apply(new AppEvent.OperationFailed(id, ex.Message));
            return;
        }
        await UpdateFirmwareAsync(id, records, ct).ConfigureAwait(false);
    }

    private async Task UpdateFirmwareAsync(DeviceId id, byte[]? image, CancellationToken ct)
    {
        if (image is null)
        {
            Apply(new AppEvent.OperationFailed(id, "No firmware image is configured."));
            return;
        }

        // The reflash needs an exclusively-owned board it can dispose; close any session.
        if (_session?.Id == id)
            await CloseSessionAsync().ConfigureAwait(false);

        var info = await Devices.Enumerate().WithId(id).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (info is null)
        {
            Apply(new AppEvent.OperationFailed(id, "Board not found."));
            return;
        }

        Apply(new AppEvent.FirmwareUpdateStarted(id));
        var board = await TreehopperBoard.OpenAsync(info, ct).ConfigureAwait(false);
        var progress = new InlineProgress(p => Apply(new AppEvent.FirmwareProgressed(id, p.RecordsSent, p.TotalRecords)));
        try
        {
            var result = await TreehopperFirmwareUpdate.ReflashAsync(
                board, new MemoryStream(image, writable: false),
                Efm8FlashConfirmation.ConfirmEraseAndReflash,
                new TreehopperReflashOptions(), progress, ct).ConfigureAwait(false);

            if (result.Success)
            {
                int? newVersion = await TryReadVersionAsync(id, ct).ConfigureAwait(false);
                Apply(new AppEvent.FirmwareUpdateFinished(id, Success: true, NewVersion: newVersion));
            }
            else
            {
                Apply(new AppEvent.FirmwareUpdateFinished(id, Success: false, Message: result.Upload.Describe()));
            }
        }
        catch (Exception ex)
        {
            try { await board.DisposeAsync().ConfigureAwait(false); } catch { /* idempotent */ }
            Apply(new AppEvent.FirmwareUpdateFinished(id, Success: false, Message: ex.Message));
        }

        await ReconcileSessionAsync(ct).ConfigureAwait(false);
    }

    // ── Session management (under the gate) ──────────────────────────────

    private async Task ReconcileSessionAsync(CancellationToken ct)
    {
        // Drop a session whose board vanished from the bus.
        if (_session is not null && State.Find(_session.Id) is null)
            await CloseSessionAsync().ConfigureAwait(false);

        // Only auto-open/track the selected board's session while streaming is on.
        if (!_streamSelected) return;

        var selected = State.SelectedBoardId is { } id ? State.Find(id) : null;
        if (selected is { Connection: BoardConnection.Application })
            await EnsureSessionAsync(selected.Id, ct).ConfigureAwait(false);
        else if (_session is not null)
            await CloseSessionAsync().ConfigureAwait(false);
    }

    /// <summary>Ensures the single session is open for <paramref name="id"/>; returns the board, or null on failure.</summary>
    private async Task<TreehopperBoard?> EnsureSessionAsync(DeviceId id, CancellationToken ct)
    {
        if (_session?.Id == id) return _session.Board;
        if (_session is not null) await CloseSessionAsync().ConfigureAwait(false);
        await OpenSessionAsync(id, ct).ConfigureAwait(false);
        return _session?.Board;
    }

    private async Task OpenSessionAsync(DeviceId id, CancellationToken ct)
    {
        var info = await Devices.Enumerate().WithId(id).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (info is null) return;

        TreehopperBoard board;
        try { board = await TreehopperBoard.OpenAsync(info, ct).ConfigureAwait(false); }
        catch (Exception ex) { Apply(new AppEvent.OperationFailed(id, $"Open failed: {ex.Message}")); return; }

        var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        var session = new Session(id, board, sessionCts);
        session.Pump = Task.Run(() => PumpReportsAsync(id, board, sessionCts.Token));
        _session = session;

        // Version straight from the open session (no separate device open).
        Apply(new AppEvent.BoardVersionRead(id, board.Version));
    }

    private async Task CloseSessionAsync()
    {
        var session = _session;
        _session = null;
        if (session is null) return;

        session.Cts.Cancel();
        try { await session.Board.DisposeAsync().ConfigureAwait(false); } catch { /* best-effort */ }
        if (session.Pump is not null)
        {
            try { await session.Pump.ConfigureAwait(false); } catch { /* shutting down */ }
        }
        session.Cts.Dispose();
    }

    private async Task PumpReportsAsync(DeviceId id, TreehopperBoard board, CancellationToken ct)
    {
        try
        {
            await foreach (var report in board.Reports.WithCancellation(ct).ConfigureAwait(false))
                Apply(new AppEvent.ReportReceived(id, report));
        }
        catch (OperationCanceledException) { /* session closed */ }
        catch (Exception ex) { Apply(new AppEvent.OperationFailed(id, $"Report stream stopped: {ex.Message}")); }
    }

    // ── Hotplug ──────────────────────────────────────────────────────────

    private void OnAppeared(object? sender, DeviceChangeEventArgs e)
        => _ = RunExclusiveAsync(async () =>
        {
            Apply(new AppEvent.BoardDiscovered(ToIdentity(e.Device)));
            await ReadVersionAsync(e.Device.Id, _cts.Token).ConfigureAwait(false);
            await ReconcileSessionAsync(_cts.Token).ConfigureAwait(false);
        }, _cts.Token);

    private void OnDisappeared(object? sender, DeviceChangeEventArgs e)
        => _ = RunExclusiveAsync(async () =>
        {
            // Re-verify absence: guards against stale events, including the transient drop
            // while a board re-enumerates through the bootloader during a flash.
            bool present = await Devices.Enumerate().WithId(e.Device.Id).AnyAsync(_cts.Token).ConfigureAwait(false);
            if (present) return;

            Apply(new AppEvent.BoardRemoved(e.Device.Id));
            await ReconcileSessionAsync(_cts.Token).ConfigureAwait(false);
        }, _cts.Token);

    // ── Helpers ──────────────────────────────────────────────────────────

    private async Task WithSessionAsync(DeviceId id, Func<TreehopperBoard, Task> action, CancellationToken ct)
    {
        var board = await EnsureSessionAsync(id, ct).ConfigureAwait(false);
        if (board is null)
        {
            Apply(new AppEvent.OperationFailed(id, "Could not open the board."));
            return;
        }
        try { await action(board).ConfigureAwait(false); }
        catch (Exception ex) { Apply(new AppEvent.OperationFailed(id, ex.Message)); }
    }

    private async Task ReadVersionAsync(DeviceId id, CancellationToken ct)
    {
        // The open session already knows its version; don't double-open the device.
        if (_session?.Id == id) return;
        int? version = await TryReadVersionAsync(id, ct).ConfigureAwait(false);
        if (version is int v) Apply(new AppEvent.BoardVersionRead(id, v));
    }

    private async Task<int?> TryReadVersionAsync(DeviceId id, CancellationToken ct)
    {
        var info = await Devices.Enumerate().WithId(id).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (info is null) return null;
        try
        {
            await using var usb = await UsbDevice.OpenAsync(info, ct).ConfigureAwait(false);
            return usb.Descriptor.DeviceVersion;
        }
        catch { return null; }
    }

    private void Apply(AppEvent e)
    {
        AppState next;
        lock (_stateLock)
        {
            _state = AppReducer.Reduce(_state, e);
            next = _state;
        }
        StateChanged?.Invoke(this, next);
    }

    private async Task RunExclusiveAsync(Func<Task> action, CancellationToken ct)
    {
        try { await _gate.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        try { await action().ConfigureAwait(false); }
        catch (OperationCanceledException) { /* shutting down */ }
        finally { _gate.Release(); }
    }

    private static BoardIdentity ToIdentity(DeviceInfo d) =>
        new(d.Id, d.SerialNumber, d.Name, Version: null, BoardConnection.Application);

    // ── Disposal ─────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();

        if (_watcher is not null)
        {
            _watcher.Appeared -= OnAppeared;
            _watcher.Disappeared -= OnDisappeared;
            try { await _watcher.DisposeAsync().ConfigureAwait(false); } catch { /* best-effort */ }
        }

        await CloseSessionAsync().ConfigureAwait(false);
        _gate.Dispose();
        _cts.Dispose();
    }

    private sealed class Session(DeviceId id, TreehopperBoard board, CancellationTokenSource cts)
    {
        public DeviceId Id { get; } = id;
        public TreehopperBoard Board { get; } = board;
        public CancellationTokenSource Cts { get; } = cts;
        public Task? Pump { get; set; }
    }

    private sealed class InlineProgress(Action<Efm8UploadProgress> report) : IProgress<Efm8UploadProgress>
    {
        public void Report(Efm8UploadProgress value) => report(value);
    }
}
