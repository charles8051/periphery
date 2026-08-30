namespace Periphery.Tests;

/// <summary>
/// Tests for <see cref="DeviceTracker.Reconfigure"/> +
/// <see cref="DeviceTracker.ReplaceProfiles"/> — see ADR-0046.
/// </summary>
public class DeviceTrackerReconfigureTests
{
    private static DeviceInfo MakeDevice(
        string id,
        string? name = null,
        DeviceCategory category = DeviceCategory.Usb,
        bool isActive = true,
        HardwareId? vendorId = null,
        HardwareId? productId = null,
        Guid? containerId = null) => new()
    {
        Id = id,
        Name = name ?? $"Device {id}",
        Category = category,
        IsActive = isActive,
        VendorId = vendorId ?? new HardwareId(0x046D),
        ProductId = productId ?? new HardwareId(0xC52B),
        ContainerId = containerId,
    };

    private static (DeviceWatcher Watcher, FakeDeviceProvider Provider) CreateWatcher(
        params DeviceInfo[] snapshotDevices)
    {
        var provider = new FakeDeviceProvider(snapshotDevices);
        var monitor = new FakeDeviceMonitorProvider();
        var watcher = new DeviceWatcher(provider, monitor);
        return (watcher, provider);
    }

    // ── Validation ─────────────────────────────────────────────────────

    [Fact]
    public void Reconfigure_NullConfigure_Throws()
    {
        var tracker = new DeviceTracker(f => f.OfCategory(DeviceCategory.Usb));

        Assert.Throws<ArgumentNullException>(() => tracker.Reconfigure(null!));
    }

    [Fact]
    public void Reconfigure_EmptyFilter_Throws()
    {
        var tracker = new DeviceTracker(f => f.OfCategory(DeviceCategory.Usb));

        Assert.Throws<ArgumentException>(() => tracker.Reconfigure(_ => { }));
    }

    [Fact]
    public void ReplaceProfiles_Null_Throws()
    {
        var tracker = new DeviceTracker(f => f.OfCategory(DeviceCategory.Usb));

        Assert.Throws<ArgumentNullException>(() => tracker.ReplaceProfiles(null!));
    }

    [Fact]
    public void ReplaceProfiles_Empty_Throws()
    {
        var tracker = new DeviceTracker(f => f.OfCategory(DeviceCategory.Usb));

        Assert.Throws<ArgumentException>(() => tracker.ReplaceProfiles());
    }

    [Fact]
    public void ReplaceProfiles_NullElement_Throws()
    {
        var tracker = new DeviceTracker(f => f.OfCategory(DeviceCategory.Usb));

        Assert.Throws<ArgumentNullException>(() =>
            tracker.ReplaceProfiles(null!, new DeviceProfile(f => f.OfCategory(DeviceCategory.Hid))));
    }

    // ── Unbound tracker — no replay (filter swap only) ────────────────

    [Fact]
    public void Reconfigure_OnUnboundTracker_IsLegal_DoesNotThrow()
    {
        var tracker = new DeviceTracker(f => f.OfCategory(DeviceCategory.Usb));

        var ex = Record.Exception(() => tracker.Reconfigure(f => f.OfCategory(DeviceCategory.Hid)));

        Assert.Null(ex);
    }

    [Fact]
    public void Reconfigure_OnUnboundTracker_DoesNotFireStateChanged()
    {
        // A reconfigure that does not change the resolved state fires nothing.
        // ADR-0056: a fresh tracker is now Unknown, so to exercise the "no net
        // change → no emission" invariant the tracker must first be resolved to
        // its determined state (Absent). Then reconfiguring to another no-match
        // filter is Absent → Absent: no StateChanged.
        var tracker = new DeviceTracker(f => f.OfCategory(DeviceCategory.Usb));
        tracker.OnInitialEnumerationComplete();
        Assert.Equal(DeviceActivityStatus.Absent, tracker.ActivityStatus);

        var fires = 0;
        tracker.StateChanged += (_, _) => fires++;

        tracker.Reconfigure(f => f.OfCategory(DeviceCategory.Hid));

        Assert.Equal(0, fires);
    }

    [Fact]
    public void Reconfigure_OnUnknownUnboundTracker_ResolvesToAbsent_FiresOnce()
    {
        // ADR-0056 consequence: reconfiguring a never-enumerated (Unknown) tracker
        // is its first determination — it resolves Unknown → Absent (no owner, so
        // empty latches) and fires StateChanged exactly once. This is correct: the
        // reconfigure is the resolving event, not a no-op filter swap.
        var tracker = new DeviceTracker(f => f.OfCategory(DeviceCategory.Usb));
        Assert.Equal(DeviceActivityStatus.Unknown, tracker.ActivityStatus);

        var fires = 0;
        tracker.StateChanged += (_, _) => fires++;

        tracker.Reconfigure(f => f.OfCategory(DeviceCategory.Hid));

        Assert.Equal(DeviceActivityStatus.Absent, tracker.ActivityStatus);
        Assert.Equal(1, fires);
    }

    // ── Bound tracker — basic rebind via watcher ──────────────────────

    [Fact]
    public async Task Reconfigure_RebindsToNewDeviceWhenFilterChanges()
    {
        var idA = new Guid("11111111-1111-1111-1111-111111111111");
        var idB = new Guid("22222222-2222-2222-2222-222222222222");
        var deviceA = MakeDevice(id: "USB\\A", name: "CameraA", containerId: idA);
        var deviceB = MakeDevice(id: "USB\\B", name: "CameraB", containerId: idB);
        var (watcher, _) = CreateWatcher(deviceA, deviceB);
        var tracker = watcher.AddTracker(f => f.WithContainerId(idA), "TestTracker");

        await watcher.StartAsync();
        Assert.Equal("USB\\A", tracker.Device?.Id);

        tracker.Reconfigure(f => f.WithContainerId(idB));

        Assert.Equal("USB\\B", tracker.Device?.Id);
        await watcher.DisposeAsync();
    }

    [Fact]
    public async Task Reconfigure_FiresStateChangedExactlyOnce_WhenDeviceSwaps()
    {
        var idA = new Guid("11111111-1111-1111-1111-111111111111");
        var idB = new Guid("22222222-2222-2222-2222-222222222222");
        var (watcher, _) = CreateWatcher(
            MakeDevice(id: "USB\\A", containerId: idA),
            MakeDevice(id: "USB\\B", containerId: idB));
        var tracker = watcher.AddTracker(f => f.WithContainerId(idA));
        await watcher.StartAsync();

        var fires = 0;
        tracker.StateChanged += (_, _) => fires++;
        tracker.Reconfigure(f => f.WithContainerId(idB));

        Assert.Equal(1, fires);
        await watcher.DisposeAsync();
    }

    [Fact]
    public async Task Reconfigure_ToNoMatch_DropsBinding()
    {
        var idA = new Guid("11111111-1111-1111-1111-111111111111");
        var (watcher, _) = CreateWatcher(MakeDevice(id: "USB\\A", containerId: idA));
        var tracker = watcher.AddTracker(f => f.WithContainerId(idA));
        await watcher.StartAsync();
        Assert.NotNull(tracker.Device);

        var idNonexistent = new Guid("99999999-9999-9999-9999-999999999999");
        tracker.Reconfigure(f => f.WithContainerId(idNonexistent));

        Assert.Null(tracker.Device);
        Assert.False(tracker.IsActive);
        Assert.False(tracker.IsPresent);
        await watcher.DisposeAsync();
    }

    // ── Reconfigure skips Unknown (ADR-0056, matrix b/b'/d) ────────────

    [Fact]
    public async Task Reconfigure_DeterminedTracker_DoesNotPassThroughUnknown()
    {
        // After StartAsync the tracker is determined (Active). A reconfigure
        // against the warm cache resolves synchronously under _lock and must
        // never publish Unknown — the cache is settled, so a transient Unknown
        // would be a lie. Here the new filter matches a different device, so
        // the net transition is device-swap (still Active), no Unknown.
        var idA = new Guid("11111111-1111-1111-1111-111111111111");
        var idB = new Guid("22222222-2222-2222-2222-222222222222");
        var (watcher, _) = CreateWatcher(
            MakeDevice(id: "USB\\A", containerId: idA),
            MakeDevice(id: "USB\\B", containerId: idB));
        var tracker = watcher.AddTracker(f => f.WithContainerId(idA));
        await watcher.StartAsync();

        var observed = new List<DeviceActivityStatus>();
        tracker.Subscribe(new ListObserver(observed));
        observed.Clear(); // drop the determined-state replay

        tracker.Reconfigure(f => f.WithContainerId(idB));

        Assert.DoesNotContain(DeviceActivityStatus.Unknown, observed);
        await watcher.DisposeAsync();
    }

    [Fact]
    public async Task Reconfigure_ToNoMatch_GoesToAbsentNotUnknown()
    {
        // matrix b': reconfigure a determined tracker onto a no-match filter.
        // Net transition is determined → Absent in one batched emission, never
        // through Unknown.
        var idA = new Guid("11111111-1111-1111-1111-111111111111");
        var (watcher, _) = CreateWatcher(MakeDevice(id: "USB\\A", containerId: idA));
        var tracker = watcher.AddTracker(f => f.WithContainerId(idA));
        await watcher.StartAsync();

        var observed = new List<DeviceActivityStatus>();
        tracker.Subscribe(new ListObserver(observed));
        observed.Clear();

        var idNonexistent = new Guid("99999999-9999-9999-9999-999999999999");
        tracker.Reconfigure(f => f.WithContainerId(idNonexistent));

        Assert.DoesNotContain(DeviceActivityStatus.Unknown, observed);
        Assert.Equal(DeviceActivityStatus.Absent, tracker.ActivityStatus);
        await watcher.DisposeAsync();
    }

    private sealed class ListObserver(List<DeviceActivityStatus> statuses)
        : IObserver<DeviceTrackerState>
    {
        public void OnNext(DeviceTrackerState value) => statuses.Add(value.ActivityStatus);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    [Fact]
    public async Task Reconfigure_ToSameMatch_PreservesDeviceIdentity_FiresAtMostOnce()
    {
        // Each Reconfigure constructs a new DeviceProfile instance,
        // so NotifyChanges sees ActiveProfile reference-inequality
        // and fires StateChanged once even when the resolved device
        // doesn't change. Consumers should compare Device.Id (not
        // subscribe blindly to StateChanged) to detect real rebinds.
        var idA = new Guid("11111111-1111-1111-1111-111111111111");
        var (watcher, _) = CreateWatcher(MakeDevice(id: "USB\\A", containerId: idA));
        var tracker = watcher.AddTracker(f => f.WithContainerId(idA));
        await watcher.StartAsync();
        var initialDeviceId = tracker.Device?.Id;
        Assert.Equal("USB\\A", initialDeviceId);

        var fires = 0;
        var observedDeviceIds = new List<string?>();
        tracker.StateChanged += (_, s) =>
        {
            fires++;
            observedDeviceIds.Add(s.Device?.Id);
        };
        tracker.Reconfigure(f => f.WithContainerId(idA));

        Assert.True(fires <= 1, $"StateChanged should fire at most once per Reconfigure; fired {fires} times");
        Assert.Equal("USB\\A", tracker.Device?.Id);
        if (fires == 1)
            Assert.Equal("USB\\A", observedDeviceIds[0]);
        await watcher.DisposeAsync();
    }

    [Fact]
    public async Task Reconfigure_FromNullToMatch_FiresAppeared()
    {
        var idA = new Guid("11111111-1111-1111-1111-111111111111");
        var idNonexistent = new Guid("99999999-9999-9999-9999-999999999999");
        var (watcher, _) = CreateWatcher(MakeDevice(id: "USB\\A", containerId: idA));
        var tracker = watcher.AddTracker(f => f.WithContainerId(idNonexistent));
        await watcher.StartAsync();
        Assert.Null(tracker.Device);

        var appearedCount = 0;
        tracker.Appeared += (_, _) => appearedCount++;
        tracker.Reconfigure(f => f.WithContainerId(idA));

        Assert.Equal(1, appearedCount);
        Assert.Equal("USB\\A", tracker.Device?.Id);
        await watcher.DisposeAsync();
    }

    [Fact]
    public async Task Reconfigure_FromMatchToNoMatch_FiresDisappeared()
    {
        var idA = new Guid("11111111-1111-1111-1111-111111111111");
        var (watcher, _) = CreateWatcher(MakeDevice(id: "USB\\A", containerId: idA));
        var tracker = watcher.AddTracker(f => f.WithContainerId(idA));
        await watcher.StartAsync();

        var disappearedCount = 0;
        tracker.Disappeared += (_, _) => disappearedCount++;
        tracker.Reconfigure(f => f.WithContainerId(
            new Guid("99999999-9999-9999-9999-999999999999")));

        Assert.Equal(1, disappearedCount);
        Assert.Null(tracker.Device);
        await watcher.DisposeAsync();
    }

    [Fact]
    public async Task Reconfigure_DeviceSwap_DoesNotFireAppearedOrDisappeared()
    {
        // Both before + after states have IsPresent == true → no
        // Appeared/Disappeared edge events. StateChanged still fires
        // (verified separately) — that's the signal consumers use to
        // detect bind-identity changes via Reconfigure.
        var idA = new Guid("11111111-1111-1111-1111-111111111111");
        var idB = new Guid("22222222-2222-2222-2222-222222222222");
        var (watcher, _) = CreateWatcher(
            MakeDevice(id: "USB\\A", containerId: idA),
            MakeDevice(id: "USB\\B", containerId: idB));
        var tracker = watcher.AddTracker(f => f.WithContainerId(idA));
        await watcher.StartAsync();

        var appearedCount = 0;
        var disappearedCount = 0;
        tracker.Appeared += (_, _) => appearedCount++;
        tracker.Disappeared += (_, _) => disappearedCount++;
        tracker.Reconfigure(f => f.WithContainerId(idB));

        Assert.Equal(0, appearedCount);
        Assert.Equal(0, disappearedCount);
        Assert.Equal("USB\\B", tracker.Device?.Id);
        await watcher.DisposeAsync();
    }

    [Fact]
    public async Task ReplaceProfiles_AppliesFirstMatchingProfile()
    {
        var idA = new Guid("11111111-1111-1111-1111-111111111111");
        var idB = new Guid("22222222-2222-2222-2222-222222222222");
        var (watcher, _) = CreateWatcher(
            MakeDevice(id: "USB\\B", containerId: idB));
        var tracker = watcher.AddTracker(f => f.WithContainerId(idA));
        await watcher.StartAsync();
        Assert.Null(tracker.Device);

        // Multi-profile reconfigure: try idA first (won't match), then idB
        tracker.ReplaceProfiles(
            new DeviceProfile(f => f.WithContainerId(idA), name: "primary"),
            new DeviceProfile(f => f.WithContainerId(idB), name: "fallback"));

        Assert.Equal("USB\\B", tracker.Device?.Id);
        Assert.Equal("fallback", tracker.ActiveProfile?.Name);
        await watcher.DisposeAsync();
    }

    // ── Identity stability across reconfigure ─────────────────────────

    [Fact]
    public async Task Reconfigure_PreservesTrackerIdentity()
    {
        // Subscriptions + event handlers attached BEFORE Reconfigure
        // must still fire AFTER — the tracker reference is stable.
        var idA = new Guid("11111111-1111-1111-1111-111111111111");
        var idB = new Guid("22222222-2222-2222-2222-222222222222");
        var (watcher, _) = CreateWatcher(
            MakeDevice(id: "USB\\A", containerId: idA),
            MakeDevice(id: "USB\\B", containerId: idB));
        var tracker = watcher.AddTracker(f => f.WithContainerId(idA), "Stable");
        await watcher.StartAsync();

        var stateChanges = new List<string?>();
        tracker.StateChanged += (_, s) => stateChanges.Add(s.Device?.Id);
        tracker.Reconfigure(f => f.WithContainerId(idB));

        Assert.Single(stateChanges);
        Assert.Equal("USB\\B", stateChanges[0]);
        Assert.Equal("Stable", tracker.Name);
        await watcher.DisposeAsync();
    }
}
