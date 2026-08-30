using Periphery.Treehopper.Wire;

namespace Periphery.Treehopper.Tests;

/// <summary>
/// Asserts that <see cref="TreehopperWire.DecodeReport"/> correctly parses raw
/// 41-byte pin-state buffers into immutable <see cref="BoardReport"/> snapshots.
/// Pure function, zero hardware. (ADR-0052 DEC-002.)
/// </summary>
public class WireDecodeTests
{
    private static byte[] BlankReport()
    {
        var r = new byte[TreehopperWire.PinReportLength];
        r[0] = 0x01; // non-zero report ID → valid
        return r;
    }

    private static byte[] ReportWith(int pin, byte high, byte low)
    {
        var r = BlankReport();
        r[1 + pin * 2] = high;
        r[2 + pin * 2] = low;
        return r;
    }

    [Fact]
    public void DecodeReport_AllZeros_AllPinsDigitalFalseAdcZero()
    {
        var raw = BlankReport();
        var report = TreehopperWire.DecodeReport(raw, 0);

        Assert.Equal(TreehopperWire.PinCount, report.Pins.Length);
        foreach (var pin in report.Pins)
        {
            Assert.False(pin.Digital);
            Assert.Equal(0, pin.Adc);
        }
    }

    [Fact]
    public void DecodeReport_AssignsSequenceNumber()
    {
        var report = TreehopperWire.DecodeReport(BlankReport(), sequence: 42);
        Assert.Equal(42L, report.Sequence);
    }

    [Theory]
    [InlineData(0,  0x0A, 0xBC, 0x0ABC)]
    [InlineData(7,  0x01, 0x00, 0x0100)]
    [InlineData(19, 0x0F, 0xFF, 0x0FFF)]
    public void DecodeReport_AdcValue_IsHighByteShift8OrLowByte(int pin, byte high, byte low, int expected)
    {
        var report = TreehopperWire.DecodeReport(ReportWith(pin, high, low), 0);
        Assert.Equal(expected, report.Pins[pin].Adc);
    }

    [Theory]
    [InlineData(0x00, 0x00, false)] // high byte 0 → low
    [InlineData(0x00, 0x01, false)] // digital reads the HIGH byte; a low-byte-only value is not digital-high
    [InlineData(0x01, 0x00, true)]  // high byte set → high (firmware: `_digitalValue = highByte > 0`)
    [InlineData(0x01, 0x01, true)]  // high byte set → high
    public void DecodeReport_Digital_TracksHighByte(byte high, byte low, bool expectedDigital)
    {
        var report = TreehopperWire.DecodeReport(ReportWith(0, high, low), 0);
        Assert.Equal(expectedDigital, report.Pins[0].Digital);
    }

    [Fact]
    public void DecodeReport_AnalogValue_ScalesByAdcDivisor()
    {
        // ADC = 0x0FFF = 4095; AnalogValue = 4095 / 4092.0 ≈ 1.000733
        var report = TreehopperWire.DecodeReport(ReportWith(3, 0x0F, 0xFF), 0);
        Assert.Equal(4095 / TreehopperWire.AdcDivisor, report.Pins[3].AnalogValue, precision: 6);
    }

    [Fact]
    public void DecodeReport_AnalogVoltage_DefaultsTo33VReference()
    {
        // ADC at half scale: 2046 / 4092 * 3.3 ≈ 1.65 V
        var report = TreehopperWire.DecodeReport(ReportWith(0, 0x07, 0xFE), 0);
        Assert.Equal(2046 / TreehopperWire.AdcDivisor * 3.3, report.Pins[0].AnalogVoltage(), precision: 4);
    }

    [Fact]
    public void DecodeReport_AnalogVoltage_HonoursCustomReference()
    {
        // Same ADC, 1.65 V reference → half of the 3.3 V result
        var report = TreehopperWire.DecodeReport(ReportWith(0, 0x07, 0xFE), 0);
        Assert.Equal(2046 / TreehopperWire.AdcDivisor * 1.65, report.Pins[0].AnalogVoltage(1.65), precision: 4);
    }

    [Fact]
    public void DecodeReport_TooShort_ThrowsArgumentException()
        => Assert.Throws<ArgumentException>(() =>
            TreehopperWire.DecodeReport(new byte[5], 0));

    [Fact]
    public void DecodeReport_PinCount_Matches20()
    {
        var report = TreehopperWire.DecodeReport(BlankReport(), 0);
        Assert.Equal(20, report.Pins.Length);
    }

    [Fact]
    public void DecodeReport_IsImmutable_SecondDecodeIndependent()
    {
        var raw = ReportWith(0, 0x01, 0x00);
        var r1 = TreehopperWire.DecodeReport(raw, 1);

        // Mutate the source buffer after decoding
        raw[1] = 0x00;
        var r2 = TreehopperWire.DecodeReport(raw, 2);

        // r1 should be unaffected (ImmutableArray, not a view)
        Assert.Equal(0x100, r1.Pins[0].Adc);
        Assert.Equal(0,     r2.Pins[0].Adc);
    }

    // ── 1-Wire ROM decode ──────────────────────────────────────────────

    [Fact]
    public void DecodeOneWireRom_ReversesPacketAndReadsLittleEndian()
    {
        // status byte 0x00 + ROM byte 0x01 at index 1 → after reversing the 9-byte
        // packet, that 0x01 becomes the most-significant byte of the 8-byte ROM.
        var packet = new byte[] { 0x00, 0x01, 0, 0, 0, 0, 0, 0, 0 };
        Assert.Equal(0x0100000000000000UL, TreehopperWire.DecodeOneWireRom(packet));
    }

    [Fact]
    public void DecodeOneWireRom_TooShort_Throws()
        => Assert.Throws<ArgumentException>(() => TreehopperWire.DecodeOneWireRom(new byte[5]));
}
