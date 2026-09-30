using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Periphery.Linux.BlueZ;
using Periphery.Linux.BlueZ.Core;
using Periphery.Linux.DBus;
using Periphery.Linux.DBus.Core;
using Periphery.Testing;
using static Periphery.Tests.Linux.BlueZObjectsBuilder;

namespace Periphery.Tests.Linux;

/// <summary>
/// The watch shell (ADR-0091 D8) against a bus the test drives over an in-memory stream. Edges are
/// awaited as they are raised; the timed cases advance a fake clock once the wait's timer is armed.
/// </summary>
public class BlueZMonitorTests
{
    private const string Central = "bluez:00:AA:01:00:00:00/00:AA:01:01:00:01";
    private const string Peripheral = "bluez:00:AA:01:01:00:01/00:AA:01:00:00:00";

    [Fact]
    public async Task Start_Subscribes_SeedsSilently_ThenRaisesEdgesFromSignals()
    {
        await using var harness = new Harness();
        var start = harness.Monitor.StartAsync(CancellationToken.None);
        var bus = await harness.AcceptAsync();

        var rules = new List<string>();
        for (int i = 0; i < BlueZMonitor.MatchRules.Length; i++)
        {
            var addMatch = await bus.ReadMessageAsync();
            Assert.Equal(("AddMatch", DBusConnection.BusName), (addMatch.Member, addMatch.Destination));
            rules.Add(Assert.IsType<DBusString>(Assert.Single(addMatch.Body)).Value);
            await bus.ReplyAsync(addMatch, "");
        }
        Assert.Equal(BlueZMonitor.MatchRules, rules);

        await AnswerOwnerAsync(bus, Owner);
        var snapshot = await bus.ReadMessageAsync();
        Assert.Equal(("GetManagedObjects", Owner), (snapshot.Member, snapshot.Destination));
        Assert.True(snapshot.Flags.HasFlag(DBusMessageFlags.NoAutoStart));
        await bus.ReplyWithFixtureAsync(snapshot, BlueZFixtures.ManagedObjectsDisconnected);
        await start;

        Assert.Equal(2, harness.Monitor.State.Devices.Count);
        await bus.SendAsync(PropertiesChanged("dev_00_AA_01_01_00_01", [("Connected", B(true))]));

        Assert.Equal((BlueZEdgeKind.Activated, Central), await harness.NextEdgeAsync());
        Assert.Equal((BlueZEdgeKind.PropertyChanged, Central), await harness.NextEdgeAsync());
        Assert.True(harness.NoEdgeYet(), "the seed raised edges");
    }

    [Fact]
    public async Task OwnerLost_EveryBondDisappears()
    {
        await using var harness = new Harness();
        var bus = await harness.StartSeededAsync();

        await bus.SendAsync(OwnerChanged(Owner, ""));

        Assert.Equal((BlueZEdgeKind.Disappeared, Central), await harness.NextEdgeAsync());
        Assert.Equal((BlueZEdgeKind.Disappeared, Peripheral), await harness.NextEdgeAsync());
    }

    [Fact]
    public async Task NoOwnerAtStart_ThenOwnerGained_BondsAppear()
    {
        await using var harness = new Harness();
        var start = harness.Monitor.StartAsync(CancellationToken.None);
        var bus = await harness.AcceptAsync();
        await AnswerMatchesAsync(bus);
        var getOwner = await bus.ReadMessageAsync();
        await bus.ReplyWithFixtureAsync(getOwner, BlueZFixtures.ErrorNameHasNoOwner);
        await start;
        Assert.Equal(LogLevel.Information, Assert.Single(harness.Logger.Entries).Level);

        await bus.SendAsync(OwnerChanged("", ":1.3801"));
        var snapshot = await bus.ReadMessageAsync();
        Assert.Equal(":1.3801", snapshot.Destination);
        await bus.SendAsync(Reply(snapshot.Serial, Body(BlueZFixtures.ManagedObjectsAfterRestart), sender: ":1.3801"));

        Assert.Equal((BlueZEdgeKind.Appeared, Central), await harness.NextEdgeAsync());
        Assert.Equal((BlueZEdgeKind.Appeared, Peripheral), await harness.NextEdgeAsync());
    }

    [Fact]
    public async Task UnansweredSeed_EndsAtTheDeadline_ThenRetries()
    {
        await using var harness = new Harness();
        var start = harness.Monitor.StartAsync(CancellationToken.None);
        var bus = await harness.AcceptAsync();
        await AnswerMatchesAsync(bus);
        await AnswerOwnerAsync(bus, Owner);
        Assert.Equal("GetManagedObjects", (await bus.ReadMessageAsync()).Member); // never answered

        // Five exchange deadlines came first (connection, three AddMatch, GetNameOwner). The sixth
        // timer is the wait for this snapshot.
        for (int i = 0; i < 6; i++)
            Assert.Equal(BlueZWatch.SnapshotDeadline, await harness.Time.NextTimerArmedAsync());
        harness.Time.Advance(BlueZWatch.SnapshotDeadline);
        await start;
        Assert.Contains("did not answer", Assert.Single(harness.Logger.Entries).Message);

        Assert.Equal(BlueZWatch.FirstRetry, await harness.Time.NextTimerArmedAsync());
        harness.Time.Advance(BlueZWatch.FirstRetry);
        Assert.Equal("GetManagedObjects", (await bus.ReadMessageAsync()).Member);
    }

    [Fact]
    public async Task RefusedSubscription_WatchesNothing()
    {
        await using var harness = new Harness();
        var start = harness.Monitor.StartAsync(CancellationToken.None);
        var bus = await harness.AcceptAsync();
        await bus.ErrorAsync(await bus.ReadMessageAsync(), "org.freedesktop.DBus.Error.AccessDenied", DBusConnection.BusName);

        await start;

        Assert.Equal(LogLevel.Warning, Assert.Single(harness.Logger.Entries).Level);
        Assert.Same(BlueZWatchState.Initial, harness.Monitor.State);
    }

    [Fact]
    public async Task BusClosing_EveryBondDisappears()
    {
        await using var harness = new Harness();
        var bus = await harness.StartSeededAsync();

        bus.Close();

        Assert.Equal((BlueZEdgeKind.Disappeared, Central), await harness.NextEdgeAsync());
        Assert.Equal((BlueZEdgeKind.Disappeared, Peripheral), await harness.NextEdgeAsync());
    }

    [Fact]
    public async Task CallerCancellation_DuringStart_Propagates()
    {
        await using var harness = new Harness();
        using var cts = new CancellationTokenSource();
        var start = harness.Monitor.StartAsync(cts.Token);
        var bus = await harness.AcceptAsync();
        await bus.ReadMessageAsync();

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
    }

    private static async Task AnswerMatchesAsync(FakeBus bus)
    {
        for (int i = 0; i < BlueZMonitor.MatchRules.Length; i++)
            await bus.ReplyAsync(await bus.ReadMessageAsync(), "");
    }

    private static async Task AnswerOwnerAsync(FakeBus bus, string owner)
    {
        var getOwner = await bus.ReadMessageAsync();
        Assert.Equal("GetNameOwner", getOwner.Member);
        await bus.ReplyAsync(getOwner, "s", S(owner));
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly Queue<FakeBus> _buses = new();
        private readonly Channel<BlueZEdge> _edges = Channel.CreateUnbounded<BlueZEdge>();

        public Harness()
        {
            Monitor = new BlueZMonitor(
                ct =>
                {
                    var (client, busSide) = DuplexStream.CreatePair();
                    _buses.Enqueue(new FakeBus(busSide));
                    return DBusConnection.OpenAsync(client, ct);
                },
                Time,
                Logger,
                edge => _edges.Writer.TryWrite(edge),
                new ConcurrentDictionary<string, byte>());
        }

        public BlueZMonitor Monitor { get; }
        public RecordingLogger Logger { get; } = new();
        public TimerSignalingFakeTimeProvider Time { get; } = new();

        public async Task<FakeBus> AcceptAsync()
        {
            var bus = _buses.Dequeue();
            await bus.AcceptAsync();
            return bus;
        }

        /// <summary>Starts the monitor against the capture's disconnected pair.</summary>
        public async Task<FakeBus> StartSeededAsync()
        {
            var start = Monitor.StartAsync(CancellationToken.None);
            var bus = await AcceptAsync();
            await AnswerMatchesAsync(bus);
            await AnswerOwnerAsync(bus, Owner);
            await bus.ReplyWithFixtureAsync(await bus.ReadMessageAsync(), BlueZFixtures.ManagedObjectsDisconnected);
            await start;
            return bus;
        }

        public async Task<(BlueZEdgeKind, string)> NextEdgeAsync()
        {
            // A safety net that bounds a failure; the edge is the signal (ADR-0089 D5).
            using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var edge = await _edges.Reader.ReadAsync(safety.Token);
            return (edge.Kind, edge.Device.Id.Value);
        }

        public bool NoEdgeYet() => !_edges.Reader.TryPeek(out _);

        public ValueTask DisposeAsync() => Monitor.DisposeAsync();
    }
}
