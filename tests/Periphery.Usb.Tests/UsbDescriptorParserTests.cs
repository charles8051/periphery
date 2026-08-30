using System;
using Periphery;
using Periphery.Usb;
using Xunit;

namespace Periphery.Usb.Tests;

/// <summary>
/// Golden-vector tests for the pure USB descriptor parser, mirroring
/// <c>Periphery.Hid.Tests.HidReportDescriptorTests</c>. Feeds known
/// device/configuration descriptor byte arrays (the raw bytes a real device
/// returns from <c>GET_DESCRIPTOR</c>) and asserts the decoded fields, so the
/// byte-level decode that ships is exercised off-device — not only on real
/// hardware via the WinUSB / libusb backends.
/// </summary>
public class UsbDescriptorParserTests
{
    /// <summary>
    /// Real device descriptor of a Silicon Labs CP210x USB-to-UART bridge
    /// (VID 0x10C4, PID 0xEA60), USB 1.10, class defined at interface level.
    /// </summary>
    private static readonly byte[] Cp210xDevice =
    [
        0x12,        // bLength = 18
        0x01,        // bDescriptorType = DEVICE
        0x10, 0x01,  // bcdUSB = 0x0110 (USB 1.1), little-endian
        0x00,        // bDeviceClass = 0 (per-interface)
        0x00,        // bDeviceSubClass
        0x00,        // bDeviceProtocol
        0x40,        // bMaxPacketSize0 = 64
        0xC4, 0x10,  // idVendor = 0x10C4 (Silicon Labs)
        0x60, 0xEA,  // idProduct = 0xEA60 (CP210x)
        0x00, 0x01,  // bcdDevice = 0x0100
        0x01,        // iManufacturer
        0x02,        // iProduct
        0x03,        // iSerialNumber
        0x01,        // bNumConfigurations = 1
    ];

    /// <summary>
    /// USB 2.0 hi-speed hub device descriptor: class 0x09 (Hub), protocol 0x02
    /// (hi-speed hub with multiple TTs), 64-byte EP0, two configurations —
    /// exercises non-zero class/subclass/protocol and a multi-config count.
    /// </summary>
    private static readonly byte[] HiSpeedHubDevice =
    [
        0x12,        // bLength = 18
        0x01,        // bDescriptorType = DEVICE
        0x00, 0x02,  // bcdUSB = 0x0200 (USB 2.0)
        0x09,        // bDeviceClass = 0x09 (Hub)
        0x00,        // bDeviceSubClass
        0x02,        // bDeviceProtocol = 0x02 (hi-speed, multiple TTs)
        0x40,        // bMaxPacketSize0 = 64
        0x09, 0x12,  // idVendor = 0x1209 (pid.codes)
        0x34, 0x12,  // idProduct = 0x1234
        0x33, 0x42,  // bcdDevice = 0x4233
        0x00,        // iManufacturer
        0x00,        // iProduct
        0x00,        // iSerialNumber
        0x02,        // bNumConfigurations = 2
    ];

    [Fact]
    public void ParseDeviceDescriptor_Cp210x_DecodesAllFields()
    {
        var d = UsbDescriptors.ParseDeviceDescriptor(Cp210xDevice);

        Assert.Equal(0x0110, d.UsbVersion);
        Assert.Equal(0x00, d.DeviceClass);
        Assert.Equal(0x00, d.DeviceSubClass);
        Assert.Equal(0x00, d.DeviceProtocol);
        Assert.Equal(64, d.MaxPacketSize0);
        Assert.Equal(new HardwareId(0x10C4), d.VendorId);
        Assert.Equal(new HardwareId(0xEA60), d.ProductId);
        Assert.Equal(0x0100, d.DeviceVersion);
        Assert.Equal((byte)1, d.ConfigurationCount);
    }

    [Fact]
    public void ParseDeviceDescriptor_HiSpeedHub_DecodesClassAndConfigCount()
    {
        var d = UsbDescriptors.ParseDeviceDescriptor(HiSpeedHubDevice);

        Assert.Equal(0x0200, d.UsbVersion);
        Assert.Equal(0x09, d.DeviceClass);
        Assert.Equal(0x02, d.DeviceProtocol);
        Assert.Equal(new HardwareId(0x1209), d.VendorId);
        Assert.Equal(new HardwareId(0x1234), d.ProductId);
        Assert.Equal(0x4233, d.DeviceVersion);
        Assert.Equal((byte)2, d.ConfigurationCount);
    }

    [Fact]
    public void ParseDeviceDescriptor_ReadsLittleEndianMultiByteFields()
    {
        // idVendor at offset 8 = bytes C4 10 -> 0x10C4, not 0xC410: guards
        // against a byte-order regression in the multi-byte field decode.
        var d = UsbDescriptors.ParseDeviceDescriptor(Cp210xDevice);

        Assert.Equal((ushort)0x10C4, d.VendorId.Value);
        Assert.Equal((ushort)0xEA60, d.ProductId.Value);
    }

    [Fact]
    public void ParseDeviceDescriptor_IgnoresTrailingBytesBeyond18()
    {
        // A caller may hand a larger buffer than the standard 18-byte prefix
        // (e.g. an over-allocated read); only the device descriptor is decoded.
        Span<byte> padded = stackalloc byte[32];
        Cp210xDevice.AsSpan().CopyTo(padded);
        padded[18] = 0xFF; // garbage tail must not affect the result.

        var d = UsbDescriptors.ParseDeviceDescriptor(padded);

        Assert.Equal(new HardwareId(0x10C4), d.VendorId);
        Assert.Equal((byte)1, d.ConfigurationCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(17)] // one byte short of the standard 18.
    public void ParseDeviceDescriptor_ThrowsOnShortBuffer(int length)
    {
        var tooShort = Cp210xDevice.AsSpan(0, length).ToArray();

        var ex = Assert.Throws<ArgumentException>(
            () => UsbDescriptors.ParseDeviceDescriptor(tooShort));
        Assert.Equal("raw", ex.ParamName);
    }

    // ── Configuration header ────────────────────────────────────────────

    /// <summary>
    /// A standard configuration descriptor header: bConfigurationValue = 1,
    /// one interface, bMaxPower = 50 (×2 = 100 mA).
    /// </summary>
    private static readonly byte[] ConfigHeader =
    [
        0x09,        // bLength = 9
        0x02,        // bDescriptorType = CONFIGURATION
        0x20, 0x00,  // wTotalLength = 0x0020 (32), little-endian
        0x01,        // bNumInterfaces = 1
        0x01,        // bConfigurationValue = 1
        0x00,        // iConfiguration
        0xC0,        // bmAttributes (self-powered)
        0x32,        // bMaxPower = 0x32 (50) -> 100 mA
    ];

    [Fact]
    public void ParseConfigurationHeader_DecodesValueInterfaceCountAndPower()
    {
        var h = UsbDescriptors.ParseConfigurationHeader(ConfigHeader);

        Assert.Equal((byte)1, h.ConfigurationValue);
        Assert.Equal((byte)1, h.InterfaceCount);
        Assert.Equal(100, h.MaxPowerMilliamps); // bMaxPower 0x32 * 2 mA.
    }

    [Fact]
    public void ParseConfigurationHeader_ExpandsMaxPowerFrom2mAUnits()
    {
        // bMaxPower = 0xFA (250) is the spec maximum -> 500 mA.
        byte[] header = (byte[])ConfigHeader.Clone();
        header[8] = 0xFA;

        var h = UsbDescriptors.ParseConfigurationHeader(header);

        Assert.Equal(500, h.MaxPowerMilliamps);
    }

    [Fact]
    public void ParseConfigurationHeader_IgnoresInterfaceAndEndpointDescriptorsInBlob()
    {
        // A full config blob is the 9-byte header followed by interface /
        // endpoint descriptors; the header parser reads only the leading 9
        // bytes and is unaffected by what trails.
        Span<byte> blob = stackalloc byte[ConfigHeader.Length + 16];
        ConfigHeader.AsSpan().CopyTo(blob);
        blob[ConfigHeader.Length] = 0x09;     // a trailing interface descriptor bLength
        blob[ConfigHeader.Length + 1] = 0x04; // bDescriptorType = INTERFACE

        var h = UsbDescriptors.ParseConfigurationHeader(blob);

        Assert.Equal((byte)1, h.ConfigurationValue);
        Assert.Equal(100, h.MaxPowerMilliamps);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)] // one byte short of the 9-byte header.
    public void ParseConfigurationHeader_ThrowsOnShortBuffer(int length)
    {
        var tooShort = ConfigHeader.AsSpan(0, length).ToArray();

        var ex = Assert.Throws<ArgumentException>(
            () => UsbDescriptors.ParseConfigurationHeader(tooShort));
        Assert.Equal("raw", ex.ParamName);
    }
}
