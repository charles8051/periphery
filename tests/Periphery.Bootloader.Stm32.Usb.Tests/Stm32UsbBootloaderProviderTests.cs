namespace Periphery.Bootloader.Stm32.Usb.Tests;

public class Stm32UsbBootloaderProviderTests
{
    [Fact]
    public void CanHandle_matches_only_the_stm32_dfu_vid_pid()
    {
        var provider = new Stm32UsbBootloaderProvider();

        Assert.Equal("STM32 USB DFU", provider.Name);
        Assert.True(provider.CanHandle(new DeviceInfo
        {
            Id = "stm32", VendorId = new HardwareId(0x0483), ProductId = new HardwareId(0xDF11),
        }));
        Assert.False(provider.CanHandle(new DeviceInfo
        {
            Id = "efm8", VendorId = new HardwareId(0x10C4), ProductId = new HardwareId(0xEAC9),
        }));
        Assert.False(provider.CanHandle(new DeviceInfo { Id = "no-ids" }));
    }
}
