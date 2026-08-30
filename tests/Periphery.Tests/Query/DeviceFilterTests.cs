using System.Collections.Immutable;

namespace Periphery.Tests;

public class DeviceFilterTests
{
    private static DeviceInfo MakeDevice(
        string id = "test",
        string? name = null,
        string? manufacturer = null,
        HardwareId? vendorId = null,
        HardwareId? productId = null,
        bool isActive = false,
        DeviceCategory category = DeviceCategory.All) => new()
    {
        Id = id,
        Name = name,
        Manufacturer = manufacturer,
        VendorId = vendorId,
        ProductId = productId,
        IsActive = isActive,
        Category = category,
    };

    // ── No predicates ──────────────────────────────────────────────────

    [Fact]
    public void EmptyFilter_MatchesEverything()
    {
        var filter = new DeviceFilter();
        Assert.True(filter.Matches(MakeDevice()));
    }

    // ── Category ───────────────────────────────────────────────────────

    [Fact]
    public void OfCategory_UpdatesCategory()
    {
        var filter = new DeviceFilter();
        filter.OfCategory(DeviceCategory.Usb);
        Assert.Equal(DeviceCategory.Usb, filter.Category);
    }

    // ── Where ──────────────────────────────────────────────────────────

    [Fact]
    public void Where_MatchingPredicate_ReturnsTrue()
    {
        var filter = new DeviceFilter();
        filter.Where(d => d.Id == "test");
        Assert.True(filter.Matches(MakeDevice(id: "test")));
    }

    [Fact]
    public void Where_NonMatchingPredicate_ReturnsFalse()
    {
        var filter = new DeviceFilter();
        filter.Where(d => d.Id == "other");
        Assert.False(filter.Matches(MakeDevice(id: "test")));
    }

    [Fact]
    public void Where_MultiplePredicates_AllMustMatch()
    {
        var filter = new DeviceFilter();
        filter.Where(d => d.Name is not null);
        filter.Where(d => d.IsActive);

        Assert.False(filter.Matches(MakeDevice(name: "Mouse", isActive: false)));
        Assert.False(filter.Matches(MakeDevice(name: null, isActive: true)));
        Assert.True(filter.Matches(MakeDevice(name: "Mouse", isActive: true)));
    }

    // ── WithName ───────────────────────────────────────────────────────

    [Fact]
    public void WithName_ContainsCaseInsensitive()
    {
        var filter = new DeviceFilter();
        filter.WithName("mouse");

        Assert.True(filter.Matches(MakeDevice(name: "USB Mouse")));
        Assert.True(filter.Matches(MakeDevice(name: "MOUSE")));
        Assert.False(filter.Matches(MakeDevice(name: "Keyboard")));
    }

    [Fact]
    public void WithName_NullName_ReturnsFalse()
    {
        var filter = new DeviceFilter();
        filter.WithName("mouse");
        Assert.False(filter.Matches(MakeDevice(name: null)));
    }

    // ── WithUsbId (HardwareId) ─────────────────────────────────────────

    [Fact]
    public void WithUsbId_VidOnly_MatchesAnyPid()
    {
        var filter = new DeviceFilter();
        filter.WithUsbId(new HardwareId(0x046D));

        Assert.True(filter.Matches(MakeDevice(vendorId: new HardwareId(0x046D), productId: new HardwareId(0xC077))));
        Assert.True(filter.Matches(MakeDevice(vendorId: new HardwareId(0x046D), productId: new HardwareId(0x0001))));
        Assert.False(filter.Matches(MakeDevice(vendorId: new HardwareId(0x1234))));
    }

    [Fact]
    public void WithUsbId_VidAndPid_MatchesExactPair()
    {
        var filter = new DeviceFilter();
        filter.WithUsbId(new HardwareId(0x046D), new HardwareId(0xC077));

        Assert.True(filter.Matches(MakeDevice(vendorId: new HardwareId(0x046D), productId: new HardwareId(0xC077))));
        Assert.False(filter.Matches(MakeDevice(vendorId: new HardwareId(0x046D), productId: new HardwareId(0x0001))));
    }

    [Fact]
    public void WithUsbId_NullVendorId_ReturnsFalse()
    {
        var filter = new DeviceFilter();
        filter.WithUsbId(new HardwareId(0x046D));
        Assert.False(filter.Matches(MakeDevice(vendorId: null)));
    }

    // ── WithUsbId (string) ─────────────────────────────────────────────

    [Fact]
    public void WithUsbId_String_ParsesAndMatches()
    {
        var filter = new DeviceFilter();
        filter.WithUsbId("046D", "C077");
        Assert.True(filter.Matches(MakeDevice(vendorId: new HardwareId(0x046D), productId: new HardwareId(0xC077))));
    }

    [Fact]
    public void WithUsbId_InvalidVidString_MatchesNothing()
    {
        var filter = new DeviceFilter();
        filter.WithUsbId("ZZZZ");
        Assert.False(filter.Matches(MakeDevice(vendorId: new HardwareId(0x046D))));
    }

    [Fact]
    public void WithUsbId_InvalidPidString_MatchesNothing()
    {
        var filter = new DeviceFilter();
        filter.WithUsbId("046D", "ZZZZ");
        Assert.False(filter.Matches(MakeDevice(vendorId: new HardwareId(0x046D), productId: new HardwareId(0xC077))));
    }

    // ── ByManufacturer ─────────────────────────────────────────────────

    [Fact]
    public void ByManufacturer_ContainsCaseInsensitive()
    {
        var filter = new DeviceFilter();
        filter.ByManufacturer("intel");

        Assert.True(filter.Matches(MakeDevice(manufacturer: "Intel Corporation")));
        Assert.True(filter.Matches(MakeDevice(manufacturer: "INTEL")));
        Assert.False(filter.Matches(MakeDevice(manufacturer: "AMD")));
    }

    [Fact]
    public void ByManufacturer_NullManufacturer_ReturnsFalse()
    {
        var filter = new DeviceFilter();
        filter.ByManufacturer("intel");
        Assert.False(filter.Matches(MakeDevice(manufacturer: null)));
    }

    // ── Present ─────────────────────────────────────────────────────────

    [Fact]
    public void Active_True_MatchesActiveDevices()
    {
        var filter = new DeviceFilter();
        filter.Active();

        Assert.True(filter.Matches(MakeDevice(isActive: true)));
        Assert.False(filter.Matches(MakeDevice(isActive: false)));
    }

    [Fact]
    public void Active_False_MatchesInactiveDevices()
    {
        var filter = new DeviceFilter();
        filter.Active(false);

        Assert.False(filter.Matches(MakeDevice(isActive: true)));
        Assert.True(filter.Matches(MakeDevice(isActive: false)));
    }

    // ── Fluent chaining ────────────────────────────────────────────────

    [Fact]
    public void FluentChaining_ReturnsSameInstance()
    {
        var filter = new DeviceFilter();

        Assert.Same(filter, filter.Where(_ => true));
        Assert.Same(filter, filter.WithName("test"));
        Assert.Same(filter, filter.ByManufacturer("test"));
        Assert.Same(filter, filter.Active());
    }

    [Fact]
    public void FluentChaining_MultipleFilters_AllApplied()
    {
        var filter = new DeviceFilter();
        filter.WithName("mouse")
              .ByManufacturer("logitech")
              .Active();

        var device = MakeDevice(
            name: "Logitech Mouse",
            manufacturer: "Logitech Inc.",
            isActive: true);

        Assert.True(filter.Matches(device));
    }

    [Fact]
    public void FluentChaining_OneFailingFilter_ReturnsFalse()
    {
        var filter = new DeviceFilter();
        filter.WithName("mouse")
              .ByManufacturer("logitech")
              .Active();

        // Name matches, manufacturer matches, but not present
        var device = MakeDevice(
            name: "Logitech Mouse",
            manufacturer: "Logitech Inc.",
            isActive: false);

        Assert.False(filter.Matches(device));
    }

    // ── Input validation ───────────────────────────────────────────────

    [Fact]
    public void Where_NullPredicate_ThrowsArgumentNullException()
    {
        var filter = new DeviceFilter();
        Assert.Throws<ArgumentNullException>(() => filter.Where(null!));
    }

    [Fact]
    public void WithName_NullText_ThrowsArgumentNullException()
    {
        var filter = new DeviceFilter();
        Assert.Throws<ArgumentNullException>(() => filter.WithName(null!));
    }

    [Fact]
    public void WithName_EmptyText_ThrowsArgumentException()
    {
        var filter = new DeviceFilter();
        Assert.Throws<ArgumentException>(() => filter.WithName(""));
    }

    [Fact]
    public void WithName_WhitespaceText_ThrowsArgumentException()
    {
        var filter = new DeviceFilter();
        Assert.Throws<ArgumentException>(() => filter.WithName("   "));
    }

    [Fact]
    public void ByManufacturer_NullText_ThrowsArgumentNullException()
    {
        var filter = new DeviceFilter();
        Assert.Throws<ArgumentNullException>(() => filter.ByManufacturer(null!));
    }

    [Fact]
    public void ByManufacturer_EmptyText_ThrowsArgumentException()
    {
        var filter = new DeviceFilter();
        Assert.Throws<ArgumentException>(() => filter.ByManufacturer(""));
    }

    [Fact]
    public void ByManufacturer_WhitespaceText_ThrowsArgumentException()
    {
        var filter = new DeviceFilter();
        Assert.Throws<ArgumentException>(() => filter.ByManufacturer("   "));
    }

    // ── Edge cases ─────────────────────────────────────────────────────

    [Fact]
    public void Matches_WithManyPredicates_EvaluatesAll()
    {
        var filter = new DeviceFilter();
        
        // Add 10 predicates
        for (int i = 0; i < 10; i++)
        {
            filter.Where(d => d.Id.Value.Length > 0);
        }

        Assert.True(filter.Matches(MakeDevice(id: "test")));
    }

    [Fact]
    public void Matches_WithContradictoryPredicates_ReturnsFalse()
    {
        var filter = new DeviceFilter();
        filter.Active(true);
        filter.Active(false);

        // Can't be both present and absent
        Assert.False(filter.Matches(MakeDevice(isActive: true)));
        Assert.False(filter.Matches(MakeDevice(isActive: false)));
    }

    [Fact]
    public void WithName_PartialMatch_Works()
    {
        var filter = new DeviceFilter();
        filter.WithName("USB");

        Assert.True(filter.Matches(MakeDevice(name: "USB Composite Device")));
        Assert.True(filter.Matches(MakeDevice(name: "Standard USB Hub")));
        Assert.False(filter.Matches(MakeDevice(name: "Ethernet Adapter")));
    }

    [Fact]
    public void ByManufacturer_PartialMatch_Works()
    {
        var filter = new DeviceFilter();
        filter.ByManufacturer("Corp");

        Assert.True(filter.Matches(MakeDevice(manufacturer: "Intel Corporation")));
        Assert.True(filter.Matches(MakeDevice(manufacturer: "AMD Corp.")));
        Assert.False(filter.Matches(MakeDevice(manufacturer: "Microsoft")));
    }

    [Fact]
    public void Category_PreservedThroughChaining()
    {
        var filter = (new DeviceFilter()).OfCategory(DeviceCategory.Usb);
        filter.WithName("test");
        
        Assert.Equal(DeviceCategory.Usb, filter.Category);
    }

    [Fact]
    public void OfCategory_OverwritesPreviousCategory()
    {
        var filter = (new DeviceFilter()).OfCategory(DeviceCategory.Usb);
        Assert.Equal(DeviceCategory.Usb, filter.Category);
        
        filter.OfCategory(DeviceCategory.Bluetooth);
        Assert.Equal(DeviceCategory.Bluetooth, filter.Category);
    }

    // ── StringComparison modes ─────────────────────────────────────────

    [Fact]
    public void WithName_CaseSensitive_RespectsCasing()
    {
        var filter = new DeviceFilter();
        filter.WithName("Mouse", StringComparison.Ordinal);

        Assert.True(filter.Matches(MakeDevice(name: "Mouse")));
        Assert.False(filter.Matches(MakeDevice(name: "mouse")));
        Assert.False(filter.Matches(MakeDevice(name: "MOUSE")));
    }

    [Fact]
    public void ByManufacturer_CaseSensitive_RespectsCasing()
    {
        var filter = new DeviceFilter();
        filter.ByManufacturer("Intel", StringComparison.Ordinal);

        Assert.True(filter.Matches(MakeDevice(manufacturer: "Intel Corporation")));
        Assert.False(filter.Matches(MakeDevice(manufacturer: "INTEL Corporation")));
    }

    // ── WithUsbSpeed ───────────────────────────────────────────────────

    [Fact]
    public void WithUsbSpeed_MatchesExactSpeed()
    {
        var filter = new DeviceFilter();
        filter.WithUsbSpeed(UsbSpeed.High);

        var usb2 = new DeviceInfo { Id = "test", UsbSpeed = UsbSpeed.High };
        var usb3 = new DeviceInfo { Id = "test", UsbSpeed = UsbSpeed.Super };
        var nonUsb = new DeviceInfo { Id = "test" };

        Assert.True(filter.Matches(usb2));
        Assert.False(filter.Matches(usb3));
        Assert.False(filter.Matches(nonUsb));
    }

    // ── WithParent ─────────────────────────────────────────────────────

    [Fact]
    public void WithParent_MatchesExactParentId()
    {
        var filter = new DeviceFilter();
        filter.WithParent("USB\\ROOT_HUB30\\4&1234");

        var child = new DeviceInfo { Id = "child", ParentId = "USB\\ROOT_HUB30\\4&1234" };
        var other = new DeviceInfo { Id = "other", ParentId = "USB\\ROOT_HUB30\\4&5678" };
        var root = new DeviceInfo { Id = "root" };

        Assert.True(filter.Matches(child));
        Assert.False(filter.Matches(other));
        Assert.False(filter.Matches(root));
    }

    [Fact]
    public void WithParent_NullOrWhitespace_ThrowsArgumentException()
    {
        var filter = new DeviceFilter();
        Assert.Throws<ArgumentNullException>(() => filter.WithParent(null!));
        Assert.Throws<ArgumentException>(() => filter.WithParent(""));
        Assert.Throws<ArgumentException>(() => filter.WithParent("   "));
    }

    // ── WithPortName ───────────────────────────────────────────────────

    [Fact]
    public void WithPortName_MatchesCaseInsensitive()
    {
        var filter = new DeviceFilter();
        filter.WithPortName("COM3");

        var com3 = new DeviceInfo { Id = "test", PortName = new SerialPortName("COM3") };
        var com3Lower = new DeviceInfo { Id = "test", PortName = new SerialPortName("com3") };
        var com4 = new DeviceInfo { Id = "test", PortName = new SerialPortName("COM4") };
        var noPort = new DeviceInfo { Id = "test" };

        Assert.True(filter.Matches(com3));
        Assert.True(filter.Matches(com3Lower));
        Assert.False(filter.Matches(com4));
        Assert.False(filter.Matches(noPort));
    }

    [Fact]
    public void WithPortName_NullOrWhitespace_ThrowsArgumentException()
    {
        var filter = new DeviceFilter();
        Assert.Throws<ArgumentNullException>(() => filter.WithPortName(null!));
        Assert.Throws<ArgumentException>(() => filter.WithPortName(""));
        Assert.Throws<ArgumentException>(() => filter.WithPortName("   "));
    }

    // ── WithBatteryStatus ──────────────────────────────────────────────

    [Fact]
    public void WithBatteryStatus_MatchesExactStatus()
    {
        var filter = new DeviceFilter();
        filter.WithBatteryStatus(BatteryStatus.Charging);

        var charging = new DeviceInfo { Id = "bat", BatteryStatus = BatteryStatus.Charging };
        var discharging = new DeviceInfo { Id = "bat", BatteryStatus = BatteryStatus.Discharging };
        var noBattery = new DeviceInfo { Id = "test" };

        Assert.True(filter.Matches(charging));
        Assert.False(filter.Matches(discharging));
        Assert.False(filter.Matches(noBattery));
    }

    // ── PhysicalOnly ───────────────────────────────────────────────────

    [Fact]
    public void PhysicalOnly_ExcludesSoftwareDevices()
    {
        var filter = new DeviceFilter();
        filter.PhysicalOnly();

        var usb = new DeviceInfo { Id = "usb", BusType = BusType.USB };
        var pci = new DeviceInfo { Id = "pci", BusType = BusType.PCI };
        var software = new DeviceInfo { Id = "swd", BusType = BusType.Software };
        var unknown = new DeviceInfo { Id = "unk", BusType = BusType.Unknown };

        Assert.True(filter.Matches(usb));
        Assert.True(filter.Matches(pci));
        Assert.False(filter.Matches(software));
        Assert.True(filter.Matches(unknown));
    }

    // ── VirtualOnly ────────────────────────────────────────────────────

    [Fact]
    public void VirtualOnly_IncludesOnlySoftwareDevices()
    {
        var filter = new DeviceFilter();
        filter.VirtualOnly();

        var usb = new DeviceInfo { Id = "usb", BusType = BusType.USB };
        var pci = new DeviceInfo { Id = "pci", BusType = BusType.PCI };
        var software = new DeviceInfo { Id = "swd", BusType = BusType.Software };
        var unknown = new DeviceInfo { Id = "unk", BusType = BusType.Unknown };

        Assert.False(filter.Matches(usb));
        Assert.False(filter.Matches(pci));
        Assert.True(filter.Matches(software));
        Assert.False(filter.Matches(unknown));
    }

    // ── WithId ─────────────────────────────────────────────────────────

    [Fact]
    public void WithId_IsCaseInsensitive()
    {
        // Device instance IDs are case-insensitive (Windows); a device that
        // re-enumerates with different casing must still match a pinned Id.
        var filter = new DeviceFilter();
        filter.WithId(@"USB\VID_10C4&PID_8A7E\JQ1KM1AI");

        Assert.True(filter.Matches(new DeviceInfo { Id = @"USB\VID_10C4&PID_8A7E\jQ1KM1Ai" }));
        Assert.True(filter.Matches(new DeviceInfo { Id = @"USB\VID_10C4&PID_8A7E\JQ1KM1AI" }));
        Assert.False(filter.Matches(new DeviceInfo { Id = @"USB\VID_10C4&PID_8A7E\OTHER" }));
    }

    // ── WithIdStartsWith ───────────────────────────────────────────────

    [Fact]
    public void WithIdStartsWith_MatchesPrefix()
    {
        var filter = new DeviceFilter();
        filter.WithIdStartsWith(@"DISPLAY\MS_0003\");

        Assert.True(filter.Matches(new DeviceInfo { Id = @"DISPLAY\MS_0003\4&1a2b3c4d&0&UID265988" }));
        Assert.True(filter.Matches(new DeviceInfo { Id = @"DISPLAY\MS_0003\7&ABCDEF12&1&UID000001" }));
        Assert.False(filter.Matches(new DeviceInfo { Id = @"DISPLAY\DELA1234\4&1a2b3c4d&0&UID198147" }));
    }

    [Fact]
    public void WithIdStartsWith_DefaultIsCaseInsensitive()
    {
        // Default comparison flipped to case-insensitive (instance ids are
        // case-insensitive by contract). A lowercase prefix matches an
        // uppercase id without the caller passing a StringComparison.
        var filter = new DeviceFilter();
        filter.WithIdStartsWith(@"display\ms_0003\");

        Assert.True(filter.Matches(new DeviceInfo { Id = @"DISPLAY\MS_0003\4&1a2b3c4d&0&UID265988" }));
    }

    [Fact]
    public void WithIdStartsWith_ExplicitOrdinal_IsCaseSensitive()
    {
        // The comparison parameter is still honoured when set explicitly.
        var filter = new DeviceFilter();
        filter.WithIdStartsWith(@"display\", StringComparison.Ordinal);

        Assert.False(filter.Matches(new DeviceInfo { Id = @"DISPLAY\MS_0003\foo" }));
    }

    [Fact]
    public void NullId_RejectedAtConstruction()
    {
        // DeviceInfo.Id is a non-nullable DeviceId that enforces the
        // non-null/non-empty instance-id invariant at the value boundary, so a
        // device with a null id is unrepresentable — the filter never has to
        // defend against one. (Previously Id was a nullable string and the
        // WithIdStartsWith predicate guarded against null with d.Id?.StartsWith.)
        Assert.Throws<ArgumentNullException>(
            () => new DeviceInfo { Id = null! });
    }

    [Fact]
    public void WithIdStartsWith_WhitespacePrefix_Throws()
    {
        var filter = new DeviceFilter();
        Assert.Throws<ArgumentException>(() => filter.WithIdStartsWith(" "));
    }

    [Fact]
    public void WithIdStartsWith_RespectsComparison()
    {
        var filter = new DeviceFilter();
        filter.WithIdStartsWith("display\\", StringComparison.OrdinalIgnoreCase);

        Assert.True(filter.Matches(new DeviceInfo { Id = @"DISPLAY\MS_0003\foo" }));
    }

    // ── WithContainerId ────────────────────────────────────────────────

    [Fact]
    public void WithContainerId_MatchesExactGuid()
    {
        var target = new Guid("ec174e30-6e00-5cd2-a2e8-f977389a0c6b");
        var other = new Guid("255e3417-4985-11f1-8d0b-103d1c90b3f6");

        var filter = new DeviceFilter();
        filter.WithContainerId(target);

        Assert.True(filter.Matches(new DeviceInfo { Id = "a", ContainerId = target }));
        Assert.False(filter.Matches(new DeviceInfo { Id = "b", ContainerId = other }));
        Assert.False(filter.Matches(new DeviceInfo { Id = "c", ContainerId = null }));
    }

    // ── Tags (ADR-0047) ────────────────────────────────────────────────

    private static DeviceInfo Tagged(params string[] tags) => new()
    {
        Id = "tagged",
        Tags = [.. tags],
    };

    [Fact]
    public void WithTag_MatchesWhenTagPresent()
    {
        var filter = new DeviceFilter().WithTag(DeviceTags.Battery);
        Assert.True(filter.Matches(Tagged(DeviceTags.Battery)));
        Assert.True(filter.Matches(Tagged(DeviceTags.Hid, DeviceTags.Battery)));
    }

    [Fact]
    public void WithTag_DoesNotMatchWhenTagAbsent()
    {
        var filter = new DeviceFilter().WithTag(DeviceTags.Battery);
        Assert.False(filter.Matches(Tagged(DeviceTags.Hid)));
        Assert.False(filter.Matches(Tagged())); // empty tag set
    }

    [Fact]
    public void WithTag_IsOrdinalCaseSensitive()
    {
        // Spelling matters — typos can't silently match. See DeviceTags
        // for the canonical constants.
        var filter = new DeviceFilter().WithTag(DeviceTags.Battery);
        Assert.False(filter.Matches(Tagged("battery"))); // lowercase ≠ Battery
        Assert.False(filter.Matches(Tagged("BATTERY")));
    }

    [Fact]
    public void WithTag_NullOrWhitespace_Throws()
    {
        var filter = new DeviceFilter();
        Assert.Throws<ArgumentNullException>(() => filter.WithTag(null!));
        Assert.Throws<ArgumentException>(() => filter.WithTag(""));
        Assert.Throws<ArgumentException>(() => filter.WithTag("   "));
    }

    [Fact]
    public void WithAllTags_RequiresEveryTag()
    {
        var filter = new DeviceFilter().WithAllTags(DeviceTags.Hid, DeviceTags.Battery);

        Assert.True(filter.Matches(Tagged(DeviceTags.Hid, DeviceTags.Battery)));
        Assert.True(filter.Matches(Tagged(DeviceTags.Hid, DeviceTags.Battery, DeviceTags.Audio)));
        Assert.False(filter.Matches(Tagged(DeviceTags.Hid)));      // missing Battery
        Assert.False(filter.Matches(Tagged(DeviceTags.Battery)));  // missing Hid
        Assert.False(filter.Matches(Tagged()));                    // missing both
    }

    [Fact]
    public void WithAllTags_EmptyArgs_MatchesEverything()
    {
        // Empty AND is vacuously true; predicate count stays unchanged.
        var filter = new DeviceFilter().WithAllTags();
        Assert.True(filter.Matches(Tagged()));
        Assert.True(filter.Matches(Tagged(DeviceTags.Hid)));
    }

    [Fact]
    public void WithAllTags_NullArray_Throws()
    {
        var filter = new DeviceFilter();
        Assert.Throws<ArgumentNullException>(() => filter.WithAllTags(null!));
    }

    [Fact]
    public void WithAnyTag_MatchesIfAnyPresent()
    {
        var filter = new DeviceFilter().WithAnyTag(DeviceTags.Hid, DeviceTags.Audio);

        Assert.True(filter.Matches(Tagged(DeviceTags.Hid)));
        Assert.True(filter.Matches(Tagged(DeviceTags.Audio)));
        Assert.True(filter.Matches(Tagged(DeviceTags.Hid, DeviceTags.Audio)));
        Assert.False(filter.Matches(Tagged(DeviceTags.Battery)));  // neither requested
        Assert.False(filter.Matches(Tagged()));
    }

    [Fact]
    public void WithAnyTag_EmptyArgs_MatchesNothing()
    {
        // Empty OR is vacuously false — opposite of WithAllTags. Spelled
        // out so a no-arg call doesn't accidentally turn into a pass-through.
        var filter = new DeviceFilter().WithAnyTag();
        Assert.False(filter.Matches(Tagged(DeviceTags.Hid)));
        Assert.False(filter.Matches(Tagged()));
    }

    [Fact]
    public void WithAnyTag_NullArray_Throws()
    {
        var filter = new DeviceFilter();
        Assert.Throws<ArgumentNullException>(() => filter.WithAnyTag(null!));
    }

    [Fact]
    public void WithTag_ComposesWithCategoryFilter()
    {
        // Demonstrates ADR-0047's intent: a HID-class UPS surfaces as
        // Category=Hid + Tags={Hid, Battery}. The "give me anything I
        // can read battery data from" filter doesn't care about Category.
        var filter = new DeviceFilter().WithTag(DeviceTags.Battery);

        var hidUps = new DeviceInfo
        {
            Id = "ups",
            Category = DeviceCategory.Hid,
            Tags = [DeviceTags.Hid, DeviceTags.Battery],
        };
        var systemBattery = new DeviceInfo
        {
            Id = "sys-bat",
            Category = DeviceCategory.Battery,
            Tags = [DeviceTags.Battery],
        };
        var plainHid = new DeviceInfo
        {
            Id = "gamepad",
            Category = DeviceCategory.Hid,
            Tags = [DeviceTags.Hid],
        };

        Assert.True(filter.Matches(hidUps));
        Assert.True(filter.Matches(systemBattery));
        Assert.False(filter.Matches(plainHid));
    }

    // ── Category-as-tag fallback (ADR-0047 §4) ─────────────────────────
    //
    // WithTag/WithAllTags/WithAnyTag also match against
    // Enum.GetName(device.Category) so enrichers don't have to redundantly
    // emit a tag for the Category their device already lives under.

    [Fact]
    public void WithTag_MatchesCategoryEnumNameAsFallback()
    {
        // Plain HID gamepad: OS-classified as Hid, no enricher tags. The
        // consumer's "give me HID devices" query via WithTag should still
        // match — the Category itself counts.
        var filter = new DeviceFilter().WithTag("Hid");

        var gamepad = new DeviceInfo
        {
            Id = "gamepad",
            Category = DeviceCategory.Hid,
            Tags = ImmutableHashSet<string>.Empty,
        };

        Assert.True(filter.Matches(gamepad));
    }

    [Fact]
    public void WithTag_CategoryFallbackUsesEnumMemberName_NotToString()
    {
        // Uses Enum.GetName, which returns the member identifier
        // ("Battery", "Hid"). DeviceTags constants are defined to match
        // those names exactly — so WithTag(DeviceTags.Battery) finds
        // both system batteries (via Category) and HID UPSs (via tag).
        var filter = new DeviceFilter().WithTag(DeviceTags.Battery);

        var systemBatteryNoTags = new DeviceInfo
        {
            Id = "sys-bat",
            Category = DeviceCategory.Battery,
            // Intentionally no Tags — proving the Category fallback is
            // what's matching, independent of enricher behavior.
            Tags = ImmutableHashSet<string>.Empty,
        };

        Assert.True(filter.Matches(systemBatteryNoTags));
    }

    [Fact]
    public void WithTag_CategoryAll_NeverMatchesAnyTag()
    {
        // DeviceCategory.All is the catch-all "no category set" sentinel.
        // It's not a capability claim, so it can't satisfy any tag query.
        var filter = new DeviceFilter().WithTag("Hid");

        var uncategorized = new DeviceInfo
        {
            Id = "unknown",
            Category = DeviceCategory.All,
            Tags = ImmutableHashSet<string>.Empty,
        };

        Assert.False(filter.Matches(uncategorized));
    }

    [Fact]
    public void WithTag_CategoryFallback_IsOrdinalCaseSensitive()
    {
        // Same comparison semantic as the Tags set itself — typos can't
        // silently match. "hid" doesn't match Category=Hid.
        var filter = new DeviceFilter().WithTag("hid");

        var gamepad = new DeviceInfo
        {
            Id = "gamepad",
            Category = DeviceCategory.Hid,
            Tags = ImmutableHashSet<string>.Empty,
        };

        Assert.False(filter.Matches(gamepad));
    }

    [Fact]
    public void WithAllTags_CombinesTagsAndCategoryFallback()
    {
        // HID UPS: Category=Hid covers "Hid" requirement, Tags={Battery}
        // covers "Battery" requirement. Both come from different sources,
        // but WithAllTags doesn't care.
        var filter = new DeviceFilter().WithAllTags("Hid", "Battery");

        var hidUps = new DeviceInfo
        {
            Id = "ups",
            Category = DeviceCategory.Hid,
            Tags = [DeviceTags.Battery],
        };
        var systemBatteryNonHid = new DeviceInfo
        {
            Id = "sys-bat",
            Category = DeviceCategory.Battery,
            Tags = ImmutableHashSet<string>.Empty,
        };

        Assert.True(filter.Matches(hidUps));
        // System battery only carries "Battery" (via Category), not "Hid".
        Assert.False(filter.Matches(systemBatteryNonHid));
    }

    [Fact]
    public void WithAnyTag_MatchesViaCategoryFallback()
    {
        // "Either an audio device OR a HID device" — both can be answered
        // by Category alone for OS-classified devices, no tags needed.
        var filter = new DeviceFilter().WithAnyTag("Audio", "Hid");

        var gamepad = new DeviceInfo
        {
            Id = "gamepad",
            Category = DeviceCategory.Hid,
            Tags = ImmutableHashSet<string>.Empty,
        };
        var soundCard = new DeviceInfo
        {
            Id = "audio",
            Category = DeviceCategory.Audio,
            Tags = ImmutableHashSet<string>.Empty,
        };
        var monitor = new DeviceInfo
        {
            Id = "screen",
            Category = DeviceCategory.Monitor,
            Tags = ImmutableHashSet<string>.Empty,
        };

        Assert.True(filter.Matches(gamepad));
        Assert.True(filter.Matches(soundCard));
        Assert.False(filter.Matches(monitor));
    }
}
