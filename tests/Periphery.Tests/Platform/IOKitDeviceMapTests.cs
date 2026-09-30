using System.Collections.Immutable;
using System.Net.NetworkInformation;
using System.Runtime.Versioning;
using Periphery.MacOS;
using Periphery.MacOS.Core;

namespace Periphery.Tests;

/// <summary>
/// <see cref="IOKitDeviceMap"/> over registry properties. The HID and serial entries carry the
/// values a MacBook Air on macOS 26.4 published; the USB, battery and network entries are
/// synthetic. Runs on any host.
/// </summary>
[SupportedOSPlatform("macos")]
public class IOKitDeviceMapTests
{
    private static IOKitProperties Props(params (string Key, object Value)[] values) =>
        new(values.ToImmutableDictionary(v => v.Key, v => v.Value));

    // The internal keyboard's keyboard-usage HID node.
    private static readonly IOKitProperties InternalKeyboard = Props(
        ("Product", "Apple Internal Keyboard / Trackpad"),
        ("Manufacturer", "Apple Inc."),
        ("VendorID", 1452L),
        ("ProductID", 641L),
        ("PrimaryUsagePage", 1L),
        ("PrimaryUsage", 6L),
        ("IOClass", "AppleHIDTransportHIDDevice"),
        ("CFBundleIdentifier", "com.apple.driver.AppleHIDTransport"));

    [Fact]
    public void HidDevice_ReadsVendorProductAndManufacturer_FromTheHidKeys()
    {
        var device = IOKitDeviceMap.ToDeviceInfo("4301483987", MacOSCategoryMap.IOHIDDevice, InternalKeyboard);

        Assert.Equal("Apple Internal Keyboard / Trackpad", device.Name);
        Assert.Equal(DeviceCategory.Keyboard, device.Category);
        Assert.Equal((HardwareId)0x05AC, device.VendorId);
        Assert.Equal((HardwareId)0x0281, device.ProductId);
        Assert.Equal("Apple Inc.", device.Manufacturer);
        Assert.Equal(BusType.HID, device.BusType);
    }

    [Fact]
    public void HidDevice_IsActive_WithoutASessionId()
    {
        // No IOHIDDevice publishes sessionID (#203).
        Assert.True(IOKitDeviceMap.ToDeviceInfo("1", MacOSCategoryMap.IOHIDDevice, InternalKeyboard).IsActive);
    }

    [Fact]
    public void SerialClient_IsNamedAfterItsTty_AndActive()
    {
        var device = IOKitDeviceMap.ToDeviceInfo("4294968230", MacOSCategoryMap.IOSerialBSDClient, Props(
            ("IOTTYDevice", "debug-console"),
            ("IOCalloutDevice", "/dev/cu.debug-console"),
            ("IODialinDevice", "/dev/tty.debug-console"),
            ("IOClass", "IOSerialBSDClient"),
            ("CFBundleIdentifier", "com.apple.iokit.IOSerialFamily")));

        Assert.Equal("debug-console", device.Name);
        Assert.Equal(new SerialPortName("/dev/cu.debug-console"), device.PortName);
        Assert.Equal(DeviceCategory.Ports, device.Category);
        Assert.True(device.IsActive);
    }

    [Fact]
    public void UsbDevice_ReadsTheUsbKeys_AndIsActiveOnlyWithASessionId()
    {
        var usb = Props(
            ("USB Product Name", "Bench Board"),
            ("USB Vendor Name", "Example Vendor"),
            ("USB Serial Number", "SN0001"),
            ("idVendor", 0x10C4L),
            ("idProduct", 0x8A7EL));

        var idle = IOKitDeviceMap.ToDeviceInfo("7", MacOSCategoryMap.IOUSBHostDevice, usb);
        var open = IOKitDeviceMap.ToDeviceInfo("7", MacOSCategoryMap.IOUSBHostDevice,
            new IOKitProperties(usb.Values.Add("sessionID", 123456789L)));

        Assert.Equal("Bench Board", idle.Name);
        Assert.Equal("Example Vendor", idle.Manufacturer);
        Assert.Equal("SN0001", idle.SerialNumber);
        Assert.Equal((HardwareId)0x10C4, idle.VendorId);
        Assert.Equal((HardwareId)0x8A7E, idle.ProductId);
        Assert.False(idle.IsActive);
        Assert.True(open.IsActive);
    }

    [Theory]
    [InlineData(true, true, 80L, BatteryStatus.Charging)]
    [InlineData(false, true, 100L, BatteryStatus.Full)]
    [InlineData(false, true, 80L, BatteryStatus.NotCharging)]
    [InlineData(false, false, 80L, BatteryStatus.Discharging)]
    public void Battery_ReadsChargeAndStatus(bool charging, bool external, long current, BatteryStatus expected)
    {
        var device = IOKitDeviceMap.ToDeviceInfo("9", MacOSCategoryMap.AppleSmartBattery, Props(
            ("CurrentCapacity", current), ("MaxCapacity", 100L), ("IsCharging", charging), ("ExternalConnected", external)));

        Assert.Equal((int)current, device.BatteryChargePercent);
        Assert.Equal(expected, device.BatteryStatus);
        Assert.Equal(external, device.IsExternalPowerConnected);
        Assert.True(device.IsActive);
    }

    [Fact]
    public void NetworkInterface_ReadsItsMacAddress()
    {
        var device = IOKitDeviceMap.ToDeviceInfo("11", MacOSCategoryMap.IONetworkInterface, Props(
            ("IOMACAddress", new byte[] { 0x02, 0x00, 0x00, 0xAA, 0xBB, 0xCC }), ("BSD Name", "en0")));

        Assert.Equal(PhysicalAddress.Parse("02-00-00-AA-BB-CC"), device.MacAddress);
        Assert.True(device.IsActive);
    }

    [Fact]
    public void AnEntryWithNoKnownName_FallsBackToItsIOClass()
    {
        var device = IOKitDeviceMap.ToDeviceInfo("12", MacOSCategoryMap.IOHIDDevice, Props(("IOClass", "AppleSPUHIDDevice")));

        Assert.Equal("AppleSPUHIDDevice", device.Name);
        Assert.Null(device.VendorId);
    }

    [Fact]
    public void AnUnreadableEntry_KeepsItsClassAndCategory()
    {
        var device = IOKitDeviceMap.Unreadable("13", MacOSCategoryMap.IOSerialBSDClient);

        Assert.Equal(DeviceCategory.Ports, device.Category);
        Assert.Equal(MacOSCategoryMap.IOSerialBSDClient, device.IOServiceClass);
    }

    [Fact]
    public void EveryKeyTheMapReads_IsInTheListTheProviderReads()
    {
        // The provider snapshots only the listed keys, so a key the map reads but the lists omit is
        // always absent. Each fixture key above must be listed under its type.
        var listed = IOKitDeviceMap.StringKeys.Concat(IOKitDeviceMap.NumberKeys)
            .Concat(IOKitDeviceMap.BoolKeys).Concat(IOKitDeviceMap.DataKeys).ToHashSet();
        string[] used =
        [
            "Product", "Manufacturer", "VendorID", "ProductID", "PrimaryUsagePage", "PrimaryUsage", "IOClass",
            "CFBundleIdentifier", "IOTTYDevice", "IOCalloutDevice", "IODialinDevice", "USB Product Name",
            "USB Vendor Name", "USB Serial Number", "idVendor", "idProduct", "sessionID", "CurrentCapacity",
            "MaxCapacity", "IsCharging", "ExternalConnected", "IOMACAddress", "BSD Name", "SerialNumber",
        ];

        Assert.Empty(used.Where(key => !listed.Contains(key)));
    }
}
