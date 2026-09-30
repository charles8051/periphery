using Periphery.Linux.DBus;
using Periphery.Linux.DBus.Core;

namespace Periphery.Tests.Linux;

/// <summary>
/// The connection shell (ADR-0091 D2, D8) over an in-memory stream, against a bus the test drives.
/// Nothing here waits on time.
/// </summary>
public class DBusConnectionTests
{
    [Fact]
    public async Task Open_Authenticates_AndTakesTheNameHelloReturns()
    {
        var (client, busSide) = DuplexStream.CreatePair();
        var bus = new FakeBus(busSide);

        var open = DBusConnection.OpenAsync(client, CancellationToken.None);
        await bus.AcceptAsync(":1.42");
        await using var connection = await open;

        Assert.Equal(":1.42", connection.UniqueName);
    }

    [Fact]
    public async Task Open_RefusedHandshake_Throws()
    {
        var (client, busSide) = DuplexStream.CreatePair();
        var bus = new FakeBus(busSide);

        var open = DBusConnection.OpenAsync(client, CancellationToken.None);
        Assert.Equal("\0AUTH EXTERNAL", await bus.ReadLineAsync());
        await bus.WriteLineAsync("REJECTED EXTERNAL");

        await Assert.ThrowsAsync<DBusAuthenticationException>(() => open);
    }

    [Fact]
    public async Task Call_SetsNoAutoStart_AndReturnsOnlyItsOwnReply()
    {
        var (connection, bus) = await OpenAsync();

        var call = connection.CallAsync(DBusMessage.Call(":1.7", "/", "org.freedesktop.DBus.ObjectManager", "GetManagedObjects"), CancellationToken.None);
        var sent = await bus.ReadMessageAsync();
        Assert.Equal(DBusMessageFlags.NoAutoStart, sent.Flags);
        Assert.Equal(":1.7", sent.Destination);

        // A signal, a reply to another serial, and a reply to this serial from a third party all
        // arrive first. None of them is the answer.
        await bus.SendAsync(BlueZFixtures.Message(BlueZFixtures.NameAcquired));
        await bus.SendAsync(Return(sent.Serial + 1, ":1.7", "other serial"));
        await bus.SendAsync(Return(sent.Serial, ":1.666", "impostor"));
        await bus.SendAsync(Return(sent.Serial, ":1.7", "mine"));

        var reply = await call;
        Assert.Equal(new DBusString('s', "mine"), Assert.Single(reply.Body));
    }

    [Fact]
    public async Task Call_ReturnsAnErrorTheBusSendsOnTheServicesBehalf()
    {
        var (connection, bus) = await OpenAsync();

        var call = connection.CallAsync(DBusMessage.Call(":1.7", "/", "org.freedesktop.DBus.ObjectManager", "GetManagedObjects"), CancellationToken.None);
        var sent = await bus.ReadMessageAsync();
        await bus.ErrorAsync(sent, "org.freedesktop.DBus.Error.NoReply", DBusConnection.BusName);

        var reply = await call;
        Assert.Equal(DBusMessageType.Error, reply.Type);
        Assert.Equal("org.freedesktop.DBus.Error.NoReply", reply.ErrorName);
    }

    [Fact]
    public async Task Receive_AfterTheBusCloses_ThrowsEndOfStream()
    {
        var (connection, bus) = await OpenAsync();

        bus.Close();

        await Assert.ThrowsAsync<EndOfStreamException>(() => connection.ReceiveAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Receive_ReassemblesAMessageSplitAcrossReads()
    {
        var (connection, bus) = await OpenAsync();
        byte[] reply = BlueZFixtures.Bytes(BlueZFixtures.ManagedObjectsConnected);

        var receive = connection.ReceiveAsync(CancellationToken.None);
        await bus.WriteRawAsync(reply.AsMemory(0, 100));
        await bus.WriteRawAsync(reply.AsMemory(100));

        var message = await receive;
        Assert.Equal("a{oa{sa{sv}}}", message.Signature);
    }

    private static async Task<(DBusConnection Connection, FakeBus Bus)> OpenAsync()
    {
        var (client, busSide) = DuplexStream.CreatePair();
        var bus = new FakeBus(busSide);
        var open = DBusConnection.OpenAsync(client, CancellationToken.None);
        await bus.AcceptAsync();
        return (await open, bus);
    }

    private static DBusMessage Return(uint replySerial, string sender, string text) => new()
    {
        Type = DBusMessageType.MethodReturn,
        ReplySerial = replySerial,
        Sender = sender,
        Signature = "s",
        Body = [new DBusString('s', text)],
    };
}
