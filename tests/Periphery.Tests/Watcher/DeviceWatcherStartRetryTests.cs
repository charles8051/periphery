namespace Periphery.Tests;

/// <summary>
/// A start attempt is transactional: it owns everything it creates until it
/// commits, so a failed start leaves the watcher exactly where it was and the
/// same instance can be started again with its trackers and event subscriptions
/// intact.
/// </summary>
/// <remarks>
/// The watcher is a consumer's entire view of its hardware — every tracker
/// reports through it — so a transient registration failure that could not be
/// retried left an application permanently blind, with its trackers frozen at
/// whatever they last read and no signal that they were stale.
/// </remarks>
public class DeviceWatcherStartRetryTests
{
    private static DeviceInfo MakeDevice(string id, bool isActive = true) =>
        new()
        {
            Id = id,
            Name = id,
            Category = DeviceCategory.Usb,
            IsActive = isActive,
        };

    private static (DeviceWatcher Watcher, FakeDeviceMonitorProvider Monitor) Build(
        params DeviceInfo[] devices
    )
    {
        var monitor = new FakeDeviceMonitorProvider();
        var watcher = new DeviceWatcher(new FakeDeviceProvider(devices), monitor);
        return (watcher, monitor);
    }

    [Fact]
    public async Task AFailedStart_LeavesTheWatcherStartable()
    {
        var (watcher, monitor) = Build(MakeDevice("USB\\1"));
        await using var _ = watcher;

        monitor.FailNextStartWith = new DeviceProviderException("registration failed");

        var thrown = await Assert.ThrowsAsync<DeviceProviderException>(() => watcher.StartAsync());
        Assert.Equal("registration failed", thrown.Message);

        // The whole point: no InvalidOperationException("already been started").
        await watcher.StartAsync();

        Assert.Equal(2, monitor.StartAttempts);
        Assert.Single(watcher.KnownDevices);
    }

    [Fact]
    public async Task AFailedStart_DoesNotLeaveAHandlerAttached()
    {
        var (watcher, monitor) = Build(MakeDevice("USB\\1"));
        await using var _ = watcher;

        monitor.FailNextStartWith = new DeviceProviderException("registration failed");
        await Assert.ThrowsAsync<DeviceProviderException>(() => watcher.StartAsync());

        // A rollback that detached nothing would leave the retry double-subscribed,
        // and every device would be reported twice for the rest of the process.
        Assert.Equal(0, monitor.AppearedSubscriberCount);

        await watcher.StartAsync();
        Assert.Equal(1, monitor.AppearedSubscriberCount);
    }

    [Fact]
    public async Task ARetry_DoesNotDuplicateEvents()
    {
        var (watcher, monitor) = Build(MakeDevice("USB\\1"), MakeDevice("USB\\2"));
        await using var _ = watcher;

        var appeared = new List<string>();
        watcher.Appeared += (_, e) => appeared.Add(e.Device.Id.Value);

        monitor.FailNextStartWith = new DeviceProviderException("registration failed");
        await Assert.ThrowsAsync<DeviceProviderException>(() => watcher.StartAsync());

        // The failed attempt never reached the snapshot, so nothing was raised.
        Assert.Empty(appeared);

        await watcher.StartAsync();
        Assert.Equal(["USB\\1", "USB\\2"], appeared);
    }

    [Fact]
    public async Task AFailedStart_DoesNotDisposeACallerSuppliedProvider()
    {
        var (watcher, monitor) = Build(MakeDevice("USB\\1"));
        await using var _ = watcher;

        monitor.FailNextStartWith = new DeviceProviderException("registration failed");
        await Assert.ThrowsAsync<DeviceProviderException>(() => watcher.StartAsync());

        // The injecting constructor is public, so the provider belongs to the
        // caller. Disposing it on rollback would leave the retry re-using a
        // disposed instance.
        Assert.Equal(0, monitor.DisposeCount);
    }

    [Fact]
    public async Task TrackersAndSubscriptions_SurviveAFailedStart()
    {
        var (watcher, monitor) = Build(MakeDevice("USB\\1"));
        await using var _ = watcher;

        var tracker = watcher.AddTracker(t => t.WithId("USB\\1"), name: "Target");
        var transitions = new List<DeviceActivityStatus>();
        tracker.StateChanged += (_, s) => transitions.Add(s.ActivityStatus);

        monitor.FailNextStartWith = new DeviceProviderException("registration failed");
        await Assert.ThrowsAsync<DeviceProviderException>(() => watcher.StartAsync());

        // Nothing observed yet — the attempt failed before the snapshot.
        Assert.Empty(transitions);
        Assert.Equal(DeviceActivityStatus.Unknown, tracker.ActivityStatus);

        // The retry uses the SAME tracker and the SAME subscription. Having to
        // rebuild these is exactly the cost the bug imposed.
        await watcher.StartAsync();

        Assert.Equal(DeviceActivityStatus.Active, tracker.ActivityStatus);
        Assert.Contains(DeviceActivityStatus.Active, transitions);
    }

    [Fact]
    public async Task AFailedStart_LeavesKnownDevicesEmpty()
    {
        var (watcher, monitor) = Build(MakeDevice("USB\\1"));
        await using var _ = watcher;

        monitor.FailNextStartWith = new DeviceProviderException("registration failed");
        await Assert.ThrowsAsync<DeviceProviderException>(() => watcher.StartAsync());

        // KnownDevices documents itself as empty until a start settles.
        Assert.Empty(watcher.KnownDevices);
    }

    [Fact]
    public async Task ASuccessfullyStartedWatcher_StillRejectsASecondStart()
    {
        var (watcher, _) = Build(MakeDevice("USB\\1"));
        await using var __ = watcher;

        await watcher.StartAsync();

        // Retryability applies to a FAILED start. A committed one is still
        // start-once, and the existing contract is unchanged.
        await Assert.ThrowsAsync<InvalidOperationException>(() => watcher.StartAsync());
    }

    [Fact]
    public async Task ACancelledStart_IsAlsoRetryable()
    {
        var (watcher, monitor) = Build(MakeDevice("USB\\1"));
        await using var _ = watcher;

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            watcher.StartAsync(cts.Token)
        );

        await watcher.StartAsync();
        Assert.Single(watcher.KnownDevices);
    }
}
