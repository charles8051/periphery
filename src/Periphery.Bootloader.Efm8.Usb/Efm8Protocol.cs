// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace Periphery.Bootloader.Efm8.Usb;

/// <summary>
/// The pure, total core of the EFM8 HID bootloader protocol (ADR-0052
/// functional-core / imperative-shell). No IO, no clock, no <see cref="System.Threading.Tasks.Task"/>:
/// parse a boot-record stream into frames, split a frame into output-report chunks,
/// and classify a reply byte. Exhaustively unit-testable with hand-built byte arrays.
/// </summary>
/// <remarks>
/// Protocol facts confirmed against the SiLabs references (both verified, in
/// agreement): <c>efm8load.py:39-64,140-145</c>
/// (<c>SIZE_OUT = 64</c>, <c>SIZE_IN = 4</c>, the <c>'$'</c>/length framing, the
/// 64-byte chunked write), <c>hex2boot.py:72-107</c> (the
/// record encoders that produce the <c>.efm8</c>), and the upstream C# loader
/// <c>treehopper-sdk/NET/API/Treehopper.Firmware/FirmwareUpdateDevice.cs:17-18,67-74,105-116</c>.
/// </remarks>
public static class Efm8Protocol
{
    /// <summary>Frame start byte <c>'$'</c> (0x24). Every record begins with it.</summary>
    public const byte StartByte = (byte)'$';

    /// <summary>Success reply byte <c>'@'</c> (0x40). The only reply that continues an upload.</summary>
    public const byte AckByte = (byte)'@';

    /// <summary>
    /// Host-to-device output report payload size, excluding the report ID byte.
    /// A frame is written in chunks of at most this many bytes
    /// (<c>efm8load.py</c> <c>SIZE_OUT = 64</c>).
    /// </summary>
    public const int OutputReportSize = 64;

    /// <summary>
    /// Device-to-host input report payload size, excluding the report ID byte
    /// (<c>efm8load.py</c> <c>SIZE_IN = 4</c>). The reply status is its first byte.
    /// </summary>
    public const int InputReportSize = 4;

    /// <summary>
    /// Parses a boot-record stream into its constituent frames. Total and
    /// allocation-light: each <see cref="Efm8BootRecord"/> is a slice of
    /// <paramref name="bootRecords"/>, not a copy.
    /// </summary>
    /// <param name="bootRecords">
    /// The raw bytes of a hex2boot-produced <c>.efm8</c>/<c>.tfi</c> file.
    /// </param>
    /// <returns>The records, in stream order.</returns>
    /// <exception cref="Efm8BootFormatException">
    /// The stream is empty, a record's start byte is not <c>0x24</c>, a record
    /// declares zero length (no command byte), or a declared length runs past the
    /// end of the stream. Thrown before the caller writes any byte to a device.
    /// </exception>
    public static ImmutableArray<Efm8BootRecord> ParseRecords(ReadOnlyMemory<byte> bootRecords)
    {
        var span = bootRecords.Span;
        var builder = ImmutableArray.CreateBuilder<Efm8BootRecord>();

        int offset = 0;
        int index = 0;
        while (offset < span.Length)
        {
            if (span.Length - offset < 2)
                throw new Efm8BootFormatException(
                    $"Truncated record header at offset {offset}: a record needs at least a " +
                    $"'$' byte and a length byte, but only {span.Length - offset} byte(s) remain.");

            if (span[offset] != StartByte)
                throw new Efm8BootFormatException(
                    $"Malformed boot record {index} at offset {offset}: expected start byte " +
                    $"'$' (0x{StartByte:X2}) but found 0x{span[offset]:X2}.");

            int declaredLength = span[offset + 1];
            if (declaredLength < 1)
                throw new Efm8BootFormatException(
                    $"Malformed boot record {index} at offset {offset}: declared length is 0, " +
                    "so the record carries no command byte.");

            int frameLength = 2 + declaredLength;
            if (offset + frameLength > span.Length)
                throw new Efm8BootFormatException(
                    $"Malformed boot record {index} at offset {offset}: declared length " +
                    $"{declaredLength} needs {frameLength} byte(s), but only {span.Length - offset} " +
                    "remain (stream ends mid-record).");

            builder.Add(new Efm8BootRecord(index, bootRecords.Slice(offset, frameLength)));
            offset += frameLength;
            index++;
        }

        if (builder.Count == 0)
            throw new Efm8BootFormatException("Empty boot-record stream: no records to upload.");

        return builder.ToImmutable();
    }

    /// <summary>
    /// Splits a frame into successive output-report chunks of at most
    /// <paramref name="reportSize"/> bytes (default <see cref="OutputReportSize"/>),
    /// mirroring <c>efm8load.py</c>'s <c>write()</c> loop (<c>efm8load.py:61-64</c>).
    /// The final chunk is short when the frame length is not a multiple of the
    /// report size. Each returned segment is a slice, not a copy.
    /// </summary>
    /// <param name="frame">The full frame (start byte, length, command, payload).</param>
    /// <param name="reportSize">Maximum chunk size. Must be positive.</param>
    public static IReadOnlyList<ReadOnlyMemory<byte>> ChunkFrame(
        ReadOnlyMemory<byte> frame, int reportSize = OutputReportSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(reportSize);

        var chunks = new List<ReadOnlyMemory<byte>>();
        for (int offset = 0; offset < frame.Length; offset += reportSize)
        {
            int len = Math.Min(reportSize, frame.Length - offset);
            chunks.Add(frame.Slice(offset, len));
        }
        return chunks;
    }

    /// <summary>
    /// Classifies the bootloader's reply byte. Any value the bootloader does not
    /// define (including a timeout sentinel) maps to <see cref="Efm8ReplyCode.Unknown"/>.
    /// </summary>
    public static Efm8ReplyCode ClassifyReply(byte reply) => reply switch
    {
        AckByte => Efm8ReplyCode.Acknowledge,
        0x41 => Efm8ReplyCode.RangeError,
        0x42 => Efm8ReplyCode.CrcError,
        0x43 => Efm8ReplyCode.OtherError,
        _ => Efm8ReplyCode.Unknown,
    };
}
