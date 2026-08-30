namespace Periphery.Tests;

public class DeviceTrackerTests
{
    private static DeviceInfo MakeDevice(
        string id = "USB\\VID_046D&PID_C52B\\1",
        string? name = "Logitech Mouse",
        DeviceCategory category = DeviceCategory.Usb,
        bool isActive = true,
        HardwareId? vendorId = null,
        HardwareId? productId = null) => new()
    {
        Id = id,
        Name = name,
        Category = category,
        IsActive = isActive,
        VendorId = vendorId ?? new HardwareId(0x046D),
        ProductId = productId ?? new HardwareId(0xC52B),
    };

    private static DeviceTracker CreateTracker(Action<DeviceFilter>? configure = null)
    {
        var filter = new DeviceFilter();
        configure?.Invoke(filter);
        return new DeviceTracker(filter);
    }

    // ── Default state ──────────────────────────────────────────────────

    [Fact]
    public void NewTracker_IsNotPresent_IsNotActive()
    {
        var tracker = CreateTracker();

        Assert.False(tracker.IsPresent);
        Assert.False(tracker.IsActive);
        Assert.Null(tracker.Device);
        Assert.Null(tracker.ActiveProfile);
    }

    [Fact]
    public void NewTracker_StatusIsUnknown()
    {
        // A freshly constructed tracker has not yet been enumerated, so its
        // status is Unknown (the field default), NOT Absent. Absent now means
        // "enumerated and confirmed gone" — see ADR-0056.
        var tracker = CreateTracker();

        Assert.Equal(DeviceActivityStatus.Unknown, tracker.ActivityStatus);
        Assert.False(tracker.IsPresent);
        Assert.False(tracker.IsActive);
        Assert.Null(tracker.Device);
    }

    // ── OnDeviceAppeared ───────────────────────────────────────────────

    [Fact]
    public void OnDeviceAppeared_SetsIsPresentTrue()
    {
        var tracker = CreateTracker();

        tracker.OnDeviceAppeared(MakeDevice());

        Assert.True(tracker.IsPresent);
        Assert.NotNull(tracker.Device);
    }

    [Fact]
    public void OnDeviceAppeared_SetsDevice()
    {
        var tracker = CreateTracker();
        var device = MakeDevice();

        tracker.OnDeviceAppeared(device);

        Assert.Equal(device.Id, tracker.Device!.Id);
    }

    [Fact]
    public void OnDeviceAppeared_DoesNotSetActive()
    {
        var tracker = CreateTracker();

        tracker.OnDeviceAppeared(MakeDevice());

        Assert.False(tracker.IsActive);
        Assert.Equal(DeviceActivityStatus.Present, tracker.ActivityStatus);
    }

    [Fact]
    public void OnDeviceAppeared_SecondDevice_RejectedByLatch()
    {
        var tracker = CreateTracker();
        var first = MakeDevice(id: "USB\\1");
        var second = MakeDevice(id: "USB\\2");

        tracker.OnDeviceAppeared(first);
        tracker.OnDeviceAppeared(second);

        // Latch holds first device; second is silently rejected
        Assert.Equal(first.Id, tracker.Device!.Id);
    }

    // ── OnDeviceConnected ──────────────────────────────────────────────

    [Fact]
    public void OnDeviceConnected_SetsIsActiveTrue()
    {
        var tracker = CreateTracker();

        tracker.OnDeviceConnected(MakeDevice());

        Assert.True(tracker.IsActive);
        Assert.NotNull(tracker.Device);
    }

    [Fact]
    public void OnDeviceConnected_SetsDevice()
    {
        var tracker = CreateTracker();
        var device = MakeDevice();

        tracker.OnDeviceConnected(device);

        Assert.Equal(device.Id, tracker.Device!.Id);
    }

    [Fact]
    public void OnDeviceConnected_WithoutAppeared_SetsIsPresent()
    {
        var tracker = CreateTracker();

        tracker.OnDeviceConnected(MakeDevice());

        Assert.True(tracker.IsPresent);
        Assert.NotNull(tracker.Device);
    }

    [Fact]
    public void OnDeviceConnected_SecondDevice_RejectedByLatch()
    {
        var tracker = CreateTracker();
        var first = MakeDevice(id: "USB\\1");
        var second = MakeDevice(id: "USB\\2");

        tracker.OnDeviceConnected(first);
        tracker.OnDeviceConnected(second);

        // Latch holds first device; second is silently rejected — no ambiguity
        Assert.Equal(first.Id, tracker.Device!.Id);
    }

    // ── OnDeviceDisconnected ───────────────────────────────────────────

    [Fact]
    public void OnDeviceDisconnected_ClearsDevice()
    {
        var tracker = CreateTracker();
        var device = MakeDevice();

        tracker.OnDeviceConnected(device);
        tracker.OnDeviceDisconnected(device);

        Assert.False(tracker.IsActive);
        Assert.Null(tracker.Device);
        Assert.Null(tracker.ActiveProfile);
    }

    [Fact]
    public void OnDeviceDisconnected_ReleasesLatch_AllowsNextDevice()
    {
        var tracker = CreateTracker();
        var first = MakeDevice(id: "USB\\1");
        var second = MakeDevice(id: "USB\\2");

        tracker.OnDeviceConnected(first);
        tracker.OnDeviceDisconnected(first);

        // Latch released — second device can now claim the profile
        tracker.OnDeviceConnected(second);

        Assert.True(tracker.IsActive);
        Assert.Equal(second.Id, tracker.Device!.Id);
    }

    [Fact]
    public void OnDeviceDisconnected_DoesNotAffectPresentState()
    {
        var tracker = CreateTracker();
        var device = MakeDevice();

        tracker.OnDeviceAppeared(device);
        tracker.OnDeviceConnected(device);
        tracker.OnDeviceDisconnected(device);

        Assert.True(tracker.IsPresent);
        Assert.Equal(device.Id, tracker.Device!.Id);
    }

    [Fact]
    public void OnDeviceDisconnected_UnknownDevice_DoesNotThrow()
    {
        var tracker = CreateTracker();

        tracker.OnDeviceDisconnected(MakeDevice(id: "UNKNOWN"));

        Assert.False(tracker.IsActive);
    }

    // ── OnDeviceDisappeared

    [Fact]
    public void OnDeviceDisappeared_ClearsPresentDevice()
    {
        var tracker = CreateTracker();
        var device = MakeDevice();

        tracker.OnDeviceAppeared(device);
        tracker.OnDeviceDisappeared(device);

        Assert.False(tracker.IsPresent);
        Assert.Null(tracker.Device);
    }

    [Fact]
    public void OnDeviceDisappeared_CascadesClearsDevice()
    {
        var tracker = CreateTracker();
        var device = MakeDevice();

        tracker.OnDeviceAppeared(device);
        tracker.OnDeviceConnected(device);
        tracker.OnDeviceDisappeared(device);

        Assert.False(tracker.IsPresent);
        Assert.False(tracker.IsActive);
        Assert.Null(tracker.Device);
    }

    [Fact]
    public void OnDeviceDisappeared_ReleasesLatch_AllowsNextDevice()
    {
        var tracker = CreateTracker();
        var first = MakeDevice(id: "USB\\1");
        var second = MakeDevice(id: "USB\\2");

        tracker.OnDeviceAppeared(first);
        tracker.OnDeviceDisappeared(first);
        tracker.OnDeviceAppeared(second);

        Assert.True(tracker.IsPresent);
        Assert.Equal(second.Id, tracker.Device!.Id);
    }

    // ── USB scenario: Appeared + Connected fire together

    [Fact]
    public void UsbScenario_PlugIn_BothPresentAndActive()
    {
        var tracker = CreateTracker();
        var device = MakeDevice();

        tracker.OnDeviceAppeared(device);
        tracker.OnDeviceConnected(device);

        Assert.True(tracker.IsPresent);
        Assert.True(tracker.IsActive);
        Assert.Equal(device.Id, tracker.Device!.Id);
    }

    [Fact]
    public void UsbScenario_Unplug_NeitherPresentNorActive()
    {
        var tracker = CreateTracker();
        var device = MakeDevice();

        tracker.OnDeviceAppeared(device);
        tracker.OnDeviceConnected(device);
        tracker.OnDeviceDisappeared(device);

        Assert.False(tracker.IsPresent);
        Assert.False(tracker.IsActive);
    }

    // ── Bluetooth scenario: Appeared ≠ Connected ──────────────────────

    [Fact]
    public void BluetoothScenario_PairedButOutOfRange_PresentNotActive()
    {
        var tracker = CreateTracker();
        var device = MakeDevice(category: DeviceCategory.Bluetooth, isActive: false);

        tracker.OnDeviceAppeared(device);

        Assert.True(tracker.IsPresent);
        Assert.False(tracker.IsActive);
    }

    [Fact]
    public void BluetoothScenario_PairedAndInRange_BothTrue()
    {
        var tracker = CreateTracker();
        var device = MakeDevice(category: DeviceCategory.Bluetooth);

        tracker.OnDeviceAppeared(device);
        tracker.OnDeviceConnected(device);

        Assert.True(tracker.IsPresent);
        Assert.True(tracker.IsActive);
    }

    [Fact]
    public void BluetoothScenario_GoesOutOfRange_PresentNotActive()
    {
        var tracker = CreateTracker();
        var device = MakeDevice(category: DeviceCategory.Bluetooth);

        tracker.OnDeviceAppeared(device);
        tracker.OnDeviceConnected(device);
        tracker.OnDeviceDisconnected(device);

        Assert.True(tracker.IsPresent);
        Assert.False(tracker.IsActive);
        Assert.NotNull(tracker.Device);
        Assert.Equal(DeviceActivityStatus.Present, tracker.ActivityStatus);
    }

    [Fact]
    public void BluetoothScenario_ComesBackInRange_Reconnects()
    {
        var tracker = CreateTracker();
        var device = MakeDevice(category: DeviceCategory.Bluetooth);

        tracker.OnDeviceAppeared(device);
        tracker.OnDeviceConnected(device);
        tracker.OnDeviceDisconnected(device);
        tracker.OnDeviceConnected(device);

        Assert.True(tracker.IsActive);
        Assert.Equal(device.Id, tracker.Device!.Id);
    }

    [Fact]
    public void BluetoothScenario_Unpair_NeitherPresentNorActive()
    {
        var tracker = CreateTracker();
        var device = MakeDevice(category: DeviceCategory.Bluetooth);

        tracker.OnDeviceAppeared(device);
        tracker.OnDeviceConnected(device);
        tracker.OnDeviceDisappeared(device);

        Assert.False(tracker.IsPresent);
        Assert.False(tracker.IsActive);
    }

    // ── Multi-profile

    [Fact]
    public void MultiProfile_PrimaryConnects_ResolvedToPrimary()
    {
        var primary = new DeviceProfile(f => f.OfCategory(DeviceCategory.Usb), name: "Primary");
        var fallback = new DeviceProfile(f => f.OfCategory(DeviceCategory.Bluetooth), name: "Fallback");
        var tracker = new DeviceTracker("Test", primary, fallback);
        var usbDevice = MakeDevice(category: DeviceCategory.Usb);

        tracker.OnDeviceConnected(usbDevice);

        Assert.Equal(usbDevice.Id, tracker.Device!.Id);
        Assert.Same(primary, tracker.ActiveProfile);
    }

    [Fact]
    public void MultiProfile_PrimaryAbsent_FallbackConnects_ResolvedToFallback()
    {
        var primary = new DeviceProfile(f => f.OfCategory(DeviceCategory.Usb), name: "Primary");
        var fallback = new DeviceProfile(f => f.OfCategory(DeviceCategory.Bluetooth), name: "Fallback");
        var tracker = new DeviceTracker("Test", primary, fallback);
        var btDevice = MakeDevice(category: DeviceCategory.Bluetooth);

        tracker.OnDeviceConnected(btDevice);

        Assert.Equal(btDevice.Id, tracker.Device!.Id);
        Assert.Same(fallback, tracker.ActiveProfile);
    }

    [Fact]
    public void MultiProfile_PrimaryConnects_OverridesFallback()
    {
        var primary = new DeviceProfile(f => f.OfCategory(DeviceCategory.Usb), name: "Primary");
        var fallback = new DeviceProfile(f => f.OfCategory(DeviceCategory.Bluetooth), name: "Fallback");
        var tracker = new DeviceTracker("Test", primary, fallback);
        var btDevice = MakeDevice(id: "BT\\1", category: DeviceCategory.Bluetooth);
        var usbDevice = MakeDevice(id: "USB\\1", category: DeviceCategory.Usb);

        tracker.OnDeviceConnected(btDevice);
        tracker.OnDeviceConnected(usbDevice);

        // Primary takes precedence
        Assert.Equal(usbDevice.Id, tracker.Device!.Id);
        Assert.Same(primary, tracker.ActiveProfile);
    }

    [Fact]
    public void MultiProfile_PrimaryDisconnects_FallsBackToFallback()
    {
        var primary = new DeviceProfile(f => f.OfCategory(DeviceCategory.Usb), name: "Primary");
        var fallback = new DeviceProfile(f => f.OfCategory(DeviceCategory.Bluetooth), name: "Fallback");
        var tracker = new DeviceTracker("Test", primary, fallback);
        var btDevice = MakeDevice(id: "BT\\1", category: DeviceCategory.Bluetooth);
        var usbDevice = MakeDevice(id: "USB\\1", category: DeviceCategory.Usb);

        tracker.OnDeviceConnected(btDevice);
        tracker.OnDeviceConnected(usbDevice);
        tracker.OnDeviceDisconnected(usbDevice);

        Assert.Equal(btDevice.Id, tracker.Device!.Id);
        Assert.Same(fallback, tracker.ActiveProfile);
    }

    // ── ActiveProfile ──────────────────────────────────────────────────

    [Fact]
    public void ActiveProfile_NullWhenNoDeviceConnected()
    {
        var tracker = CreateTracker();

        Assert.Null(tracker.ActiveProfile);
    }

    [Fact]
    public void ActiveProfile_SetWhenDeviceConnected()
    {
        var tracker = CreateTracker();

        tracker.OnDeviceConnected(MakeDevice());

        Assert.NotNull(tracker.ActiveProfile);
    }

    [Fact]
    public void ActiveProfile_ClearedWhenDeviceDisconnected()
    {
        var tracker = CreateTracker();
        var device = MakeDevice();

        tracker.OnDeviceConnected(device);
        tracker.OnDeviceDisconnected(device);

        Assert.Null(tracker.ActiveProfile);
    }

    // ── StateChanged event ─────────────────────────────────────────────

    [Fact]
    public void OnDeviceAppeared_RaisesStateChanged()
    {
        var tracker = CreateTracker();
        var raised = false;
        tracker.StateChanged += (_, _) => raised = true;

        tracker.OnDeviceAppeared(MakeDevice());

        Assert.True(raised);
    }

    [Fact]
    public void OnDeviceConnected_RaisesStateChanged()
    {
        var tracker = CreateTracker();
        var raised = false;
        tracker.StateChanged += (_, _) => raised = true;

        tracker.OnDeviceConnected(MakeDevice());

        Assert.True(raised);
    }

    [Fact]
    public void OnDeviceConnected_SecondDevice_DoesNotRaiseStateChanged()
    {
        // Second device is rejected by latch — nothing changes
        var tracker = CreateTracker();
        tracker.OnDeviceConnected(MakeDevice(id: "USB\\1"));

        var raised = false;
        tracker.StateChanged += (_, _) => raised = true;

        tracker.OnDeviceConnected(MakeDevice(id: "USB\\2"));

        Assert.False(raised);
    }

    [Fact]
    public void OnDeviceDisconnected_RaisesStateChanged()
    {
        var tracker = CreateTracker();
        tracker.OnDeviceConnected(MakeDevice());

        var raised = false;
        tracker.StateChanged += (_, _) => raised = true;

        tracker.OnDeviceDisconnected(MakeDevice());

        Assert.True(raised);
    }

    // ── IObservable<DeviceTrackerState> ─────────────────────────────────

    [Fact]
    public void Subscribe_ReceivesTrueOnFirstConnect()
    {
        var tracker = CreateTracker();
        var values = new List<DeviceTrackerState>();
        tracker.Subscribe(new TestObserver(values));

        tracker.OnDeviceConnected(MakeDevice());

        Assert.Equal(2, values.Count);
        Assert.Equal(DeviceActivityStatus.Unknown, values[0].ActivityStatus); // initial replay: Unknown (ADR-0056)
        Assert.False(values[0].IsActive);
        Assert.True(values[1].IsActive);
        Assert.Equal(DeviceActivityStatus.Active, values[1].ActivityStatus);
    }

    [Fact]
    public void Subscribe_ReceivesFalseOnDisconnect()
    {
        var tracker = CreateTracker();
        var values = new List<DeviceTrackerState>();
        tracker.Subscribe(new TestObserver(values));

        tracker.OnDeviceConnected(MakeDevice());
        tracker.OnDeviceDisconnected(MakeDevice());

        Assert.Equal(3, values.Count);
        Assert.Equal(DeviceActivityStatus.Unknown, values[0].ActivityStatus); // initial replay: Unknown (ADR-0056)
        Assert.False(values[0].IsActive);
        Assert.True(values[1].IsActive);
        Assert.False(values[2].IsActive);
    }

    [Fact]
    public void Subscribe_AppearedOnly_PushesIsPresentState()
    {
        var tracker = CreateTracker();
        var values = new List<DeviceTrackerState>();
        tracker.Subscribe(new TestObserver(values));

        tracker.OnDeviceAppeared(MakeDevice());

        Assert.Equal(2, values.Count);
        Assert.Equal(DeviceActivityStatus.Unknown, values[0].ActivityStatus); // initial replay: Unknown (ADR-0056)
        Assert.False(values[0].IsPresent);
        Assert.True(values[1].IsPresent);
        Assert.False(values[1].IsActive);
        Assert.Equal(DeviceActivityStatus.Present, values[1].ActivityStatus);
    }

    [Fact]
    public void Subscribe_SecondDevice_DoesNotPush()
    {
        // Second device rejected by latch — no additional push beyond initial + first connect
        var tracker = CreateTracker();
        var values = new List<DeviceTrackerState>();
        tracker.Subscribe(new TestObserver(values));

        tracker.OnDeviceConnected(MakeDevice(id: "USB\\1"));
        tracker.OnDeviceConnected(MakeDevice(id: "USB\\2"));

        Assert.Equal(2, values.Count); // initial replay + first connect only
    }

    [Fact]
    public void Unsubscribe_StopsReceivingValues()
    {
        var tracker = CreateTracker();
        var values = new List<DeviceTrackerState>();
        var subscription = tracker.Subscribe(new TestObserver(values));

        Assert.Single(values); // initial replay fires synchronously on Subscribe

        subscription.Dispose();
        tracker.OnDeviceConnected(MakeDevice());

        Assert.Single(values); // no further values after unsubscribe
    }

    [Fact]
    public void Subscribe_NullObserver_Throws()
    {
        var tracker = CreateTracker();

        Assert.Throws<ArgumentNullException>(() => tracker.Subscribe(null!));
    }

    [Fact]
    public void Subscribe_LateSubscriber_ReceivesCurrentState()
    {
        var tracker = CreateTracker();
        tracker.OnDeviceConnected(MakeDevice());

        // Subscribe after the device is already connected
        var values = new List<DeviceTrackerState>();
        tracker.Subscribe(new TestObserver(values));

        Assert.Single(values);
        Assert.True(values[0].IsActive);
    }

    // ── Bind / Unbind

    [Fact]
    public void Bind_ToSecondWatcher_WhileBound_Throws()
    {
        var tracker = CreateTracker();
        var watcher1 = Devices.Watch();
        var watcher2 = Devices.Watch();

        tracker.Bind(watcher1);

        Assert.Throws<InvalidOperationException>(() => tracker.Bind(watcher2));
    }

    [Fact]
    public void Unbind_ThenRebind_Succeeds()
    {
        var tracker = CreateTracker();
        var watcher1 = Devices.Watch();
        var watcher2 = Devices.Watch();

        tracker.Bind(watcher1);
        tracker.Unbind();
        tracker.Bind(watcher2);
    }

    [Fact]
    public void Unbind_ClearsResolvedState()
    {
        var tracker = CreateTracker();
        var device = MakeDevice();

        tracker.OnDeviceAppeared(device);
        tracker.OnDeviceConnected(device);
        tracker.Unbind();

        Assert.False(tracker.IsPresent);
        Assert.False(tracker.IsActive);
        Assert.Null(tracker.Device);
        Assert.Null(tracker.ActiveProfile);
    }

    [Fact]
    public void Unbind_WhenConnected_RaisesStateChanged()
    {
        var tracker = CreateTracker();
        tracker.OnDeviceAppeared(MakeDevice());
        tracker.OnDeviceConnected(MakeDevice());

        var count = 0;
        tracker.StateChanged += (_, _) => count++;

        tracker.Unbind();

        Assert.True(count > 0);
    }

    [Fact]
    public void Unbind_WhenAlreadyAbsent_DoesNotRaiseStateChanged()
    {
        // A fresh tracker is now Unknown, not Absent (ADR-0056). To exercise the
        // "already Absent → Unbind is a no-op emit" invariant, first resolve the
        // tracker to Absent (the enumeration-complete signal an unmatched tracker
        // receives), then Unbind: Absent → Absent must not raise StateChanged.
        var tracker = CreateTracker();
        tracker.OnInitialEnumerationComplete();
        Assert.Equal(DeviceActivityStatus.Absent, tracker.ActivityStatus);

        var raised = false;
        tracker.StateChanged += (_, _) => raised = true;

        tracker.Unbind();

        Assert.False(raised);
    }

    [Fact]
    public void Unbind_FromUnknown_ResetsToAbsentAndRaisesStateChanged()
    {
        // Lifecycle matrix case (e): Unbind resets a still-Unknown tracker to
        // Absent (the deliberate asymmetry — Unbind is post-determination
        // teardown, not the pre-determination Unknown state). Unknown → Absent
        // is a genuine status change, so StateChanged fires exactly once.
        var tracker = CreateTracker();
        Assert.Equal(DeviceActivityStatus.Unknown, tracker.ActivityStatus);

        var count = 0;
        tracker.StateChanged += (_, _) => count++;

        tracker.Unbind();

        Assert.Equal(DeviceActivityStatus.Absent, tracker.ActivityStatus);
        Assert.Equal(1, count);
    }

    [Fact]
    public void Unbind_NotifiesObserverWithFalse()
    {
        var tracker = CreateTracker();
        var values = new List<DeviceTrackerState>();
        tracker.Subscribe(new TestObserver(values));

        tracker.OnDeviceConnected(MakeDevice());
        tracker.Unbind();

        Assert.Equal(3, values.Count); // initial replay + Connected + Absent
        Assert.False(values[2].IsActive);
    }

    [Fact]
    public void Unbind_ReleasesLatches_AllowsReuseAfterRebind()
    {
        var tracker = CreateTracker();
        var watcher = Devices.Watch();
        var device = MakeDevice();

        tracker.Bind(watcher);
        tracker.OnDeviceConnected(device);
        tracker.Unbind();

        var watcher2 = Devices.Watch();
        tracker.Bind(watcher2);
        tracker.OnDeviceConnected(device);

        Assert.True(tracker.IsActive);
        Assert.Equal(device.Id, tracker.Device!.Id);
    }

    // ── Latch — release and re-claim

    [Fact]
    public void Latch_DisconnectAndReconnect_SameDevice_Succeeds()
    {
        var tracker = CreateTracker();
        var device = MakeDevice();

        tracker.OnDeviceConnected(device);
        tracker.OnDeviceDisconnected(device);
        tracker.OnDeviceConnected(device);

        Assert.True(tracker.IsActive);
        Assert.Equal(device.Id, tracker.Device!.Id);
    }

    [Fact]
    public void Latch_WhileActive_DifferentDevice_IsRejected()
    {
        var tracker = CreateTracker();
        var first = MakeDevice(id: "USB\\1");
        var second = MakeDevice(id: "USB\\2");

        tracker.OnDeviceConnected(first);
        tracker.OnDeviceConnected(second);

        Assert.Equal(first.Id, tracker.Device!.Id);
    }

    [Fact]
    public void Latch_AfterDisconnect_NewDevice_Claims()
    {
        var tracker = CreateTracker();
        var first = MakeDevice(id: "USB\\1");
        var second = MakeDevice(id: "USB\\2");

        tracker.OnDeviceConnected(first);
        tracker.OnDeviceDisconnected(first);
        tracker.OnDeviceConnected(second);

        Assert.Equal(second.Id, tracker.Device!.Id);
    }

    // ── Matches (filter delegation) ────────────────────────────────────

    [Fact]
    public void Matches_SingleProfile_DelegatesToFilter()
    {
        var tracker = CreateTracker(f => f.OfCategory(DeviceCategory.Usb));

        Assert.True(tracker.Matches(MakeDevice(category: DeviceCategory.Usb)));
        Assert.False(tracker.Matches(MakeDevice(category: DeviceCategory.Bluetooth)));
    }

    [Fact]
    public void Matches_MultiProfile_ReturnsTrueIfAnyProfileMatches()
    {
        var tracker = new DeviceTracker("Test",
            new DeviceProfile(f => f.OfCategory(DeviceCategory.Usb)),
            new DeviceProfile(f => f.OfCategory(DeviceCategory.Bluetooth)));

        Assert.True(tracker.Matches(MakeDevice(category: DeviceCategory.Usb)));
        Assert.True(tracker.Matches(MakeDevice(category: DeviceCategory.Bluetooth)));
        Assert.False(tracker.Matches(MakeDevice(category: DeviceCategory.Network)));
    }

    // ── Test helper

    private sealed class TestObserver(List<DeviceTrackerState> values) : IObserver<DeviceTrackerState>
    {
        public void OnNext(DeviceTrackerState value) => values.Add(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    // ── OnDevicePropertyChanged ────────────────────────────────────────

    [Fact]
    public void OnDevicePropertyChanged_UpdatesDevice_WhenResolvedDeviceChanges()
    {
        var tracker = CreateTracker();
        var device = MakeDevice();
        tracker.OnDeviceConnected(device);

        var updated = device with { Name = "Updated Name" };
        tracker.OnDevicePropertyChanged(device, updated, DeviceInfoDiff.Compute(device, updated));

        Assert.Equal("Updated Name", tracker.Device!.Name);
    }

    [Fact]
    public void OnDevicePropertyChanged_DoesNotUpdate_WhenDeviceIsNotResolved()
    {
        var tracker = CreateTracker();
        var resolved = MakeDevice(id: "USB\\1");
        var other = MakeDevice(id: "USB\\2");
        tracker.OnDeviceConnected(resolved);

        var updated = other with { Name = "Changed" };
        tracker.OnDevicePropertyChanged(other, updated, DeviceInfoDiff.Compute(other, updated));

        // Resolved device should be unchanged
        Assert.Equal("USB\\1", tracker.Device!.Id);
        Assert.Equal(resolved.Name, tracker.Device!.Name);
    }

    [Fact]
    public void OnDevicePropertyChanged_WhenNotConnected_DoesNotUpdateDevice()
    {
        var tracker = CreateTracker();
        var device = MakeDevice();
        // Tracker has no resolved device (nothing connected)

        var updated = device with { Name = "Changed" };
        tracker.OnDevicePropertyChanged(device, updated, DeviceInfoDiff.Compute(device, updated));

        Assert.Null(tracker.Device);
    }

    [Fact]
    public void OnDevicePropertyChanged_UpdatesDevice_WhenPresentOnly()
    {
        var tracker = CreateTracker();
        var device = MakeDevice();
        tracker.OnDeviceAppeared(device);
        // Not connected — Device holds the present-only snapshot

        var updated = device with { Name = "Updated Name" };
        tracker.OnDevicePropertyChanged(device, updated, DeviceInfoDiff.Compute(device, updated));

        Assert.Equal("Updated Name", tracker.Device!.Name);
    }

    [Fact]
    public void OnDevicePropertyChanged_RaisesStateChanged()
    {
        // The device snapshot changes — StateChanged must fire so consumers see the update
        var tracker = CreateTracker();
        var device = MakeDevice();
        tracker.OnDeviceConnected(device);

        var raised = false;
        tracker.StateChanged += (_, _) => raised = true;

        var updated = device with { Name = "Changed" };
        tracker.OnDevicePropertyChanged(device, updated, DeviceInfoDiff.Compute(device, updated));

        Assert.True(raised);
    }

    // ── Appeared edge event ────────────────────────────────────────────

    [Fact]
    public void OnDeviceAppeared_RaisesAppeared()
    {
        var tracker = CreateTracker();
        DeviceTrackerTransition? received = null;
        tracker.Appeared += (_, t) => received = t;

        tracker.OnDeviceAppeared(MakeDevice());

        Assert.NotNull(received);
        Assert.False(received.Value.Before.IsPresent);
        Assert.True(received.Value.After.IsPresent);
    }

    [Fact]
    public void OnDeviceAppeared_SecondDevice_DoesNotRaiseAppeared()
    {
        var tracker = CreateTracker();
        tracker.OnDeviceAppeared(MakeDevice(id: "USB\\1"));

        var count = 0;
        tracker.Appeared += (_, _) => count++;

        tracker.OnDeviceAppeared(MakeDevice(id: "USB\\2"));

        Assert.Equal(0, count);
    }

    [Fact]
    public void OnDeviceAppeared_DoesNotRaiseActivated()
    {
        var tracker = CreateTracker();
        var raised = false;
        tracker.Activated += (_, _) => raised = true;

        tracker.OnDeviceAppeared(MakeDevice(isActive: false));

        Assert.False(raised);
    }

    // ── Disappeared edge event ─────────────────────────────────────────

    [Fact]
    public void OnDeviceDisappeared_RaisesDisappeared()
    {
        var tracker = CreateTracker();
        var device = MakeDevice();
        tracker.OnDeviceAppeared(device);

        DeviceTrackerTransition? received = null;
        tracker.Disappeared += (_, t) => received = t;

        tracker.OnDeviceDisappeared(device);

        Assert.NotNull(received);
        Assert.True(received.Value.Before.IsPresent);
        Assert.False(received.Value.After.IsPresent);
    }

    [Fact]
    public void OnDeviceDisappeared_Before_CarriesLastKnownDevice()
    {
        var tracker = CreateTracker();
        var device = MakeDevice();
        tracker.OnDeviceAppeared(device);

        DeviceTrackerTransition? received = null;
        tracker.Disappeared += (_, t) => received = t;

        tracker.OnDeviceDisappeared(device);

        Assert.Equal(device.Id, received!.Value.Before.Device!.Id);
        Assert.Null(received.Value.After.Device);
    }

    // ── Activated edge event ───────────────────────────────────────────

    [Fact]
    public void OnDeviceConnected_RaisesActivated()
    {
        var tracker = CreateTracker();
        DeviceTrackerTransition? received = null;
        tracker.Activated += (_, t) => received = t;

        tracker.OnDeviceConnected(MakeDevice());

        Assert.NotNull(received);
        Assert.False(received.Value.Before.IsActive);
        Assert.True(received.Value.After.IsActive);
    }

    [Fact]
    public void OnDeviceConnected_SecondDevice_DoesNotRaiseActivated()
    {
        var tracker = CreateTracker();
        tracker.OnDeviceConnected(MakeDevice(id: "USB\\1"));

        var count = 0;
        tracker.Activated += (_, _) => count++;

        tracker.OnDeviceConnected(MakeDevice(id: "USB\\2"));

        Assert.Equal(0, count);
    }

    [Fact]
    public void OnDeviceConnected_RaisesAppearedAndActivated_Simultaneously()
    {
        // USB: Appeared + Activated fire from the same OnDeviceConnected call
        var tracker = CreateTracker();
        var appearedFired = false;
        var activatedFired = false;
        tracker.Appeared  += (_, _) => appearedFired = true;
        tracker.Activated += (_, _) => activatedFired = true;

        tracker.OnDeviceConnected(MakeDevice());

        Assert.True(appearedFired);
        Assert.True(activatedFired);
    }

    [Fact]
    public void OnDeviceAppeared_ThenConnected_RaisesActivatedButNotAppeared()
    {
        // BT: Appeared fires once on OnDeviceAppeared; Activated fires separately on OnDeviceConnected
        var tracker = CreateTracker();
        tracker.OnDeviceAppeared(MakeDevice(isActive: false));

        var appearedCount = 0;
        var activatedFired = false;
        tracker.Appeared  += (_, _) => appearedCount++;
        tracker.Activated += (_, _) => activatedFired = true;

        tracker.OnDeviceConnected(MakeDevice());

        Assert.Equal(0, appearedCount); // already present — no second Appeared
        Assert.True(activatedFired);
    }

    // ── Deactivated edge event ─────────────────────────────────────────

    [Fact]
    public void OnDeviceDisconnected_RaisesDeactivated()
    {
        var tracker = CreateTracker();
        var device = MakeDevice();
        tracker.OnDeviceConnected(device);

        DeviceTrackerTransition? received = null;
        tracker.Deactivated += (_, t) => received = t;

        tracker.OnDeviceDisconnected(device);

        Assert.NotNull(received);
        Assert.True(received.Value.Before.IsActive);
        Assert.False(received.Value.After.IsActive);
    }

    [Fact]
    public void OnDeviceDisconnected_Before_CarriesLastActiveDevice()
    {
        var tracker = CreateTracker();
        var device = MakeDevice();
        tracker.OnDeviceConnected(device);

        DeviceTrackerTransition? received = null;
        tracker.Deactivated += (_, t) => received = t;

        tracker.OnDeviceDisconnected(device);

        Assert.Equal(device.Id, received!.Value.Before.Device!.Id);
    }

    // ── DeviceTracker.PropertyChanged ──────────────────────────────────

    [Fact]
    public void OnDevicePropertyChanged_RaisesPropertyChanged_ForResolvedDevice()
    {
        var tracker = CreateTracker();
        var device = MakeDevice();
        tracker.OnDeviceConnected(device);

        DevicePropertyChangedEventArgs? received = null;
        tracker.PropertyChanged += (_, e) => received = e;

        var updated = device with { Name = "Updated Name" };
        tracker.OnDevicePropertyChanged(device, updated, DeviceInfoDiff.Compute(device, updated));

        Assert.NotNull(received);
        Assert.Equal(device.Id, received.Previous.Id);
        Assert.Equal("Updated Name", received.Current.Name);
        Assert.Contains(nameof(DeviceInfo.Name), received.ChangedProperties);
    }

    [Fact]
    public void OnDevicePropertyChanged_DoesNotRaisePropertyChanged_ForUnresolvedDevice()
    {
        var tracker = CreateTracker();
        var resolved = MakeDevice(id: "USB\\1");
        var other = MakeDevice(id: "USB\\2");
        tracker.OnDeviceConnected(resolved);

        var count = 0;
        tracker.PropertyChanged += (_, _) => count++;

        var updated = other with { Name = "Changed" };
        tracker.OnDevicePropertyChanged(other, updated, DeviceInfoDiff.Compute(other, updated));

        Assert.Equal(0, count);
    }

    [Fact]
    public void OnDevicePropertyChanged_PropertyChanged_CarriesCorrectChangedProperties()
    {
        var tracker = CreateTracker();
        var device = MakeDevice();
        tracker.OnDeviceConnected(device);

        DevicePropertyChangedEventArgs? received = null;
        tracker.PropertyChanged += (_, e) => received = e;

        var updated = device with { Name = "New Name" };
        tracker.OnDevicePropertyChanged(device, updated, DeviceInfoDiff.Compute(device, updated));

        Assert.Contains(nameof(DeviceInfo.Name), received!.ChangedProperties);
        Assert.DoesNotContain(nameof(DeviceInfo.Category), received.ChangedProperties);
    }

    // ── OnInitialEnumerationComplete (Unknown → determined) — ADR-0056 ──

    [Fact]
    public void OnInitialEnumerationComplete_UnmatchedTracker_ResolvesToAbsent()
    {
        // An unmatched tracker (empty latches) is Unknown until the watcher's
        // initial enumeration settles; the hook resolves it to Absent.
        var tracker = CreateTracker();
        Assert.Equal(DeviceActivityStatus.Unknown, tracker.ActivityStatus);

        tracker.OnInitialEnumerationComplete();

        Assert.Equal(DeviceActivityStatus.Absent, tracker.ActivityStatus);
        Assert.False(tracker.IsPresent);
        Assert.False(tracker.IsActive);
        Assert.Null(tracker.Device);
    }

    [Fact]
    public void OnInitialEnumerationComplete_UnmatchedTracker_EmitsExactlyOneStateChange_NoEdgeEvents()
    {
        // Unknown → Absent: the state-level StateChanged/OnNext fires once, but
        // NO edge events — nothing appeared, so nothing disappeared (Subtlety 5).
        var tracker = CreateTracker();
        var states = new List<DeviceTrackerState>();
        tracker.Subscribe(new TestObserver(states));
        states.Clear(); // drop the initial Unknown replay; focus on the transition

        var stateChangedCount = 0;
        var appeared = 0;
        var disappeared = 0;
        var activated = 0;
        var deactivated = 0;
        tracker.StateChanged += (_, _) => stateChangedCount++;
        tracker.Appeared += (_, _) => appeared++;
        tracker.Disappeared += (_, _) => disappeared++;
        tracker.Activated += (_, _) => activated++;
        tracker.Deactivated += (_, _) => deactivated++;

        tracker.OnInitialEnumerationComplete();

        Assert.Equal(1, stateChangedCount);
        Assert.Single(states);
        Assert.Equal(DeviceActivityStatus.Absent, states[0].ActivityStatus);
        Assert.Equal(0, appeared);
        Assert.Equal(0, disappeared);
        Assert.Equal(0, activated);
        Assert.Equal(0, deactivated);
    }

    [Fact]
    public void OnInitialEnumerationComplete_MatchedTracker_IsNoOp()
    {
        // A tracker the fan-out already resolved (Active) must not be re-emitted
        // by the hook — the early-return guards the same-snapshot race.
        var tracker = CreateTracker();
        tracker.OnDeviceConnected(MakeDevice());
        Assert.Equal(DeviceActivityStatus.Active, tracker.ActivityStatus);

        var stateChangedCount = 0;
        tracker.StateChanged += (_, _) => stateChangedCount++;

        tracker.OnInitialEnumerationComplete();

        Assert.Equal(DeviceActivityStatus.Active, tracker.ActivityStatus);
        Assert.Equal(0, stateChangedCount);
    }

    [Fact]
    public void OnInitialEnumerationComplete_CalledTwice_SecondIsNoOp()
    {
        // Idempotent: once resolved to Absent, a second signal does nothing.
        var tracker = CreateTracker();
        tracker.OnInitialEnumerationComplete();
        Assert.Equal(DeviceActivityStatus.Absent, tracker.ActivityStatus);

        var stateChangedCount = 0;
        tracker.StateChanged += (_, _) => stateChangedCount++;

        tracker.OnInitialEnumerationComplete();

        Assert.Equal(DeviceActivityStatus.Absent, tracker.ActivityStatus);
        Assert.Equal(0, stateChangedCount);
    }

    // ── Unknown → determined transition sequences (one emission each) ──

    [Fact]
    public void Unknown_To_Active_FiresAppearedAndActivated_SingleStateChange()
    {
        // USB present at startup: Unknown → Active. One StateChanged; Appeared
        // and Activated fire together (Subtlety 5).
        var tracker = CreateTracker();
        var states = new List<DeviceTrackerState>();
        tracker.Subscribe(new TestObserver(states));
        states.Clear();

        var appeared = 0;
        var activated = 0;
        var disappeared = 0;
        var deactivated = 0;
        tracker.Appeared += (_, _) => appeared++;
        tracker.Activated += (_, _) => activated++;
        tracker.Disappeared += (_, _) => disappeared++;
        tracker.Deactivated += (_, _) => deactivated++;

        tracker.OnDeviceConnected(MakeDevice());

        Assert.Single(states);
        Assert.Equal(DeviceActivityStatus.Active, states[0].ActivityStatus);
        Assert.Equal(1, appeared);
        Assert.Equal(1, activated);
        Assert.Equal(0, disappeared);
        Assert.Equal(0, deactivated);
    }

    [Fact]
    public void Unknown_To_Present_FiresAppearedOnly_SingleStateChange()
    {
        // BT paired/out-of-range at startup: Unknown → Present. Appeared only.
        var tracker = CreateTracker();
        var states = new List<DeviceTrackerState>();
        tracker.Subscribe(new TestObserver(states));
        states.Clear();

        var appeared = 0;
        var activated = 0;
        tracker.Appeared += (_, _) => appeared++;
        tracker.Activated += (_, _) => activated++;

        tracker.OnDeviceAppeared(MakeDevice(category: DeviceCategory.Bluetooth, isActive: false));

        Assert.Single(states);
        Assert.Equal(DeviceActivityStatus.Present, states[0].ActivityStatus);
        Assert.Equal(1, appeared);
        Assert.Equal(0, activated);
    }

    [Fact]
    public void Unknown_To_Active_To_Absent_PresentAtStartupThenUnplugged()
    {
        // Device present at startup, later unplugged: Unknown → Active → Absent.
        var tracker = CreateTracker();
        var device = MakeDevice();
        var states = new List<DeviceTrackerState>();
        tracker.Subscribe(new TestObserver(states));

        tracker.OnDeviceConnected(device);   // Unknown → Active
        tracker.OnDeviceDisappeared(device); // Active → Absent

        // initial Unknown replay + Active + Absent
        Assert.Equal(3, states.Count);
        Assert.Equal(DeviceActivityStatus.Unknown, states[0].ActivityStatus);
        Assert.Equal(DeviceActivityStatus.Active, states[1].ActivityStatus);
        Assert.Equal(DeviceActivityStatus.Absent, states[2].ActivityStatus);
    }

    // ── Late subscriber never sees Unknown ─────────────────────────────

    [Fact]
    public void LateSubscriber_AfterEnumerationComplete_NeverSeesUnknown()
    {
        // Resolve the tracker first (the hook), then subscribe: the single
        // replayed value is the determined one (Absent), never Unknown.
        var tracker = CreateTracker();
        tracker.OnInitialEnumerationComplete();

        var values = new List<DeviceTrackerState>();
        tracker.Subscribe(new TestObserver(values));

        Assert.Single(values);
        Assert.Equal(DeviceActivityStatus.Absent, values[0].ActivityStatus);
        Assert.NotEqual(DeviceActivityStatus.Unknown, values[0].ActivityStatus);
    }
}
