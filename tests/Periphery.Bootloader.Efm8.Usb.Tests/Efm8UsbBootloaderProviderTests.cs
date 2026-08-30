using Xunit;

namespace Periphery.Bootloader.Efm8.Usb.Tests;

/// <summary>Tests for the EFM8 <see cref="IBootloaderProvider"/>: VID/PID matching and identification mode.</summary>
public class Efm8UsbBootloaderProviderTests
{
    private static DeviceInfo Device(
        ushort vid, ushort pid,
        DeviceCategory category = DeviceCategory.Hid, BusType busType = BusType.HID) => new()
    {
        Id = $"{vid:X4}:{pid:X4}",
        VendorId = new HardwareId(vid),
        ProductId = new HardwareId(pid),
        Category = category,
        BusType = busType,
    };

    [Fact]
    public void CanHandle_MatchesEfm8HidBootloader()
    {
        var provider = new Efm8UsbBootloaderProvider();
        Assert.True(provider.CanHandle(Device(0x10C4, 0xEAC9)));
    }

    [Fact]
    public void CanHandle_RejectsUsbParentNodeSharingTheVidPid()
    {
        // A USB-HID bootloader enumerates as two nodes with the same VID/PID: the USB-bus parent
        // and the HID-bus child. Only the HID-bus child is openable; matching the USB parent too
        // would surface the bootloader as two targets in the GUI.
        var provider = new Efm8UsbBootloaderProvider();
        Assert.False(provider.CanHandle(Device(0x10C4, 0xEAC9, DeviceCategory.Usb, BusType.USB)));
    }

    [Theory]
    [InlineData(0x10C4, 0x8A7E)] // the Treehopper application (not the bootloader)
    [InlineData(0x0483, 0xDF11)] // an STM32 DFU device
    [InlineData(0x1234, 0x5678)]
    public void CanHandle_RejectsOtherDevices(ushort vid, ushort pid)
    {
        var provider = new Efm8UsbBootloaderProvider();
        Assert.False(provider.CanHandle(Device(vid, pid)));
    }

    [Fact]
    public void Identification_IsPassive()
    {
        // 0x10C4:0xEAC9 is itself the target (USB VID/PID), so EFM8 is autoflash-eligible.
        Assert.Equal(IdentificationMode.Passive, new Efm8UsbBootloaderProvider().Identification);
    }

    [Fact]
    public void Name_IsHumanReadable()
    {
        Assert.Equal("EFM8 USB-HID bootloader", new Efm8UsbBootloaderProvider().Name);
    }
}
