namespace Periphery.Tests;

/// <summary>
/// Runs the full <see cref="DeviceMonitorProviderContractTests"/> suite against
/// <see cref="FakeDeviceMonitorProvider"/>, and adds tests for the simulation helpers
/// that all <see cref="DeviceWatcher"/> tests depend on.
/// </summary>
/// <remarks>
/// The simulation helpers are foundational infrastructure. If <c>SimulateConnect</c>
/// fires the wrong events, every watcher test built on top is testing a fiction.
/// Covering the helpers here catches that class of bug at the source.
/// </remarks>
public sealed class FakeMonitorProviderContractTests : DeviceMonitorProviderContractTests
{
    protected override object CreateMonitorCore() => new FakeDeviceMonitorProvider();

    // ── Simulate methods require StartAsync ────────────────────────────

    [Fact]
    public void SimulateConnect_BeforeStart_ThrowsInvalidOperationException()
    {
        var monitor = new FakeDeviceMonitorProvider();

        Assert.Throws<InvalidOperationException>(() => monitor.SimulateConnect(ADevice()));
    }

    [Fact]
    public void SimulateDisconnect_BeforeStart_ThrowsInvalidOperationException()
    {
        var monitor = new FakeDeviceMonitorProvider();

        Assert.Throws<InvalidOperationException>(() => monitor.SimulateDisconnect(ADevice()));
    }

    [Fact]
    public void SimulateStatusChange_BeforeStart_ThrowsInvalidOperationException()
    {
        var monitor = new FakeDeviceMonitorProvider();

        Assert.Throws<InvalidOperationException>(() => monitor.SimulateStatusChange(ADevice()));
    }

    [Fact]
    public void SimulatePropertyChange_BeforeStart_ThrowsInvalidOperationException()
    {
        var monitor = new FakeDeviceMonitorProvider();
        var device  = ADevice();

        Assert.Throws<InvalidOperationException>(() => monitor.SimulatePropertyChange(device, device));
    }

    // ── SimulateConnect event semantics ────────────────────────────────

    [Fact]
    public async Task SimulateConnect_ConnectedDevice_FiresAppearedAndConnected()
    {
        var monitor = new FakeDeviceMonitorProvider();
        await monitor.StartAsync(new DeviceFilter());

        var appeared  = new List<DeviceInfo>();
        var activated = new List<DeviceInfo>();
        monitor.DeviceAppeared  += (_, e) => appeared.Add(e.Device);
        monitor.DeviceActivated += (_, e) => activated.Add(e.Device);

        var device = ADevice(isActive: true);
        monitor.SimulateConnect(device);

        Assert.Single(appeared);
        Assert.Single(activated);
        Assert.Equal(device.Id, appeared[0].Id);
        Assert.Equal(device.Id, activated[0].Id);
    }

    [Fact]
    public async Task SimulateConnect_DisconnectedDevice_FiresOnlyAppeared()
    {
        var monitor = new FakeDeviceMonitorProvider();
        await monitor.StartAsync(new DeviceFilter());

        var appeared  = new List<DeviceInfo>();
        var activated = new List<DeviceInfo>();
        monitor.DeviceAppeared  += (_, e) => appeared.Add(e.Device);
        monitor.DeviceActivated += (_, e) => activated.Add(e.Device);

        monitor.SimulateConnect(ADevice(isActive: false));

        Assert.Single(appeared);
        Assert.Empty(activated);
    }

    [Fact]
    public async Task SimulateConnect_DoesNotFireDisappearedDisconnectedOrPropertyChanged()
    {
        var monitor = new FakeDeviceMonitorProvider();
        await monitor.StartAsync(new DeviceFilter());

        var unexpected = new List<string>();
        monitor.DeviceDisappeared    += (_, _) => unexpected.Add(nameof(monitor.DeviceDisappeared));
        monitor.DeviceDeactivated    += (_, _) => unexpected.Add(nameof(monitor.DeviceDeactivated));
        monitor.DevicePropertyChanged += (_, _) => unexpected.Add(nameof(monitor.DevicePropertyChanged));

        monitor.SimulateConnect(ADevice(isActive: true));

        Assert.Empty(unexpected);
    }

    // ── SimulateDisconnect event semantics ─────────────────────────────

    [Fact]
    public async Task SimulateDisconnect_FiresOnlyDisappeared()
    {
        var monitor = new FakeDeviceMonitorProvider();
        await monitor.StartAsync(new DeviceFilter());

        var disappeared  = new List<DeviceInfo>();
        var deactivated = new List<DeviceInfo>();
        monitor.DeviceDisappeared  += (_, e) => disappeared.Add(e.Device);
        monitor.DeviceDeactivated  += (_, e) => deactivated.Add(e.Device);

        monitor.SimulateDisconnect(ADevice());

        Assert.Single(disappeared);
        Assert.Empty(deactivated);
    }

    [Fact]
    public async Task SimulateDisconnect_DoesNotFireAppearedConnectedOrPropertyChanged()
    {
        var monitor = new FakeDeviceMonitorProvider();
        await monitor.StartAsync(new DeviceFilter());

        var unexpected = new List<string>();
        monitor.DeviceAppeared       += (_, _) => unexpected.Add(nameof(monitor.DeviceAppeared));
        monitor.DeviceActivated      += (_, _) => unexpected.Add(nameof(monitor.DeviceActivated));
        monitor.DevicePropertyChanged += (_, _) => unexpected.Add(nameof(monitor.DevicePropertyChanged));

        monitor.SimulateDisconnect(ADevice());

        Assert.Empty(unexpected);
    }

    // ── SimulateStatusChange event semantics ───────────────────────────

    [Fact]
    public async Task SimulateStatusChange_ActiveDevice_FiresOnlyActivated()
    {
        var monitor = new FakeDeviceMonitorProvider();
        await monitor.StartAsync(new DeviceFilter());

        var activated   = new List<DeviceInfo>();
        var deactivated = new List<DeviceInfo>();
        monitor.DeviceActivated    += (_, e) => activated.Add(e.Device);
        monitor.DeviceDeactivated  += (_, e) => deactivated.Add(e.Device);

        monitor.SimulateStatusChange(ADevice(isActive: true));

        Assert.Single(activated);
        Assert.Empty(deactivated);
    }

    [Fact]
    public async Task SimulateStatusChange_InactiveDevice_FiresOnlyDeactivated()
    {
        var monitor = new FakeDeviceMonitorProvider();
        await monitor.StartAsync(new DeviceFilter());

        var activated   = new List<DeviceInfo>();
        var deactivated = new List<DeviceInfo>();
        monitor.DeviceActivated    += (_, e) => activated.Add(e.Device);
        monitor.DeviceDeactivated  += (_, e) => deactivated.Add(e.Device);

        monitor.SimulateStatusChange(ADevice(isActive: false));

        Assert.Empty(activated);
        Assert.Single(deactivated);
    }

    [Fact]
    public async Task SimulateStatusChange_DoesNotFireAppearedDisappearedOrPropertyChanged()
    {
        var monitor = new FakeDeviceMonitorProvider();
        await monitor.StartAsync(new DeviceFilter());

        var unexpected = new List<string>();
        monitor.DeviceAppeared       += (_, _) => unexpected.Add(nameof(monitor.DeviceAppeared));
        monitor.DeviceDisappeared    += (_, _) => unexpected.Add(nameof(monitor.DeviceDisappeared));
        monitor.DevicePropertyChanged += (_, _) => unexpected.Add(nameof(monitor.DevicePropertyChanged));

        monitor.SimulateStatusChange(ADevice(isActive: true));

        Assert.Empty(unexpected);
    }

    // ── SimulatePropertyChange event semantics

    [Fact]
    public async Task SimulatePropertyChange_FiresPropertyChangedWithCorrectSnapshots()
    {
        var monitor = new FakeDeviceMonitorProvider();
        await monitor.StartAsync(new DeviceFilter());

        DeviceModificationEventArgs? received = null;
        monitor.DevicePropertyChanged += (_, e) => received = e;

        var prev = ADevice(batteryPercent: 80);
        var curr = prev with { BatteryChargePercent = 79 };
        monitor.SimulatePropertyChange(prev, curr);

        Assert.NotNull(received);
        Assert.Equal(80, received.Previous.BatteryChargePercent);
        Assert.Equal(79, received.Current.BatteryChargePercent);
        Assert.Equal(prev.Id, received.Previous.Id);
        Assert.Equal(curr.Id, received.Current.Id);
    }

    [Fact]
    public async Task SimulatePropertyChange_DoesNotFireAppearedDisappearedConnectedOrDisconnected()
    {
        var monitor = new FakeDeviceMonitorProvider();
        await monitor.StartAsync(new DeviceFilter());

        var unexpected = new List<string>();
        monitor.DeviceAppeared     += (_, _) => unexpected.Add(nameof(monitor.DeviceAppeared));
        monitor.DeviceDisappeared  += (_, _) => unexpected.Add(nameof(monitor.DeviceDisappeared));
        monitor.DeviceActivated    += (_, _) => unexpected.Add(nameof(monitor.DeviceActivated));
        monitor.DeviceDeactivated  += (_, _) => unexpected.Add(nameof(monitor.DeviceDeactivated));

        var device = ADevice(batteryPercent: 80);
        monitor.SimulatePropertyChange(device, device with { BatteryChargePercent = 79 });

        Assert.Empty(unexpected);
    }

    // ── Multi-event integrity ──────────────────────────────────────────

    [Fact]
    public async Task SimulateConnect_MultipleDevices_EachFiresIndependently()
    {
        var monitor = new FakeDeviceMonitorProvider();
        await monitor.StartAsync(new DeviceFilter());

        var appeared = new List<string>();
        monitor.DeviceAppeared += (_, e) => appeared.Add(e.Device.Id);

        monitor.SimulateConnect(ADevice("DEV\\A"));
        monitor.SimulateConnect(ADevice("DEV\\B"));
        monitor.SimulateConnect(ADevice("DEV\\C"));

        Assert.Equal(3, appeared.Count);
        Assert.Contains("DEV\\A", appeared);
        Assert.Contains("DEV\\B", appeared);
        Assert.Contains("DEV\\C", appeared);
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private static DeviceInfo ADevice(
        string id = "USB\\VID_046D&PID_C52B\\1",
        bool isActive = true,
        int? batteryPercent = null) => new()
    {
        Id = id,
        Name = "Test Device",
        Category = DeviceCategory.Usb,
        IsActive = isActive,
        Status = DeviceStatus.OK,
        BatteryChargePercent = batteryPercent,
    };
}
