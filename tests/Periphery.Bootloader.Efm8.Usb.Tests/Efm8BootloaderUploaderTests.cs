using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Periphery.Bootloader.Efm8.Usb.Tests;

/// <summary>
/// Behavioural tests for the uploader shell driven against <see cref="FakeEfm8Transport"/>:
/// chunked writes, all-ack success, malformed-input rejection before any write,
/// stop-on-error with no further writes, and the destructive-confirmation gate.
/// </summary>
public class Efm8BootloaderUploaderTests
{
    private const Efm8FlashConfirmation Confirm = Efm8FlashConfirmation.ConfirmEraseAndReflash;

    [Fact]
    public async Task UploadAsync_WellFormedStream_AllAcked_Succeeds()
    {
        var stream = BootRecordBuilder.Stream(
            BootRecordBuilder.Frame(0x31, 0xA5, 0xF1, 0x00),
            BootRecordBuilder.Frame(0x33, BootRecordBuilder.Bytes(8)),
            BootRecordBuilder.Frame(0x36, 0x00, 0x00));
        var transport = new FakeEfm8Transport();

        var result = await Efm8BootloaderUploader.UploadAsync(transport, stream, Confirm);

        Assert.True(result.Success);
        Assert.Equal(3, result.RecordsSent);
        Assert.Equal(3, result.TotalRecords);
        Assert.Equal(stream.Length, result.TotalBytes);
        Assert.Equal(3, transport.ReadCount); // one reply read per record
        Assert.Null(result.FailedRecordIndex);
    }

    [Fact]
    public async Task UploadAsync_FrameLargerThanReport_WritesExactChunkSequence()
    {
        // Record 0: small (1 chunk). Record 1: 103-byte frame -> 64 + 39 (2 chunks).
        var small = BootRecordBuilder.Frame(0x31, 0xA5, 0xF1, 0x00); // 6 bytes
        var large = BootRecordBuilder.Frame(0x33, BootRecordBuilder.Bytes(100)); // 103 bytes
        var stream = BootRecordBuilder.Stream(small, large);
        var transport = new FakeEfm8Transport();

        var result = await Efm8BootloaderUploader.UploadAsync(transport, stream, Confirm);

        Assert.True(result.Success);
        Assert.Equal(3, transport.Writes.Count); // 1 + 2 chunks
        Assert.Equal(small, transport.Writes[0]);
        Assert.Equal(64, transport.Writes[1].Length);
        Assert.Equal(39, transport.Writes[2].Length);
        // The two chunks of the large frame reconstruct it byte-for-byte.
        var rejoined = transport.Writes[1].Concat(transport.Writes[2]).ToArray();
        Assert.Equal(large, rejoined);
    }

    [Fact]
    public async Task UploadAsync_MalformedStream_ThrowsBeforeAnyWrite()
    {
        byte[] malformed = [0x24, 0x0A, 0x33, 0x00, 0x00]; // length 10 overruns
        var transport = new FakeEfm8Transport();

        await Assert.ThrowsAsync<Efm8BootFormatException>(
            () => Efm8BootloaderUploader.UploadAsync(transport, malformed, Confirm));

        Assert.Empty(transport.Writes);
        Assert.Equal(0, transport.ReadCount);
    }

    [Fact]
    public async Task UploadAsync_CrcErrorMidStream_StopsAndReportsRecord_NoFurtherWrites()
    {
        var r0 = BootRecordBuilder.Frame(0x31, 0xA5, 0xF1, 0x00);
        var r1 = BootRecordBuilder.Frame(0x33, BootRecordBuilder.Bytes(4)); // the failing record
        var r2 = BootRecordBuilder.Frame(0x36, 0x00, 0x00);
        var stream = BootRecordBuilder.Stream(r0, r1, r2);
        // Ack record 0, then return 'B' (CRC error) for record 1's reply.
        var transport = FakeEfm8Transport.AckThen(ackCount: 1, thenReply: 0x42);

        var result = await Efm8BootloaderUploader.UploadAsync(transport, stream, Confirm);

        Assert.False(result.Success);
        Assert.Equal(1, result.FailedRecordIndex);
        Assert.Equal((byte)0x33, result.FailedCommand);
        Assert.Equal(Efm8ReplyCode.CrcError, result.FailedReply);
        Assert.Equal((byte)0x42, result.FailedReplyByte);
        // Wrote record 0 (1 chunk) + record 1 (1 chunk), then stopped — record 2 never sent.
        Assert.Equal(2, transport.Writes.Count);
        Assert.Equal(2, transport.ReadCount);
    }

    [Theory]
    // Pure bus corruption: the status byte is garbage and the trailing bytes are noise.
    [InlineData(new byte[] { 0x90, 0x11, 0x22, 0x33 }, "90 11 22 33")]
    // Shifted-ack framing desync: the same 0x90 status, but the tail carries a recognizable ack (0x40).
    // The point of capturing the *whole* report is that these two shapes are distinguishable downstream;
    // the uploader must preserve either verbatim — it classifies neither, it just surfaces the evidence.
    [InlineData(new byte[] { 0x90, 0x40, 0x00, 0x00 }, "90 40 00 00")]
    public async Task UploadAsync_NonAck_CapturesTheFullReplyReport_NotJustTheStatusByte(byte[] report, string expectedHex)
    {
        // The concurrent-flash corruption shows up as a garbage 0x90 where a 0x40 ('@') ack was due.
        // The status byte alone can't tell a shifted-ack framing desync from bus corruption — the whole
        // 4-byte input report can. Prove the uploader preserves all of it, whatever the shape (Observability A).
        var r0 = BootRecordBuilder.Frame(0x31, 0xA5, 0xF1, 0x00);
        var r1 = BootRecordBuilder.Frame(0x33, BootRecordBuilder.Bytes(4)); // the failing record
        var stream = BootRecordBuilder.Stream(r0, r1);
        // Ack record 0, then deliver the full 4-byte report for record 1's reply.
        var transport = FakeEfm8Transport.AckThenReport(ackCount: 1, report);

        var log = new CapturingLogger();
        var result = await Efm8BootloaderUploader.UploadAsync(transport, stream, Confirm, log);

        Assert.False(result.Success);
        Assert.Equal(1, result.FailedRecordIndex);
        Assert.Equal((byte)0x90, result.FailedReplyByte);         // the status byte, kept for compatibility
        Assert.Equal(report, result.FailedReplyBytes.ToArray());  // ...and now the whole report, verbatim
        Assert.Contains(expectedHex, result.Describe());          // surfaced in the one-line summary

        // ...and the production diagnostic operators actually see — the non-ack Warning — carries the
        // same full report, not just the status byte. Pins the LogWarning line, whatever the shape.
        var warning = Assert.Single(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("not acknowledged"));
        Assert.Contains(expectedHex, warning.Message);
    }

    [Fact]
    public async Task UploadAsync_Success_HasEmptyFailedReplyBytes()
    {
        var stream = BootRecordBuilder.Stream(BootRecordBuilder.Frame(0x36, 0x00, 0x00));

        var result = await Efm8BootloaderUploader.UploadAsync(new FakeEfm8Transport(), stream, Confirm);

        Assert.True(result.Success);
        Assert.True(result.FailedReplyBytes.IsEmpty); // never default (would throw on use), just empty
    }

    [Theory]
    [InlineData(Efm8FlashConfirmation.Unconfirmed)]
    [InlineData((Efm8FlashConfirmation)99)]
    public async Task UploadAsync_WithoutConfirmation_ThrowsBeforeAnyWrite(Efm8FlashConfirmation confirmation)
    {
        var stream = BootRecordBuilder.Stream(BootRecordBuilder.Frame(0x36, 0x00, 0x00));
        var transport = new FakeEfm8Transport();

        await Assert.ThrowsAsync<ArgumentException>(
            () => Efm8BootloaderUploader.UploadAsync(transport, stream, confirmation));

        Assert.Empty(transport.Writes);
        Assert.Equal(0, transport.ReadCount);
    }

    [Fact]
    public async Task UploadAsync_ReportsProgressPerAcknowledgedRecord()
    {
        var stream = BootRecordBuilder.Stream(
            BootRecordBuilder.Frame(0x31, 0xA5, 0xF1, 0x00),
            BootRecordBuilder.Frame(0x33, BootRecordBuilder.Bytes(8)),
            BootRecordBuilder.Frame(0x36, 0x00, 0x00));
        var transport = new FakeEfm8Transport();
        // Synchronous sink: the uploader calls Report inline on its own thread, so by
        // the time UploadAsync returns every report is recorded (no marshalling race,
        // unlike Progress<T> which posts to the captured context / thread pool).
        var snapshots = new List<Efm8UploadProgress>();
        var progress = new SyncProgress<Efm8UploadProgress>(snapshots.Add);

        var result = await Efm8BootloaderUploader.UploadAsync(transport, stream, Confirm, progress);

        Assert.True(result.Success);
        Assert.Equal(3, snapshots.Count);
        Assert.Equal(new[] { 1, 2, 3 }, snapshots.Select(s => s.RecordsSent));
        Assert.Equal(3, snapshots[^1].RecordsSent);
        Assert.Equal(stream.Length, snapshots[^1].BytesSent);
        Assert.Equal(100.0, snapshots[^1].Percent, 3);
    }

    [Fact]
    public async Task UploadAsync_ReplyNeverArrives_TimesOutAtThatRecord_WithoutHanging()
    {
        var r0 = BootRecordBuilder.Frame(0x31, 0xA5, 0xF1, 0x00);
        var r1 = BootRecordBuilder.Frame(0x33, BootRecordBuilder.Bytes(4)); // reply never comes
        var r2 = BootRecordBuilder.Frame(0x36, 0x00, 0x00);
        var stream = BootRecordBuilder.Stream(r0, r1, r2);
        // Ack record 0, then the read for record 1 blocks until its deadline cancels it.
        var transport = FakeEfm8Transport.AckThenHang(ackCount: 1);
        var time = new FakeTimeProvider();
        var timeout = TimeSpan.FromSeconds(5);

        var task = Efm8BootloaderUploader.UploadAsync(
            transport, stream, Confirm, replyTimeout: timeout, timeProvider: time);

        // Falsifiable: just under the deadline, the upload is still waiting — proving the timeout value
        // is load-bearing (a shorter/absent deadline would already have completed or hung).
        time.Advance(timeout - TimeSpan.FromMilliseconds(1));
        Assert.False(task.IsCompleted);

        // Crossing the deadline turns the stalled read into a reported timeout, not an infinite hang.
        time.Advance(TimeSpan.FromMilliseconds(1));
        var result = await task;

        Assert.False(result.Success);
        Assert.True(result.TimedOut);
        Assert.Equal(1, result.FailedRecordIndex);
        Assert.Equal((byte)0x33, result.FailedCommand);
        Assert.Equal(Efm8ReplyCode.Unknown, result.FailedReply);
        Assert.Null(result.FailedReplyByte);          // a timeout has no reply byte
        Assert.Equal(1, result.RecordsSent);          // only record 0 acknowledged
        Assert.Equal(2, transport.ReadCount);         // read 0 (ack) + read 1 (hung, then cancelled)
        Assert.Contains("timed out at record 1", result.Describe());
    }

    [Fact]
    public async Task UploadAsync_CallerCancellation_WhileWaitingReply_Throws_NotReportedAsTimeout()
    {
        var stream = BootRecordBuilder.Stream(
            BootRecordBuilder.Frame(0x31, 0xA5, 0xF1, 0x00),
            BootRecordBuilder.Frame(0x33, BootRecordBuilder.Bytes(4)));
        var transport = FakeEfm8Transport.AckThenHang(ackCount: 1);
        var time = new FakeTimeProvider();     // never advanced: the deadline never fires
        using var cts = new CancellationTokenSource();

        var task = Efm8BootloaderUploader.UploadAsync(
            transport, stream, Confirm, replyTimeout: TimeSpan.FromSeconds(30), timeProvider: time, ct: cts.Token);

        // A real caller cancellation must surface as cancellation — never be swallowed into a
        // timed-out result — so a caller can tell "operator aborted" from "the board stalled".
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task UploadAsync_ReplyNeverArrivesOnFirstRecord_TimesOutAtIndexZero()
    {
        // Symmetric to the mid-stream case: the very first read hangs, so RecordsSent == 0 and the
        // failing index is 0. No first-record special-casing, but the boundary is worth pinning.
        var stream = BootRecordBuilder.Stream(
            BootRecordBuilder.Frame(0x31, 0xA5, 0xF1, 0x00),
            BootRecordBuilder.Frame(0x36, 0x00, 0x00));
        var transport = FakeEfm8Transport.AckThenHang(ackCount: 0); // read 0 hangs
        var time = new FakeTimeProvider();
        var timeout = TimeSpan.FromSeconds(5);

        var task = Efm8BootloaderUploader.UploadAsync(
            transport, stream, Confirm, replyTimeout: timeout, timeProvider: time);
        time.Advance(timeout);
        var result = await task;

        Assert.True(result.TimedOut);
        Assert.Equal(0, result.FailedRecordIndex);
        Assert.Equal(0, result.RecordsSent);
        Assert.Equal((byte)0x31, result.FailedCommand);
    }

    [Fact]
    public async Task UploadAsync_CallerCancelAndDeadlineBothFire_CallerWins()
    {
        // The tie: caller cancellation and the per-reply deadline are both requested before the read's
        // continuation runs. The `when` clause must let cancellation win — never a spurious timeout —
        // so an operator abort is never misreported as a stalled board.
        var stream = BootRecordBuilder.Stream(
            BootRecordBuilder.Frame(0x31, 0xA5, 0xF1, 0x00),
            BootRecordBuilder.Frame(0x33, BootRecordBuilder.Bytes(4)));
        var transport = FakeEfm8Transport.AckThenHang(ackCount: 1);
        var time = new FakeTimeProvider();
        var timeout = TimeSpan.FromSeconds(5);
        using var cts = new CancellationTokenSource();

        var task = Efm8BootloaderUploader.UploadAsync(
            transport, stream, Confirm, replyTimeout: timeout, timeProvider: time, ct: cts.Token);
        cts.Cancel();          // caller cancels...
        time.Advance(timeout); // ...and the deadline also elapses — the tie

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Theory]
    [InlineData(0)]      // TimeSpan.Zero — would time out record 0 before its read even runs
    [InlineData(-5)]     // negative — would otherwise throw from the CTS ctor mid-loop
    public async Task UploadAsync_NonPositiveTimeout_ThrowsBeforeAnyWrite(int seconds)
    {
        var stream = BootRecordBuilder.Stream(BootRecordBuilder.Frame(0x36, 0x00, 0x00));
        var transport = new FakeEfm8Transport();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => Efm8BootloaderUploader.UploadAsync(
                transport, stream, Confirm, replyTimeout: TimeSpan.FromSeconds(seconds)));

        Assert.Empty(transport.Writes); // rejected up front, nothing on the wire
        Assert.Equal(0, transport.ReadCount);
    }

    [Fact]
    public async Task UploadAsync_InfiniteTimeout_IsAccepted_AndFlashesNormally()
    {
        // Timeout.InfiniteTimeSpan is the documented opt-out (restores the unbounded read); with a
        // responsive device it must behave exactly like a normal flash, not be rejected as non-positive.
        var stream = BootRecordBuilder.Stream(
            BootRecordBuilder.Frame(0x31, 0xA5, 0xF1, 0x00),
            BootRecordBuilder.Frame(0x36, 0x00, 0x00));
        var transport = new FakeEfm8Transport();

        var result = await Efm8BootloaderUploader.UploadAsync(
            transport, stream, Confirm, replyTimeout: Timeout.InfiniteTimeSpan);

        Assert.True(result.Success);
        Assert.Equal(2, result.RecordsSent);
    }

    [Fact]
    public void TimedOutResult_Describes_AsTimeout_NotNak()
    {
        var timedOut = Efm8UploadResult.TimedOutAt(
            failedIndex: 31, totalRecords: 120, totalBytes: 5000, command: 0x33);

        Assert.False(timedOut.Success);
        Assert.True(timedOut.TimedOut);
        var text = timedOut.Describe();
        Assert.Contains("timed out at record 31", text);
        Assert.Contains("command 0x33", text);
        Assert.Contains("no reply from the bootloader", text);
        // A timeout has no reply byte, so it must NOT render the NAK path's "reply Unknown (0x..)"
        // clause — which for a null byte would print the garbage "(0x)".
        Assert.DoesNotContain("reply Unknown", text);
        Assert.DoesNotContain("(0x)", text);
    }

    private sealed class SyncProgress<T>(Action<T> onReport) : IProgress<T>
    {
        public void Report(T value) => onReport(value);
    }
}
