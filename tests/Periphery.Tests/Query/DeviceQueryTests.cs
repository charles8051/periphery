namespace Periphery.Tests;

/// <summary>
/// Unit tests for <see cref="DeviceQuery"/> materialization, ordering, limiting,
/// and LINQ interop. All tests use <see cref="FakeDeviceProvider"/> — no OS APIs required.
/// </summary>
public class DeviceQueryTests
{
    // ── Deterministic test dataset ─────────────────────────────────────
    // 5 devices: 3 USB (2 Logitech VID 046D, 1 disconnected), 1 HID, 1 Network.
    // Names chosen so Ordinal ascending order is: Alpha, Beta, Delta, Epsilon, Gamma.

    private static readonly DeviceInfo[] _testDevices =
    [
        new() { Id = "USB\\1", Name = "Alpha Device",   Category = DeviceCategory.Usb,     Manufacturer = "Logitech",   VendorId = new HardwareId(0x046D), ProductId = new HardwareId(0xC077), IsActive = true,  Status = DeviceStatus.OK },
        new() { Id = "USB\\2", Name = "Beta Device",    Category = DeviceCategory.Usb,     Manufacturer = "Logitech",   VendorId = new HardwareId(0x046D), ProductId = new HardwareId(0xC52B), IsActive = true,  Status = DeviceStatus.OK },
        new() { Id = "NET\\1", Name = "Delta Device",   Category = DeviceCategory.Network, Manufacturer = "Intel",                                                                             IsActive = true,  Status = DeviceStatus.OK },
        new() { Id = "USB\\3", Name = "Epsilon Device", Category = DeviceCategory.Usb,                                                                                                         IsActive = false, Status = DeviceStatus.Error },
        new() { Id = "HID\\1", Name = "Gamma Device",   Category = DeviceCategory.Hid,     Manufacturer = "Microsoft",                                                                         IsActive = true,  Status = DeviceStatus.OK },
    ];

    private static DeviceQuery Query() => new(new FakeDeviceProvider(_testDevices));
    private static DeviceQuery EmptyQuery() => new(FakeDeviceProvider.Empty());

    // ── Materialisation ────────────────────────────────────────────────

    [Fact]
    public async Task ToListAsync_ReturnsAllMatchingDevices()
    {
        var devices = await Query().ToListAsync();

        Assert.Equal(_testDevices.Length, devices.Count);
    }

    [Fact]
    public async Task FirstOrDefaultAsync_WithMatches_ReturnsFirstDevice()
    {
        var device = await Query().FirstOrDefaultAsync();

        Assert.NotNull(device);
        Assert.NotNull(device.Id);
    }

    [Fact]
    public async Task FirstOrDefaultAsync_WithNoMatches_ReturnsNull()
    {
        var device = await Query().WithName("__NONEXISTENT__").FirstOrDefaultAsync();

        Assert.Null(device);
    }

    [Fact]
    public async Task CountAsync_ReturnsExactCount()
    {
        var count = await Query().CountAsync();

        Assert.Equal(_testDevices.Length, count);
    }

    [Fact]
    public async Task AnyAsync_WithMatches_ReturnsTrue()
    {
        Assert.True(await Query().AnyAsync());
    }

    [Fact]
    public async Task AnyAsync_WithNoMatches_ReturnsFalse()
    {
        Assert.False(await EmptyQuery().AnyAsync());
    }

    // ── Ordering ───────────────────────────────────────────────────────

    [Fact]
    public async Task OrderBy_AscendingByName_SortsCorrectly()
    {
        var devices = await Query().OrderBy(d => d.Name).ToListAsync();

        for (int i = 0; i < devices.Count - 1; i++)
        {
            var a = devices[i].Name ?? "";
            var b = devices[i + 1].Name ?? "";
            Assert.True(string.Compare(a, b, StringComparison.Ordinal) <= 0,
                $"Expected '{a}' <= '{b}'");
        }
    }

    [Fact]
    public async Task OrderBy_DescendingByName_SortsCorrectly()
    {
        var devices = await Query().OrderBy(d => d.Name, descending: true).ToListAsync();

        for (int i = 0; i < devices.Count - 1; i++)
        {
            var a = devices[i].Name ?? "";
            var b = devices[i + 1].Name ?? "";
            Assert.True(string.Compare(a, b, StringComparison.Ordinal) >= 0,
                $"Expected '{a}' >= '{b}'");
        }
    }

    [Fact]
    public async Task OrderBy_Ascending_ProducesExpectedSequence()
    {
        var names = (await Query().OrderBy(d => d.Name).ToListAsync())
            .Select(d => d.Name)
            .ToList();

        Assert.Equal(
            ["Alpha Device", "Beta Device", "Delta Device", "Epsilon Device", "Gamma Device"],
            names);
    }

    [Fact]
    public async Task OrderBy_Descending_ProducesExpectedSequence()
    {
        var names = (await Query().OrderBy(d => d.Name, descending: true).ToListAsync())
            .Select(d => d.Name)
            .ToList();

        Assert.Equal(
            ["Gamma Device", "Epsilon Device", "Delta Device", "Beta Device", "Alpha Device"],
            names);
    }

    // ── Limiting ───────────────────────────────────────────────────────

    [Fact]
    public async Task Take_LimitsResultCount()
    {
        var devices = await Query().Take(3).ToListAsync();

        Assert.Equal(3, devices.Count);
    }

    [Fact]
    public void Take_WithZero_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Query().Take(0));
    }

    [Fact]
    public void Take_WithNegative_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Query().Take(-1));
    }

    // ── Filtering ──────────────────────────────────────────────────────

    [Fact]
    public async Task Where_WithPredicate_FiltersResults()
    {
        var devices = await Query().Where(d => d.IsActive).ToListAsync();

        Assert.All(devices, d => Assert.True(d.IsActive));
        Assert.Equal(_testDevices.Count(d => d.IsActive), devices.Count);
    }

    [Fact]
    public void Where_WithNullPredicate_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => Query().Where(null!));
    }

    [Fact]
    public void WithName_WithNull_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => Query().WithName(null!));
    }

    [Fact]
    public void WithName_WithEmpty_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => Query().WithName(""));
    }

    [Fact]
    public void WithName_WithWhitespace_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => Query().WithName("   "));
    }

    [Fact]
    public void ByManufacturer_WithNull_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => Query().ByManufacturer(null!));
    }

    [Fact]
    public void ByManufacturer_WithEmpty_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => Query().ByManufacturer(""));
    }

    // ── IAsyncEnumerable interop ───────────────────────────────────────

    [Fact]
    public async Task GetAsyncEnumerator_CanIterate()
    {
        int count = 0;
        await foreach (var device in Query())
        {
            Assert.NotNull(device.Id);
            count++;
        }

        Assert.Equal(_testDevices.Length, count);
    }

    [Fact]
    public async Task GetAsyncEnumerator_SupportsComposedFilters()
    {
        var devices = await Query()
            .Where(d => d.IsActive)
            .OrderBy(d => d.Name)
            .Take(3)
            .ToListAsync();

        Assert.Equal(3, devices.Count);
        Assert.All(devices, d => Assert.True(d.IsActive));
    }

    // ── Fluent chaining

    [Fact]
    public async Task FluentChaining_ActiveOrderedLimited_AllApplied()
    {
        var devices = await Query()
            .Active()
            .OrderBy(d => d.Name)
            .Take(3)
            .ToListAsync();

        Assert.Equal(3, devices.Count);
        Assert.All(devices, d => Assert.True(d.IsActive));
        for (int i = 0; i < devices.Count - 1; i++)
            Assert.True(string.Compare(devices[i].Name, devices[i + 1].Name, StringComparison.Ordinal) <= 0);
    }

    [Fact]
    public async Task Active_WithTrue_OnlyReturnsActive()
    {
        var devices = await Query().Active(true).ToListAsync();

        Assert.All(devices, d => Assert.True(d.IsActive));
        Assert.Equal(_testDevices.Count(d => d.IsActive), devices.Count);
    }

    [Fact]
    public async Task Active_WithFalse_OnlyReturnsInactive()
    {
        var devices = await Query().Active(false).ToListAsync();

        Assert.All(devices, d => Assert.False(d.IsActive));
        Assert.Equal(_testDevices.Count(d => !d.IsActive), devices.Count);
    }

    // ── USB ID filtering ───────────────────────────────────────────────

    [Fact]
    public async Task WithUsbId_WithValidVid_FiltersResults()
    {
        var vid = new HardwareId(0x046D); // Logitech — matches Alpha and Beta

        var devices = await Query().WithUsbId(vid).ToListAsync();

        Assert.All(devices, d => Assert.Equal(vid, d.VendorId));
        Assert.Equal(2, devices.Count);
    }

    [Fact]
    public async Task WithUsbId_WithStringVid_ParsesAndFilters()
    {
        var devices = await Query().WithUsbId("046D").ToListAsync();

        Assert.Equal(2, devices.Count);
        Assert.All(devices, d => Assert.Equal(new HardwareId(0x046D), d.VendorId));
    }

    [Fact]
    public async Task WithUsbId_WithInvalidString_ReturnsEmpty()
    {
        var devices = await Query().WithUsbId("INVALID_VID").ToListAsync();

        Assert.Empty(devices);
    }

    // ── Category filtering ─────────────────────────────────────────────

    [Fact]
    public async Task OfCategory_Usb_FiltersToUsbOnly()
    {
        var devices = await Query().OfCategory(DeviceCategory.Usb).ToListAsync();

        Assert.All(devices, d => Assert.Equal(DeviceCategory.Usb, d.Category));
        Assert.Equal(_testDevices.Count(d => d.Category == DeviceCategory.Usb), devices.Count);
    }

    [Fact]
    public async Task OfCategory_Hid_ReturnsSingleDevice()
    {
        var devices = await Query().OfCategory(DeviceCategory.Hid).ToListAsync();

        Assert.Single(devices);
        Assert.Equal("HID\\1", devices[0].Id);
    }

    [Fact]
    public async Task OfCategory_Network_ReturnsSingleDevice()
    {
        var devices = await Query().OfCategory(DeviceCategory.Network).ToListAsync();

        Assert.Single(devices);
        Assert.Equal("NET\\1", devices[0].Id);
    }
}
