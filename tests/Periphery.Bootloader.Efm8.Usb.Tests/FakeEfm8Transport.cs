using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;

namespace Periphery.Bootloader.Efm8.Usb.Tests;

/// <summary>
/// In-memory <see cref="IEfm8Transport"/> for exercising the uploader with no
/// hardware. Records every output-report chunk (a defensive copy) and serves reply
/// bytes from a per-read function, so a test can assert the exact bytes written and
/// the precise stop behaviour on an error reply.
/// </summary>
internal sealed class FakeEfm8Transport : IEfm8Transport
{
    private readonly Func<int, byte> _replyForRead;
    private readonly Func<int, ImmutableArray<byte>>? _reportForRead; // full report per read, when a test needs the trailing bytes
    private readonly int _hangAtRead;   // read index whose reply never arrives (int.MaxValue = never hang)

    /// <summary>Every chunk handed to <see cref="WriteOutputReportAsync"/>, in order.</summary>
    public List<byte[]> Writes { get; } = new();

    /// <summary>Number of reply reads performed.</summary>
    public int ReadCount { get; private set; }

    /// <param name="replyForRead">
    /// Maps the zero-based read index to the reply byte. Defaults to acknowledging
    /// every record (<c>'@'</c>).
    /// </param>
    /// <param name="hangAtRead">
    /// Zero-based read index whose reply never arrives — the read blocks until its token is
    /// cancelled (a wedged endpoint). Defaults to never hanging.
    /// </param>
    public FakeEfm8Transport(
        Func<int, byte>? replyForRead = null,
        int hangAtRead = int.MaxValue,
        Func<int, ImmutableArray<byte>>? reportForRead = null)
    {
        _replyForRead = replyForRead ?? (_ => Efm8Protocol.AckByte);
        _reportForRead = reportForRead;
        _hangAtRead = hangAtRead;
    }

    /// <summary>A transport that acknowledges the first <paramref name="ackCount"/>
    /// records, then returns <paramref name="thenReply"/> for the next read.</summary>
    public static FakeEfm8Transport AckThen(int ackCount, byte thenReply)
        => new(read => read < ackCount ? Efm8Protocol.AckByte : thenReply);

    /// <summary>
    /// Acknowledges the first <paramref name="ackCount"/> records, then delivers <paramref name="thenReport"/>
    /// as the full input report for the next read — the status byte is its first byte. Lets a test assert
    /// the whole failing report is captured, not just the status.
    /// </summary>
    public static FakeEfm8Transport AckThenReport(int ackCount, params byte[] thenReport)
        => new(reportForRead: read => read < ackCount
            ? ImmutableArray.Create(Efm8Protocol.AckByte)
            : ImmutableArray.Create(thenReport));

    /// <summary>A transport that acknowledges the first <paramref name="ackCount"/> records, then
    /// never replies to the next read — the read blocks until cancelled (a stalled bootloader).</summary>
    public static FakeEfm8Transport AckThenHang(int ackCount)
        => new(hangAtRead: ackCount);

    public Task WriteOutputReportAsync(ReadOnlyMemory<byte> reportChunk, CancellationToken ct)
    {
        Writes.Add(reportChunk.ToArray());
        return Task.CompletedTask;
    }

    public async Task<byte> ReadReplyAsync(CancellationToken ct)
        => (await ReadReplyReportAsync(ct).ConfigureAwait(false)).Status;

    public async Task<Efm8Reply> ReadReplyReportAsync(CancellationToken ct)
    {
        int read = ReadCount++;
        if (read == _hangAtRead)
            // Block exactly as a wedged HID read does: return nothing until the token is cancelled.
            // The uploader's per-reply deadline is what cancels it, so this exercises the timeout path.
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
        if (_reportForRead is not null)
            return Efm8Reply.FromReport(_reportForRead(read).AsSpan(), (byte)'?');
        byte status = _replyForRead(read); // capture once: a stateful scripted func must advance only per read
        return new Efm8Reply(status, ImmutableArray.Create(status));
    }
}
