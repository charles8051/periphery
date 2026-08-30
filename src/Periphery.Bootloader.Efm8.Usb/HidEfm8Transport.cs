// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Threading;
using System.Threading.Tasks;
using Periphery.Hid;

namespace Periphery.Bootloader.Efm8.Usb;

/// <summary>
/// <see cref="IEfm8Transport"/> over a Periphery.Hid <see cref="HidDevice"/> — the
/// production link to the SiLabs EFM8 factory USB-HID bootloader.
/// </summary>
/// <remarks>
/// <para>
/// <b>Report ID / chunking.</b> The bootloader exposes a single unnamed output and
/// input report, so the report ID is always <c>0</c>. Each chunk is written as
/// <c>new HidReport(0, chunk)</c>; the Periphery.Hid Windows backend builds a buffer
/// of the descriptor-declared output length (65 bytes = the report ID byte + the
/// 64-byte payload), sets <c>buffer[0] = 0</c>, copies the chunk after it, and
/// zero-pads the rest (<c>WindowsHidDevice.cs:160-178</c>). That reproduces exactly
/// what the SiLabs reference does on Windows — <c>efm8load.py</c> prepends a dummy
/// report-ID byte <c>b'\x00'</c> to every output report (<c>efm8load.py:44-48,58-64</c>)
/// and the upstream C# loader writes a <c>SizeOut + 1</c> buffer with
/// <c>buffer[0] = 0</c> (<c>FirmwareUpdateDevice.cs:105-111</c>). The chunking is
/// done by <see cref="Efm8Protocol.ChunkFrame"/>, not here.
/// </para>
/// <para>
/// On read, the OS prepends the report ID; Periphery.Hid strips it into
/// <see cref="HidReport.ReportId"/> and returns the payload in
/// <see cref="HidReport.Data"/> (<c>WindowsHidDevice.cs:134-158</c>), so the reply
/// status is <c>Data.Span[0]</c> — the same byte <c>efm8load.py</c> reads as
/// <c>in_report[0]</c> (<c>efm8load.py:50-56</c>).
/// </para>
/// <para>
/// This transport does not own the <see cref="HidDevice"/>; the caller opens and
/// disposes it (so the device can be re-polled after the bootloader resets into the
/// app).
/// </para>
/// </remarks>
public sealed class HidEfm8Transport : IEfm8Transport
{
    /// <summary>Reply byte for a timeout / empty read — mirrors efm8load's <c>'?'</c>.</summary>
    private const byte UnknownReply = (byte)'?';

    private readonly HidDevice _device;

    /// <summary>Wraps an already-open bootloader HID device.</summary>
    /// <param name="device">
    /// An open handle to the EFM8 HID bootloader. The caller is responsible for
    /// having verified it is the bootloader VID/PID before constructing this.
    /// </param>
    public HidEfm8Transport(HidDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        _device = device;
    }

    /// <inheritdoc/>
    public Task WriteOutputReportAsync(ReadOnlyMemory<byte> reportChunk, CancellationToken ct)
        => _device.WriteReportAsync(new HidReport(0x00, reportChunk), ct);

    /// <inheritdoc/>
    public async Task<byte> ReadReplyAsync(CancellationToken ct)
        => (await ReadReplyReportAsync(ct).ConfigureAwait(false)).Status;

    /// <inheritdoc/>
    public async Task<Efm8Reply> ReadReplyReportAsync(CancellationToken ct)
    {
        var report = await _device.ReadReportAsync(ct).ConfigureAwait(false);
        // Keep the whole input report, not just the status byte: on a bad reply the trailing bytes
        // tell a shifted-ack framing desync from pure bus corruption (see Efm8Reply). An empty read
        // maps to the '?' non-acknowledge, as efm8load does.
        return Efm8Reply.FromReport(report.Data.Span, UnknownReply);
    }
}
