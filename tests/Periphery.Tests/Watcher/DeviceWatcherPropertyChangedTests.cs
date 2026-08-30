
namespace Periphery.Tests;

/// <summary>
/// Unit tests for <see cref="DeviceWatcher.PropertyChanged"/> — the ADR-0005
/// property-change event. Uses fake providers; no OS APIs required.
/// </summary>
public class DeviceWatcherPropertyChangedTests
{
    private static DeviceInfo MakeDevice(
        string id = "USB\\VID_046D&PID_C52B\\1",
        bool isActive = true,
        int? batteryChargePercent = null) => new()
    {
        Id = id,
        Name = "Test Device",
        Category = DeviceCategory.Battery,
        IsActive = isActive,
        Status = DeviceStatus.OK,
        BatteryChargePercent = batteryChargePercent,
    };

    private static (DeviceWatcher Watcher, FakeDeviceProvider Provider, FakeDeviceMonitorProvider Monitor) CreateWatcher(
        params DeviceInfo[] snapshotDevices)
    {
        var provider = new FakeDeviceProvider(snapshotDevices);
        var monitor = new FakeDeviceMonitorProvider();
        var watcher = new DeviceWatcher(provider, monitor);
        return (watcher, provider, monitor);
    }

    // ── Basic firing ───────────────────────────────────────────────────

    [Fact]
    public async Task PropertyChanged_Fires_WhenProviderRaisesModification()
    {
        var device = MakeDevice(batteryChargePercent: 80);
        var (watcher, _, monitor) = CreateWatcher(device);
        DevicePropertyChangedEventArgs? received = null;
        watcher.PropertyChanged += (_, e) => received = e;

        await watcher.StartAsync();

        var updated = device with { BatteryChargePercent = 79 };
        monitor.SimulatePropertyChange(device, updated);

        Assert.NotNull(received);
        await watcher.DisposeAsync();
    }

    [Fact]
    public async Task PropertyChanged_Previous_MatchesOriginalSnapshot()
    {
        var device = MakeDevice(batteryChargePercent: 80);
        var (watcher, _, monitor) = CreateWatcher(device);
        DevicePropertyChangedEventArgs? received = null;
        watcher.PropertyChanged += (_, e) => received = e;

        await watcher.StartAsync();

        var updated = device with { BatteryChargePercent = 79 };
        monitor.SimulatePropertyChange(device, updated);

        Assert.Equal(80, received!.Previous.BatteryChargePercent);
        await watcher.DisposeAsync();
    }

    [Fact]
    public async Task PropertyChanged_Current_IsUpdatedSnapshot()
    {
        var device = MakeDevice(batteryChargePercent: 80);
        var (watcher, _, monitor) = CreateWatcher(device);
        DevicePropertyChangedEventArgs? received = null;
        watcher.PropertyChanged += (_, e) => received = e;

        await watcher.StartAsync();

        var updated = device with { BatteryChargePercent = 79 };
        monitor.SimulatePropertyChange(device, updated);

        Assert.Equal(79, received!.Current.BatteryChargePercent);
        await watcher.DisposeAsync();
    }

    // ── ChangedProperties ──────────────────────────────────────────────

    [Fact]
    public async Task PropertyChanged_ChangedProperties_ContainsChangedName()
    {
        var device = MakeDevice(batteryChargePercent: 80);
        var (watcher, _, monitor) = CreateWatcher(device);
        DevicePropertyChangedEventArgs? received = null;
        watcher.PropertyChanged += (_, e) => received = e;

        await watcher.StartAsync();

        var updated = device with { BatteryChargePercent = 79 };
        monitor.SimulatePropertyChange(device, updated);

        Assert.Contains(nameof(DeviceInfo.BatteryChargePercent), received!.ChangedProperties);
        await watcher.DisposeAsync();
    }

    [Fact]
    public async Task PropertyChanged_ChangedProperties_DoesNotContainUnchangedName()
    {
        var device = MakeDevice(batteryChargePercent: 80);
        var (watcher, _, monitor) = CreateWatcher(device);
        DevicePropertyChangedEventArgs? received = null;
        watcher.PropertyChanged += (_, e) => received = e;

        await watcher.StartAsync();

        var updated = device with { BatteryChargePercent = 79 };
        monitor.SimulatePropertyChange(device, updated);

        Assert.DoesNotContain(nameof(DeviceInfo.Name), received!.ChangedProperties);
        await watcher.DisposeAsync();
    }

    [Fact]
    public async Task PropertyChanged_MultipleChanges_AllReported()
    {
        var device = MakeDevice(batteryChargePercent: 80);
        var (watcher, _, monitor) = CreateWatcher(device);
        DevicePropertyChangedEventArgs? received = null;
        watcher.PropertyChanged += (_, e) => received = e;

        await watcher.StartAsync();

        var updated = device with
        {
            BatteryChargePercent = 50,
            BatteryStatus = BatteryStatus.Discharging,
        };
        monitor.SimulatePropertyChange(device, updated);

        Assert.Contains(nameof(DeviceInfo.BatteryChargePercent),     received!.ChangedProperties);
        Assert.Contains(nameof(DeviceInfo.BatteryStatus),            received.ChangedProperties);
        await watcher.DisposeAsync();
    }

    // ── Watcher filter ─────────────────────────────────────────────────

    [Fact]
    public async Task PropertyChanged_WatcherFilter_SuppressesNonMatchingDevices()
    {
        var battery = MakeDevice(id: "BAT\\1");
        var usb = new DeviceInfo { Id = "USB\\1", Category = DeviceCategory.Usb, IsActive = true };
        var provider = new FakeDeviceProvider(battery, usb);
        var monitor = new FakeDeviceMonitorProvider();
        var watcher = new DeviceWatcher(provider, monitor);
        watcher.OfCategory(DeviceCategory.Battery);

        var received = new List<string>();
        watcher.PropertyChanged += (_, e) => received.Add(e.Current.Id);

        await watcher.StartAsync();

        // Change on the USB device — should be suppressed by the battery filter
        monitor.SimulatePropertyChange(usb, usb with { Name = "Updated USB" });
        // Change on the battery device — should fire
        monitor.SimulatePropertyChange(battery, battery with { BatteryChargePercent = 79 });

        Assert.DoesNotContain("USB\\1", received);
        Assert.Contains("BAT\\1", received);

        await watcher.DisposeAsync();
    }

    [Fact]
    public async Task PropertyChanged_IdenticalSnapshots_DoesNotFire()
    {
        var device = MakeDevice(batteryChargePercent: 80);
        var (watcher, _, monitor) = CreateWatcher(device);
        int fireCount = 0;
        watcher.PropertyChanged += (_, _) => fireCount++;

        await watcher.StartAsync();

        // Simulate a modification event where nothing actually changed
        monitor.SimulatePropertyChange(device, device);

        Assert.Equal(0, fireCount);
        await watcher.DisposeAsync();
    }

    // ── IsActive is included in the diff ───────────────────────────

    [Fact]
    public async Task PropertyChanged_IsActiveTransition_IncludedInChangedProperties()
    {
        var device = MakeDevice(isActive: true);
        var (watcher, _, monitor) = CreateWatcher(device);
        DevicePropertyChangedEventArgs? received = null;
        watcher.PropertyChanged += (_, e) => received = e;

        await watcher.StartAsync();

        var disconnected = device with { IsActive = false };
        monitor.SimulatePropertyChange(device, disconnected);

        Assert.Contains(nameof(DeviceInfo.IsActive), received!.ChangedProperties);
        await watcher.DisposeAsync();
    }

    // ── Tracker Device is updated ──────────────────────────────────────

    [Fact]
    public async Task PropertyChanged_UpdatesTrackerDevice_ToNewSnapshot()
    {
        var device = MakeDevice(batteryChargePercent: 80);
        var (watcher, _, monitor) = CreateWatcher(device);
        var tracker = watcher.AddTracker(f => f.OfCategory(DeviceCategory.Battery), "Battery");

        await watcher.StartAsync();

        // Snapshot connects the device to the tracker
        var updated = device with { BatteryChargePercent = 79 };
        monitor.SimulatePropertyChange(device, updated);

        Assert.Equal(79, tracker.Device!.BatteryChargePercent);
        await watcher.DisposeAsync();
    }

    [Fact]
    public async Task PropertyChanged_NonResolvedDevice_DoesNotUpdateTrackerDevice()
    {
        var resolvedDevice = MakeDevice(id: "BAT\\1", batteryChargePercent: 80);
        var otherDevice = MakeDevice(id: "BAT\\2", batteryChargePercent: 50);

        // Only put resolvedDevice in snapshot so it latches
        var (watcher, _, monitor) = CreateWatcher(resolvedDevice);
        var tracker = watcher.AddTracker(f => f.OfCategory(DeviceCategory.Battery), "Battery");

        await watcher.StartAsync();
        Assert.Equal("BAT\\1", tracker.Device!.Id);

        // Simulate a property change on a device that is NOT the resolved one
        monitor.SimulatePropertyChange(otherDevice, otherDevice with { BatteryChargePercent = 40 });

        // Tracker's resolved Device should not change
        Assert.Equal("BAT\\1", tracker.Device!.Id);
        Assert.Equal(80, tracker.Device!.BatteryChargePercent);
        await watcher.DisposeAsync();
    }

    // ── No-op when no handlers ─────────────────────────────────────────

    [Fact]
    public async Task PropertyChanged_NoHandlers_DoesNotThrow()
    {
        var device = MakeDevice(batteryChargePercent: 80);
        var (watcher, _, monitor) = CreateWatcher(device);
        // Deliberately no PropertyChanged handler

        await watcher.StartAsync();

        var ex = Record.Exception(() =>
            monitor.SimulatePropertyChange(device, device with { BatteryChargePercent = 79 }));

        Assert.Null(ex);
        await watcher.DisposeAsync();
    }
}
