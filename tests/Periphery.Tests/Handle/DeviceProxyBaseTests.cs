using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Periphery.Tests;

public class DeviceProxyBaseTests
{
    // ── Helpers ────────────────────────────────────────────────────────

    private static DeviceInfo MakeDevice(
        string id = "TEST\\VID_0001&PID_0002\\1",
        bool isActive = true) => new()
    {
        Id = id,
        Name = "Test Device",
        Category = DeviceCategory.Usb,
        IsActive = isActive,
        VendorId = new HardwareId(0x0001),
        ProductId = new HardwareId(0x0002),
    };

    /// <summary>Minimal <see cref="IAsyncDisposable"/> device for tests.</summary>
    private sealed class FakeDevice : IAsyncDisposable
    {
        public bool IsDisposed { get; private set; }
        public ValueTask DisposeAsync() { IsDisposed = true; return ValueTask.CompletedTask; }
    }

    /// <summary>
    /// Test-double that inherits the base class and exposes
    /// configurable hooks via delegates.
    /// </summary>
    private sealed class TestHandle : DeviceProxyBase<FakeDevice, Exception>
    {
        private readonly Func<DeviceInfo, CancellationToken, Task<FakeDevice>>? _openDevice;
        private readonly Func<FakeDevice, CancellationToken, Task>? _onActivated;
        private readonly Func<FakeDevice, Task>? _onDeactivated;
        private readonly Func<FakeDevice, CancellationToken, Task>? _whileOpen;
        private readonly TimeSpan? _stableOpenDwell;
        private readonly TimeSpan? _faultedSettleWindow;
        private readonly TimeSpan? _resetReopenTimeout;
        private readonly TimeSpan? _resetReopenPollInterval;

        public FakeDevice? LastOpenedDevice { get; private set; }

        public TestHandle(
            DeviceTracker tracker,
            DeviceWatcher watcher,
            Func<DeviceInfo, CancellationToken, Task<FakeDevice>>? openDevice = null,
            Func<FakeDevice, CancellationToken, Task>? onActivated = null,
            Func<FakeDevice, Task>? onDeactivated = null,
            Func<FakeDevice, CancellationToken, Task>? whileOpen = null,
            IRecoveryPolicy? recoveryPolicy = null,
            IDeviceReset? deviceReset = null,
            IResetSafetyGate? resetSafetyGate = null,
            bool faultedNodeRecovery = false,
            TimeSpan? stableOpenDwell = null,
            TimeSpan? faultedSettleWindow = null,
            TimeSpan? resetReopenTimeout = null,
            TimeSpan? resetReopenPollInterval = null)
            : base(tracker, watcher, recoveryPolicy, deviceReset, resetSafetyGate, faultedNodeRecovery)
        {
            _openDevice = openDevice;
            _onActivated = onActivated;
            _onDeactivated = onDeactivated;
            _whileOpen = whileOpen;
            _stableOpenDwell = stableOpenDwell;
            _faultedSettleWindow = faultedSettleWindow;
            _resetReopenTimeout = resetReopenTimeout;
            _resetReopenPollInterval = resetReopenPollInterval;
        }

        // Lets tests drive the ADR-0060 stable-open dwell deterministically: very long
        // to keep it from ever firing during a fast flap loop, or very short to assert
        // the budget actually clears once a session proves stable.
        protected override TimeSpan StableOpenDwell => _stableOpenDwell ?? base.StableOpenDwell;

        // ADR-0060 Decision 11 faulted-node timing knobs — short in tests so the settle
        // window and the reset self-reopen backstop resolve in milliseconds, not seconds.
        protected override TimeSpan FaultedNodeSettleWindow => _faultedSettleWindow ?? base.FaultedNodeSettleWindow;
        protected override TimeSpan ResetReopenTimeout => _resetReopenTimeout ?? base.ResetReopenTimeout;
        protected override TimeSpan ResetReopenPollInterval => _resetReopenPollInterval ?? base.ResetReopenPollInterval;

        protected override Task<FakeDevice> OpenDeviceAsync(
            DeviceInfo deviceInfo, CancellationToken ct)
        {
            var device = new FakeDevice();
            LastOpenedDevice = device;
            return _openDevice?.Invoke(deviceInfo, ct)
                ?? Task.FromResult(device);
        }

        protected override Task OnActivatedAsync(FakeDevice device, CancellationToken ct)
            => _onActivated?.Invoke(device, ct) ?? Task.CompletedTask;

        protected override Task OnDeactivatedAsync(FakeDevice device)
            => _onDeactivated?.Invoke(device) ?? Task.CompletedTask;

        protected override bool HasWorker => _whileOpen is not null;

        protected override Task WhileOpenAsync(FakeDevice device, CancellationToken ct)
            => _whileOpen?.Invoke(device, ct) ?? Task.CompletedTask;
    }

    /// <summary>
    /// Creates a tracker + watcher pair wired up for testing.
    /// The watcher is NOT started — tracker state is driven via
    /// internal methods (<c>OnDeviceAppeared</c>, <c>OnDeviceActivated</c>).
    /// </summary>
    private static (DeviceTracker tracker, DeviceWatcher watcher) CreateTestInfra()
    {
        var tracker = new DeviceTracker(new DeviceFilter());
        var watcher = Devices.Watch().AddTracker(tracker);
        return (tracker, watcher);
    }

    /// <summary>Simulates a device appearing and becoming active.</summary>
    private static void SimulateConnect(DeviceTracker tracker, DeviceInfo device)
    {
        tracker.OnDeviceAppeared(device);
        tracker.OnDeviceConnected(device);
    }

    /// <summary>Simulates a device deactivating and disappearing.</summary>
    private static void SimulateDisconnect(DeviceTracker tracker, DeviceInfo device)
    {
        var inactive = device with { IsActive = false };
        tracker.OnDeviceDisconnected(inactive);
        tracker.OnDeviceDisappeared(inactive);
    }

    // ── Default state ──────────────────────────────────────────────────

    [Fact]
    public void NewHandle_IsNotConnected()
    {
        var (tracker, watcher) = CreateTestInfra();
        var handle = new TestHandle(tracker, watcher);

        Assert.False(handle.IsOpen);
        Assert.Null(handle.Device);
    }

    // ── Connection lifecycle ───────────────────────────────────────────

    [Fact]
    public async Task DeviceActivation_OpensDevice_SetsIsOpen()
    {
        var (tracker, watcher) = CreateTestInfra();
        var opened = new TaskCompletionSource();
        var handle = new TestHandle(tracker, watcher);
        handle.DeviceOpened += (_, _) => opened.TrySetResult();

        SimulateConnect(tracker, MakeDevice());
        await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(handle.IsOpen);
        Assert.NotNull(handle.Device);
    }

    [Fact]
    public async Task DeviceDeactivation_ClosesDevice_ClearsIsOpen()
    {
        var (tracker, watcher) = CreateTestInfra();
        var device = MakeDevice();
        var opened = new TaskCompletionSource();
        var closed = new TaskCompletionSource();
        var handle = new TestHandle(tracker, watcher);
        handle.DeviceOpened += (_, _) => opened.TrySetResult();
        handle.DeviceClosed += (_, _) => closed.TrySetResult();

        SimulateConnect(tracker, device);
        await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));

        SimulateDisconnect(tracker, device);
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(handle.IsOpen);
        Assert.Null(handle.Device);
    }

    [Fact]
    public async Task DeviceDeactivation_DisposesDevice()
    {
        var (tracker, watcher) = CreateTestInfra();
        var device = MakeDevice();
        var opened = new TaskCompletionSource();
        var closed = new TaskCompletionSource();
        var handle = new TestHandle(tracker, watcher);
        handle.DeviceOpened += (_, _) => opened.TrySetResult();
        handle.DeviceClosed += (_, _) => closed.TrySetResult();

        SimulateConnect(tracker, device);
        await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var fakeDevice = handle.LastOpenedDevice!;
        Assert.False(fakeDevice.IsDisposed);

        SimulateDisconnect(tracker, device);
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(fakeDevice.IsDisposed);
    }

    // ── Init gate (OnActivatedAsync) ──────────────────────────────────

    [Fact]
    public async Task OnActivatedAsync_Failure_AbortsConnection_DisposesDevice()
    {
        var (tracker, watcher) = CreateTestInfra();
        var initAttempted = new TaskCompletionSource();
        var handle = new TestHandle(tracker, watcher,
            onActivated: (_, _) =>
            {
                initAttempted.TrySetResult();
                throw new InvalidOperationException("init failed");
            });

        SimulateConnect(tracker, MakeDevice());
        await initAttempted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Give async state machine time to settle
        await Task.Delay(50);

        Assert.False(handle.IsOpen);
        Assert.True(handle.LastOpenedDevice!.IsDisposed);
    }

    // ── Per-connection CancellationToken ───────────────────────────────

    [Fact]
    public async Task OnActivatedAsync_ReceivesCancellableToken()
    {
        var (tracker, watcher) = CreateTestInfra();
        CancellationToken capturedCt = default;
        var initStarted = new TaskCompletionSource();

        var handle = new TestHandle(tracker, watcher,
            onActivated: (_, ct) =>
            {
                capturedCt = ct;
                initStarted.TrySetResult();
                return Task.CompletedTask;
            });

        SimulateConnect(tracker, MakeDevice());
        await initStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(capturedCt.CanBeCanceled);
    }

    // ── WhileOpenAsync ────────────────────────────────────────────────

    [Fact]
    public async Task WhileOpenAsync_NonCTException_TriggersClose()
    {
        var (tracker, watcher) = CreateTestInfra();
        var opened = new TaskCompletionSource();
        var closed = new TaskCompletionSource();

        await using var handle = new TestHandle(tracker, watcher,
            whileOpen: (_, _) => throw new InvalidOperationException("worker crash"));

        handle.DeviceOpened += (_, _) => opened.TrySetResult();
        handle.DeviceClosed += (_, _) => closed.TrySetResult();

        SimulateConnect(tracker, MakeDevice());
        await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(handle.IsOpen);
    }

    [Fact]
    public async Task WhileOpenAsync_ReceivesCancellableToken()
    {
        var (tracker, watcher) = CreateTestInfra();
        CancellationToken capturedCt = default;
        var workerStarted = new TaskCompletionSource();

        var handle = new TestHandle(tracker, watcher,
            whileOpen: async (_, ct) =>
            {
                capturedCt = ct;
                workerStarted.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            });

        var opened = new TaskCompletionSource();
        handle.DeviceOpened += (_, _) => opened.TrySetResult();

        SimulateConnect(tracker, MakeDevice());
        await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await workerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(capturedCt.CanBeCanceled);
        Assert.False(capturedCt.IsCancellationRequested);
    }

    // ── PropertyChanged ────────────────────────────────────────────────

    [Fact]
    public async Task IsOpen_RaisesPropertyChanged()
    {
        var (tracker, watcher) = CreateTestInfra();
        var handle = new TestHandle(tracker, watcher);

        var propertyNames = new List<string>();
        handle.PropertyChanged += (_, e) => propertyNames.Add(e.PropertyName!);

        var opened = new TaskCompletionSource();
        handle.DeviceOpened += (_, _) => opened.TrySetResult();

        SimulateConnect(tracker, MakeDevice());
        await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains(nameof(handle.IsOpen), propertyNames);
    }

    // ── Disposal ───────────────────────────────────────────────────────

    [Fact]
    public async Task DisposeAsync_ClosesDevice_StopsTracking()
    {
        var (tracker, watcher) = CreateTestInfra();
        var opened = new TaskCompletionSource();
        var handle = new TestHandle(tracker, watcher);
        handle.DeviceOpened += (_, _) => opened.TrySetResult();

        SimulateConnect(tracker, MakeDevice());
        await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var fakeDevice = handle.LastOpenedDevice!;
        await handle.DisposeAsync();

        Assert.False(handle.IsOpen);
        Assert.True(fakeDevice.IsDisposed);
    }

    [Fact]
    public async Task DisposeAsync_BeforeConnection_DoesNotThrow()
    {
        var (tracker, watcher) = CreateTestInfra();
        var handle = new TestHandle(tracker, watcher);

        await handle.DisposeAsync();

        Assert.False(handle.IsOpen);
    }

    // ── OpenFailed event ───────────────────────────────────────────────

    [Fact]
    public async Task OpenDeviceAsync_ThrowsTException_FiresOpenFailed()
    {
        var (tracker, watcher) = CreateTestInfra();
        var failedTcs = new TaskCompletionSource<Exception>();
        var handle = new TestHandle(tracker, watcher,
            openDevice: (_, _) => throw new InvalidOperationException("open failed"));

        handle.OpenFailed += (_, ex) => failedTcs.TrySetResult(ex);

        SimulateConnect(tracker, MakeDevice());
        var ex = await failedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsType<InvalidOperationException>(ex);
        Assert.False(handle.IsOpen);
    }

    // ── OnDeactivatedAsync hook ───────────────────────────────────────

    [Fact]
    public async Task OnDeactivatedAsync_CalledDuringClose()
    {
        var (tracker, watcher) = CreateTestInfra();
        var device = MakeDevice();
        bool wasDisposedDuringHook = true; // pessimistic default
        var disconnectHookCalled = new TaskCompletionSource();

        var handle = new TestHandle(tracker, watcher,
            onDeactivated: d =>
            {
                // Capture disposed state inside the hook — by the time the
                // TCS continuation runs, CloseDeviceAsync may have already
                // called DisposeAsync on the device.
                wasDisposedDuringHook = d.IsDisposed;
                disconnectHookCalled.TrySetResult();
                return Task.CompletedTask;
            });

        var opened = new TaskCompletionSource();
        handle.DeviceOpened += (_, _) => opened.TrySetResult();

        SimulateConnect(tracker, device);
        await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));

        SimulateDisconnect(tracker, device);
        await disconnectHookCalled.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // OnDisconnecting is called BEFORE DisposeAsync
        Assert.False(wasDisposedDuringHook);
    }

    // ── Reconnect policy seam (ADR-0055) ──────────────────────────────

    /// <summary>
    /// Records each <see cref="ReconnectContext"/> it sees and returns a
    /// caller-supplied delay (or null to give up). Delays are tiny so retry
    /// loops in tests resolve quickly.
    /// </summary>
    private sealed class RecordingPolicy(Func<RecoveryContext, TimeSpan?> decide)
        : IRecoveryPolicy
    {
        public List<int> SeenAttempts { get; } = [];

        public RecoveryDirective Decide(RecoveryContext context)
        {
            lock (SeenAttempts) SeenAttempts.Add(context.Attempt);
            // null delay => give up; otherwise retry after the delay — maps the
            // legacy TimeSpan? decide-func onto the directive shape.
            return decide(context) is { } delay
                ? new RecoveryDirective.Retry(delay)
                : new RecoveryDirective.GiveUp();
        }
    }

    /// <summary>A policy that returns a caller-supplied directive verbatim.</summary>
    private sealed class FuncPolicy(Func<RecoveryContext, RecoveryDirective> decide) : IRecoveryPolicy
    {
        public RecoveryDirective Decide(RecoveryContext context) => decide(context);
    }

    /// <summary>Records reset invocations; advertises a fixed strategy set.</summary>
    private sealed class FakeDeviceReset(params ResetStrategy[] strategies) : IDeviceReset
    {
        private int _resetCalls;
        public int ResetCalls => Volatile.Read(ref _resetCalls);

        /// <summary>Invoked at the start of each <see cref="ResetAsync"/> (after the
        /// call is counted). Lets a test model the reset actually clearing the
        /// devnode — e.g. driving the tracker Active — deterministically ordered
        /// after the reset is recorded, rather than racing a state-edge handler.</summary>
        public Action? OnReset { get; set; }

        public IReadOnlyList<ResetStrategy> StrategiesFor(DeviceInfo device) => strategies;

        public ValueTask<ResetOutcome> ResetAsync(DeviceInfo device, ResetStrategy strategy, CancellationToken ct)
        {
            Interlocked.Increment(ref _resetCalls);
            OnReset?.Invoke();
            return new(ResetOutcome.Issued);
        }
    }

    /// <summary>A toggleable reset-safety gate.</summary>
    private sealed class ToggleGate : IResetSafetyGate
    {
        public volatile bool Safe = true;
        public ValueTask<bool> CanResetAsync(DeviceInfo device, CancellationToken ct) => new(Safe);
    }

    [Fact]
    public void DefaultPolicy_ReproducesLegacyCurve()
    {
        // Legacy s_reconnectBackoff was [1s, 2s, 4s, 5s] clamped at 5s.
        var policy = ExponentialBackoffRecoveryPolicy.Default;
        var device = MakeDevice();

        TimeSpan Delay(int attempt) =>
            ((RecoveryDirective.Retry)policy.Decide(
                new RecoveryContext(attempt, 0, null, device, []))).Delay;

        Assert.Equal(TimeSpan.FromSeconds(1), Delay(1));
        Assert.Equal(TimeSpan.FromSeconds(2), Delay(2));
        Assert.Equal(TimeSpan.FromSeconds(4), Delay(3));
        Assert.Equal(TimeSpan.FromSeconds(5), Delay(4)); // 8s clamped to 5s
        Assert.Equal(TimeSpan.FromSeconds(5), Delay(5)); // stays clamped
        Assert.Equal(TimeSpan.FromSeconds(5), Delay(10));
    }

    [Fact]
    public void DefaultPolicy_NeverGivesUp()
    {
        var policy = ExponentialBackoffRecoveryPolicy.Default;
        var directive = policy.Decide(new RecoveryContext(1000, 0, null, MakeDevice(), []));
        Assert.IsType<RecoveryDirective.Retry>(directive);
    }

    [Fact]
    public void BoundedPolicy_GivesUpAfterMaxAttempts()
    {
        var policy = new ExponentialBackoffRecoveryPolicy(
            TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1), maxAttempts: 3);
        var device = MakeDevice();

        RecoveryDirective Decide(int attempt) =>
            policy.Decide(new RecoveryContext(attempt, 0, null, device, []));

        Assert.IsType<RecoveryDirective.Retry>(Decide(1));
        Assert.IsType<RecoveryDirective.Retry>(Decide(2));
        Assert.IsType<RecoveryDirective.Retry>(Decide(3));
        Assert.IsType<RecoveryDirective.GiveUp>(Decide(4)); // exceeds maxAttempts -> give up
    }

    [Fact]
    public async Task BoundedPolicy_FailingOpen_TransitionsToGaveUp()
    {
        var (tracker, watcher) = CreateTestInfra();
        // Give up immediately on attempt 1: openable presence but policy stops.
        var policy = new RecordingPolicy(ctx => ctx.Attempt > 2 ? null : TimeSpan.FromMilliseconds(1));

        var gaveUp = new TaskCompletionSource();
        var handle = new TestHandle(tracker, watcher,
            openDevice: (_, _) => throw new InvalidOperationException("wedged endpoint"),
            recoveryPolicy: policy);
        handle.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(handle.State) && handle.State == ConnectionState.GaveUp)
                gaveUp.TrySetResult();
        };

        SimulateConnect(tracker, MakeDevice());
        await gaveUp.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ConnectionState.GaveUp, handle.State);
        Assert.False(handle.IsOpen);
        Assert.NotNull(handle.LastOpenFault);
        // Attempts 1, 2 (delayed retries) then 3 -> null/give-up.
        lock (policy.SeenAttempts)
            Assert.Equal(new[] { 1, 2, 3 }, policy.SeenAttempts);
    }

    [Fact]
    public async Task ReEnumeration_AfterGaveUp_ResetsCounter_AndReopens()
    {
        var (tracker, watcher) = CreateTestInfra();

        // Fail until we flip the gate, then succeed. Give up after attempt 1
        // so the first connect reaches GaveUp quickly.
        bool failOpens = true;
        var policy = new RecordingPolicy(ctx => ctx.Attempt > 1 ? null : TimeSpan.FromMilliseconds(1));

        var device = MakeDevice();
        var gaveUp = new TaskCompletionSource();
        var reopened = new TaskCompletionSource();

        var handle = new TestHandle(tracker, watcher,
            openDevice: (_, _) => failOpens
                ? throw new InvalidOperationException("still wedged")
                : Task.FromResult(new FakeDevice()),
            recoveryPolicy: policy);

        handle.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(handle.State) && handle.State == ConnectionState.GaveUp)
                gaveUp.TrySetResult();
        };
        handle.DeviceOpened += (_, _) => reopened.TrySetResult();

        SimulateConnect(tracker, device);
        await gaveUp.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ConnectionState.GaveUp, handle.State);

        // Device "re-enumerates" (power-cycle / replug): fix the open path,
        // then drive a fresh active transition.
        failOpens = false;
        lock (policy.SeenAttempts) policy.SeenAttempts.Clear();

        SimulateDisconnect(tracker, device);
        SimulateConnect(tracker, device);

        await reopened.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(handle.IsOpen);
        Assert.Equal(ConnectionState.Open, handle.State);
    }

    [Fact]
    public async Task State_TransitionsRaisePropertyChanged()
    {
        var (tracker, watcher) = CreateTestInfra();
        var handle = new TestHandle(tracker, watcher);

        var stateNames = new List<string>();
        handle.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(handle.State) or nameof(handle.IsOpen))
                lock (stateNames) stateNames.Add(e.PropertyName!);
        };

        var opened = new TaskCompletionSource();
        handle.DeviceOpened += (_, _) => opened.TrySetResult();

        SimulateConnect(tracker, MakeDevice());
        await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));

        lock (stateNames)
        {
            Assert.Contains(nameof(handle.State), stateNames);
            Assert.Contains(nameof(handle.IsOpen), stateNames);
        }
        Assert.Equal(ConnectionState.Open, handle.State);
    }

    // ── Reset + recovery (ADR-0060) ───────────────────────────────────

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return;
            await Task.Delay(20);
        }
        if (!condition()) throw new TimeoutException("Condition not met within timeout.");
    }

    [Fact]
    public async Task Policy_ReturnsReset_InvokesMechanismThenSelfReopens()
    {
        var (tracker, watcher) = CreateTestInfra();
        var reset = new FakeDeviceReset(
            new ResetStrategy(ResetKind.UsbPortCycle, ResetBlastRadius.Self, ReEnumerates: true));

        bool healed = false;
        var reopened = new TaskCompletionSource();

        var handle = new TestHandle(tracker, watcher,
            openDevice: (_, _) => healed
                ? Task.FromResult(new FakeDevice())
                : throw new InvalidOperationException("wedged endpoint"),
            // Reset once (budget 1), otherwise give up.
            recoveryPolicy: new FuncPolicy(ctx =>
                ctx.AvailableResets.Count > 0 && ctx.ResetCount < 1
                    ? new RecoveryDirective.Reset(ctx.AvailableResets[0])
                    : new RecoveryDirective.GiveUp()),
            deviceReset: reset);

        // The reset "heals" the device: flip the open path to success the moment
        // the proxy enters Resetting, before its self-driven reopen polls.
        handle.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(handle.State) && handle.State == ConnectionState.Resetting)
                healed = true;
        };
        handle.DeviceOpened += (_, _) => reopened.TrySetResult();

        SimulateConnect(tracker, MakeDevice());
        await reopened.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(handle.IsOpen);
        Assert.Equal(ConnectionState.Open, handle.State);
        Assert.Equal(1, reset.ResetCalls);   // the mechanism ran exactly once
    }

    [Fact]
    public async Task Recover_FromConsumerFault_ClosesThenReopens()
    {
        var (tracker, watcher) = CreateTestInfra();
        int opens = 0;
        var reopened = new TaskCompletionSource();
        var closed = new TaskCompletionSource();

        var handle = new TestHandle(tracker, watcher,
            recoveryPolicy: new FuncPolicy(_ => new RecoveryDirective.Retry(TimeSpan.FromMilliseconds(1))));
        handle.DeviceOpened += (_, _) =>
        {
            if (Interlocked.Increment(ref opens) == 2) reopened.TrySetResult();
        };
        handle.DeviceClosed += (_, _) => closed.TrySetResult();

        SimulateConnect(tracker, MakeDevice());
        await WaitForAsync(() => handle.IsOpen, TimeSpan.FromSeconds(5));

        handle.Recover(new InvalidOperationException("io wedge"));

        await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("io wedge", handle.LastOpenFault?.Message);

        await reopened.Task.WaitAsync(TimeSpan.FromSeconds(5));   // the recovery loop reopens
        Assert.True(handle.IsOpen);
    }

    [Fact]
    public async Task SafetyGate_Unsafe_DefersReset_MechanismNotInvoked()
    {
        var (tracker, watcher) = CreateTestInfra();
        var reset = new FakeDeviceReset(
            new ResetStrategy(ResetKind.UsbPortCycle, ResetBlastRadius.Self, ReEnumerates: true));
        var gate = new ToggleGate { Safe = false };

        await using var handle = new TestHandle(tracker, watcher,
            openDevice: (_, _) => throw new InvalidOperationException("wedged"),
            recoveryPolicy: new FuncPolicy(ctx => ctx.AvailableResets.Count > 0
                ? new RecoveryDirective.Reset(ctx.AvailableResets[0])
                : new RecoveryDirective.GiveUp()),
            deviceReset: reset,
            resetSafetyGate: gate);

        SimulateConnect(tracker, MakeDevice());
        await Task.Delay(400);   // gate denies every attempt within the defer window

        Assert.Equal(0, reset.ResetCalls);   // the gate blocked the mechanism
        Assert.False(handle.IsOpen);
    }

    // ── Stable-open dwell (ADR-0060) ──────────────────────────────────

    [Fact]
    public async Task OpenThenFaultWithinDwell_PreservesBudget_EscalatesToGaveUp()
    {
        // The motivating regression: a device whose OPEN succeeds (the open only
        // exercises a healthy endpoint) but whose session re-faults shortly after.
        // Because the worker faults far sooner than the (very long) dwell, the budget
        // must NOT be cleared — so ResetCount keeps climbing across cycles, the policy
        // is allowed to escalate, and recovery finally reaches GaveUp. Under the old
        // "clear on open" behaviour this is an infinite reset loop that never gives up.
        var (tracker, watcher) = CreateTestInfra();
        var reset = new FakeDeviceReset(
            new ResetStrategy(ResetKind.UsbPortCycle, ResetBlastRadius.Self, ReEnumerates: true));

        var seenResetCounts = new List<int>();
        var gaveUp = new TaskCompletionSource();

        var handle = new TestHandle(tracker, watcher,
            // Open always succeeds; the SESSION is what faults (immediately).
            whileOpen: (_, _) => throw new InvalidOperationException("wedged DATA endpoint"),
            recoveryPolicy: new FuncPolicy(ctx =>
            {
                lock (seenResetCounts) seenResetCounts.Add(ctx.ResetCount);
                // Reset while the budget is below 3, then concede. With the budget
                // preserved this walks 0 -> 1 -> 2 -> 3 and gives up; without the fix
                // it stays pinned at 0 forever.
                return ctx.AvailableResets.Count > 0 && ctx.ResetCount < 3
                    ? new RecoveryDirective.Reset(ctx.AvailableResets[0])
                    : new RecoveryDirective.GiveUp();
            }),
            deviceReset: reset,
            // Long enough that it never fires while the session keeps refaulting fast.
            stableOpenDwell: TimeSpan.FromSeconds(30));
        handle.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(handle.State) && handle.State == ConnectionState.GaveUp)
                gaveUp.TrySetResult();
        };

        SimulateConnect(tracker, MakeDevice());
        await gaveUp.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(ConnectionState.GaveUp, handle.State);
        Assert.Equal(3, reset.ResetCalls);   // the ladder escalated through 3 resets
        lock (seenResetCounts)
            // The budget climbed monotonically across cycles, then conceded at 3.
            Assert.Equal(new[] { 0, 1, 2, 3 }, seenResetCounts);
    }

    [Fact]
    public async Task OpenSurvivesDwell_ClearsBudget_AndLastOpenFault()
    {
        // A session that stays up past the dwell DOES clear the budget and the last
        // fault: a later, unrelated fault must start a fresh ladder from strategy [0]
        // (ResetCount == 0).
        var (tracker, watcher) = CreateTestInfra();
        var reset = new FakeDeviceReset(
            new ResetStrategy(ResetKind.UsbPortCycle, ResetBlastRadius.Self, ReEnumerates: true));

        int workerRuns = 0;
        bool firstResetDone = false;
        bool finalPhase = false;
        var budgetOnLaterFault = new TaskCompletionSource<int>();

        var handle = new TestHandle(tracker, watcher,
            whileOpen: async (_, ct) =>
            {
                // Run #1 faults to drive one reset (budget -> 1); every later session
                // stays up so the dwell can elapse.
                if (Interlocked.Increment(ref workerRuns) == 1)
                    throw new InvalidOperationException("initial wedge");
                await Task.Delay(Timeout.Infinite, ct);
            },
            recoveryPolicy: new FuncPolicy(ctx =>
            {
                if (finalPhase)
                {
                    // The later, post-dwell fault: capture what budget it sees.
                    budgetOnLaterFault.TrySetResult(ctx.ResetCount);
                    return new RecoveryDirective.Retry(TimeSpan.FromMilliseconds(1));
                }
                if (ctx.AvailableResets.Count > 0 && !firstResetDone)
                {
                    firstResetDone = true;
                    return new RecoveryDirective.Reset(ctx.AvailableResets[0]);   // budget -> 1
                }
                return new RecoveryDirective.Retry(TimeSpan.FromMilliseconds(1));
            }),
            deviceReset: reset,
            stableOpenDwell: TimeSpan.FromMilliseconds(120));

        // Connect -> worker faults -> reset (budget 1) -> reopen -> session stays up.
        SimulateConnect(tracker, MakeDevice());
        await WaitForAsync(() => handle.IsOpen, TimeSpan.FromSeconds(5));
        Assert.NotNull(handle.LastOpenFault);   // the initial wedge is recorded

        // Let the session out-survive the dwell: the budget AND LastOpenFault clear.
        await WaitForAsync(() => handle.LastOpenFault is null, TimeSpan.FromSeconds(5));

        // A later, unrelated fault must now see a fresh budget (0).
        finalPhase = true;
        handle.Recover(new InvalidOperationException("later unrelated fault"));

        int seen = await budgetOnLaterFault.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, seen);
    }

    [Fact]
    public async Task DwellCancelledOnClose_DoesNotClearBudget_ForEndedSession()
    {
        // The dwell timer must never zero the budget for a session that has already
        // ended. A session opens with budget 1, then CLOSES before the dwell elapses;
        // the stale timer must not fire. We then wait well past the (now-cancelled)
        // dwell deadline and confirm the next fault still sees the preserved budget.
        var (tracker, watcher) = CreateTestInfra();
        var reset = new FakeDeviceReset(
            new ResetStrategy(ResetKind.UsbPortCycle, ResetBlastRadius.Self, ReEnumerates: true));
        var device = MakeDevice();

        int workerRuns = 0;
        bool firstResetDone = false;
        bool finalPhase = false;
        var budgetOnFinalFault = new TaskCompletionSource<int>();

        var handle = new TestHandle(tracker, watcher,
            whileOpen: async (_, ct) =>
            {
                // Run #1 faults to drive one reset (budget -> 1); later sessions stay up.
                if (Interlocked.Increment(ref workerRuns) == 1)
                    throw new InvalidOperationException("initial wedge");
                await Task.Delay(Timeout.Infinite, ct);
            },
            recoveryPolicy: new FuncPolicy(ctx =>
            {
                if (finalPhase)
                {
                    budgetOnFinalFault.TrySetResult(ctx.ResetCount);
                    return new RecoveryDirective.GiveUp();
                }
                if (ctx.AvailableResets.Count > 0 && !firstResetDone)
                {
                    firstResetDone = true;
                    return new RecoveryDirective.Reset(ctx.AvailableResets[0]);   // budget -> 1
                }
                return new RecoveryDirective.Retry(TimeSpan.FromMilliseconds(1));
            }),
            deviceReset: reset,
            stableOpenDwell: TimeSpan.FromMilliseconds(200));

        // Connect -> worker faults -> reset (budget 1) -> reopen -> session B stays up.
        SimulateConnect(tracker, device);
        await WaitForAsync(() => handle.IsOpen, TimeSpan.FromSeconds(5));

        // Close session B BEFORE its 200ms dwell elapses -> the dwell must be cancelled.
        SimulateDisconnect(tracker, device);
        await WaitForAsync(() => !handle.IsOpen, TimeSpan.FromSeconds(5));

        // Wait well past the cancelled dwell's deadline. A stale-but-firing dwell would
        // wrongly zero the budget here.
        await Task.Delay(500);

        // Device returns; the next fault must still see the preserved budget (1).
        finalPhase = true;
        SimulateConnect(tracker, device);
        handle.Recover(new InvalidOperationException("probe"));

        int seen = await budgetOnFinalFault.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, seen);   // the ended session's cancelled dwell did not clear it
    }

    // ── Faulted-node recovery (ADR-0060 Decision 11) ──────────────────

    /// <summary>
    /// Builds an enumerated-but-not-active device snapshot: it matches the (empty)
    /// test filter so the tracker holds it as <see cref="DeviceActivityStatus.Present"/>,
    /// but <see cref="DeviceInfo.IsActive"/> is <c>false</c> so it never resolves to
    /// Active. The OS-status fields drive the fault classifier.
    /// </summary>
    private static DeviceInfo MakeFaultedDevice(
        DeviceStatus status = DeviceStatus.Error,
        int? problemCode = DeviceFaultClassifier.CmProbFailedPostStart,   // 21 — the field wedge
        string id = "TEST\\VID_0001&PID_0002\\1")
    {
        var props = new Dictionary<string, object?>();
        if (problemCode is { } code)
            props[WellKnownProperties.RawStatus] = code;

        return new()
        {
            Id = id,
            Name = "Faulted Device",
            Category = DeviceCategory.Usb,
            IsActive = false,            // enumerated, not active -> tracker resolves to Present
            BusType = BusType.USB,       // USB-backed so a reset strategy is conceivable
            VendorId = new HardwareId(0x0001),
            ProductId = new HardwareId(0x0002),
            Status = status,
            Properties = props,
        };
    }

    /// <summary>Simulates a device appearing in the tree but never becoming active (Present, not Active).</summary>
    private static void SimulatePresentFaulted(DeviceTracker tracker, DeviceInfo device)
        => tracker.OnDeviceAppeared(device);

    private static ResetStrategy UsbCycle =>
        new(ResetKind.UsbPortCycle, ResetBlastRadius.Self, ReEnumerates: true);

    /// <summary>A policy that records the trigger of every decision it is asked to make.</summary>
    private sealed class TriggerRecordingPolicy(Func<RecoveryContext, RecoveryDirective> decide) : IRecoveryPolicy
    {
        public List<RecoveryTrigger> SeenTriggers { get; } = [];

        public RecoveryDirective Decide(RecoveryContext context)
        {
            lock (SeenTriggers) SeenTriggers.Add(context.Trigger);
            return decide(context);
        }
    }

    [Fact]
    public async Task FaultedNode_ResettableFault_ResetsThenReachesActive_WithoutPriorOpen()
    {
        // A device enumerates faulted (Status=Error, problem 21) and never reaches
        // Active. With faulted-node recovery opted in, the proxy drives the reset ladder
        // after the settle window; the reset "clears the devnode" (modelled by driving it
        // Active from inside the reset action, so the heal is ordered after the reset is
        // recorded), and the device then opens through the normal Active path — without
        // ever having attempted an open on the faulted handle.
        var (tracker, watcher) = CreateTestInfra();
        var reset = new FakeDeviceReset(UsbCycle);
        var faulted = MakeFaultedDevice();
        var healthy = MakeDevice();   // the same id, now active/healthy

        var policy = new TriggerRecordingPolicy(ctx =>
            ctx.AvailableResets.Count > 0 && ctx.ResetCount < 1
                ? new RecoveryDirective.Reset(ctx.AvailableResets[0])
                : new RecoveryDirective.GiveUp());

        var reopened = new TaskCompletionSource();
        // The reset heals the node: drive it Active from inside the reset itself, so the
        // self-driven reopen finds an Active device to open. Modelling the heal on the
        // reset action (not the Resetting state edge, which fires BEFORE ExecuteResetAsync
        // records the reset) keeps "reset ran, then reopened" deterministically ordered —
        // otherwise the injected Active event races the reset call and the reopen can win.
        reset.OnReset = () => SimulateConnect(tracker, healthy);
        var handle = new TestHandle(tracker, watcher,
            recoveryPolicy: policy,
            deviceReset: reset,
            faultedNodeRecovery: true,
            faultedSettleWindow: TimeSpan.FromMilliseconds(50));
        handle.DeviceOpened += (_, _) => reopened.TrySetResult();

        SimulatePresentFaulted(tracker, faulted);
        await reopened.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(handle.IsOpen);
        Assert.Equal(ConnectionState.Open, handle.State);
        Assert.Equal(1, reset.ResetCalls);
        // Every decision on this path was tagged EnumeratedFault, never OpenFailure.
        lock (policy.SeenTriggers)
            Assert.All(policy.SeenTriggers, t => Assert.Equal(RecoveryTrigger.EnumeratedFault, t));
    }

    [Fact]
    public async Task FaultedNode_KeepsFaulting_EscalatesToGaveUp_WithoutPriorOpen()
    {
        // The node never clears (we never drive it Active). The reset budget climbs across
        // cycles (0 -> 1 -> 2 -> 3) and the policy concedes at 3, so recovery reaches
        // GaveUp — the "needs a human" signal — instead of reset-looping forever. The open
        // path is never exercised: there is no Active device to open.
        var (tracker, watcher) = CreateTestInfra();
        var reset = new FakeDeviceReset(UsbCycle);
        var faulted = MakeFaultedDevice(DeviceStatus.Error, DeviceFaultClassifier.CmProbFailedStart);  // 10

        var seenResetCounts = new List<int>();
        var gaveUp = new TaskCompletionSource();

        var handle = new TestHandle(tracker, watcher,
            recoveryPolicy: new TriggerRecordingPolicy(ctx =>
            {
                lock (seenResetCounts) seenResetCounts.Add(ctx.ResetCount);
                return ctx.AvailableResets.Count > 0 && ctx.ResetCount < 3
                    ? new RecoveryDirective.Reset(ctx.AvailableResets[0])
                    : new RecoveryDirective.GiveUp();
            }),
            deviceReset: reset,
            faultedNodeRecovery: true,
            faultedSettleWindow: TimeSpan.FromMilliseconds(50),
            // The node never goes Active, so each reset's self-reopen backstop times out;
            // keep that fast so the ladder walks to GaveUp in well under the test timeout.
            resetReopenTimeout: TimeSpan.FromMilliseconds(80),
            resetReopenPollInterval: TimeSpan.FromMilliseconds(20));
        handle.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(handle.State) && handle.State == ConnectionState.GaveUp)
                gaveUp.TrySetResult();
        };

        SimulatePresentFaulted(tracker, faulted);
        await gaveUp.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(ConnectionState.GaveUp, handle.State);
        Assert.Equal(3, reset.ResetCalls);                 // escalated through 3 resets
        Assert.Null(handle.LastOpenedDevice);              // never attempted an open
        lock (seenResetCounts)
            Assert.Equal(new[] { 0, 1, 2, 3 }, seenResetCounts);
    }

    [Theory]
    [InlineData(DeviceStatus.Disabled, DeviceFaultClassifier.CmProbDisabled)]   // 22
    [InlineData(DeviceStatus.Disabled, null)]                                    // non-Windows disabled
    [InlineData(DeviceStatus.Error, DeviceFaultClassifier.CmProbDisabled)]       // Error coarse but code says disabled
    public async Task FaultedNode_DisabledOrProblem22_IsNeverReset(DeviceStatus status, int? problemCode)
    {
        // A user/policy-disabled node (or one whose problem code is CM_PROB_DISABLED) must
        // never be auto-enabled — that fights the operator. The classifier rejects it, so
        // the recovery loop never starts: the mechanism and the policy are never touched.
        var (tracker, watcher) = CreateTestInfra();
        var reset = new FakeDeviceReset(UsbCycle);
        int policyCalls = 0;

        await using var handle = new TestHandle(tracker, watcher,
            recoveryPolicy: new FuncPolicy(ctx =>
            {
                Interlocked.Increment(ref policyCalls);
                return new RecoveryDirective.Reset(ctx.AvailableResets[0]);   // would reset if ever asked
            }),
            deviceReset: reset,
            faultedNodeRecovery: true,
            faultedSettleWindow: TimeSpan.FromMilliseconds(30));

        SimulatePresentFaulted(tracker, MakeFaultedDevice(status, problemCode));
        await Task.Delay(300);   // well past the settle window

        Assert.Equal(0, reset.ResetCalls);
        Assert.Equal(0, Volatile.Read(ref policyCalls));
        Assert.NotEqual(ConnectionState.GaveUp, handle.State);
        Assert.False(handle.IsOpen);
    }

    [Fact]
    public async Task HealthyPresentDevice_IsNeverReset()
    {
        // A healthy present-but-not-active device (problem code 0 — e.g. a Bluetooth
        // device paired but out of range) is a legitimate steady state, not a fault. It
        // must be left strictly alone even with faulted-node recovery opted in.
        var (tracker, watcher) = CreateTestInfra();
        var reset = new FakeDeviceReset(UsbCycle);
        int policyCalls = 0;

        await using var handle = new TestHandle(tracker, watcher,
            recoveryPolicy: new FuncPolicy(_ =>
            {
                Interlocked.Increment(ref policyCalls);
                return new RecoveryDirective.GiveUp();
            }),
            deviceReset: reset,
            faultedNodeRecovery: true,
            faultedSettleWindow: TimeSpan.FromMilliseconds(30));

        SimulatePresentFaulted(tracker, MakeFaultedDevice(DeviceStatus.OK, DeviceFaultClassifier.CmProbNone));
        await Task.Delay(300);

        Assert.Equal(0, reset.ResetCalls);
        Assert.Equal(0, Volatile.Read(ref policyCalls));
        Assert.NotEqual(ConnectionState.GaveUp, handle.State);
        Assert.False(handle.IsOpen);
    }

    [Fact]
    public async Task FaultedNode_OptOutByDefault_LeavesDeviceUntouched()
    {
        // Regression guard for the default: with faulted-node recovery NOT opted in (the
        // default), a faulted Present device behaves exactly as before — the proxy does
        // nothing, no reset, no GaveUp. Only an Active device ever drives recovery.
        var (tracker, watcher) = CreateTestInfra();
        var reset = new FakeDeviceReset(UsbCycle);
        int policyCalls = 0;

        await using var handle = new TestHandle(tracker, watcher,
            recoveryPolicy: new FuncPolicy(ctx =>
            {
                Interlocked.Increment(ref policyCalls);
                return new RecoveryDirective.Reset(ctx.AvailableResets[0]);
            }),
            deviceReset: reset,
            faultedSettleWindow: TimeSpan.FromMilliseconds(30));   // faultedNodeRecovery left false

        SimulatePresentFaulted(tracker, MakeFaultedDevice());
        await Task.Delay(300);

        Assert.Equal(0, reset.ResetCalls);
        Assert.Equal(0, Volatile.Read(ref policyCalls));
        Assert.Equal(ConnectionState.Disconnected, handle.State);
        Assert.False(handle.IsOpen);
    }

    [Fact]
    public async Task OpenFailureRecovery_TagsTriggerOpenFailure()
    {
        // The pre-existing open-failure path (an Active device whose open fails) keeps
        // tagging its RecoveryContext with the default OpenFailure trigger, so a policy
        // can still tell the two recovery causes apart.
        var (tracker, watcher) = CreateTestInfra();
        var gaveUp = new TaskCompletionSource();
        var policy = new TriggerRecordingPolicy(ctx =>
            ctx.Attempt > 2 ? new RecoveryDirective.GiveUp() : new RecoveryDirective.Retry(TimeSpan.FromMilliseconds(1)));

        var handle = new TestHandle(tracker, watcher,
            openDevice: (_, _) => throw new InvalidOperationException("wedged"),
            recoveryPolicy: policy);
        handle.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(handle.State) && handle.State == ConnectionState.GaveUp)
                gaveUp.TrySetResult();
        };

        SimulateConnect(tracker, MakeDevice());   // Active -> open fails -> open-failure ladder
        await gaveUp.Task.WaitAsync(TimeSpan.FromSeconds(5));

        lock (policy.SeenTriggers)
        {
            Assert.NotEmpty(policy.SeenTriggers);
            Assert.All(policy.SeenTriggers, t => Assert.Equal(RecoveryTrigger.OpenFailure, t));
        }
    }

    // ── Pure fault classifier (ADR-0060 Decision 11 / ADR-0052 functional core) ──

    [Theory]
    // Genuine resettable faults: Error with a non-zero, non-disabled problem code.
    [InlineData(DeviceStatus.Error, DeviceFaultClassifier.CmProbFailedStart, true)]        // 10
    [InlineData(DeviceStatus.Error, DeviceFaultClassifier.CmProbFailedPostStart, true)]    // 21
    [InlineData(DeviceStatus.Error, DeviceFaultClassifier.CmProbFailedDriverEntry, true)]  // 31
    [InlineData(DeviceStatus.Error, DeviceFaultClassifier.CmProbDeviceReportedProblem, true)] // 43
    [InlineData(DeviceStatus.Error, null, true)]                                           // non-Windows Error, no code
    // Hands-off: disabled by user / policy.
    [InlineData(DeviceStatus.Error, DeviceFaultClassifier.CmProbDisabled, false)]          // code 22 excludes
    [InlineData(DeviceStatus.Disabled, DeviceFaultClassifier.CmProbDisabled, false)]
    [InlineData(DeviceStatus.Disabled, null, false)]                                       // non-Windows disabled
    [InlineData(DeviceStatus.Disabled, DeviceFaultClassifier.CmProbFailedStart, false)]    // status Disabled wins
    // Not a fault: OS reports no problem, or healthy-present.
    [InlineData(DeviceStatus.Error, DeviceFaultClassifier.CmProbNone, false)]              // code 0 wins over stale status
    [InlineData(DeviceStatus.OK, DeviceFaultClassifier.CmProbNone, false)]
    [InlineData(DeviceStatus.OK, null, false)]
    [InlineData(DeviceStatus.Unknown, null, false)]                                        // BT paired, out of range
    [InlineData(DeviceStatus.Unknown, DeviceFaultClassifier.CmProbNone, false)]
    public void FaultClassifier_ClassifiesStatusAndProblemCode(DeviceStatus status, int? problemCode, bool expected)
        => Assert.Equal(expected, DeviceFaultClassifier.IsResettableFault(status, problemCode));

    [Fact]
    public void FaultClassifier_DeviceInfoOverload_ReadsRawStatusProblemCode()
    {
        // The DeviceInfo overload reads the Windows CM problem code from Properties["RawStatus"].
        Assert.True(DeviceFaultClassifier.IsResettableFault(
            MakeFaultedDevice(DeviceStatus.Error, DeviceFaultClassifier.CmProbFailedPostStart)));
        Assert.False(DeviceFaultClassifier.IsResettableFault(
            MakeFaultedDevice(DeviceStatus.Disabled, DeviceFaultClassifier.CmProbDisabled)));
        Assert.False(DeviceFaultClassifier.IsResettableFault(
            MakeFaultedDevice(DeviceStatus.OK, DeviceFaultClassifier.CmProbNone)));

        // ReadProblemCode returns the int, or null when the key is absent.
        Assert.Equal(DeviceFaultClassifier.CmProbFailedPostStart,
            DeviceFaultClassifier.ReadProblemCode(MakeFaultedDevice(DeviceStatus.Error, 21)));
        Assert.Null(DeviceFaultClassifier.ReadProblemCode(MakeFaultedDevice(DeviceStatus.Error, problemCode: null)));
    }

    // -- Teardown races (#259) ------------------------------------------
    //
    // "_tracker.StateChanged -= OnTrackerStateChanged" in DisposeAsync does not
    // retract a handler that is already running, and every detached loop in the
    // proxy *checks* _disposed rather than awaiting quiescence. So a detached
    // open/close can reach the open lock after DisposeAsync has already returned.
    // That has to unwind quietly: those tasks are fire-and-forget, so anything
    // thrown there resurfaces from the finalizer thread as an unobserved-task
    // AggregateException -- a benign reconnect reported to the host as a crash.

    private static readonly Type ProxyBaseType = typeof(DeviceProxyBase<FakeDevice, Exception>);

    private static T GetPrivateField<T>(object target, string name)
        => (T)ProxyBaseType
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(target)!;

    private static object InvokePrivate(object target, string name, params object?[] args)
        => ProxyBaseType
            .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(target, args)!;

    [Fact]
    public async Task Dispose_LeavesOpenLockAndDisposeCtsUsable()
    {
        var (tracker, watcher) = CreateTestInfra();
        var opened = new TaskCompletionSource();
        var handle = new TestHandle(tracker, watcher);
        handle.DeviceOpened += (_, _) => opened.TrySetResult();

        SimulateConnect(tracker, MakeDevice());
        await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await handle.DisposeAsync();

        // Neither field may be disposed: in-flight detached work still reaches both,
        // and there is nothing to gain by disposing them (SemaphoreSlim.Dispose is
        // only required once AvailableWaitHandle is used; _disposeCts is a plain,
        // already-cancelled source).
        var openLock = GetPrivateField<SemaphoreSlim>(handle, "_openLock");
        Assert.True(await openLock.WaitAsync(TimeSpan.FromSeconds(1)));
        openLock.Release();

        var disposeCts = GetPrivateField<CancellationTokenSource>(handle, "_disposeCts");
        Assert.True(disposeCts.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task CloseDevice_AfterDispose_DoesNotThrow()
    {
        // The reported fault: a tracker transition spawns the detached close, the
        // proxy is disposed underneath it, and the close then awaits the lock.
        var (tracker, watcher) = CreateTestInfra();
        var opened = new TaskCompletionSource();
        var handle = new TestHandle(tracker, watcher);
        handle.DeviceOpened += (_, _) => opened.TrySetResult();

        SimulateConnect(tracker, MakeDevice());
        await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await handle.DisposeAsync();

        await (Task)InvokePrivate(handle, "CloseDeviceAsync");
    }

    [Fact]
    public async Task TryOpenDevice_AfterDispose_DoesNotThrow()
    {
        // Same race on the open path: OnTrackerStateChanged's Active branch and the
        // reconnect / faulted-node ladders all detach TryOpenDeviceAsync, which takes
        // the same lock.
        var (tracker, watcher) = CreateTestInfra();
        var handle = new TestHandle(tracker, watcher);

        await handle.DisposeAsync();

        Assert.False(await (Task<bool>)InvokePrivate(
            handle, "TryOpenDeviceAsync", MakeDevice(), false));
    }

    [Fact]
    public async Task OpenCompletingAfterDispose_PublishesNothingAndClosesTheHandle()
    {
        // A derived OpenDeviceAsync is handed the connection token but is not obliged to
        // honour it. One that ignores it can return a live handle after DisposeAsync has
        // set _disposed and parked its own close on the open lock. Nothing may be
        // published off the back of that: no DeviceOpened, no worker, no leaked handle.
        var (tracker, watcher) = CreateTestInfra();
        var openEntered = new TaskCompletionSource();
        var releaseOpen = new TaskCompletionSource();
        FakeDevice? handedOut = null;
        var workerRan = false;
        var openedFired = false;

        var handle = new TestHandle(
            tracker, watcher,
            openDevice: async (_, _) =>
            {
                openEntered.TrySetResult();
                await releaseOpen.Task;          // deliberately ignores the token
                handedOut = new FakeDevice();
                return handedOut;
            },
            whileOpen: (_, _) => { workerRan = true; return Task.CompletedTask; });
        handle.DeviceOpened += (_, _) => openedFired = true;

        SimulateConnect(tracker, MakeDevice());
        await openEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // DisposeAsync runs synchronously up to its first await, so _disposed is already
        // set when this returns; its close then blocks on the lock the open still holds.
        var disposing = handle.DisposeAsync().AsTask();
        releaseOpen.SetResult();
        await disposing.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(openedFired);
        Assert.False(handle.IsOpen);
        Assert.False(workerRan);
        Assert.NotNull(handedOut);
        Assert.True(handedOut!.IsDisposed);   // closed by the open path, not leaked
    }

    // Faults the task only AFTER Forget has returned, and does so in a frame that goes
    // away, so nothing roots the task once the fault lands. An already-faulted task
    // (Task.FromException) would be caught synchronously inside Forget and would prove
    // nothing about the detached lifecycle or the finalizer path.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ForgetATaskThatFaultsAfterDetaching(object handle, Exception fault)
    {
        var gate = new TaskCompletionSource();
        InvokePrivate(handle, "Forget", gate.Task, "test");
        gate.SetException(fault);
    }

    [Fact]
    public async Task Forget_ObservesFaultedTask_SoItIsNeverUnobserved()
    {
        // Identity-matched so a parallel test's unobserved fault cannot fail this one.
        var sentinel = new InvalidOperationException("periphery-259-sentinel");
        var escaped = new List<Exception>();

        void OnUnobserved(object? _, UnobservedTaskExceptionEventArgs e)
        {
            if (!e.Exception.InnerExceptions.Any(x => ReferenceEquals(x, sentinel)))
                return;
            lock (escaped) escaped.Add(e.Exception);
            e.SetObserved();
        }

        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            var (tracker, watcher) = CreateTestInfra();
            await using (var handle = new TestHandle(tracker, watcher))
                ForgetATaskThatFaultsAfterDetaching(handle, sentinel);

            // Let ObserveAsync resume, then force the finalizer pass that would publish
            // the fault had nothing observed it.
            await Task.Delay(100);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Delay(100);

            lock (escaped) Assert.Empty(escaped);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }
    }
}
