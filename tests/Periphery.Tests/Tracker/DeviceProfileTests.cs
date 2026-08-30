namespace Periphery.Tests;

/// <summary>
/// Unit tests for <see cref="DeviceProfile"/> construction and edge cases.
/// </summary>
public class DeviceProfileTests
{
    // ── Constructor (Action<DeviceFilter>) ──────────────────────────────

    [Fact]
    public void Constructor_WithValidConfigure_CreatesProfile()
    {
        var profile = new DeviceProfile(f => f.OfCategory(DeviceCategory.Usb), "USB Profile");

        Assert.Equal("USB Profile", profile.Name);
    }

    [Fact]
    public void Constructor_WithNullConfigure_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new DeviceProfile((Action<DeviceFilter>)null!, "Test"));
    }

    [Fact]
    public void Constructor_WithNullName_IsAllowed()
    {
        var profile = new DeviceProfile(f => f.OfCategory(DeviceCategory.Usb), name: null);

        Assert.Null(profile.Name);
    }

    [Fact]
    public void Constructor_WithEmptyName_IsAllowed()
    {
        var profile = new DeviceProfile(f => f.OfCategory(DeviceCategory.Usb), name: "");

        Assert.Equal("", profile.Name);
    }

    [Fact]
    public void Constructor_DefaultName_IsNull()
    {
        var profile = new DeviceProfile(f => f.OfCategory(DeviceCategory.Usb));

        Assert.Null(profile.Name);
    }

    // ── Filter is configured ───────────────────────────────────────────

    [Fact]
    public void Constructor_AppliesConfigureToFilter()
    {
        var profile = new DeviceProfile(f => f.OfCategory(DeviceCategory.Bluetooth));

        var btDevice = new DeviceInfo
        {
            Id = "BT\\1",
            Category = DeviceCategory.Bluetooth,
            IsActive = true,
        };
        var usbDevice = new DeviceInfo
        {
            Id = "USB\\1",
            Category = DeviceCategory.Usb,
            IsActive = true,
        };

        Assert.True(profile.Filter.Matches(btDevice));
        Assert.False(profile.Filter.Matches(usbDevice));
    }

    // ── DeviceTracker with profiles ────────────────────────────────────

    [Fact]
    public void DeviceTracker_WithSingleProfile_UsesProfileFilter()
    {
        var profile = new DeviceProfile(f => f.OfCategory(DeviceCategory.Usb), "USB");
        var tracker = new DeviceTracker("Test", profile);

        var usbDevice = new DeviceInfo
        {
            Id = "USB\\1",
            Category = DeviceCategory.Usb,
            IsActive = true,
        };

        Assert.True(tracker.Matches(usbDevice));
    }

    [Fact]
    public void DeviceTracker_WithMultipleProfiles_MatchesAny()
    {
        var usb = new DeviceProfile(f => f.OfCategory(DeviceCategory.Usb), "USB");
        var bt = new DeviceProfile(f => f.OfCategory(DeviceCategory.Bluetooth), "BT");
        var tracker = new DeviceTracker("Test", usb, bt);

        var usbDevice = new DeviceInfo { Id = "USB\\1", Category = DeviceCategory.Usb, IsActive = true };
        var btDevice = new DeviceInfo { Id = "BT\\1", Category = DeviceCategory.Bluetooth, IsActive = true };
        var netDevice = new DeviceInfo { Id = "NET\\1", Category = DeviceCategory.Network, IsActive = true };

        Assert.True(tracker.Matches(usbDevice));
        Assert.True(tracker.Matches(btDevice));
        Assert.False(tracker.Matches(netDevice));
    }

    // ── ForDevice (ID-pinned profile) ──────────────────────────────────

    [Fact]
    public void ForDevice_NullDevice_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => DeviceProfile.ForDevice(null!));
    }

    [Fact]
    public void ForDevice_UsesDeviceNameAsLabel()
    {
        var device = new DeviceInfo
        {
            Id = "USB\\VID_046D&PID_C52B\\0001",
            Name = "Logitech MX Master",
            Category = DeviceCategory.Usb,
        };

        var profile = DeviceProfile.ForDevice(device);

        Assert.Equal("Logitech MX Master", profile.Name);
    }

    [Fact]
    public void ForDevice_FallsBackToDeviceIdWhenNameIsNull()
    {
        var device = new DeviceInfo
        {
            Id = "TEST\\NO_NAME\\1",
            Name = null,
            Category = DeviceCategory.Camera,
        };

        var profile = DeviceProfile.ForDevice(device);

        Assert.Equal("TEST\\NO_NAME\\1", profile.Name);
    }

    [Fact]
    public void ForDevice_PinnedProfileMatchesOnlyExactDevice()
    {
        var device = new DeviceInfo
        {
            Id = "USB\\VID_AAAA&PID_BBBB\\1",
            Name = "Camera A",
            Category = DeviceCategory.Camera,
            IsActive = true,
        };
        var sibling = new DeviceInfo
        {
            Id = "USB\\VID_AAAA&PID_BBBB\\2", // same name, different ID
            Name = "Camera A",
            Category = DeviceCategory.Camera,
            IsActive = true,
        };

        var profile = DeviceProfile.ForDevice(device);
        var tracker = new DeviceTracker("Pinned", profile);

        Assert.True(tracker.Matches(device));
        Assert.False(tracker.Matches(sibling));
    }
}
