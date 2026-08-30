using System;
using Xunit;

namespace Periphery.Firmware.Tests;

/// <summary>The firmware payload union: memory-image vs packaged-blob loading, kinds, and the safety checks.</summary>
public class FirmwarePayloadTests
{
    [Theory]
    [InlineData("fw.efm8")]
    [InlineData("fw.tfi")]
    [InlineData("FW.EFM8")] // extension match is case-insensitive
    public void Load_packaged_blob_takes_the_bytes_as_is(string fileName)
    {
        var bytes = new byte[] { (byte)'$', 0x03, 0x36, 0x00, 0x00 };
        var payload = FirmwarePayload.Load(bytes, fileName, binBaseAddress: 0);

        Assert.Equal(FirmwareKind.PackagedBlob, payload.Kind);
        Assert.Equal(FirmwareFormat.Efm8BootRecords, payload.Format);
        Assert.Equal(bytes, payload.Blob.ToArray());
        Assert.Null(payload.MemoryImage);
        Assert.Equal(bytes.Length, payload.ByteLength);
    }

    [Fact]
    public void Load_memory_image_parses_a_bin_as_raw_binary()
    {
        var payload = FirmwarePayload.Load(new byte[16], "fw.bin", binBaseAddress: 0x08000000);

        Assert.Equal(FirmwareKind.MemoryImage, payload.Kind);
        Assert.Equal(FirmwareFormat.RawBinary, payload.Format);
        Assert.NotNull(payload.MemoryImage);
        Assert.Equal(16, payload.ByteLength);
    }

    [Fact]
    public void Load_rejects_an_unknown_extension()
    {
        Assert.Throws<FirmwareFormatException>(() => FirmwarePayload.Load(new byte[4], "fw.xyz", 0));
    }

    [Fact]
    public void FromBlob_rejects_a_memory_image_format()
    {
        Assert.Throws<ArgumentException>(() => FirmwarePayload.FromBlob(new byte[4], FirmwareFormat.IntelHex));
    }

    [Fact]
    public void FromImage_rejects_a_packaged_blob_format()
    {
        Assert.Throws<ArgumentException>(() =>
            FirmwarePayload.FromImage(FirmwareImage.FromBytes(0, new byte[4]), FirmwareFormat.Efm8BootRecords));
    }

    [Theory]
    [InlineData(FirmwareFormat.IntelHex, FirmwareKind.MemoryImage)]
    [InlineData(FirmwareFormat.RawBinary, FirmwareKind.MemoryImage)]
    [InlineData(FirmwareFormat.Elf, FirmwareKind.MemoryImage)]
    [InlineData(FirmwareFormat.Efm8BootRecords, FirmwareKind.PackagedBlob)]
    public void Format_kind_mapping(FirmwareFormat format, FirmwareKind expected)
    {
        Assert.Equal(expected, format.Kind());
    }
}
