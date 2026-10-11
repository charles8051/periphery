namespace Periphery.Usb.Browser.Tests;

/// <summary>The <see cref="DeviceInfo"/> projection of ADR-0092 D5.</summary>
public class UsbGrantProjectionTests
{
    private static readonly UsbGrantFacts DfuBootloader = new()
    {
        VendorId = 0x0483,
        ProductId = 0xDF11,
        SerialNumber = "356A37563034",
        ProductName = "STM32  BOOTLOADER",
        ManufacturerName = "STMicroelectronics",
        DeviceClass = 0x00,
        DeviceSubclass = 0x00,
        DeviceProtocol = 0x00,
    };

    private static UsbGrant Grant(UsbGrantFacts facts)
    {
        var (state, _) = UsbGrantIds.Admit(UsbGrantRegistry.Empty, facts, new GrantHandle(1));
        return state.Grants[new GrantHandle(1)];
    }

    [Fact]
    public void Fills_the_fields_WebUSB_reports()
    {
        var info = UsbGrantProjection.ToDeviceInfo(Grant(DfuBootloader));

        Assert.Equal("webusb:0483:DF11:356A37563034", info.Id.Value);
        Assert.Equal("STM32  BOOTLOADER", info.Name);
        Assert.Equal("STMicroelectronics", info.Manufacturer);
        Assert.Equal(new HardwareId(0x0483), info.VendorId);
        Assert.Equal(new HardwareId(0xDF11), info.ProductId);
        Assert.Equal("356A37563034", info.SerialNumber);
        Assert.Equal(new UsbClassCode(0x00, 0x00, 0x00), info.UsbClassCode);
        Assert.Equal(BusType.USB, info.BusType);
        Assert.Equal(DeviceStatus.Unknown, info.Status);
    }

    [Fact]
    public void Carries_the_device_class_triple()
    {
        var facts = DfuBootloader with { DeviceClass = 0xEF, DeviceSubclass = 0x02, DeviceProtocol = 0x01 };

        var info = UsbGrantProjection.ToDeviceInfo(Grant(facts));

        Assert.Equal(new UsbClassCode(0xEF, 0x02, 0x01), info.UsbClassCode);
    }

    [Fact]
    public void Leaves_what_the_browser_has_no_source_for_at_its_default()
    {
        var info = UsbGrantProjection.ToDeviceInfo(Grant(DfuBootloader));

        Assert.Null(info.ParentId);
        Assert.Null(info.PortNumber);
        Assert.Null(info.LocationPath);
        Assert.Null(info.ContainerId);
        Assert.Null(info.Driver);
        Assert.Null(info.ClassGuid);
        Assert.Null(info.PortName);
        Assert.False(info.IsActive);
        Assert.Empty(info.Tags);
        Assert.Empty(info.Properties);
    }

    [Fact]
    public void An_empty_serial_projects_as_none()
    {
        var info = UsbGrantProjection.ToDeviceInfo(Grant(DfuBootloader with { SerialNumber = "" }));

        Assert.Null(info.SerialNumber);
        Assert.StartsWith(UsbGrantIds.SessionPrefix, info.Id.Value);
    }
}
