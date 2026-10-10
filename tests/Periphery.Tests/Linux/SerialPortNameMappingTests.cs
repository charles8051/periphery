using Periphery.Linux;

namespace Periphery.Tests.Linux;

/// <summary>Issue #326: a Linux tty carries its device node as <see cref="DeviceInfo.PortName"/>.</summary>
public class SerialPortNameMappingTests
{
    [Theory]
    [InlineData("tty", "/dev/ttyUSB0", "/dev/ttyUSB0")]
    [InlineData("tty", "/dev/ttyACM1", "/dev/ttyACM1")]
    [InlineData("tty", "/dev/ttyS0", "/dev/ttyS0")]
    [InlineData("tty", "ttyUSB0", "/dev/ttyUSB0")]   // a kernel uevent's DEVNAME is relative to /dev
    public void ATtyIsNamedByItsDeviceNode(string subsystem, string devname, string expected) =>
        Assert.Equal(new SerialPortName(expected), LinuxCategoryMap.GetPortName(subsystem, devname));

    [Theory]
    [InlineData("tty", null)]
    [InlineData("tty", "")]
    [InlineData("usb", "/dev/bus/usb/001/002")]
    [InlineData("video4linux", "/dev/video0")]
    [InlineData(null, "/dev/ttyUSB0")]
    public void AnythingElseHasNoPortName(string? subsystem, string? devname) =>
        Assert.Null(LinuxCategoryMap.GetPortName(subsystem, devname));
}
