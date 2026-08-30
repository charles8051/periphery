// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;

namespace Periphery.Firmware;

/// <summary>
/// An immutable firmware image: one or more byte segments at absolute addresses — the
/// value a bootloader client writes to a device. Build it from a raw binary
/// (<see cref="FromBytes"/>), Intel HEX text (<see cref="FromIntelHex(string)"/>), an ELF
/// (<see cref="FromElf"/>), explicit segments (<see cref="FromSegments"/>), or by
/// auto-detecting the format from a file name (<see cref="Load"/>).
/// </summary>
/// <remarks>
/// Pure value (ADR-0052): parsing is total and addresses are absolute, so the planner that
/// turns an image into device writes never has to guess where bytes go. A raw binary carries
/// no addresses, so its base must be supplied; Intel HEX and ELF carry their own. DfuSe
/// (<c>.dfu</c>) is not parsed yet (ADR-0061 phase 0+).
/// </remarks>
public sealed class FirmwareImage
{
    private FirmwareImage(ImmutableArray<FirmwareSegment> segments)
    {
        Segments = segments;
        TotalBytes = segments.Sum(s => (long)s.Data.Length);
    }

    /// <summary>The image's byte segments, each at an absolute address.</summary>
    public ImmutableArray<FirmwareSegment> Segments { get; }

    /// <summary>Total payload bytes across all segments.</summary>
    public long TotalBytes { get; }

    /// <summary>An image with no data.</summary>
    public static readonly FirmwareImage Empty = new(ImmutableArray<FirmwareSegment>.Empty);

    /// <summary>A single-segment image of raw bytes at <paramref name="baseAddress"/>.</summary>
    public static FirmwareImage FromBytes(uint baseAddress, ReadOnlyMemory<byte> data) =>
        new(ImmutableArray.Create(new FirmwareSegment(baseAddress, data)));

    /// <summary>An image of the given segments, each at its own absolute address.</summary>
    public static FirmwareImage FromSegments(IEnumerable<FirmwareSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        return new(segments.ToImmutableArray());
    }

    /// <summary>
    /// Parses Intel HEX text and coalesces its sparse address map into contiguous segments —
    /// each run of consecutive populated addresses becomes one <see cref="FirmwareSegment"/>
    /// at its real address, so a multi-region .hex flashes each region where it belongs.
    /// </summary>
    /// <exception cref="IntelHexFormatException">The HEX is malformed.</exception>
    public static FirmwareImage FromIntelHex(string hexText) => FromIntelHex(IntelHexImage.Parse(hexText));

    /// <summary>Coalesces an already-parsed sparse <see cref="IntelHexImage"/> into contiguous segments.</summary>
    public static FirmwareImage FromIntelHex(IntelHexImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.IsEmpty) return Empty;

        var segments = ImmutableArray.CreateBuilder<FirmwareSegment>();
        int runStart = 0, prev = 0;
        var run = new List<byte>();
        bool inRun = false;

        foreach (int addr in image.Addresses) // ascending
        {
            if (!inRun)
            {
                runStart = addr;
                inRun = true;
            }
            else if (addr != prev + 1)
            {
                segments.Add(new FirmwareSegment((uint)runStart, run.ToArray()));
                run = new List<byte>();
                runStart = addr;
            }
            run.Add(image.Get(addr));
            prev = addr;
        }
        segments.Add(new FirmwareSegment((uint)runStart, run.ToArray()));
        return new(segments.ToImmutable());
    }

    /// <summary>
    /// Parses an ELF file's loadable (<c>PT_LOAD</c>) program headers into segments at their
    /// physical load addresses — the embedded toolchain's native output, treated exactly like a
    /// HEX or raw binary once parsed (it never reaches flash as ELF). See <see cref="ElfImage"/>
    /// for precisely what is and is not loaded.
    /// </summary>
    /// <exception cref="ElfFormatException">The ELF is malformed or carries nothing to flash.</exception>
    public static FirmwareImage FromElf(ReadOnlyMemory<byte> content) =>
        new(CoalesceAdjacent(ElfImage.ReadLoadableSegments(content)));

    /// <summary>
    /// Merges segments that are <em>exactly adjacent</em> in the address space into one contiguous
    /// segment (input must be sorted ascending, as <see cref="ElfImage.ReadLoadableSegments"/>
    /// returns). ELF routinely splits <c>.text</c> and <c>.data</c> into separate <c>PT_LOAD</c>
    /// segments at consecutive load addresses; flashing the second at its own load address fails on
    /// targets that require aligned writes — e.g. an STM32 whose <c>.data</c> LMA is not 8-byte
    /// aligned (flash is programmed in 64-bit doublewords). Coalescing flashes the image as a single
    /// run from the aligned base, matching <c>objcopy -O binary</c>. Non-adjacent regions (a real
    /// gap) stay separate.
    /// </summary>
    private static ImmutableArray<FirmwareSegment> CoalesceAdjacent(ImmutableArray<FirmwareSegment> segments)
    {
        if (segments.Length <= 1)
            return segments;

        var result = ImmutableArray.CreateBuilder<FirmwareSegment>();
        uint runStart = segments[0].Address;
        var run = new List<byte>(segments[0].Data.ToArray());

        for (int i = 1; i < segments.Length; i++)
        {
            var seg = segments[i];
            if (seg.Address == runStart + (uint)run.Count)
            {
                run.AddRange(seg.Data.ToArray()); // exactly adjacent — extend the contiguous run
            }
            else
            {
                result.Add(new FirmwareSegment(runStart, run.ToArray()));
                runStart = seg.Address;
                run = new List<byte>(seg.Data.ToArray());
            }
        }
        result.Add(new FirmwareSegment(runStart, run.ToArray()));
        return result.ToImmutable();
    }

    /// <summary>
    /// Loads a firmware image, inferring the format from <paramref name="fileName"/>'s
    /// extension and verifying the content matches (a brick-guard): <c>.hex</c>/<c>.ihex</c>
    /// is parsed as Intel HEX; <c>.elf</c>/<c>.axf</c>/<c>.out</c> is parsed as ELF (its
    /// <c>PT_LOAD</c> segments, at their own addresses); <c>.bin</c> is taken as a raw binary
    /// placed at <paramref name="binBaseAddress"/> — but refused if its content looks like
    /// Intel HEX or ELF (a mislabelled file, which flashed verbatim would brick the device).
    /// <c>.dfu</c> (DfuSe) is not implemented yet; any other extension is rejected.
    /// </summary>
    /// <exception cref="FirmwareFormatException">Unknown extension, or a content/extension mismatch (e.g. a .bin whose content is Intel HEX or ELF, or a .elf whose content is not ELF).</exception>
    /// <exception cref="IntelHexFormatException">A .hex whose content is malformed.</exception>
    /// <exception cref="ElfFormatException">A .elf whose content is a malformed or non-loadable ELF.</exception>
    /// <exception cref="NotSupportedException">A .dfu file (DfuSe parsing is not implemented).</exception>
    public static FirmwareImage Load(ReadOnlyMemory<byte> content, string fileName, uint binBaseAddress)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".hex" or ".ihex" => FromIntelHex(Encoding.ASCII.GetString(content.Span)),
            ".elf" or ".axf" or ".out" => FromElfChecked(content, fileName),
            ".bin" => FromRawBinary(content, binBaseAddress, fileName),
            ".dfu" => throw new NotSupportedException(
                "DfuSe '.dfu' parsing is not implemented yet (ADR-0061 phase 0+). " +
                "Flash a raw .bin (with an explicit base), an Intel .hex, or an .elf instead."),
            "" => throw new FirmwareFormatException(
                $"Cannot determine the firmware format of '{fileName}': it has no extension. " +
                "Use .bin (raw binary), .hex (Intel HEX), .elf (ELF), or .dfu."),
            var ext => throw new FirmwareFormatException(
                $"Unsupported firmware format '{ext}' for '{fileName}'. Supported: .bin (raw binary), " +
                ".hex (Intel HEX), .elf (ELF). (.dfu / DfuSe is not implemented yet.)"),
        };
    }

    private static FirmwareImage FromElfChecked(ReadOnlyMemory<byte> content, string fileName)
    {
        // Brick-guard: a non-ELF file wearing a .elf/.axf/.out extension. Refuse it here with a
        // clear extension/content-mismatch message before the parser even looks at the header.
        if (!ElfImage.HasMagic(content.Span))
            throw new FirmwareFormatException(
                $"'{fileName}' has an ELF extension but its content is not an ELF file " +
                "(missing the 0x7F 'E' 'L' 'F' magic). Flashing it as-is could brick the device.");
        return FromElf(content);
    }

    private static FirmwareImage FromRawBinary(ReadOnlyMemory<byte> content, uint baseAddress, string fileName)
    {
        // Brick-guard: a memory-image file (Intel HEX text, or a binary ELF with its headers)
        // renamed to .bin would be streamed to flash verbatim. Refuse it before a byte moves.
        var span = content.Span;
        if (ElfImage.HasMagic(span))
            throw new FirmwareFormatException(
                $"'{fileName}' has a .bin extension but its content is an ELF file " +
                "(0x7F 'E' 'L' 'F' magic). Rename it to .elf, or export a raw binary " +
                "(e.g. objcopy -O binary) — flashing the ELF headers as-is would brick the device.");

        int i = 0;
        while (i < span.Length && span[i] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            i++;
        if (i < span.Length && span[i] == (byte)':')
            throw new FirmwareFormatException(
                $"'{fileName}' has a .bin extension but its content looks like Intel HEX text " +
                "(it starts with ':'). Rename it to .hex or re-export a raw binary — flashing the " +
                "text as-is would brick the device.");
        return FromBytes(baseAddress, content);
    }
}

/// <summary>A contiguous run of firmware bytes at an absolute address.</summary>
public readonly record struct FirmwareSegment(uint Address, ReadOnlyMemory<byte> Data);
