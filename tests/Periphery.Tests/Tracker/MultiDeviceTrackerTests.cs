namespace Periphery.Tests;

public class MultiDeviceTrackerTests
{
    private static DeviceInfo MakeDevice(
        string id = "USB\\VID_046D&PID_C52B\\1",
        string? name = "Logitech Mouse",
        DeviceCategory category = DeviceCategory.Usb,
        bool isActive = true) => new()
    {
        Id = id,
        Name = name,
        Category = category,
        IsActive = isActive,
        VendorId = new HardwareId(0x046D),
        ProductId = new HardwareId(0xC52B),
    };

    private static MultiDeviceTracker CreateMultiTracker(
        Action<DeviceFilter>? configure = null, string? name = null)
    {
        var filter = new DeviceFilter();
        configure?.Invoke(filter);
        return new MultiDeviceTracker(filter, name);
    }

    private static (MultiDeviceTracker group, DeviceWatcher watcher) CreateBound(
        Action<DeviceFilter>? configure = null, string? name = null)
    {
        var group = CreateMultiTracker(configure, name);
        var watcher = Devices.Watch().AddMultiTracker(group);
        return (group, watcher);
    }

    // ── Default state ──────────────────────────────────────────────────

    [Fact]
    public void NewGroupTracker_HasNoChildren()
    {
        var group = CreateMultiTracker();

        Assert.Empty(group.Trackers);
        Assert.Equal(0, group.Count);
        Assert.False(group.HasAny);
    }

    // ── Child creation on first appearance ─────────────────────────────

    [Fact]
    public void OnDeviceAppeared_CreatesChildTracker()
    {
        var (group, _) = CreateBound();
        var device = MakeDevice();

        group.OnDeviceAppeared(device);

        Assert.Single(group.Trackers);
        Assert.True(group.HasAny);
        Assert.True(group.Trackers.ContainsKey(device.Id));
    }

    [Fact]
    public void OnDeviceAppeared_ChildTracker_IsPresent()
    {
        var (group, _) = CreateBound();
        var device = MakeDevice(isActive: false);

        group.OnDeviceAppeared(device);

        var child = group.Trackers[device.Id];
        Assert.True(child.IsPresent);
        Assert.False(child.IsActive);
    }

    [Fact]
    public void OnDeviceAppeared_MultipleDevices_CreatesMultipleChildren()
    {
        var (group, _) = CreateBound();
        var d1 = MakeDevice(id: "USB\\1", name: "Mouse 1");
        var d2 = MakeDevice(id: "USB\\2", name: "Mouse 2");
        var d3 = MakeDevice(id: "USB\\3", name: "Mouse 3");

        group.OnDeviceAppeared(d1);
        group.OnDeviceAppeared(d2);
        group.OnDeviceAppeared(d3);

        Assert.Equal(3, group.Count);
        Assert.True(group.Trackers.ContainsKey("USB\\1"));
        Assert.True(group.Trackers.ContainsKey("USB\\2"));
        Assert.True(group.Trackers.ContainsKey("USB\\3"));
    }

    [Fact]
    public void OnDeviceAppeared_SameDeviceTwice_DoesNotCreateDuplicate()
    {
        var (group, _) = CreateBound();
        var device = MakeDevice();

        group.OnDeviceAppeared(device);
        group.OnDeviceAppeared(device);

        Assert.Single(group.Trackers);
    }

    [Fact]
    public void OnDeviceAppeared_SameIdDifferentCase_DoesNotCreateDuplicate()
    {
        // Device instance IDs are case-insensitive (Windows). The same physical
        // device can re-enumerate with different casing — e.g. a firmware reboot
        // that re-reports its serial in different case — which previously created
        // a phantom second child. Regression for a deployed host
        // "duplicate Available Devices after Treehopper reboot" bug.
        var (group, _) = CreateBound();
        var upper = MakeDevice(id: "USB\\VID_10C4&PID_8A7E\\JQ1KM1AI");
        var mixed = MakeDevice(id: "USB\\VID_10C4&PID_8A7E\\jQ1KM1Ai");

        group.OnDeviceAppeared(upper);
        group.OnDeviceAppeared(mixed);

        // One child (children dict is case-insensitive) AND it's present
        // (the child's WithId filter matched the re-cased id too).
        var child = Assert.Single(group.Trackers);
        Assert.True(child.Value.IsPresent);
    }

    // ── Filter matching ────────────────────────────────────────────────

    [Fact]
    public void OnDeviceAppeared_NonMatchingDevice_DoesNotCreateChild()
    {
        var (group, _) = CreateBound(f => f.OfCategory(DeviceCategory.Monitor));
        var usbDevice = MakeDevice(category: DeviceCategory.Usb);

        group.OnDeviceAppeared(usbDevice);

        Assert.Empty(group.Trackers);
    }

    [Fact]
    public void OnDeviceAppeared_MatchingDevice_CreatesChild()
    {
        var (group, _) = CreateBound(f => f.OfCategory(DeviceCategory.Usb));
        var usbDevice = MakeDevice(category: DeviceCategory.Usb);

        group.OnDeviceAppeared(usbDevice);

        Assert.Single(group.Trackers);
    }

    // ── Activation ─────────────────────────────────────────────────────

    [Fact]
    public void OnDeviceActivated_SetsChildActive()
    {
        var (group, _) = CreateBound();
        var device = MakeDevice();

        group.OnDeviceAppeared(device);
        group.OnDeviceActivated(device);

        var child = group.Trackers[device.Id];
        Assert.True(child.IsActive);
    }

    [Fact]
    public void OnDeviceActivated_WithoutAppeared_CreatesChildAndActivates()
    {
        var (group, _) = CreateBound();
        var device = MakeDevice();

        group.OnDeviceActivated(device);

        Assert.Single(group.Trackers);
        var child = group.Trackers[device.Id];
        Assert.True(child.IsActive);
    }

    // ── Deactivation ───────────────────────────────────────────────────

    [Fact]
    public void OnDeviceDeactivated_ChildBecomesInactive_StaysInGroup()
    {
        var (group, _) = CreateBound();
        var device = MakeDevice();

        group.OnDeviceAppeared(device);
        group.OnDeviceActivated(device);
        group.OnDeviceDeactivated(device);

        Assert.Single(group.Trackers);
        var child = group.Trackers[device.Id];
        Assert.False(child.IsActive);
        Assert.True(child.IsPresent); // still present
    }

    [Fact]
    public void OnDeviceDeactivated_UnknownDevice_DoesNotThrow()
    {
        var (group, _) = CreateBound();

        group.OnDeviceDeactivated(MakeDevice(id: "UNKNOWN"));

        Assert.Empty(group.Trackers);
    }

    // ── Disappear — persistent child ───────────────────────────────────

    [Fact]
    public void OnDeviceDisappeared_ChildBecomesAbsent_StaysInGroup()
    {
        var (group, _) = CreateBound();
        var device = MakeDevice();

        group.OnDeviceAppeared(device);
        group.OnDeviceActivated(device);
        group.OnDeviceDisappeared(device);

        Assert.Single(group.Trackers);
        var child = group.Trackers[device.Id];
        Assert.False(child.IsPresent);
        Assert.False(child.IsActive);
    }

    [Fact]
    public void OnDeviceDisappeared_ThenReappeared_ChildRecovers()
    {
        var (group, _) = CreateBound();
        var device = MakeDevice();

        group.OnDeviceAppeared(device);
        group.OnDeviceActivated(device);
        group.OnDeviceDisappeared(device);

        // Still one child
        Assert.Single(group.Trackers);
        Assert.False(group.Trackers[device.Id].IsActive);

        // Reappear
        group.OnDeviceAppeared(device);
        group.OnDeviceActivated(device);

        Assert.Single(group.Trackers); // still same child, not a new one
        Assert.True(group.Trackers[device.Id].IsActive);
    }

    // ── DeviceAdded event ──────────────────────────────────────────────

    [Fact]
    public void DeviceAdded_FiresOnFirstAppearance()
    {
        var (group, _) = CreateBound();
        DeviceTracker? received = null;
        group.DeviceAdded += (_, tracker) => received = tracker;

        group.OnDeviceAppeared(MakeDevice());

        Assert.NotNull(received);
    }

    [Fact]
    public void DeviceAdded_DoesNotFireOnSecondAppearance()
    {
        var (group, _) = CreateBound();
        var device = MakeDevice();
        group.OnDeviceAppeared(device);

        var count = 0;
        group.DeviceAdded += (_, _) => count++;

        group.OnDeviceDisappeared(device);
        group.OnDeviceAppeared(device); // re-appear — same ID

        Assert.Equal(0, count);
    }

    [Fact]
    public void DeviceAdded_FiresOnce_PerUniqueDevice()
    {
        var (group, _) = CreateBound();
        var received = new List<DeviceTracker>();
        group.DeviceAdded += (_, tracker) => received.Add(tracker);

        group.OnDeviceAppeared(MakeDevice(id: "USB\\1"));
        group.OnDeviceAppeared(MakeDevice(id: "USB\\2"));

        Assert.Equal(2, received.Count);
    }

    [Fact]
    public void DeviceAdded_FiresOnActivated_WhenNoAppearancePreceded()
    {
        var (group, _) = CreateBound();
        DeviceTracker? received = null;
        group.DeviceAdded += (_, tracker) => received = tracker;

        group.OnDeviceActivated(MakeDevice());

        Assert.NotNull(received);
    }

    // ── Property changed forwarding ────────────────────────────────────

    [Fact]
    public void OnDevicePropertyChanged_ForwardsToChild()
    {
        var (group, _) = CreateBound();
        var device = MakeDevice();
        group.OnDeviceAppeared(device);
        group.OnDeviceActivated(device);

        var child = group.Trackers[device.Id];
        DevicePropertyChangedEventArgs? received = null;
        child.PropertyChanged += (_, e) => received = e;

        var updated = device with { Name = "Updated Name" };
        var changed = DeviceInfoDiff.Compute(device, updated);
        group.OnDevicePropertyChanged(device, updated, changed);

        Assert.NotNull(received);
        Assert.Equal("Updated Name", received.Current.Name);
    }

    [Fact]
    public void OnDevicePropertyChanged_NonMatchingDevice_Ignored()
    {
        var (group, _) = CreateBound(f => f.OfCategory(DeviceCategory.Monitor));
        var device = MakeDevice(category: DeviceCategory.Usb);

        // Should not throw even though no child exists
        var updated = device with { Name = "Changed" };
        group.OnDevicePropertyChanged(device, updated, DeviceInfoDiff.Compute(device, updated));

        Assert.Empty(group.Trackers);
    }

    // ── IObservable<DeviceTrackerState> ────────────────────────────────

    [Fact]
    public void Subscribe_ReceivesStateChangesFromChildren()
    {
        var (group, _) = CreateBound();
        var values = new List<DeviceTrackerState>();
        group.Subscribe(new TestObserver(values));

        group.OnDeviceAppeared(MakeDevice(id: "USB\\1"));
        group.OnDeviceActivated(MakeDevice(id: "USB\\1"));

        // Child was created with Appeared, then Activated
        Assert.True(values.Count >= 2);
        Assert.True(values[^1].IsActive);
    }

    [Fact]
    public void Subscribe_LateSubscriber_ReceivesCurrentStateOfAllChildren()
    {
        var (group, _) = CreateBound();
        group.OnDeviceAppeared(MakeDevice(id: "USB\\1"));
        group.OnDeviceActivated(MakeDevice(id: "USB\\1"));
        group.OnDeviceAppeared(MakeDevice(id: "USB\\2", isActive: false));

        var values = new List<DeviceTrackerState>();
        group.Subscribe(new TestObserver(values));

        // Should receive current state of both children
        Assert.Equal(2, values.Count);
    }

    [Fact]
    public void Unsubscribe_StopsReceivingValues()
    {
        var (group, _) = CreateBound();
        var values = new List<DeviceTrackerState>();
        var sub = group.Subscribe(new TestObserver(values));

        group.OnDeviceAppeared(MakeDevice(id: "USB\\1"));
        var countAfterFirst = values.Count;

        sub.Dispose();
        group.OnDeviceAppeared(MakeDevice(id: "USB\\2"));

        Assert.Equal(countAfterFirst, values.Count);
    }

    // ── Bind / Unbind ──────────────────────────────────────────────────

    [Fact]
    public void Bind_ToSecondWatcher_WhileBound_Throws()
    {
        var group = CreateMultiTracker();
        var watcher1 = Devices.Watch();
        var watcher2 = Devices.Watch();

        group.Bind(watcher1);

        Assert.Throws<InvalidOperationException>(() => group.Bind(watcher2));
    }

    [Fact]
    public void Unbind_ClearsAllChildren()
    {
        var (group, _) = CreateBound();
        group.OnDeviceAppeared(MakeDevice(id: "USB\\1"));
        group.OnDeviceAppeared(MakeDevice(id: "USB\\2"));

        group.Unbind();

        Assert.Empty(group.Trackers);
        Assert.Equal(0, group.Count);
    }

    // ── Watcher integration ────────────────────────────────────────────

    [Fact]
    public async Task Watcher_SnapshotCreatesChildren()
    {
        var monitor1 = new DeviceInfo
        {
            Id = "MONITOR\\1",
            Name = "Monitor 1",
            Category = DeviceCategory.Monitor,
            IsActive = true,
        };
        var monitor2 = new DeviceInfo
        {
            Id = "MONITOR\\2",
            Name = "Monitor 2",
            Category = DeviceCategory.Monitor,
            IsActive = true,
        };
        var usbDevice = new DeviceInfo
        {
            Id = "USB\\1",
            Name = "Mouse",
            Category = DeviceCategory.Usb,
            IsActive = true,
        };

        var provider = new FakeDeviceProvider(monitor1, monitor2, usbDevice);
        var monitor = new FakeDeviceMonitorProvider();

        await using var watcher = new DeviceWatcher(provider, monitor);
        var group = watcher.AddMultiTracker(
            f => f.OfCategory(DeviceCategory.Monitor), name: "Monitors");

        await watcher.StartAsync();

        Assert.Equal(2, group.Count);
        Assert.True(group.Trackers.ContainsKey("MONITOR\\1"));
        Assert.True(group.Trackers.ContainsKey("MONITOR\\2"));

        // Both should be active (they were active in the snapshot)
        Assert.True(group.Trackers["MONITOR\\1"].IsActive);
        Assert.True(group.Trackers["MONITOR\\2"].IsActive);
    }

    [Fact]
    public async Task Watcher_RuntimeEvents_CreateAndUpdateChildren()
    {
        var provider = FakeDeviceProvider.Empty();
        var monitorProvider = new FakeDeviceMonitorProvider();

        await using var watcher = new DeviceWatcher(provider, monitorProvider);
        var group = watcher.AddMultiTracker(
            f => f.OfCategory(DeviceCategory.Usb), name: "USBDevices");

        await watcher.StartAsync();
        Assert.Empty(group.Trackers);

        // Simulate a new device appearing
        var device = MakeDevice(id: "USB\\NEW", category: DeviceCategory.Usb);
        monitorProvider.SimulateConnect(device);

        Assert.Single(group.Trackers);
        Assert.True(group.Trackers["USB\\NEW"].IsActive);

        // Simulate disconnect
        monitorProvider.SimulateDisconnect(device);

        // Child persists but goes absent
        Assert.Single(group.Trackers);
        Assert.False(group.Trackers["USB\\NEW"].IsPresent);
    }

    [Fact]
    public async Task Watcher_MixedTrackersAndGroups_WorkTogether()
    {
        var usbDevice = new DeviceInfo
        {
            Id = "USB\\VID_046D&PID_C52B\\1",
            Name = "Logitech Mouse",
            Category = DeviceCategory.Usb,
            IsActive = true,
            VendorId = new HardwareId(0x046D),
            ProductId = new HardwareId(0xC52B),
        };
        var monitor1 = new DeviceInfo
        {
            Id = "MONITOR\\1",
            Name = "Monitor 1",
            Category = DeviceCategory.Monitor,
            IsActive = true,
        };

        var provider = new FakeDeviceProvider(usbDevice, monitor1);
        var monitorProv = new FakeDeviceMonitorProvider();

        await using var watcher = new DeviceWatcher(provider, monitorProv);

        // Static tracker for a known device
        var mouseTracker = watcher.AddTracker(
            f => f.WithUsbId("046D", "C52B"), name: "Mouse");

        // Dynamic group for all monitors
        var monitors = watcher.AddMultiTracker(
            f => f.OfCategory(DeviceCategory.Monitor), name: "Monitors");

        await watcher.StartAsync();

        // Static tracker resolved
        Assert.True(mouseTracker.IsActive);
        Assert.Equal(usbDevice.Id, mouseTracker.Device!.Id);

        // Group tracker has the monitor
        Assert.Single(monitors.Trackers);
        Assert.True(monitors.Trackers["MONITOR\\1"].IsActive);
    }

    [Fact]
    public async Task Watcher_Dispose_UnbindsGroupTrackers()
    {
        var provider = FakeDeviceProvider.Empty();
        var monitorProv = new FakeDeviceMonitorProvider();

        var watcher = new DeviceWatcher(provider, monitorProv);
        var group = watcher.AddMultiTracker(
            f => f.OfCategory(DeviceCategory.Usb), name: "USBDevices");

        await watcher.StartAsync();

        var device = MakeDevice(id: "USB\\1");
        monitorProv.SimulateConnect(device);
        Assert.Single(group.Trackers);

        await watcher.DisposeAsync();

        Assert.Empty(group.Trackers);
    }

    [Fact]
    public async Task Watcher_PropertyChanged_ForwardsToGroup()
    {
        var device = MakeDevice(id: "USB\\1", category: DeviceCategory.Usb);
        var provider = new FakeDeviceProvider(device);
        var monitorProv = new FakeDeviceMonitorProvider();

        await using var watcher = new DeviceWatcher(provider, monitorProv);
        var group = watcher.AddMultiTracker(
            f => f.OfCategory(DeviceCategory.Usb), name: "USBDevices");

        await watcher.StartAsync();
        Assert.Single(group.Trackers);

        var child = group.Trackers["USB\\1"];
        DevicePropertyChangedEventArgs? received = null;
        child.PropertyChanged += (_, e) => received = e;

        var updated = device with { Name = "Updated" };
        monitorProv.SimulatePropertyChange(device, updated);

        Assert.NotNull(received);
        Assert.Equal("Updated", received.Current.Name);
    }

    // ── MultiDeviceTracker via AddMultiTracker factory ──────────────────

    [Fact]
    public void AddMultiTracker_NullConfigure_Throws()
    {
        var watcher = Devices.Watch();

        Assert.Throws<ArgumentNullException>(() =>
            watcher.AddMultiTracker((Action<DeviceFilter>)null!));
    }

    [Fact]
    public void AddMultiTracker_EmptyFilter_Throws()
    {
        var watcher = Devices.Watch();

        Assert.Throws<ArgumentException>(() =>
            watcher.AddMultiTracker(f => { }));
    }

    [Fact]
    public void AddMultiTracker_ExistingInstance_ReturnsSameWatcher()
    {
        var watcher = Devices.Watch();
        var group = new MultiDeviceTracker(f => f.OfCategory(DeviceCategory.Usb));

        var result = watcher.AddMultiTracker(group);

        Assert.Same(watcher, result);
    }

    // ── Bluetooth scenario ─────────────────────────────────────────────

    [Fact]
    public void BluetoothScenario_PairedOutOfRange_PresentNotActive()
    {
        var (group, _) = CreateBound();
        var device = MakeDevice(
            id: "BT\\1",
            category: DeviceCategory.Bluetooth,
            isActive: false);

        group.OnDeviceAppeared(device);

        var child = group.Trackers["BT\\1"];
        Assert.True(child.IsPresent);
        Assert.False(child.IsActive);
    }

    [Fact]
    public void BluetoothScenario_ComesIntoRange_ChildActivates()
    {
        var (group, _) = CreateBound();
        var device = MakeDevice(
            id: "BT\\1",
            category: DeviceCategory.Bluetooth,
            isActive: false);

        group.OnDeviceAppeared(device);
        group.OnDeviceActivated(device with { IsActive = true });

        var child = group.Trackers["BT\\1"];
        Assert.True(child.IsActive);
    }

    [Fact]
    public void BluetoothScenario_GoesOutOfRange_ChildDeactivates_StaysInGroup()
    {
        var (group, _) = CreateBound();
        var device = MakeDevice(
            id: "BT\\1",
            category: DeviceCategory.Bluetooth);

        group.OnDeviceAppeared(device);
        group.OnDeviceActivated(device);
        group.OnDeviceDeactivated(device with { IsActive = false });

        Assert.Single(group.Trackers);
        var child = group.Trackers["BT\\1"];
        Assert.True(child.IsPresent);
        Assert.False(child.IsActive);
    }

    // ── Matches ────────────────────────────────────────────────────────

    [Fact]
    public void Matches_DelegatesToFilter()
    {
        var group = CreateMultiTracker(f => f.OfCategory(DeviceCategory.Monitor));

        Assert.True(group.Matches(MakeDevice(category: DeviceCategory.Monitor)));
        Assert.False(group.Matches(MakeDevice(category: DeviceCategory.Usb)));
    }

    // ── Test helper ────────────────────────────────────────────────────

    private sealed class TestObserver(List<DeviceTrackerState> values)
        : IObserver<DeviceTrackerState>
    {
        public void OnNext(DeviceTrackerState value) => values.Add(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
