using System.Buffers;
using System.IO.Pipelines;

namespace Periphery.Bootloader.Stm32.Serial.Tests;

/// <summary>
/// An in-memory STM32 system UART bootloader: an <see cref="IDuplexPipe"/> whose far end answers
/// AN3155 over a modelled flash array. Drives <see cref="Stm32SerialProgrammer"/> end to end with
/// no port, no RJCP, and no hardware — the payoff of the programmer taking a pipe rather than a
/// port.
/// </summary>
/// <remarks>
/// Implements the subset the programmer uses: sync (0x7F), Get (0x00), Get ID (0x02),
/// Extended Erase (0x44), Write Memory (0x31), Read Memory (0x11), Go (0x21).
/// </remarks>
internal sealed class FakeStm32Bootloader : IDuplexPipe, IAsyncDisposable
{
    public const byte Ack = 0x79;
    public const byte Nack = 0x1F;
    public const uint FlashBase = 0x08000000;

    private readonly Pipe _toDevice = new();  // programmer writes, device reads
    private readonly Pipe _toHost = new();    // device writes, programmer reads
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _deviceLoop;
    private readonly byte[] _flash;
    private readonly int _pageSize;

    private bool _synced;

    /// <summary>Bytes the device corrupts on write, keyed by absolute address — to force a verify failure.</summary>
    public Dictionary<uint, byte> CorruptOnWrite { get; } = new();

    /// <summary>Pages erased so far, in the order Extended Erase reported them.</summary>
    public List<int> ErasedPageCounts { get; } = new();

    /// <summary>The 16-bit product id reported by Get ID.</summary>
    public ushort ProductId { get; init; } = 0x0413;

    /// <summary>The BCD protocol version reported by Get.</summary>
    public byte ProtocolVersion { get; init; } = 0x31;

    /// <summary>NACK the Go command instead of ACKing it — a read-protected or mis-addressed part.</summary>
    public bool RefuseGo { get; init; }

    /// <summary>
    /// Stop answering and close the pipe after this many commands — the cable-unplugged case.
    /// </summary>
    public int? DisconnectAfterCommands { get; init; }

    private int _commandsHandled;

    public PipeReader Input => _toHost.Reader;
    public PipeWriter Output => _toDevice.Writer;

    public FakeStm32Bootloader(int flashSize = 64 * 1024, int pageSize = 2048)
    {
        _flash = new byte[flashSize];
        Array.Fill(_flash, (byte)0xFF);
        _pageSize = pageSize;
        _deviceLoop = Task.Run(() => RunAsync(_cts.Token));
    }

    /// <summary>Reads modelled flash at an absolute address.</summary>
    public ReadOnlySpan<byte> Read(uint address, int length) =>
        _flash.AsSpan((int)(address - FlashBase), length);

    /// <summary>
    /// Pushes unsolicited bytes at the host, as a real line does — a NACK from a refused command,
    /// an autobaud echo, noise picked up when the port opened.
    /// </summary>
    public async Task InjectNoiseAsync(params byte[] bytes)
    {
        bytes.CopyTo(_toHost.Writer.GetSpan(bytes.Length));
        _toHost.Writer.Advance(bytes.Length);
        await _toHost.Writer.FlushAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await _deviceLoop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        _cts.Dispose();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var reader = _toDevice.Reader;
        var writer = _toHost.Writer;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (_commandsHandled == DisconnectAfterCommands)
                    break;   // the finally completes the writer — the host sees the pipe close

                byte command = await ReadByteAsync(reader, ct).ConfigureAwait(false);
                _commandsHandled++;

                if (command == 0x7F)
                {
                    // AN3155 3.1: the first sync since reset is ACKed; a second is NACKed.
                    await SendAsync(writer, new[] { _synced ? Nack : Ack }, ct).ConfigureAwait(false);
                    _synced = true;
                    continue;
                }

                byte complement = await ReadByteAsync(reader, ct).ConfigureAwait(false);
                if ((byte)(command ^ complement) != 0xFF)
                {
                    await SendAsync(writer, new[] { Nack }, ct).ConfigureAwait(false);
                    continue;
                }

                switch (command)
                {
                    case 0x00: await GetAsync(writer, ct).ConfigureAwait(false); break;
                    case 0x02: await GetIdAsync(writer, ct).ConfigureAwait(false); break;
                    case 0x44: await ExtendedEraseAsync(reader, writer, ct).ConfigureAwait(false); break;
                    case 0x31: await WriteMemoryAsync(reader, writer, ct).ConfigureAwait(false); break;
                    case 0x11: await ReadMemoryAsync(reader, writer, ct).ConfigureAwait(false); break;
                    case 0x21: await GoAsync(reader, writer, ct).ConfigureAwait(false); break;
                    default: await SendAsync(writer, new[] { Nack }, ct).ConfigureAwait(false); break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        finally
        {
            await writer.CompleteAsync().ConfigureAwait(false);
        }
    }

    // ACK, N, version, supported commands, ACK.
    private Task GetAsync(PipeWriter writer, CancellationToken ct)
    {
        byte[] commands = { 0x00, 0x01, 0x02, 0x11, 0x21, 0x31, 0x44 };
        var reply = new List<byte> { Ack, (byte)(commands.Length + 1), ProtocolVersion };
        reply.AddRange(commands);
        reply.Add(Ack);
        return SendAsync(writer, reply.ToArray(), ct);
    }

    // ACK, N=1, PID high, PID low, ACK.
    private Task GetIdAsync(PipeWriter writer, CancellationToken ct) =>
        SendAsync(writer, new[] { Ack, (byte)0x01, (byte)(ProductId >> 8), (byte)(ProductId & 0xFF), Ack }, ct);

    private async Task ExtendedEraseAsync(PipeReader reader, PipeWriter writer, CancellationToken ct)
    {
        await SendAsync(writer, new[] { Ack }, ct).ConfigureAwait(false);

        var header = await ReadExactAsync(reader, 2, ct).ConfigureAwait(false);
        int n = (header[0] << 8) | header[1];          // AN3155 half-word: pages - 1
        int pageCount = n + 1;
        await ReadExactAsync(reader, pageCount * 2 + 1, ct).ConfigureAwait(false); // page list + checksum

        for (int page = 0; page < pageCount; page++)
        {
            int start = page * _pageSize;
            if (start >= _flash.Length) break;
            Array.Fill(_flash, (byte)0xFF, start, Math.Min(_pageSize, _flash.Length - start));
        }
        ErasedPageCounts.Add(pageCount);

        await SendAsync(writer, new[] { Ack }, ct).ConfigureAwait(false);
    }

    private async Task WriteMemoryAsync(PipeReader reader, PipeWriter writer, CancellationToken ct)
    {
        await SendAsync(writer, new[] { Ack }, ct).ConfigureAwait(false);

        var addressFrame = await ReadExactAsync(reader, 5, ct).ConfigureAwait(false);
        uint address = ReadBigEndian(addressFrame);
        await SendAsync(writer, new[] { Ack }, ct).ConfigureAwait(false);

        var lengthByte = await ReadExactAsync(reader, 1, ct).ConfigureAwait(false);
        int length = lengthByte[0] + 1;
        var data = await ReadExactAsync(reader, length, ct).ConfigureAwait(false);
        await ReadExactAsync(reader, 1, ct).ConfigureAwait(false); // checksum

        int offset = (int)(address - FlashBase);
        for (int i = 0; i < length && offset + i < _flash.Length; i++)
        {
            uint at = address + (uint)i;
            _flash[offset + i] = CorruptOnWrite.TryGetValue(at, out byte bad) ? bad : data[i];
        }

        await SendAsync(writer, new[] { Ack }, ct).ConfigureAwait(false);
    }

    private async Task ReadMemoryAsync(PipeReader reader, PipeWriter writer, CancellationToken ct)
    {
        await SendAsync(writer, new[] { Ack }, ct).ConfigureAwait(false);

        var addressFrame = await ReadExactAsync(reader, 5, ct).ConfigureAwait(false);
        uint address = ReadBigEndian(addressFrame);
        await SendAsync(writer, new[] { Ack }, ct).ConfigureAwait(false);

        var lengthFrame = await ReadExactAsync(reader, 2, ct).ConfigureAwait(false);
        int length = lengthFrame[0] + 1;

        var reply = new byte[length + 1];
        reply[0] = Ack;
        _flash.AsSpan((int)(address - FlashBase), length).CopyTo(reply.AsSpan(1));
        await SendAsync(writer, reply, ct).ConfigureAwait(false);
    }

    private async Task GoAsync(PipeReader reader, PipeWriter writer, CancellationToken ct)
    {
        if (RefuseGo)
        {
            // AN3155 NACKs Go on a read-protected part or an invalid jump address. The host waits
            // for an ACK that never comes and its command deadline fires.
            await SendAsync(writer, new[] { Nack }, ct).ConfigureAwait(false);
            return;
        }

        await SendAsync(writer, new[] { Ack }, ct).ConfigureAwait(false);
        await ReadExactAsync(reader, 5, ct).ConfigureAwait(false);
        await SendAsync(writer, new[] { Ack }, ct).ConfigureAwait(false);
    }

    private static uint ReadBigEndian(byte[] frame) =>
        ((uint)frame[0] << 24) | ((uint)frame[1] << 16) | ((uint)frame[2] << 8) | frame[3];

    private static async Task SendAsync(PipeWriter writer, byte[] bytes, CancellationToken ct)
    {
        bytes.CopyTo(writer.GetSpan(bytes.Length));
        writer.Advance(bytes.Length);
        await writer.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task<byte> ReadByteAsync(PipeReader reader, CancellationToken ct)
    {
        var one = await ReadExactAsync(reader, 1, ct).ConfigureAwait(false);
        return one[0];
    }

    private static async Task<byte[]> ReadExactAsync(PipeReader reader, int count, CancellationToken ct)
    {
        var buffer = new byte[count];
        int got = 0;
        while (got < count)
        {
            var result = await reader.ReadAsync(ct).ConfigureAwait(false);
            var sequence = result.Buffer;
            if (sequence.Length > 0)
            {
                int take = (int)Math.Min(sequence.Length, count - got);
                sequence.Slice(0, take).CopyTo(buffer.AsSpan(got, take));
                got += take;
                reader.AdvanceTo(sequence.GetPosition(take));
            }
            else
            {
                reader.AdvanceTo(sequence.Start, sequence.End);
                if (result.IsCompleted)
                    throw new OperationCanceledException("the host closed the pipe");
            }
        }
        return buffer;
    }
}
