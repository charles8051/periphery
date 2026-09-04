// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Periphery.Treehopper.Wire;
using Periphery.Usb;

namespace Periphery.Treehopper;

/// <summary>
/// A Treehopper USB I/O board. The shell in the functional-core / imperative-shell
/// split (ADR-0052): it owns the USB transport, the report-producer, and the
/// reconcile loop; all codec and planning logic is in the pure
/// <see cref="TreehopperWire"/> layer.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item>
///     <term>Config changes</term>
///     <description>
///       Go through <see cref="ReconcileWithAsync"/> — the caller supplies a pure
///       transform of the current desired config; the shell diffs it against the
///       last applied config, encodes the delta commands, and ships them.
///     </description>
///   </item>
///   <item>
///     <term>Transactions</term>
///     <description>
///       I²C / SPI / UART operations go through <see cref="ExecuteTransactionAsync"/>
///       — one write + optional read, serialised under the board lock.
///     </description>
///   </item>
///   <item>
///     <term>Pin-state stream</term>
///     <description>
///       A background producer reads the board's IN endpoint and publishes
///       immutable <see cref="BoardReport"/> snapshots to a bounded channel.
///       Consumers read via <see cref="Reports"/>.
///     </description>
///   </item>
/// </list>
/// </remarks>
public sealed partial class TreehopperBoard : IAsyncDisposable
{
    /// <summary>Treehopper USB Vendor ID (<c>0x10C4</c>).</summary>
    public static readonly HardwareId Vid = new(0x10C4);

    /// <summary>Treehopper USB Product ID (<c>0x8A7E</c>).</summary>
    public static readonly HardwareId Pid = new(0x8A7E);

    /// <summary>
    /// Per-transfer deadline handed to the USB transport. Treehopper transactions are
    /// all small and fast (sub-millisecond on a healthy board), so a wedged endpoint —
    /// a firmware hang that stops draining it — surfaces as a
    /// <see cref="UsbTimeoutException"/> within this bound instead of blocking forever.
    /// </summary>
    internal static readonly TimeSpan TransferTimeout = TimeSpan.FromSeconds(2);

    // Wire contract for RescueResetAsync, mirroring the firmware's TREEHOPPER_RESCUE_* defines in
    // Treehopper-EFM8/inc/treehopper.h. Host-to-device | vendor | device, guarded by a magic so a
    // probing vendor request cannot reset a live board. Does not collide with the Microsoft OS
    // descriptor requests the firmware already answers, which are device-to-host (0xC0 / 0xC1).
    private const byte RescueResetRequestType = 0x40;
    private const byte RescueResetRequest = 0x52;
    private const ushort RescueResetMagic = 0xA5A5;

    /// <summary>
    /// Whether SPI transfers may be clocked in the EFM8's silicon-bug danger band
    /// (0.8–6 MHz) instead of being rounded up to the safe 6 MHz boundary. This is
    /// the imperative shell reading the <c>TREEHOPPER_SPI_DANGER_BAND</c> environment
    /// variable exactly once (at type initialisation), so the pure wire codec
    /// (<see cref="TreehopperWire.Encode"/>) never touches the environment and stays
    /// deterministic (ADR-0052 DEC-001). DEBUG-ONLY: enabling it deliberately
    /// re-enables the firmware lock-up this guard exists to prevent.
    /// </summary>
    internal static readonly bool AllowSpiDangerBand =
        Environment.GetEnvironmentVariable("TREEHOPPER_SPI_DANGER_BAND") == "1";

    private readonly UsbDevice _usb;
    private readonly ILogger<TreehopperBoard> _logger;
    private readonly SemaphoreSlim _comsLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();

    // Fan-out: every active Reports enumeration registers its own channel here, so
    // each subscriber sees every report (broadcast) rather than competing for items.
    // The list doubles as its own lock object.
    private readonly List<Channel<BoardReport>> _subscribers = new();

    // ── Reconcile state ───────────────────────────────────────────────
    // The last config successfully committed to *this live connection*, or null if
    // the board has never been initialised. Each reconcile transforms this value,
    // ships the diff, then advances it — so it doubles as the transform base and the
    // Plan baseline. Guarded by _comsLock.
    //
    // INVARIANT — _applied is per-connection. The Treehopper protocol has no config
    // read-back (see ResyncAsync), so a non-null _applied is only ever a valid belief
    // about the metal *on the connection that produced it*. A reconnect-resilient
    // layer built on top MUST reset this to null on every (re)connection and let the
    // desired config re-apply from blank (ConfigureDevice + full re-send) — never
    // preserve _applied across a connection boundary. Doing so re-introduces the
    // classic "host thinks I2C is on, the metal reset it off" silent divergence.
    private BoardConfig? _applied;

    private Task? _producerTask;
    private BoardReport? _lastReport;                  // volatile cache for quick pin reads

    // volatile because the coms-lock paths re-read it AFTER acquiring the semaphore, and
    // the release they acquire from is the previous holder's, not DisposeAsync's — so
    // nothing else orders that read against DisposeAsync's write on another thread.
    private volatile bool _disposed;

    // Set once a transaction that expected a response failed to consume one (#263 item 3).
    // From then on the peripheral response endpoint may hold bytes belonging to a command
    // that has already given up, and nothing on the wire distinguishes them from a fresh
    // reply — so every later request/response call refuses rather than reading them.
    // Written under _comsLock, read under it too; volatile for the same reason as _disposed.
    private volatile bool _responsePipeDesynced;

    private TreehopperBoard(DeviceInfo deviceInfo, UsbDevice usb, ILogger<TreehopperBoard>? logger)
    {
        DeviceInfo = deviceInfo;
        _usb = usb;
        _logger = logger ?? NullLogger<TreehopperBoard>.Instance;

        var pins = ImmutableArray.CreateBuilder<Pin>(TreehopperWire.PinCount);
        for (int i = 0; i < TreehopperWire.PinCount; i++)
            pins.Add(new Pin(this, i));
        Pins = pins.ToImmutable();
    }

    /// <summary>The discovery snapshot this board was opened from.</summary>
    public DeviceInfo DeviceInfo { get; }

    /// <summary>The board's 20 I/O pins, indexed 0–19.</summary>
    public IReadOnlyList<Pin> Pins { get; }

    /// <summary>
    /// Whether the peripheral response endpoint has been left holding an unclaimed reply,
    /// after which every request/response operation on this board fails with
    /// <see cref="TreehopperDesyncException"/> (#263 item 3).
    /// </summary>
    /// <remarks>
    /// Latched, and deliberately not clearable: the protocol offers no way to drain the
    /// endpoint or to tell a stale reply from a fresh one, so the only honest recovery is a
    /// new connection. A reconnect-resilient layer should treat this the way it treats a
    /// transport fault — dispose the board and re-open. Config writes keep working, so an
    /// LED flush or pin reconcile does not start failing just because a transaction did.
    /// </remarks>
    public bool IsResponsePipeDesynced => _responsePipeDesynced;

    /// <summary>
    /// The firmware version, read from the USB device-release descriptor
    /// (<c>bcdDevice</c>). The original SDK exposes the same value; format it with
    /// <see cref="VersionString"/>.
    /// </summary>
    public int Version => _usb.Descriptor.DeviceVersion;

    /// <summary>The firmware version formatted as <c>major.minor</c> (e.g. <c>1.11</c>).</summary>
    public string VersionString => $"{Version / 100.0:0.00}";

    // ── Report stream (DEC-002) ────────────────────────────────────────

    /// <summary>
    /// Continuous broadcast of immutable pin-state snapshots emitted by the firmware.
    /// Each enumeration is an independent subscription — every subscriber sees every
    /// report. The first element is the current state (the latest report, replayed
    /// on subscribe); thereafter one element per change, since the firmware emits
    /// only when a monitored input moves.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the primary API for reading pin state (ADR-0052 DEC-002).
    /// Consume it with <c>await foreach (var report in board.Reports) { … }</c>,
    /// or layer the <c>System.Linq.Async</c> operators on top for
    /// <c>FirstAsync</c>/<c>Where</c>-style queries. For a single pin, prefer
    /// <see cref="PinHandle.ReadAsync"/> / <see cref="PinHandle.WatchAsync"/>.
    /// </para>
    /// <para>
    /// Each subscription is backed by its own bounded channel (capacity 32, drops
    /// oldest on overflow) so a slow consumer cannot stall the producer or other
    /// subscribers. Only started when the board is opened via <see cref="OpenAsync"/>;
    /// a board created via the internal <c>CreateForTest</c> factory has no producer
    /// — inject test reports with <see cref="InjectReportForTest"/>.
    /// </para>
    /// </remarks>
    public IAsyncEnumerable<BoardReport> Reports => SubscribeReportsAsync();

    /// <summary>
    /// The most recently received <see cref="BoardReport"/>, or
    /// <see langword="null"/> if no report has arrived yet. Updated by the
    /// producer on every valid packet.
    /// </summary>
    public BoardReport? LastReport => Volatile.Read(ref _lastReport);

    /// <summary>
    /// Returns the board's current <see cref="BoardReport"/> — the latest received,
    /// or the first to arrive if none has yet. Unlike <see cref="LastReport"/> it
    /// never returns <see langword="null"/>: it waits (honouring <paramref name="ct"/>)
    /// until a report is available.
    /// </summary>
    public async Task<BoardReport> ReadReportAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var cached = Volatile.Read(ref _lastReport);
        if (cached is not null) return cached;

        await foreach (var report in SubscribeReportsAsync(ct).ConfigureAwait(false))
            return report;
        throw new TreehopperException("Report stream ended before any report arrived.");
    }

    private async IAsyncEnumerable<BoardReport> SubscribeReportsAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var channel = Channel.CreateBounded<BoardReport>(new BoundedChannelOptions(32)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleWriter = true,
            SingleReader = true,
        });
        lock (_subscribers) _subscribers.Add(channel);
        try
        {
            // Replay the latest known report so a new subscriber learns current state
            // immediately — the firmware only emits on change, so without this a watch
            // on a steady pin would block until the next edge.
            var seed = Volatile.Read(ref _lastReport);
            if (seed is not null) yield return seed;

            await foreach (var report in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                yield return report;
        }
        finally
        {
            lock (_subscribers) _subscribers.Remove(channel);
            channel.Writer.TryComplete();
        }
    }

    // ── Declarative configuration (ADR-0052 DEC-003) ───────────────────

    /// <summary>
    /// Applies a desired board configuration atomically. The
    /// <paramref name="update"/> transform receives the last applied
    /// <see cref="BoardConfig"/> and returns the desired one; the board diffs the
    /// two, ships only the minimal set of commands, and commits the new state — all
    /// under the board-wide lock.
    /// </summary>
    /// <remarks>
    /// This is the power-user entry point for setting several things at once (LED,
    /// pin modes, peripheral enables) in a single reconcile, rather than one call
    /// each. Convenience methods (<see cref="SetLedAsync"/>, the <c>Use*Async</c>
    /// leases, pin handles) are thin wrappers over this.
    /// <code>
    /// await board.ReconcileAsync(cfg => cfg with
    /// {
    ///     LedOn = true,
    ///     Pins  = cfg.Pins.SetItem(3, new PinConfig(PinMode.PushPullOutput, true)),
    ///     I2c   = new I2cConfig(400),
    /// });
    /// </code>
    /// </remarks>
    /// <param name="update">Pure transform from the current desired config to the next.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    public Task ReconcileAsync(Func<BoardConfig, BoardConfig> update, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        ObjectDisposedException.ThrowIf(_disposed, this);
        return ReconcileWithAsync(update, ct);
    }

    /// <summary>
    /// Re-asserts the board's entire current configuration from a known reset:
    /// sends <c>ConfigureDevice</c> (which fully resets the firmware — all
    /// peripherals disabled, all pins high-impedance) and then re-applies every
    /// setting from that blank baseline.
    /// </summary>
    /// <remarks>
    /// Stopgap for the one structural limitation of the Treehopper wire protocol:
    /// it has <b>no config read-back</b>, so the host's model of the MCU registers
    /// can never be verified — only re-asserted. Reach for this only when you
    /// suspect the metal's registers reset out from under a still-open handle (a
    /// brown-out / glitch that resets the MCU without dropping USB); a real
    /// unplug/replug kills the handle, so the re-opened instance re-inits from blank
    /// on its own. The proper fix is a firmware patch adding a read-back command so
    /// the host can verify rather than assume — to be explored later.
    /// </remarks>
    public Task ResyncAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return ReconcileCoreAsync(cfg => cfg, forceFull: true, ct);
    }

    // ── Lifecycle commands & identity ──────────────────────────────────

    /// <summary>
    /// Reboots the board MCU. The USB device drops and re-enumerates, so this handle
    /// is dead afterwards — dispose it and re-open. Use after
    /// <see cref="UpdateNameAsync"/> / <see cref="UpdateSerialAsync"/> so other apps
    /// see the new identity.
    /// </summary>
    public Task RebootAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return FireConfigCommandAsync(new Command.Reboot(), ct);
    }

    /// <summary>
    /// Reboots the board into its USB-HID bootloader (re-enumerates as a DFU device at
    /// <c>0x10C4:0xEAC9</c>). This handle is dead afterwards. To reboot, replay a
    /// hex2boot-produced firmware image, and return to the app in one call, use
    /// <c>TreehopperFirmwareUpdate.ReflashAsync</c> (in the <c>Periphery.Treehopper.Firmware</c>
    /// package), which wraps this plus the generic <c>Periphery.Bootloader.Efm8.Usb</c> uploader.
    /// </summary>
    public Task RebootIntoBootloaderAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return FireConfigCommandAsync(new Command.EnterBootloader(), ct);
    }

    /// <summary>
    /// Resets the board out-of-band, over EP0, for use when <see cref="RebootAsync"/> cannot be
    /// delivered. Requires firmware carrying the rescue handler; older firmware ignores it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="RebootAsync"/> travels over the peripheral-config bulk endpoint, which the
    /// firmware re-arms only from its foreground superloop. If that superloop has stopped, or the
    /// endpoint is stuck, the reboot command goes to the very endpoint that is wedged and can
    /// never arrive. EP0 is serviced entirely from the device's USB ISR, so it stays reachable in
    /// exactly that state — which is what makes this the only in-band recovery for a wedged board
    /// short of a physical replug.
    /// </para>
    /// <para>
    /// <b>The transfer is expected to fail.</b> The device resets inside its ISR without completing
    /// the status stage, so the control transfer faults or the device vanishes mid-request. That is
    /// success. Crucially the converse does not hold: firmware <i>without</i> the handler stalls
    /// EP0 and produces an indistinguishable failure, so <b>the exception tells you nothing either
    /// way</b> and this method deliberately reports nothing. Confirm the reset by observing
    /// re-enumeration — an arrival timestamp, or a device-watcher event. Polling for the device to
    /// be absent is not reliable: it returns under the same instance id in a couple of hundred
    /// milliseconds and a sampling loop will miss the gap.
    /// </para>
    /// <para>This handle is dead afterwards; dispose it and re-open.</para>
    /// <para>
    /// <b>Use the static <see cref="RescueResetAsync(DeviceInfo, CancellationToken, ILoggerFactory?)"/>
    /// overload to rescue a board that is actually wedged.</b> Reaching this instance method
    /// requires a <see cref="TreehopperBoard"/>, and <see cref="OpenAsync"/> reconciles the board's
    /// configuration over the peripheral-config endpoint before it returns — the endpoint a wedged
    /// board is not draining. So on the boards this rescue exists for, the open throws and this
    /// method is never reached. The static overload skips the board entirely: it opens the USB
    /// device and nothing more.
    /// </para>
    /// </remarks>
    public Task RescueResetAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return SendRescueRequestAsync(_usb, DeviceInfo.Id, _logger, ct);
    }

    /// <summary>
    /// Resets a board out-of-band, over EP0, <b>without opening it as a board</b> — the form that
    /// works on a board whose foreground has stopped. Requires firmware carrying the rescue
    /// handler; older firmware ignores it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the rescue path that survives the failure mode the rescue is for. <see cref="OpenAsync"/>
    /// does not just acquire a handle: it reconciles the board's configuration, which writes
    /// <c>ConfigureDevice</c> to the peripheral-config bulk endpoint and waits for it. A board whose
    /// superloop has stopped never drains that endpoint, so the open times out and throws — and the
    /// instance <see cref="RescueResetAsync(CancellationToken)"/> can never be reached on precisely
    /// the boards it was written to rescue. This overload opens the <see cref="UsbDevice"/> and
    /// issues the control request, touching no bulk endpoint at all.
    /// </para>
    /// <para>
    /// Everything the instance method says about interpreting the result applies here unchanged:
    /// <b>the transfer is expected to fail, and its failure proves nothing either way</b>, because
    /// firmware without the handler stalls EP0 identically. This method reports nothing. Confirm a
    /// reset by observing re-enumeration — an arrival timestamp or a watcher event — never by
    /// polling for absence, which misses the ~230 ms gap.
    /// </para>
    /// </remarks>
    /// <param name="deviceInfo">The board to rescue, as discovery reported it.</param>
    /// <param name="ct">Cancels the USB open and the request; cancellation always propagates.</param>
    /// <param name="loggerFactory">Optional diagnostics for the transient USB handle.</param>
    public static async Task RescueResetAsync(
        DeviceInfo deviceInfo, CancellationToken ct = default, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(deviceInfo);

        var usb = await UsbDevice.OpenAsync(
            deviceInfo, ct, TransferTimeout,
            loggerFactory?.CreateLogger<UsbDevice>()).ConfigureAwait(false);
        try
        {
            await SendRescueRequestAsync(
                usb, deviceInfo.Id, loggerFactory?.CreateLogger<TreehopperBoard>(), ct).ConfigureAwait(false);
        }
        finally
        {
            // The link is dropping as the board resets, so a close fault here is expected.
            try { await usb.DisposeAsync().ConfigureAwait(false); } catch { /* expected as the link drops */ }
        }
    }

    // The one place the rescue request is built and sent, shared by both entry points so the wire
    // contract and the "report nothing" rule cannot drift between them.
    private static async Task SendRescueRequestAsync(
        UsbDevice usb, DeviceId id, ILogger? logger, CancellationToken ct)
    {
        var setup = new UsbControlSetup
        {
            RequestType = RescueResetRequestType,
            Request = RescueResetRequest,
            Value = RescueResetMagic,
            Index = 0,
        };

        try
        {
            await usb.ControlTransferAsync(setup, Memory<byte>.Empty, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Swallowed by design: a resetting device and one that never implemented the request
            // fault identically, so distinguishing them here is not possible and pretending
            // otherwise would be worse than saying nothing. Logged for the operator, not surfaced.
            logger?.LogDebug(
                ex, "Rescue reset transfer for {Device} faulted; expected when the board resets.", id);
        }
    }

    /// <summary>
    /// Writes a new device name to the board's EEPROM. The change persists across power
    /// cycles but is not visible to other applications until the board is rebooted
    /// (<see cref="RebootAsync"/>).
    /// </summary>
    /// <remarks>
    /// The limit is <see cref="TreehopperWire.IdentityMaxBytes"/> <em>UTF-8 bytes</em>, not
    /// characters: the board stores the payload one byte per character in a single 64-byte
    /// flash page. This was a character count, which let a name of 60 non-ASCII characters
    /// through to a write the board could not hold. See issue #170.
    /// </remarks>
    public async Task UpdateNameAsync(string name, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(name);
        if (Encoding.UTF8.GetByteCount(name) > TreehopperWire.IdentityMaxBytes)
            throw new ArgumentOutOfRangeException(
                nameof(name),
                $"Device name must encode to {TreehopperWire.IdentityMaxBytes} UTF-8 bytes or fewer.");
        await ExecuteTransactionAsync(new Command.UpdateName(name), ct).ConfigureAwait(false);
        // The flash write disables interrupts while it runs; give it time to settle.
        await Task.Delay(100, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes a new serial number to the board's EEPROM. The change persists across power
    /// cycles but is not visible to other applications until the board is rebooted
    /// (<see cref="RebootAsync"/>). Bounded in UTF-8 bytes, as
    /// <see cref="UpdateNameAsync"/> explains.
    /// </summary>
    public async Task UpdateSerialAsync(string serialNumber, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(serialNumber);
        if (Encoding.UTF8.GetByteCount(serialNumber) > TreehopperWire.IdentityMaxBytes)
            throw new ArgumentOutOfRangeException(
                nameof(serialNumber),
                $"Serial number must encode to {TreehopperWire.IdentityMaxBytes} UTF-8 bytes or fewer.");
        await ExecuteTransactionAsync(new Command.UpdateSerial(serialNumber), ct).ConfigureAwait(false);
        await Task.Delay(100, ct).ConfigureAwait(false);
    }

    // ── LED ────────────────────────────────────────────────────────────

    /// <summary>Turns the on-board LED on or off.</summary>
    public Task SetLedAsync(bool on, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return ReconcileWithAsync(cfg => cfg with { LedOn = on }, ct);
    }

    // ── Peripheral leases (DEC-003) ────────────────────────────────────

    /// <summary>Enables the I²C module and returns a lease for running transactions.</summary>
    /// <param name="speedKhz">Bus clock in kHz (≈62.5–16000); 100 is standard-mode.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    public async Task<I2cLease> UseI2cAsync(int speedKhz = 100, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await ReconcileWithAsync(
            cfg => cfg with { I2c = new I2cConfig(speedKhz) }, ct).ConfigureAwait(false);
        return new I2cLease(this);
    }

    /// <summary>Enables the SPI module and returns a lease for running transfers.</summary>
    /// <param name="clockMhz">Default clock speed in MHz (≈0.094–24). Overridable per-transfer.</param>
    /// <param name="mode">Default clock polarity / phase. Overridable per-transfer.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    public async Task<SpiLease> UseSpiAsync(
        double clockMhz = 6, SpiMode mode = SpiMode.Mode00, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await ReconcileWithAsync(cfg => cfg with { Spi = new SpiConfig() }, ct).ConfigureAwait(false);
        return new SpiLease(this, clockMhz, mode);
    }

    /// <summary>Enables the UART and returns a lease for send / receive.</summary>
    /// <param name="baud">Baud rate (≈7813–2 400 000).</param>
    /// <param name="ct">A token to cancel the operation.</param>
    public async Task<UartLease> UseUartAsync(int baud = 9600, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await ReconcileWithAsync(
            cfg => cfg with { Uart = new UartConfig(baud) }, ct).ConfigureAwait(false);
        return new UartLease(this);
    }

    /// <summary>Enables hardware PWM and returns a lease for driving its channels.</summary>
    /// <param name="frequency">Base frequency shared by all channels.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    public async Task<PwmLease> UsePwmAsync(
        PwmFrequency frequency = PwmFrequency.Freq732Hz, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var lease = new PwmLease(this, frequency);
        await lease.InitializeAsync(ct).ConfigureAwait(false);
        return lease;
    }

    /// <summary>
    /// Switches the UART into 1-Wire mode (TX open-drain, TX/RX tied together) and
    /// returns a lease for 1-Wire reset / ROM search / read / write — the substrate
    /// for Dallas/Maxim peripherals such as the DS18B20.
    /// </summary>
    public async Task<OneWireLease> UseOneWireAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await ReconcileWithAsync(
            cfg => cfg with { Uart = new UartConfig(Mode: UartMode.OneWire) }, ct).ConfigureAwait(false);
        return new OneWireLease(this);
    }

    /// <summary>
    /// Enables the 8080-style parallel interface and returns a lease for command/data
    /// writes (especially parallel-bus character/graphic displays).
    /// </summary>
    /// <param name="dataBusPins">The 4–16 data-bus pin numbers, least-significant first.</param>
    /// <param name="registerSelectPin">RS pin number, or -1 if unused.</param>
    /// <param name="readWritePin">R/W pin number, or -1 if unused.</param>
    /// <param name="enablePin">E (strobe) pin number, or -1 if unused.</param>
    /// <param name="delayMicroseconds">Settling delay after each strobe.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    public async Task<ParallelLease> UseParallelAsync(
        IReadOnlyList<int> dataBusPins,
        int registerSelectPin = -1,
        int readWritePin = -1,
        int enablePin = -1,
        int delayMicroseconds = 0,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(dataBusPins);
        if (dataBusPins.Count is < 4 or > 16)
            throw new ArgumentException("Parallel data bus must have between 4 and 16 pins.", nameof(dataBusPins));

        var bus = dataBusPins.Select(p => (byte)p).ToImmutableArray();
        await ReconcileWithAsync(
            cfg => cfg with
            {
                Parallel = new ParallelConfig(
                    bus, registerSelectPin, readWritePin, enablePin, delayMicroseconds)
            }, ct).ConfigureAwait(false);
        return new ParallelLease(this, dataBusPins.Count);
    }

    // ── Discovery / open / dispose ─────────────────────────────────────

    /// <summary>
    /// Snapshots the Treehopper boards currently connected. Sugar for
    /// <c>Devices.Enumerate().WithUsbId(Vid, Pid).ToListAsync(ct)</c>; returns an
    /// immediate list rather than an <see cref="IAsyncEnumerable{T}"/> so UI
    /// consumers can bind directly. (Mirrors <c>CameraDevice.EnumerateAsync</c>.)
    /// </summary>
    public static async Task<IReadOnlyList<DeviceInfo>> EnumerateAsync(CancellationToken ct = default)
        => await Devices.Enumerate().WithUsbId(Vid, Pid).ToListAsync(ct).ConfigureAwait(false);

    /// <summary>
    /// Opens the first connected Treehopper board — the one-liner for the common
    /// single-board case. Throws <see cref="TreehopperException"/> if none is found.
    /// </summary>
    public static async Task<TreehopperBoard> OpenFirstAsync(CancellationToken ct = default)
    {
        var boards = await EnumerateAsync(ct).ConfigureAwait(false);
        if (boards.Count == 0)
            throw new TreehopperException($"No Treehopper board ({Vid}:{Pid}) is connected.");
        return await OpenAsync(boards[0], ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Opens the Treehopper board identified by <paramref name="deviceInfo"/>,
    /// sends the initialisation command, and starts the background pin-report
    /// producer. Matches the <c>createSession</c> factory signature for
    /// <c>DeviceSessionHost&lt;TreehopperBoard&gt;</c>.
    /// </summary>
    public static Task<TreehopperBoard> OpenAsync(DeviceInfo deviceInfo, CancellationToken ct = default)
        => OpenAsync(deviceInfo, ct, loggerFactory: null);

    /// <summary>
    /// Opens a board with diagnostics wired in. <paramref name="loggerFactory"/> mints the
    /// board's <see cref="ILogger{TCategoryName}"/> and the underlying transport's
    /// <c>ILogger&lt;UsbDevice&gt;</c> — pass your application's factory
    /// to capture open/close, per-transaction, and report-producer diagnostics. Metrics
    /// flow to the <c>Periphery.Treehopper</c> and <c>Periphery.Usb</c> Meters regardless
    /// of whether a factory is supplied. A per-transfer watchdog
    /// (<see cref="TransferTimeout"/>) faults a wedged transfer with
    /// <see cref="UsbTimeoutException"/> rather than hanging the caller.
    /// </summary>
    public static async Task<TreehopperBoard> OpenAsync(
        DeviceInfo deviceInfo, CancellationToken ct, ILoggerFactory? loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(deviceInfo);

        var logger = loggerFactory?.CreateLogger<TreehopperBoard>();
        var usb = await UsbDevice.OpenAsync(
            deviceInfo, ct, TransferTimeout, loggerFactory?.CreateLogger<UsbDevice>()).ConfigureAwait(false);
        try
        {
            var board = new TreehopperBoard(deviceInfo, usb, logger);
            // First reconcile: _applied == null → Plan prepends ConfigureDevice,
            // then reconciles Blank → Blank (no other commands).
            await board.ReconcileWithAsync(cfg => cfg, ct).ConfigureAwait(false);
            board._producerTask = Task.Run(
                () => board.ProduceReportsAsync(board._cts.Token));
            LogBoardOpened(board._logger, deviceInfo.Name, deviceInfo.SerialNumber ?? "?", board.VersionString);
            return board;
        }
        catch
        {
            await usb.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();
        CompleteAllSubscribers();

        if (_producerTask is not null)
        {
            // Best-effort drain — the producer swallows its own cancellation, but
            // guard against anything the transport throws on teardown.
            try { await _producerTask.ConfigureAwait(false); }
            catch { /* shutting down */ }
        }

        // Wait for whoever holds the coms lock to finish before tearing the transport
        // down under them (#263 item 2 — #262 fixed the semaphore half of this teardown
        // and deliberately left this half).
        //
        // _cts.Cancel() above reaches only the report producer; a reconcile, transaction,
        // lease teardown or LED flush is on its own token and runs to completion. Without
        // this wait, `await _usb.DisposeAsync()` freed the transport while one of them was
        // mid-transfer.
        //
        // BOUNDED, and it proceeds anyway on expiry. A caller wedged on a dead endpoint
        // must not be able to hang dispose — that would take the whole reconnect path
        // down, which is worse than the race being closed here. The bound is the transfer
        // deadline plus margin, so a caller that is merely slow always wins the wait and
        // only a genuinely stuck one is stepped over.
        //
        // What covers the expiry path is NOT the transport's drain — that only knows about
        // transfers already registered with the backend, and the danger here is a caller
        // still queued on _comsLock that has not issued one yet. It is the _disposed
        // re-check every coms-lock path performs after acquiring the semaphore: the stuck
        // holder eventually releases, the queued caller acquires, sees teardown has begun,
        // and unwinds instead of writing to a transport that is gone.
        bool quiesced = await _comsLock.WaitAsync(TransferTimeout * 2, CancellationToken.None)
            .ConfigureAwait(false);

        try
        {
            await _usb.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            if (quiesced)
                _comsLock.Release();
        }

        // Deliberately NOT disposing _comsLock or _cts (#262 — the same defect #259
        // reported in DeviceProxyBase, one layer down).
        //
        // _cts.Cancel() above reaches the report producer and nothing else: it is the
        // only holder of that token. Every other caller — a reconcile, a transaction, a
        // lease teardown, an LED flush — passes its OWN token, so this method neither
        // cancels nor awaits them, and it does not take _comsLock. Callers are therefore
        // still in flight right here: one may hold the semaphore inside a transfer, and
        // any number more may be parked in WaitAsync behind it.
        //
        // That race is benign — they finish, find the board disposed, and unwind — right
        // up until the semaphore is disposed underneath them, at which point an ordinary
        // teardown becomes an ObjectDisposedException out of WaitAsync (for the parked
        // callers) or Release (for the holder), thrown inside work that is frequently
        // detached and so has no caller to catch it. The higher the write cadence, the
        // likelier a caller is queued at exactly this moment.
        //
        // Neither dispose buys anything in exchange. SemaphoreSlim.Dispose is only
        // required once AvailableWaitHandle has been touched, which nothing here does;
        // _cts is a plain, already-cancelled source with no timer. _cts is the safer of
        // the two — the producer that holds its token is awaited above — but it is
        // dropped for the same reason rather than leaving a sharp edge that only stays
        // blunt while that ordering holds. Both are plain collectable objects.
        LogBoardClosed(_logger, DeviceInfo.Name, DeviceInfo.SerialNumber ?? "?");
    }

    // ── Transport plumbing (internal — used by leases and pins) ──────

    /// <summary>
    /// Applies a pure transform to the current desired config, diffs against the
    /// last applied config, encodes the delta commands, and ships them — all under
    /// the board-wide coms lock. This is the single path through which all
    /// configuration changes (LED, pin mode, peripheral enable, PWM) flow.
    /// (ADR-0052 DEC-003.)
    /// </summary>
    internal Task ReconcileWithAsync(Func<BoardConfig, BoardConfig> update, CancellationToken ct)
        => ReconcileCoreAsync(update, forceFull: false, ct);

    private async Task ReconcileCoreAsync(
        Func<BoardConfig, BoardConfig> update, bool forceFull, CancellationToken ct)
    {
        await _comsLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Teardown may have begun while this caller was queued here (#263 item 2).
            // DisposeAsync's wait for this lock is bounded and proceeds on expiry, so
            // holding the lock is no longer proof the transport is alive — re-check.
            //
            // QUIETLY, not by throwing. Reconciles are routinely detached — an LED flush,
            // a soft-PWM tick, a lease teardown — and an ObjectDisposedException raised
            // inside detached work is precisely the unobserved-task fault #259 and #262
            // were about. Nothing is left half-applied either: _applied only advances
            // after every command has shipped, so a reconcile skipped here is a reconcile
            // that never started.
            if (_disposed) return;

            var next = update(_applied ?? BoardConfig.Blank);
            // forceFull plans against a null baseline → Plan prepends ConfigureDevice
            // (a true firmware reset) and re-sends every command, re-asserting the
            // board from a known state rather than trusting the cached delta.
            var baseline = forceFull ? null : _applied;
            var commands = TreehopperWire.Plan(next, baseline);

            foreach (var cmd in commands)
            {
                var (endpoint, bytes) = TreehopperWire.Encode(cmd);
                await WriteChunkedAsync(endpoint, bytes, ct).ConfigureAwait(false);
            }

            // Advance only after every command shipped — a mid-stream failure
            // leaves _applied untouched, so the next reconcile re-plans the full delta.
            _applied = next;
        }
        catch (UsbException ex)
        {
            TreehopperMeters.TransactionErrorsTotal.Add(1);
            throw new TreehopperException("Treehopper reconcile failed.", ex);
        }
        finally
        {
            _comsLock.Release();
        }
    }

    /// <summary>
    /// Sends a transaction command (I²C, SPI, or UART) and reads the response,
    /// all under the board-wide coms lock. Transaction commands are not config
    /// changes and do not go through the reconcile planner.
    /// </summary>
    internal async Task<byte[]> ExecuteTransactionAsync(Command cmd, CancellationToken ct)
    {
        await _comsLock.WaitAsync(ct).ConfigureAwait(false);
        long startTs = Stopwatch.GetTimestamp();

        // Hoisted out of the try so the finally can tell whether a reply was owed, and
        // whether the command that would have produced one ever left the host.
        int responseLen = TreehopperWire.ResponseLength(cmd);
        bool dispatched = false;
        bool consumedResponse = false;
        try
        {
            // See ReconcileCoreAsync: acquiring the lock no longer proves the transport is
            // alive. This path DOES throw, unlike the reconcile ones — it is a
            // request/response call whose caller is awaiting a result, so there is someone
            // to catch it, and the alternative is handing back an empty response that
            // reads as a reply from the board. ObjectDisposedException is what every
            // public entry point into this board already throws once disposed.
            ObjectDisposedException.ThrowIf(_disposed, this);
            ThrowIfResponsePipeDesynced();

            var (endpoint, bytes) = TreehopperWire.Encode(cmd);

            await WriteChunkedAsync(endpoint, bytes, ct, () => dispatched = true).ConfigureAwait(false);

            var response = responseLen > 0
                ? await ReadChunkedAsync(TreehopperWire.PeripheralResponseEndpoint, responseLen, ct)
                    .ConfigureAwait(false)
                : [];

            consumedResponse = true;

            double ms = Stopwatch.GetElapsedTime(startTs).TotalMilliseconds;
            TreehopperMeters.TransactionsTotal.Add(1);
            TreehopperMeters.TransactionDuration.Record(ms);
            LogTransaction(_logger, cmd.GetType().Name, bytes.Length, responseLen, ms);
            return response;
        }
        catch (UsbException ex)
        {
            // Count the fault, then surface it as a typed exception the caller can act on
            // (reconnect / alert). The transport already logged the underlying transfer
            // failure or timeout, so we don't double-log here (log-or-throw, not both).
            TreehopperMeters.TransactionErrorsTotal.Add(1);
            throw new TreehopperException("Treehopper transaction failed.", ex);
        }
        finally
        {
            // #263 item 3. A transaction that owed a response and did not consume one leaves
            // that response unaccounted for: the command is on the wire, so the device may
            // still queue a reply nobody is waiting for, and the next read on this endpoint
            // would take it as its own. The protocol has no sequence field to catch that,
            // which makes it silent corruption — an I2C read returning some other command's
            // bytes — rather than a visible failure.
            //
            // Deliberately in the finally, not the catch: a timeout arrives as UsbException
            // but a caller-cancelled read arrives as OperationCanceledException, and both
            // strand the same reply.
            //
            // Gated on `dispatched`, though. An exit that happens BEFORE the command reaches
            // the transport — the board was disposed, the pipe was already desynced, Encode
            // threw — cannot have produced a reply, and latching there would cost a
            // connection for a command the device never saw (#271 review turn 1).
            //
            // Once the FINAL packet is ISSUED, the state is indeterminate and this errs toward
            // latching: a bulk write that faults or is cancelled after that point may still
            // have delivered its bytes, so "it threw" is not evidence the device stayed
            // ignorant. Before that point there is no indeterminacy to respect — the transport
            // never began the packet — which is why `dispatched` is driven by UsbDevice's
            // issue callback rather than by this method being about to call it.
            //
            // The remaining asymmetry is deliberate: over-latching costs a reconnect,
            // under-latching costs an I2C read returning another command's bytes with nothing
            // reporting a fault. Where the state is unknowable, the tie breaks toward the
            // cheap failure.
            if (responseLen > 0 && dispatched && !consumedResponse)
                MarkResponsePipeDesynced();

            _comsLock.Release();
        }
    }

    /// <summary>
    /// Writes a config command with no response, tolerating the transport error that
    /// occurs when the command (reboot / enter-bootloader) drops the USB link.
    /// </summary>
    private async Task FireConfigCommandAsync(Command cmd, CancellationToken ct)
    {
        await _comsLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // See ReconcileCoreAsync. Quiet, for the same reason: a reboot / bootloader
            // command already tolerates the link vanishing under it, so a board that is
            // being torn down is not a new kind of failure worth throwing over.
            if (_disposed) return;

            var (endpoint, bytes) = TreehopperWire.Encode(cmd);
            await WriteChunkedAsync(endpoint, bytes, ct).ConfigureAwait(false);
        }
        catch (UsbException) { /* the board drops the USB link as it reboots — expected */ }
        finally
        {
            _comsLock.Release();
        }
    }

    /// <summary>Sets (or updates) a pin's soft-PWM tick value. Used by <see cref="SoftPwmHandle"/>.</summary>
    internal Task SetSoftPwmAsync(byte pin, ushort ticks, CancellationToken ct)
        => ReconcileWithAsync(cfg => cfg with { SoftPwm = cfg.SoftPwm.SetItem(pin, ticks) }, ct);

    /// <summary>Removes a pin from the soft-PWM set. Used by <see cref="SoftPwmHandle"/>.</summary>
    internal Task ClearSoftPwmAsync(byte pin, CancellationToken ct)
        => ReconcileWithAsync(cfg => cfg with { SoftPwm = cfg.SoftPwm.Remove(pin) }, ct);

    /// <summary>
    /// Runs a 1-Wire ROM search: writes the scan command and streams 9-byte ROM
    /// packets until the firmware sends the terminator. Used by <see cref="OneWireLease"/>.
    /// </summary>
    internal async Task<IReadOnlyList<ulong>> ExecuteOneWireSearchAsync(CancellationToken ct)
    {
        await _comsLock.WaitAsync(ct).ConfigureAwait(false);
        bool dispatched = false;
        bool readTerminator = false;
        try
        {
            // See ExecuteTransactionAsync — a search whose caller is awaiting the ROM
            // list must not report "no devices" because the board was disposed.
            ObjectDisposedException.ThrowIf(_disposed, this);
            ThrowIfResponsePipeDesynced();

            var (endpoint, bytes) = TreehopperWire.Encode(new Command.OneWireScan());
            await WriteChunkedAsync(endpoint, bytes, ct, () => dispatched = true).ConfigureAwait(false);

            var roms = new List<ulong>();
            while (true)
            {
                var packet = await ReadChunkedAsync(
                    TreehopperWire.PeripheralResponseEndpoint, TreehopperWire.OneWireRomPacketLength, ct)
                    .ConfigureAwait(false);
                if (packet[0] == TreehopperWire.OneWireScanTerminator)
                    break;
                roms.Add(TreehopperWire.DecodeOneWireRom(packet));
            }

            // Only the terminator ends the stream cleanly. Anything else — a fault, a
            // cancellation — leaves the rest of the ROM packets queued behind us.
            readTerminator = true;
            return roms;
        }
        catch (UsbException ex)
        {
            throw new TreehopperException("Treehopper 1-Wire search failed.", ex);
        }
        finally
        {
            // See ExecuteTransactionAsync for the gate. A search that fails before the scan
            // command is dispatched has no ROM packets queued behind it.
            if (dispatched && !readTerminator)
                MarkResponsePipeDesynced();

            _comsLock.Release();
        }
    }

    /// <summary>
    /// Refuses a request/response operation once the response endpoint has been left
    /// holding an unclaimed reply. Called with <c>_comsLock</c> held.
    /// </summary>
    private void ThrowIfResponsePipeDesynced()
    {
        if (!_responsePipeDesynced) return;

        throw new TreehopperDesyncException(
            "This board's peripheral response endpoint may still hold the reply to an earlier "
            + "transaction that timed out or was cancelled, and the Treehopper protocol has no "
            + "way to tell that reply from a fresh one. Reading it would silently return another "
            + "command's bytes. Dispose this board and re-open it — a new connection starts with "
            + "an empty endpoint.");
    }

    private void MarkResponsePipeDesynced()
    {
        // Latched: nothing here can drain the endpoint or prove it empty, so there is no
        // honest way back short of a new connection.
        if (_responsePipeDesynced) return;

        _responsePipeDesynced = true;
        TreehopperMeters.ResponsePipeDesyncsTotal.Add(1);
        LogResponsePipeDesynced(_logger, DeviceInfo.Name, DeviceInfo.SerialNumber ?? "?");
    }

    // ── Report producer ────────────────────────────────────────────────

    private async Task ProduceReportsAsync(CancellationToken ct)
    {
        long sequence = 0;
        using var scope = _logger.BeginScope("Board={Serial}", DeviceInfo.SerialNumber ?? "?");
        LogReportProducerStarted(_logger);
        try
        {
            await foreach (var packet in _usb.ReadBulkStreamAsync(
                TreehopperWire.PinReportEndpoint, TreehopperWire.PinReportLength, ct)
                .ConfigureAwait(false))
            {
                // report[0] == 0 → padding / invalid; firmware only emits non-zero IDs.
                if (packet.Length < TreehopperWire.PinReportLength || packet.Span[0] == 0)
                    continue;

                var report = TreehopperWire.DecodeReport(packet.Span, sequence++);
                TreehopperMeters.ReportsTotal.Add(1);
                Publish(report);
            }
            LogReportProducerStopped(_logger, sequence);
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
        catch (Exception ex)
        {
            // The producer runs on a detached Task.Run, so without this catch a transport
            // fault (board unplugged, endpoint error) would fault that task unobserved —
            // the pin-state stream would silently stop with no diagnostic. Log it at Error
            // (a worker-loop fault the caller can't observe directly), then let the finally
            // complete every subscriber so their `await foreach` ends cleanly.
            LogReportProducerFaulted(_logger, DeviceInfo.SerialNumber ?? "?", ex);
        }
        finally
        {
            CompleteAllSubscribers();
        }
    }

    /// <summary>Caches the latest report and fans it out to every active subscriber.</summary>
    private void Publish(BoardReport report)
    {
        Volatile.Write(ref _lastReport, report);
        Channel<BoardReport>[] subs;
        lock (_subscribers) subs = _subscribers.ToArray();
        // Non-blocking writes; each channel is bounded with DropOldest, so a slow
        // subscriber never stalls the producer or the other subscribers.
        foreach (var sub in subs) sub.Writer.TryWrite(report);
    }

    private void CompleteAllSubscribers()
    {
        Channel<BoardReport>[] subs;
        lock (_subscribers) subs = _subscribers.ToArray();
        foreach (var sub in subs) sub.Writer.TryComplete();
    }

    // ── Chunked I/O helpers ────────────────────────────────────────────

    /// <param name="onDispatch">
    /// Invoked when the <b>final</b> packet of the command is issued to the transport — the
    /// first instant at which the device could hold a <em>complete</em> command. Lets a
    /// transaction distinguish "the command never went out" from "the command may have gone
    /// out", which is the difference between a reply that cannot exist and one that might be
    /// sitting on the response endpoint (#263 item 3).
    /// <para>
    /// The final packet, not the first, because reaching it means every prior packet
    /// succeeded; not reaching it means the firmware has at most a truncated prefix, which it
    /// waits on rather than answers (#271 review turn 3).
    /// </para>
    /// <para>
    /// <em>Issued</em>, not "about to be attempted": the callback is handed to
    /// <see cref="UsbDevice.BulkWriteAsync"/> and fires only once that packet clears the pipe
    /// gate. A cancellation or deadline that lands while the packet is still queued therefore
    /// does not count as dispatched, and the board is not tainted for a command the transport
    /// never began (#271 review turn 5). Past that point the outcome is genuinely
    /// indeterminate — a write can deliver and then fault — and the caller treats it as
    /// dispatched.
    /// </para>
    /// </param>
    private async Task WriteChunkedAsync(
        byte endpoint, byte[] data, CancellationToken ct, Action? onDispatch = null)
    {
        if (data.Length == 0)
        {
            await _usb.BulkWriteAsync(endpoint, ReadOnlyMemory<byte>.Empty, ct, onDispatch)
                .ConfigureAwait(false);
            return;
        }
        for (int offset = 0; offset < data.Length; offset += TreehopperWire.MaxPacket)
        {
            int len = Math.Min(TreehopperWire.MaxPacket, data.Length - offset);
            bool last = offset + len >= data.Length;
            await _usb.BulkWriteAsync(endpoint, data.AsMemory(offset, len), ct, last ? onDispatch : null)
                .ConfigureAwait(false);
        }
    }

    private async Task<byte[]> ReadChunkedAsync(byte endpoint, int count, CancellationToken ct)
    {
        var result = new byte[count];
        int got = 0;
        while (got < count)
        {
            int chunk = Math.Min(TreehopperWire.MaxPacket, count - got);
            var data = await _usb.BulkReadAsync(endpoint, chunk, ct).ConfigureAwait(false);

            // A read that returns nothing cannot be advanced past: `got` would not move and
            // this would spin forever. It also means the response is not coming, so say so
            // rather than handing back a buffer that is partly this reply and partly zeros.
            if (data.Length == 0)
                throw new TreehopperException(
                    $"Endpoint 0x{endpoint:X2} returned no data with {count - got} of {count} "
                    + "response bytes still outstanding.");

            int copied = Math.Min(data.Length, count - got);
            Array.Copy(data, 0, result, got, copied);

            // By bytes RETURNED, not bytes requested (#263 item 4). `got += chunk` skipped
            // over whatever a short read had not delivered, so the tail of `result` stayed
            // zeroed and was returned as if the device had sent it — a silently wrong
            // response rather than a retry of the remainder.
            got += copied;
        }
        return result;
    }

    // ── Test helpers ───────────────────────────────────────────────────

    /// <summary>
    /// Test-only factory. Creates a board over an injected (fake-backed)
    /// <see cref="UsbDevice"/>, bypassing <see cref="OpenAsync"/> and its
    /// background producer. The board is pre-initialized (no ConfigureDevice
    /// emitted on first reconcile). Use <see cref="InjectReportForTest"/> to
    /// push synthetic reports into <see cref="Reports"/>.
    /// </summary>
    internal static TreehopperBoard CreateForTest(DeviceInfo deviceInfo, UsbDevice usb)
    {
        var board = new TreehopperBoard(deviceInfo, usb, logger: null);
        // Mark as post-init so Plan does not prepend ConfigureDevice on the
        // first reconcile call in tests.
        board._applied = BoardConfig.Blank;
        return board;
    }

    /// <summary>
    /// Injects a synthetic <see cref="BoardReport"/> directly into the channel
    /// (test-only — bypasses the hardware producer).
    /// </summary>
    internal void InjectReportForTest(BoardReport report) => Publish(report);

    // ── Source-generated log methods ───────────────────────────────────

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Treehopper board '{Name}' opened (serial {Serial}, firmware {Firmware})")]
    private static partial void LogBoardOpened(ILogger logger, string name, string serial, string firmware);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Treehopper board '{Name}' closed (serial {Serial})")]
    private static partial void LogBoardClosed(ILogger logger, string name, string serial);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Treehopper board '{Name}' (serial {Serial}) desynced: a transaction did not "
            + "consume the response it asked for, so the peripheral response endpoint may hold "
            + "a stale reply. Request/response operations now fail until the board is re-opened.")]
    private static partial void LogResponsePipeDesynced(ILogger logger, string name, string serial);

    [LoggerMessage(Level = LogLevel.Trace,
        Message = "Transaction {Command}: {WriteBytes} B out, {ReadBytes} B in, {ElapsedMs:F2} ms")]
    private static partial void LogTransaction(
        ILogger logger, string command, int writeBytes, int readBytes, double elapsedMs);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Report producer started")]
    private static partial void LogReportProducerStarted(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Report producer stopped after {Reports} report(s)")]
    private static partial void LogReportProducerStopped(ILogger logger, long reports);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Report producer for board {Serial} faulted — pin-state stream stopped")]
    private static partial void LogReportProducerFaulted(ILogger logger, string serial, Exception ex);
}
