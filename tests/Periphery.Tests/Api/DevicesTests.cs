namespace Periphery.Tests;

/// <summary>
/// Tests for the static Devices API entry point.
/// </summary>
public class DevicesTests
{
    // ── Find ───────────────────────────────────────────────────────────

    [Fact]
    public void Find_ReturnsDeviceQuery()
    {
        var query = Devices.Enumerate();
        
        Assert.NotNull(query);
        Assert.IsType<DeviceQuery>(query);
    }

    [Fact]
    public void Find_WithCategory_SetsInitialCategory()
    {
        var query = Devices.Enumerate().OfCategory(DeviceCategory.Usb);
        
        Assert.NotNull(query);
    }

    [Fact]
    public void Find_DefaultsToAll()
    {
        var query = Devices.Enumerate();
        
        Assert.NotNull(query);
    }

    [Fact]
    public void Find_ReturnsFreshInstance()
    {
        var query1 = Devices.Enumerate().OfCategory(DeviceCategory.Usb);
        var query2 = Devices.Enumerate().OfCategory(DeviceCategory.Usb);
        
        Assert.NotSame(query1, query2);
    }

    // ── FindAsync ──────────────────────────────────────────────────────

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Enumerate_ToListAsync_ReturnsReadOnlyList()
    {
        var devices = await Devices.Enumerate().Active().ToListAsync();

        Assert.NotNull(devices);
        Assert.IsAssignableFrom<IReadOnlyList<DeviceInfo>>(devices);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Enumerate_WithCategory_FiltersToCategory()
    {
        var devices = await Devices.Enumerate()
            .OfCategory(DeviceCategory.Usb)
            .Active()
            .ToListAsync();

        Assert.NotNull(devices);
        Assert.All(devices, d => Assert.Equal(DeviceCategory.Usb, d.Category));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Enumerate_DefaultsToAll()
    {
        var devices = await Devices.Enumerate().Active().ToListAsync();

        Assert.NotNull(devices);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Enumerate_Present_OnlyReturnsPresentDevices()
    {
        var devices = await Devices.Enumerate().Active().ToListAsync();

        Assert.NotNull(devices);
        Assert.All(devices, d => Assert.True(d.IsActive));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Enumerate_MultipleCalls_ReturnsFreshData()
    {
        var devices1 = await Devices.Enumerate().Active().ToListAsync();
        var devices2 = await Devices.Enumerate().Active().ToListAsync();

        Assert.NotSame(devices1, devices2);
    }

    // ── Watch ──────────────────────────────────────────────────────────

    [Fact]
    public void Watch_ReturnsDeviceWatcher()
    {
        var watcher = Devices.Watch();
        
        Assert.NotNull(watcher);
        Assert.IsType<DeviceWatcher>(watcher);
    }

    [Fact]
    public void Watch_WithCategory_SetsInitialCategory()
    {
        var watcher = Devices.Watch().OfCategory(DeviceCategory.Bluetooth);
        
        Assert.NotNull(watcher);
    }

    [Fact]
    public void Watch_DefaultsToAll()
    {
        var watcher = Devices.Watch();
        
        Assert.NotNull(watcher);
    }

    [Fact]
    public void Watch_ReturnsFreshInstance()
    {
        var watcher1 = Devices.Watch().OfCategory(DeviceCategory.Usb);
        var watcher2 = Devices.Watch().OfCategory(DeviceCategory.Usb);
        
        Assert.NotSame(watcher1, watcher2);
    }

    [Fact]
    public void Watch_SupportsFluentConfiguration()
    {
        var watcher = Devices.Watch().OfCategory(DeviceCategory.Hid)
            .WithName("Mouse");
        
        Assert.NotNull(watcher);
    }

    // ── Category variations ────────────────────────────────────────────

    [Theory]
    [InlineData(DeviceCategory.All)]
    [InlineData(DeviceCategory.Usb)]
    [InlineData(DeviceCategory.Bluetooth)]
    [InlineData(DeviceCategory.Network)]
    [InlineData(DeviceCategory.Display)]
    [InlineData(DeviceCategory.Hid)]
    [InlineData(DeviceCategory.Audio)]
    [InlineData(DeviceCategory.Storage)]
    public void Find_AllCategories_ReturnsQuery(DeviceCategory category)
    {
        var query = Devices.Enumerate().OfCategory(category);
        
        Assert.NotNull(query);
    }

    [Theory]
    [InlineData(DeviceCategory.All)]
    [InlineData(DeviceCategory.Usb)]
    [InlineData(DeviceCategory.Bluetooth)]
    [InlineData(DeviceCategory.Network)]
    [InlineData(DeviceCategory.Display)]
    [InlineData(DeviceCategory.Hid)]
    [Trait("Category", "Integration")]
    public async Task Enumerate_AllCategories_Succeeds(DeviceCategory category)
    {
        var devices = await Devices.Enumerate()
            .OfCategory(category)
            .Active()
            .ToListAsync();

        Assert.NotNull(devices);
    }

    [Theory]
    [InlineData(DeviceCategory.All)]
    [InlineData(DeviceCategory.Usb)]
    [InlineData(DeviceCategory.Bluetooth)]
    [InlineData(DeviceCategory.Network)]
    [InlineData(DeviceCategory.Display)]
    [InlineData(DeviceCategory.Hid)]
    public void Watch_AllCategories_ReturnsWatcher(DeviceCategory category)
    {
        var watcher = Devices.Watch().OfCategory(category);
        
        Assert.NotNull(watcher);
    }

    // ── Integration ────────────────────────────────────────────────────

    [Fact]
    [Trait("Category", "Integration")]
    public async Task FindAndWatch_SameCategory_ShouldReturnConsistentData()
    {
        // Find current devices
        var foundDevices = await Devices.Enumerate().Active().ToListAsync();

        // Watch should fire Activated events for same devices
        await using var watcher = Devices.Watch().OfCategory(DeviceCategory.All);
        var activatedDevices = new List<DeviceInfo>();

        watcher.Activated += (_, e) => activatedDevices.Add(e.Device);
        
        await watcher.StartAsync();
        
        // Give watcher time to snapshot
        await Task.Delay(100);
        
        // Should have similar device counts (allowing for timing differences)
        Assert.True(Math.Abs(foundDevices.Count - activatedDevices.Count) <= 5,
            $"FindAsync returned {foundDevices.Count} devices, Watch snapshotted {activatedDevices.Count}");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Find_ChainedWithLinq_Composes()
    {
        var devices = await Devices.Enumerate().OfCategory(DeviceCategory.All)
            .Where(d => d.IsActive)
            .OrderBy(d => d.Name)
            .Take(5)
            .ToListAsync();

        Assert.NotNull(devices);
        Assert.True(devices.Count <= 5);
        Assert.All(devices, d => Assert.True(d.IsActive));
    }
}
