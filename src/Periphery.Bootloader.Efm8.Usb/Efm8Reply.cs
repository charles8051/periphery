// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.Immutable;

namespace Periphery.Bootloader.Efm8.Usb;

/// <summary>
/// One reply report read back from the EFM8 bootloader: the <see cref="Status"/> byte the uploader
/// classifies, plus the <see cref="Report"/> — the whole input report the OS delivered
/// (<see cref="Efm8Protocol.InputReportSize"/> bytes on the wire).
/// </summary>
/// <remarks>
/// <para>
/// The bootloader's protocol answer is the first byte alone; historically the transport returned only
/// that. But the trailing bytes are diagnostic gold on a <b>bad</b> reply. When a concurrent-flash
/// collision produces a garbage <c>0x90</c> where a <c>0x40</c> ('@') ack was due, the rest of the
/// report distinguishes a framing/endpoint desync (a shifted-but-recognizable ack) from pure bus
/// corruption. Carrying the full report costs nothing on the happy path and preserves that evidence
/// for the failure path (surfaced via <see cref="Efm8UploadResult.FailedReplyBytes"/>).
/// </para>
/// </remarks>
/// <param name="Status">
/// The reply status byte the uploader classifies — <see cref="Report"/>'s first byte, or the supplied
/// empty-read sentinel when the report was empty.
/// </param>
/// <param name="Report">The full input report as delivered (may be empty on a timeout / empty read).</param>
public readonly record struct Efm8Reply(byte Status, ImmutableArray<byte> Report)
{
    /// <summary>
    /// Builds a reply from a raw input report. <see cref="Status"/> is the report's first byte, or
    /// <paramref name="emptyStatus"/> when the report is empty (nothing was read).
    /// </summary>
    public static Efm8Reply FromReport(ReadOnlySpan<byte> report, byte emptyStatus)
        => report.Length > 0
            ? new Efm8Reply(report[0], ImmutableArray.Create(report))
            : new Efm8Reply(emptyStatus, ImmutableArray<byte>.Empty);
}
