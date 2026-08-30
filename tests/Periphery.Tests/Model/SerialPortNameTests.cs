namespace Periphery.Tests;

public class SerialPortNameTests
{
    // ── Construction ───────────────────────────────────────────────────

    [Fact]
    public void Constructor_WithValidValue_StoresValue()
    {
        var name = new SerialPortName("COM3");
        Assert.Equal("COM3", name.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_WithEmptyOrWhitespace_Throws(string? value)
    {
        Assert.Throws<ArgumentException>(() => new SerialPortName(value!));
    }

    [Theory]
    [InlineData(null)]
    public void Constructor_WithNull_Throws(string? value)
    {
        Assert.Throws<ArgumentNullException>(() => new SerialPortName(value!));
    }

    // ── Parse ──────────────────────────────────────────────────────────

    [Fact]
    public void Parse_ValidValue_ReturnsInstance()
    {
        var name = SerialPortName.Parse("COM3");
        Assert.Equal("COM3", name.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_InvalidValue_ThrowsFormatException(string? value)
    {
        Assert.Throws<FormatException>(() => SerialPortName.Parse(value!));
    }

    // ── TryParse ───────────────────────────────────────────────────────

    [Fact]
    public void TryParse_ValidValue_ReturnsTrueAndResult()
    {
        var success = SerialPortName.TryParse("COM3", out var result);

        Assert.True(success);
        Assert.Equal("COM3", result.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryParse_InvalidValue_ReturnsFalse(string? value)
    {
        var success = SerialPortName.TryParse(value, out _);
        Assert.False(success);
    }

    // ── Equality ───────────────────────────────────────────────────────

    [Fact]
    public void Equals_SameValue_ReturnsTrue()
    {
        var a = new SerialPortName("COM3");
        var b = new SerialPortName("COM3");
        Assert.Equal(a, b);
    }

    [Fact]
    public void Equals_DifferentValue_ReturnsFalse()
    {
        var a = new SerialPortName("COM3");
        var b = new SerialPortName("COM4");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void GetHashCode_SameValue_SameHash()
    {
        var a = new SerialPortName("COM3");
        var b = new SerialPortName("COM3");
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    // ── ToString ───────────────────────────────────────────────────────

    [Fact]
    public void ToString_ReturnsValue()
    {
        var name = new SerialPortName("COM3");
        Assert.Equal("COM3", name.ToString());
    }

    // ── Linux / macOS port names ───────────────────────────────────────

    [Fact]
    public void Constructor_AcceptsLinuxPortName()
    {
        var name = new SerialPortName("/dev/ttyUSB0");
        Assert.Equal("/dev/ttyUSB0", name.Value);
    }

    [Fact]
    public void Constructor_AcceptsMacOsPortName()
    {
        var name = new SerialPortName("/dev/cu.usbserial-1420");
        Assert.Equal("/dev/cu.usbserial-1420", name.Value);
    }
}
