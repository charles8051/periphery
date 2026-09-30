// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using Periphery.Linux.DBus.Core;

namespace Periphery.Linux.DBus;

/// <summary>
/// One private connection to a D-Bus bus (ADR-0091 D2): the handshake, <c>Hello</c>, and calls that
/// wait for their own reply. It is the shell around <see cref="DBusCodec"/> and
/// <see cref="DBusAuth"/>, and it owns the stream. One caller uses it at a time.
/// </summary>
internal sealed class DBusConnection : IAsyncDisposable
{
    internal const string BusName = "org.freedesktop.DBus";
    internal const string BusPath = "/org/freedesktop/DBus";

    private const int MaxAuthLineLength = 16 * 1024;

    private readonly Stream _stream;
    private byte[] _buffer = new byte[16 * 1024];
    private int _start;
    private int _end;
    private uint _lastSerial;

    private DBusConnection(Stream stream) => _stream = stream;

    /// <summary>The unique name the bus assigned in reply to <c>Hello</c>.</summary>
    internal string UniqueName { get; private set; } = "";

    /// <summary>Connects to the system bus, authenticates, and says <c>Hello</c>.</summary>
    /// <exception cref="SocketException">No bus is listening at the address.</exception>
    [SupportedOSPlatform("linux")]
    internal static async Task<DBusConnection> ConnectSystemBusAsync(CancellationToken ct)
    {
        string path = DBusAddress.SystemBusSocketPath(Environment.GetEnvironmentVariable("DBUS_SYSTEM_BUS_ADDRESS"));
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), ct).ConfigureAwait(false);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
        return await OpenAsync(new NetworkStream(socket, ownsSocket: true), ct).ConfigureAwait(false);
    }

    /// <summary>Authenticates over <paramref name="stream"/> and says <c>Hello</c>.</summary>
    /// <exception cref="DBusAuthenticationException">The bus refused the handshake.</exception>
    internal static async Task<DBusConnection> OpenAsync(Stream stream, CancellationToken ct)
    {
        var connection = new DBusConnection(stream);
        try
        {
            await connection.AuthenticateAsync(ct).ConfigureAwait(false);
            var reply = await connection.CallAsync(DBusMessage.Call(BusName, BusPath, BusName, "Hello"), ct).ConfigureAwait(false);
            if (reply.Type != DBusMessageType.MethodReturn || reply.Body is not [DBusString { Value: var uniqueName }])
                throw new DBusProtocolException("Hello did not return a unique name.");
            connection.UniqueName = uniqueName;
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Sends <paramref name="call"/> under the next serial and returns its method return or error.
    /// A reply counts only when its reply serial matches and it comes from the call's destination or
    /// from the bus, which sends errors such as <c>NoReply</c> and <c>AccessDenied</c> itself. Every
    /// other message that arrives first is dropped.
    /// </summary>
    internal async Task<DBusMessage> CallAsync(DBusMessage call, CancellationToken ct)
    {
        uint serial = ++_lastSerial;
        await WriteAsync(DBusCodec.Encode(call with { Serial = serial }), ct).ConfigureAwait(false);
        while (true)
        {
            var message = await ReceiveAsync(ct).ConfigureAwait(false);
            if (message.Type is DBusMessageType.MethodReturn or DBusMessageType.Error
                && message.ReplySerial == serial
                && (message.Sender == call.Destination || message.Sender == BusName))
                return message;
        }
    }

    /// <summary>Returns the next whole message from the bus.</summary>
    /// <exception cref="EndOfStreamException">The bus closed the connection.</exception>
    internal async Task<DBusMessage> ReceiveAsync(CancellationToken ct)
    {
        while (true)
        {
            if (DBusCodec.TryDecode(_buffer.AsSpan(_start, _end - _start), out var message, out int consumed))
            {
                _start += consumed;
                return message;
            }
            await FillAsync(ct).ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync() => _stream.DisposeAsync();

    private async Task AuthenticateAsync(CancellationToken ct)
    {
        await WriteAsync(Encoding.ASCII.GetBytes(DBusAuth.Greeting), ct).ConfigureAwait(false);
        var state = DBusAuthState.AwaitingChallenge;
        while (state != DBusAuthState.Authenticated)
        {
            string line = await ReadLineAsync(ct).ConfigureAwait(false);
            var step = DBusAuth.Next(state, line);
            if (step.State == DBusAuthState.Rejected)
                throw new DBusAuthenticationException(line);
            if (step.Send is not null)
                await WriteAsync(Encoding.ASCII.GetBytes(step.Send), ct).ConfigureAwait(false);
            state = step.State;
        }
    }

    private async Task<string> ReadLineAsync(CancellationToken ct)
    {
        while (true)
        {
            int newline = _buffer.AsSpan(_start, _end - _start).IndexOf("\r\n"u8);
            if (newline >= 0)
            {
                string line = Encoding.ASCII.GetString(_buffer, _start, newline);
                _start += newline + 2;
                return line;
            }
            if (_end - _start > MaxAuthLineLength)
                throw new DBusProtocolException("An authentication line is too long.");
            await FillAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task FillAsync(CancellationToken ct)
    {
        if (_start > 0)
        {
            Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
            _end -= _start;
            _start = 0;
        }
        if (_end == _buffer.Length)
            Array.Resize(ref _buffer, Math.Min(_buffer.Length * 2, DBusCodec.MaxMessageLength));

        int read = await _stream.ReadAsync(_buffer.AsMemory(_end), ct).ConfigureAwait(false);
        if (read == 0)
            throw new EndOfStreamException("The bus closed the connection.");
        _end += read;
    }

    private async Task WriteAsync(byte[] bytes, CancellationToken ct)
    {
        await _stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await _stream.FlushAsync(ct).ConfigureAwait(false);
    }
}

/// <summary>The bus refused the client's handshake. <see cref="Exception.Message"/> holds its line.</summary>
internal sealed class DBusAuthenticationException : Exception
{
    public DBusAuthenticationException(string serverLine) : base($"The bus refused authentication: '{serverLine}'.") { }
}
