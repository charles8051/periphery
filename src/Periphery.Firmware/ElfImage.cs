// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace Periphery.Firmware;

/// <summary>
/// Reads the loadable contents of an ELF file (<c>.elf</c> / <c>.axf</c> / <c>.out</c>) — the
/// native output of every GCC/Clang embedded build — into the addressed segments a flasher
/// writes. ELF is a <em>memory image</em> (ADR image-formats Decision 1): it decomposes to
/// <c>address -&gt; bytes</c> exactly like Intel HEX or a raw binary, so it feeds the shared
/// <see cref="FirmwareImage"/> via <see cref="FirmwareImage.FromElf"/>.
/// </summary>
/// <remarks>
/// <para>
/// Pure core (ADR-0052): a total function over in-memory bytes, no IO, BCL-only (AOT-clean).
/// </para>
/// <para>
/// What is loaded, and why — this matches <c>objcopy -O binary</c> and how OpenOCD / pyOCD
/// flash an ELF:
/// </para>
/// <list type="bullet">
///   <item>Only <c>PT_LOAD</c> program-header segments are emitted; non-load segments
///   (dynamic, note, GNU stack, …) and section-header metadata (symbols, debug info) are
///   ignored — they are never written to flash.</item>
///   <item>The segment's load address is <c>p_paddr</c> (the LMA — where the bytes physically
///   live in flash), not <c>p_vaddr</c> (the runtime address). For initialized <c>.data</c>
///   the two differ: the bytes are stored in flash at the LMA and copied to RAM at startup.</item>
///   <item>Only <c>p_filesz</c> bytes are taken. A <c>PT_LOAD</c> whose <c>p_memsz &gt;
///   p_filesz</c> has a zero-initialized (<c>.bss</c>) tail that is <em>not</em> stored on disk
///   and must not be flashed; a segment with <c>p_filesz == 0</c> (pure <c>.bss</c>) is skipped
///   entirely.</item>
/// </list>
/// <para>
/// Both ELF classes (32- and 64-bit) and both data encodings (little- and big-endian) are
/// supported. Load addresses must fit the 32-bit space Periphery flashes; a 64-bit ELF whose
/// <c>p_paddr</c> exceeds that is refused rather than silently truncated.
/// </para>
/// </remarks>
public static class ElfImage
{
    // e_ident magic: 0x7F 'E' 'L' 'F'.
    private const byte Magic0 = 0x7F, Magic1 = (byte)'E', Magic2 = (byte)'L', Magic3 = (byte)'F';
    private const uint PtLoad = 1; // program-header type for a loadable segment.

    /// <summary><see langword="true"/> when <paramref name="content"/> begins with the ELF magic.</summary>
    public static bool HasMagic(ReadOnlySpan<byte> content) =>
        content.Length >= 4 &&
        content[0] == Magic0 && content[1] == Magic1 && content[2] == Magic2 && content[3] == Magic3;

    /// <summary>
    /// Parses the <c>PT_LOAD</c> program headers of an ELF file into firmware segments at their
    /// load addresses (<c>p_paddr</c>), ascending. See the type remarks for exactly what is and
    /// is not loaded.
    /// </summary>
    /// <exception cref="ElfFormatException">
    /// The bytes are not a well-formed, loadable ELF (see <see cref="ElfFormatException"/>).
    /// </exception>
    public static ImmutableArray<FirmwareSegment> ReadLoadableSegments(ReadOnlyMemory<byte> content)
    {
        var span = content.Span;
        if (!HasMagic(span))
            throw new ElfFormatException("Not an ELF file: missing the 0x7F 'E' 'L' 'F' magic.");

        bool is64 = span[4] switch
        {
            1 => false, // ELFCLASS32
            2 => true,  // ELFCLASS64
            var c => throw new ElfFormatException(
                $"Unsupported ELF class byte 0x{c:X2} (expected 1 = 32-bit or 2 = 64-bit)."),
        };
        bool le = span[5] switch
        {
            1 => true,  // ELFDATA2LSB (little-endian)
            2 => false, // ELFDATA2MSB (big-endian)
            var d => throw new ElfFormatException(
                $"Unsupported ELF data-encoding byte 0x{d:X2} (expected 1 = little-endian or 2 = big-endian)."),
        };

        // Program-header table location. Field offsets diverge between the two classes.
        ulong phoff;
        int phentsize, phnum;
        if (!is64)
        {
            // Elf32_Ehdr: e_phoff@28 (u32), e_phentsize@42 (u16), e_phnum@44 (u16); header is 52 bytes.
            if (span.Length < 52)
                throw new ElfFormatException($"Truncated ELF32 header ({span.Length} bytes; need at least 52).");
            phoff = ReadU32(span.Slice(28, 4), le);
            phentsize = ReadU16(span.Slice(42, 2), le);
            phnum = ReadU16(span.Slice(44, 2), le);
        }
        else
        {
            // Elf64_Ehdr: e_phoff@32 (u64), e_phentsize@54 (u16), e_phnum@56 (u16); header is 64 bytes.
            if (span.Length < 64)
                throw new ElfFormatException($"Truncated ELF64 header ({span.Length} bytes; need at least 64).");
            phoff = ReadU64(span.Slice(32, 8), le);
            phentsize = ReadU16(span.Slice(54, 2), le);
            phnum = ReadU16(span.Slice(56, 2), le);
        }

        int minEnt = is64 ? 56 : 32; // sizeof(Elf{64,32}_Phdr)
        if (phnum == 0)
            throw new ElfFormatException(
                "ELF has no program headers — it is not a loadable executable (a relocatable .o or a debug-only file?).");
        if (phentsize < minEnt)
            throw new ElfFormatException(
                $"ELF program-header entry size {phentsize} is below the {minEnt}-byte minimum for this class.");

        ulong tableEnd = phoff + (ulong)phnum * (ulong)phentsize;
        if (phoff > (ulong)span.Length || tableEnd > (ulong)span.Length)
            throw new ElfFormatException("ELF program-header table runs past the end of the file (truncated).");

        var segments = new List<FirmwareSegment>(phnum);
        for (int i = 0; i < phnum; i++)
        {
            var ph = span.Slice((int)(phoff + (ulong)i * (ulong)phentsize), phentsize);
            if (ReadU32(ph.Slice(0, 4), le) != PtLoad)
                continue; // not a loadable segment — nothing to flash.

            ulong pOffset, pPaddr, pFilesz;
            if (!is64)
            {
                // Elf32_Phdr: p_type@0, p_offset@4, p_vaddr@8, p_paddr@12, p_filesz@16, p_memsz@20.
                pOffset = ReadU32(ph.Slice(4, 4), le);
                pPaddr = ReadU32(ph.Slice(12, 4), le);
                pFilesz = ReadU32(ph.Slice(16, 4), le);
            }
            else
            {
                // Elf64_Phdr: p_type@0, p_flags@4, p_offset@8, p_vaddr@16, p_paddr@24, p_filesz@32, p_memsz@40.
                pOffset = ReadU64(ph.Slice(8, 8), le);
                pPaddr = ReadU64(ph.Slice(24, 8), le);
                pFilesz = ReadU64(ph.Slice(32, 8), le);
            }

            if (pFilesz == 0)
                continue; // pure .bss (p_memsz > 0, p_filesz == 0) — zero-filled at runtime, not on disk.

            if (pOffset + pFilesz > (ulong)span.Length)
                throw new ElfFormatException(
                    $"ELF PT_LOAD segment {i} data [0x{pOffset:X}, 0x{pOffset + pFilesz:X}) runs past the end of the file.");
            if (pPaddr > uint.MaxValue || pPaddr + pFilesz > (ulong)uint.MaxValue + 1)
                throw new ElfFormatException(
                    $"ELF PT_LOAD segment {i} load address 0x{pPaddr:X} (+0x{pFilesz:X}) is outside the 32-bit " +
                    "address space Periphery flashes.");

            // Slice a view over the source buffer (zero-copy, matching FromBytes); the buffer is not mutated.
            segments.Add(new FirmwareSegment((uint)pPaddr, content.Slice((int)pOffset, (int)pFilesz)));
        }

        if (segments.Count == 0)
            throw new ElfFormatException(
                "ELF has no PT_LOAD segment with file data — there is nothing to flash " +
                "(a debug-only or .bss-only image?).");

        segments.Sort(static (a, b) => a.Address.CompareTo(b.Address));
        return segments.ToImmutableArray();
    }

    private static ushort ReadU16(ReadOnlySpan<byte> s, bool le) =>
        le ? BinaryPrimitives.ReadUInt16LittleEndian(s) : BinaryPrimitives.ReadUInt16BigEndian(s);

    private static uint ReadU32(ReadOnlySpan<byte> s, bool le) =>
        le ? BinaryPrimitives.ReadUInt32LittleEndian(s) : BinaryPrimitives.ReadUInt32BigEndian(s);

    private static ulong ReadU64(ReadOnlySpan<byte> s, bool le) =>
        le ? BinaryPrimitives.ReadUInt64LittleEndian(s) : BinaryPrimitives.ReadUInt64BigEndian(s);
}
