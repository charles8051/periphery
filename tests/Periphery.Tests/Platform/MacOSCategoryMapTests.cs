using System.Runtime.Versioning;
using Periphery.MacOS;

namespace Periphery.Tests;

[SupportedOSPlatform("macos")]
public class MacOSCategoryMapTests
{
    // ── Tier 1: Direct IOKit class name mapping ────────────────────────

    [Theory]
    [InlineData(DeviceCategory.Camera, MacOSCategoryMap.IOVideoDevice)]
    [InlineData(DeviceCategory.Ports, MacOSCategoryMap.IOSerialBSDClient)]
    public void GetIOKitClasses_Tier1Category_ReturnsExpectedClass(
        DeviceCategory category, string expectedClass)
    {
        var classes = MacOSCategoryMap.GetIOKitClasses(category);

        Assert.Contains(expectedClass, classes);
    }

    [Theory]
    [InlineData(MacOSCategoryMap.IOVideoDevice, DeviceCategory.Camera)]
    [InlineData(MacOSCategoryMap.IOSerialBSDClient, DeviceCategory.Ports)]
    public void ResolveCategory_Tier1Class_ReturnsExpectedCategory(
        string className, DeviceCategory expected)
    {
        var category = MacOSCategoryMap.ResolveCategory(className);

        Assert.Equal(expected, category);
    }

    // ── USB class code → category resolution ───────────────────────────
    // ADR-0051 demoted every USB-class category (Imaging 0x06, Printer 0x07,
    // smart-card 0x0B) to a capability tag, so ResolveUsbCategory maps no class
    // code to a category any more — every value resolves to null.

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x03)]
    [InlineData(0x06)]  // ADR-0051: 0x06 (Still Image) → Imaging tag, not a category
    [InlineData(0x07)]  // ADR-0051: 0x07 (Printer) → Printer tag, not a category
    [InlineData(0x09)]
    [InlineData(0x0B)]  // ADR-0051: 0x0B (CCID) → SmartCard tag, not a category
    [InlineData(0xFF)]
    [InlineData(null)]
    public void ResolveUsbCategory_AnyClassCode_ReturnsNull(int? usbClassCode)
    {
        var category = MacOSCategoryMap.ResolveUsbCategory(usbClassCode);

        Assert.Null(category);
    }

    // ── HID sensor category ────────────────────────────────────────────

    [Theory]
    [InlineData(0x20, 0x01)]
    [InlineData(0x20, 0x41)]
    [InlineData(0x20, null)]
    public void ResolveHidCategory_SensorUsagePage_NowReturnsHid(
        int usagePage, int? usage)
    {
        // ADR-0051: HID sensor usage page 0x20 no longer resolves to a Sensor
        // *category* — it stays Hid. SensorEnricher emits the Sensor *tag* from
        // DeviceInfo.HidUsagePage instead (see SensorEnricherTests).
        var category = MacOSCategoryMap.ResolveHidCategory(usagePage, usage);

        Assert.Equal(DeviceCategory.Hid, category);
    }

    [Fact]
    public void ResolveHidCategory_GenericDesktopKeyboard_ReturnsKeyboard()
    {
        var category = MacOSCategoryMap.ResolveHidCategory(0x01, 0x06);

        Assert.Equal(DeviceCategory.Keyboard, category);
    }

    [Fact]
    public void ResolveHidCategory_GenericDesktopMouse_ReturnsMouse()
    {
        var category = MacOSCategoryMap.ResolveHidCategory(0x01, 0x02);

        Assert.Equal(DeviceCategory.Mouse, category);
    }

    [Fact]
    public void ResolveHidCategory_UnrecognizedUsage_ReturnsHid()
    {
        var category = MacOSCategoryMap.ResolveHidCategory(0x0C, 0x01);

        Assert.Equal(DeviceCategory.Hid, category);
    }

    // ── All category includes new classes ──────────────────────────────

    [Fact]
    public void GetIOKitClasses_AllCategory_IncludesTier1Classes()
    {
        var classes = MacOSCategoryMap.GetIOKitClasses(null);

        Assert.Contains(MacOSCategoryMap.IOVideoDevice, classes);
        Assert.Contains(MacOSCategoryMap.IOSerialBSDClient, classes);
        Assert.Contains(MacOSCategoryMap.IOUSBSmartCardController, classes);
    }

    // ── Bus type inference ─────────────────────────────────────────────

    [Theory]
    [InlineData(MacOSCategoryMap.IOVideoDevice, BusType.USB)]
    [InlineData(MacOSCategoryMap.IOUSBSmartCardController, BusType.USB)]
    [InlineData(MacOSCategoryMap.IOSerialBSDClient, BusType.Unknown)]
    public void InferBusType_NewClasses_ReturnsExpectedType(
        string className, BusType expected)
    {
        var busType = MacOSCategoryMap.InferBusType(className);

        Assert.Equal(expected, busType);
    }

    // ── Existing mappings unchanged ────────────────────────────────────

    [Theory]
    [InlineData(DeviceCategory.Usb, MacOSCategoryMap.IOUSBDevice)]
    [InlineData(DeviceCategory.Bluetooth, MacOSCategoryMap.IOBluetoothDevice)]
    [InlineData(DeviceCategory.Network, MacOSCategoryMap.IONetworkInterface)]
    [InlineData(DeviceCategory.Display, MacOSCategoryMap.IODisplayConnect)]
    [InlineData(DeviceCategory.Hid, MacOSCategoryMap.IOHIDDevice)]
    [InlineData(DeviceCategory.Audio, MacOSCategoryMap.IOAudioDevice)]
    [InlineData(DeviceCategory.Storage, MacOSCategoryMap.IOMedia)]
    [InlineData(DeviceCategory.Battery, MacOSCategoryMap.AppleSmartBattery)]
    public void GetIOKitClasses_ExistingCategory_StillReturnsExpectedClass(
        DeviceCategory category, string expectedClass)
    {
        var classes = MacOSCategoryMap.GetIOKitClasses(category);

        Assert.Contains(expectedClass, classes);
    }
}
