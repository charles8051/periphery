using System.Diagnostics.Metrics;

namespace Periphery.Tests;

/// <summary>
/// Issue #229: a caller-supplied <c>Where</c> predicate is invoked by
/// <c>Matches</c>, and those calls sat outside the isolation #143 put around
/// every raise and every notification. A predicate that throws on one odd device
/// therefore still unwound the provider's notification pump. These pin that it no
/// longer can, and that the answer substituted for the predicate is directional.
/// </summary>
public class DeviceWatcherFilterIsolationTests
{
    private static DeviceInfo MakeDevice(string id = "USB\\VID_046D&PID_C52B\\1", bool isActive = true) => new()
    {
        Id = id,
        Name = "Test Device",
        Category = DeviceCategory.Usb,
        IsActive = isActive,
        Status = DeviceStatus.OK,
    };

    /// <summary>
    /// A predicate that answers normally until it is armed, so a device can be
    /// admitted on arrival and then have the filter break under it — the shape
    /// that produces the leak, and the reason the fallback is directional.
    /// </summary>
    private sealed class Trip
    {
        public bool Armed;
        public bool Matches(DeviceInfo _) => Armed ? throw new InvalidOperationException("predicate blew up") : true;
    }

    private static (DeviceWatcher Watcher, FakeDeviceMonitorProvider Monitor) CreateWatcher(
        Func<DeviceInfo, bool> predicate, params DeviceInfo[] snapshot)
    {
        var monitor = new FakeDeviceMonitorProvider();
        var watcher = new DeviceWatcher(new FakeDeviceProvider(snapshot), monitor);
        watcher.Where(predicate);
        return (watcher, monitor);
    }

    // ── The pump survives a throwing predicate ──────────────────────────

    [Fact]
    public async Task AThrowingPredicate_DoesNotUnwindTheLivePump()
    {
        var (watcher, monitor) = CreateWatcher(_ => throw new InvalidOperationException("predicate blew up"));
        await using var _ = watcher;
        await watcher.StartAsync();

        // Pre-fix each of these threw out of the provider callback.
        monitor.SimulateConnect(MakeDevice());
        monitor.SimulateStatusChange(MakeDevice(isActive: false));
        monitor.SimulateDisconnect(MakeDevice());
    }

    [Fact]
    public async Task AThrowingPredicate_DoesNotUnwindTheStartupSnapshot()
    {
        var (watcher, _m) = CreateWatcher(
            _ => throw new InvalidOperationException("predicate blew up"),
            MakeDevice(id: "USB\\1"), MakeDevice(id: "USB\\2"));
        await using var _ = watcher;

        await watcher.StartAsync();

        // The snapshot walk consults the filter per device; a throw used to fail
        // the start outright. KnownDevices applies the filter on read and is a
        // caller-facing property, so it still surfaces the fault — which is the
        // documented split, not an oversight.
        Assert.Throws<InvalidOperationException>(() => watcher.KnownDevices);
    }

    // ── The substituted answer is directional ───────────────────────────

    [Fact]
    public async Task APredicateThatBreaksAfterArrival_StillReportsTheRemoval()
    {
        // The leak ADR-0084 D1 refused to accept: announced on arrival, then never
        // un-announced, so the consumer believes the device is present forever.
        var trip = new Trip();
        var (watcher, monitor) = CreateWatcher(trip.Matches);
        await using var _ = watcher;

        var appeared = new List<string>();
        var disappeared = new List<string>();
        watcher.Appeared += (_, e) => appeared.Add(e.Device.Id);
        watcher.Disappeared += (_, e) => disappeared.Add(e.Device.Id);
        await watcher.StartAsync();

        monitor.SimulateConnect(MakeDevice(id: "USB\\LEAK"));
        Assert.Equal(["USB\\LEAK"], appeared);

        trip.Armed = true;
        monitor.SimulateDisconnect(MakeDevice(id: "USB\\LEAK"));

        Assert.Equal(["USB\\LEAK"], disappeared);
    }

    [Fact]
    public async Task APredicateThatThrowsOnArrival_DoesNotAnnounceTheDevice()
    {
        // The other direction: nothing is announced on the strength of a predicate
        // that could not answer.
        var (watcher, monitor) = CreateWatcher(_ => throw new InvalidOperationException("predicate blew up"));
        await using var _ = watcher;

        var appeared = new List<string>();
        watcher.Appeared += (_, e) => appeared.Add(e.Device.Id);
        await watcher.StartAsync();

        monitor.SimulateConnect(MakeDevice());

        Assert.Empty(appeared);
    }

    // ── The fault is counted, with the answer used ──────────────────────

    [Fact]
    public async Task AFilterFault_IsCounted_TaggedWithTheAnswerSubstituted()
    {
        var fallbacks = new List<string?>();
        using var listener = StartFilterFaultListener(fallbacks);

        var trip = new Trip();
        var (watcher, monitor) = CreateWatcher(trip.Matches);
        await using var _ = watcher;
        await watcher.StartAsync();

        monitor.SimulateConnect(MakeDevice(id: "USB\\COUNTED"));
        trip.Armed = true;
        monitor.SimulateDisconnect(MakeDevice(id: "USB\\COUNTED"));

        // The removal path answered "announced"; a suppressed arrival would be
        // distinguishable from it rather than one undifferentiated number.
        Assert.Contains("announced", fallbacks);
    }

    [Fact]
    public async Task AThrowingPredicateOnArrival_IsCountedAsSuppressed()
    {
        var fallbacks = new List<string?>();
        using var listener = StartFilterFaultListener(fallbacks);

        var (watcher, monitor) = CreateWatcher(_ => throw new InvalidOperationException("predicate blew up"));
        await using var _ = watcher;
        await watcher.StartAsync();

        monitor.SimulateConnect(MakeDevice());

        Assert.Contains("suppressed", fallbacks);
    }

    private static MeterListener StartFilterFaultListener(List<string?> fallbacks)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == "Periphery"
                    && instrument.Name == "periphery.events.filter_faults")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "periphery.filter_fallback")
                {
                    lock (fallbacks) fallbacks.Add(tag.Value as string);
                }
            }
        });
        listener.Start();
        return listener;
    }
}
