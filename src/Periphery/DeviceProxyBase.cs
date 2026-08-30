// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Periphery;

/// <summary>
/// Abstract base class for reconnect-resilient device handles. Owns the
/// concurrency, lifecycle, and reconnect state machine so that derived
/// classes supply only the device-specific open/close logic.
/// </summary>
/// <typeparam name="TDevice">
/// The platform device type (e.g. <c>HidDevice</c>, <c>SerialPort</c>).
/// Must be a reference type implementing <see cref="IAsyncDisposable"/>.
/// </typeparam>
/// <typeparam name="TException">
/// The exception type surfaced by <see cref="OpenFailed"/>. Allows extension
/// packages to expose typed exceptions (e.g. <c>HidException</c>) without
/// consumers needing to cast.
/// </typeparam>
/// <remarks>
/// <para><b>Extension packages</b> (Periphery.Hid, .Serial, .Usb) derive from
/// this class, override the four hooks, and ship a sealed leaf type with a
/// static <c>OpenAsync</c> factory.</para>
/// <para><b>Application code</b> should use the delegate-configured
/// <see cref="DeviceProxy{TDevice}"/> instead — no derived class needed.</para>
/// <para><b>Threading:</b> All events fire on thread-pool threads.
/// UI dispatch is the consumer's responsibility.</para>
/// </remarks>
public abstract class DeviceProxyBase<TDevice, TException>
    : INotifyPropertyChanged, IAsyncDisposable
    where TDevice : class, IAsyncDisposable
    where TException : Exception
{
    private readonly DeviceTracker _tracker;
    private readonly DeviceWatcher? _ownedWatcher;
    private readonly IRecoveryPolicy _recoveryPolicy;
    private readonly IDeviceReset _deviceReset;
    private readonly IResetSafetyGate? _resetSafetyGate;
    private readonly bool _faultedNodeRecovery;
    private TDevice? _device;
    private ConnectionState _state;
    private Exception? _lastOpenFault;
    private Exception? _lastFault;
    private bool _disposed;
    private int _reconnectInProgress;
    private int _faultedRecoveryInProgress;
    private int _resetCount;
    private readonly SemaphoreSlim _openLock = new(1, 1);

    // Makes "decide to publish an opened device" and "decide to tear down" mutually
    // exclusive (#259 review). _disposed on its own is a plain field, so every read of
    // it is a check-then-act; for most call sites that is fine, because losing the race
    // only costs a wasted retry. The open path's publication is the one place where it
    // is load-bearing: it decides whether a live device handle becomes this proxy's
    // _device or is closed on the spot, and exactly one of those must happen. Both
    // critical sections are short, synchronous, and never await or raise events, so a
    // plain monitor is the right primitive. Lock order is one-way -- DisposeAsync
    // releases this gate before its close ever reaches _openLock -- so there is no cycle.
    private readonly object _lifecycleGate = new();
    private readonly CancellationTokenSource _disposeCts = new();
    private CancellationTokenSource? _connectionCts;

    // ADR-0060 reset/recovery timing — shell-owned (the policy stays pure).
    // ResetReopenTimeout / ResetReopenPollInterval are overridable so tests can drive
    // the self-reopen backstop deterministically without real 10s waits.
    private static readonly TimeSpan ResetReopenTimeoutDefault = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ResetReopenPollIntervalDefault = TimeSpan.FromMilliseconds(250);
    protected virtual TimeSpan ResetReopenTimeout => ResetReopenTimeoutDefault;
    protected virtual TimeSpan ResetReopenPollInterval => ResetReopenPollIntervalDefault;
    private static readonly TimeSpan ResetDeferDelay = TimeSpan.FromSeconds(1);

    // ADR-0060 stable-open dwell. A freshly-opened session must STAY open for this
    // long before its reset budget (and last fault) are cleared. The motivating
    // wedge opens cleanly (the open only exercises a healthy CONFIG endpoint) and
    // then re-faults ~2s later on the first real IO over the wedged DATA endpoint;
    // clearing the budget the instant open returns would restart the recovery ladder
    // at strategy [0] every cycle, so it would never escalate and never reach GaveUp
    // (a self-made flap loop for a re-enumerating strategy). 5s comfortably outlasts
    // that fast refault while still being short for a genuinely-stable session.
    // Overridable so tests can drive the dwell deterministically without real waits.
    private static readonly TimeSpan StableOpenDwellDefault = TimeSpan.FromSeconds(5);
    protected virtual TimeSpan StableOpenDwell => StableOpenDwellDefault;

    // ADR-0060 Decision 11 faulted-node settle window. When a matched device is
    // enumerated-but-faulted (Present, never Active) we do NOT reset instantly: a
    // freshly-enumerated node often reports a transient problem code for a moment
    // while the driver finishes starting, then transitions to Active on its own. We
    // give it this long to self-clear before engaging the reset ladder. 3s is well
    // past a normal driver bring-up's transient-problem window yet short enough that a
    // genuinely-stuck node (a 45-minute wedge observed in the field) heals promptly.
    // Overridable so tests can drive it deterministically without real waits.
    private static readonly TimeSpan FaultedNodeSettleWindowDefault = TimeSpan.FromSeconds(3);
    protected virtual TimeSpan FaultedNodeSettleWindow => FaultedNodeSettleWindowDefault;

    // Monotonic id of the live connection. Bumped under the open lock each time a
    // session opens; the stable-open dwell captures it and only clears the budget if
    // it is still the current generation (the race guard against a stale dwell timer
    // zeroing a newer connection's budget).
    private int _connectionGeneration;

    // Recovery-flow diagnostics (ADR-0060). Category "Periphery.DeviceProxy"; per-device
    // context rides on the {Device} field of each message. Dormant until a consumer wires
    // PeripheryLoggerFactory (the CLI's `--verbose`, or a host's own file logger).
    private static readonly ILogger _logger = PeripheryLoggerFactory.CreateLogger("Periphery.DeviceProxy");

    // Stable, human-readable label for this proxy's device, for log correlation.
    private string Label => _tracker.Name ?? _tracker.Device?.Id ?? "device";

    /// <summary>
    /// Initializes the base class with a tracker and an owned watcher.
    /// The watcher will be disposed when this handle is disposed.
    /// </summary>
    /// <param name="tracker">The device tracker to supervise.</param>
    /// <param name="ownedWatcher">The watcher disposed alongside this handle.</param>
    /// <param name="recoveryPolicy">
    /// Policy governing recovery (retry cadence, reset, give-up). Defaults to
    /// <see cref="ExponentialBackoffRecoveryPolicy.Default"/> (the legacy
    /// 1→2→4→5 s capped, retry-forever, never-reset curve) when <see langword="null"/>.
    /// </param>
    /// <param name="deviceReset">
    /// The reset mechanism the <c>reset</c> rung uses. Defaults to
    /// <see cref="DeviceReset.PlatformDefault"/> (cfgmgr32 on Windows, no-op
    /// elsewhere); reset is only ever invoked when the policy returns
    /// <see cref="RecoveryDirective.Reset"/>, so the default is dormant.
    /// </param>
    /// <param name="resetSafetyGate">
    /// Optional predicate consulted before a reset (e.g. "no sale in progress").
    /// <see langword="null"/> means always-safe.
    /// </param>
    /// <param name="faultedNodeRecovery">
    /// Opt-in (ADR-0060 Decision 11). When <see langword="true"/>, a matched device
    /// that enumerates with a genuine OS fault (<see cref="DeviceStatus.Error"/> /
    /// resettable problem code) but never reaches <see cref="DeviceActivityStatus.Active"/>
    /// drives the reset ladder after a settle window, instead of sitting dead until an
    /// external disable/enable. <see langword="false"/> (the default) preserves the
    /// prior behavior exactly: the proxy only ever acts on an Active device.
    /// </param>
    protected DeviceProxyBase(
        DeviceTracker tracker,
        DeviceWatcher ownedWatcher,
        IRecoveryPolicy? recoveryPolicy = null,
        IDeviceReset? deviceReset = null,
        IResetSafetyGate? resetSafetyGate = null,
        bool faultedNodeRecovery = false)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(ownedWatcher);
        _tracker = tracker;
        _ownedWatcher = ownedWatcher;
        _recoveryPolicy = recoveryPolicy ?? ExponentialBackoffRecoveryPolicy.Default;
        _deviceReset = deviceReset ?? DeviceReset.PlatformDefault;
        _resetSafetyGate = resetSafetyGate;
        _faultedNodeRecovery = faultedNodeRecovery;
        _tracker.StateChanged += OnTrackerStateChanged;
    }

    /// <summary>
    /// Initializes the base class with a tracker that is already attached
    /// to a caller-owned watcher. The handle does not own or dispose the
    /// watcher. Use <see cref="CheckInitialState"/> after construction to
    /// handle already-active trackers.
    /// </summary>
    /// <param name="tracker">The device tracker to supervise.</param>
    /// <param name="recoveryPolicy">
    /// Policy governing recovery (retry cadence, reset, give-up). Defaults to
    /// <see cref="ExponentialBackoffRecoveryPolicy.Default"/> when <see langword="null"/>.
    /// </param>
    /// <param name="deviceReset">
    /// The reset mechanism the <c>reset</c> rung uses. Defaults to
    /// <see cref="DeviceReset.PlatformDefault"/>; dormant unless the policy resets.
    /// </param>
    /// <param name="resetSafetyGate">
    /// Optional predicate consulted before a reset. <see langword="null"/> = always-safe.
    /// </param>
    /// <param name="faultedNodeRecovery">
    /// Opt-in faulted-node recovery (ADR-0060 Decision 11). <see langword="false"/>
    /// (the default) preserves the prior behavior exactly; see the other constructor.
    /// </param>
    protected DeviceProxyBase(
        DeviceTracker tracker,
        IRecoveryPolicy? recoveryPolicy = null,
        IDeviceReset? deviceReset = null,
        IResetSafetyGate? resetSafetyGate = null,
        bool faultedNodeRecovery = false)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        _tracker = tracker;
        _recoveryPolicy = recoveryPolicy ?? ExponentialBackoffRecoveryPolicy.Default;
        _deviceReset = deviceReset ?? DeviceReset.PlatformDefault;
        _resetSafetyGate = resetSafetyGate;
        _faultedNodeRecovery = faultedNodeRecovery;
        _tracker.StateChanged += OnTrackerStateChanged;
    }

    /// <summary>
    /// Checks whether the tracker already has an active device and triggers
    /// activation if so. Call this from <c>Create</c> factories after
    /// construction when the watcher may already be running.
    /// </summary>
    protected void CheckInitialState()
    {
        if (_tracker.IsActive && _tracker.Device is { } device)
            Forget(TryOpenDeviceAsync(device), nameof(TryOpenDeviceAsync));
        else if (_tracker.Device is { } present)
            MaybeStartFaultedNodeRecovery(present);   // already enumerated-but-faulted at construction
    }

    // -------------------------------------------------------------------
    // Public state
    // -------------------------------------------------------------------

    /// <summary>
    /// The observable session-openability state of this handle. Raises
    /// <see cref="PropertyChanged"/> (and <see cref="IsOpen"/> alongside it) on
    /// every transition. <see cref="ConnectionState.GaveUp"/> is the
    /// "enumerated but unopenable" signal a health probe reads.
    /// </summary>
    public ConnectionState State
    {
        get => _state;
        private set
        {
            if (_state == value) return;
            bool wasOpen = _state == ConnectionState.Open;
            _state = value;
            PropertyChanged?.Invoke(this,
                new PropertyChangedEventArgs(nameof(State)));
            if (wasOpen != (value == ConnectionState.Open))
                PropertyChanged?.Invoke(this,
                    new PropertyChangedEventArgs(nameof(IsOpen)));
        }
    }

    /// <summary>
    /// <see langword="true"/> when the platform device is open and ready
    /// for I/O. Becomes <see langword="true"/> only after
    /// <see cref="OnActivatedAsync"/> completes successfully. Equivalent to
    /// <c><see cref="State"/> == <see cref="ConnectionState.Open"/></c>.
    /// </summary>
    public bool IsOpen => _state == ConnectionState.Open;

    /// <summary>
    /// The most recent fault that closed a live session or aborted an open
    /// attempt, or <see langword="null"/> if none has occurred since the last
    /// successful open or re-enumeration.
    /// </summary>
    public Exception? LastOpenFault
    {
        get => _lastOpenFault;
        private set
        {
            if (ReferenceEquals(_lastOpenFault, value)) return;
            _lastOpenFault = value;
            PropertyChanged?.Invoke(this,
                new PropertyChangedEventArgs(nameof(LastOpenFault)));
        }
    }

    private void SetState(ConnectionState state) => State = state;

    /// <summary>
    /// The most recent enumeration snapshot for the tracked device,
    /// or <see langword="null"/> when no matching device is present.
    /// </summary>
    public DeviceInfo? DeviceInfo => _tracker.Device;

    /// <summary>
    /// The open device, or <see langword="null"/> when
    /// <see cref="IsOpen"/> is <see langword="false"/>.
    /// </summary>
    public TDevice? Device => _device;

    // -------------------------------------------------------------------
    // Events
    // -------------------------------------------------------------------

    /// <summary>
    /// Raised after <see cref="OnActivatedAsync"/> completes successfully
    /// and <see cref="IsOpen"/> is <see langword="true"/>. Use for
    /// notification work (UI updates, telemetry) — NOT for init gates.
    /// </summary>
    public event EventHandler<TDevice>? DeviceOpened;

    /// <summary>
    /// Raised when the platform device handle has been closed, either because
    /// the device was disconnected or because this handle is being disposed.
    /// </summary>
    public event EventHandler? DeviceClosed;

    /// <summary>
    /// Raised when <see cref="OpenDeviceAsync"/> throws
    /// <typeparamref name="TException"/>.
    /// </summary>
    public event EventHandler<TException>? OpenFailed;

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    // -------------------------------------------------------------------
    // Hooks (override in derived classes)
    // -------------------------------------------------------------------

    /// <summary>
    /// Opens the platform device. Called inside the open lock.
    /// </summary>
    /// <param name="deviceInfo">The enumeration snapshot identifying the device.</param>
    /// <param name="ct">Per-connection cancellation token.</param>
    /// <returns>The opened device, ready for I/O.</returns>
    protected abstract Task<TDevice> OpenDeviceAsync(
        DeviceInfo deviceInfo, CancellationToken ct);

    /// <summary>
    /// Awaitable init gate. Called inside the open lock, BEFORE
    /// <see cref="IsOpen"/> becomes <see langword="true"/>.
    /// Throw to abort the connection attempt (the device will be disposed).
    /// </summary>
    /// <param name="device">The device returned by <see cref="OpenDeviceAsync"/>.</param>
    /// <param name="ct">Per-connection cancellation token.</param>
    protected virtual Task OnActivatedAsync(
        TDevice device, CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Teardown hook. Called inside the open lock during close, before
    /// the device is disposed.
    /// </summary>
    /// <param name="device">The device being closed.</param>
    protected virtual Task OnDeactivatedAsync(
        TDevice device) => Task.CompletedTask;

    /// <summary>
    /// <see langword="true"/> when <see cref="WhileOpenAsync"/> should be
    /// started after activation. Override in derived classes that provide a
    /// <see cref="WhileOpenAsync"/> implementation.
    /// </summary>
    protected virtual bool HasWorker => false;

    /// <summary>
    /// Supervised background worker. Started outside the open lock after
    /// <see cref="IsOpen"/> becomes <see langword="true"/> and cancelled when
    /// the device disconnects or the handle is disposed.
    /// A non-<see cref="OperationCanceledException"/> causes the device to be
    /// closed and a reconnect to be attempted.
    /// A normal return leaves the device open.
    /// </summary>
    /// <param name="device">The open device.</param>
    /// <param name="ct">Per-connection cancellation token.</param>
    protected virtual Task WhileOpenAsync(
        TDevice device, CancellationToken ct) => Task.CompletedTask;

    // -------------------------------------------------------------------
    // State machine
    // -------------------------------------------------------------------

    private void OnTrackerStateChanged(object? sender, DeviceTrackerState state)
    {
        if (state.IsActive)
        {
            // Re-enumeration is a fresh start: a power-cycled / replugged device
            // gets a clean reconnect budget. Clear a prior give-up and the last
            // fault, and drop back to Disconnected so the attempt counter (which
            // restarts at 0 in ReconnectAsync) gets a full run.
            if (_state == ConnectionState.GaveUp)
            {
                SetState(ConnectionState.Disconnected);
                _lastFault = null;
                LastOpenFault = null;
                _resetCount = 0;                 // re-enumeration is a fresh budget
                // ADR-0060: this clear (a genuine external replug while parked in
                // GaveUp) is kept and is orthogonal to the stable-open dwell. It can
                // only run from GaveUp, where there is no open session and therefore no
                // pending dwell, so the two never double-clear or cancel each other.
            }
            Forget(TryOpenDeviceAsync(state.Device!), nameof(TryOpenDeviceAsync));
        }
        else
        {
            Forget(CloseDeviceAsync(), nameof(CloseDeviceAsync));
            // ADR-0060 Decision 11: a matched-but-not-active device may be a genuinely
            // faulted node that will never reach Active on its own (the field
            // wedge: Status=Error, problem code 21, dead from boot). Engage the reset
            // ladder for it — but ONLY if the consumer opted in and the device is a
            // real fault (not a healthy paired-but-out-of-range present device, and not
            // a user/policy-disabled one). Off by default => unchanged behavior.
            if (state.Device is { } device)
                MaybeStartFaultedNodeRecovery(device);
        }
    }

    // ADR-0060 Decision 11. Kick off faulted-node recovery for an enumerated-but-not-
    // active device, gated three ways: (1) opt-in flag, (2) the pure fault classifier
    // (genuine resettable fault, never a healthy-present or disabled node), (3) the
    // single-driver guard so only one faulted loop runs. The actual reset decision and
    // budget still live in the injected IRecoveryPolicy, consulted inside the loop.
    private void MaybeStartFaultedNodeRecovery(DeviceInfo device)
    {
        if (!_faultedNodeRecovery || _disposed || IsOpen)
            return;

        // Already conceded for this device; a real re-enumeration to Active is the only
        // thing that should revive it (handled by the IsActive branch above).
        if (_state == ConnectionState.GaveUp)
            return;

        // The load-bearing safety check: only a genuine, resettable OS fault. A healthy
        // Present device (problem code 0 — e.g. Bluetooth out of range) or a user/policy
        // disabled one is left strictly alone.
        if (!DeviceFaultClassifier.IsResettableFault(device))
            return;

        if (Interlocked.CompareExchange(ref _faultedRecoveryInProgress, 1, 0) != 0)
            return;   // a faulted loop is already running

        Forget(RunFaultedNodeRecoveryAsync(), nameof(RunFaultedNodeRecoveryAsync));
    }

    // Drives the reset ladder against an enumerated-but-faulted node that never reached
    // Active. Symmetric with ReconnectAsync (the open-failure path), but the device is
    // Present-not-Active, so "retry" cannot re-open a healthy handle — it can only wait
    // for the node to self-clear. The cure is reset: clear the devnode, let it come up
    // Active, and hand off to the normal open path. Shares the reset budget (_resetCount)
    // and the stable-open dwell, so a node that keeps re-faulting converges to GaveUp
    // rather than reset-looping.
    private async Task RunFaultedNodeRecoveryAsync()
    {
        try
        {
            // Settle window: give a freshly-enumerated node a moment to start on its own
            // (drivers can report a transient problem code mid bring-up) before we touch it.
            try { await Task.Delay(FaultedNodeSettleWindow, _disposeCts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            int attempt = 0;
            while (!_disposed && !IsOpen)
            {
                var deviceInfo = _tracker.Device;
                var status = _tracker.ActivityStatus;

                if (deviceInfo is null || status == DeviceActivityStatus.Absent)
                    return;                                   // device left the tree

                if (status == DeviceActivityStatus.Active)
                {
                    // It reached Active (self-healed or our reset cleared it). The normal
                    // open path owns it now; hand off the open-failure ladder explicitly in
                    // case the OS-event-driven open already failed without re-arming.
                    RequestReconnect();
                    return;
                }

                // Present. Stop the moment it is no longer a genuine fault (the snapshot
                // refreshed to healthy / disabled), and stop if we already conceded.
                if (!DeviceFaultClassifier.IsResettableFault(deviceInfo)
                    || _state == ConnectionState.GaveUp)
                    return;

                attempt++;
                var availableResets = _deviceReset.StrategiesFor(deviceInfo);

                // Pure decision (no await, no ct): same context -> same directive. The
                // EnumeratedFault trigger lets a policy tell this cause from an open-failure.
                var faultedContext = new RecoveryContext(
                    attempt, _resetCount, _lastFault, deviceInfo, availableResets,
                    RecoveryTrigger.EnumeratedFault);
                var directive = _recoveryPolicy.Decide(faultedContext);

                switch (directive)
                {
                    case RecoveryDirective.GiveUp:
                        _logger.LogWarning(
                            "[{Device}] faulted-node recovery gave up after {Attempt} attempt(s) and {ResetCount} reset(s); parking in GaveUp until re-enumeration. Status={Status}, problem={Problem}.",
                            Label, attempt, _resetCount, deviceInfo.Status,
                            DeviceFaultClassifier.ReadProblemCode(deviceInfo)?.ToString() ?? "(n/a)");
                        SetState(ConnectionState.GaveUp);
                        return;

                    case RecoveryDirective.Retry retry:
                        // A faulted node has no healthy handle to re-open — "retry" here
                        // means wait, then re-check whether it cleared to Active on its own.
                        SetState(ConnectionState.Connecting);
                        try { await Task.Delay(retry.Delay, _disposeCts.Token).ConfigureAwait(false); }
                        catch (OperationCanceledException) { return; }
                        break;

                    case RecoveryDirective.Reset reset:
                        // Same reset rung + budget as the open-failure path. The pure
                        // escalation step validates the chosen strategy as a value; on a
                        // successful reset the device went Active and reopened, otherwise the
                        // loop re-decides (reset again or, once the budget is spent, give up).
                        if (ResetEscalation.Decide(faultedContext, reset) is EscalationDecision.ExecuteDecision exec
                            && await ExecuteResetAsync(deviceInfo, exec.Strategy).ConfigureAwait(false))
                            return;
                        break;
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _faultedRecoveryInProgress, 0);
        }
    }

    private async Task<bool> TryOpenDeviceAsync(
        DeviceInfo deviceInfo,
        bool requestReconnectOnFailure = true)
    {
        bool aborted = false;
        bool connected = false;
        bool cancelled = false;
        bool shouldReconnect = false;
        TDevice? opened = null;
        CancellationToken workerCt = default;

        await _openLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed || IsOpen) return IsOpen;

            ResetConnectionState();

            var cts = CancellationTokenSource.CreateLinkedTokenSource(
                _disposeCts.Token);
            _connectionCts = cts;

            try
            {
                // Handle cancellation before typed open-failure handling
                if (cts.IsCancellationRequested)
                {
                    ResetConnectionState();
                    cancelled = true;
                    aborted = true;
                }

                if (!aborted)
                {
                    opened = await OpenDeviceAsync(deviceInfo, cts.Token)
                        .ConfigureAwait(false);
                }
            }
            catch (TException ex)
            {
                _lastFault = ex;
                LastOpenFault = ex;
                TryNotify(() => OpenFailed?.Invoke(this, ex));
                ResetConnectionState();
                aborted = true;
                shouldReconnect = requestReconnectOnFailure && !_disposed;
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                ResetConnectionState();
                cancelled = true;
                aborted = true;
            }
            catch (Exception ex)
            {
                _lastFault = ex;
                LastOpenFault = ex;
                ResetConnectionState();
                aborted = true;
                shouldReconnect = requestReconnectOnFailure && !_disposed;
            }

            if (!aborted && opened is not null)
            {
                try
                {
                    await OnActivatedAsync(opened, cts.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested)
                {
                    await opened.DisposeAsync().ConfigureAwait(false);
                    ResetConnectionState();
                    cancelled = true;
                    aborted = true;
                }
                catch (Exception ex)
                {
                    _lastFault = ex;
                    LastOpenFault = ex;
                    await opened.DisposeAsync().ConfigureAwait(false);
                    ResetConnectionState();
                    aborted = true;
                    shouldReconnect = requestReconnectOnFailure && !_disposed;
                }
            }

            if (!aborted && !cancelled && _connectionCts is not null && opened is not null)
            {
                // The proxy can have been disposed while OpenDeviceAsync / OnActivatedAsync
                // were in flight. Both are handed the connection token — linked to
                // _disposeCts, which DisposeAsync cancels before its own close queues behind
                // this lock — but honouring it is a derived hook's choice, and a hook that
                // ignores it hands back a live device into a torn-down proxy.
                //
                // Taking _lifecycleGate makes this decision atomic with DisposeAsync's own
                // "am I the one tearing down" transition, so the handle has exactly one
                // owner: either it becomes _device and dispose's close (parked on _openLock,
                // so it cannot have run yet) will close it, or disposal won the race and we
                // close it here — dispose's close would find _device null and leave it.
                bool publish;
                lock (_lifecycleGate)
                {
                    publish = !_disposed;
                    if (publish)
                        _device = opened;
                }

                if (!publish)
                {
                    await opened.DisposeAsync().ConfigureAwait(false);
                    ResetConnectionState();
                    return false;
                }

                // ADR-0060: clearing the reset budget is DEFERRED to the stable-open
                // dwell — NOT done here. SetState(Open) stays immediate so health /
                // openability reporting is correct at once (consumers map Open ->
                // Healthy right away); only the budget / last-fault clear waits until
                // the session has proven it can survive past a fast post-open refault.
                SetState(ConnectionState.Open);
                _logger.LogDebug("[{Device}] session open; reset budget clears after {DwellS}s dwell.",
                    Label, (int)StableOpenDwell.TotalSeconds);
                TryNotify(() => DeviceOpened?.Invoke(this, opened));
                connected = true;
                // Launch the dwell INSIDE the open lock: its synchronous prefix (the
                // Task.Delay registration on the connection token) then runs while the
                // CTS is guaranteed alive, closing the launch-vs-dispose race. It yields
                // at the first await, so it does not hold up the lock.
                int generation = ++_connectionGeneration;
                Forget(RunStableOpenDwellAsync(_connectionCts.Token, generation), nameof(RunStableOpenDwellAsync));
                if (HasWorker)
                    workerCt = _connectionCts.Token;
            }
        }
        finally
        {
            _openLock.Release();
        }

        // Starting the worker is the last thing the open path does and it happens outside
        // the open lock, so disposal can have begun since publication. workerCt is already
        // cancelled in that case — DisposeAsync cancels _disposeCts, and the connection CTS
        // is linked to it — but honouring a token is WhileOpenAsync's choice, so don't hand
        // it the device at all. Read the flag through the gate for a fresh acquire.
        //
        // Scope, precisely: this covers the DISPOSAL half only. An ordinary tracker-driven
        // CloseDeviceAsync never sets _disposed, so it can acquire the lock the moment it is
        // released above, null _device and dispose the handle, and this check will still
        // start the worker on a closed device. That window is pre-existing and lands in the
        // same place as the disposal one — workerCt is cancelled first either way (close
        // cancels _connectionCts before taking the lock), so only a hook that ignores its
        // token notices. Closing it symmetrically wants _connectionGeneration, which already
        // answers "is this still my connection?" for the stable-open dwell, but it is
        // declared inside the publication block and would need hoisting to be in scope here.
        // Best-effort narrowing, not atomicity: the base class can decline to START a hook,
        // never make a running one stop.
        bool startWorker;
        lock (_lifecycleGate)
            startWorker = connected && HasWorker && !_disposed;

        if (startWorker)
            Forget(RunWorkerAsync(opened!, workerCt), nameof(RunWorkerAsync));

        if (!connected && shouldReconnect)
            RequestReconnect();

        return connected;
    }

    private async Task RunWorkerAsync(TDevice device, CancellationToken ct)
    {
        try
        {
            await WhileOpenAsync(device, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Device disconnected or handle disposed — clean exit.
        }
        catch (Exception ex)
        {
            // Non-CT exception — record the fault, close the device, and
            // schedule a reconnect. The fault feeds the next RecoveryContext.
            _lastFault = ex;
            LastOpenFault = ex;
            await CloseDeviceAsync().ConfigureAwait(false);
            RequestReconnect();
        }
    }

    // ADR-0060 stable-open dwell. A successful open defers clearing the reset budget
    // to this timer instead of zeroing it the instant the open returns. If the session
    // faults, closes, is reset, or the proxy is disposed BEFORE the dwell elapses, the
    // connection token is cancelled, the delay throws, and the budget is preserved so
    // the recovery ladder keeps escalating toward GaveUp. Only a session that actually
    // survives the dwell clears the budget — and only if it is still the live
    // generation, so a stale timer can never zero a newer connection's budget.
    private async Task RunStableOpenDwellAsync(CancellationToken connectionToken, int generation)
    {
        try
        {
            await Task.Delay(StableOpenDwell, connectionToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;   // session ended (close / fault / reset / dispose) before the dwell — keep the budget
        }

        // The dwell elapsed with the session still notionally live. Take the open lock
        // (cancellable on the same token, so a concurrent close/dispose still aborts the
        // wait) and re-check that THIS is the current, open connection before clearing.
        try { await _openLock.WaitAsync(connectionToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        catch (ObjectDisposedException) { return; }   // proxy disposed out from under us

        try
        {
            if (_disposed || generation != _connectionGeneration || !IsOpen)
                return;   // superseded or torn down — not our budget to clear

            _resetCount = 0;
            _lastFault = null;
            LastOpenFault = null;
            _logger.LogDebug("[{Device}] stable-open dwell ({DwellS}s) elapsed; reset budget cleared.",
                Label, (int)StableOpenDwell.TotalSeconds);
        }
        finally
        {
            _openLock.Release();
        }
    }

    private void RequestReconnect()
    {
        if (_disposed) return;

        if (Interlocked.CompareExchange(ref _reconnectInProgress, 1, 0) != 0)
            return;

        Forget(ReconnectAsync(), nameof(ReconnectAsync));
    }

    private async Task ReconnectAsync()
    {
        try
        {
            int attempt = 0;

            while (!_disposed && !IsOpen && _tracker.IsActive)
            {
                var deviceInfo = _tracker.Device;
                if (deviceInfo is null) return;
                attempt++;

                var availableResets = _deviceReset.StrategiesFor(deviceInfo);

                // Pure decision (no await, no ct): same context -> same directive.
                var directive = _recoveryPolicy.Decide(
                    new RecoveryContext(attempt, _resetCount, _lastFault, deviceInfo, availableResets));

                switch (directive)
                {
                    case RecoveryDirective.GiveUp:
                        _logger.LogWarning(
                            "[{Device}] recovery gave up after {Attempt} attempt(s) and {ResetCount} reset(s); parking in GaveUp until re-enumeration. Last fault: {Fault}",
                            Label, attempt, _resetCount, _lastFault?.Message ?? "(none)");
                        SetState(ConnectionState.GaveUp);    // wait for re-enumeration
                        return;

                    case RecoveryDirective.Retry retry:
                        SetState(ConnectionState.Connecting);
                        try { await Task.Delay(retry.Delay, _disposeCts.Token).ConfigureAwait(false); }
                        catch (OperationCanceledException) { return; }

                        if (_disposed || IsOpen || !_tracker.IsActive) return;
                        if (await TryOpenDeviceAsync(deviceInfo, requestReconnectOnFailure: false).ConfigureAwait(false))
                            return;                          // success -> TryOpen set State = Open
                        break;

                    case RecoveryDirective.Reset reset:
                        // Pure escalation step decides admissibility as a value; the shell
                        // then owns the gate consult + reset IO + self-reopen poll.
                        if (ResetEscalation.Decide(
                                new RecoveryContext(attempt, _resetCount, _lastFault, deviceInfo, availableResets),
                                reset) is EscalationDecision.ExecuteDecision exec)
                        {
                            if (await ExecuteResetAsync(deviceInfo, exec.Strategy).ConfigureAwait(false))
                                return;                      // reset + reopen succeeded
                        }
                        break;                               // concede / deferred / timed out -> loop re-decides
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _reconnectInProgress, 0);

            // Re-arm unless we deliberately gave up — and not while a reset is still
            // in flight (ExecuteResetAsync owns the loop until it returns).
            if (!_disposed && !IsOpen && _tracker.IsActive
                && _state != ConnectionState.GaveUp && _state != ConnectionState.Resetting)
                RequestReconnect();
        }
    }

    // The effectful half of the reset rung (ADR-0060 Decisions 4, 6, 9). The pure
    // ResetEscalation.Decide step already validated that `strategy` is admissible
    // (it is one the device advertises); this method owns ONLY the IO and timing:
    // consult the safety gate (a genuine async port across a boundary), perform the
    // reset via the injected mechanism, then SELF-DRIVE the re-open with a
    // shell-owned (Environment.TickCount64) timeout backstop. The proxy knows it
    // just reset, so it does not wait on the watcher; a re-enumerating strategy may
    // also wake via OnTrackerStateChanged, and the open lock dedups whoever opens first.
    private async Task<bool> ExecuteResetAsync(DeviceInfo deviceInfo, ResetStrategy strategy)
    {
        // Safety gate: defer if the consumer says now is unsafe.
        if (_resetSafetyGate is not null)
        {
            bool safe;
            try { safe = await _resetSafetyGate.CanResetAsync(deviceInfo, _disposeCts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return false; }
            catch { safe = false; }

            if (!safe)
            {
                _logger.LogDebug("[{Device}] reset deferred by safety gate; backing off {DelayMs}ms.",
                    Label, (int)ResetDeferDelay.TotalMilliseconds);
                SetState(ConnectionState.Connecting);
                try { await Task.Delay(ResetDeferDelay, _disposeCts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return false; }
                return false;                            // loop re-decides (likely reset again, still gated)
            }
        }

        SetState(ConnectionState.Resetting);
        _resetCount++;
        _logger.LogInformation(
            "[{Device}] reset #{ResetCount} via {Strategy} (re-enumerates={ReEnumerates}); driving self-reopen.",
            Label, _resetCount, strategy.Kind, strategy.ReEnumerates);

        try
        {
            var outcome = await _deviceReset.ResetAsync(deviceInfo, strategy, _disposeCts.Token).ConfigureAwait(false);
            _logger.LogInformation("[{Device}] reset {Strategy} -> {Outcome}.", Label, strategy.Kind, outcome);
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            _lastFault = ex;                             // record but still try to reopen
            _logger.LogWarning(ex, "[{Device}] reset {Strategy} threw; attempting reopen anyway.", Label, strategy.Kind);
        }

        // Self-driven reopen, bounded by ResetReopenTimeout.
        long deadline = Environment.TickCount64 + (long)ResetReopenTimeout.TotalMilliseconds;
        while (!_disposed && !IsOpen && Environment.TickCount64 < deadline)
        {
            try { await Task.Delay(ResetReopenPollInterval, _disposeCts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return false; }

            if (_disposed || IsOpen) return IsOpen;
            if (!_tracker.IsActive) continue;            // mid-drop on a re-enumerating reset; keep waiting

            var di = _tracker.Device ?? deviceInfo;
            if (await TryOpenDeviceAsync(di, requestReconnectOnFailure: false).ConfigureAwait(false))
            {
                _logger.LogInformation("[{Device}] reopened after reset.", Label);
                return true;
            }
        }

        if (!IsOpen)
            _logger.LogWarning(
                "[{Device}] reset reopen did not complete within {TimeoutS}s; re-running recovery.",
                Label, (int)ResetReopenTimeout.TotalSeconds);
        return IsOpen;                                   // backstop: outer loop re-decides (reset again / give up)
    }

    /// <summary>
    /// Funnel a consumer-observed fault into the recovery lifecycle: record it as
    /// the <see cref="LastOpenFault"/>, close the current session, and run the
    /// recovery seam (which may retry, reset, or give up per the injected
    /// <see cref="IRecoveryPolicy"/>) — the same path a supervised-worker fault
    /// takes (ADR-0060 Decision 5). For consumers doing direct I/O on
    /// <see cref="Device"/>: call this from the catch block instead of opening a
    /// parallel reset path. No-op after disposal.
    /// </summary>
    /// <param name="fault">The observed fault driving recovery.</param>
    public void Recover(Exception fault)
    {
        ArgumentNullException.ThrowIfNull(fault);
        if (_disposed) return;
        _logger.LogInformation(fault,
            "[{Device}] Recover() funneling a consumer-observed fault into the recovery lifecycle.", Label);
        Forget(RecoverAsync(fault), nameof(RecoverAsync));
    }

    private async Task RecoverAsync(Exception fault)
    {
        _lastFault = fault;
        LastOpenFault = fault;
        await CloseDeviceAsync().ConfigureAwait(false);
        RequestReconnect();
    }

    private void ResetConnectionState()
    {
        _connectionCts?.Dispose();
        _connectionCts = null;
    }

    private static void TryNotify(Action notify)
    {
        try
        {
            notify();
        }
        catch
        {
        }
    }

    // Fire-and-forget WITH the fault observed (#259). A plain discard ("_ = Foo()")
    // leaves a faulting task unobserved, so the exception resurfaces from the
    // finalizer thread as an AggregateException — which on any host that hooks
    // TaskScheduler.UnobservedTaskException (an unattended host's flight recorder,
    // for one) is indistinguishable from a process crash. Every detached call in
    // this class routes through here so a fault costs a log line, not a crash
    // report. Teardown races are expected and logged at Debug; anything else is a
    // Warning, because it is a real defect that would otherwise be silent.
    private void Forget(Task task, string origin)
    {
        if (task.IsCompletedSuccessfully)
            return;

        _ = ObserveAsync(task, origin);
    }

    private async Task ObserveAsync(Task task, string origin)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Nothing upstream can observe this task, so the log call is the last
            // line of defence: a throwing ILogger must not become the very
            // unobserved-task fault this helper exists to prevent.
            try
            {
                if (ex is OperationCanceledException or ObjectDisposedException)
                    _logger.LogDebug(ex, "[{Device}] detached {Origin} ended on teardown.", Label, origin);
                else
                    _logger.LogWarning(ex, "[{Device}] detached {Origin} faulted.", Label, origin);
            }
            catch
            {
            }
        }
    }

    private async Task CloseDeviceAsync()
    {
        // Cancel in-flight init work BEFORE acquiring the lock.
        _connectionCts?.Cancel();

        await _openLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_device is null)
            {
                ResetConnectionState();
                return;
            }

            var closing = _device;
            _device = null;
            SetState(ConnectionState.Disconnected);
            _logger.LogDebug("[{Device}] session closed.", Label);
            TryNotify(() => DeviceClosed?.Invoke(this, EventArgs.Empty));

            try
            {
                await OnDeactivatedAsync(closing).ConfigureAwait(false);
            }
            catch
            {
            }

            await closing.DisposeAsync().ConfigureAwait(false);

            ResetConnectionState();
        }
        finally
        {
            _openLock.Release();
        }
    }

    // -------------------------------------------------------------------
    // Disposal
    // -------------------------------------------------------------------

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        // Atomic with the open path's publication decision, and idempotent: exactly one
        // caller performs the teardown, and once this returns no open in flight can
        // publish a device into the proxy.
        lock (_lifecycleGate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        _disposeCts.Cancel();

        _tracker.StateChanged -= OnTrackerStateChanged;
        await CloseDeviceAsync().ConfigureAwait(false);

        if (_ownedWatcher is not null)
            await _ownedWatcher.DisposeAsync().ConfigureAwait(false);

        // Deliberately NOT disposing _openLock or _disposeCts (#259).
        //
        // Detached work can still be in flight right here: "_tracker.StateChanged -="
        // above does not retract a handler already running, and every loop in this
        // class *checks* _disposed rather than awaiting quiescence. That race is
        // benign — the detached task finds _disposed set and unwinds — right up until
        // one of these two fields is disposed underneath it, at which point an
        // ordinary teardown becomes an ObjectDisposedException out of
        // SemaphoreSlim.WaitAsync/Release or CancellationTokenSource.Token, thrown
        // inside a task no caller can catch.
        //
        // Neither dispose buys anything in exchange. SemaphoreSlim.Dispose is only
        // required once AvailableWaitHandle has been touched, which nothing here does;
        // _disposeCts is a plain, already-cancelled source with no timer, and the
        // linked children created from it are disposed by ResetConnectionState. Both
        // are plain collectable objects, so dropping the Dispose calls removes the
        // sharp edge outright instead of narrowing the window it fires in.
    }
}
