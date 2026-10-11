namespace Periphery.Usb.Browser.Tests;

/// <summary>Decoding <c>bmRequestType</c> into WebUSB's control-transfer names.</summary>
public class WebUsbSetupTests
{
    [Theory]
    [InlineData(0x80, "standard", "device")]      // GET_DESCRIPTOR
    [InlineData(0x00, "standard", "device")]
    [InlineData(0x21, "class", "interface")]      // DFU DNLOAD
    [InlineData(0xA1, "class", "interface")]      // DFU GETSTATUS
    [InlineData(0x02, "standard", "endpoint")]    // CLEAR_FEATURE(ENDPOINT_HALT)
    [InlineData(0xC0, "vendor", "device")]        // Microsoft OS 1.0 string request
    [InlineData(0x43, "vendor", "other")]
    public void Decodes_type_and_recipient_and_ignores_direction(byte bmRequestType, string requestType, string recipient)
    {
        Assert.Equal((requestType, recipient), WebUsbSetup.Names(bmRequestType));
    }

    [Theory]
    [InlineData(0x60)] // reserved type
    [InlineData(0xE1)] // reserved type, IN
    [InlineData(0x04)] // reserved recipient
    [InlineData(0x1F)] // reserved recipient
    public void A_reserved_type_or_recipient_has_no_names(byte bmRequestType)
    {
        Assert.Null(WebUsbSetup.Names(bmRequestType));
    }

    [Fact]
    public void A_setup_goes_through_the_method_its_direction_bit_names()
    {
        var getStatus = new UsbControlSetup { RequestType = 0xA1, Request = 0x03 };
        var download = new UsbControlSetup { RequestType = 0x21, Request = 0x01 };

        Assert.Equal(("class", "interface"), WebUsbSetup.NamesFor(getStatus, UsbTransferDirection.DeviceToHost));
        Assert.Equal(("class", "interface"), WebUsbSetup.NamesFor(download, UsbTransferDirection.HostToDevice));
    }

    [Fact]
    public void A_setup_sent_through_the_other_method_has_no_names()
    {
        var getStatus = new UsbControlSetup { RequestType = 0xA1, Request = 0x03 };
        var download = new UsbControlSetup { RequestType = 0x21, Request = 0x01 };

        Assert.Null(WebUsbSetup.NamesFor(getStatus, UsbTransferDirection.HostToDevice));
        Assert.Null(WebUsbSetup.NamesFor(download, UsbTransferDirection.DeviceToHost));
    }
}
