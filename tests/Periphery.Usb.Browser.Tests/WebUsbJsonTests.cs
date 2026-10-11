namespace Periphery.Usb.Browser.Tests;

/// <summary>The JSON the package exchanges with its JS module.</summary>
public class WebUsbJsonTests
{
    // ── Filters ────────────────────────────────────────────────────────

    [Fact]
    public void An_empty_filter_list_is_an_empty_array()
    {
        Assert.Equal("[]", WebUsbJson.WriteFilters([]));
    }

    [Fact]
    public void A_filter_writes_only_the_fields_it_sets_under_WebUSBs_names()
    {
        var json = WebUsbJson.WriteFilters(
        [
            new UsbDeviceRequestFilter { VendorId = new HardwareId(0x10C4), ProductId = new HardwareId(0x8A7E) },
            new UsbDeviceRequestFilter { ClassCode = 0xFE, SubclassCode = 0x01, ProtocolCode = 0x02, SerialNumber = "AB12" },
        ]);

        Assert.Equal(
            """[{"vendorId":4292,"productId":35454},{"classCode":254,"subclassCode":1,"protocolCode":2,"serialNumber":"AB12"}]""",
            json);
    }

    [Fact]
    public void A_serial_number_with_a_quote_survives_the_round_trip()
    {
        var json = WebUsbJson.WriteFilters([new UsbDeviceRequestFilter { SerialNumber = "A\"B" }]);

        using var document = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("A\"B", document.RootElement[0].GetProperty("serialNumber").GetString());
    }

    [Fact]
    public void A_product_id_without_a_vendor_id_is_refused()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            WebUsbJson.WriteFilters([new UsbDeviceRequestFilter { ProductId = new HardwareId(1) }]));

        Assert.Contains("Filter 0", ex.Message);
    }

    [Fact]
    public void A_subclass_without_a_class_is_refused()
    {
        Assert.Throws<ArgumentException>(() =>
            WebUsbJson.WriteFilters([new UsbDeviceRequestFilter(), new UsbDeviceRequestFilter { SubclassCode = 1 }]));
    }

    [Fact]
    public void A_protocol_without_a_subclass_is_refused()
    {
        Assert.Throws<ArgumentException>(() =>
            WebUsbJson.WriteFilters([new UsbDeviceRequestFilter { ClassCode = 0xFF, ProtocolCode = 1 }]));
    }

    // ── Devices ────────────────────────────────────────────────────────

    [Fact]
    public void Reads_each_devices_handle_and_facts()
    {
        const string json = """
            [
              {"handle":3,"vendorId":4292,"productId":35454,"serialNumber":"AB12","productName":"Treehopper USB",
               "manufacturerName":"Treehopper","deviceClass":0,"deviceSubclass":0,"deviceProtocol":0},
              {"handle":7,"vendorId":1155,"productId":57105,"serialNumber":null,"productName":null,
               "manufacturerName":null,"deviceClass":239,"deviceSubclass":2,"deviceProtocol":1}
            ]
            """;

        var devices = WebUsbJson.ReadDevices(json);

        Assert.Equal(2, devices.Count);
        Assert.Equal(new GrantHandle(3), devices[0].Handle);
        Assert.Equal(
            new UsbGrantFacts
            {
                VendorId = 0x10C4, ProductId = 0x8A7E, SerialNumber = "AB12",
                ProductName = "Treehopper USB", ManufacturerName = "Treehopper",
            },
            devices[0].Facts);
        Assert.Equal(new GrantHandle(7), devices[1].Handle);
        Assert.Null(devices[1].Facts.SerialNumber);
        Assert.Equal((0xEF, 0x02, 0x01), (devices[1].Facts.DeviceClass, devices[1].Facts.DeviceSubclass, devices[1].Facts.DeviceProtocol));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[{\"handle\":1}]")]
    [InlineData("[{\"handle\":1,\"vendorId\":70000,\"productId\":1,\"deviceClass\":0,\"deviceSubclass\":0,\"deviceProtocol\":0}]")]
    [InlineData("not json")]
    public void A_device_list_in_another_shape_is_a_format_error(string json)
    {
        Assert.Throws<FormatException>(() => WebUsbJson.ReadDevices(json));
    }

    // ── Rejections ─────────────────────────────────────────────────────

    [Fact]
    public void Reads_a_rejections_name_and_message()
    {
        Assert.Equal(
            new WebUsbRejection("NotFoundError", "No device selected."),
            WebUsbJson.ReadRejection("""{"name":"NotFoundError","message":"No device selected."}"""));
    }

    [Fact]
    public void A_rejection_that_is_not_json_keeps_its_text_as_the_message()
    {
        Assert.Equal(new WebUsbRejection("Error", "boom"), WebUsbJson.ReadRejection("boom"));
    }
}
