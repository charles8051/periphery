using System;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Linq;

namespace Periphery.Firmware.Tests;

/// <summary>
/// The ELF program-header reader: the pure core that turns a GCC/Clang ELF into the addressed
/// segments a flasher writes. Tests cover both classes (32/64-bit), both endiannesses, the
/// PT_LOAD-only / p_paddr / p_filesz semantics, and the brick-guarding rejections.
/// </summary>
public class ElfImageTests
{
    // ---- a tiny ELF builder (so tests assert against bytes we control) ----------------------

    private sealed record Ph(uint Type, ulong Paddr, byte[] Data, ulong MemSz, ulong Vaddr);

    private const uint PtLoad = 1, PtNote = 4;

    private static Ph Load(ulong paddr, byte[] data, ulong? memSz = null, ulong? vaddr = null) =>
        new(PtLoad, paddr, data, memSz ?? (ulong)data.Length, vaddr ?? paddr);

    private static byte[] BuildElf(bool is64, bool littleEndian, params Ph[] phs)
    {
        int hdrSize = is64 ? 64 : 52;
        int entSize = is64 ? 56 : 32;
        int phoff = hdrSize;
        int dataStart = hdrSize + phs.Length * entSize;

        var offsets = new int[phs.Length];
        int cursor = dataStart;
        for (int i = 0; i < phs.Length; i++)
        {
            offsets[i] = cursor;
            cursor += phs[i].Data.Length;
        }

        var buf = new byte[cursor];
        void W16(int o, ushort v)
        {
            if (littleEndian) BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(o, 2), v);
            else BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(o, 2), v);
        }
        void W32(int o, uint v)
        {
            if (littleEndian) BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(o, 4), v);
            else BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(o, 4), v);
        }
        void W64(int o, ulong v)
        {
            if (littleEndian) BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(o, 8), v);
            else BinaryPrimitives.WriteUInt64BigEndian(buf.AsSpan(o, 8), v);
        }

        buf[0] = 0x7F; buf[1] = (byte)'E'; buf[2] = (byte)'L'; buf[3] = (byte)'F';
        buf[4] = (byte)(is64 ? 2 : 1);          // EI_CLASS
        buf[5] = (byte)(littleEndian ? 1 : 2);  // EI_DATA
        buf[6] = 1;                             // EI_VERSION

        W16(16, 2);     // e_type = ET_EXEC
        W16(18, 0x28);  // e_machine = ARM (arbitrary; unused by the parser)
        W32(20, 1);     // e_version

        if (!is64)
        {
            W32(28, (uint)phoff);          // e_phoff
            W16(40, 52);                   // e_ehsize
            W16(42, 32);                   // e_phentsize
            W16(44, (ushort)phs.Length);   // e_phnum
        }
        else
        {
            W64(32, (ulong)phoff);         // e_phoff
            W16(52, 64);                   // e_ehsize
            W16(54, 56);                   // e_phentsize
            W16(56, (ushort)phs.Length);   // e_phnum
        }

        for (int i = 0; i < phs.Length; i++)
        {
            int p = phoff + i * entSize;
            var ph = phs[i];
            if (!is64)
            {
                W32(p + 0, ph.Type);
                W32(p + 4, (uint)offsets[i]);        // p_offset
                W32(p + 8, (uint)ph.Vaddr);          // p_vaddr
                W32(p + 12, (uint)ph.Paddr);         // p_paddr
                W32(p + 16, (uint)ph.Data.Length);   // p_filesz
                W32(p + 20, (uint)ph.MemSz);         // p_memsz
                W32(p + 24, 5);                      // p_flags = R+X
                W32(p + 28, 4);                      // p_align
            }
            else
            {
                W32(p + 0, ph.Type);
                W32(p + 4, 5);                       // p_flags = R+X
                W64(p + 8, (ulong)offsets[i]);       // p_offset
                W64(p + 16, ph.Vaddr);               // p_vaddr
                W64(p + 24, ph.Paddr);               // p_paddr
                W64(p + 32, (ulong)ph.Data.Length);  // p_filesz
                W64(p + 40, ph.MemSz);               // p_memsz
                W64(p + 48, 8);                      // p_align
            }
            ph.Data.CopyTo(buf, offsets[i]);
        }
        return buf;
    }

    private static byte[] Bytes(params byte[] b) => b;

    // ---- FromElf coalescing (adjacent PT_LOAD -> one base-aligned run) ------------------------

    [Fact]
    public void FromElf_coalesces_adjacent_load_segments_into_one_run()
    {
        // The .text / .data split GCC emits: two PT_LOAD segments at exactly contiguous load
        // addresses (.data's LMA == .text's end). FromElf must merge them so the image flashes as
        // one base-aligned run, not a separate (possibly unaligned) write per segment — flashing
        // .data at its own unaligned LMA fails on an STM32 (8-byte doubleword programming).
        var text = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray(); // 0x08000000, 16 bytes
        var data = Bytes(0xAA, 0xBB, 0xCC, 0xDD);                          // 0x08000010, contiguous
        var elf = BuildElf(is64: false, littleEndian: true,
            Load(0x08000000, text),
            Load(0x08000010, data, vaddr: 0x20000000));                    // .data LMA contiguous, VMA in RAM

        var image = FirmwareImage.FromElf(elf);

        var seg = Assert.Single(image.Segments);
        Assert.Equal(0x08000000u, seg.Address);
        Assert.Equal(text.Concat(data).ToArray(), seg.Data.ToArray());
    }

    [Fact]
    public void FromElf_keeps_non_adjacent_segments_separate()
    {
        var elf = BuildElf(is64: false, littleEndian: true,
            Load(0x08000000, Bytes(1, 2, 3, 4)),
            Load(0x08010000, Bytes(5, 6, 7, 8)));   // a real gap -> two regions

        var image = FirmwareImage.FromElf(elf);

        Assert.Equal(2, image.Segments.Length);
        Assert.Equal(0x08000000u, image.Segments[0].Address);
        Assert.Equal(0x08010000u, image.Segments[1].Address);
    }

    // ---- happy paths -------------------------------------------------------------------------

    [Theory]
    [InlineData(false, true)]   // ELF32 little-endian (the common embedded case)
    [InlineData(true, true)]    // ELF64 little-endian
    [InlineData(false, false)]  // ELF32 big-endian
    [InlineData(true, false)]   // ELF64 big-endian
    public void Reads_a_single_PT_LOAD_segment_at_its_paddr(bool is64, bool le)
    {
        var data = Bytes(0xDE, 0xAD, 0xBE, 0xEF);
        var elf = BuildElf(is64, le, Load(0x08000000, data));

        var seg = Assert.Single(ElfImage.ReadLoadableSegments(elf));
        Assert.Equal(0x08000000u, seg.Address);
        Assert.Equal(data, seg.Data.ToArray());
    }

    [Fact]
    public void Uses_the_physical_load_address_not_the_virtual_address()
    {
        // Initialized .data: stored in flash at the LMA (paddr), runs from RAM (vaddr). Flash the LMA.
        var elf = BuildElf(is64: false, littleEndian: true,
            Load(paddr: 0x08010000, data: Bytes(1, 2, 3, 4), vaddr: 0x20000000));

        var seg = Assert.Single(ElfImage.ReadLoadableSegments(elf));
        Assert.Equal(0x08010000u, seg.Address);
    }

    [Fact]
    public void Skips_non_PT_LOAD_program_headers()
    {
        var elf = BuildElf(is64: false, littleEndian: true,
            new Ph(PtNote, 0x0, Bytes(0xAA, 0xBB), 2, 0x0),
            Load(0x08000000, Bytes(0x11, 0x22)));

        var seg = Assert.Single(ElfImage.ReadLoadableSegments(elf));
        Assert.Equal(0x08000000u, seg.Address);
        Assert.Equal(Bytes(0x11, 0x22), seg.Data.ToArray());
    }

    [Fact]
    public void Takes_only_filesz_bytes_ignoring_the_bss_tail()
    {
        // p_memsz (8) > p_filesz (4): the 4-byte zero-init tail lives only in RAM, never on disk.
        var elf = BuildElf(is64: false, littleEndian: true,
            Load(0x08000000, Bytes(1, 2, 3, 4), memSz: 8));

        var seg = Assert.Single(ElfImage.ReadLoadableSegments(elf));
        Assert.Equal(4, seg.Data.Length);
        Assert.Equal(Bytes(1, 2, 3, 4), seg.Data.ToArray());
    }

    [Fact]
    public void Skips_a_pure_bss_PT_LOAD_with_no_file_data()
    {
        var elf = BuildElf(is64: false, littleEndian: true,
            Load(0x20000000, Array.Empty<byte>(), memSz: 64), // pure .bss in RAM
            Load(0x08000000, Bytes(0xCA, 0xFE)));             // the real flash segment

        var seg = Assert.Single(ElfImage.ReadLoadableSegments(elf));
        Assert.Equal(0x08000000u, seg.Address);
    }

    [Fact]
    public void Returns_multiple_segments_sorted_by_ascending_address()
    {
        var elf = BuildElf(is64: false, littleEndian: true,
            Load(0x08020000, Bytes(0x33, 0x44)),  // declared out of order
            Load(0x08000000, Bytes(0x11, 0x22)));

        ImmutableArray<FirmwareSegment> segs = ElfImage.ReadLoadableSegments(elf);
        Assert.Equal(2, segs.Length);
        Assert.Equal(0x08000000u, segs[0].Address);
        Assert.Equal(0x08020000u, segs[1].Address);
    }

    // ---- rejections (brick-guarding) ---------------------------------------------------------

    [Fact]
    public void Rejects_a_file_without_the_ELF_magic()
    {
        var ex = Assert.Throws<ElfFormatException>(() => ElfImage.ReadLoadableSegments(Bytes(1, 2, 3, 4)));
        Assert.Contains("ELF", ex.Message);
    }

    [Fact]
    public void Rejects_an_unsupported_class_byte()
    {
        var elf = BuildElf(is64: false, littleEndian: true, Load(0x0, Bytes(1, 2)));
        elf[4] = 9; // neither 1 (32-bit) nor 2 (64-bit)
        Assert.Throws<ElfFormatException>(() => ElfImage.ReadLoadableSegments(elf));
    }

    [Fact]
    public void Rejects_an_unsupported_data_encoding_byte()
    {
        var elf = BuildElf(is64: false, littleEndian: true, Load(0x0, Bytes(1, 2)));
        elf[5] = 9; // neither 1 (LE) nor 2 (BE)
        Assert.Throws<ElfFormatException>(() => ElfImage.ReadLoadableSegments(elf));
    }

    [Fact]
    public void Rejects_an_ELF_with_no_program_headers()
    {
        var elf = BuildElf(is64: false, littleEndian: true); // e_phnum = 0
        var ex = Assert.Throws<ElfFormatException>(() => ElfImage.ReadLoadableSegments(elf));
        Assert.Contains("no program headers", ex.Message);
    }

    [Fact]
    public void Rejects_an_ELF_with_no_loadable_segment()
    {
        var elf = BuildElf(is64: false, littleEndian: true,
            new Ph(PtNote, 0x0, Bytes(0xAA, 0xBB), 2, 0x0)); // only a non-load header
        var ex = Assert.Throws<ElfFormatException>(() => ElfImage.ReadLoadableSegments(elf));
        Assert.Contains("PT_LOAD", ex.Message);
    }

    [Fact]
    public void Rejects_a_truncated_program_header_table()
    {
        var elf = BuildElf(is64: false, littleEndian: true, Load(0x08000000, Bytes(1, 2, 3, 4)));
        var cut = elf.AsSpan(0, 52 + 16).ToArray(); // header + a partial 32-byte program header
        Assert.Throws<ElfFormatException>(() => ElfImage.ReadLoadableSegments(cut));
    }

    [Fact]
    public void Rejects_a_segment_whose_data_runs_past_the_end_of_the_file()
    {
        var elf = BuildElf(is64: false, littleEndian: true, Load(0x08000000, Bytes(1, 2, 3, 4)));
        var cut = elf.AsSpan(0, elf.Length - 2).ToArray(); // drop 2 of the 4 payload bytes
        Assert.Throws<ElfFormatException>(() => ElfImage.ReadLoadableSegments(cut));
    }

    [Fact]
    public void Rejects_a_64bit_load_address_outside_the_32bit_space()
    {
        var elf = BuildElf(is64: true, littleEndian: true,
            Load(paddr: 0x1_0000_0000, data: Bytes(1, 2, 3, 4))); // 4 GiB — beyond uint32
        var ex = Assert.Throws<ElfFormatException>(() => ElfImage.ReadLoadableSegments(elf));
        Assert.Contains("32-bit", ex.Message);
    }
}
