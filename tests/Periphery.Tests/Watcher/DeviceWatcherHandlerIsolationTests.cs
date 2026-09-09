using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

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

    // ── Review findings on the first cut of the fix ─────────────────────

    [Fact]
    public async Task ASubscriberBehindAThrowingOne_OnTheSameTracker_StillRuns()
    {
        var (watcher, monitor) = CreateWatcher();
        await using var _ = watcher;

        var tracker = new DeviceTracker(new DeviceFilter(), "one tracker, two subscribers");
        watcher.AddTracker(tracker);

        var seen = new List<string?>();
        tracker.StateChanged += (_, _) => throw Boom("first subscriber");
        tracker.StateChanged += (_, s) => seen.Add(s.Device?.Id);

        await watcher.StartAsync();
        monitor.SimulateConnect(MakeDevice(id: "USB\\SAMETRACKER"));

        // Isolating per tracker is not enough: the tracker's own StateChanged is
        // a multicast event, so an unisolated raise there still lost every
        // subscriber behind the thrower.
        Assert.Contains("USB\\SAMETRACKER", seen);
    }

    // No test for the reentrant-disposal case, deliberately. Disposal clears the
    // tracker list the fan-out walks, so a live enumeration would throw from
    // MoveNext — outside the per-target try/catch — and unwind the pump. The fix
    // is to walk a snapshot, which is correct by construction. Exercising the
    // unfixed path is not: the Clear happens after an await inside DisposeAsync,
    // so a fire-and-forget dispose races the walk on another thread (a flaky
    // test), and blocking on it from the handler deadlocks — a separate,
    // pre-existing hazard worth its own issue rather than a test here.

    // Exercised against EventIsolation directly rather than through the watcher,
    // because every type's logger is a static readonly captured at type load — a
    // factory swapped in mid-run does not reach an already-loaded type, so a test
    // driving this through DeviceWatcher would pass without proving anything.
    [Fact]
    public void AThrowingLogger_DoesNotEscapeTheIsolationItIsReporting()
    {
        EventHandler<EventArgs>? handlers = null;
        handlers += (_, _) => throw Boom("subscriber");

        // The log call happens inside the catch that keeps a handler off the
        // provider's pump. A logger that throws there would escape from the very
        // place the isolation is being reported, and unwind the pump anyway.
        EventIsolation.Raise(this, handlers, EventArgs.Empty, new ThrowingLogger(), "Test", "ctx");
    }

    [Fact]
    public void AThrowingLogger_DoesNotStopTheRemainingSubscribers()
    {
        var seen = new List<int>();
        EventHandler<EventArgs>? handlers = null;
        handlers += (_, _) => seen.Add(1);
        handlers += (_, _) => throw Boom("middle");
        handlers += (_, _) => seen.Add(3);

        EventIsolation.Raise(this, handlers, EventArgs.Empty, new ThrowingLogger(), "Test", "ctx");

        Assert.Equal([1, 3], seen);
    }

    private sealed class ThrowingLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => throw new InvalidOperationException("the logging sink is down");
    }

    // ── Registration cannot outlive disposal ────────────────────────────

    [Fact]
    public async Task RegisteringAfterDisposal_IsRefusedRatherThanLeavingATrackerBound()
    {
        var (watcher, _) = CreateWatcher();
        await watcher.StartAsync();
        await watcher.DisposeAsync();

        // Adding after disposal would bind the tracker to a watcher that has
        // already walked its list, so nothing would ever unbind it.
        Assert.Throws<ObjectDisposedException>(
            () => watcher.AddTracker(new DeviceTracker(new DeviceFilter(), "too late")));
    }

    [Fact]
    public async Task ATrackerRegisteredByAnUnbindSubscriber_IsRefused()
    {
        // The tracker must hold a device, or Unbind is an Absent -> Absent no-op
        // and raises nothing for the subscriber to run in.
        var (watcher, _) = CreateWatcher(MakeDevice());
        var registrar = new DeviceTracker(new DeviceFilter(), "registers during unbind");
        watcher.AddTracker(registrar);

        Exception? observed = null;
        registrar.StateChanged += (_, _) =>
        {
            // Unbind runs consumer code during disposal. A tracker added here
            // would land in a list disposal has already emptied and stay bound
            // for the life of the process.
            try { watcher.AddTracker(new DeviceTracker(new DeviceFilter(), "smuggled in")); }
            catch (Exception ex) { observed ??= ex; }
        };

        await watcher.StartAsync();
        await watcher.DisposeAsync();

        Assert.IsType<ObjectDisposedException>(observed);
    }

    // ── The counter is the signal that survives configuration (#225) ────

    [Fact]
    public async Task AnIsolatedHandlerFault_IsCounted_TaggedWithTheEvent()
    {
        var faults = new List<string?>();
        using var listener = StartFaultListener(faults);

        var (watcher, monitor) = CreateWatcher();
        await using var _ = watcher;
        watcher.Appeared += (_, _) => throw Boom("Appeared");
        await watcher.StartAsync();

        monitor.SimulateConnect(MakeDevice());

        Assert.Contains("Appeared", faults);
    }

    [Fact]
    public void TheCounterIsIncrementedEvenWhenTheLoggerDropsEverything()
    {
        var faults = new List<string?>();
        using var listener = StartFaultListener(faults);

        EventHandler<EventArgs>? handlers = null;
        handlers += (_, _) => throw Boom("subscriber");

        // A logger that throws stands in for the whole class of loggers that
        // carry nothing — filtered above Error, no provider registered, a sink
        // that is down. The counter is the half that still fires (#225).
        EventIsolation.Raise(this, handlers, EventArgs.Empty, new ThrowingLogger(), "Filtered", "ctx");

        Assert.Equal(["Filtered"], faults);
    }

    [Fact]
    public void ATargetFault_IsCountedSeparatelyFromAHandlerFault()
    {
        // A tracker's own subscribers are isolated inside the tracker, so a fault
        // that surfaces at the watcher's fan-out is the tracker failing in its own
        // code — a library fault, not consumer code misbehaving. Folding the two
        // into one counter would bury the more alarming of them (#225 review).
        //
        // Driven through EventIsolation directly: with every notification path
        // isolated, a target fault is by design hard to provoke end to end, and a
        // test that cannot provoke one would prove nothing about the split.
        var targets = new List<string?>();
        var handlers = new List<string?>();
        using var targetListener = StartFaultListener(targets, "periphery.events.target_faults");
        using var handlerListener = StartFaultListener(handlers);

        EventIsolation.LogTargetFaulted(
            new ThrowingLogger(), Boom("tracker"), "Appeared", "tracker", "a tracker", "USB-TEST-1");

        Assert.Equal(["Appeared"], targets);
        Assert.Empty(handlers);
    }

    private static MeterListener StartFaultListener(
        List<string?> faults, string instrumentName = "periphery.events.handler_faults")
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == "Periphery" && instrument.Name == instrumentName)
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "periphery.event")
                {
                    lock (faults) faults.Add(tag.Value as string);
                }
            }
        });
        listener.Start();
        return listener;
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
