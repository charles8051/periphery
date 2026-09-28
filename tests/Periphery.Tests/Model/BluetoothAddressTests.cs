using System;

namespace Periphery.Tests;

/// <summary>
/// Unit tests for <see cref="BluetoothAddress"/>, the D5 join key of ADR-0085. Strings only, so
/// they run on any host. Addresses are synthetic.
/// </summary>
public class BluetoothAddressTests
{
    private const ulong Keyboard = 0xA0B1C2D3E4F5;
    private const ulong Mouse = 0xC1D2E3F4A5B6;

    // ── Construction ───────────────────────────────────────────────────

    [Fact]
    public void Ctor_StoresValue()
    {
        Assert.Equal(Keyboard, new BluetoothAddress(Keyboard).Value);
    }

    [Fact]
    public void Ctor_ValueWiderThan48Bits_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BluetoothAddress(0x1_0000_0000_0000));
    }

    [Fact]
    public void Ctor_Largest48BitValue_IsAccepted()
    {
        Assert.Equal("FF:FF:FF:FF:FF:FF", new BluetoothAddress(0xFFFF_FFFF_FFFF).ToString());
    }

    [Fact]
    public void Default_IsAllZeros()
    {
        Assert.Equal("00:00:00:00:00:00", default(BluetoothAddress).ToString());
    }

    // ── Parsing ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("A0:B1:C2:D3:E4:F5")]
    [InlineData("a0:b1:c2:d3:e4:f5")]
    [InlineData("A0-B1-C2-D3-E4-F5")]
    [InlineData("A0B1C2D3E4F5")]
    [InlineData("a0b1c2d3e4f5")]
    public void TryParse_AcceptedForms_ReadTheSameAddress(string input)
    {
        Assert.True(BluetoothAddress.TryParse(input, out var address));
        Assert.Equal(Keyboard, address.Value);
    }

    [Fact]
    public void TryParse_UnpaddedDigits_EqualTheSameAddressWrittenInFull()
    {
        // 32feet's Windows BluetoothDevice.Id formats with "X6", which drops leading zeros.
        var unpadded = BluetoothAddress.Parse("1A2B3C4D5E");
        var separated = BluetoothAddress.Parse("00:1A:2B:3C:4D:5E");

        Assert.Equal(separated, unpadded);
        Assert.Equal(0x001A2B3C4D5EUL, unpadded.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("A0:B1:C2:D3:E4")]              // five octets
    [InlineData("A0:B1:C2:D3:E4:F5:06")]        // seven octets
    [InlineData("A0:B1-C2:D3:E4:F5")]           // mixed separators
    [InlineData("A0.B1.C2.D3.E4.F5")]           // unsupported separator
    [InlineData("A0:B1:C2:D3:E4:G5")]           // not hex
    [InlineData("A0:B1C:2D:3E:4F:5")]           // separators out of place
    [InlineData("A0B1C2D3E4F5A")]               // thirteen digits
    [InlineData("0xA0B1C2D3E4F5")]              // prefix
    [InlineData(" A0B1C2D3E4F5")]               // whitespace
    [InlineData("-1")]
    public void TryParse_Invalid_IsRejected(string? input)
    {
        Assert.False(BluetoothAddress.TryParse(input, out var address));
        Assert.Equal(default, address);
    }

    [Fact]
    public void Parse_Invalid_ThrowsFormatException()
    {
        Assert.Throws<FormatException>(() => BluetoothAddress.Parse("not an address"));
    }

    // ── TryParseInstanceId ─────────────────────────────────────────────

    [Theory]
    [InlineData(@"BTHENUM\DEV_A0B1C2D3E4F5\7&2A1B3C4D&0&BLUETOOTHDEVICE_A0B1C2D3E4F5", Keyboard, BluetoothTransport.BrEdr)]
    [InlineData(@"BTHENUM\DEV_A0B1C2D3E4F5", Keyboard, BluetoothTransport.BrEdr)]
    [InlineData(@"bthenum\dev_a0b1c2d3e4f5\7&2a1b3c4d&0&bluetoothdevice_a0b1c2d3e4f5", Keyboard, BluetoothTransport.BrEdr)]
    [InlineData(@"BTHLE\DEV_C1D2E3F4A5B6\7&11111111&0&C1D2E3F4A5B6", Mouse, BluetoothTransport.LowEnergy)]
    public void TryParseInstanceId_DeviceNode_ReadsAddressAndTransport(string instanceId, ulong address, BluetoothTransport transport)
    {
        Assert.True(BluetoothAddress.TryParseInstanceId(instanceId, out var parsed, out var parsedTransport));
        Assert.Equal(address, parsed.Value);
        Assert.Equal(transport, parsedTransport);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"BTHENUM\{00001124-0000-1000-8000-00805F9B34FB}_VID&0002046D_PID&B35B\7&2A1B3C4D&0&A0B1C2D3E4F5_C00000000")]
    [InlineData(@"HID\{00001124-0000-1000-8000-00805F9B34FB}_VID&0002046D_PID&B35B&COL01\8&1F2E3D4C&0&0000")]
    [InlineData(@"BTHLEDEVICE\{00001812-0000-1000-8000-00805F9B34FB}_DEV_C1D2E3F4A5B6\8&0&0001")]
    [InlineData(@"USB\VID_046D&PID_C52B\5&3A1B2C&0&2")]
    [InlineData(@"BTHENUM\DEV_A0B1C2D3E4F\7&0&0")]      // eleven digits
    [InlineData(@"BTHENUM\DEV_A0B1C2D3E4F5A\7&0&0")]    // thirteen digits
    [InlineData(@"BTHENUM\DEV_A0B1C2D3E4G5\7&0&0")]     // not hex
    [InlineData("/sys/devices/pci0000:00/0000:00:14.0/usb1/1-10/1-10:1.0/bluetooth/hci0/hci0:3")]
    public void TryParseInstanceId_AnyOtherNode_IsRejected(string? instanceId)
    {
        Assert.False(BluetoothAddress.TryParseInstanceId(instanceId, out var address, out var transport));
        Assert.Equal(default, address);
        Assert.Equal(BluetoothTransport.Unknown, transport);
    }

    // ── ClassifyAsRandom ───────────────────────────────────────────────

    [Theory]
    [InlineData(0xC1D2E3F4A5B6UL, BluetoothRandomAddressKind.Static)]
    [InlineData(0xFFFFFFFFFFFFUL, BluetoothRandomAddressKind.Static)]
    [InlineData(0x41D2E3F4A5B6UL, BluetoothRandomAddressKind.ResolvablePrivate)]
    [InlineData(0x7FFFFFFFFFFFUL, BluetoothRandomAddressKind.ResolvablePrivate)]
    [InlineData(0x01D2E3F4A5B6UL, BluetoothRandomAddressKind.NonResolvablePrivate)]
    [InlineData(0x000000000000UL, BluetoothRandomAddressKind.NonResolvablePrivate)]
    [InlineData(0x81D2E3F4A5B6UL, BluetoothRandomAddressKind.Reserved)]
    [InlineData(0xBFFFFFFFFFFFUL, BluetoothRandomAddressKind.Reserved)]
    public void ClassifyAsRandom_ReadsTheTopTwoBits(ulong value, BluetoothRandomAddressKind expected)
    {
        Assert.Equal(expected, new BluetoothAddress(value).ClassifyAsRandom());
    }

    // ── Formatting ─────────────────────────────────────────────────────

    [Fact]
    public void ToString_KeepsLeadingZeroOctets()
    {
        Assert.Equal("00:1A:2B:3C:4D:5E", new BluetoothAddress(0x001A2B3C4D5E).ToString());
    }

    [Fact]
    public void ToString_WithX12_GivesTwelveUnseparatedDigits()
    {
        var address = new BluetoothAddress(0x001A2B3C4D5E);

        Assert.Equal("001A2B3C4D5E", $"{address:X12}");
        Assert.Equal("00:1A:2B:3C:4D:5E", $"{address}");
    }

    [Theory]
    [InlineData(Keyboard)]
    [InlineData(0x000000000001UL)]
    [InlineData(0xFFFFFFFFFFFFUL)]
    public void ToString_RoundTripsThroughParse(ulong value)
    {
        var address = new BluetoothAddress(value);

        Assert.Equal(address, BluetoothAddress.Parse(address.ToString()));
        Assert.Equal(address, BluetoothAddress.Parse(address.ToString("X12", null)));
    }
}
