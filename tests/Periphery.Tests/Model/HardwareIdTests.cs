namespace Periphery.Tests;

public class HardwareIdTests
{
    // ── Construction ───────────────────────────────────────────────────

    [Fact]
    public void Ctor_StoresValue()
    {
        var id = new HardwareId(0x046D);
        Assert.Equal((ushort)0x046D, id.Value);
    }

    [Fact]
    public void Default_IsZero()
    {
        var id = default(HardwareId);
        Assert.Equal((ushort)0, id.Value);
    }

    // ── Parsing ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("046D", 0x046D)]
    [InlineData("0x046D", 0x046D)]
    [InlineData("0X046D", 0x046D)]
    [InlineData("FFFF", 0xFFFF)]
    [InlineData("0000", 0x0000)]
    [InlineData("0xABCD", 0xABCD)]
    [InlineData("abcd", 0xABCD)]
    public void Parse_ValidHex_ReturnsExpectedValue(string input, ushort expected)
    {
        var id = HardwareId.Parse(input);
        Assert.Equal(expected, id.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("GGGG")]
    [InlineData("1234567")]
    public void Parse_Invalid_ThrowsFormatException(string input)
    {
        Assert.Throws<FormatException>(() => HardwareId.Parse(input));
    }

    [Fact]
    public void TryParse_Null_ReturnsFalse()
    {
        Assert.False(HardwareId.TryParse(null, out _));
    }

    [Fact]
    public void TryParse_ValidHex_ReturnsTrueAndParsedValue()
    {
        Assert.True(HardwareId.TryParse("046D", out var result));
        Assert.Equal((ushort)0x046D, result.Value);
    }

    [Fact]
    public void TryParse_Invalid_ReturnsFalse()
    {
        Assert.False(HardwareId.TryParse("ZZZZ", out _));
    }

    // ── Formatting ─────────────────────────────────────────────────────

    [Fact]
    public void ToString_ReturnsZeroPaddedUppercaseHex()
    {
        var id = new HardwareId(0x001A);
        Assert.Equal("001A", id.ToString());
    }

    [Fact]
    public void ToString_MaxValue()
    {
        var id = new HardwareId(0xFFFF);
        Assert.Equal("FFFF", id.ToString());
    }

    [Fact]
    public void ToString_Zero_ReturnsFourZeros()
    {
        var id = new HardwareId(0);
        Assert.Equal("0000", id.ToString());
    }

    [Fact]
    public void ToString_WithFormat_UsesProvidedFormat()
    {
        var id = new HardwareId(255);
        Assert.Equal("ff", id.ToString("x2", null));
    }

    // ── Equality ───────────────────────────────────────────────────────

    [Fact]
    public void Equals_SameValue_ReturnsTrue()
    {
        var a = new HardwareId(0x046D);
        var b = new HardwareId(0x046D);
        Assert.True(a.Equals(b));
        Assert.True(a == b);
        Assert.False(a != b);
    }

    [Fact]
    public void Equals_DifferentValue_ReturnsFalse()
    {
        var a = new HardwareId(0x046D);
        var b = new HardwareId(0x046E);
        Assert.False(a.Equals(b));
        Assert.False(a == b);
        Assert.True(a != b);
    }

    [Fact]
    public void Equals_BoxedObject_ReturnsTrue()
    {
        var a = new HardwareId(0x046D);
        object b = new HardwareId(0x046D);
        Assert.True(a.Equals(b));
    }

    [Fact]
    public void Equals_NonHardwareIdObject_ReturnsFalse()
    {
        var a = new HardwareId(0x046D);
        Assert.False(a.Equals("046D"));
    }

    [Fact]
    public void GetHashCode_SameValue_SameHash()
    {
        var a = new HardwareId(0x046D);
        var b = new HardwareId(0x046D);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    // ── Operators ──────────────────────────────────────────────────────

    [Fact]
    public void ImplicitConversion_ToUshort()
    {
        var id = new HardwareId(0x046D);
        ushort value = id;
        Assert.Equal((ushort)0x046D, value);
    }

    [Fact]
    public void ExplicitConversion_FromUshort()
    {
        var id = (HardwareId)(ushort)0x046D;
        Assert.Equal((ushort)0x046D, id.Value);
    }

    // ── Round-trip ─────────────────────────────────────────────────────

    [Theory]
    [InlineData((ushort)0)]
    [InlineData((ushort)1)]
    [InlineData((ushort)0x046D)]
    [InlineData((ushort)0xFFFF)]
    public void RoundTrip_ThroughToStringAndParse(ushort raw)
    {
        var original = new HardwareId(raw);
        var roundTripped = HardwareId.Parse(original.ToString());
        Assert.Equal(original, roundTripped);
    }
}
