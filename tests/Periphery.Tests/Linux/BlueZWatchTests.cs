using Microsoft.Extensions.Logging;
using Periphery.Linux.BlueZ.Core;
using Periphery.Linux.DBus.Core;
using static Periphery.Tests.Linux.BlueZObjectsBuilder;

namespace Periphery.Tests.Linux;

/// <summary>
/// ADR-0091 D5, D6 and D8 as a pure step. Each test drives <see cref="BlueZWatch.Step"/> with
/// observations and a clock it sets, and reads edges and effects. Nothing is timed or connected.
/// </summary>
public class BlueZWatchTests
{
    private const string Bond = "bluez:00:AA:01:00:00:00/11:22:33:44:55:66";
    private static readonly DateTimeOffset T0 = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    // ── Seeding and D8 ordering ────────────────────────────────────────

    [Fact]
    public void Start_RequestsASnapshotFromTheOwner_AndTheFirstSnapshotSeedsSilently()
    {
        var step = Step(BlueZWatchState.Initial, new WatchStarted(Owner));
        Assert.Equal(new BlueZEffect[] { new RequestSnapshot(Owner) }, step.Effects);
        Assert.False(step.State.Seeded);

        var seeded = Seed(Managed(Adapter0, Bonded("dev_11", connected: true)));

        Assert.True(seeded.State.Seeded);
        Assert.Empty(seeded.Edges);
        Assert.True(Assert.Single(seeded.State.Devices).Value.IsActive);
    }

    [Fact]
    public void SignalsBeforeTheReply_AreDropped_BecauseTheReplyAlreadyHoldsThem()
    {
        var state = Step(BlueZWatchState.Initial, new WatchStarted(Owner)).State;
        state = Step(state, new SnapshotSent(7)).State;

        var early = Step(state, new MessageReceived(PropertiesChanged("dev_11", [("Connected", B(false))])));
        Assert.Same(state, early.State);

        var replied = Step(early.State, new MessageReceived(Reply(7, Managed(Adapter0, Bonded("dev_11", connected: true)))));
        Assert.True(replied.State.Devices[Bond].IsActive);
    }

    [Fact]
    public void ReplyWithAnotherSerial_OrFromAThirdParty_IsIgnored()
    {
        var state = Step(Step(BlueZWatchState.Initial, new WatchStarted(Owner)).State, new SnapshotSent(7)).State;

        Assert.Same(state, Step(state, new MessageReceived(Reply(8, Managed(Adapter0)))).State);
        Assert.Same(state, Step(state, new MessageReceived(Reply(7, Managed(Adapter0), sender: ":1.666"))).State);
    }

    // ── D5 edges ───────────────────────────────────────────────────────

    [Fact]
    public void ConnectionChange_RaisesActivated_ThenDeactivated()
    {
        var state = Seed(Managed(Adapter0, Bonded("dev_11", connected: false))).State;

        var up = Step(state, new MessageReceived(PropertiesChanged("dev_11", [("Connected", B(true))])));
        Assert.Equal(new[] { BlueZEdgeKind.Activated, BlueZEdgeKind.PropertyChanged }, up.Edges.Select(e => e.Kind));
        Assert.All(up.Edges, e => Assert.Equal(Bond, e.Device.Id.Value));

        var down = Step(up.State, new MessageReceived(PropertiesChanged("dev_11", [("Connected", B(false))])));
        Assert.Equal(new[] { BlueZEdgeKind.Deactivated, BlueZEdgeKind.PropertyChanged }, down.Edges.Select(e => e.Kind));
    }

    [Fact]
    public void RepeatedValue_RaisesNothing()
    {
        // Measured: after a restart BlueZ sends Paired=true again on the first reconnection.
        var state = Seed(Managed(Adapter0, Bonded("dev_11", connected: true))).State;

        var step = Step(state, new MessageReceived(PropertiesChanged("dev_11", [("Paired", B(true)), ("Bonded", B(true))])));

        Assert.Empty(step.Edges);
    }

    [Fact]
    public void NewBond_WhileConnected_AppearsThenActivates()
    {
        // Measured: the link comes up before pairing completes.
        var state = Seed(Managed(Adapter0)).State;
        state = Step(state, new MessageReceived(InterfacesAdded(Device("dev_11", "11:22:33:44:55:66",
            ("Paired", B(false)), ("Bonded", B(false)), ("Connected", B(false)))))).State;
        state = Step(state, new MessageReceived(PropertiesChanged("dev_11", [("Connected", B(true))]))).State;

        var paired = Step(state, new MessageReceived(PropertiesChanged("dev_11", [("Paired", B(true)), ("Bonded", B(true))])));

        Assert.Equal(new[] { BlueZEdgeKind.Appeared, BlueZEdgeKind.Activated }, paired.Edges.Select(e => e.Kind));
    }

    [Fact]
    public void Unpair_RaisesDisappeared()
    {
        var state = Seed(Managed(Adapter0, Bonded("dev_11", connected: false))).State;

        var step = Step(state, new MessageReceived(InterfacesRemoved("/org/bluez/hci0/dev_11", Owner, BlueZInventory.DeviceInterface)));

        var edge = Assert.Single(step.Edges);
        Assert.Equal((BlueZEdgeKind.Disappeared, Bond), (edge.Kind, edge.Device.Id.Value));
    }

    [Fact]
    public void AddressChange_IsANewId()
    {
        var state = Seed(Managed(Adapter0, Bonded("dev_7A", connected: true, address: "7A:00:00:00:00:01"))).State;

        var step = Step(state, new MessageReceived(PropertiesChanged("dev_7A", [("Address", S("11:22:33:44:55:66"))])));

        Assert.Equal(
            new[] { (BlueZEdgeKind.Disappeared, "bluez:00:AA:01:00:00:00/7A:00:00:00:00:01"), (BlueZEdgeKind.Appeared, Bond), (BlueZEdgeKind.Activated, Bond) },
            step.Edges.Select(e => (e.Kind, e.Device.Id.Value)));
    }

    [Fact]
    public void RenamedBond_RaisesPropertyChanged()
    {
        var state = Seed(Managed(Adapter0, Bonded("dev_11", connected: false))).State;

        var step = Step(state, new MessageReceived(PropertiesChanged("dev_11", [("Alias", S("Kitchen sensor"))])));

        var edge = Assert.Single(step.Edges);
        Assert.Equal(BlueZEdgeKind.PropertyChanged, edge.Kind);
        Assert.Equal("Kitchen sensor", edge.Device.Name);
        Assert.NotNull(edge.Previous);
    }

    [Fact]
    public void TransportEvidence_ArrivingLater_IsAPropertyChange()
    {
        // BlueZ updates UUIDs whenever GATT or SDP discovery finds services.
        var state = Seed(Managed(Adapter0, Bonded("dev_11", connected: true))).State;
        Assert.Equal(BluetoothTransports.None, state.Devices[Bond].BluetoothTransports);

        var step = Step(state, new MessageReceived(PropertiesChanged("dev_11", [("UUIDs", Uuids("00001800-0000-1000-8000-00805f9b34fb"))])));

        var edge = Assert.Single(step.Edges);
        Assert.Equal(BlueZEdgeKind.PropertyChanged, edge.Kind);
        Assert.Equal(BluetoothTransports.LowEnergy, edge.Device.BluetoothTransports);
    }

    [Fact]
    public void BearerInterfaceAdded_IsAPropertyChange()
    {
        var state = Seed(Managed(Adapter0, Bonded("dev_11", connected: false))).State;

        var step = Step(state, new MessageReceived(InterfacesAdded(Object("/org/bluez/hci0/dev_11", Interface(BlueZInventory.BrEdrBearerInterface)))));

        Assert.Equal(BluetoothTransports.BrEdr, Assert.Single(step.Edges).Device.BluetoothTransports);
    }

    [Fact]
    public void SignalFromAnyoneButTheOwner_IsIgnored()
    {
        var state = Seed(Managed(Adapter0, Bonded("dev_11", connected: false))).State;

        var step = Step(state, new MessageReceived(PropertiesChanged("dev_11", [("Connected", B(true))], sender: ":1.666")));

        Assert.Same(state, step.State);
    }

    [Fact]
    public void InvalidatedReadProperty_RequestsASnapshot_ButOthersAreDropped()
    {
        var state = Seed(Managed(Adapter0, Bonded("dev_11", connected: false))).State;

        var rssi = Step(state, new MessageReceived(PropertiesChanged("dev_11", [], ["RSSI"])));
        Assert.Empty(rssi.Effects);

        var connected = Step(state, new MessageReceived(PropertiesChanged("dev_11", [], ["Connected"])));
        Assert.Equal(new BlueZEffect[] { new RequestSnapshot(Owner) }, connected.Effects);
    }

    // ── D6 owner changes ───────────────────────────────────────────────

    [Fact]
    public void OwnerLost_EveryBondDisappears()
    {
        var state = Seed(Managed(Adapter0, Bonded("dev_11", connected: true))).State;

        var step = Step(state, new MessageReceived(OwnerChanged(Owner, "")));

        Assert.Equal((BlueZEdgeKind.Disappeared, Bond), (Assert.Single(step.Edges).Kind, step.Edges[0].Device.Id.Value));
        Assert.Null(step.State.Owner);
        Assert.Empty(step.Effects);
    }

    [Fact]
    public void OwnerChange_FromAnyoneButTheBus_OrForAnotherName_IsIgnored()
    {
        var state = Seed(Managed(Adapter0, Bonded("dev_11", connected: true))).State;

        Assert.Same(state, Step(state, new MessageReceived(OwnerChanged(Owner, ":1.666", sender: ":1.666"))).State);
        Assert.Same(state, Step(state, new MessageReceived(OwnerChanged(Owner, "", name: "org.example"))).State);
    }

    [Fact]
    public void OwnerGained_RequestsASnapshot_WhoseBondsAppear()
    {
        var state = Step(BlueZWatchState.Initial, new WatchStarted(null)).State;
        Assert.True(state.Seeded);

        var gained = Step(state, new MessageReceived(OwnerChanged("", ":1.3801")));
        Assert.Equal(new BlueZEffect[] { new RequestSnapshot(":1.3801") }, gained.Effects);

        state = Step(gained.State, new SnapshotSent(3)).State;
        var reply = Step(state, new MessageReceived(Reply(3, Managed(Adapter0, Bonded("dev_11", connected: false)), sender: ":1.3801")));

        Assert.Equal((BlueZEdgeKind.Appeared, Bond), (Assert.Single(reply.Edges).Kind, reply.Edges[0].Device.Id.Value));
    }

    // ── Failure and retry ──────────────────────────────────────────────

    [Fact]
    public void ErrorReply_RetriesAfterOneSecond_DoublingToAMinute()
    {
        var state = Step(Step(BlueZWatchState.Initial, new WatchStarted(Owner)).State, new SnapshotSent(1)).State;
        var now = T0;
        var delays = new List<TimeSpan>();

        for (uint serial = 1; serial <= 9; serial++)
        {
            var failed = Step(state, new MessageReceived(Error(serial, "org.freedesktop.DBus.Error.NoReply", "org.freedesktop.DBus")), now);
            Assert.True(failed.State.Seeded);
            delays.Add(failed.State.RetryAt!.Value - now);

            now = failed.State.RetryAt!.Value;
            Assert.Equal(now, BlueZWatch.NextWake(failed.State));
            var woken = Step(failed.State, new WakeTime(), now);
            Assert.Equal(new BlueZEffect[] { new RequestSnapshot(Owner) }, woken.Effects);
            state = Step(woken.State, new SnapshotSent(serial + 1), now).State;
        }

        Assert.Equal(new[] { 1, 2, 4, 8, 16, 32, 60, 60, 60 }, delays.Select(d => (int)d.TotalSeconds));
    }

    [Theory]
    [InlineData(true)]   // an error reply
    [InlineData(false)]  // no reply by the deadline
    public void FailedRefresh_KeepsTheInventory_AndSignalsStillUpdateIt(bool errorReply)
    {
        var state = Seed(Managed(Adapter0, Bonded("dev_11", connected: false))).State;
        var refresh = Step(state, new MessageReceived(PropertiesChanged("dev_11", [], ["Connected"])));
        state = Step(refresh.State, new SnapshotSent(9)).State;

        var failed = errorReply
            ? Step(state, new MessageReceived(Error(9, "org.freedesktop.DBus.Error.NoReply", "org.freedesktop.DBus")))
            : Step(state, new WakeTime(), T0 + BlueZWatch.SnapshotDeadline);

        Assert.Empty(failed.Edges);
        Assert.Contains(Bond, failed.State.Devices.Keys);
        Assert.NotNull(failed.State.RetryAt);

        var connected = Step(failed.State, new MessageReceived(PropertiesChanged("dev_11", [("Connected", B(true))])));
        Assert.Equal(BlueZEdgeKind.Activated, connected.Edges[0].Kind);
    }

    [Fact]
    public void AccessDenied_StopsTheWatch()
    {
        var state = Step(Step(BlueZWatchState.Initial, new WatchStarted(Owner)).State, new SnapshotSent(1)).State;

        var denied = Step(state, new MessageReceived(Error(1, "org.freedesktop.DBus.Error.AccessDenied", "org.freedesktop.DBus")));

        Assert.True(denied.State.Stopped);
        Assert.Null(BlueZWatch.NextWake(denied.State));
        Assert.Equal(LogLevel.Warning, Assert.IsType<ReportProblem>(Assert.Single(denied.Effects)).Level);
        Assert.Same(denied.State, Step(denied.State, new MessageReceived(OwnerChanged(Owner, ":1.3801"))).State);
    }

    [Fact]
    public void UnansweredSnapshot_TimesOutAtTheDeadline_AndRetries()
    {
        var state = Step(Step(BlueZWatchState.Initial, new WatchStarted(Owner)).State, new SnapshotSent(1)).State;
        var deadline = T0 + BlueZWatch.SnapshotDeadline;
        Assert.Equal(deadline, BlueZWatch.NextWake(state));

        Assert.Same(state, Step(state, new WakeTime(), deadline - TimeSpan.FromTicks(1)).State);

        var timedOut = Step(state, new WakeTime(), deadline);
        Assert.True(timedOut.State.Seeded);
        Assert.Null(timedOut.State.Pending);
        Assert.Equal(deadline + BlueZWatch.FirstRetry, timedOut.State.RetryAt);
        Assert.Equal("Timeout", Assert.IsType<ReportProblem>(Assert.Single(timedOut.Effects)).Key);
    }

    [Fact]
    public void ConnectionLost_EveryBondDisappears_AndTheWatchStops()
    {
        var state = Seed(Managed(Adapter0, Bonded("dev_11", connected: false))).State;

        var step = Step(state, new ConnectionLost("closed"));

        Assert.Equal(BlueZEdgeKind.Disappeared, Assert.Single(step.Edges).Kind);
        Assert.True(step.State.Stopped);
    }

    // ── Captured traffic ───────────────────────────────────────────────

    [Fact]
    public void CapturedDisconnectStopAndRestart_RaiseTheExpectedEdges()
    {
        // Seed from the connected snapshot (message 21 of the capture), then replay everything after
        // it: the disconnection, bluetoothd stopping, and bluetoothd starting again as :1.3801.
        var messages = BlueZFixtures.StreamMessages();
        var state = Seed(Body(BlueZFixtures.ManagedObjectsConnected)).State;
        var edges = new List<(BlueZEdgeKind, string)>();

        foreach (var message in messages.Skip(22))
        {
            var input = message;
            if (message.Type == DBusMessageType.MethodReturn && message.Sender == ":1.3801"
                && message.Signature == BlueZInventory.ManagedObjectsSignature)
                input = message with { ReplySerial = 777 }; // the capture's reply went to busctl; take it as ours

            var step = Step(state, new MessageReceived(input));
            edges.AddRange(step.Edges.Select(e => (e.Kind, e.Device.Id.Value)));
            state = step.Effects.OfType<RequestSnapshot>().Any()
                ? Step(step.State, new SnapshotSent(777)).State
                : step.State;
        }

        const string central = "bluez:00:AA:01:00:00:00/00:AA:01:01:00:01";
        const string peripheral = "bluez:00:AA:01:01:00:01/00:AA:01:00:00:00";
        Assert.Equal(new[]
        {
            (BlueZEdgeKind.Deactivated, central), (BlueZEdgeKind.PropertyChanged, central),
            (BlueZEdgeKind.Deactivated, peripheral), (BlueZEdgeKind.PropertyChanged, peripheral),
            (BlueZEdgeKind.Disappeared, peripheral), (BlueZEdgeKind.Disappeared, central),
            (BlueZEdgeKind.Appeared, central), (BlueZEdgeKind.Appeared, peripheral),
        }, edges);
        Assert.Equal(":1.3801", state.Owner);
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private static BlueZStep Step(BlueZWatchState state, BlueZObservation observation, DateTimeOffset? now = null) =>
        BlueZWatch.Step(state, observation, now ?? T0);

    private static BlueZStep Seed(DBusValue objects)
    {
        var state = Step(BlueZWatchState.Initial, new WatchStarted(Owner)).State;
        state = Step(state, new SnapshotSent(1)).State;
        return Step(state, new MessageReceived(Reply(1, objects)));
    }

    private static DBusDictEntry Bonded(string node, bool connected, string address = "11:22:33:44:55:66") =>
        Device(node, address, ("Paired", B(true)), ("Bonded", B(true)), ("Connected", B(connected)));
}
