// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Periphery.Firmware;

/// <summary>
/// An immutable, sparse address-to-byte image parsed from Intel HEX text — the
/// pure-core counterpart of the Python <c>intelhex.IntelHex</c> that
/// <c>hex2boot</c> consumes. No IO: <see cref="Parse"/> takes the HEX text, and
/// every accessor is a total function over the parsed bytes (ADR-0052 functional
/// core).
/// </summary>
/// <remarks>
/// Unpopulated addresses read as <see cref="Padding"/> (0xFF, matching the EFM8
/// erased-flash value and <c>intelhex</c>'s default), so <see cref="ToBinary"/>
/// over a range that straddles a gap fills the gap with 0xFF — exactly what a
/// flash write of that range must contain.
/// </remarks>
public sealed class IntelHexImage
{
    private readonly Dictionary<int, byte> _bytes;

    /// <summary>Fill value for unpopulated addresses (0xFF = erased flash).</summary>
    public byte Padding { get; }

    /// <summary><see langword="true"/> when no byte is populated.</summary>
    public bool IsEmpty => _bytes.Count == 0;

    /// <summary>Lowest populated address. Throws when <see cref="IsEmpty"/>.</summary>
    public int MinAddress { get; }

    /// <summary>Highest populated address. Throws when <see cref="IsEmpty"/>.</summary>
    public int MaxAddress { get; }

    private IntelHexImage(Dictionary<int, byte> bytes, byte padding)
    {
        _bytes = bytes;
        Padding = padding;
        if (bytes.Count == 0)
        {
            MinAddress = 0;
            MaxAddress = -1;
            return;
        }
        int min = int.MaxValue, max = int.MinValue;
        foreach (int a in bytes.Keys)
        {
            if (a < min) min = a;
            if (a > max) max = a;
        }
        MinAddress = min;
        MaxAddress = max;
    }

    /// <summary>The populated addresses, ascending (diagnostic / tests).</summary>
    public IEnumerable<int> Addresses => _bytes.Keys.OrderBy(a => a);

    /// <summary>
    /// The byte at <paramref name="address"/>, or <see cref="Padding"/> when that
    /// address is not populated.
    /// </summary>
    public byte Get(int address) => _bytes.TryGetValue(address, out byte v) ? v : Padding;

    /// <summary>
    /// The <paramref name="size"/> bytes at <c>[start, start+size)</c>, padding
    /// unpopulated addresses with <see cref="Padding"/> (the <c>intelhex.tobinstr</c>
    /// equivalent).
    /// </summary>
    public byte[] ToBinary(int start, int size)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(size);
        var buf = new byte[size];
        for (int i = 0; i < size; i++)
            buf[i] = Get(start + i);
        return buf;
    }

    /// <summary>
    /// A new image holding only the addresses in <c>[startInclusive, stopExclusive)</c>
    /// — the <c>intelhex</c> <c>ih[slice(start, stop)]</c> semantics (stop is
    /// exclusive). Used to split an image across the flash regions.
    /// </summary>
    public IntelHexImage Slice(int startInclusive, int stopExclusive)
    {
        var sub = new Dictionary<int, byte>();
        foreach (var kv in _bytes)
            if (kv.Key >= startInclusive && kv.Key < stopExclusive)
                sub[kv.Key] = kv.Value;
        return new IntelHexImage(sub, Padding);
    }

    /// <summary>
    /// A copy with one address overridden (the EFM8 generator's failsafe reset-vector blank).
    /// Public so callers in other assemblies (Periphery.Bootloader.Efm8.Usb) can use it now that
    /// this type lives in Periphery.Firmware.
    /// </summary>
    public IntelHexImage With(int address, byte value)
    {
        var copy = new Dictionary<int, byte>(_bytes) { [address] = value };
        return new IntelHexImage(copy, Padding);
    }

    /// <summary>
    /// Builds an image directly from a sequence of (address, value) pairs — e.g. reconstructing the
    /// final memory layout an already-built boot-record blob's own Write/Erase records describe, for
    /// an independent verify built without the original source Intel HEX at hand. A later entry for
    /// an address already seen overrides the earlier one (last write wins), matching how flashing
    /// itself applies writes in stream order — this is the one bulk-construction path that does not
    /// pay <see cref="With"/>'s per-call full-dictionary-copy cost for every individual byte.
    /// </summary>
    public static IntelHexImage FromBytes(IEnumerable<(int Address, byte Value)> bytes, byte padding = 0xFF)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var dict = new Dictionary<int, byte>();
        foreach (var (address, value) in bytes)
            dict[address] = value;
        return new IntelHexImage(dict, padding);
    }

    /// <summary>
    /// Parses Intel HEX text into an image. Supports data (0x00), end-of-file
    /// (0x01), extended-segment-address (0x02), and extended-linear-address (0x04)
    /// records; start-address records (0x03 / 0x05) are accepted and ignored. Every
    /// record's checksum is verified.
    /// </summary>
    /// <exception cref="IntelHexFormatException">The HEX is malformed (see the type doc).</exception>
    public static IntelHexImage Parse(string hexText, byte padding = 0xFF)
    {
        ArgumentNullException.ThrowIfNull(hexText);

        var bytes = new Dictionary<int, byte>();
        int baseAddress = 0;
        bool sawEof = false;
        int lineNo = 0;

        foreach (var rawLine in hexText.Split('\n'))
        {
            lineNo++;
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            if (sawEof)
                throw new IntelHexFormatException(
                    $"Line {lineNo}: content after the end-of-file (0x01) record.");
            if (line[0] != ':')
                throw new IntelHexFormatException(
                    $"Line {lineNo}: record does not start with ':' (found '{line[0]}').");

            byte[] rec = DecodeHex(line.AsSpan(1), lineNo);
            // count(1) + address(2) + type(1) + checksum(1) = 5 bytes minimum.
            if (rec.Length < 5)
                throw new IntelHexFormatException(
                    $"Line {lineNo}: record is too short ({rec.Length} bytes; need at least 5).");

            int count = rec[0];
            int address = (rec[1] << 8) | rec[2];
            int type = rec[3];
            if (rec.Length != 5 + count)
                throw new IntelHexFormatException(
                    $"Line {lineNo}: byte count {count} disagrees with the record length " +
                    $"(expected {5 + count} bytes, got {rec.Length}).");

            byte sum = 0;
            foreach (byte b in rec) sum += b;
            if (sum != 0)
                throw new IntelHexFormatException(
                    $"Line {lineNo}: checksum mismatch (record does not sum to zero).");

            switch (type)
            {
                case 0x00: // data
                    for (int i = 0; i < count; i++)
                        bytes[baseAddress + address + i] = rec[4 + i];
                    break;
                case 0x01: // end of file
                    sawEof = true;
                    break;
                case 0x04: // extended linear address (upper 16 bits)
                    baseAddress = ((rec[4] << 8) | rec[5]) << 16;
                    break;
                case 0x02: // extended segment address (<< 4)
                    baseAddress = ((rec[4] << 8) | rec[5]) << 4;
                    break;
                case 0x03: // start segment address — execution hint, no image bytes
                case 0x05: // start linear address
                    break;
                default:
                    throw new IntelHexFormatException(
                        $"Line {lineNo}: unsupported record type 0x{type:X2}.");
            }
        }

        if (!sawEof)
            throw new IntelHexFormatException(
                "Intel HEX has no end-of-file (0x01) record — the input may be truncated.");

        return new IntelHexImage(bytes, padding);
    }

    private static byte[] DecodeHex(ReadOnlySpan<char> hex, int lineNo)
    {
        if ((hex.Length & 1) != 0)
            throw new IntelHexFormatException(
                $"Line {lineNo}: record has an odd number of hex digits ({hex.Length}).");

        var bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            int hi = HexDigit(hex[i * 2], lineNo);
            int lo = HexDigit(hex[i * 2 + 1], lineNo);
            bytes[i] = (byte)((hi << 4) | lo);
        }
        return bytes;
    }

    private static int HexDigit(char c, int lineNo) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'A' and <= 'F' => c - 'A' + 10,
        >= 'a' and <= 'f' => c - 'a' + 10,
        _ => throw new IntelHexFormatException(
            $"Line {lineNo}: '{c}' is not a hex digit."),
    };
}
