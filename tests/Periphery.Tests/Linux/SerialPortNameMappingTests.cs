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
    [InlineData("tty", "/dev/rfcomm0", "/dev/rfcomm0")]
    [InlineData("tty", "/dev/ttyAMA0", "/dev/ttyAMA0")]
    [InlineData("tty", "/dev/tty64", "/dev/tty64")]   // past the last virtual console
    [InlineData("tty", "/dev/tty00", "/dev/tty00")]   // not a name the kernel gives a console
    public void ATtyIsNamedByItsDeviceNode(string subsystem, string devname, string expected) =>
        Assert.Equal(new SerialPortName(expected), LinuxCategoryMap.GetPortName(subsystem, devname));

    [Theory]
    [InlineData("tty", null)]
    [InlineData("tty", "")]
    [InlineData("usb", "/dev/bus/usb/001/002")]
    [InlineData("video4linux", "/dev/video0")]
    [InlineData(null, "/dev/ttyUSB0")]
    [InlineData("tty", "/dev/tty")]       // the controlling terminal
    [InlineData("tty", "/dev/tty0")]      // the active virtual console
    [InlineData("tty", "/dev/tty63")]
    [InlineData("tty", "tty1")]
    [InlineData("tty", "/dev/console")]
    [InlineData("tty", "/dev/ptmx")]
    [InlineData("tty", "/dev/ttyprintk")]
    public void AnythingElseHasNoPortName(string? subsystem, string? devname) =>
        Assert.Null(LinuxCategoryMap.GetPortName(subsystem, devname));
}
