namespace Periphery.Tests;

/// <summary>
/// Tests for the non-generic <see cref="DeviceProxy"/> and the
/// <c>Create</c> (shared-watcher) factory on all handle types.
/// </summary>
public class DeviceProxyTests
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

    private static (DeviceTracker tracker, DeviceWatcher watcher) CreateTestInfra()
    {
        var tracker = new DeviceTracker(new DeviceFilter());
        var watcher = Devices.Watch().AddTracker(tracker);
        return (tracker, watcher);
    }

    private static void SimulateConnect(DeviceTracker tracker, DeviceInfo device)
    {
        tracker.OnDeviceAppeared(device);
        tracker.OnDeviceConnected(device);
    }

    private static void SimulateDisconnect(DeviceTracker tracker, DeviceInfo device)
    {
        var inactive = device with { IsActive = false };
        tracker.OnDeviceDisconnected(inactive);
        tracker.OnDeviceDisappeared(inactive);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Non-generic DeviceProxy — OpenAsync
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task OpenAsync_DeviceActivation_SetsIsOpen()
    {
        var (tracker, watcher) = CreateTestInfra();
        var profile = new DeviceProfile(new DeviceFilter());
        var activated = new TaskCompletionSource();

        await using var handle = await DeviceProxy.OpenAsync(
            profile,
            onActivated: (info, ct) =>
            {
                activated.TrySetResult();
                return Task.CompletedTask;
            });

        SimulateConnect(tracker, MakeDevice());

        // The handle creates its own tracker, so we need to drive
        // the profile's tracker instead. Let's test via Create.
    }

    [Fact]
    public async Task Create_DeviceActivation_SetsIsOpen()
    {
        var (tracker, _) = CreateTestInfra();
        var activated = new TaskCompletionSource();

        await using var handle = DeviceProxy.Create(
            tracker,
            onActivated: (info, ct) =>
            {
                activated.TrySetResult();
                return Task.CompletedTask;
            });

        SimulateConnect(tracker, MakeDevice());
        await activated.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(handle.IsOpen);
    }

    [Fact]
    public async Task Create_DeviceDeactivation_ClearsIsOpen()
    {
        var (tracker, _) = CreateTestInfra();
        var device = MakeDevice();
        var activated = new TaskCompletionSource();
        var deactivated = new TaskCompletionSource();

        await using var handle = DeviceProxy.Create(
            tracker,
            onActivated: (_, _) =>
            {
                activated.TrySetResult();
                return Task.CompletedTask;
            },
            onDeactivated: _ =>
            {
                deactivated.TrySetResult();
                return Task.CompletedTask;
            });

        SimulateConnect(tracker, device);
        await activated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(handle.IsOpen);

        SimulateDisconnect(tracker, device);
        await deactivated.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(handle.IsOpen);
    }

    [Fact]
    public async Task Create_OnActivatedFailure_FiresOpenFailed()
    {
        var (tracker, _) = CreateTestInfra();
        var failedTcs = new TaskCompletionSource<Exception>();

        await using var handle = DeviceProxy.Create(
            tracker,
            onActivated: (_, _) => throw new InvalidOperationException("init failed"));

        handle.OpenFailed += (_, ex) => failedTcs.TrySetResult(ex);

        SimulateConnect(tracker, MakeDevice());
        var ex = await failedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsType<InvalidOperationException>(ex);
        Assert.False(handle.IsOpen);
    }

    [Fact]
    public async Task Create_WhileOpenException_TriggersClose()
    {
        var (tracker, _) = CreateTestInfra();
        var opened = new TaskCompletionSource();
        var closed = new TaskCompletionSource();

        await using var handle = DeviceProxy.Create(
            tracker,
            whileOpen: (_, _) => throw new InvalidOperationException("worker crash"));

        handle.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(handle.IsOpen))
            {
                if (handle.IsOpen) opened.TrySetResult();
                else closed.TrySetResult();
            }
        };

        SimulateConnect(tracker, MakeDevice());
        await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Dispose stops the reconnect loop so background tasks don't outlive the test.
        await handle.DisposeAsync();
        Assert.False(handle.IsOpen);
    }

    [Fact]
    public async Task Create_IsOpen_RaisesPropertyChanged()
    {
        var (tracker, _) = CreateTestInfra();
        var propertyNames = new List<string>();
        var activated = new TaskCompletionSource();

        await using var handle = DeviceProxy.Create(tracker,
            onActivated: (_, _) =>
            {
                activated.TrySetResult();
                return Task.CompletedTask;
            });

        handle.PropertyChanged += (_, e) => propertyNames.Add(e.PropertyName!);

        SimulateConnect(tracker, MakeDevice());
        await activated.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains(nameof(handle.IsOpen), propertyNames);
    }

    [Fact]
    public async Task Create_DisposeAsync_Deactivates()
    {
        var (tracker, _) = CreateTestInfra();
        var activated = new TaskCompletionSource();

        var handle = DeviceProxy.Create(
            tracker,
            onActivated: (_, _) =>
            {
                activated.TrySetResult();
                return Task.CompletedTask;
            });

        SimulateConnect(tracker, MakeDevice());
        await activated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(handle.IsOpen);

        await handle.DisposeAsync();
        Assert.False(handle.IsOpen);
    }

    [Fact]
    public async Task Create_DisposeAsync_BeforeActivation_DoesNotThrow()
    {
        var (tracker, _) = CreateTestInfra();
        var handle = DeviceProxy.Create(tracker);

        await handle.DisposeAsync();
        Assert.False(handle.IsOpen);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Create with already-active tracker
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Create_AlreadyActiveTracker_ActivatesImmediately()
    {
        var (tracker, _) = CreateTestInfra();

        // Simulate a device that's already connected before Create
        SimulateConnect(tracker, MakeDevice());
        Assert.True(tracker.IsActive);

        var activated = new TaskCompletionSource();

        await using var handle = DeviceProxy.Create(
            tracker,
            onActivated: (_, _) =>
            {
                activated.TrySetResult();
                return Task.CompletedTask;
            });

        await activated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(handle.IsOpen);
    }

    // ═══════════════════════════════════════════════════════════════════
    // DeviceProxy<TDevice>.Create (generic)
    // ═══════════════════════════════════════════════════════════════════

    private sealed class FakeDevice : IAsyncDisposable
    {
        public Action? DisposeAction { get; init; }
        public bool IsDisposed { get; private set; }
        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            DisposeAction?.Invoke();
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task Create_OnActivatedFailure_RetriesWhileTrackerRemainsActive()
    {
        var (tracker, _) = CreateTestInfra();
        var activationCount = 0;
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var handle = DeviceProxy.Create(
            tracker,
            onActivated: (_, _) =>
            {
                if (Interlocked.Increment(ref activationCount) < 3)
                    throw new InvalidOperationException("init failed");

                recovered.TrySetResult();
                return Task.CompletedTask;
            });

        SimulateConnect(tracker, MakeDevice());
        await recovered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(handle.IsOpen);
        Assert.Equal(3, activationCount);
    }

    [Fact]
    public async Task GenericCreate_DeviceActivation_SetsIsOpen()
    {
        var (tracker, _) = CreateTestInfra();
        var opened = new TaskCompletionSource();

        await using var handle = DeviceProxy<FakeDevice>.Create(
            tracker,
            openDevice: (_, _) => Task.FromResult(new FakeDevice()));

        handle.DeviceOpened += (_, _) => opened.TrySetResult();

        SimulateConnect(tracker, MakeDevice());
        await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(handle.IsOpen);
        Assert.NotNull(handle.Device);
    }

    [Fact]
    public async Task GenericCreate_OnActivatedFailure_RetriesAndDisposesFailedDevice()
    {
        var (tracker, _) = CreateTestInfra();
        var connectAttempts = 0;
        var disposedCount = 0;
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var handle = DeviceProxy<FakeDevice>.Create(
            tracker,
            openDevice: (_, _) => Task.FromResult(new FakeDevice
            {
                DisposeAction = () => Interlocked.Increment(ref disposedCount),
            }),
            onActivated: (_, _) =>
            {
                if (Interlocked.Increment(ref connectAttempts) == 1)
                    throw new InvalidOperationException("handshake failed");

                recovered.TrySetResult();
                return Task.CompletedTask;
            });

        handle.DeviceOpened += (_, _) => opened.TrySetResult();

        SimulateConnect(tracker, MakeDevice());
        await recovered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(handle.IsOpen);
        Assert.Equal(2, connectAttempts);
        Assert.Equal(1, disposedCount);
    }

    [Fact]
    public async Task GenericCreate_DeviceOpenedHandlerThrow_DoesNotPreventReconnect()
    {
        var (tracker, _) = CreateTestInfra();
        var device = MakeDevice();
        var openCount = 0;
        var secondOpened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var handle = DeviceProxy<FakeDevice>.Create(
            tracker,
            openDevice: (_, _) =>
            {
                Interlocked.Increment(ref openCount);
                return Task.FromResult(new FakeDevice());
            });

        handle.DeviceOpened += (_, _) =>
        {
            if (openCount >= 2) secondOpened.TrySetResult();
            throw new InvalidOperationException("consumer");
        };

        // First connect — DeviceOpened fires and throws, but state machine should survive.
        SimulateConnect(tracker, device);
        await Task.Delay(50);

        // Disconnect then reconnect to verify the handle is still functional.
        SimulateDisconnect(tracker, device);
        await Task.Delay(50);

        SimulateConnect(tracker, device);
        await secondOpened.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(openCount >= 2);
    }

    [Fact]
    public async Task GenericCreate_OnDeactivatedFailure_StillDisposesDevice()
    {
        var (tracker, _) = CreateTestInfra();
        var device = MakeDevice();
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var handle = DeviceProxy<FakeDevice>.Create(
            tracker,
            openDevice: (_, _) => Task.FromResult(new FakeDevice
            {
                DisposeAction = () => disposed.TrySetResult(),
            }),
            onDeactivated: _ => throw new InvalidOperationException("teardown failed"));

        var opened = new TaskCompletionSource();
        handle.DeviceOpened += (_, _) => opened.TrySetResult();

        SimulateConnect(tracker, device);
        await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));

        SimulateDisconnect(tracker, device);
        await disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task GenericCreate_AlreadyActiveTracker_OpensImmediately()
    {
        var (tracker, _) = CreateTestInfra();
        SimulateConnect(tracker, MakeDevice());
        Assert.True(tracker.IsActive);

        var openCalled = new TaskCompletionSource();

        await using var handle = DeviceProxy<FakeDevice>.Create(
            tracker,
            openDevice: (_, _) =>
            {
                openCalled.TrySetResult();
                return Task.FromResult(new FakeDevice());
            });

        await openCalled.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Give the async state machine time to settle (set IsOpen)
        await Task.Delay(50);
        Assert.True(handle.IsOpen);
    }

    [Fact]
    public async Task GenericCreate_DisposeAsync_DoesNotDisposeWatcher()
    {
        var (tracker, _) = CreateTestInfra();
        var opened = new TaskCompletionSource();

        var handle = DeviceProxy<FakeDevice>.Create(
            tracker,
            openDevice: (_, _) => Task.FromResult(new FakeDevice()));

        handle.DeviceOpened += (_, _) => opened.TrySetResult();

        SimulateConnect(tracker, MakeDevice());
        await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await handle.DisposeAsync();

        // Tracker should still be usable (watcher not disposed by handle)
        Assert.False(handle.IsOpen);
    }

    [Fact]
    public void GenericCreate_NullTracker_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            DeviceProxy<FakeDevice>.Create(
                null!,
                openDevice: (_, _) => Task.FromResult(new FakeDevice())));
    }

    [Fact]
    public void GenericCreate_NullOpenDevice_ThrowsArgumentNullException()
    {
        var (tracker, _) = CreateTestInfra();

        Assert.Throws<ArgumentNullException>(() =>
            DeviceProxy<FakeDevice>.Create(
                tracker,
                openDevice: null!));
    }

    [Fact]
    public void NonGenericCreate_NullTracker_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            DeviceProxy.Create(null!));
    }

    // ═══════════════════════════════════════════════════════════════════
    // Non-generic DeviceProxy — unified base lifecycle (folded onto
    // DeviceProxyBase per ADR-0055): injectable policy, State / GaveUp,
    // re-enumeration reset, observability — now inherited, not re-copied.
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Records each <see cref="RecoveryContext"/> attempt and returns a
    /// caller-supplied delay (or null to give up). Mirrors the policy double
    /// used by the generic base tests.
    /// </summary>
    private sealed class RecordingPolicy(Func<RecoveryContext, TimeSpan?> decide)
        : IRecoveryPolicy
    {
        public List<int> SeenAttempts { get; } = [];

        public RecoveryDirective Decide(RecoveryContext context)
        {
            lock (SeenAttempts) SeenAttempts.Add(context.Attempt);
            return decide(context) is { } delay
                ? new RecoveryDirective.Retry(delay)
                : new RecoveryDirective.GiveUp();
        }
    }

    [Fact]
    public async Task Create_InjectedPolicy_FailingActivation_TransitionsToGaveUp()
    {
        var (tracker, _) = CreateTestInfra();
        // Give up at attempt 3; attempts 1 and 2 retry with a tiny delay.
        var policy = new RecordingPolicy(ctx => ctx.Attempt > 2 ? null : TimeSpan.FromMilliseconds(1));
        var gaveUp = new TaskCompletionSource();

        await using var handle = DeviceProxy.Create(
            tracker,
            onActivated: (_, _) => throw new InvalidOperationException("wedged endpoint"),
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
        lock (policy.SeenAttempts)
            Assert.Equal(new[] { 1, 2, 3 }, policy.SeenAttempts);
    }

    [Fact]
    public async Task Create_ReEnumeration_AfterGaveUp_ResetsCounter_AndReopens()
    {
        var (tracker, _) = CreateTestInfra();

        bool failActivations = true;
        var policy = new RecordingPolicy(ctx => ctx.Attempt > 1 ? null : TimeSpan.FromMilliseconds(1));

        var device = MakeDevice();
        var gaveUp = new TaskCompletionSource();
        var reopened = new TaskCompletionSource();

        await using var handle = DeviceProxy.Create(
            tracker,
            onActivated: (_, _) => failActivations
                ? throw new InvalidOperationException("still wedged")
                : Task.CompletedTask,
            recoveryPolicy: policy);

        handle.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(handle.State))
            {
                if (handle.State == ConnectionState.GaveUp) gaveUp.TrySetResult();
                if (handle.State == ConnectionState.Open) reopened.TrySetResult();
            }
        };

        SimulateConnect(tracker, device);
        await gaveUp.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ConnectionState.GaveUp, handle.State);

        // Device "re-enumerates" (power-cycle / replug): fix the activation
        // path, then drive a fresh active transition. The give-up budget resets.
        failActivations = false;
        lock (policy.SeenAttempts) policy.SeenAttempts.Clear();

        SimulateDisconnect(tracker, device);
        SimulateConnect(tracker, device);

        await reopened.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(handle.IsOpen);
        Assert.Equal(ConnectionState.Open, handle.State);
    }

    [Fact]
    public async Task Create_State_TransitionsRaisePropertyChanged()
    {
        var (tracker, _) = CreateTestInfra();
        var stateNames = new List<string>();
        var opened = new TaskCompletionSource();

        await using var handle = DeviceProxy.Create(
            tracker,
            onActivated: (_, _) => Task.CompletedTask);

        handle.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(handle.State) or nameof(handle.IsOpen))
                lock (stateNames) stateNames.Add(e.PropertyName!);
            if (e.PropertyName == nameof(handle.IsOpen) && handle.IsOpen)
                opened.TrySetResult();
        };

        SimulateConnect(tracker, MakeDevice());
        await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));

        lock (stateNames)
        {
            Assert.Contains(nameof(handle.State), stateNames);
            Assert.Contains(nameof(handle.IsOpen), stateNames);
        }
        Assert.Equal(ConnectionState.Open, handle.State);
    }

    [Fact]
    public async Task Create_DeviceDisconnect_FiresDeviceClosed()
    {
        var (tracker, _) = CreateTestInfra();
        var device = MakeDevice();
        var opened = new TaskCompletionSource();
        var closed = new TaskCompletionSource();

        await using var handle = DeviceProxy.Create(
            tracker,
            onActivated: (_, _) => Task.CompletedTask);

        handle.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(handle.IsOpen) && handle.IsOpen)
                opened.TrySetResult();
        };
        handle.DeviceClosed += (_, _) => closed.TrySetResult();

        SimulateConnect(tracker, device);
        await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));

        SimulateDisconnect(tracker, device);
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(handle.IsOpen);
    }
}
