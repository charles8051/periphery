using System;
using System.Linq;
using Periphery;
using Periphery.Usb;
using Xunit;

namespace Periphery.Usb.Tests;

/// <summary>
/// Golden-vector tests for <see cref="UsbDescriptors.ParseConfiguration"/>, the walk over a whole
/// configuration descriptor that a backend with no interface query surface (WebUSB) uses in place
/// of WinUSB's and libusb's.
/// </summary>
public class UsbConfigurationParserTests
{
    /// <summary>
    /// The Treehopper's configuration descriptor, byte for byte from
    /// <c>src/Periphery.Treehopper/firmware/Treehopper-EFM8/src/descriptors.c</c>: one vendor
    /// interface with four 64-byte bulk endpoints.
    /// </summary>
    private static readonly byte[] Treehopper =
    [
        0x09, 0x02, 0x2E, 0x00, 0x01, 0x01, 0x00, 0xC0, 0x32, // configuration, wTotalLength 46, 100 mA
        0x09, 0x04, 0x00, 0x00, 0x04, 0xFF, 0xFF, 0xFF, 0x00, // interface 0, alt 0, 4 endpoints, vendor
        0x07, 0x05, 0x81, 0x02, 0x40, 0x00, 0x01,             // EP1 IN, bulk, 64
        0x07, 0x05, 0x82, 0x02, 0x40, 0x00, 0x01,             // EP2 IN, bulk, 64
        0x07, 0x05, 0x01, 0x02, 0x40, 0x00, 0x01,             // EP1 OUT, bulk, 64
        0x07, 0x05, 0x02, 0x02, 0x40, 0x00, 0x01,             // EP2 OUT, bulk, 64
    ];

    /// <summary>
    /// An STM32 DFU bootloader's shape: one DFU interface listed once per memory region as
    /// alternate settings 0 to 2, no endpoints, then a DFU functional descriptor.
    /// </summary>
    private static readonly byte[] Stm32Dfu =
    [
        0x09, 0x02, 0x2D, 0x00, 0x01, 0x01, 0x00, 0x80, 0x32,
        0x09, 0x04, 0x00, 0x00, 0x00, 0xFE, 0x01, 0x02, 0x04, // interface 0, alt 0: internal flash
        0x09, 0x04, 0x00, 0x01, 0x00, 0xFE, 0x01, 0x02, 0x05, // interface 0, alt 1: option bytes
        0x09, 0x04, 0x00, 0x02, 0x00, 0xFE, 0x01, 0x02, 0x06, // interface 0, alt 2: OTP
        0x09, 0x21, 0x0B, 0xFF, 0x00, 0x00, 0x08, 0x1A, 0x01, // DFU functional descriptor
    ];

    /// <summary>
    /// A CDC ACM serial adapter: an interface association, a communication interface with its
    /// class-specific descriptors and an interrupt endpoint, then a data interface with two bulk
    /// endpoints.
    /// </summary>
    private static readonly byte[] CdcAcm =
    [
        0x09, 0x02, 0x4B, 0x00, 0x02, 0x01, 0x00, 0x80, 0xFA,
        0x08, 0x0B, 0x00, 0x02, 0x02, 0x02, 0x01, 0x00,       // interface association
        0x09, 0x04, 0x00, 0x00, 0x01, 0x02, 0x02, 0x01, 0x00, // interface 0: CDC communication
        0x05, 0x24, 0x00, 0x10, 0x01,                         // CS header
        0x05, 0x24, 0x01, 0x00, 0x01,                         // CS call management
        0x04, 0x24, 0x02, 0x02,                               // CS ACM
        0x05, 0x24, 0x06, 0x00, 0x01,                         // CS union
        0x07, 0x05, 0x83, 0x03, 0x08, 0x00, 0x10,             // EP3 IN, interrupt, 8, interval 16
        0x09, 0x04, 0x01, 0x00, 0x02, 0x0A, 0x00, 0x00, 0x00, // interface 1: CDC data
        0x07, 0x05, 0x01, 0x02, 0x40, 0x00, 0x00,             // EP1 OUT, bulk, 64
        0x07, 0x05, 0x82, 0x02, 0x40, 0x00, 0x00,             // EP2 IN, bulk, 64
    ];

    /// <summary>Sets a blob's <c>wTotalLength</c> to its own length, after bytes are spliced in.</summary>
    private static byte[] WithTotalLength(byte[] blob)
    {
        blob[2] = (byte)blob.Length;
        blob[3] = (byte)(blob.Length >> 8);
        return blob;
    }

    [Theory]
    [InlineData(nameof(Treehopper))]
    [InlineData(nameof(Stm32Dfu))]
    [InlineData(nameof(CdcAcm))]
    public void The_golden_vectors_declare_their_own_length(string name)
    {
        var blob = name switch { nameof(Treehopper) => Treehopper, nameof(Stm32Dfu) => Stm32Dfu, _ => CdcAcm };

        Assert.Equal(blob.Length, blob[2] | (blob[3] << 8));
    }

    [Fact]
    public void ParseConfiguration_DecodesTheTreehopper()
    {
        var config = UsbDescriptors.ParseConfiguration(Treehopper);

        Assert.Equal((byte)1, config.ConfigurationValue);
        Assert.Equal(100, config.MaxPowerMilliamps);
        var iface = Assert.Single(config.Interfaces);
        Assert.Equal((byte)0, iface.InterfaceNumber);
        Assert.Equal((byte)0, iface.AlternateSetting);
        Assert.Equal((byte)0xFF, iface.InterfaceClass);
        Assert.Equal((byte)0xFF, iface.InterfaceSubClass);
        Assert.Equal((byte)0xFF, iface.InterfaceProtocol);
        Assert.Equal(new byte[] { 0x81, 0x82, 0x01, 0x02 }, iface.Endpoints.Select(e => e.EndpointAddress));
        Assert.All(iface.Endpoints, e =>
        {
            Assert.Equal(UsbTransferType.Bulk, e.TransferType);
            Assert.Equal(64, e.MaxPacketSize);
            Assert.Equal((byte)1, e.Interval);
        });
    }

    [Fact]
    public void ParseConfiguration_KeepsOnlyAlternateSettingZero()
    {
        var config = UsbDescriptors.ParseConfiguration(Stm32Dfu);

        var iface = Assert.Single(config.Interfaces);
        Assert.Equal((byte)0, iface.AlternateSetting);
        Assert.Equal((byte)0xFE, iface.InterfaceClass);
        Assert.Equal((byte)0x01, iface.InterfaceSubClass);
        Assert.Equal((byte)0x02, iface.InterfaceProtocol);
        Assert.Empty(iface.Endpoints);
    }

    [Fact]
    public void ParseConfiguration_SkipsAssociationAndClassSpecificDescriptors()
    {
        var config = UsbDescriptors.ParseConfiguration(CdcAcm);

        Assert.Equal(500, config.MaxPowerMilliamps);
        Assert.Equal(new byte[] { 0, 1 }, config.Interfaces.Select(i => i.InterfaceNumber));

        var comm = config.Interfaces[0];
        var notify = Assert.Single(comm.Endpoints);
        Assert.Equal((byte)0x83, notify.EndpointAddress);
        Assert.Equal(UsbTransferType.Interrupt, notify.TransferType);
        Assert.Equal(8, notify.MaxPacketSize);
        Assert.Equal((byte)16, notify.Interval);

        var data = config.Interfaces[1];
        Assert.Equal((byte)0x0A, data.InterfaceClass);
        Assert.Equal(new byte[] { 0x01, 0x82 }, data.Endpoints.Select(e => e.EndpointAddress));
    }

    [Fact]
    public void ParseConfiguration_DropsTheEndpointsOfANonZeroAlternateSetting()
    {
        // A UVC-style streaming interface: alt 0 has no bandwidth, alt 1 carries the
        // isochronous endpoint.
        byte[] blob =
        [
            0x09, 0x02, 0x22, 0x00, 0x01, 0x01, 0x00, 0x80, 0xFA,
            0x09, 0x04, 0x01, 0x00, 0x00, 0x0E, 0x02, 0x00, 0x00, // interface 1, alt 0, no endpoints
            0x09, 0x04, 0x01, 0x01, 0x01, 0x0E, 0x02, 0x00, 0x00, // interface 1, alt 1
            0x07, 0x05, 0x81, 0x05, 0x00, 0x14, 0x01,             // EP1 IN, isochronous
        ];

        var config = UsbDescriptors.ParseConfiguration(blob);

        Assert.Empty(Assert.Single(config.Interfaces).Endpoints);
    }

    [Fact]
    public void ParseConfiguration_KeepsTheRawMaxPacketSizeField()
    {
        // 0x1400: 1024 bytes with one additional transaction per microframe (bits 11-12).
        byte[] blob =
        [
            0x09, 0x02, 0x19, 0x00, 0x01, 0x01, 0x00, 0x80, 0xFA,
            0x09, 0x04, 0x00, 0x00, 0x01, 0xFF, 0x00, 0x00, 0x00,
            0x07, 0x05, 0x81, 0x01, 0x00, 0x14, 0x01,
        ];

        var endpoint = Assert.Single(Assert.Single(UsbDescriptors.ParseConfiguration(blob).Interfaces).Endpoints);

        Assert.Equal(0x1400, endpoint.MaxPacketSize);
        Assert.Equal(UsbTransferType.Isochronous, endpoint.TransferType);
    }

    [Fact]
    public void ParseConfiguration_OfAHeaderAloneHasNoInterfaces()
    {
        var config = UsbDescriptors.ParseConfiguration(Treehopper.AsSpan(0, 9));

        Assert.Empty(config.Interfaces);
        Assert.Equal((byte)1, config.ConfigurationValue);
    }

    [Fact]
    public void ParseConfiguration_KeepsWhatItDecodedBeforeATruncatedDescriptor()
    {
        // Cut three bytes into the third endpoint descriptor.
        var config = UsbDescriptors.ParseConfiguration(Treehopper.AsSpan(0, 9 + 9 + 7 + 7 + 3));

        Assert.Equal(new byte[] { 0x81, 0x82 }, Assert.Single(config.Interfaces).Endpoints.Select(e => e.EndpointAddress));
    }

    [Fact]
    public void ParseConfiguration_StopsAtAZeroLengthDescriptor()
    {
        byte[] blob = WithTotalLength([.. Treehopper.AsSpan(0, 9 + 9 + 7), 0x00, 0x05, .. Treehopper.AsSpan(9 + 9 + 7)]);

        var config = UsbDescriptors.ParseConfiguration(blob);

        Assert.Equal((byte)0x81, Assert.Single(Assert.Single(config.Interfaces).Endpoints).EndpointAddress);
    }

    [Fact]
    public void ParseConfiguration_IgnoresAnEndpointBeforeAnyInterface()
    {
        byte[] blob = WithTotalLength([.. Treehopper.AsSpan(0, 9), 0x07, 0x05, 0x83, 0x02, 0x40, 0x00, 0x01, .. Treehopper.AsSpan(9)]);

        var config = UsbDescriptors.ParseConfiguration(blob);

        Assert.Equal(4, Assert.Single(config.Interfaces).Endpoints.Length);
        Assert.DoesNotContain(config.Interfaces[0].Endpoints, e => e.EndpointAddress == 0x83);
    }

    [Fact]
    public void ParseConfiguration_DoesNotWalkPastWTotalLength()
    {
        // The Treehopper's descriptor followed by a stray interface and endpoint.
        byte[] blob = [.. Treehopper, 0x09, 0x04, 0x01, 0x00, 0x01, 0xFF, 0x00, 0x00, 0x00, 0x07, 0x05, 0x83, 0x02, 0x40, 0x00, 0x01];

        var config = UsbDescriptors.ParseConfiguration(blob);

        var iface = Assert.Single(config.Interfaces);
        Assert.DoesNotContain(iface.Endpoints, e => e.EndpointAddress == 0x83);
    }

    [Fact]
    public void ParseConfiguration_OfAHeaderDeclaringOnlyItselfHasNoInterfaces()
    {
        byte[] blob = [.. Treehopper];
        blob[2] = 9;
        blob[3] = 0;

        Assert.Empty(UsbDescriptors.ParseConfiguration(blob).Interfaces);
    }

    [Fact]
    public void ParseConfiguration_StopsAtAShortInterfaceDescriptor()
    {
        // The CDC communication interface, then an 8-byte interface descriptor, then an endpoint
        // that must not be handed to the communication interface.
        byte[] blob = WithTotalLength(
        [
            .. CdcAcm.AsSpan(0, 9 + 8 + 9 + 5 + 5 + 4 + 5 + 7),
            0x08, 0x04, 0x01, 0x00, 0x02, 0x0A, 0x00, 0x00,
            0x07, 0x05, 0x01, 0x02, 0x40, 0x00, 0x00,
        ]);

        var config = UsbDescriptors.ParseConfiguration(blob);

        var comm = Assert.Single(config.Interfaces);
        Assert.Equal((byte)0x83, Assert.Single(comm.Endpoints).EndpointAddress);
    }

    [Fact]
    public void ParseConfiguration_StopsAtAShortEndpointDescriptor()
    {
        byte[] blob = WithTotalLength(
        [
            .. Treehopper.AsSpan(0, 9 + 9 + 7),
            0x06, 0x05, 0x82, 0x02, 0x40, 0x00,
            .. Treehopper.AsSpan(9 + 9 + 7 + 7),
        ]);

        var config = UsbDescriptors.ParseConfiguration(blob);

        Assert.Equal((byte)0x81, Assert.Single(Assert.Single(config.Interfaces).Endpoints).EndpointAddress);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    public void ParseConfiguration_ThrowsOnABufferShorterThanTheHeader(int length)
    {
        var ex = Assert.Throws<ArgumentException>(
            () => UsbDescriptors.ParseConfiguration(Treehopper.AsSpan(0, length)));
        Assert.Equal("raw", ex.ParamName);
    }
}
