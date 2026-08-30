using System.Runtime.Versioning;
using Periphery.Linux;

namespace Periphery.Tests;

[SupportedOSPlatform("linux")]
public class LinuxDeviceProviderTests
{
    [Theory]
    [InlineData(DeviceCategory.Usb, "usb")]
    [InlineData(DeviceCategory.Bluetooth, "bluetooth")]
    [InlineData(DeviceCategory.Network, "net")]
    [InlineData(DeviceCategory.Display, "drm")]
    [InlineData(DeviceCategory.Audio, "sound")]
    [InlineData(DeviceCategory.Storage, "block")]
    [InlineData(DeviceCategory.Ports, "tty")]
    [InlineData(DeviceCategory.Battery, "power_supply")]
    [InlineData(DeviceCategory.Camera, "video4linux")]
    public void GetSubsystems_KnownCategory_ReturnsExpectedSubsystem(DeviceCategory category, string expectedSubsystem)
    {
        var subsystems = LinuxCategoryMap.GetSubsystems(category);

        Assert.Contains(expectedSubsystem, subsystems);
    }

    [Fact]
    public void GetSubsystems_AllCategory_ReturnsEmpty()
    {
        var subsystems = LinuxCategoryMap.GetSubsystems(DeviceCategory.All);

        Assert.Empty(subsystems);
    }

    [Theory]
    [InlineData("usb", DeviceCategory.Usb)]
    [InlineData("bluetooth", DeviceCategory.Bluetooth)]
    [InlineData("net", DeviceCategory.Network)]
    [InlineData("drm", DeviceCategory.Display)]
    [InlineData("hid", DeviceCategory.Hid)]
    [InlineData("sound", DeviceCategory.Audio)]
    [InlineData("block", DeviceCategory.Storage)]
    [InlineData("tty", DeviceCategory.Ports)]
    [InlineData("power_supply", DeviceCategory.Battery)]
    [InlineData("video4linux", DeviceCategory.Camera)]
    // ADR-0051: `iio` is no longer mapped to a category — sensor-ness is the
    // Sensor tag now (SensorEnricher), so the subsystem resolves to All.
    [InlineData("iio", DeviceCategory.All)]
    public void ResolveCategory_KnownSubsystem_ReturnsExpectedCategory(string subsystem, DeviceCategory expected)
    {
        var category = LinuxCategoryMap.ResolveCategory(subsystem, IntPtr.Zero);

        Assert.Equal(expected, category);
    }

    [Fact]
    public void ResolveCategory_UnknownSubsystem_ReturnsAll()
    {
        var category = LinuxCategoryMap.ResolveCategory("unknown_subsystem", IntPtr.Zero);

        Assert.Equal(DeviceCategory.All, category);
    }

    [Fact]
    public void ResolveCategory_NullSubsystem_ReturnsAll()
    {
        var category = LinuxCategoryMap.ResolveCategory(null, IntPtr.Zero);

        Assert.Equal(DeviceCategory.All, category);
    }

    [Theory]
    [InlineData("usb", null, BusType.USB)]
    [InlineData("pci", null, BusType.PCI)]
    [InlineData("bluetooth", null, BusType.Bluetooth)]
    [InlineData("hid", null, BusType.HID)]
    [InlineData("scsi", null, BusType.SCSI)]
    [InlineData("ide", null, BusType.IDE)]
    [InlineData("acpi", null, BusType.ACPI)]
    public void InferBusType_WithIdBus_ReturnsExpectedType(string idBus, string? subsystem, BusType expected)
    {
        var busType = LinuxCategoryMap.InferBusType(idBus, subsystem);

        Assert.Equal(expected, busType);
    }

    [Theory]
    [InlineData("usb", BusType.USB)]
    [InlineData("bluetooth", BusType.Bluetooth)]
    [InlineData("hid", BusType.HID)]
    [InlineData("input", BusType.HID)]
    [InlineData("drm", BusType.Display)]
    [InlineData("sound", BusType.HDAudio)]
    public void InferBusType_WithoutIdBus_FallsBackToSubsystem(string subsystem, BusType expected)
    {
        var busType = LinuxCategoryMap.InferBusType(null, subsystem);

        Assert.Equal(expected, busType);
    }

    [Fact]
    public void InferBusType_UnknownBoth_ReturnsUnknown()
    {
        var busType = LinuxCategoryMap.InferBusType(null, "unknown");

        Assert.Equal(BusType.Unknown, busType);
    }
}
