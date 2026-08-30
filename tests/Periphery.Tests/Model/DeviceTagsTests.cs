namespace Periphery.Tests;

/// <summary>
/// Behaviour pinning for <see cref="DeviceTags.Carries"/> — the Tags-or-Category
/// rule promoted out of <see cref="DeviceFilter"/> as a public helper so list-
/// based callers can apply the same matching logic without re-deriving it.
/// </summary>
public class DeviceTagsTests
{
    [Fact]
    public void Carries_ExplicitTag_ReturnsTrue()
    {
        var device = new DeviceInfo
        {
            Id = "id",
            Category = DeviceCategory.Hid,
            Tags = [DeviceTags.Battery],
        };

        Assert.True(DeviceTags.Carries(device, DeviceTags.Battery));
    }

    [Fact]
    public void Carries_CategoryFallback_ReturnsTrue()
    {
        // Plain HID gamepad — Category=Hid, no explicit tags. The Option B
        // fallback in DeviceTags.Carries matches on the Category enum name.
        var device = new DeviceInfo
        {
            Id = "id",
            Category = DeviceCategory.Hid,
        };

        Assert.True(DeviceTags.Carries(device, DeviceTags.Hid));
    }

    [Fact]
    public void Carries_SystemBattery_MatchesViaCategoryFallback()
    {
        // System battery enumerated under DeviceCategory.Battery should
        // match WithTag(Battery) via the fallback, without the enricher
        // having to emit a redundant tag.
        var device = new DeviceInfo
        {
            Id = "id",
            Category = DeviceCategory.Battery,
        };

        Assert.True(DeviceTags.Carries(device, DeviceTags.Battery));
    }

    [Fact]
    public void Carries_NoMatch_ReturnsFalse()
    {
        var device = new DeviceInfo
        {
            Id = "id",
            Category = DeviceCategory.Usb,
        };

        Assert.False(DeviceTags.Carries(device, DeviceTags.Battery));
    }

    [Fact]
    public void Carries_CategoryAll_NeverMatchesSpecificTag()
    {
        // DeviceCategory.All is a catch-all routing token, not a capability
        // claim — a Category=All device must not match every WithTag query.
        var device = new DeviceInfo
        {
            Id = "id",
            Category = DeviceCategory.All,
        };

        Assert.False(DeviceTags.Carries(device, DeviceTags.Hid));
        Assert.False(DeviceTags.Carries(device, DeviceTags.Battery));
    }

    [Fact]
    public void Carries_FallbackIsOrdinalCaseSensitive()
    {
        // Tags comparison is ordinal — the Category fallback follows the
        // same rule so consumers don't see different case-sensitivity
        // semantics depending on which source matched.
        var device = new DeviceInfo
        {
            Id = "id",
            Category = DeviceCategory.Hid,
        };

        Assert.True(DeviceTags.Carries(device, "Hid"));
        Assert.False(DeviceTags.Carries(device, "hid"));
        Assert.False(DeviceTags.Carries(device, "HID"));
    }

    [Fact]
    public void Carries_ExplicitTagWinsBeforeCategoryCheck()
    {
        // A USB device that an enricher tagged "Hid" (e.g. composite USB
        // device with a HID interface) carries the tag even though its
        // Category doesn't supply it via fallback.
        var device = new DeviceInfo
        {
            Id = "id",
            Category = DeviceCategory.Usb,
            Tags = [DeviceTags.Hid],
        };

        Assert.True(DeviceTags.Carries(device, DeviceTags.Hid));
    }

    [Fact]
    public void Carries_NullDevice_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => DeviceTags.Carries(null!, DeviceTags.Battery));
    }

    [Fact]
    public void Carries_NullOrWhitespaceTag_Throws()
    {
        var device = new DeviceInfo { Id = "id" };
        Assert.Throws<ArgumentNullException>(() => DeviceTags.Carries(device, null!));
        Assert.Throws<ArgumentException>(() => DeviceTags.Carries(device, ""));
        Assert.Throws<ArgumentException>(() => DeviceTags.Carries(device, "   "));
    }
}
