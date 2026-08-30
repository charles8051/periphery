namespace Periphery.Monitor.Tests;

public class MccsCapabilitiesTests
{
    // Shaped like a real Dell capabilities string (abridged value lists).
    private const string DellStyle =
        "(prot(monitor)type(lcd)model(U2723QE)cmds(01 02 03 07 0C E3 F3)"
        + "vcp(02 04 05 08 10 12 14(05 08 0B 0C) 16 18 1A 60(0F 11 13) "
        + "AC AE B2 B6 C6 C8 C9 D6(01 04 05) DC(00 01 02 03 05) DF E0 E1 E2(00 01 02 04 0E 12 14))"
        + "mswhql(1)asset_eep(40)mccs_ver(2.1))";

    [Fact]
    public void DellStyleString_ParsesModelVersionAndCodes()
    {
        var caps = MccsCapabilities.Parse(DellStyle);

        Assert.Equal("U2723QE", caps.Model);
        Assert.Equal("2.1", caps.MccsVersion);
        Assert.True(caps.Supports(VcpCode.Luminance));
        Assert.True(caps.Supports(VcpCode.Contrast));
        Assert.True(caps.Supports(VcpCode.InputSource));
        Assert.True(caps.Supports(VcpCode.PowerMode));
        Assert.False(caps.Supports(0x62)); // No audio volume on this panel.
        Assert.Equal(DellStyle, caps.Raw);
    }

    [Fact]
    public void NonContinuousFeatures_CarryAllowedValueLists()
    {
        var caps = MccsCapabilities.Parse(DellStyle);

        Assert.Equal(new ushort[] { 0x0F, 0x11, 0x13 }, caps.AllowedValues(VcpCode.InputSource));
        Assert.Equal(new ushort[] { 0x01, 0x04, 0x05 }, caps.AllowedValues(VcpCode.PowerMode));
        Assert.Empty(caps.AllowedValues(VcpCode.Luminance)); // Continuous — no list.
    }

    [Fact]
    public void LowercaseHexAndLooseWhitespace_StillParse()
    {
        var caps = MccsCapabilities.Parse("(vcp( 10  12 60(0f 11) )mccs_ver(2.2))");

        Assert.True(caps.Supports(0x10));
        Assert.True(caps.Supports(0x12));
        Assert.Equal(new ushort[] { 0x0F, 0x11 }, caps.AllowedValues(0x60));
        Assert.Equal("2.2", caps.MccsVersion);
    }

    [Fact]
    public void TruncatedString_ParsesBestEffortWithoutThrowing()
    {
        // Cut mid value-list, unbalanced parentheses.
        var caps = MccsCapabilities.Parse("(prot(monitor)vcp(10 12 60(0F 11");

        Assert.True(caps.Supports(0x10));
        Assert.True(caps.Supports(0x12));
        Assert.True(caps.Supports(0x60));
    }

    [Fact]
    public void EmptyAndGarbageStrings_YieldEmptyCapabilities()
    {
        Assert.Empty(MccsCapabilities.Parse("").SupportedVcpCodes);
        Assert.Empty(MccsCapabilities.Parse("not a caps string").SupportedVcpCodes);
        Assert.Null(MccsCapabilities.Parse("()").Model);
    }
}
