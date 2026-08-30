namespace Periphery.Tests;

/// <summary>
/// Unit tests for <see cref="DeviceWatcher"/> event fan-out logic, snapshot behavior,
/// and <c>_knownConnectedIds</c> cascade. Uses <see cref="FakeDeviceProvider"/> and
/// <see cref="FakeDeviceMonitorProvider"/> — no OS APIs required.
/// </summary>
public class DeviceWatcherEventTests
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
        Status = DeviceStatus.OK,
    };

    private static (DeviceWatcher Watcher, FakeDeviceProvider Provider, FakeDeviceMonitorProvider Monitor) CreateWatcher(
        params DeviceInfo[] snapshotDevices)
    {
        var provider = new FakeDeviceProvider(snapshotDevices);
        var monitor = new FakeDeviceMonitorProvider();
        var watcher = new DeviceWatcher(provider, monitor);
        return (watcher, provider, monitor);
    }

    // ── Snapshot — Appeared / Connected events ─────────────────────────

    [Fact]
    public async Task StartAsync_RaisesAppearedForAllSnapshotDevices()
    {
        var devices = new[]
        {
            MakeDevice(id: "USB\\1", isActive: true),
            MakeDevice(id: "USB\\2", isActive: false),
        };
        var (watcher, _, _) = CreateWatcher(devices);
        var appeared = new List<string>();
        watcher.Appeared += (_, e) => appeared.Add(e.Device.Id);

        await watcher.StartAsync();

        Assert.Equal(2, appeared.Count);
        Assert.Contains("USB\\1", appeared);
        Assert.Contains("USB\\2", appeared);

        await watcher.DisposeAsync();
    }

    [Fact]
    public async Task StartAsync_RaisesConnectedOnlyForActiveDevices()
    {
        var devices = new[]
        {
            MakeDevice(id: "USB\\1", isActive: true),
            MakeDevice(id: "USB\\2", isActive: false),
        };
        var (watcher, _, _) = CreateWatcher(devices);
        var activated = new List<string>();
        watcher.Activated += (_, e) => activated.Add(e.Device.Id);

        await watcher.StartAsync();

        Assert.Single(activated);
        Assert.Equal("USB\\1", activated[0]);

        await watcher.DisposeAsync();
    }

    [Fact]
    public async Task StartAsync_EmptySnapshot_RaisesNoEvents()
    {
        var (watcher, _, _) = CreateWatcher();
        var appeared = new List<string>();
        var activated = new List<string>();
        watcher.Appeared += (_, e) => appeared.Add(e.Device.Id);
        watcher.Activated += (_, e) => activated.Add(e.Device.Id);

        await watcher.StartAsync();

        Assert.Empty(appeared);
        Assert.Empty(activated);

        await watcher.DisposeAsync();
    }

    // ── Snapshot ordering: Appeared fires before Connected ─────────────

    [Fact]
    public async Task StartAsync_AppearedFiresBeforeActivated()
    {
        var device = MakeDevice(id: "USB\\1", isActive: true);
        var (watcher, _, _) = CreateWatcher(device);
        var events = new List<string>();
        watcher.Appeared += (_, e) => events.Add($"appeared:{e.Device.Id}");
        watcher.Activated += (_, e) => events.Add($"activated:{e.Device.Id}");

        await watcher.StartAsync();

        Assert.Equal(2, events.Count);
        Assert.Equal("appeared:USB\\1", events[0]);
        Assert.Equal("activated:USB\\1", events[1]);

        await watcher.DisposeAsync();
    }

    // ── Real-time events — provider event handlers ─────────────────────

    [Fact]
    public async Task ProviderAppeared_RaisesAppearedEvent()
    {
        var (watcher, _, monitor) = CreateWatcher();
        var appeared = new List<string>();
        watcher.Appeared += (_, e) => appeared.Add(e.Device.Id);

        await watcher.StartAsync();

        var device = MakeDevice(id: "USB\\NEW", isActive: true);
        monitor.SimulateConnect(device);

        Assert.Contains("USB\\NEW", appeared);

        await watcher.DisposeAsync();
    }

    [Fact]
    public async Task ProviderActivated_RaisesActivatedEvent()
    {
        var (watcher, _, monitor) = CreateWatcher();
        var activated = new List<string>();
        watcher.Activated += (_, e) => activated.Add(e.Device.Id);

        await watcher.StartAsync();

        var device = MakeDevice(id: "USB\\NEW", isActive: true);
        monitor.SimulateConnect(device);

        Assert.Contains("USB\\NEW", activated);

        await watcher.DisposeAsync();
    }

    [Fact]
    public async Task ProviderDeactivated_RaisesDeactivatedEvent()
    {
        var (watcher, _, monitor) = CreateWatcher();
        var deactivated = new List<string>();
        watcher.Deactivated += (_, e) => deactivated.Add(e.Device.Id);

        await watcher.StartAsync();

        var device = MakeDevice(id: "USB\\1", isActive: true);
        monitor.SimulateConnect(device);
        monitor.SimulateDisconnect(device);

        Assert.Contains("USB\\1", deactivated);

        await watcher.DisposeAsync();
    }

    [Fact]
    public async Task ProviderDisappeared_RaisesDisappearedEvent()
    {
        var (watcher, _, monitor) = CreateWatcher();
        var disappeared = new List<string>();
        watcher.Disappeared += (_, e) => disappeared.Add(e.Device.Id);

        await watcher.StartAsync();

        var device = MakeDevice(id: "USB\\1", isActive: true);
        monitor.SimulateConnect(device);
        monitor.SimulateDisconnect(device);

        Assert.Contains("USB\\1", disappeared);

        await watcher.DisposeAsync();
    }

    // ── _knownConnectedIds cascade ─────────────────────────────────────

    [Fact]
    public async Task DisappearedDevice_ThatWasActivated_CascadesDeactivated()
    {
        var device = MakeDevice(id: "USB\\1", isActive: true);
        var (watcher, _, monitor) = CreateWatcher(device);
        var events = new List<string>();
        watcher.Deactivated += (_, e) => events.Add($"deactivated:{e.Device.Id}");
        watcher.Disappeared += (_, e) => events.Add($"disappeared:{e.Device.Id}");

        await watcher.StartAsync();
        // Snapshot connected USB\1, now simulate disappearance
        monitor.SimulateDisconnect(device);

        Assert.Equal(2, events.Count);
        Assert.Equal("deactivated:USB\\1", events[0]);
        Assert.Equal("disappeared:USB\\1", events[1]);

        await watcher.DisposeAsync();
    }

    [Fact]
    public async Task DisappearedDevice_ThatWasNotActivated_DoesNotCascadeDeactivated()
    {
        var device = MakeDevice(id: "USB\\1", isActive: false);
        var (watcher, _, monitor) = CreateWatcher(device);
        var deactivated = new List<string>();
        var disappeared = new List<string>();
        watcher.Deactivated += (_, e) => deactivated.Add(e.Device.Id);
        watcher.Disappeared += (_, e) => disappeared.Add(e.Device.Id);

        await watcher.StartAsync();
        monitor.SimulateDisconnect(device);

        Assert.Empty(deactivated);
        Assert.Single(disappeared);

        await watcher.DisposeAsync();
    }

    // ── Filter application on events ───────────────────────────────────

    [Fact]
    public async Task WatcherFilter_ExcludesNonMatchingDevicesFromGlobalEvents()
    {
        var usbDevice = MakeDevice(id: "USB\\1", category: DeviceCategory.Usb);
        var netDevice = MakeDevice(id: "NET\\1", category: DeviceCategory.Network);
        var (watcher, _, monitor) = CreateWatcher();
        watcher.OfCategory(DeviceCategory.Usb);

        var appeared = new List<string>();
        watcher.Appeared += (_, e) => appeared.Add(e.Device.Id);

        await watcher.StartAsync();

        monitor.SimulateConnect(usbDevice);
        monitor.SimulateConnect(netDevice);

        Assert.Single(appeared);
        Assert.Equal("USB\\1", appeared[0]);

        await watcher.DisposeAsync();
    }

    [Fact]
    public async Task WatcherFilter_AppliesToSnapshotDevices()
    {
        var devices = new[]
        {
            MakeDevice(id: "USB\\1", category: DeviceCategory.Usb),
            MakeDevice(id: "NET\\1", category: DeviceCategory.Network),
        };
        var (watcher, _, _) = CreateWatcher(devices);
        watcher.OfCategory(DeviceCategory.Usb);

        var appeared = new List<string>();
        watcher.Appeared += (_, e) => appeared.Add(e.Device.Id);

        await watcher.StartAsync();

        Assert.Single(appeared);
        Assert.Equal("USB\\1", appeared[0]);

        await watcher.DisposeAsync();
    }

    // ── Tracker fan-out during snapshot ─────────────────────────────────

    [Fact]
    public async Task StartAsync_FansOutToTrackersDuringSnapshot()
    {
        var device = MakeDevice(id: "USB\\1", category: DeviceCategory.Usb, isActive: true);
        var (watcher, _, _) = CreateWatcher(device);
        var tracker = watcher.AddTracker(f => f.OfCategory(DeviceCategory.Usb), "USBTracker");

        await watcher.StartAsync();

        Assert.True(tracker.IsPresent);
        Assert.True(tracker.IsActive);
        Assert.Equal("USB\\1", tracker.Device!.Id);

        await watcher.DisposeAsync();
    }

    [Fact]
    public async Task StartAsync_TrackerIgnoresNonMatchingDevicesInSnapshot()
    {
        var usbDevice = MakeDevice(id: "USB\\1", category: DeviceCategory.Usb);
        var netDevice = MakeDevice(id: "NET\\1", category: DeviceCategory.Network);
        var (watcher, _, _) = CreateWatcher(usbDevice, netDevice);
        var tracker = watcher.AddTracker(f => f.OfCategory(DeviceCategory.Usb), "USBTracker");

        await watcher.StartAsync();

        Assert.True(tracker.IsActive);
        Assert.Equal("USB\\1", tracker.Device!.Id);

        await watcher.DisposeAsync();
    }

    // ── Tracker fan-out during real-time events

    [Fact]
    public async Task ProviderConnect_FansOutToMatchingTracker()
    {
        var (watcher, _, monitor) = CreateWatcher();
        var tracker = watcher.AddTracker(f => f.OfCategory(DeviceCategory.Usb), "USBTracker");

        await watcher.StartAsync();

        var device = MakeDevice(id: "USB\\1", category: DeviceCategory.Usb, isActive: true);
        monitor.SimulateConnect(device);

        Assert.True(tracker.IsPresent);
        Assert.True(tracker.IsActive);

        await watcher.DisposeAsync();
    }

    [Fact]
    public async Task ProviderConnect_DoesNotFanOutToNonMatchingTracker()
    {
        var (watcher, _, monitor) = CreateWatcher();
        var tracker = watcher.AddTracker(f => f.OfCategory(DeviceCategory.Bluetooth), "BTTracker");

        await watcher.StartAsync();

        var device = MakeDevice(id: "USB\\1", category: DeviceCategory.Usb, isActive: true);
        monitor.SimulateConnect(device);

        Assert.False(tracker.IsPresent);
        Assert.False(tracker.IsActive);

        await watcher.DisposeAsync();
    }

    [Fact]
    public async Task ProviderDisappear_FansOutDisappearedToTracker()
    {
        var device = MakeDevice(id: "USB\\1", category: DeviceCategory.Usb, isActive: true);
        var (watcher, _, monitor) = CreateWatcher(device);
        var tracker = watcher.AddTracker(f => f.OfCategory(DeviceCategory.Usb), "USBTracker");

        await watcher.StartAsync();
        Assert.True(tracker.IsActive);

        monitor.SimulateDisconnect(device);

        Assert.False(tracker.IsPresent);

        await watcher.DisposeAsync();
    }

    // ── Multiple trackers ──────────────────────────────────────────────

    [Fact]
    public async Task MultipleTrackers_EachReceivesMatchingEvents()
    {
        var usbDevice = MakeDevice(id: "USB\\1", category: DeviceCategory.Usb, isActive: true);
        var btDevice = MakeDevice(id: "BT\\1", category: DeviceCategory.Bluetooth, isActive: true);
        var (watcher, _, monitor) = CreateWatcher();
        var usbTracker = watcher.AddTracker(f => f.OfCategory(DeviceCategory.Usb), "USB");
        var btTracker = watcher.AddTracker(f => f.OfCategory(DeviceCategory.Bluetooth), "BT");

        await watcher.StartAsync();

        monitor.SimulateConnect(usbDevice);
        monitor.SimulateConnect(btDevice);

        Assert.True(usbTracker.IsActive);
        Assert.Equal("USB\\1", usbTracker.Device!.Id);
        Assert.True(btTracker.IsActive);
        Assert.Equal("BT\\1", btTracker.Device!.Id);

        await watcher.DisposeAsync();
    }

    // ── KnownDevices cached accessor ───────────────────────────────────

    [Fact]
    public void KnownDevices_BeforeStart_IsEmpty()
    {
        var (watcher, _, _) = CreateWatcher(MakeDevice(id: "USB\\1"));

        Assert.Empty(watcher.KnownDevices);
    }

    [Fact]
    public async Task KnownDevices_AfterStart_ReturnsSnapshotSet()
    {
        var devices = new[]
        {
            MakeDevice(id: "USB\\1", isActive: true),
            MakeDevice(id: "USB\\2", isActive: false),
        };
        var (watcher, _, _) = CreateWatcher(devices);

        await watcher.StartAsync();

        var known = watcher.KnownDevices.Select(d => d.Id).ToList();
        Assert.Equal(2, known.Count);
        Assert.Contains("USB\\1", known);
        Assert.Contains("USB\\2", known);

        await watcher.DisposeAsync();
    }

    [Fact]
    public async Task KnownDevices_RespectsWatcherFilter()
    {
        var devices = new[]
        {
            MakeDevice(id: "USB\\1", category: DeviceCategory.Usb),
            MakeDevice(id: "NET\\1", category: DeviceCategory.Network),
        };
        var (watcher, _, _) = CreateWatcher(devices);
        watcher.OfCategory(DeviceCategory.Usb);

        await watcher.StartAsync();

        var known = watcher.KnownDevices.Select(d => d.Id).ToList();
        Assert.Single(known);
        Assert.Equal("USB\\1", known[0]);

        await watcher.DisposeAsync();
    }

    [Fact]
    public async Task KnownDevices_ReturnsIndependentSnapshotCopy()
    {
        var (watcher, _, monitor) = CreateWatcher(MakeDevice(id: "USB\\1"));

        await watcher.StartAsync();

        var first = watcher.KnownDevices;
        Assert.Single(first);

        // A later property change updates the cache; the earlier returned list
        // is an independent copy and must not change retroactively.
        var updated = MakeDevice(id: "USB\\1", name: "Renamed Mouse");
        monitor.SimulatePropertyChange(MakeDevice(id: "USB\\1"), updated);

        Assert.Single(first);
        Assert.Equal("Logitech Mouse", first[0].Name);
        Assert.Equal("Renamed Mouse", watcher.KnownDevices[0].Name);

        await watcher.DisposeAsync();
    }

    // ── StartAsync twice throws ────────────────────────────────────────

    [Fact]
    public async Task StartAsync_CalledTwice_ThrowsInvalidOperationException()
    {
        var (watcher, _, _) = CreateWatcher();

        await watcher.StartAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => watcher.StartAsync());

        await watcher.DisposeAsync();
    }

    // ── DisposeAsync clears trackers ───────────────────────────────────

    [Fact]
    public async Task DisposeAsync_UnbindsTrackers()
    {
        var device = MakeDevice(id: "USB\\1", isActive: true);
        var (watcher, _, _) = CreateWatcher(device);
        var tracker = watcher.AddTracker(f => f.OfCategory(DeviceCategory.Usb), "USB");

        await watcher.StartAsync();
        Assert.True(tracker.IsActive);

        await watcher.DisposeAsync();

        Assert.False(tracker.IsActive);
        Assert.Null(tracker.Device);
    }

    // ── DeviceActivityStatus.Unknown initial state (ADR-0056) ──────────

    [Fact]
    public void BeforeStart_BoundTracker_IsUnknown()
    {
        // A tracker bound to a not-yet-started watcher has not been enumerated,
        // so it is Unknown (not Absent).
        var (watcher, _, _) = CreateWatcher(MakeDevice(id: "USB\\1"));
        var tracker = watcher.AddTracker(f => f.OfCategory(DeviceCategory.Usb), "USB");

        Assert.Equal(DeviceActivityStatus.Unknown, tracker.ActivityStatus);
    }

    [Fact]
    public async Task StartAsync_UnmatchedTracker_ResolvesToAbsentExactlyOnce()
    {
        // A tracker whose filter matches nothing in the snapshot resolves to
        // Absent via the enumeration-complete signal, and the observable sees
        // the single transition Unknown → Absent.
        var (watcher, _, _) = CreateWatcher(MakeDevice(id: "USB\\1", category: DeviceCategory.Usb));
        var tracker = watcher.AddTracker(f => f.OfCategory(DeviceCategory.Bluetooth), "BT");

        var observed = new List<DeviceActivityStatus>();
        tracker.Subscribe(new StatusObserver(observed));

        await watcher.StartAsync();

        Assert.Equal(DeviceActivityStatus.Absent, tracker.ActivityStatus);
        // initial Unknown replay, then exactly one transition to Absent
        Assert.Equal(
            new[] { DeviceActivityStatus.Unknown, DeviceActivityStatus.Absent },
            observed);

        await watcher.DisposeAsync();
    }

    [Fact]
    public async Task StartAsync_UnmatchedTracker_FiresNoEdgeEvents()
    {
        // Unknown → Absent fires no Appeared/Disappeared/Activated/Deactivated.
        var (watcher, _, _) = CreateWatcher(MakeDevice(id: "USB\\1", category: DeviceCategory.Usb));
        var tracker = watcher.AddTracker(f => f.OfCategory(DeviceCategory.Bluetooth), "BT");

        var edges = 0;
        tracker.Appeared += (_, _) => edges++;
        tracker.Disappeared += (_, _) => edges++;
        tracker.Activated += (_, _) => edges++;
        tracker.Deactivated += (_, _) => edges++;

        await watcher.StartAsync();

        Assert.Equal(0, edges);
        Assert.Equal(DeviceActivityStatus.Absent, tracker.ActivityStatus);

        await watcher.DisposeAsync();
    }

    [Fact]
    public async Task StartAsync_MatchedTracker_GoesUnknownToActive()
    {
        // Present active device in the snapshot. The watcher snapshot fans out
        // Appeared then Activated as two separate tracker calls, so the observed
        // status sequence is Unknown → Present → Active (the Present→Active split
        // is pre-existing snapshot behavior; ADR-0056 only changes the FIRST value
        // from Absent to Unknown). The contract points that matter:
        //   - the first replayed value is Unknown, not Absent
        //   - it terminates at Active
        //   - no stale Unknown reappears after the first value
        //   - the enumeration-complete hook adds no extra emission (no-op for a
        //     tracker already resolved by the fan-out)
        //   - Appeared + Activated each fire exactly once.
        var (watcher, _, _) = CreateWatcher(MakeDevice(id: "USB\\1", category: DeviceCategory.Usb, isActive: true));
        var tracker = watcher.AddTracker(f => f.OfCategory(DeviceCategory.Usb), "USB");

        var observed = new List<DeviceActivityStatus>();
        tracker.Subscribe(new StatusObserver(observed));
        var appeared = 0;
        var activated = 0;
        tracker.Appeared += (_, _) => appeared++;
        tracker.Activated += (_, _) => activated++;

        await watcher.StartAsync();

        Assert.Equal(
            new[] { DeviceActivityStatus.Unknown, DeviceActivityStatus.Present, DeviceActivityStatus.Active },
            observed);
        Assert.Equal(DeviceActivityStatus.Unknown, observed[0]);          // first value: Unknown, not Absent
        Assert.Equal(DeviceActivityStatus.Active, observed[^1]);          // terminates Active
        Assert.DoesNotContain(DeviceActivityStatus.Unknown, observed.Skip(1)); // no stale Unknown after first
        Assert.Equal(1, appeared);
        Assert.Equal(1, activated);

        await watcher.DisposeAsync();
    }

    [Fact]
    public async Task StartAsync_PostCondition_NoBoundTrackerIsUnknown()
    {
        // After await StartAsync, every bound tracker has left Unknown —
        // matched ones via fan-out, unmatched ones via the hook.
        var (watcher, _, _) = CreateWatcher(
            MakeDevice(id: "USB\\1", category: DeviceCategory.Usb, isActive: true));
        var matched = watcher.AddTracker(f => f.OfCategory(DeviceCategory.Usb), "USB");
        var unmatched = watcher.AddTracker(f => f.OfCategory(DeviceCategory.Bluetooth), "BT");

        await watcher.StartAsync();

        Assert.NotEqual(DeviceActivityStatus.Unknown, matched.ActivityStatus);
        Assert.NotEqual(DeviceActivityStatus.Unknown, unmatched.ActivityStatus);
        Assert.Equal(DeviceActivityStatus.Active, matched.ActivityStatus);
        Assert.Equal(DeviceActivityStatus.Absent, unmatched.ActivityStatus);

        await watcher.DisposeAsync();
    }

    [Fact]
    public async Task StartAsync_EmptySnapshot_BoundTrackerResolvesToAbsent()
    {
        // No devices at all: the bound tracker still leaves Unknown (→ Absent)
        // because the enumeration-complete signal fires even on an empty drain.
        var (watcher, _, _) = CreateWatcher();
        var tracker = watcher.AddTracker(f => f.OfCategory(DeviceCategory.Usb), "USB");

        await watcher.StartAsync();

        Assert.Equal(DeviceActivityStatus.Absent, tracker.ActivityStatus);

        await watcher.DisposeAsync();
    }

    [Fact]
    public void NeverStarted_TrackerStaysUnknown()
    {
        // matrix case (c): construct + bind, never start. The tracker stays
        // Unknown — nothing has enumerated, so "unknown" is the truthful state.
        var (watcher, _, _) = CreateWatcher(MakeDevice(id: "USB\\1"));
        var tracker = watcher.AddTracker(f => f.OfCategory(DeviceCategory.Usb), "USB");

        Assert.Equal(DeviceActivityStatus.Unknown, tracker.ActivityStatus);
    }

    [Fact]
    public async Task ReconfigureAfterStart_SkipsUnknown()
    {
        // matrix case (b): reconfigure against a warm cache resolves synchronously
        // and never publishes Unknown.
        var (watcher, _, _) = CreateWatcher(
            MakeDevice(id: "USB\\1", category: DeviceCategory.Usb, isActive: true),
            MakeDevice(id: "HID\\1", category: DeviceCategory.Hid, isActive: true));
        var tracker = watcher.AddTracker(f => f.OfCategory(DeviceCategory.Usb), "T");
        await watcher.StartAsync();

        var observed = new List<DeviceActivityStatus>();
        tracker.Subscribe(new StatusObserver(observed));
        observed.Clear();

        tracker.Reconfigure(f => f.OfCategory(DeviceCategory.Hid));

        Assert.DoesNotContain(DeviceActivityStatus.Unknown, observed);

        await watcher.DisposeAsync();
    }

    private sealed class StatusObserver(List<DeviceActivityStatus> statuses)
        : IObserver<DeviceTrackerState>
    {
        public void OnNext(DeviceTrackerState value) => statuses.Add(value.ActivityStatus);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
