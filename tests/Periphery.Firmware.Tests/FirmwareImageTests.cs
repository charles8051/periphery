using System;
using System.Buffers.Binary;
using System.Text;

namespace Periphery.Firmware.Tests;

/// <summary>
/// The format-detecting loader and the Intel HEX -> contiguous-segment coalescer: the pure
/// core that turns a firmware file into the addressed segments a flasher writes.
/// </summary>
public class FirmwareImageTests
{
    // Known-valid Intel HEX records (checksums verified): 4 bytes at 0x0000 and at 0x0010, + EOF.
    private const string OneRegion = ":04001000DEADBEEFB4\n:00000001FF\n";
    private const string TwoRegions = ":04000000102030405C\n:04001000DEADBEEFB4\n:00000001FF\n";

    [Fact]
    public void FromIntelHex_coalesces_a_contiguous_run_into_one_segment()
    {
        var image = FirmwareImage.FromIntelHex(OneRegion);

        var seg = Assert.Single(image.Segments);
        Assert.Equal(0x0010u, seg.Address);
        Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, seg.Data.ToArray());
        Assert.Equal(4L, image.TotalBytes);
    }

    [Fact]
    public void FromIntelHex_splits_disjoint_regions_into_separate_segments()
    {
        var image = FirmwareImage.FromIntelHex(TwoRegions);

        Assert.Equal(2, image.Segments.Length);
        Assert.Equal(0x0000u, image.Segments[0].Address);
        Assert.Equal(new byte[] { 0x10, 0x20, 0x30, 0x40 }, image.Segments[0].Data.ToArray());
        Assert.Equal(0x0010u, image.Segments[1].Address);
        Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, image.Segments[1].Data.ToArray());
    }

    [Fact]
    public void FromIntelHex_empty_input_is_an_empty_image()
        => Assert.True(FirmwareImage.FromIntelHex(":00000001FF\n").Segments.IsEmpty);

    [Fact]
    public void Load_hex_uses_the_files_own_addresses_ignoring_the_bin_base()
    {
        var image = FirmwareImage.Load(Encoding.ASCII.GetBytes(OneRegion), "app.hex", binBaseAddress: 0xDEADBEEF);

        var seg = Assert.Single(image.Segments);
        Assert.Equal(0x0010u, seg.Address); // from the HEX, not the (ignored) base
    }

    [Fact]
    public void Load_bin_places_raw_bytes_at_the_base()
    {
        var image = FirmwareImage.Load(new byte[] { 1, 2, 3, 4 }, "app.bin", 0x08000000);

        var seg = Assert.Single(image.Segments);
        Assert.Equal(0x08000000u, seg.Address);
        Assert.Equal(4L, image.TotalBytes);
    }

    [Fact]
    public void Load_bin_whose_content_is_intel_hex_is_refused() // brick-guard
    {
        var ex = Assert.Throws<FirmwareFormatException>(
            () => FirmwareImage.Load(Encoding.ASCII.GetBytes(OneRegion), "mislabelled.bin", 0x08000000));
        Assert.Contains("Intel HEX", ex.Message);
    }

    [Fact]
    public void Load_dfu_is_not_supported_yet()
        => Assert.Throws<NotSupportedException>(
            () => FirmwareImage.Load(Encoding.ASCII.GetBytes("DfuSe"), "app.dfu", 0x08000000));

    [Theory]
    [InlineData("app.srec")] // a real but not-yet-supported format
    [InlineData("app.xyz")]
    [InlineData("app")]      // no extension
    public void Load_unsupported_or_missing_extension_is_rejected(string fileName)
        => Assert.Throws<FirmwareFormatException>(
            () => FirmwareImage.Load(new byte[] { 1, 2, 3 }, fileName, 0x08000000));

    [Theory]
    [InlineData("app.elf")]
    [InlineData("app.axf")]
    [InlineData("app.out")]
    public void Load_elf_parses_PT_LOAD_segments_at_their_own_addresses(string fileName)
    {
        var elf = MinimalElf(0x08004000, new byte[] { 0xCA, 0xFE, 0xBA, 0xBE });

        var image = FirmwareImage.Load(elf, fileName, binBaseAddress: 0xDEADBEEF);

        var seg = Assert.Single(image.Segments);
        Assert.Equal(0x08004000u, seg.Address); // from the ELF, not the (ignored) base
        Assert.Equal(new byte[] { 0xCA, 0xFE, 0xBA, 0xBE }, seg.Data.ToArray());
    }

    [Fact]
    public void FromElf_reads_the_loadable_segments()
    {
        var image = FirmwareImage.FromElf(MinimalElf(0x08000000, new byte[] { 1, 2, 3, 4 }));

        var seg = Assert.Single(image.Segments);
        Assert.Equal(0x08000000u, seg.Address);
        Assert.Equal(4L, image.TotalBytes);
    }

    [Fact]
    public void Load_elf_whose_content_is_not_elf_is_refused() // brick-guard
    {
        var ex = Assert.Throws<FirmwareFormatException>(
            () => FirmwareImage.Load(new byte[] { 1, 2, 3, 4 }, "mislabelled.elf", 0x08000000));
        Assert.Contains("ELF", ex.Message);
    }

    [Fact]
    public void Load_bin_whose_content_is_elf_is_refused() // brick-guard
    {
        var elf = MinimalElf(0x08000000, new byte[] { 1, 2, 3, 4 });

        var ex = Assert.Throws<FirmwareFormatException>(
            () => FirmwareImage.Load(elf, "mislabelled.bin", 0x08000000));
        Assert.Contains("ELF", ex.Message);
    }

    // A minimal ELF32 little-endian executable with one PT_LOAD segment carrying <paramref name="data"/>
    // at <paramref name="paddr"/> — enough to exercise FirmwareImage's ELF path. (ElfImageTests covers
    // the format matrix; this is just a fixture for the loader.)
    private static byte[] MinimalElf(uint paddr, byte[] data)
    {
        const int hdr = 52, ent = 32;
        int dataOff = hdr + ent;
        var buf = new byte[dataOff + data.Length];

        buf[0] = 0x7F; buf[1] = (byte)'E'; buf[2] = (byte)'L'; buf[3] = (byte)'F';
        buf[4] = 1; // ELFCLASS32
        buf[5] = 1; // ELFDATA2LSB
        buf[6] = 1; // version
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(16, 2), 2);          // e_type ET_EXEC
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(28, 4), hdr);        // e_phoff
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(42, 2), ent);        // e_phentsize
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(44, 2), 1);          // e_phnum

        int p = hdr;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(p + 0, 4), 1);                    // p_type PT_LOAD
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(p + 4, 4), (uint)dataOff);        // p_offset
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(p + 8, 4), paddr);                // p_vaddr
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(p + 12, 4), paddr);               // p_paddr
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(p + 16, 4), (uint)data.Length);   // p_filesz
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(p + 20, 4), (uint)data.Length);   // p_memsz
        data.CopyTo(buf, dataOff);
        return buf;
    }
}
