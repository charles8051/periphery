// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Periphery;

namespace Periphery.Bootloader.Efm8.Usb;

/// <summary>
/// Replays a hex2boot-produced boot-record stream over an <see cref="IEfm8Transport"/>
/// to the SiLabs EFM8 factory bootloader. The imperative shell that drives the pure
/// <see cref="Efm8Protocol"/> core: parse, then for each record write its chunks,
/// read the reply, and stop on the first non-acknowledge.
/// </summary>
/// <remarks>
/// <para>
/// <b>Replay only.</b> The records are written verbatim, in order. This uploader
/// never synthesises, reorders, or mutates a record. The device-bricking guarantees
/// — the reset vector being written last as a failsafe, and a Lock (<c>0x35</c>)
/// record never being emitted — live in <c>hex2boot</c>
/// (<c>hex2boot.py:153-173</c>), upstream of this tool. The
/// input <b>must</b> come from <c>hex2boot</c>; do not hand-author a boot-record
/// stream.
/// </para>
/// <para>
/// A faithful port of the upstream loop in
/// <c>treehopper-sdk/NET/API/Treehopper.Firmware/FirmwareUpdateDevice.cs:63-117</c>
/// and the SiLabs reference <c>efm8load.py:122-163</c>.
/// </para>
/// </remarks>
public static class Efm8BootloaderUploader
{
    /// <summary>
    /// The default per-reply deadline: how long to wait for the bootloader to answer one record
    /// before treating it as a stalled device. A page erase/write acknowledges in milliseconds, so
    /// this is generous; its job is only to convert an endpoint that has stopped replying (the
    /// app-mode reboot path's failure mode) into a reported result instead of an unbounded hang.
    /// </summary>
    public static readonly TimeSpan DefaultReplyTimeout = TimeSpan.FromSeconds(5);

    // Per-record diagnostic lines over the shared static sink (NullLogger unless the host wired
    // PeripheryLoggerFactory, e.g. the flasher's --log-file / -v). Static to match every other
    // Periphery.* library; the uploader is a static class, so this is the by-category logger.
    // Emitted at Debug (not Trace) on purpose: both wired sinks (--log-file and -v) filter at
    // LogLevel.Debug, so Trace would be silently dropped — defeating the capture this exists for.
    private static readonly ILogger _logger =
        PeripheryLoggerFactory.CreateLogger("Periphery.Bootloader.Efm8.Usb.Uploader");

    // A monotonic per-upload tag stamped on every line, so concurrent uploads (FlashAllAsync flashes
    // several boards at once) can be demuxed in one interleaved log. BeginScope is not an option here:
    // the Periphery sink discards scopes (SinkLogger.BeginScope returns null), so the id must ride the
    // message itself. Wraps to stay short; collision across a wrap is cosmetic (log attribution only).
    private static int _uploadSeq;

    /// <summary>
    /// Uploads a boot-record stream. Parses up front (throwing before any byte is
    /// written if the stream is malformed), then replays each record and checks the
    /// reply, stopping on the first non-acknowledge — or on the first reply that does
    /// not arrive within <paramref name="replyTimeout"/> (a stalled bootloader).
    /// </summary>
    /// <param name="transport">The open transport to the bootloader.</param>
    /// <param name="bootRecords">
    /// The raw bytes of a hex2boot-produced <c>.efm8</c>/<c>.tfi</c> file.
    /// </param>
    /// <param name="confirmation">
    /// Must be <see cref="Efm8FlashConfirmation.ConfirmEraseAndReflash"/>; this
    /// operation erases and rewrites device firmware.
    /// </param>
    /// <param name="progress">Optional per-record progress sink.</param>
    /// <param name="replyTimeout">
    /// How long to wait for each record's reply before giving up on that record and returning a
    /// timed-out result. <c>null</c> uses <see cref="DefaultReplyTimeout"/>. Must be strictly positive,
    /// or <see cref="Timeout.InfiniteTimeSpan"/> to disable the deadline (restoring the pre-timeout
    /// unbounded read); <see cref="TimeSpan.Zero"/> or a negative span throws
    /// <see cref="System.ArgumentOutOfRangeException"/>. Without a deadline, a device that stops
    /// draining its endpoint mid-flash hangs the read forever.
    /// </param>
    /// <param name="timeProvider">
    /// Clock backing the per-reply deadline (ADR-0052: the shell owns timing). <c>null</c> uses
    /// <see cref="TimeProvider.System"/>; tests inject a <c>FakeTimeProvider</c> to drive the
    /// timeout-vs-cancellation branch without real time.
    /// </param>
    /// <param name="ct">Cancellation token. Caller cancellation still throws; only the internal
    /// per-reply deadline is folded into a timed-out result. <b>Caller wins on a tie:</b> if
    /// <paramref name="ct"/> and the deadline fire together, the cancellation propagates (the result is
    /// never a spurious timeout), because the timeout is claimed only when the caller has not cancelled.</param>
    /// <returns>
    /// A success result when every record was acknowledged, a failed result naming the first record
    /// whose reply was not <c>'@'</c>, or a timed-out result naming the first record left unanswered.
    /// </returns>
    /// <exception cref="System.ArgumentException">
    /// <paramref name="confirmation"/> is not
    /// <see cref="Efm8FlashConfirmation.ConfirmEraseAndReflash"/>. Thrown before any write.
    /// </exception>
    /// <exception cref="System.ArgumentOutOfRangeException">
    /// <paramref name="replyTimeout"/> is <see cref="TimeSpan.Zero"/> or negative (and not
    /// <see cref="Timeout.InfiniteTimeSpan"/>). Thrown before any write.
    /// </exception>
    /// <exception cref="Efm8BootFormatException">
    /// <paramref name="bootRecords"/> is not a well-formed boot-record stream.
    /// Thrown before any write.
    /// </exception>
    public static Task<Efm8UploadResult> UploadAsync(
        IEfm8Transport transport,
        ReadOnlyMemory<byte> bootRecords,
        Efm8FlashConfirmation confirmation,
        IProgress<Efm8UploadProgress>? progress = null,
        TimeSpan? replyTimeout = null,
        TimeProvider? timeProvider = null,
        CancellationToken ct = default)
        => UploadAsync(transport, bootRecords, confirmation, _logger, progress, replyTimeout, timeProvider, ct);

    // Same upload, with the diagnostic sink injected. Kept internal (InternalsVisibleTo) so a test can
    // capture the Info/Debug/Warning lines — most importantly that a non-ack Warning carries the full
    // reply report — without wiring the process-wide PeripheryLoggerFactory. The public overload passes
    // the shared static sink, so production behavior is unchanged.
    internal static async Task<Efm8UploadResult> UploadAsync(
        IEfm8Transport transport,
        ReadOnlyMemory<byte> bootRecords,
        Efm8FlashConfirmation confirmation,
        ILogger logger,
        IProgress<Efm8UploadProgress>? progress = null,
        TimeSpan? replyTimeout = null,
        TimeProvider? timeProvider = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(logger);
        if (confirmation != Efm8FlashConfirmation.ConfirmEraseAndReflash)
            throw new ArgumentException(
                "An EFM8 reflash erases and rewrites device firmware. Pass "
                + "Efm8FlashConfirmation.ConfirmEraseAndReflash to proceed.",
                nameof(confirmation));

        var timeout = replyTimeout ?? DefaultReplyTimeout;
        // A non-positive deadline is a caller bug: TimeSpan.Zero would time out every record before its
        // read even runs (record 0 always "stalls"), and a negative span would throw from the CTS ctor
        // mid-loop (a surprise, and after bytes are already on the wire). Reject both up front — but let
        // Timeout.InfiniteTimeSpan through as the explicit "no deadline" opt-out the CTS ctor honours.
        if (timeout <= TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(
                nameof(replyTimeout), timeout,
                "The per-reply timeout must be positive, or Timeout.InfiniteTimeSpan to disable the deadline.");
        var clock = timeProvider ?? TimeProvider.System;

        // Parse first — a malformed stream fails here, before a single byte is sent,
        // so it can never leave a device half-written.
        var records = Efm8Protocol.ParseRecords(bootRecords);
        int totalBytes = bootRecords.Length;
        int bytesSent = 0;
        int upload = Interlocked.Increment(ref _uploadSeq);
        logger.LogInformation(
            "EFM8 upload #{Upload}: replaying {Count} records ({Bytes} bytes); per-reply timeout {Timeout}.",
            upload, records.Length, totalBytes, timeout);

        for (int i = 0; i < records.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var record = records[i];

            foreach (var chunk in Efm8Protocol.ChunkFrame(record.Frame))
                await transport.WriteOutputReportAsync(chunk, ct).ConfigureAwait(false);

            // Bound the reply read: a device that has wedged its endpoint stops replying, and an
            // unbounded ReadReplyAsync would hang the whole flash. A linked source cancels the read on
            // either the caller's ct or our own deadline; the `when` clause tells the two apart so a
            // real cancellation still propagates while a deadline becomes a reported timeout.
            Efm8Reply reply;
            using (var deadline = new CancellationTokenSource(timeout, clock))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token))
            {
                try
                {
                    reply = await transport.ReadReplyReportAsync(linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    logger.LogWarning(
                        "EFM8 upload #{Upload}: no reply for record {Index}/{Total} (command 0x{Command:X2}) within {Timeout}; "
                        + "the bootloader has stalled. {Sent} records acknowledged before the stop.",
                        upload, i, records.Length, record.Command, timeout, i);
                    return Efm8UploadResult.TimedOutAt(i, records.Length, totalBytes, record.Command);
                }
            }

            byte replyByte = reply.Status;
            var replyCode = Efm8Protocol.ClassifyReply(replyByte);
            logger.LogDebug(
                "EFM8 upload #{Upload}: record {Index}/{Total} (command 0x{Command:X2}) -> reply {Reply} (0x{Byte:X2}).",
                upload, i, records.Length, record.Command, replyCode, replyByte);

            if (replyCode != Efm8ReplyCode.Acknowledge)
            {
                // Capture the whole reply report, not just the status byte: on a concurrent-flash
                // collision the trailing bytes tell a shifted-ack framing desync from bus corruption.
                logger.LogWarning(
                    "EFM8 upload #{Upload}: record {Index}/{Total} (command 0x{Command:X2}) not acknowledged: reply {Reply} (0x{Byte:X2}); full report [{Report}].",
                    upload, i, records.Length, record.Command, replyCode, replyByte, FormatReport(reply.Report));
                return Efm8UploadResult.Failed(
                    i, records.Length, totalBytes, record.Command, replyCode, replyByte, reply.Report);
            }

            bytesSent += record.Frame.Length;
            progress?.Report(new Efm8UploadProgress(i + 1, records.Length, bytesSent, totalBytes));
        }

        logger.LogInformation(
            "EFM8 upload #{Upload}: all {Count} records acknowledged ({Bytes} bytes).", upload, records.Length, totalBytes);
        return Efm8UploadResult.Succeeded(records.Length, totalBytes);
    }

    // Space-separated hex of a reply report, for the warning line (e.g. "90 00 00 00"). Empty for an
    // empty report (a timeout has no report — that path never reaches here).
    private static string FormatReport(ImmutableArray<byte> report)
        => report.IsDefaultOrEmpty ? "" : string.Join(' ', report.Select(b => b.ToString("X2")));
}
