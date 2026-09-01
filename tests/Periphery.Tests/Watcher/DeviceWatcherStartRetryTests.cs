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

    private static (
        DeviceWatcher Watcher,
        FakeDeviceMonitorProvider Monitor,
        FakeDeviceProvider Query
    ) BuildAll(params DeviceInfo[] devices)
    {
        var monitor = new FakeDeviceMonitorProvider();
        var query = new FakeDeviceProvider(devices);
        return (new DeviceWatcher(query, monitor), monitor, query);
    }

    private static (DeviceWatcher Watcher, FakeDeviceMonitorProvider Monitor) Build(
        params DeviceInfo[] devices
    )
    {
        var (watcher, monitor, _) = BuildAll(devices);
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

    // ── Failures after the registration succeeded ──────────────────────

    [Fact]
    public async Task ASnapshotFailure_IsRetryable_WhenTheWatcherOwnsTheProvider()
    {
        // The production path: the watcher mints its own provider per attempt,
        // so it owns one and disposes it on rollback. Registration SUCCEEDS here
        // and the snapshot is what fails — the case a registration-only test
        // cannot reach, and the one a reviewer correctly flagged as untested.
        var query = new FakeDeviceProvider(MakeDevice("USB\\1"), MakeDevice("USB\\2"));
        var minted = new List<FakeDeviceMonitorProvider>();
        await using var watcher = new DeviceWatcher(
            query,
            () =>
            {
                var m = new FakeDeviceMonitorProvider();
                minted.Add(m);
                return m;
            }
        );

        query.FailEnumerationWith = new InvalidOperationException("enumeration failed");
        query.FailAfterYielding = 1;

        await Assert.ThrowsAsync<InvalidOperationException>(() => watcher.StartAsync());

        // Rollback disposed the provider the attempt created.
        Assert.Single(minted);
        Assert.Equal(1, minted[0].DisposeCount);

        // So the retry mints a fresh one and succeeds, even though the first
        // attempt got as far as a completed registration.
        await watcher.StartAsync();

        Assert.Equal(2, minted.Count);
        Assert.Equal(2, watcher.KnownDevices.Count);
    }

    [Fact]
    public async Task ASnapshotFailure_WithACallerSuppliedProvider_IsNotRetryable()
    {
        // Known limitation, pinned rather than left to be discovered.
        //
        // A caller-supplied provider belongs to the caller, so rollback will not
        // dispose it — disposing would leave the retry using a disposed instance.
        // But IDeviceMonitorProvider has no stop or reset, and every real
        // implementation latches its start ("Dispose and create a new monitor to
        // restart"). So once the registration has succeeded, a later failure
        // leaves that provider started and the retry cannot re-register it.
        //
        // Production is unaffected: there _monitorOverride is null, the watcher
        // owns the provider, and the test above covers it.
        var (watcher, _, query) = BuildAll(MakeDevice("USB\\1"), MakeDevice("USB\\2"));
        await using var _ = watcher;

        query.FailEnumerationWith = new InvalidOperationException("enumeration failed");
        query.FailAfterYielding = 1;

        await Assert.ThrowsAsync<InvalidOperationException>(() => watcher.StartAsync());

        var second = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            watcher.StartAsync()
        );
        Assert.Contains("Already started", second.Message, StringComparison.Ordinal);
    }
}
