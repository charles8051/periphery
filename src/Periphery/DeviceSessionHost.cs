// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace Periphery;

/// <summary>
/// Reconnect-resilient host that creates, publishes, and withdraws a
/// session-scoped object each time the tracked device becomes active.
/// </summary>
/// <typeparam name="TSession">The published session type.</typeparam>
public sealed class DeviceSessionHost<TSession>
    : INotifyPropertyChanged, IAsyncDisposable
    where TSession : class
{
    private readonly DeviceTracker _tracker;
    private readonly DeviceWatcher? _ownedWatcher;
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly object _statusLock = new();

    private DeviceProxy<SessionLease<TSession>>? _handle;
    private volatile HostStatus<TSession> _status;
    private Exception? _lastError;
    private int _attempt;
    private bool _disposed;

    private DeviceSessionHost(DeviceTracker tracker, DeviceWatcher? ownedWatcher)
    {
        ArgumentNullException.ThrowIfNull(tracker);

        _tracker = tracker;
        _ownedWatcher = ownedWatcher;
        _tracker.StateChanged += OnTrackerStateChanged;
        _status = GetInitialStatus();
    }

    /// <summary>
    /// Creates a self-contained session host that owns its own watcher.
    /// </summary>
    /// <param name="profile">The device profile describing which device to track.</param>
    /// <param name="createSession">
    /// Factory delegate invoked each time the tracked device becomes active.
    /// </param>
    /// <param name="onSessionEnded">
    /// Optional teardown delegate invoked when a session ends.
    /// </param>
    /// <param name="whileSessionActive">
    /// Optional supervised background worker for the active session.
    /// </param>
    /// <param name="recoveryPolicy">
    /// Device recovery policy (retry / reset / give-up) forwarded to the underlying device
    /// handle; defaults to <see cref="ExponentialBackoffRecoveryPolicy.Default"/>
    /// (retry forever, no reset) when <see langword="null"/>. Pass a bounded / reset-aware
    /// policy to surface a terminal <see cref="SessionGaveUp{TSession}"/> /
    /// <see cref="ConnectionState.GaveUp"/> after the device stays unopenable.
    /// </param>
    /// <param name="deviceReset">
    /// Optional reset capability (ADR-0060) forwarded to the device handle.
    /// <see langword="null"/> ⇒ no resets.
    /// </param>
    /// <param name="resetSafetyGate">
    /// Optional gate consulted before each reset. <see langword="null"/> ⇒ always safe.
    /// </param>
    /// <param name="faultedNodeRecovery">
    /// Opt-in faulted-node recovery (ADR-0060 Decision 11) forwarded to the device
    /// handle. <see langword="false"/> (the default) preserves prior behavior.
    /// </param>
    /// <param name="ct">Cancellation token for the initial watcher start.</param>
    public static async Task<DeviceSessionHost<TSession>> StartAsync(
        DeviceProfile profile,
        Func<DeviceInfo, CancellationToken, Task<TSession>> createSession,
        Func<TSession, Task>? onSessionEnded = null,
        Func<TSession, CancellationToken, Task>? whileSessionActive = null,
        IRecoveryPolicy? recoveryPolicy = null,
        IDeviceReset? deviceReset = null,
        IResetSafetyGate? resetSafetyGate = null,
        bool faultedNodeRecovery = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(createSession);

        var tracker = new DeviceTracker(profile.Name, profile);
        var watcher = Devices.Watch().AddTracker(tracker);
        var host = new DeviceSessionHost<TSession>(tracker, watcher);

        try
        {
            var handle = DeviceProxy<SessionLease<TSession>>.Create(
                tracker,
                openDevice: (device, token) => host.CreateLeaseAsync(
                    device,
                    createSession,
                    onSessionEnded,
                    token),
                whileOpen: whileSessionActive is not null
                    ? (lease, ct2) => whileSessionActive(lease.Session, ct2)
                    : null,
                recoveryPolicy: recoveryPolicy,
                deviceReset: deviceReset,
                resetSafetyGate: resetSafetyGate,
                faultedNodeRecovery: faultedNodeRecovery);

            host.AttachHandle(handle);
            await watcher.StartAsync(ct).ConfigureAwait(false);
            host.RefreshStateFromHandle();
            return host;
        }
        catch
        {
            await host.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Convenience overload that pins the host to one specific
    /// <see cref="DeviceInfo"/>. Builds an ID-based <see cref="DeviceProfile"/>
    /// internally via <see cref="DeviceProfile.ForDevice"/> and forwards to
    /// <see cref="StartAsync"/>. Use when you have a device in hand (e.g.
    /// from a UI picker) and want to follow that exact instance through
    /// disconnect/reconnect cycles.
    /// </summary>
    /// <param name="device">The specific device to pin the host to.</param>
    /// <param name="createSession">
    /// Factory delegate invoked each time the tracked device becomes active.
    /// </param>
    /// <param name="onSessionEnded">
    /// Optional teardown delegate invoked when a session ends.
    /// </param>
    /// <param name="whileSessionActive">
    /// Optional supervised background worker for the active session.
    /// </param>
    /// <param name="recoveryPolicy">
    /// Device recovery policy (retry / reset / give-up) forwarded to the underlying device
    /// handle; defaults to <see cref="ExponentialBackoffRecoveryPolicy.Default"/>
    /// (retry forever, no reset) when <see langword="null"/>.
    /// </param>
    /// <param name="deviceReset">
    /// Optional reset capability (ADR-0060) forwarded to the device handle.
    /// <see langword="null"/> ⇒ no resets.
    /// </param>
    /// <param name="resetSafetyGate">
    /// Optional gate consulted before each reset. <see langword="null"/> ⇒ always safe.
    /// </param>
    /// <param name="faultedNodeRecovery">
    /// Opt-in faulted-node recovery (ADR-0060 Decision 11) forwarded to the device
    /// handle. <see langword="false"/> (the default) preserves prior behavior.
    /// </param>
    /// <param name="ct">Cancellation token for the initial watcher start.</param>
    public static Task<DeviceSessionHost<TSession>> ForDeviceAsync(
        DeviceInfo device,
        Func<DeviceInfo, CancellationToken, Task<TSession>> createSession,
        Func<TSession, Task>? onSessionEnded = null,
        Func<TSession, CancellationToken, Task>? whileSessionActive = null,
        IRecoveryPolicy? recoveryPolicy = null,
        IDeviceReset? deviceReset = null,
        IResetSafetyGate? resetSafetyGate = null,
        bool faultedNodeRecovery = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        return StartAsync(
            DeviceProfile.ForDevice(device),
            createSession,
            onSessionEnded,
            whileSessionActive,
            recoveryPolicy,
            deviceReset,
            resetSafetyGate,
            faultedNodeRecovery,
            ct);
    }

    /// <summary>
    /// Creates a session host that borrows an existing tracker attached to a
    /// caller-owned watcher.
    /// </summary>
    /// <param name="tracker">
    /// An existing tracker, already attached to a running watcher.
    /// </param>
    /// <param name="createSession">
    /// Factory delegate invoked each time the tracked device becomes active.
    /// </param>
    /// <param name="onSessionEnded">
    /// Optional teardown delegate invoked when a session ends.
    /// </param>
    /// <param name="whileSessionActive">
    /// Optional supervised background worker for the active session.
    /// </param>
    /// <param name="recoveryPolicy">
    /// Device recovery policy (retry / reset / give-up) forwarded to the underlying device
    /// handle; defaults to <see cref="ExponentialBackoffRecoveryPolicy.Default"/>
    /// (retry forever, no reset) when <see langword="null"/>. Pass a bounded / reset-aware
    /// policy to surface a terminal <see cref="SessionGaveUp{TSession}"/> /
    /// <see cref="ConnectionState.GaveUp"/> after the device stays unopenable.
    /// </param>
    /// <param name="deviceReset">
    /// Optional reset capability (ADR-0060) forwarded to the device handle.
    /// <see langword="null"/> ⇒ no resets.
    /// </param>
    /// <param name="resetSafetyGate">
    /// Optional gate consulted before each reset. <see langword="null"/> ⇒ always safe.
    /// </param>
    /// <param name="faultedNodeRecovery">
    /// Opt-in faulted-node recovery (ADR-0060 Decision 11) forwarded to the device
    /// handle. <see langword="false"/> (the default) preserves prior behavior.
    /// </param>
    public static DeviceSessionHost<TSession> Create(
        DeviceTracker tracker,
        Func<DeviceInfo, CancellationToken, Task<TSession>> createSession,
        Func<TSession, Task>? onSessionEnded = null,
        Func<TSession, CancellationToken, Task>? whileSessionActive = null,
        IRecoveryPolicy? recoveryPolicy = null,
        IDeviceReset? deviceReset = null,
        IResetSafetyGate? resetSafetyGate = null,
        bool faultedNodeRecovery = false)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(createSession);

        var host = new DeviceSessionHost<TSession>(tracker, ownedWatcher: null);
        var handle = DeviceProxy<SessionLease<TSession>>.Create(
            tracker,
            openDevice: (device, token) => host.CreateLeaseAsync(
                device,
                createSession,
                onSessionEnded,
                token),
            whileOpen: whileSessionActive is not null
                ? (lease, ct) => whileSessionActive(lease.Session, ct)
                : null,
            recoveryPolicy: recoveryPolicy,
            deviceReset: deviceReset,
            resetSafetyGate: resetSafetyGate,
            faultedNodeRecovery: faultedNodeRecovery);

        host.AttachHandle(handle);
        host.RefreshStateFromHandle();
        return host;
    }

    /// <summary>
    /// The current observable host status.
    /// </summary>
    public HostStatus<TSession> Status => _status;

    /// <summary>
    /// <see langword="true"/> when a session is active.
    /// </summary>
    public bool HasSession => _status is SessionActive<TSession>;

    /// <summary>
    /// The session-openability state of the underlying device handle. This
    /// surfaces the inner <see cref="DeviceProxy{TDevice}"/>'s
    /// <see cref="DeviceProxyBase{TDevice,TException}.State"/> outward so the
    /// session-host cohort can feed the same health evaluator as the
    /// <see cref="DeviceProxy{TDevice}"/>-direct cohort (Open → Healthy,
    /// <see cref="ConnectionState.GaveUp"/> → Unhealthy, etc.). Reports
    /// <see cref="ConnectionState.Disconnected"/> before the handle is attached.
    /// </summary>
    public ConnectionState ConnectionState =>
        _handle?.State ?? ConnectionState.Disconnected;

    /// <summary>
    /// The most recent device snapshot, or <see langword="null"/> if no
    /// matching device is known.
    /// </summary>
    public DeviceInfo? DeviceInfo => _tracker.Device;

    /// <summary>
    /// The current session, or <see langword="null"/> when no session is active.
    /// </summary>
    public TSession? CurrentSession => _status is SessionActive<TSession> active
        ? active.Session
        : null;

    /// <summary>
    /// A human-readable, UI-friendly description of <see cref="Status"/>.
    /// Suitable for direct binding to a status bar; updates automatically
    /// whenever <see cref="Status"/> transitions. Consumers that need
    /// custom wording can switch on <see cref="Status"/> themselves.
    /// </summary>
    public string StatusDescription => Status switch
    {
        DeviceAbsent<TSession> =>
            "Waiting for device.",
        SessionStarting<TSession> s =>
            $"Connecting to {s.Device.Name ?? "(unnamed)"}…",
        SessionActive<TSession> a =>
            $"Live — {a.Device.Name ?? "(unnamed)"}.",
        SessionUnavailable<TSession> u when u.LastError is null =>
            $"Unavailable (attempt {u.Attempt}).",
        SessionUnavailable<TSession> u =>
            $"Unavailable (attempt {u.Attempt}) — {u.LastError!.GetType().Name}: {u.LastError.Message}",
        SessionGaveUp<TSession> g when g.LastError is null =>
            $"Gave up after {g.Attempt} attempt(s); device present but unopenable.",
        SessionGaveUp<TSession> g =>
            $"Gave up after {g.Attempt} attempt(s) — {g.LastError!.GetType().Name}: {g.LastError.Message}",
        _ => Status.GetType().Name,
    };

    /// <summary>
    /// Raised whenever <see cref="Status"/> changes.
    /// </summary>
    public event EventHandler<HostStatus<TSession>>? StatusChanged;

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Gets the current session or throws if none is active.
    /// </summary>
    public TSession GetRequiredSession()
    {
        if (TryGetCurrentSession(out var session))
            return session;

        throw new InvalidOperationException(
            $"No session available for '{_tracker.Name ?? "device"}' ({DescribeStatus(_status)}).");
    }

    /// <summary>
    /// Attempts to get the current session.
    /// </summary>
    public bool TryGetCurrentSession([NotNullWhen(true)] out TSession? session)
    {
        if (_status is SessionActive<TSession> active)
        {
            session = active.Session;
            return true;
        }

        session = null;
        return false;
    }

    /// <summary>
    /// Waits until a session becomes active or the supplied token is cancelled.
    /// </summary>
    public Task<TSession> WaitForSessionAsync(CancellationToken ct = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(DeviceSessionHost<TSession>));

        if (TryGetCurrentSession(out var session))
            return Task.FromResult(session);

        var tcs = new TaskCompletionSource<TSession>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        EventHandler<HostStatus<TSession>>? handler = null;
        CancellationTokenRegistration cancellationRegistration = default;
        CancellationTokenRegistration disposeRegistration = default;

        void Cleanup()
        {
            StatusChanged -= handler;
            cancellationRegistration.Dispose();
            disposeRegistration.Dispose();
        }

        handler = (_, status) =>
        {
            if (status is SessionActive<TSession> active)
            {
                Cleanup();
                tcs.TrySetResult(active.Session);
            }
        };

        StatusChanged += handler;

        if (TryGetCurrentSession(out session))
        {
            Cleanup();
            return Task.FromResult(session);
        }

        if (ct.CanBeCanceled)
        {
            cancellationRegistration = ct.Register(static state =>
            {
                var tuple = ((TaskCompletionSource<TSession> Tcs, Action Cleanup))state!;
                tuple.Cleanup();
                tuple.Tcs.TrySetCanceled();
            }, (tcs, (Action)Cleanup));
        }

        disposeRegistration = _disposeCts.Token.Register(static state =>
        {
            var tuple = ((TaskCompletionSource<TSession> Tcs, Action Cleanup))state!;
            tuple.Cleanup();
            tuple.Tcs.TrySetException(
                new ObjectDisposedException(nameof(DeviceSessionHost<TSession>)));
        }, (tcs, (Action)Cleanup));

        return tcs.Task;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        _disposeCts.Cancel();
        _tracker.StateChanged -= OnTrackerStateChanged;

        if (_handle is not null)
        {
            _handle.DeviceOpened -= OnDeviceOpened;
            _handle.DeviceClosed -= OnDeviceClosed;
            _handle.PropertyChanged -= OnHandlePropertyChanged;
            await _handle.DisposeAsync().ConfigureAwait(false);
        }

        if (_ownedWatcher is not null)
            await _ownedWatcher.DisposeAsync().ConfigureAwait(false);

        _disposeCts.Dispose();
    }

    private HostStatus<TSession> GetInitialStatus()
    {
        if (_tracker.IsActive && _tracker.Device is { } device)
            return new SessionStarting<TSession>(device);

        return new DeviceAbsent<TSession>();
    }

    private void AttachHandle(DeviceProxy<SessionLease<TSession>> handle)
    {
        _handle = handle;
        handle.DeviceOpened += OnDeviceOpened;
        handle.DeviceClosed += OnDeviceClosed;
        // The give-up transition is driven by the inner proxy's reconnect loop
        // (it sets State = GaveUp from a background task), not by any
        // open/close/tracker edge the host already observes. Subscribe to the
        // proxy's PropertyChanged so a terminal GaveUp is mapped outward to
        // SessionGaveUp and the host's ConnectionState re-raises.
        handle.PropertyChanged += OnHandlePropertyChanged;
    }

    private void OnHandlePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DeviceProxy<SessionLease<TSession>>.State))
            return;

        // Forward the underlying handle's openability state outward.
        RaisePropertyChanged(nameof(ConnectionState));

        if (_handle?.State == Periphery.ConnectionState.GaveUp)
            SetStatus(new SessionGaveUp<TSession>(_lastError, _attempt));
    }

    private void RefreshStateFromHandle()
    {
        var handle = _handle;
        if (handle is null)
            return;

        if (handle.IsOpen && handle.Device is { } lease && _tracker.Device is { } device)
        {
            _attempt = 0;
            _lastError = null;
            SetStatus(new SessionActive<TSession>(lease.Session, device));
        }
        else if (handle.State == Periphery.ConnectionState.GaveUp)
        {
            SetStatus(new SessionGaveUp<TSession>(_lastError, _attempt));
        }
        else if (_tracker.IsActive && _tracker.Device is { } activeDevice)
        {
            SetStatus(new SessionStarting<TSession>(activeDevice));
        }
        else
        {
            SetStatus(new DeviceAbsent<TSession>());
        }
    }

    private async Task<SessionLease<TSession>> CreateLeaseAsync(
        DeviceInfo deviceInfo,
        Func<DeviceInfo, CancellationToken, Task<TSession>> createSession,
        Func<TSession, Task>? onSessionEnded,
        CancellationToken ct)
    {
        SetStatus(new SessionStarting<TSession>(deviceInfo));

        try
        {
            var session = await createSession(deviceInfo, ct).ConfigureAwait(false);
            return new SessionLease<TSession>(deviceInfo, session, onSessionEnded);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _attempt++;
            _lastError = ex;
            SetStatus(new SessionUnavailable<TSession>(deviceInfo, ex, _attempt));
            throw;
        }
    }

    private void OnTrackerStateChanged(object? sender, DeviceTrackerState state)
    {
        if (!state.IsActive)
        {
            _attempt = 0;
            _lastError = null;
            SetStatus(new DeviceAbsent<TSession>());
            return;
        }

        if (_handle?.IsOpen == true && _handle.Device is { } lease && state.Device is { } activeDevice)
        {
            SetStatus(new SessionActive<TSession>(lease.Session, activeDevice));
            return;
        }

        // The device is enumerated but the policy already gave up trying to open
        // it. Surface the terminal status; a genuine re-enumeration resets the
        // inner proxy's budget and re-opens, which moves us back through
        // SessionStarting/SessionActive via OnDeviceOpened.
        if (_handle?.State == Periphery.ConnectionState.GaveUp)
        {
            SetStatus(new SessionGaveUp<TSession>(_lastError, _attempt));
            return;
        }

        if (_status is DeviceAbsent<TSession> && state.Device is { } device)
            SetStatus(new SessionStarting<TSession>(device));
    }

    private void OnDeviceOpened(object? sender, SessionLease<TSession> lease)
    {
        _attempt = 0;
        _lastError = null;
        SetStatus(new SessionActive<TSession>(lease.Session, lease.DeviceInfo));
    }

    private void OnDeviceClosed(object? sender, EventArgs args)
    {
        _attempt = 0;
        _lastError = null;
        SetStatus(new DeviceAbsent<TSession>());
    }

    private void SetStatus(HostStatus<TSession> status)
    {
        ArgumentNullException.ThrowIfNull(status);

        HostStatus<TSession>? previous;
        lock (_statusLock)
        {
            if (Equals(_status, status))
                return;

            previous = _status;
            _status = status;
        }

        TryNotify(() => StatusChanged?.Invoke(this, status));
        RaisePropertyChanged(nameof(Status));
        RaisePropertyChanged(nameof(StatusDescription));

        if ((previous is SessionActive<TSession>) != (status is SessionActive<TSession>))
        {
            RaisePropertyChanged(nameof(HasSession));
            RaisePropertyChanged(nameof(CurrentSession));
        }

        RaisePropertyChanged(nameof(DeviceInfo));
    }

    private void RaisePropertyChanged(string propertyName)
        => TryNotify(() => PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(propertyName)));

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

    private static string DescribeStatus(HostStatus<TSession> status) => status switch
    {
        DeviceAbsent<TSession> => "current status: DeviceAbsent",
        SessionStarting<TSession> => "current status: SessionStarting",
        SessionActive<TSession> => "current status: SessionActive",
        SessionUnavailable<TSession> unavailable => unavailable.LastError is null
            ? $"current status: SessionUnavailable (attempt {unavailable.Attempt})"
            : $"current status: SessionUnavailable (attempt {unavailable.Attempt}, last error: {unavailable.LastError.Message})",
        SessionGaveUp<TSession> gaveUp => gaveUp.LastError is null
            ? $"current status: SessionGaveUp (attempt {gaveUp.Attempt})"
            : $"current status: SessionGaveUp (attempt {gaveUp.Attempt}, last error: {gaveUp.LastError.Message})",
        _ => "current status: Unknown",
    };

    private sealed class SessionLease<TLeaseSession> : IAsyncDisposable
        where TLeaseSession : class
    {
        private readonly Func<TLeaseSession, Task>? _onSessionEnded;
        private int _disposed;

        public SessionLease(
            DeviceInfo deviceInfo,
            TLeaseSession session,
            Func<TLeaseSession, Task>? onSessionEnded)
        {
            DeviceInfo = deviceInfo;
            Session = session;
            _onSessionEnded = onSessionEnded;
        }

        public DeviceInfo DeviceInfo { get; }

        public TLeaseSession Session { get; }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            if (_onSessionEnded is null)
                return;

            try
            {
                await _onSessionEnded(Session).ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }
}
