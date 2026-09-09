namespace Periphery.Tests;

/// <summary>
/// Issue #143: a throwing subscriber used to unwind whichever thread raised the
/// event. On the live path that is the platform provider's notification pump, and
/// every tracker in the process reports through it — so one bad handler left the
/// application silently blind with its trackers frozen. These pin that a throw is
/// isolated at every raise site and in the tracker fan-out.
/// </summary>
public class DeviceWatcherHandlerIsolationTests
{
    private static DeviceInfo MakeDevice(
        string id = "USB\\VID_046D&PID_C52B\\1", bool isActive = true) => new()
    {
        Id = id,
        Name = "Test Device",
        Category = DeviceCategory.Usb,
        IsActive = isActive,
        Status = DeviceStatus.OK,
    };

    private static (DeviceWatcher Watcher, FakeDeviceMonitorProvider Monitor) CreateWatcher(
        params DeviceInfo[] snapshotDevices)
    {
        var monitor = new FakeDeviceMonitorProvider();
        return (new DeviceWatcher(new FakeDeviceProvider(snapshotDevices), monitor), monitor);
    }

    private static Exception Boom(string where) => new InvalidOperationException($"handler blew up on {where}");

    // ── The pump survives a throwing handler, on every event ────────────

    [Fact]
    public async Task ThrowingAppearedHandler_DoesNotUnwindTheLivePump()
    {
        var (watcher, monitor) = CreateWatcher();
        await using var _ = watcher;
        watcher.Appeared += (_, _) => throw Boom("Appeared");
        await watcher.StartAsync();

        // Pre-fix this threw out of SimulateConnect — i.e. out of the pump.
        monitor.SimulateConnect(MakeDevice());
    }

    [Fact]
    public async Task ThrowingActivatedHandler_DoesNotUnwindTheLivePump()
    {
        var (watcher, monitor) = CreateWatcher();
        await using var _ = watcher;
        watcher.Activated += (_, _) => throw Boom("Activated");
        await watcher.StartAsync();

        monitor.SimulateActivatedEdge(MakeDevice());
    }

    [Fact]
    public async Task ThrowingDeactivatedHandler_DoesNotUnwindTheLivePump()
    {
        var (watcher, monitor) = CreateWatcher();
        await using var _ = watcher;
        watcher.Deactivated += (_, _) => throw Boom("Deactivated");
        await watcher.StartAsync();

        monitor.SimulateConnect(MakeDevice());
        monitor.SimulateStatusChange(MakeDevice(isActive: false));
    }

    [Fact]
    public async Task ThrowingDisappearedHandler_DoesNotUnwindTheLivePump()
    {
        var (watcher, monitor) = CreateWatcher();
        await using var _ = watcher;
        watcher.Disappeared += (_, _) => throw Boom("Disappeared");
        await watcher.StartAsync();

        monitor.SimulateConnect(MakeDevice());
        monitor.SimulateDisconnect(MakeDevice());
    }

    [Fact]
    public async Task ThrowingPropertyChangedHandler_DoesNotUnwindTheLivePump()
    {
        var (watcher, monitor) = CreateWatcher();
        await using var _ = watcher;
        watcher.PropertyChanged += (_, _) => throw Boom("PropertyChanged");
        await watcher.StartAsync();

        var before = MakeDevice();
        monitor.SimulateConnect(before);
        monitor.SimulatePropertyChange(before, before with { Name = "Renamed" });
    }

    [Fact]
    public async Task ThrowingAppearedHandler_DoesNotUnwindTheStartupSnapshot()
    {
        var (watcher, _) = CreateWatcher(MakeDevice(id: "USB\\1"), MakeDevice(id: "USB\\2"));
        await using var _w = watcher;
        watcher.Appeared += (_, _) => throw Boom("snapshot Appeared");

        // The snapshot walk runs inside StartAsync; a throw here used to fail the
        // start outright, leaving the watcher unusable. A settled start is what
        // populates the replay cache, so this is the observable proof it finished.
        await watcher.StartAsync();

        Assert.Equal(2, watcher.KnownDevices.Count);
    }

    // ── Subscribers behind a throwing one still run ─────────────────────

    [Fact]
    public async Task ASubscriberRegisteredAfterAThrowingOne_StillReceivesTheEvent()
    {
        var (watcher, monitor) = CreateWatcher();
        await using var _ = watcher;
        var seen = new List<string>();

        watcher.Appeared += (_, _) => throw Boom("first");
        watcher.Appeared += (_, e) => seen.Add(e.Device.Id);
        await watcher.StartAsync();

        monitor.SimulateConnect(MakeDevice(id: "USB\\LATER"));

        // A multicast invoke abandons the walk at the first throw, so pre-fix the
        // second subscriber never ran and which subscribers survived depended on
        // registration order.
        Assert.Equal(["USB\\LATER"], seen);
    }

    [Fact]
    public async Task EverySurvivingSubscriberRuns_WhenTheThrowerIsInTheMiddle()
    {
        var (watcher, monitor) = CreateWatcher();
        await using var _ = watcher;
        var seen = new List<int>();

        watcher.Appeared += (_, _) => seen.Add(1);
        watcher.Appeared += (_, _) => throw Boom("middle");
        watcher.Appeared += (_, _) => seen.Add(3);
        await watcher.StartAsync();

        monitor.SimulateConnect(MakeDevice());

        Assert.Equal([1, 3], seen);
    }

    // ── Tracker fan-out gets the same treatment ─────────────────────────

    [Fact]
    public async Task AThrowingTracker_DoesNotStopTheTrackersBehindIt()
    {
        var (watcher, monitor) = CreateWatcher();
        await using var _ = watcher;

        var first = new DeviceTracker(new DeviceFilter(), "throws");
        var second = new DeviceTracker(new DeviceFilter(), "records");
        watcher.AddTracker(first);
        watcher.AddTracker(second);

        // A tracker notification raises StateChanged, which is what drives every
        // DeviceProxyBase in the process — so consumer code runs here too.
        first.StateChanged += (_, _) => throw Boom("tracker StateChanged");
        var secondSaw = new List<string?>();
        second.StateChanged += (_, s) => secondSaw.Add(s.Device?.Id);

        await watcher.StartAsync();
        monitor.SimulateConnect(MakeDevice(id: "USB\\FANOUT"));

        Assert.Contains("USB\\FANOUT", secondSaw);
    }

    [Fact]
    public async Task AThrowingTracker_DoesNotUnwindThePump()
    {
        var (watcher, monitor) = CreateWatcher();
        await using var _ = watcher;

        var tracker = new DeviceTracker(new DeviceFilter(), "throws");
        watcher.AddTracker(tracker);
        tracker.StateChanged += (_, _) => throw Boom("tracker StateChanged");

        await watcher.StartAsync();

        monitor.SimulateConnect(MakeDevice());
        monitor.SimulateDisconnect(MakeDevice());
    }

    // ── The watcher keeps working afterwards ────────────────────────────

    [Fact]
    public async Task AThrowingHandlerDoesNotPoisonLaterEvents()
    {
        var (watcher, monitor) = CreateWatcher();
        await using var _ = watcher;
        var appeared = new List<string>();

        watcher.Appeared += (_, e) =>
        {
            if (e.Device.Id == "USB\\BAD") throw Boom("one device only");
            appeared.Add(e.Device.Id);
        };
        await watcher.StartAsync();

        monitor.SimulateConnect(MakeDevice(id: "USB\\BAD"));
        monitor.SimulateConnect(MakeDevice(id: "USB\\GOOD"));

        Assert.Equal(["USB\\GOOD"], appeared);
    }
}
