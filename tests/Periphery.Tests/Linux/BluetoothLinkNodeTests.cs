using Periphery.Linux;

namespace Periphery.Tests.Linux;

/// <summary>ADR-0091 D7: which udev nodes leave every result.</summary>
public class BluetoothLinkNodeTests
{
    [Theory]
    [InlineData("bluetooth", "link", true)]   // hci0:42, one live connection
    [InlineData("bluetooth", "host", false)]  // hci0, the adapter
    [InlineData("bluetooth", null, false)]
    [InlineData("usb", "link", false)]
    [InlineData(null, "link", false)]
    public void OnlyABluetoothLinkIsLeftOut(string? subsystem, string? devtype, bool expected) =>
        Assert.Equal(expected, LinuxCategoryMap.IsBluetoothLink(subsystem, devtype));
}
