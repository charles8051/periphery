// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Collections.Immutable;
using System.Linq;

namespace Periphery.Bootloader.Efm8.Usb;

/// <summary>
/// The outcome of an upload. <see cref="Success"/> is <c>true</c> only when every
/// record was acknowledged with <c>'@'</c>. On the first non-acknowledge reply the
/// uploader stops immediately (no further writes) and returns a failed result naming
/// the offending record.
/// </summary>
/// <param name="Success">Whether every record was acknowledged.</param>
/// <param name="RecordsSent">
/// Number of records acknowledged. On failure this is the count before the failing
/// record (i.e. the failing record's index).
/// </param>
/// <param name="TotalRecords">Total records in the stream.</param>
/// <param name="TotalBytes">Total frame bytes in the stream.</param>
/// <param name="FailedRecordIndex">Index of the record that failed, or <c>null</c> on success.</param>
/// <param name="FailedCommand">The failing record's command byte, or <c>null</c> on success.</param>
/// <param name="FailedReply">Classification of the failing reply, or <c>null</c> on success.</param>
/// <param name="FailedReplyByte">
/// The raw failing reply byte, or <c>null</c> on success <b>and on a timeout</b> (a timeout is the
/// absence of any reply byte, so there is none to report — see <see cref="TimedOut"/>).
/// </param>
/// <param name="TimedOut">
/// <c>true</c> when the failure was the bootloader not replying to <see cref="FailedRecordIndex"/>
/// within the per-reply deadline (a stalled/wedged device), as opposed to it replying with a
/// non-acknowledge byte. Distinguishing the two matters: a wrong byte is a protocol rejection, a
/// timeout is a hung endpoint — the failure the app-mode reboot path exhibits mid-flash.
/// </param>
public sealed record Efm8UploadResult(
    bool Success,
    int RecordsSent,
    int TotalRecords,
    int TotalBytes,
    int? FailedRecordIndex,
    byte? FailedCommand,
    Efm8ReplyCode? FailedReply,
    byte? FailedReplyByte,
    bool TimedOut = false)
{
    /// <summary>
    /// The <b>full</b> failing reply report (every <see cref="Efm8Protocol.InputReportSize"/> byte the
    /// OS delivered), not just <see cref="FailedReplyByte"/>. Empty on success and on a timeout (a
    /// timeout has no report at all). On a non-acknowledge these trailing bytes are the diagnostic that
    /// tells a shifted-ack framing/endpoint desync from pure bus corruption — the two ways a concurrent
    /// no-serial flash can produce a garbage reply. <see cref="FailedReplyByte"/> (this report's first
    /// byte) is kept unchanged for compatibility.
    /// <para>
    /// Declared as an init property (not a positional parameter) so the primary constructor and
    /// <c>Deconstruct</c> shape are unchanged. It <b>does</b> participate in the record's synthesized
    /// equality/hash — intentionally: two results that failed with different reply reports are not
    /// equal. (<see cref="ImmutableArray{T}"/> compares by underlying-array reference, so equality is
    /// most meaningful for the same instance; the field exists for diagnostics, not as an equality key.)
    /// </para>
    /// </summary>
    public ImmutableArray<byte> FailedReplyBytes { get; init; } = ImmutableArray<byte>.Empty;

    internal static Efm8UploadResult Succeeded(int totalRecords, int totalBytes)
        => new(true, totalRecords, totalRecords, totalBytes, null, null, null, null);

    internal static Efm8UploadResult Failed(
        int failedIndex, int totalRecords, int totalBytes, byte command, Efm8ReplyCode reply, byte replyByte,
        ImmutableArray<byte> replyBytes = default)
        => new(false, failedIndex, totalRecords, totalBytes, failedIndex, command, reply, replyByte)
        {
            FailedReplyBytes = replyBytes.IsDefault ? ImmutableArray<byte>.Empty : replyBytes,
        };

    /// <summary>
    /// The bootloader sent no reply to record <paramref name="failedIndex"/> within the per-reply
    /// deadline. Classified <see cref="Efm8ReplyCode.Unknown"/> with no reply byte, but flagged
    /// <see cref="TimedOut"/> so callers and <see cref="Describe"/> can tell a hang from a NAK.
    /// </summary>
    internal static Efm8UploadResult TimedOutAt(
        int failedIndex, int totalRecords, int totalBytes, byte command)
        => new(false, failedIndex, totalRecords, totalBytes, failedIndex, command,
            Efm8ReplyCode.Unknown, FailedReplyByte: null, TimedOut: true);

    /// <summary>A one-line human-readable summary suitable for logging or a CLI line.</summary>
    public string Describe()
    {
        if (Success)
            return $"EFM8 upload succeeded: {RecordsSent}/{TotalRecords} records ({TotalBytes} bytes) acknowledged.";
        if (TimedOut)
            return $"EFM8 upload timed out at record {FailedRecordIndex} (command 0x{FailedCommand:X2}): "
                + $"no reply from the bootloader. {RecordsSent}/{TotalRecords} records sent before the stop.";
        return $"EFM8 upload failed at record {FailedRecordIndex} (command 0x{FailedCommand:X2}): "
            + $"reply {FailedReply} (0x{FailedReplyByte:X2}){DescribeFullReport()}. "
            + $"{RecordsSent}/{TotalRecords} records sent before the stop.";
    }

    // The full reply report appended to Describe() only when it carries more than the status byte
    // already shown — the trailing bytes that disambiguate a shifted-ack desync from bus corruption.
    private string DescribeFullReport()
        => FailedReplyBytes.Length > 1
            ? $" [full reply {string.Join(' ', FailedReplyBytes.Select(b => b.ToString("X2")))}]"
            : "";
}
