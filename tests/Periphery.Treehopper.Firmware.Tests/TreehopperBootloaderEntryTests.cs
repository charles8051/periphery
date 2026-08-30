namespace Periphery.Treehopper.Firmware.Tests;

/// <summary>
/// Tests for <see cref="TreehopperBootloaderEntry.ExpectedBootloader"/>: the safety-gate filter
/// <see cref="Bootloader.BootloaderEntryOrchestrator"/> also uses to correlate a rebooted board's
/// re-enumeration.
/// </summary>
public class TreehopperBootloaderEntryTests
{
    private static DeviceInfo Device(
        ushort vid, ushort pid,
        DeviceCategory category = DeviceCategory.Hid, BusType busType = BusType.HID) => new()
    {
        Id = $"{vid:X4}:{pid:X4}:{category}",
        VendorId = new HardwareId(vid),
        ProductId = new HardwareId(pid),
        Category = category,
        BusType = busType,
    };

    [Fact]
    public void ExpectedBootloader_MatchesTheHidBusChild()
    {
        var entry = new TreehopperBootloaderEntry();
        Assert.True(entry.ExpectedBootloader.Matches(Device(0x10C4, 0xEAC9)));
    }

    [Fact]
    public void ExpectedBootloader_RejectsTheUsbParentNodeSharingTheVidPid()
    {
        // periphery#247: a USB-HID bootloader enumerates as two PnP nodes sharing the same VID/PID
        // - the USB-bus parent and its HID-bus child - and only the HID-bus child is something
        // HidDevice.OpenAsync can open. Standalone callers (TreehopperFirmwareUpdate.ReflashAsync,
        // VerifyFromFileAsync) ride a bare DeviceWatcherWaitSource over this filter with no upstream
        // device-class pre-filter, so a VID/PID-only ExpectedBootloader let FirstAppearance
        // correlation grab the unopenable USB parent node just as readily as the HID child -
        // deterministically stranding the board in the bootloader on open, not as a rare race.
        // Mirrors Efm8UsbBootloaderProviderTests.CanHandle_RejectsUsbParentNodeSharingTheVidPid,
        // which guards the identical ambiguity on the flash-side registry match.
        var entry = new TreehopperBootloaderEntry();
        Assert.False(entry.ExpectedBootloader.Matches(Device(0x10C4, 0xEAC9, DeviceCategory.Usb, BusType.USB)));
    }

    [Theory]
    [InlineData(0x10C4, 0x8A7E)] // the Treehopper application (not the bootloader)
    [InlineData(0x0483, 0xDF11)] // an STM32 DFU device
    [InlineData(0x1234, 0x5678)]
    public void ExpectedBootloader_RejectsOtherDevices(ushort vid, ushort pid)
    {
        var entry = new TreehopperBootloaderEntry();
        Assert.False(entry.ExpectedBootloader.Matches(Device(vid, pid)));
    }

    [Fact]
    public void CanEnter_MatchesTheTreehopperApplicationVidPid()
    {
        var entry = new TreehopperBootloaderEntry();
        Assert.True(entry.CanEnter(Device(0x10C4, 0x8A7E, DeviceCategory.Usb, BusType.USB)));
    }

    [Fact]
    public void CanEnter_RejectsTheBootloaderVidPid()
    {
        var entry = new TreehopperBootloaderEntry();
        Assert.False(entry.CanEnter(Device(0x10C4, 0xEAC9)));
    }

    [Fact]
    public void CanVerify_IsTrue()
    {
        // periphery#246: Efm8HidProgrammer has no in-session read-back, so Treehopper opts into
        // BootloaderEntryOrchestrator.RunWithVerificationAsync's automatic post-flash confirmation.
        var entry = new TreehopperBootloaderEntry();
        Assert.True(entry.CanVerify);
    }
}
