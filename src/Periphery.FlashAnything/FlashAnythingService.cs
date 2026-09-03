// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Periphery.Bootloader;
using Periphery.Firmware;

namespace Periphery.FlashAnything;

/// <summary>
/// The imperative shell: owns device discovery and the <see cref="BootloaderRegistry"/>,
/// dispatches <see cref="AppIntent"/>s, drives the bootloader contract, and emits
/// <see cref="AppEvent"/>s that the pure <see cref="AppReducer"/> folds into
/// <see cref="State"/> — the value both front-ends render.
/// </summary>
/// <remarks>
/// <para>This establishes the orchestration patterns the whole app follows:</para>
/// <list type="number">
///   <item><b>Discovery</b> (<see cref="RefreshAsync"/>): a watcher-driven push model - a
///   <see cref="MultiDeviceTracker"/> over a flashable-device filter coalesces OS events into a
///   per-device child tracker whose state stream drives detect/remove (autoflash spec Decision 1).</item>
///   <item><b>Flash one</b> (the per-target pattern): open → identify → flash
///   (progress → events) → leave, error-isolated, terminating in a
///   <see cref="AppEvent.FlashFinished"/>.</item>
///   <item><b>Fan-out</b> (<see cref="FlashAllAsync"/>): flash every matched target concurrently
///   (bounded by the configured flash concurrency), isolating per-target failures and aggregating
///   into a <see cref="FleetFlashSummary"/>.</item>
/// </list>
/// <para>Protocol cores behind the contract (Periphery.Bootloader.Stm32.Usb, .Efm8.Usb, ...) ride
/// the same contract, so with no providers registered the watcher's flashable-device filter matches
/// nothing. The watcher is injectable (built over fake device providers) so the whole flow is
/// unit-testable without hardware. Dispatch is expected to be serial (one front-end loop).</para>
/// </remarks>
public sealed class FlashAnythingService : IAsyncDisposable
{
    private readonly BootloaderRegistry _registry;
    private readonly BootloaderEntryRegistry _entries;        // app-mode devices an entry can reboot (ADR-0063 DEC-004)
    private readonly BootloaderEntryOptions _entryOptions;    // reboot-into-bootloader tunables (timeouts, correlation) for every app-mode flash
    private readonly FirmwareConverterRegistry _converters;   // bridges a loaded format to one a programmer accepts
    private readonly DeviceWatcher _watcher;
    private readonly MultiDeviceTracker _tracker;
    private readonly IDisposable _trackerSub;
    private readonly ILogger _logger;
    // Device ids are compared case-insensitively: Windows re-enumerates the same USB device with
    // different casing across a reset (e.g. app 8A7E \IMNUZ6YW -> bootloader-returned \imnuz6yw), and a
    // case-sensitive key treats the returned board as a NEW device — which double-flashes it under
    // autoflash (flashed once as \IMNUZ6YW, then again as \imnuz6yw before the dedupe finally matches).
    //
    // These collections take NO explicit comparer on purpose. The invariant lives in DeviceId's own
    // Equals/GetHashCode (StringComparison.OrdinalIgnoreCase, pinned by Periphery.Tests'
    // DeviceIdTests.Equals_DifferentCasing_ReturnsTrue / GetHashCode_DiffersOnlyByCasing_SameHash /
    // Dictionary_KeyedByDeviceId_IsCaseInsensitive), so it cannot be forgotten at one construction
    // site the way a per-collection StringComparer.OrdinalIgnoreCase argument can — which is exactly
    // how AutoflashPolicy.Decide's already-flashed set came to depend on caller discipline.
    private readonly Dictionary<DeviceId, DeviceInfo> _devices = new();
    private readonly HashSet<DeviceId> _flashingNow = new();    // targets mid-flash: not removed even if their device drops (an app-mode device disappears by design while it reboots)
    private readonly List<DeviceFilter> _claimedFilters = new(); // bootloaders an in-flight app-mode flash owns: the orchestration drives them, so they are suppressed from separate detection / autoflash
    private readonly object _gate = new();     // guards _devices, _flashedThisSession, _flashingNow, _claimedFilters, _payload, _rawContent, and the State swap
    private readonly object _emitGate = new();  // serializes Emit (fold + notify) for in-order delivery; lock order is always _emitGate -> _gate
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private bool _started;

    // Per-family one-at-a-time gates for no-serial families. Every EFM8 device in its bootloader
    // enumerates as the shared id 0x10C4:0xEAC9 — there is no serial to tell two apart. Flashing two
    // such boards concurrently through the app-mode reboot path corrupts deterministically (a garbage
    // 0x90 reply to a 0x33 write): established on hardware as a *physical* current-collision on the
    // shared USB bus by a stagger dose-response (single board fine; two overlapping fail; separating
    // them in time fixes it), NOT a software id/correlation defect. So a no-serial family must flash
    // strictly one board at a time even though _maxFlashConcurrency would run several — ADR-0063
    // DEC-005 ("one device at a time") and the autoflash spec's Safety rule 4. Serial-bearing families
    // (STM32 DFU / BySerial) keep flashing concurrently. Instance-scoped shell state (not static, not
    // an env var), created on demand and shared by every app-mode flash of that family; guarded by _gate.
    private readonly Dictionary<string, SemaphoreSlim> _familyGates = new(StringComparer.Ordinal);

    /// <summary>Default cap on how many boards flash at once. Bounded by USB power/bandwidth on a shared hub, not CPU.</summary>
    public const int DefaultMaxFlashConcurrency = 4;
    private readonly int _maxFlashConcurrency;

    /// <summary>
    /// The effective cap on how many boards flash simultaneously (autoflash and
    /// <see cref="FlashAllAsync"/>). A composition may pin this low as a deliberate posture — e.g. the
    /// Treehopper flasher exposes an opt-out that forces it to 1 (serialize), though it flashes
    /// concurrently by default.
    /// </summary>
    public int MaxFlashConcurrency => _maxFlashConcurrency;

    // Autoflash: a multi-reader queue drains to a bounded pool of flash workers (up to
    // _maxFlashConcurrency boards flash at once). _flashedThisSession dedupes within an armed session
    // (the armed config itself lives in State.Autoflash); a device is enqueued at most once (test-and-
    // marked under _gate before the write), so no two workers ever flash the same device.
    private readonly Channel<DeviceId> _autoflashQueue =
        Channel.CreateUnbounded<DeviceId>(new() { SingleReader = false, SingleWriter = false });
    private readonly HashSet<DeviceId> _flashedThisSession = new();
    private readonly CancellationTokenSource _cts = new();

    // One probe loop per bound bridge, alive only while armed on a probe family. Guarded by _gate.
    private readonly Dictionary<BridgeIdentity, ProbeSession> _probeLoops = new();

    // One gate per bound bridge: a serial port is an exclusive open, so a probe cycle and a flash
    // on the same fixture must not overlap (ADR-0062 section 1). Guarded by _gate.
    private readonly Dictionary<BridgeIdentity, SemaphoreSlim> _probeGates = new(); 

    // Bumped every time loops are started or stopped. A probe already inside OpenAsync when the
    // operator disarms will still finish and call back; cancellation cannot prevent that. The
    // generation is what lets a stale callback be recognised and dropped, rather than being
    // attributed to whatever session happens to be armed by the time it lands.
    private int _probeGeneration;

    /// <summary>A running probe loop and the token that stops it.</summary>
    private sealed record ProbeSession(Task Task, CancellationTokenSource Cts, int Generation)
    {
        /// <summary>The last device the bridge resolved to, so a removal or fault can still name it.</summary>
        public DeviceId? LastDevice { get; set; }
    }

    /// <summary>Interval between probes on a live row. Overridable for tests.</summary>
    internal TimeSpan ProbeCadence { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Interval once a row has stalled — a fixture that has been sitting empty.</summary>
    internal TimeSpan StalledProbeCadence { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How the probe loops wait. Injected so tests drive the cadence without sleeping.</summary>
    internal Func<TimeSpan, CancellationToken, Task> ProbeDelay { get; init; } = (d, ct) => Task.Delay(d, ct);
    private readonly Task[] _autoflashWorkers;

    private FirmwarePayload? _payload;  // the parsed firmware payload, loaded once and flashed many; guarded by _gate
    private byte[]? _rawContent;        // the loaded file's raw bytes, kept so a converter can re-derive a target's format; guarded by _gate

    /// <summary>
    /// Creates the service over a registry of flasher providers and a <see cref="DeviceWatcher"/>
    /// (defaults to <see cref="Devices.Watch"/>; tests inject one built over fake providers). The
    /// watcher is filtered to devices some provider can flash; the service owns its lifecycle.
    /// </summary>
    /// <param name="registry">The flasher providers to match discovered devices against.</param>
    /// <param name="watcher">Device discovery source; defaults to the real OS watcher (tests inject a fake).</param>
    /// <param name="maxFlashConcurrency">
    /// How many boards may flash simultaneously (autoflash and <see cref="FlashAllAsync"/>); the
    /// bound is USB power/bandwidth on a shared hub, not CPU. Defaults to
    /// <see cref="DefaultMaxFlashConcurrency"/>; must be at least 1.
    /// </param>
    /// <param name="logger">
    /// Optional sink for the discovery/flash trace (off by default). A front-end wires this to a
    /// file for the debug loop; it complements Periphery's own watcher/provider logs.
    /// </param>
    /// <param name="entries">Bootloader entries: how an app-mode device is rebooted into its bootloader (DEC-004).</param>
    /// <param name="converters">Firmware converters bridging a loaded format to one a programmer accepts.</param>
    /// <param name="entryOptions">
    /// Tunables for the app-mode reboot orchestration — most usefully
    /// <see cref="BootloaderEntryOptions.BootloaderTimeout"/>, how long to wait for a rebooted board's
    /// bootloader to re-enumerate. Defaults to <see cref="BootloaderEntryOptions"/>'s own defaults
    /// (15s); a slow or marginal board needs more, and a front-end exposes it as a flag.
    /// </param>
    public FlashAnythingService(
        BootloaderRegistry registry, DeviceWatcher? watcher = null,
        int maxFlashConcurrency = DefaultMaxFlashConcurrency, ILogger? logger = null,
        BootloaderEntryRegistry? entries = null, FirmwareConverterRegistry? converters = null,
        BootloaderEntryOptions? entryOptions = null)
    {
        if (maxFlashConcurrency < 1)
            throw new ArgumentOutOfRangeException(nameof(maxFlashConcurrency), "must flash at least one board at a time.");
        _registry = registry;
        _entries = entries ?? new BootloaderEntryRegistry();
        _entryOptions = entryOptions ?? new BootloaderEntryOptions();
        _converters = converters ?? new FirmwareConverterRegistry();
        _watcher = watcher ?? Devices.Watch();
        _maxFlashConcurrency = maxFlashConcurrency;
        _logger = logger ?? NullLogger.Instance;
        // Detection is push via a MultiDeviceTracker over the flashable filter (autoflash spec
        // Decision 1). The group folds appeared + activated + property-changed into a per-device
        // child tracker, so a device is recognized regardless of which raw OS event first carried
        // its full id - a DFU board's interface can arrive before its instance is readable - and it
        // survives the disconnect/reconnect of a reset. Its state stream drives detect/remove.
        // Match a device some provider can flash (already a bootloader) OR some entry can reboot into
        // one (running its application). The latter widens discovery to app-mode targets (DEC-004).
        _tracker = _watcher.AddMultiTracker(
            f => f.Where(d => _registry.Match(d) is not null || _entries.Match(d) is not null), "flashable");
        _trackerSub = _tracker.Subscribe(new TrackerObserver(this));
        // A bounded pool of identical workers drains the shared queue; the pool size is the cap on
        // how many boards flash at once. An idle worker simply blocks on the queue (cheap).
        _autoflashWorkers = new Task[maxFlashConcurrency];
        for (int i = 0; i < maxFlashConcurrency; i++)
            _autoflashWorkers[i] = Task.Run(() => RunAutoflashQueueAsync(_cts.Token));
    }

    /// <summary>The registered provider + entry family names (for a front-end autoflash family picker).</summary>
    public IReadOnlyList<string> KnownFamilies =>
        _registry.Providers.Select(p => p.Name)
            .Concat(_entries.Entries.Select(e => e.Name))
            .Distinct()
            .ToList();

    /// <summary>The latest folded state.</summary>
    public AppState State { get; private set; } = AppState.Empty;

    /// <summary>Raised after each event is folded — front-ends re-render on this.</summary>
    public event Action<AppState>? StateChanged;

    /// <summary>Folds an event into <see cref="State"/> and notifies. The only state mutator.</summary>
    private void Emit(AppEvent e)
    {
        // _emitGate serializes the whole fold-then-notify, so under concurrent flashing two threads
        // can't interleave and deliver an older snapshot after a newer one. The data lock (_gate) is
        // held only for the fold; subscriber handlers run outside it. Lock order is always
        // _emitGate -> _gate, and Emit is never called while _gate is held, so the pair can't deadlock.
        lock (_emitGate)
        {
            AppState next;
            lock (_gate) { State = AppReducer.Reduce(State, e); next = State; }
            StateChanged?.Invoke(next);
        }
    }

    /// <summary>Front-end entry point: execute a user intent.</summary>
    public async Task DispatchAsync(AppIntent intent, CancellationToken ct = default)
    {
        switch (intent)
        {
            case AppIntent.Refresh:
                await RefreshAsync(ct).ConfigureAwait(false);
                break;
            case AppIntent.SelectTarget s:
                Emit(new AppEvent.SelectionChanged(s.Id));
                break;
            case AppIntent.LoadFirmware lf:
                await LoadFirmwareAsync(lf.Path, lf.BinBaseAddress, ct).ConfigureAwait(false);
                break;
            case AppIntent.Flash f:
                await FlashOneAsync(f.Id, FlashOptions.Default, ct).ConfigureAwait(false);
                break;
            case AppIntent.FlashAll:
                await FlashAllAsync(ct: ct).ConfigureAwait(false);
                break;
            case AppIntent.ArmAutoflash arm:
                ArmAutoflash(arm);
                break;
            case AppIntent.DisarmAutoflash:
                DisarmAutoflash();
                break;
        }
    }

    // ── Discovery ──────────────────────────────────────────────────────────

    /// <summary>
    /// Starts watcher-driven discovery (idempotent). Starting the watcher fires
    /// <see cref="AppEvent.TargetDetected"/> for every present flashable device (the Start
    /// snapshot), then keeps the target list current from live hotplug; a manual call after the
    /// first start is a no-op (the watcher is already live). The OS-event handlers cache each
    /// <see cref="DeviceInfo"/> for opening and reconcile the target list.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (_started) return;
        await _startGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_started) return;
            _logger.LogInformation("Discovery: starting watcher (initial snapshot of present devices).");
            await _watcher.StartAsync(ct).ConfigureAwait(false);
            _started = true;
            _logger.LogInformation(
                "Discovery: watcher started; {Count} flashable target(s) in the initial snapshot.",
                State.Targets.Length);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // The GUI starts discovery fire-and-forget, so without this the snapshot failure is
            // invisible: present devices simply never appear, while hotplug still works (the monitor
            // is subscribed before the snapshot runs). Log, then rethrow — the contract is unchanged.
            _logger.LogError(ex,
                "Discovery: watcher start / initial snapshot FAILED. Present devices will be missed until a hotplug event.");
            throw;
        }
        finally { _startGate.Release(); }
    }

    // Driven by the MultiDeviceTracker's state stream (thread-pool threads). A present/active child
    // is a detected target; an absence triggers a reconcile against the group's children (an absent
    // state carries no DeviceInfo, so we diff the cached set against children no longer present).
    private void OnTrackerState(DeviceTrackerState state)
    {
        if (state.Device is { } device && state.ActivityStatus != DeviceActivityStatus.Absent)
        {
            // A device an in-flight app-mode flash has claimed (the bootloader it rebooted into) is
            // driven by that orchestration's own tracker subscription; don't surface it as a separate
            // target or autoflash it (ADR-0063 slice 4: no transient target, no double-flash).
            if (IsClaimed(device))
            {
                _logger.LogDebug("Discovery: {Id} is claimed by an in-flight app-mode flash; not surfacing it as a separate target.", device.Id);
                return;
            }

            // A flashable target is a device a provider can flash (already a bootloader) or an entry
            // can reboot into one (running its application). Bootloader mode wins if both match.
            string family;
            IdentificationMode identification;
            DeviceMode mode;
            BridgeIdentity? bridge = null;
            if (_registry.Match(device) is { } provider)
            {
                (family, identification, mode) = (provider.Name, provider.Identification, DeviceMode.Bootloader);

                // Only probe families need one. A passive target identifies itself, so binding it
                // to a bridge would add a constraint that buys nothing.
                if (provider.Identification != IdentificationMode.Passive)
                {
                    if (BridgeIdentity.TryFrom(device, out var identified, out string? why))
                        bridge = identified;
                    else
                        _logger.LogDebug(
                            "Discovery: {Id} is probe-identified but its bridge cannot be bound: {Why}", device.Id, why);
                }
            }
            else if (_entries.Match(device) is { } entry)
                // An app device is identified passively by its USB VID/PID, so it is autoflash-eligible.
                (family, identification, mode) = (entry.Name, IdentificationMode.Passive, DeviceMode.Application);
            else
            {
                _logger.LogDebug("Discovery: tracker state for {Id} (status={Status}) matched no provider or entry; ignoring.",
                    device.Id, state.ActivityStatus);
                return;
            }
            bool isNew;
            lock (_gate) { isNew = !_devices.ContainsKey(device.Id); _devices[device.Id] = device; }

            // Detection ownership (adr.md Decision 9). While a probe family is armed on this
            // bridge, the loop is the only thing that may say a target is present — it is the only
            // thing that has actually asked. Letting the watcher emit as well would produce two
            // detections for one physical target, and MaybeAutoflash fires on the first: a double
            // flash, a flash dispatched before the probe established there is an STM32 there at
            // all, or two opens of one port.
            if (bridge is { } bound && IsArmedOnBridge(bound))
            {
                _logger.LogDebug("Discovery: {Id} is on bound bridge {Bridge}; its probe loop owns detection.", device.Id, bound);
                return;
            }
            _logger.LogInformation("Discovery: detected {Kind} {Mode} target {Id} '{Name}' [{Family}] status={Status}.",
                isNew ? "new" : "existing", mode, device.Id, DisplayName(device), family, state.ActivityStatus);
            Emit(new AppEvent.TargetDetected(device.Id, DisplayName(device), family, identification, mode, bridge));
            if (isNew) MaybeAutoflash(device.Id); // autoflash on first detection; re-arrival after a reset re-evaluates (and dedupes)
        }
        else
        {
            ReconcileRemovals();
        }
    }

    private void ReconcileRemovals()
    {
        List<string> gone = new();
        lock (_gate)
        {
            foreach (var id in _devices.Keys)
            {
                // A target mid-flash is never removed: an application-mode device disappears by design
                // while it reboots into its bootloader, and its row must persist through the flash.
                if (_flashingNow.Contains(id)) continue;
                if (!_tracker.Trackers.TryGetValue(id, out var child) || !child.IsPresent)
                    gone.Add(id);
            }
            foreach (var id in gone) _devices.Remove(id);
        }
        foreach (var id in gone)
        {
            _logger.LogInformation("Discovery: target {Id} removed (no longer present).", id);
            Emit(new AppEvent.TargetRemoved(id));
        }
    }

    // ── Firmware ───────────────────────────────────────────────────────────

    /// <summary>
    /// Loads (parses, once) the firmware image at <paramref name="path"/> so subsequent
    /// flashes reuse it. The format is detected from the extension and verified against the
    /// content (<see cref="FirmwareImage.Load"/>); <paramref name="binBaseAddress"/> places a
    /// raw <c>.bin</c> and is ignored for Intel HEX. A load failure clears any prior image and
    /// surfaces the real reason via <see cref="AppEvent.FirmwareLoadFailed"/> (visible to both
    /// front-ends), instead of a misleading "no firmware loaded" later at flash time.
    /// </summary>
    public async Task LoadFirmwareAsync(string path, uint binBaseAddress = 0x08000000, CancellationToken ct = default)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            var payload = FirmwarePayload.Load(bytes, path, binBaseAddress);
            lock (_gate) { _payload = payload; _rawContent = bytes; }
            Emit(new AppEvent.FirmwareLoaded(new FirmwareSelection(path, Path.GetFileName(path), payload.ByteLength)));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            lock (_gate) { _payload = null; _rawContent = null; }
            Emit(new AppEvent.FirmwareLoadFailed($"Failed to load '{Path.GetFileName(path)}': {ex.Message}"));
        }
    }

    // ── Flashing ───────────────────────────────────────────────────────────

    /// <summary>
    /// Flashes the loaded firmware to every detected target, isolating per-target failures.
    /// Returns a summary; no-ops (returns all-skipped) when no firmware is loaded.
    /// </summary>
    public async Task<FleetFlashSummary> FlashAllAsync(FlashOptions? options = null, CancellationToken ct = default)
    {
        if (CurrentPayload() is null)
            return new FleetFlashSummary(0, 0, State.Targets.Length);

        var opts = options ?? FlashOptions.Default;
        var ids = State.Targets.Select(t => t.Id).ToList(); // snapshot ids; Emit mutates State
        int ok = 0, failed = 0, skipped = 0;
        // Flash up to _maxFlashConcurrency boards at once. Per-target failures are isolated inside
        // FlashOneAsync (only cancellation throws out, aborting the fleet); the counters are folded
        // atomically since the body runs on several threads.
        await Parallel.ForEachAsync(
            ids,
            new ParallelOptions { MaxDegreeOfParallelism = _maxFlashConcurrency, CancellationToken = ct },
            async (id, token) =>
            {
                switch (await FlashOneAsync(id, opts, token).ConfigureAwait(false))
                {
                    case FlashOutcome.Succeeded: Interlocked.Increment(ref ok); break;
                    case FlashOutcome.Failed: Interlocked.Increment(ref failed); break;
                    default: Interlocked.Increment(ref skipped); break;
                }
            }).ConfigureAwait(false);
        return new FleetFlashSummary(ok, failed, skipped);
    }

    /// <summary>Flashes the loaded firmware to one target with the given options; true on success.</summary>
    public async Task<bool> FlashAsync(DeviceId id, FlashOptions? options = null, CancellationToken ct = default)
        => await FlashOneAsync(id, options ?? FlashOptions.Default, ct).ConfigureAwait(false) == FlashOutcome.Succeeded;

    private async Task<FlashOutcome> FlashOneAsync(DeviceId id, FlashOptions options, CancellationToken ct)
    {
        // A bound probe fixture is probed on a cadence while it is armed, and a serial port is an
        // exclusive open. Hold the bridge gate for the whole flash so the next probe cycle waits
        // rather than colliding with it (adr.md Decision 11).
        if (GateFor(id) is { } bridgeGate)
        {
            await bridgeGate.WaitAsync(ct).ConfigureAwait(false);
            try { return await FlashOneCoreAsync(id, options, ct).ConfigureAwait(false); }
            finally { bridgeGate.Release(); }
        }

        return await FlashOneCoreAsync(id, options, ct).ConfigureAwait(false);
    }

    private async Task<FlashOutcome> FlashOneCoreAsync(DeviceId id, FlashOptions options, CancellationToken ct)
    {
        if (State.Find(id) is not { } target) return FlashOutcome.Skipped;

        if (CurrentPayload() is null)
        {
            Emit(new AppEvent.OperationFailed(id, "No firmware loaded."));
            return FlashOutcome.Skipped;
        }
        if (GetDevice(id) is not { } device)
        {
            Emit(new AppEvent.OperationFailed(id, "Target is no longer connected."));
            return FlashOutcome.Skipped;
        }

        // Hold the row through the flash: an application-mode device disappears (it reboots) and a
        // bootloader-mode device may briefly drop, but the target must not be reconciled away mid-flash.
        lock (_gate) _flashingNow.Add(id);
        try
        {
            return target.Mode == DeviceMode.Application
                ? await FlashApplicationAsync(id, device, options, ct).ConfigureAwait(false)
                : await FlashBootloaderAsync(id, device, options, ct).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate) _flashingNow.Remove(id);
        }
    }

    // Flash a device already in its bootloader: open its programmer and run the shared flash body.
    // No automatic post-flash verification here (entry.CanVerify's re-entry needs an IBootloaderEntry
    // to get back INTO the bootloader after a leave, which a device already sitting in one — with no
    // app-mode identity or entry resolved for it at all — has no equivalent of). Scoped to the
    // app-mode path below, which is what periphery#246 actually surfaced.
    private async Task<FlashOutcome> FlashBootloaderAsync(DeviceId id, DeviceInfo device, FlashOptions options, CancellationToken ct)
    {
        if (_registry.Match(device) is not { } provider)
        {
            Emit(new AppEvent.OperationFailed(id, "No bootloader provider for this device."));
            return FlashOutcome.Skipped;
        }
        try
        {
            await using var programmer = await provider.OpenAsync(device, ct).ConfigureAwait(false);
            var (result, _) = await RunProgrammerAsync(id, programmer, options, ct).ConfigureAwait(false);
            return result.Success ? FlashOutcome.Succeeded : FlashOutcome.Failed;
        }
        catch (OperationCanceledException) { throw; } // cancellation is not a per-target failure; abort the fleet
        catch (Exception ex)
        {
            Emit(new AppEvent.FlashFinished(id, FlashResult.Fail(ex)));
            return FlashOutcome.Failed;
        }
    }

    // Flash an application-mode device: run the shared orchestration (reboot -> wait -> safety gate ->
    // flash). The phase callback surfaces the Entering / WaitingForBootloader stages on this row; the
    // flash callback opens the re-enumerated bootloader's programmer and runs the same flash body,
    // emitting the flash events on this id. No app-return wait — discovery re-detects the returned app.
    private async Task<FlashOutcome> FlashApplicationAsync(DeviceId id, DeviceInfo device, FlashOptions options, CancellationToken ct)
    {
        if (_entries.Match(device) is not { } entry)
        {
            Emit(new AppEvent.OperationFailed(id, "No bootloader entry for this application device."));
            return FlashOutcome.Skipped;
        }

        // Serialize no-serial families: only one board of a shared-id family may be in its reboot →
        // correlate → flash window at a time (see _familyGates). Gating the whole window — not just the
        // write — is what prevents two boards being live on the shared bus together. A serial-bearing
        // family gets no gate (null) and flashes concurrently.
        var serializationGate = SerializationGateFor(entry.Name);
        if (serializationGate is not null)
            await serializationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await RebootAndFlashApplicationAsync(id, device, entry, options, ct).ConfigureAwait(false);
        }
        finally
        {
            serializationGate?.Release();
        }
    }

    // The reboot → wait-for-bootloader → safety-gate → flash body for one app-mode device, run under
    // the family serialization gate (when the family is no-serial) by FlashApplicationAsync.
    private async Task<FlashOutcome> RebootAndFlashApplicationAsync(
        DeviceId id, DeviceInfo device, IBootloaderEntry entry, FlashOptions options, CancellationToken ct)
    {
        var phase = new SyncProgress<BootloaderEntryPhase>(p => Emit(p == BootloaderEntryPhase.Entering
            ? new AppEvent.EnteringBootloader(id)
            : new AppEvent.WaitingForBootloader(id)));
        // Claim the bootloader this entry reboots into for the whole flash, so its transient
        // appearance is neither surfaced as a separate target nor autoflashed.
        var bootloaderFilter = entry.ExpectedBootloader;
        lock (_gate) _claimedFilters.Add(bootloaderFilter);
        try
        {
            // Captured across flash attempts by the closure below: RunWithVerificationAsync's verify
            // callback needs the SAME, possibly-converted payload the device actually just received,
            // not the originally loaded one - and a retry re-flashes, so this must reflect whichever
            // attempt most recently ran, not just the first.
            FirmwarePayload? lastPayload = null;
            Func<DeviceInfo, CancellationToken, Task<FlashResult>> flash = async (bootDevice, token) =>
            {
                if (_registry.Match(bootDevice) is not { } provider)
                    return FlashResult.Fail($"No bootloader provider for the re-enumerated device {bootDevice.Id}.");
                await using var programmer = await provider.OpenAsync(bootDevice, token).ConfigureAwait(false);
                var (result, payload) = await RunProgrammerAsync(id, programmer, options, token).ConfigureAwait(false);
                lastPayload = payload;
                return result;
            };

            FlashResult finalResult;
            if (entry.CanVerify)
            {
                // periphery#246: this family's flasher has no in-session read-back proof, so
                // independently confirm the write in a genuinely separate, later bootloader session
                // and retry automatically on a mismatch - the durable fix, not an operator's manual
                // "run verify after flash" discipline.
                var verified = await BootloaderEntryOrchestrator.RunWithVerificationAsync<FlashResult>(
                    entry,
                    device,
                    flash,
                    verify: (bootDevice, token) => entry.VerifyAsync(bootDevice, lastPayload!, token),
                    flashSucceeded: static r => r.Success,
                    options: _entryOptions,
                    phase: phase,
                    waitSource: f => new TrackerDeviceWaitSource(_tracker, f),
                    ct: ct).ConfigureAwait(false);

                // verified.Verified, not just verified.FlashResult.Success, decides the outcome - a
                // persistent mismatch across every retry must be reported as a failure, not silently
                // accepted because the underlying write's own (unverified) ack happened to say OK.
                finalResult = verified.Verified
                    ? verified.FlashResult with { Verified = true }
                    : new FlashResult(
                        Success: false,
                        BytesWritten: verified.FlashResult.BytesWritten,
                        Verified: false,
                        Error: verified.FlashResult.Success
                            ? "The write appeared to succeed, but an independent, later "
                              + "bootloader-session check could not confirm it landed (or the board "
                              + "never confirmed returning to its application) after every retry attempt."
                            : verified.FlashResult.Error);

                // RunProgrammerAsync already emitted FlashFinished for the last individual write
                // attempt - that result's own Verified is always false (Efm8HidProgrammer.FlashAsync
                // has no in-session read-back) and, on a persistent mismatch, can even show a stale
                // success message from a write that later turned out unconfirmed. Re-emit the TRUE,
                // independently-confirmed final answer so the target's displayed result reflects it.
                Emit(new AppEvent.FlashFinished(id, finalResult));
            }
            else
            {
                var outcome = await BootloaderEntryOrchestrator.RunAsync<FlashResult>(
                    entry,
                    device,
                    flash,
                    options: _entryOptions,
                    flashSucceeded: static r => r.Success,
                    phase: phase,
                    // Ride the service's MultiDeviceTracker instead of starting a second watcher; the
                    // tracker already sees the re-enumerated bootloader (ADR-0063 slice 4).
                    waitSource: f => new TrackerDeviceWaitSource(_tracker, f),
                    ct: ct).ConfigureAwait(false);
                finalResult = outcome.FlashResult;
            }
            return finalResult.Success ? FlashOutcome.Succeeded : FlashOutcome.Failed;
        }
        catch (OperationCanceledException) { throw; } // cancellation is not a per-target failure; abort the fleet
        catch (Exception ex)
        {
            // A BootloaderEntryException (the bootloader never appeared / the gate refused) or any
            // reboot-stage fault lands here, before the flash body emitted anything — surface it as a
            // finished failure on the row, with the whole cause chain (the wrapper alone reads
            // "Treehopper reconcile failed." and hides the USB error underneath).
            Emit(new AppEvent.FlashFinished(id, FlashResult.Fail(ex)));
            return FlashOutcome.Failed;
        }
        finally
        {
            lock (_gate) _claimedFilters.Remove(bootloaderFilter);
        }
    }

    // The per-programmer flash body, shared by both modes: resolve the payload to a format the
    // programmer accepts (converting if needed), identify, flash, and emit the flash events on `id`.
    // Returns the payload actually flashed alongside the result — a caller doing automatic
    // post-flash verification (RebootAndFlashApplicationAsync, when entry.CanVerify) needs the
    // SAME, possibly-converted bytes the device actually received, not the originally loaded one.
    private async Task<(FlashResult Result, FirmwarePayload? Payload)> RunProgrammerAsync(
        DeviceId id, IFirmwareProgrammer programmer, FlashOptions options, CancellationToken ct)
    {
        var (payload, error) = PayloadFor(programmer);
        if (payload is null)
        {
            var failed = FlashResult.Fail(error ?? "No firmware loaded.");
            Emit(new AppEvent.FlashFinished(id, failed));
            return (failed, null);
        }

        var identity = await programmer.IdentifyAsync(ct).ConfigureAwait(false);
        _logger.LogInformation(
            "Flash {Id}: identified {Family} (bootloader {Boot}); transfer size {TransferSize} bytes.",
            id, identity.Family, identity.BootloaderVersion ?? "?", identity.TransferSize);
        Emit(new AppEvent.TargetIdentified(id, identity));

        Emit(new AppEvent.FlashStarted(id));
        // Synchronous sink: System.Progress<T> marshals callbacks asynchronously, which can deliver a
        // tick *after* FlashFinished and rewind the stage. Reporting inline keeps the emitted event
        // order deterministic (progress strictly before finish).
        var progress = new SyncProgress<FlashProgress>(p => Emit(new AppEvent.FlashProgressed(id, p)));
        var result = await programmer.FlashAsync(payload, options, progress, ct).ConfigureAwait(false);

        // FlashAsync leaves the bootloader itself when options.LeaveAfterFlash (its final plan step),
        // so we must NOT leave again here — a second leave hits the already-reset device.
        Emit(new AppEvent.FlashFinished(id, result));
        return (result, payload);
    }

    // Resolve the loaded firmware to a payload `programmer` accepts: the loaded payload directly when
    // its format is accepted, else converted via a registered IFirmwareConverter. (null, reason) if
    // neither is possible (e.g. an EFM8 target but only an unconvertible format loaded).
    private (FirmwarePayload? Payload, string? Error) PayloadFor(IFirmwareProgrammer programmer)
    {
        FirmwarePayload? payload;
        byte[]? raw;
        lock (_gate) { payload = _payload; raw = _rawContent; }
        if (payload is null) return (null, "No firmware loaded.");
        if (programmer.AcceptedFormats.Contains(payload.Format))
            return (payload, null);

        if (raw is null || _converters.Find(payload.Format, programmer.AcceptedFormats) is not { } converter)
            return (null,
                $"The loaded {payload.Format} firmware can't be flashed to this target " +
                $"(it accepts {string.Join(", ", programmer.AcceptedFormats)}), and no converter is registered.");
        try
        {
            return (converter.Convert(raw), null);
        }
        catch (Exception ex)
        {
            return (null, $"Converting {payload.Format} to {converter.Target} failed: {ex.Message}");
        }
    }

    // ── Autoflash ────────────────────────────────────────────────────────────

    /// <summary>
    /// Arms autoflash: matching, passively-identified targets are flashed automatically as they
    /// appear (and any already present at arm time), up to <c>_maxFlashConcurrency</c> at once, each
    /// at most once this armed session. Requires a loaded image. The per-device decision is the pure
    /// <see cref="AutoflashPolicy"/>.
    /// </summary>
    private void ArmAutoflash(AppIntent.ArmAutoflash arm)
    {
        // Every refusal below disarms as well as reporting, which AutoflashArmFailed does. A failed
        // re-arm that left the previous session running would be the worst of both: the operator is
        // told the arm failed while the old family, options and bindings keep flashing whatever
        // appears. It also leaves the loaded image alone — a port that is absent or unidentifiable
        // says nothing about the firmware.
        if (CurrentPayload() is null)
        {
            StopProbeLoops();
            Emit(new AppEvent.AutoflashArmFailed("Load a firmware image before arming autoflash."));
            return;
        }
        if (!TryBindBridges(arm, out var bridges, out string? bindError))
        {
            StopProbeLoops();
            Emit(new AppEvent.AutoflashArmFailed(bindError));
            return;
        }

        lock (_gate) _flashedThisSession.Clear();
        Emit(new AppEvent.AutoflashArmed(
            new AutoflashConfig(arm.Family, arm.Options) { Bridges = bridges, Repeat = arm.Repeat }));

        StartProbeLoops(bridges);

        // Evaluate targets already present at arm time (arming a bench with boards already
        // connected) — but only passively-identified ones. A probe row exists as soon as the
        // watcher sees the bridge, before anything has asked what is behind it, so flashing it here
        // would act on a fixture that might be empty. Only a positive probe makes a probe target
        // eligible, and that arrives through OnProbeAction.
        foreach (var id in State.Targets
                     .Where(t => t.Identification == IdentificationMode.Passive)
                     .Select(t => t.Id).ToList())
        {
            MaybeAutoflash(id);
        }
    }

    /// <summary>
    /// Resolves the ports named at arm time to the identities of the bridges currently behind them
    /// (adr.md Decision 8). A COM name is not an identity — Windows recycles them, so a bind
    /// against the string would follow the number to whatever device inherits it next.
    /// </summary>
    /// <remarks>
    /// Every named port must resolve, and every resolved bridge must be identifiable. A partial
    /// bind is refused rather than silently arming for a subset: the operator named a bench, and
    /// arming for part of it while reporting success is how a fixture gets left unattended and
    /// unflashed.
    /// </remarks>
    private bool TryBindBridges(
        AppIntent.ArmAutoflash arm, out ImmutableHashSet<BridgeIdentity> bridges, out string? error)
    {
        bridges = ImmutableHashSet<BridgeIdentity>.Empty;
        error = null;

        if (arm.Ports.IsDefaultOrEmpty)
        {
            // A probe family with nothing bound is not dangerous — the policy's scope check fails
            // closed, so such a session skips every target it ever sees. It is useless, which is its
            // own hazard: an operator who armed a fixture and walked away would come back to a bench
            // that never flashed anything and never said why. Refuse at arm time instead.
            if (IsProbeFamily(arm.Family))
            {
                error = $"cannot arm '{arm.Family}' without naming a port: it is probe-identified, so " +
                        "autoflash has no way to know which fixture was meant, and an unbound arm " +
                        "would skip every target it saw.";
                return false;
            }

            return true;
        }

        var builder = ImmutableHashSet.CreateBuilder<BridgeIdentity>();
        foreach (var port in arm.Ports)
        {
            DeviceInfo? device;
            lock (_gate) device = _devices.Values.FirstOrDefault(d => d.PortName == port);

            if (device is null)
            {
                error = $"cannot arm on {port.Value}: no device is present on that port.";
                return false;
            }

            if (!BridgeIdentity.TryFrom(device, out var identity, out string? why))
            {
                error = $"cannot arm on {port.Value}: {why}";
                return false;
            }

            builder.Add(identity);
            _logger.LogInformation("Autoflash: bound {Port} to bridge {Bridge}.", port.Value, identity);
        }

        bridges = builder.ToImmutable();
        return true;
    }

    /// <summary>Whether the named family identifies its targets by probing rather than by VID/PID.</summary>
    private bool IsProbeFamily(string family) =>
        _registry.Providers.FirstOrDefault(p => string.Equals(p.Name, family, StringComparison.Ordinal))
            is { Identification: not IdentificationMode.Passive };

    private void DisarmAutoflash()
    {
        StopProbeLoops();
        lock (_gate) _flashedThisSession.Clear();
        Emit(new AppEvent.AutoflashDisarmed());
    }

    /// <summary>Whether a probe loop is currently running for this bridge.</summary>
    private bool IsArmedOnBridge(BridgeIdentity bridge)
    {
        lock (_gate) return _probeLoops.ContainsKey(bridge);
    }

    /// <summary>
    /// Starts one loop per bound bridge. Cancellation is the operator disarming, and it is the
    /// stop — a fixture sitting empty is the normal resting state, so a loop slows down rather
    /// than giving up.
    /// </summary>
    private void StartProbeLoops(ImmutableHashSet<BridgeIdentity> bridges)
    {
        StopProbeLoops();

        string? family = State.Autoflash?.Family;

        // Probe families only. A passive family armed with ports would otherwise get a loop poking
        // its targets while detection ownership suppressed the watcher events those targets are
        // actually identified by — the family would stop working in exchange for nothing.
        if (bridges.IsEmpty || family is null || !IsProbeFamily(family)
            || _registry.Providers.FirstOrDefault(
                p => string.Equals(p.Name, family, StringComparison.Ordinal)) is not { } provider)
        {
            return;
        }

        int generation;
        lock (_gate) generation = ++_probeGeneration;

        foreach (var bridge in bridges)
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            var gate = new SemaphoreSlim(1, 1);
            var loop = new SerialProbeLoop(
                bridge, ResolveBoundBridge, provider,
                action => OnProbeAction(bridge, generation, action),
                ProbeDelay, ProbeCadence, StalledProbeCadence)
            {
                Gate = gate,
            };

            var task = Task.Run(() => loop.RunAsync(cts.Token), CancellationToken.None);
            lock (_gate)
            {
                _probeLoops[bridge] = new ProbeSession(task, cts, generation);
                _probeGates[bridge] = gate;
            }
            _logger.LogInformation("Autoflash: probing {Bridge} every {Cadence}.", bridge, ProbeCadence);
        }
    }

    /// <summary>Cancels every probe loop and waits for it to stop before returning.</summary>
    /// <remarks>
    /// Waiting is the point. Cancellation does not reach a probe already inside the provider open
    /// call, so without this a loop can finish its cycle and call back after a disarm — or after a
    /// re-arm has installed replacements, where the callback would be attributed to a session it
    /// knows nothing about. The generation check in <see cref="OnProbeAction"/> catches the
    /// callback that still slips through; this bounds how long that window is.
    /// </remarks>
    private void StopProbeLoops()
    {
        ProbeSession[] running;
        SemaphoreSlim[] gates;
        lock (_gate)
        {
            running = _probeLoops.Values.ToArray();
            gates = _probeGates.Values.ToArray();
            _probeLoops.Clear();
            _probeGates.Clear();
            _probeGeneration++;   // anything already in flight is now stale
        }

        foreach (var session in running)
            session.Cts.Cancel();

        try
        {
            Task.WhenAll(running.Select(r => r.Task)).Wait(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // A loop that faulted on the way out has already reported through OnProbeAction.
        }

        foreach (var session in running)
            session.Cts.Dispose();
        foreach (var gate in gates)
            gate.Dispose();
    }

    /// <summary>
    /// The bridge gate a flash on this device must hold, or null when it is not a bound probe
    /// target. A serial port is an exclusive open (ADR-0062 section 1), so a probe cycle and a
    /// flash on the same fixture must not overlap: otherwise either open can lose, faulting the row
    /// or failing the flash partway through a write. adr.md Decision 11 calls for one state machine
    /// per bridge; this is that exclusion.
    /// </summary>
    private SemaphoreSlim? GateFor(DeviceId id)
    {
        lock (_gate)
        {
            return State.Find(id)?.Bridge is { } bound && _probeGates.TryGetValue(bound, out var gate)
                ? gate
                : null;
        }
    }

    /// <summary>Finds the device currently behind a bound bridge, or null if it is no longer present.</summary>
    private DeviceInfo? ResolveBoundBridge(BridgeIdentity bridge)
    {
        lock (_gate)
        {
            foreach (var device in _devices.Values)
            {
                if (device.PortName is not null
                    && BridgeIdentity.TryFrom(device, out var identity, out _)
                    && identity == bridge)
                {
                    return device;
                }
            }
        }
        return null;
    }

    /// <summary>Turns one probe row action into the app's existing target lifecycle.</summary>
    private void OnProbeAction(BridgeIdentity bridge, int generation, ProbeRowAction action)
    {
        var device = ResolveBoundBridge(bridge);

        // Drop callbacks from a loop that is no longer the current one. A probe inside the
        // provider open call when the operator disarms still finishes and still calls back, and
        // after a re-arm that callback would otherwise be attributed to the new session family —
        // and could enqueue a flash off a probe belonging to the old one.
        lock (_gate)
        {
            if (!_probeLoops.TryGetValue(bridge, out var session) || session.Generation != generation)
                return;

            if (device is not null)
                session.LastDevice = device.Id;
        }

        switch (action)
        {
            case ProbeRowAction.Detected detected when device is not null:
                Emit(new AppEvent.TargetDetected(
                    device.Id, DisplayName(device), State.Autoflash?.Family ?? "serial",
                    IdentificationMode.Probe, DeviceMode.Bootloader, bridge));
                Emit(new AppEvent.TargetIdentified(device.Id, detected.Identity));
                MaybeAutoflash(device.Id);
                break;

            // Removal and faults still have to be reported when the bridge itself has gone — that
            // is exactly when they matter most. The last device the loop saw is what names them,
            // because there is nothing left to resolve.
            case ProbeRowAction.Removed:
                if (LastDeviceFor(bridge) is { } removed)
                {
                    ReopenGateIfRepeating(removed);
                    Emit(new AppEvent.TargetRemoved(removed));
                }
                break;

            case ProbeRowAction.Faulted faulted:
                _logger.LogWarning("Autoflash: probe loop for {Bridge} stopped: {Message}", bridge, faulted.Message);
                if (LastDeviceFor(bridge) is { } faultedId)
                    Emit(new AppEvent.OperationFailed(faultedId, faulted.Message));
                lock (_gate)
                {
                    _probeLoops.Remove(bridge);
                    if (_probeGates.Remove(bridge, out var gate)) gate.Dispose();
                }
                break;
        }
    }

    /// <summary>
    /// Lets a fixture flash the next board, when the operator armed it to (adr.md Decision 10).
    /// </summary>
    /// <remarks>
    /// The default is one flash per bound bridge per armed session, which is Decision 5 unchanged:
    /// a fixture produces the same <see cref="DeviceId"/> for every board, so the existing
    /// already-flashed set is what stops a second one. Reopening it is the whole of
    /// <c>--repeat</c>, and it is opt-in because the evidence a board left is weaker than the
    /// evidence one arrived — silence cannot tell a departure from a part that reset while seated.
    /// </remarks>
    private void ReopenGateIfRepeating(DeviceId id)
    {
        lock (_gate)
        {
            if (State.Autoflash is not { Repeat: RepeatMode.Silence })
                return;

            if (_flashedThisSession.Remove(id))
                _logger.LogInformation("Autoflash: {Id} left the fixture; it may flash again (--repeat).", id);
        }
    }

    /// <summary>The device this bridge resolves to, or the last one it did before it vanished.</summary>
    private DeviceId? LastDeviceFor(BridgeIdentity bridge)
    {
        if (ResolveBoundBridge(bridge) is { } device)
            return device.Id;

        lock (_gate) return _probeLoops.TryGetValue(bridge, out var session) ? session.LastDevice : null;
    }

    /// <summary>
    /// Runs the pure policy for one target and acts: enqueue it for flashing (the worker pool drains
    /// the queue, up to <c>_maxFlashConcurrency</c> at once), or record a surfaced skip. Called on
    /// every detection (no-op unless armed) and for present targets on arm. The decide-and-mark is
    /// atomic under <c>_gate</c>, so a racing re-detection of the same id is deduped (never double-enqueued).
    /// </summary>
    private void MaybeAutoflash(DeviceId id)
    {
        bool enqueue = false;
        string? skipReason = null;
        lock (_gate)
        {
            if (State.Autoflash is not { } armed || State.Find(id) is not { } target)
                return;
            switch (AutoflashPolicy.Decide(armed, target, _flashedThisSession))
            {
                case AutoflashAction.Flash:
                    _flashedThisSession.Add(id); // mark now so a rapid re-appearance dedupes
                    enqueue = true;
                    break;
                case AutoflashAction.Skip skip:
                    skipReason = skip.Reason;
                    break;
            }
        }
        if (enqueue) _autoflashQueue.Writer.TryWrite(id);
        else if (skipReason is not null) Emit(new AppEvent.AutoflashOutcome(id, AutoflashOutcomeKind.Skipped, skipReason));
    }

    // An autoflash worker: one of _maxFlashConcurrency identical workers draining the shared queue.
    // Each pulls a queued device, flashes it, and folds the outcome into the session tally. Several
    // run at once, so up to _maxFlashConcurrency boards flash concurrently; dedupe-before-enqueue
    // guarantees a device is never handed to two workers. Runs for the service's lifetime.
    private async Task RunAutoflashQueueAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var id in _autoflashQueue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                AutoflashConfig? armed;
                lock (_gate) armed = State.Autoflash;
                if (armed is null)
                {
                    Emit(new AppEvent.AutoflashOutcome(id, AutoflashOutcomeKind.Skipped, "disarmed before flash"));
                    continue;
                }

                var outcome = await FlashOneAsync(id, armed.Options, ct).ConfigureAwait(false);
                var (kind, detail) = outcome switch
                {
                    FlashOutcome.Succeeded => (AutoflashOutcomeKind.Flashed, (string?)null),
                    FlashOutcome.Failed => (AutoflashOutcomeKind.Failed, State.Find(id)?.LastError),
                    _ => (AutoflashOutcomeKind.Skipped, State.Find(id)?.LastError ?? "skipped"),
                };
                Emit(new AppEvent.AutoflashOutcome(id, kind, detail));
            }
        }
        catch (OperationCanceledException) { /* service disposing */ }
    }

    // The one-at-a-time gate for the given family, or null when app-mode flashes may run concurrently.
    // The decision turns on whether the correlation mode has a per-board *distinguisher* that survives
    // the mode switch (reads the *service-wide* _entryOptions.Correlation):
    //   • FirstAppearance — NO distinguisher: the re-enumerated bootloader is the shared no-serial id,
    //     so two concurrent waits would both correlate to the first-appearing bootloader (collapsing
    //     onto one board). Serialize — the safe fallback for any family exposing neither serial nor port.
    //   • BySerial / ByLocationPath — HAS a distinguisher (the surviving serial, resp. the stable USB
    //     port): each concurrent wait correlates to its OWN board exactly, so flashing is parallel-safe.
    //     No gate. ByLocationPath is what unlocks concurrency for no-serial families such as EFM8/
    //     Treehopper, superseding #220's blanket serialization for them.
    // (Today one correlation mode covers all entries; if BootloaderEntryOptions ever becomes per-entry,
    // thread that per-entry mode through here so the decision is genuinely per-family.) The gate is
    // keyed per family name and memoized, so each family's app-mode flashes share one semaphore and
    // distinct families never block each other.
    private SemaphoreSlim? SerializationGateFor(string family)
    {
        if (_entryOptions.Correlation != DeviceCorrelationMode.FirstAppearance)
            return null; // has a distinguisher (BySerial / ByLocationPath): concurrent app-mode flashing is safe.
        lock (_gate)
        {
            if (!_familyGates.TryGetValue(family, out var gate))
                _familyGates[family] = gate = new SemaphoreSlim(1, 1);
            return gate;
        }
    }

    private DeviceInfo? GetDevice(DeviceId id)
    {
        lock (_gate) return _devices.TryGetValue(id, out var device) ? device : null;
    }

    // True if some in-flight app-mode flash has claimed this device (it is the bootloader that flash
    // rebooted into, driven by the orchestration rather than surfaced as its own target).
    private bool IsClaimed(DeviceInfo device)
    {
        lock (_gate)
        {
            foreach (var filter in _claimedFilters)
                if (filter.Matches(device)) return true;
            return false;
        }
    }

    // The loaded payload, read under _gate: it's written by LoadFirmwareAsync on a dispatch thread and
    // read by flash workers, so a plain field read could miss the latest write under parallelism.
    private FirmwarePayload? CurrentPayload()
    {
        lock (_gate) return _payload;
    }

    private static string DisplayName(DeviceInfo d) => d.Name ?? d.Id;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _trackerSub.Dispose();

        // Before _cts.Cancel: the loops hold linked sources, and cancelling them here means their
        // own dispose does not race the one below.
        StopProbeLoops();

        _cts.Cancel();
        _autoflashQueue.Writer.TryComplete();
        try { await Task.WhenAll(_autoflashWorkers).ConfigureAwait(false); } catch { /* shutdown */ }
        await _watcher.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
        _startGate.Dispose();
        lock (_gate)
        {
            foreach (var gate in _familyGates.Values) gate.Dispose();
            _familyGates.Clear();
        }
    }

    /// <summary>Bridges the MultiDeviceTracker's IObservable to <see cref="OnTrackerState"/>.</summary>
    private sealed class TrackerObserver(FlashAnythingService service) : IObserver<DeviceTrackerState>
    {
        public void OnNext(DeviceTrackerState state) => service.OnTrackerState(state);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    /// <summary>Reports progress inline (no async marshaling), preserving emitted event order.</summary>
    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    private enum FlashOutcome { Succeeded, Failed, Skipped }
}
