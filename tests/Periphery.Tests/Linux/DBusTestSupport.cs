using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Periphery.Linux.DBus.Core;

namespace Periphery.Tests.Linux;

/// <summary>Raw D-Bus messages captured from BlueZ 5.72 on a Linux host with two btvirt controllers.</summary>
internal static class BlueZFixtures
{
    /// <summary>92 messages as a bus monitor saw them: replies, signals and errors, back to back.</summary>
    public const string MonitorStream = "monitor-stream.bin";

    /// <summary>The reply to GetManagedObjects with the two controllers bonded to each other, disconnected.</summary>
    public const string ManagedObjectsDisconnected = "get-managed-objects-bonded-disconnected.bin";

    /// <summary>The same, while hci0 is connected to hci1, so GATT objects are present.</summary>
    public const string ManagedObjectsConnected = "get-managed-objects-bonded-connected.bin";

    /// <summary>The reply to GetManagedObjects after bluetoothd restarted.</summary>
    public const string ManagedObjectsAfterRestart = "get-managed-objects-after-restart.bin";

    public const string ErrorNameHasNoOwner = "error-name-has-no-owner.bin";
    public const string ErrorServiceUnknown = "error-service-unknown.bin";
    public const string NameAcquired = "bus-name-acquired.bin";

    public static byte[] Bytes(string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Linux", "Fixtures", "BlueZ", name));

    public static DBusMessage Message(string name)
    {
        Assert.True(DBusCodec.TryDecode(Bytes(name), out var message, out _), $"{name} did not decode.");
        return message;
    }

    /// <summary>Every message in <see cref="MonitorStream"/>, in the order the bus delivered them.</summary>
    public static IReadOnlyList<DBusMessage> StreamMessages()
    {
        byte[] stream = Bytes(MonitorStream);
        var messages = new List<DBusMessage>();
        for (int offset = 0; offset < stream.Length;)
        {
            Assert.True(DBusCodec.TryDecode(stream.AsSpan(offset), out var message, out int consumed));
            messages.Add(message);
            offset += consumed;
        }
        return messages;
    }
}

/// <summary>
/// Two connected in-memory streams. What one side writes, the other reads, in order, with nothing
/// timed: a read waits for a write or for the other side to be disposed.
/// </summary>
internal sealed class DuplexStream : Stream
{
    private readonly ChannelReader<byte[]> _inbound;
    private readonly ChannelWriter<byte[]> _outbound;
    private ReadOnlyMemory<byte> _pending;

    private DuplexStream(ChannelReader<byte[]> inbound, ChannelWriter<byte[]> outbound)
    {
        _inbound = inbound;
        _outbound = outbound;
    }

    public static (DuplexStream Client, DuplexStream Bus) CreatePair()
    {
        var toBus = Channel.CreateUnbounded<byte[]>();
        var toClient = Channel.CreateUnbounded<byte[]>();
        return (new DuplexStream(toClient.Reader, toBus.Writer), new DuplexStream(toBus.Reader, toClient.Writer));
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (_pending.IsEmpty)
        {
            if (!await _inbound.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                return 0;
            if (_inbound.TryRead(out var chunk))
                _pending = chunk;
        }
        int count = Math.Min(buffer.Length, _pending.Length);
        _pending[..count].CopyTo(buffer);
        _pending = _pending[count..];
        return count;
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _outbound.TryWrite(buffer.ToArray());
        return ValueTask.CompletedTask;
    }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    protected override void Dispose(bool disposing)
    {
        _outbound.TryComplete();
        base.Dispose(disposing);
    }

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>The bus side of a <see cref="DuplexStream"/> pair, driven by the test.</summary>
internal sealed class FakeBus
{
    private readonly DuplexStream _stream;
    private readonly List<byte> _buffer = new();
    private uint _serial = 1000;

    public FakeBus(DuplexStream stream) => _stream = stream;

    public string ClientName { get; private set; } = "";

    /// <summary>The standard handshake, then a reply to Hello naming <paramref name="clientName"/>.</summary>
    public async Task AcceptAsync(string clientName = ":1.99")
    {
        Assert.Equal("\0AUTH EXTERNAL", await ReadLineAsync());
        await WriteLineAsync("DATA");
        Assert.Equal("DATA", await ReadLineAsync());
        await WriteLineAsync("OK 0123456789abcdef0123456789abcdef");
        Assert.Equal("BEGIN", await ReadLineAsync());

        var hello = await ReadMessageAsync();
        Assert.Equal("Hello", hello.Member);
        ClientName = clientName;
        await ReplyAsync(hello, "s", new DBusString('s', clientName));
    }

    public async Task<string> ReadLineAsync()
    {
        while (true)
        {
            int end = IndexOfCrlf();
            if (end >= 0)
            {
                string line = Encoding.ASCII.GetString(_buffer.GetRange(0, end).ToArray());
                _buffer.RemoveRange(0, end + 2);
                return line;
            }
            await FillAsync();
        }
    }

    public Task WriteLineAsync(string line) => _stream.WriteAsync(Encoding.ASCII.GetBytes(line + "\r\n")).AsTask();

    public Task WriteRawAsync(ReadOnlyMemory<byte> bytes) => _stream.WriteAsync(bytes).AsTask();

    public async Task<DBusMessage> ReadMessageAsync()
    {
        while (true)
        {
            if (DBusCodec.TryDecode(_buffer.ToArray(), out var message, out int consumed))
            {
                _buffer.RemoveRange(0, consumed);
                return message;
            }
            await FillAsync();
        }
    }

    public Task SendAsync(DBusMessage message) =>
        _stream.WriteAsync(DBusCodec.Encode(message.Serial == 0 ? message with { Serial = ++_serial } : message)).AsTask();

    /// <summary>A method return to <paramref name="call"/>, from the call's destination.</summary>
    public Task ReplyAsync(DBusMessage call, string signature, params DBusValue[] body) => SendAsync(new DBusMessage
    {
        Type = DBusMessageType.MethodReturn,
        ReplySerial = call.Serial,
        Sender = call.Destination,
        Destination = ClientName,
        Signature = signature,
        Body = [.. body],
    });

    /// <summary>A captured reply or error, re-addressed as the answer to <paramref name="call"/>.</summary>
    public Task ReplyWithFixtureAsync(DBusMessage call, string fixture) => SendAsync(BlueZFixtures.Message(fixture) with
    {
        Serial = 0,
        ReplySerial = call.Serial,
        Destination = ClientName,
    });

    public Task ErrorAsync(DBusMessage call, string errorName, string sender) => SendAsync(new DBusMessage
    {
        Type = DBusMessageType.Error,
        ErrorName = errorName,
        ReplySerial = call.Serial,
        Sender = sender,
        Destination = ClientName,
        Signature = "s",
        Body = [new DBusString('s', "refused by the test")],
    });

    public void Close() => _stream.Dispose();

    private int IndexOfCrlf()
    {
        for (int i = 0; i + 1 < _buffer.Count; i++)
        {
            if (_buffer[i] == '\r' && _buffer[i + 1] == '\n')
                return i;
        }
        return -1;
    }

    private async Task FillAsync()
    {
        var chunk = new byte[4096];
        int read = await _stream.ReadAsync(chunk);
        if (read == 0)
            throw new EndOfStreamException("The client closed the connection.");
        _buffer.AddRange(chunk.AsSpan(0, read).ToArray());
    }
}

/// <summary>An <see cref="ILogger"/> that keeps every entry.</summary>
internal sealed class RecordingLogger : ILogger
{
    public List<(LogLevel Level, string Message)> Entries { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Entries.Add((logLevel, formatter(state, exception)));
}
