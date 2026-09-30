using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Periphery.Linux.BlueZ;
using Periphery.Linux.BlueZ.Core;
using Periphery.Linux.DBus;
using Periphery.Linux.DBus.Core;
using Periphery.Testing;

namespace Periphery.Tests.Linux;

/// <summary>
/// The BlueZ leg's shell (ADR-0091 D2, D3) against a bus the test drives over an in-memory stream.
/// The one timed case advances a fake clock only once the deadline's timer is armed (ADR-0089).
/// </summary>
public class BlueZDeviceSourceTests
{
    private const string BlueZOwner = ":1.3769";

    [Fact]
    public async Task Enumerate_AsksTheOwnerByUniqueName_WithoutAutoStart_AndMapsTheReply()
    {
        var harness = new Harness();
        var enumerate = harness.Source.EnumerateAsync(CancellationToken.None);
        var bus = await harness.AcceptAsync();

        var getOwner = await bus.ReadMessageAsync();
        Assert.Equal(("GetNameOwner", DBusConnection.BusName), (getOwner.Member, getOwner.Destination));
        Assert.Equal(new DBusString('s', "org.bluez"), Assert.Single(getOwner.Body));
        await bus.ReplyAsync(getOwner, "s", new DBusString('s', BlueZOwner));

        var getObjects = await bus.ReadMessageAsync();
        Assert.Equal(("GetManagedObjects", BlueZOwner, "/"), (getObjects.Member, getObjects.Destination, getObjects.Path));
        Assert.True(getObjects.Flags.HasFlag(DBusMessageFlags.NoAutoStart));
        await bus.ReplyWithFixtureAsync(getObjects, BlueZFixtures.ManagedObjectsDisconnected);

        var devices = await enumerate;
        Assert.Equal(
            new[] { "bluez:00:AA:01:00:00:00/00:AA:01:01:00:01", "bluez:00:AA:01:01:00:01/00:AA:01:00:00:00" },
            devices.Select(d => d.Id.Value));
        Assert.Empty(harness.Logger.Entries);
    }

    [Fact]
    public async Task NoOwner_IsAnEmptyAnswer_LoggedOnceAtInformation()
    {
        var harness = new Harness();

        for (int i = 0; i < 2; i++)
        {
            var enumerate = harness.Source.EnumerateAsync(CancellationToken.None);
            var bus = await harness.AcceptAsync();
            await bus.ReplyWithFixtureAsync(await bus.ReadMessageAsync(), BlueZFixtures.ErrorNameHasNoOwner);
            Assert.Empty(await enumerate);
        }

        var entry = Assert.Single(harness.Logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("NameHasNoOwner", entry.Message);
    }

    [Fact]
    public async Task AccessDenied_IsLatched_SoBlueZIsNotAskedAgain()
    {
        var harness = new Harness();
        var enumerate = harness.Source.EnumerateAsync(CancellationToken.None);
        var bus = await harness.AcceptAsync();
        await bus.ReplyAsync(await bus.ReadMessageAsync(), "s", new DBusString('s', BlueZOwner));
        await bus.ErrorAsync(await bus.ReadMessageAsync(), "org.freedesktop.DBus.Error.AccessDenied", DBusConnection.BusName);
        Assert.Empty(await enumerate);

        Assert.Empty(await harness.Source.EnumerateAsync(CancellationToken.None));

        Assert.Equal(1, harness.Connects);
        Assert.Equal(LogLevel.Warning, Assert.Single(harness.Logger.Entries).Level);
    }

    [Fact]
    public async Task ReplyOfTheWrongShape_IsAnEmptyAnswer()
    {
        var harness = new Harness();
        var enumerate = harness.Source.EnumerateAsync(CancellationToken.None);
        var bus = await harness.AcceptAsync();
        await bus.ReplyAsync(await bus.ReadMessageAsync(), "s", new DBusString('s', BlueZOwner));
        await bus.ReplyAsync(await bus.ReadMessageAsync(), "s", new DBusString('s', "not objects"));

        Assert.Empty(await enumerate);
        Assert.Contains("cannot read", Assert.Single(harness.Logger.Entries).Message);
    }

    [Fact]
    public async Task BusThatClosesMidCall_IsAnEmptyAnswer()
    {
        var harness = new Harness();
        var enumerate = harness.Source.EnumerateAsync(CancellationToken.None);
        var bus = await harness.AcceptAsync();
        await bus.ReadMessageAsync();
        bus.Close();

        Assert.Empty(await enumerate);
        Assert.Contains("dropped", Assert.Single(harness.Logger.Entries).Message);
    }

    [Fact]
    public async Task NoSystemBus_IsAnEmptyAnswer_LoggedAtInformation()
    {
        var logger = new RecordingLogger();
        var source = new BlueZDeviceSource(
            _ => Task.FromException<DBusConnection>(new SocketException((int)SocketError.AddressNotAvailable)),
            TimeProvider.System,
            logger);

        Assert.Empty(await source.EnumerateAsync(CancellationToken.None));
        Assert.Equal(LogLevel.Information, Assert.Single(logger.Entries).Level);
    }

    [Fact]
    public async Task UnansweredCall_EndsAtTheDeadline()
    {
        var harness = new Harness();
        var enumerate = harness.Source.EnumerateAsync(CancellationToken.None);
        var bus = await harness.AcceptAsync();
        await bus.ReadMessageAsync(); // GetNameOwner, never answered

        // One timer bounded the connection, and the next bounds this call. Advance once it exists.
        Assert.Equal(BlueZDeviceSource.ExchangeDeadline, await harness.Time.NextTimerArmedAsync());
        Assert.Equal(BlueZDeviceSource.ExchangeDeadline, await harness.Time.NextTimerArmedAsync());
        harness.Time.Advance(BlueZDeviceSource.ExchangeDeadline);

        Assert.Empty(await enumerate);
        Assert.Contains("did not answer", Assert.Single(harness.Logger.Entries).Message);
    }

    [Fact]
    public async Task CallerCancellation_Propagates()
    {
        var harness = new Harness();
        using var cts = new CancellationTokenSource();
        var enumerate = harness.Source.EnumerateAsync(cts.Token);
        var bus = await harness.AcceptAsync();
        await bus.ReadMessageAsync();

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => enumerate);
        Assert.Empty(harness.Logger.Entries);
    }

    private sealed class Harness
    {
        private readonly Queue<FakeBus> _buses = new();

        public Harness()
        {
            Source = new BlueZDeviceSource(
                ct =>
                {
                    Connects++;
                    var (client, busSide) = DuplexStream.CreatePair();
                    _buses.Enqueue(new FakeBus(busSide));
                    return DBusConnection.OpenAsync(client, ct);
                },
                Time,
                Logger);
        }

        public BlueZDeviceSource Source { get; }
        public RecordingLogger Logger { get; } = new();
        public TimerSignalingFakeTimeProvider Time { get; } = new();
        public int Connects { get; private set; }

        /// <summary>The bus for the connection just opened, past its handshake.</summary>
        public async Task<FakeBus> AcceptAsync()
        {
            var bus = _buses.Dequeue();
            await bus.AcceptAsync();
            return bus;
        }
    }
}
