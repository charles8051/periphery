namespace Periphery.Tests;

public class MultiDeviceSessionHostTests
{
    private sealed class FakeSession
    {
        public required string DeviceId { get; init; }
        public int Value { get; init; }
    }

    private static DeviceInfo MakeDevice(
        string id,
        bool isActive = true,
        DeviceCategory category = DeviceCategory.Usb) => new()
    {
        Id = id,
        Name = $"Device {id}",
        Category = category,
        IsActive = isActive,
        VendorId = new HardwareId(0x0001),
        ProductId = new HardwareId(0x0002),
    };

    // ── Create factory ─────────────────────────────────────────────────

    [Fact]
    public async Task Create_ExistingDevices_CreatesSessionHosts()
    {
        var d1 = MakeDevice("USB\\1");
        var d2 = MakeDevice("USB\\2");

        var provider = new FakeDeviceProvider(d1, d2);
        var monitor = new FakeDeviceMonitorProvider();

        await using var watcher = new DeviceWatcher(provider, monitor);
        var group = watcher.AddMultiTracker(
            f => f.OfCategory(DeviceCategory.Usb), name: "USBDevices");

        await watcher.StartAsync();

        await using var host = MultiDeviceSessionHost<FakeSession>.Create(
            group,
            createSession: (info, _) => Task.FromResult(
                new FakeSession { DeviceId = info.Id }));

        Assert.Equal(2, host.Count);
        Assert.True(host.Hosts.ContainsKey("USB\\1"));
        Assert.True(host.Hosts.ContainsKey("USB\\2"));
    }

    [Fact]
    public async Task Create_NewDevice_CreatesSessionHostDynamically()
    {
        var provider = FakeDeviceProvider.Empty();
        var monitor = new FakeDeviceMonitorProvider();

        await using var watcher = new DeviceWatcher(provider, monitor);
        var group = watcher.AddMultiTracker(
            f => f.OfCategory(DeviceCategory.Usb), name: "USBDevices");

        await watcher.StartAsync();

        DeviceSessionHost<FakeSession>? addedHost = null;
        await using var host = MultiDeviceSessionHost<FakeSession>.Create(
            group,
            createSession: (info, _) => Task.FromResult(
                new FakeSession { DeviceId = info.Id }));

        host.SessionHostAdded += (_, h) => addedHost = h;

        Assert.Empty(host.Hosts);

        // Simulate new device
        monitor.SimulateConnect(MakeDevice("USB\\NEW"));

        Assert.Single(host.Hosts);
        Assert.True(host.Hosts.ContainsKey("USB\\NEW"));
        Assert.NotNull(addedHost);
    }

    [Fact]
    public async Task Create_SessionActivates_WhenDeviceActive()
    {
        var device = MakeDevice("USB\\1");
        var provider = new FakeDeviceProvider(device);
        var monitor = new FakeDeviceMonitorProvider();

        await using var watcher = new DeviceWatcher(provider, monitor);
        var group = watcher.AddMultiTracker(
            f => f.OfCategory(DeviceCategory.Usb));

        await watcher.StartAsync();

        await using var host = MultiDeviceSessionHost<FakeSession>.Create(
            group,
            createSession: (info, _) => Task.FromResult(
                new FakeSession { DeviceId = info.Id, Value = 42 }));

        var sessionHost = host.Hosts["USB\\1"];

        // Wait for session to become active
        var session = await sessionHost.WaitForSessionAsync()
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(session);
        Assert.Equal("USB\\1", session.DeviceId);
        Assert.Equal(42, session.Value);
    }

    [Fact]
    public async Task Dispose_DisposesAllSessionHosts()
    {
        var device = MakeDevice("USB\\1");
        var provider = new FakeDeviceProvider(device);
        var monitor = new FakeDeviceMonitorProvider();

        await using var watcher = new DeviceWatcher(provider, monitor);
        var group = watcher.AddMultiTracker(
            f => f.OfCategory(DeviceCategory.Usb));

        await watcher.StartAsync();

        var host = MultiDeviceSessionHost<FakeSession>.Create(
            group,
            createSession: (info, _) => Task.FromResult(
                new FakeSession { DeviceId = info.Id }));

        Assert.Single(host.Hosts);

        await host.DisposeAsync();

        // After disposal, hosts dictionary is cleared
        Assert.Empty(host.Hosts);
    }

    [Fact]
    public async Task GroupTracker_Property_ReturnsTheGroupTracker()
    {
        var provider = FakeDeviceProvider.Empty();
        var monitor = new FakeDeviceMonitorProvider();

        await using var watcher = new DeviceWatcher(provider, monitor);
        var group = watcher.AddMultiTracker(
            f => f.OfCategory(DeviceCategory.Usb));

        await watcher.StartAsync();

        await using var host = MultiDeviceSessionHost<FakeSession>.Create(
            group,
            createSession: (info, _) => Task.FromResult(
                new FakeSession { DeviceId = info.Id }));

        Assert.Same(group, host.MultiTracker);
    }

    [Fact]
    public async Task SessionHostAdded_FiresForEachNewDevice()
    {
        var provider = FakeDeviceProvider.Empty();
        var monitor = new FakeDeviceMonitorProvider();

        await using var watcher = new DeviceWatcher(provider, monitor);
        var group = watcher.AddMultiTracker(
            f => f.OfCategory(DeviceCategory.Usb));

        await watcher.StartAsync();

        var addedHosts = new List<DeviceSessionHost<FakeSession>>();
        await using var host = MultiDeviceSessionHost<FakeSession>.Create(
            group,
            createSession: (info, _) => Task.FromResult(
                new FakeSession { DeviceId = info.Id }));

        host.SessionHostAdded += (_, h) => addedHosts.Add(h);

        monitor.SimulateConnect(MakeDevice("USB\\1"));
        monitor.SimulateConnect(MakeDevice("USB\\2"));
        monitor.SimulateConnect(MakeDevice("USB\\3"));

        Assert.Equal(3, addedHosts.Count);
        Assert.Equal(3, host.Count);
    }

    [Fact]
    public void Create_NullGroupTracker_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            MultiDeviceSessionHost<FakeSession>.Create(
                null!,
                createSession: (_, _) => Task.FromResult(
                    new FakeSession { DeviceId = "" })));
    }

    [Fact]
    public void Create_NullCreateSession_Throws()
    {
        var group = new MultiDeviceTracker(f => f.OfCategory(DeviceCategory.Usb));

        Assert.Throws<ArgumentNullException>(() =>
            MultiDeviceSessionHost<FakeSession>.Create(
                group,
                createSession: null!));
    }

    // ── Reconnect-policy fan-out ───────────────────────────────────────

    /// <summary>Give-up-immediately policy: gives up on the first failure.</summary>
    private sealed class GiveUpImmediatelyPolicy : IRecoveryPolicy
    {
        public RecoveryDirective Decide(RecoveryContext context)
            => new RecoveryDirective.GiveUp();
    }

    [Fact]
    public async Task Create_ReconnectPolicyForwardedToEachHost_TerminalGaveUp()
    {
        var device = MakeDevice("USB\\1");
        var provider = new FakeDeviceProvider(device);
        var monitor = new FakeDeviceMonitorProvider();

        await using var watcher = new DeviceWatcher(provider, monitor);
        var group = watcher.AddMultiTracker(f => f.OfCategory(DeviceCategory.Usb));

        await watcher.StartAsync();

        await using var host = MultiDeviceSessionHost<FakeSession>.Create(
            group,
            createSession: (_, _) =>
                throw new InvalidOperationException("unopenable"),
            recoveryPolicy: new GiveUpImmediatelyPolicy());

        var sessionHost = host.Hosts["USB\\1"];

        // The forwarded policy must drive the per-device host to the terminal state.
        var gaveUp = await SessionHostTestHelpers.WaitForStatusAsync<
            FakeSession, SessionGaveUp<FakeSession>>(sessionHost, TimeSpan.FromSeconds(5));

        Assert.Equal(ConnectionState.GaveUp, sessionHost.ConnectionState);
        Assert.NotNull(gaveUp.LastError);
    }
}
