using Periphery.Hid;
using Xunit;

namespace Periphery.Hid.Tests;

/// <summary>
/// Golden-descriptor tests for the pure report-descriptor parser the Linux
/// backend uses in place of Windows' <c>HidP_GetCaps</c>.
/// </summary>
public class HidReportDescriptorTests
{
    /// <summary>Standard HID boot keyboard descriptor (unnumbered reports).</summary>
    private static readonly byte[] BootKeyboard =
    [
        0x05, 0x01,       // Usage Page (Generic Desktop)
        0x09, 0x06,       // Usage (Keyboard)
        0xA1, 0x01,       // Collection (Application)
        0x05, 0x07,       //   Usage Page (Key Codes)
        0x19, 0xE0,       //   Usage Minimum (224)
        0x29, 0xE7,       //   Usage Maximum (231)
        0x15, 0x00,       //   Logical Minimum (0)
        0x25, 0x01,       //   Logical Maximum (1)
        0x75, 0x01,       //   Report Size (1)
        0x95, 0x08,       //   Report Count (8)
        0x81, 0x02,       //   Input (Data, Var, Abs)      — modifier bits
        0x95, 0x01,       //   Report Count (1)
        0x75, 0x08,       //   Report Size (8)
        0x81, 0x01,       //   Input (Const)               — reserved byte
        0x95, 0x05,       //   Report Count (5)
        0x75, 0x01,       //   Report Size (1)
        0x05, 0x08,       //   Usage Page (LEDs)
        0x19, 0x01,       //   Usage Minimum (1)
        0x29, 0x05,       //   Usage Maximum (5)
        0x91, 0x02,       //   Output (Data, Var, Abs)     — LED bits
        0x95, 0x01,       //   Report Count (1)
        0x75, 0x03,       //   Report Size (3)
        0x91, 0x01,       //   Output (Const)              — LED padding
        0x95, 0x06,       //   Report Count (6)
        0x75, 0x08,       //   Report Size (8)
        0x15, 0x00,       //   Logical Minimum (0)
        0x25, 0x65,       //   Logical Maximum (101)
        0x05, 0x07,       //   Usage Page (Key Codes)
        0x19, 0x00,       //   Usage Minimum (0)
        0x29, 0x65,       //   Usage Maximum (101)
        0x81, 0x00,       //   Input (Data, Array)         — key array
        0xC0,             // End Collection
    ];

    /// <summary>Standard HID boot mouse descriptor with a nested physical collection.</summary>
    private static readonly byte[] BootMouse =
    [
        0x05, 0x01,       // Usage Page (Generic Desktop)
        0x09, 0x02,       // Usage (Mouse)
        0xA1, 0x01,       // Collection (Application)
        0x09, 0x01,       //   Usage (Pointer)
        0xA1, 0x00,       //   Collection (Physical)
        0x05, 0x09,       //     Usage Page (Buttons)
        0x19, 0x01,       //     Usage Minimum (1)
        0x29, 0x03,       //     Usage Maximum (3)
        0x15, 0x00,       //     Logical Minimum (0)
        0x25, 0x01,       //     Logical Maximum (1)
        0x95, 0x03,       //     Report Count (3)
        0x75, 0x01,       //     Report Size (1)
        0x81, 0x02,       //     Input (Data, Var, Abs)    — buttons
        0x95, 0x01,       //     Report Count (1)
        0x75, 0x05,       //     Report Size (5)
        0x81, 0x01,       //     Input (Const)             — padding
        0x05, 0x01,       //     Usage Page (Generic Desktop)
        0x09, 0x30,       //     Usage (X)
        0x09, 0x31,       //     Usage (Y)
        0x09, 0x38,       //     Usage (Wheel)
        0x15, 0x81,       //     Logical Minimum (-127)
        0x25, 0x7F,       //     Logical Maximum (127)
        0x75, 0x08,       //     Report Size (8)
        0x95, 0x03,       //     Report Count (3)
        0x81, 0x06,       //     Input (Data, Var, Rel)    — X/Y/wheel
        0xC0,             //   End Collection
        0xC0,             // End Collection
    ];

    /// <summary>Synthetic UPS-style descriptor with numbered feature reports.</summary>
    private static readonly byte[] NumberedUps =
    [
        0x05, 0x84,       // Usage Page (Power Device)
        0x09, 0x04,       // Usage (UPS)
        0xA1, 0x01,       // Collection (Application)
        0x85, 0x01,       //   Report ID (1)
        0x09, 0x30,       //   Usage
        0x75, 0x08,       //   Report Size (8)
        0x95, 0x02,       //   Report Count (2)
        0xB1, 0x02,       //   Feature                     — report 1: 2 bytes
        0x85, 0x02,       //   Report ID (2)
        0x95, 0x05,       //   Report Count (5)
        0xB1, 0x02,       //   Feature                     — report 2: 5 bytes
        0x85, 0x03,       //   Report ID (3)
        0x95, 0x01,       //   Report Count (1)
        0x81, 0x02,       //   Input                       — report 3: 1 byte
        0xC0,             // End Collection
    ];

    [Fact]
    public void BootKeyboard_ParsesUsageAndLengths()
    {
        var info = HidReportDescriptor.Parse(BootKeyboard);

        Assert.Equal(0x01, info.UsagePage);
        Assert.Equal(0x06, info.Usage);
        Assert.False(info.UsesReportIds);
        Assert.Equal(8, info.MaxInputPayloadBytes);   // 8 + 8 + 48 bits
        Assert.Equal(1, info.MaxOutputPayloadBytes);  // 5 + 3 bits
        Assert.Equal(0, info.MaxFeaturePayloadBytes);
    }

    [Fact]
    public void BootMouse_CapturesTopLevelCollectionNotNested()
    {
        var info = HidReportDescriptor.Parse(BootMouse);

        Assert.Equal(0x01, info.UsagePage);
        Assert.Equal(0x02, info.Usage);               // Mouse, not Pointer (nested)
        Assert.False(info.UsesReportIds);
        Assert.Equal(4, info.MaxInputPayloadBytes);   // 3 + 5 + 24 bits
        Assert.Equal(0, info.MaxOutputPayloadBytes);
    }

    [Fact]
    public void NumberedReports_TracksMaxPerReportId()
    {
        var info = HidReportDescriptor.Parse(NumberedUps);

        Assert.Equal(0x84, info.UsagePage);
        Assert.Equal(0x04, info.Usage);
        Assert.True(info.UsesReportIds);
        Assert.Equal(5, info.MaxFeaturePayloadBytes); // Largest of report 1 (2) and 2 (5).
        Assert.Equal(1, info.MaxInputPayloadBytes);
        Assert.Equal(0, info.MaxOutputPayloadBytes);
    }

    [Fact]
    public void ExtendedUsage_OverridesUsagePage()
    {
        byte[] descriptor =
        [
            0x05, 0x01,                   // Usage Page (Generic Desktop)
            0x0B, 0x01, 0x00, 0x0C, 0x00, // Usage (extended: page 0x0C Consumer, usage 0x01)
            0xA1, 0x01,                   // Collection (Application)
            0x85, 0x05,                   //   Report ID (5)
            0x75, 0x01,                   //   Report Size (1)
            0x95, 0x10,                   //   Report Count (16)
            0x81, 0x02,                   //   Input — 16 bits
            0xC0,                         // End Collection
        ];

        var info = HidReportDescriptor.Parse(descriptor);

        Assert.Equal(0x0C, info.UsagePage);
        Assert.Equal(0x01, info.Usage);
        Assert.True(info.UsesReportIds);
        Assert.Equal(2, info.MaxInputPayloadBytes);
    }

    [Fact]
    public void PushPop_RestoresGlobalState()
    {
        byte[] descriptor =
        [
            0x05, 0x01,       // Usage Page (Generic Desktop)
            0x09, 0x05,       // Usage (Gamepad)
            0xA1, 0x01,       // Collection (Application)
            0x75, 0x08,       //   Report Size (8)
            0x95, 0x04,       //   Report Count (4)
            0xA4,             //   Push
            0x75, 0x10,       //     Report Size (16)
            0x95, 0x02,       //     Report Count (2)
            0x81, 0x02,       //     Input — 32 bits
            0xB4,             //   Pop (restores size 8, count 4)
            0x81, 0x02,       //   Input — 32 bits
            0xC0,             // End Collection
        ];

        var info = HidReportDescriptor.Parse(descriptor);

        Assert.Equal(8, info.MaxInputPayloadBytes);   // 32 + 32 bits, same (implicit) report.
        Assert.False(info.UsesReportIds);
    }

    [Fact]
    public void TruncatedDescriptor_ReturnsBestEffortWithoutThrowing()
    {
        // Boot keyboard cut mid-item: parser stops at the truncation point.
        var truncated = BootKeyboard.AsSpan(0, 7); // Ends inside "Usage Page (Key Codes)".
        var info = HidReportDescriptor.Parse(truncated);

        Assert.Equal(0x01, info.UsagePage);
        Assert.Equal(0x06, info.Usage);
        Assert.Equal(0, info.MaxInputPayloadBytes);
    }

    [Fact]
    public void EmptyDescriptor_ReturnsZeroes()
    {
        var info = HidReportDescriptor.Parse([]);

        Assert.Equal(0, info.UsagePage);
        Assert.Equal(0, info.Usage);
        Assert.False(info.UsesReportIds);
        Assert.Equal(0, info.MaxInputPayloadBytes);
        Assert.Equal(0, info.MaxOutputPayloadBytes);
        Assert.Equal(0, info.MaxFeaturePayloadBytes);
    }
}
