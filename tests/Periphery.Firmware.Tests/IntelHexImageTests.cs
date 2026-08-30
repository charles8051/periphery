using System;
using System.Linq;
using Xunit;

namespace Periphery.Firmware.Tests;

/// <summary>Unit tests for the pure Intel HEX parser / sparse image.</summary>
public class IntelHexImageTests
{
    [Fact]
    public void Parse_DataRecord_PopulatesAddressesAndPadsGaps()
    {
        // 4 bytes (DE AD BE EF) at address 0x0010, then EOF.
        var image = IntelHexImage.Parse(":04001000DEADBEEFB4\n:00000001FF\n");

        Assert.False(image.IsEmpty);
        Assert.Equal(0x0010, image.MinAddress);
        Assert.Equal(0x0013, image.MaxAddress);
        Assert.Equal(0xDE, image.Get(0x0010));
        Assert.Equal(0xEF, image.Get(0x0013));
        Assert.Equal(0xFF, image.Get(0x0014));   // unpopulated -> padding
        Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, image.ToBinary(0x0010, 4));
        // ToBinary across a gap pads with 0xFF.
        Assert.Equal(new byte[] { 0xEF, 0xFF, 0xFF }, image.ToBinary(0x0013, 3));
    }

    [Fact]
    public void Parse_ExtendedLinearAddress_OffsetsSubsequentData()
    {
        // ELA 0x0001 -> base 0x00010000; then 2 bytes at offset 0x0000.
        var image = IntelHexImage.Parse(":020000040001F9\n:020000000102FB\n:00000001FF\n");
        Assert.Equal(0x00010000, image.MinAddress);
        Assert.Equal(0x01, image.Get(0x00010000));
        Assert.Equal(0x02, image.Get(0x00010001));
    }

    [Fact]
    public void Slice_KeepsAddressesInHalfOpenRange()
    {
        var image = IntelHexImage.Parse(":04000000102030405C\n:00000001FF\n"); // 0x10..0x40 at 0..3
        var slice = image.Slice(1, 3);  // [1, 3) -> addresses 1, 2
        Assert.Equal(1, slice.MinAddress);
        Assert.Equal(2, slice.MaxAddress);
        Assert.Equal(new[] { 1, 2 }, slice.Addresses.ToArray());
    }

    [Fact]
    public void Parse_BlankLinesAndCarriageReturns_AreTolerated()
    {
        var image = IntelHexImage.Parse(":04000000102030405C\r\n\r\n:00000001FF\r\n");
        Assert.Equal(0x10, image.Get(0));
    }

    [Theory]
    [InlineData(":04001000DEADBEEF00\n:00000001FF\n", "checksum")]     // wrong checksum
    [InlineData("04001000DEADBEEF66\n:00000001FF\n", "':'")]           // missing start mark
    [InlineData(":04001000DEADBEE\n:00000001FF\n", "odd")]             // odd hex digit count
    [InlineData(":04001000DEADBEEFGG\n:00000001FF\n", "hex digit")]    // non-hex
    [InlineData(":04001000DEADBEEFB4\n", "end-of-file")]               // no EOF record
    public void Parse_Malformed_Throws(string hex, string expectedFragment)
    {
        var ex = Assert.Throws<IntelHexFormatException>(() => IntelHexImage.Parse(hex));
        Assert.Contains(expectedFragment, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_EmptyAfterEof_Throws()
    {
        Assert.Throws<IntelHexFormatException>(
            () => IntelHexImage.Parse(":00000001FF\n:04000000102030405C\n"));
    }

    [Fact]
    public void Parse_OnlyEof_IsEmptyImage()
    {
        var image = IntelHexImage.Parse(":00000001FF\n");
        Assert.True(image.IsEmpty);
    }
}
