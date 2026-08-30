// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;

namespace Periphery.Bootloader.Efm8.Usb;

/// <summary>
/// The narrow transport seam between the EFM8 uploader and the physical link.
/// The imperative shell of the functional-core / imperative-shell split — the only
/// place real IO happens. Tests substitute a fake to exercise the uploader with no
/// hardware (assert the exact chunks written, script the reply bytes).
/// </summary>
/// <remarks>
/// The production implementation is <see cref="HidEfm8Transport"/>; the protocol is
/// otherwise link-agnostic (the SiLabs bootloader also speaks UART and SMBus, see
/// <c>efm8load.py</c>), so the uploader binds to this seam rather than to HID.
/// </remarks>
public interface IEfm8Transport
{
    /// <summary>
    /// Writes one output report carrying <paramref name="reportChunk"/> (at most
    /// <see cref="Efm8Protocol.OutputReportSize"/> bytes) to the device.
    /// </summary>
    Task WriteOutputReportAsync(ReadOnlyMemory<byte> reportChunk, CancellationToken ct);

    /// <summary>
    /// Reads one input report and returns its first byte — the bootloader's reply
    /// status. Implementations map a timeout / empty read to a non-acknowledge byte
    /// (e.g. <c>0x3F</c>) rather than blocking forever.
    /// </summary>
    Task<byte> ReadReplyAsync(CancellationToken ct);

    /// <summary>
    /// Reads one input report as an <see cref="Efm8Reply"/>: the reply <see cref="Efm8Reply.Status"/>
    /// byte the uploader classifies, plus the full <see cref="Efm8Reply.Report"/> for diagnostics on a
    /// bad reply (the trailing bytes tell a shifted-ack framing desync from bus corruption). The
    /// uploader reads through this so a failure preserves the whole report.
    /// </summary>
    /// <remarks>
    /// Additive over <see cref="ReadReplyAsync"/> (which keeps its <c>byte</c> contract): the default
    /// implementation wraps the status byte into a one-byte report, so an existing implementer that only
    /// supplies the status keeps working. A transport that can surface the full input report — as
    /// <see cref="HidEfm8Transport"/> does — overrides this to return every byte.
    /// </remarks>
    async Task<Efm8Reply> ReadReplyReportAsync(CancellationToken ct)
    {
        byte status = await ReadReplyAsync(ct).ConfigureAwait(false);
        return new Efm8Reply(status, ImmutableArray.Create(status));
    }
}
