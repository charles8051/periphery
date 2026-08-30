namespace Periphery.Tests;

/// <summary>
/// End-to-end composition tests for <see cref="DeviceQuery"/> filter stacking,
/// ordering, and limiting. All tests use <see cref="FakeDeviceProvider"/> for
/// deterministic, hardware-free results.
/// </summary>
public class DeviceQueryIntegrationTests
{
    private static DeviceInfo MakeDevice(
        string id,
        string? name = null,
        DeviceCategory category = DeviceCategory.All,
        string? manufacturer = null,
        HardwareId? vendorId = null,
        bool isActive = true) => new()
    {
        Id = id,
        Name = name,
        Category = category,
        Manufacturer = manufacturer,
        VendorId = vendorId,
        IsActive = isActive,
        Status = DeviceStatus.OK,
    };

    // ── Category filtering ─────────────────────────────────────────────

    [Fact]
    public async Task FilterByName_ReturnsOnlyMatchingDevices()
    {
        var devices = new[]
        {
            MakeDevice("1", "USB Mouse",       DeviceCategory.Usb),
            MakeDevice("2", "USB Keyboard",    DeviceCategory.Usb),
            MakeDevice("3", "Ethernet Adapter",DeviceCategory.Network),
        };
        var query = new DeviceQuery(new FakeDeviceProvider(devices));

        var results = await query.WithName("Mouse").ToListAsync();

        Assert.Single(results);
        Assert.Equal("USB Mouse", results[0].Name);
    }

    [Fact]
    public async Task FilterByCategory_ReturnsOnlyMatchingCategory()
    {
        var devices = new[]
        {
            MakeDevice("1", "USB A", DeviceCategory.Usb),
            MakeDevice("2", "NET A", DeviceCategory.Network),
            MakeDevice("3", "USB B", DeviceCategory.Usb),
        };
        var query = new DeviceQuery(new FakeDeviceProvider(devices));

        var results = await query.OfCategory(DeviceCategory.Usb).ToListAsync();

        Assert.Equal(2, results.Count);
        Assert.All(results, d => Assert.Equal(DeviceCategory.Usb, d.Category));
    }

    // ── Ordering ───────────────────────────────────────────────────────

    [Fact]
    public async Task OrderByName_SortsCorrectly()
    {
        var devices = new[]
        {
            MakeDevice("1", "Zebra Device"),
            MakeDevice("2", "Alpha Device"),
            MakeDevice("3", "Beta Device"),
        };
        var query = new DeviceQuery(new FakeDeviceProvider(devices));

        var results = await query.OrderBy(d => d.Name).ToListAsync();

        Assert.Equal(["Alpha Device", "Beta Device", "Zebra Device"],
            results.Select(d => d.Name).ToList());
    }

    // ── Limiting ───────────────────────────────────────────────────────

    [Fact]
    public async Task Take_LimitsToExactCount()
    {
        var devices = Enumerable.Range(1, 10)
            .Select(i => MakeDevice($"device-{i}", $"Device {i:D2}"))
            .ToArray();
        var query = new DeviceQuery(new FakeDeviceProvider(devices));

        var results = await query.Take(3).ToListAsync();

        Assert.Equal(3, results.Count);
    }

    // ── Complex queries ────────────────────────────────────────────────

    [Fact]
    public async Task ComplexQuery_CategoryVidOrderingAndLimit_WorksTogether()
    {
        var devices = new[]
        {
            MakeDevice("1", "Logitech Mouse",    DeviceCategory.Usb,     "Logitech", new HardwareId(0x046D)),
            MakeDevice("2", "Logitech Keyboard", DeviceCategory.Usb,     "Logitech", new HardwareId(0x046D)),
            MakeDevice("3", "Intel NIC",         DeviceCategory.Network, "Intel",    new HardwareId(0x8086)),
            MakeDevice("4", "Logitech Webcam",   DeviceCategory.Usb,     "Logitech", new HardwareId(0x046D)),
            MakeDevice("5", "Generic Mouse",     DeviceCategory.Usb,     "Generic",  new HardwareId(0x1234)),
        };
        var query = new DeviceQuery(new FakeDeviceProvider(devices));

        // USB + Logitech VID, ascending by name, top 2
        var results = await query
            .OfCategory(DeviceCategory.Usb)
            .WithUsbId(new HardwareId(0x046D))
            .OrderBy(d => d.Name)
            .Take(2)
            .ToListAsync();

        Assert.Equal(2, results.Count);
        Assert.Equal("Logitech Keyboard", results[0].Name);
        Assert.Equal("Logitech Mouse",    results[1].Name);
    }

    // ── Active state filtering ──────────────────────────────────────────

    [Fact]
    public async Task Active_ReturnsOnlyActiveDevices()
    {
        var devices = new[]
        {
            MakeDevice("1", "Present Device",  isActive: true),
            MakeDevice("2", "Absent Device",   isActive: false),
            MakeDevice("3", "Another Present", isActive: true),
        };
        var query = new DeviceQuery(new FakeDeviceProvider(devices));

        var results = await query.Active().ToListAsync();

        Assert.Equal(2, results.Count);
        Assert.All(results, d => Assert.True(d.IsActive));
    }

    // ── Empty results ──────────────────────────────────────────────────

    [Fact]
    public async Task Query_WithNoMatches_ReturnsEmptyList()
    {
        var devices = new[] { MakeDevice("1", "USB Device", DeviceCategory.Usb) };
        var query = new DeviceQuery(new FakeDeviceProvider(devices));

        var results = await query.WithName("Bluetooth").ToListAsync();

        Assert.Empty(results);
    }

    // ── Manufacturer filtering ─────────────────────────────────────────

    [Fact]
    public async Task ByManufacturer_ReturnsOnlyMatchingManufacturer()
    {
        var devices = new[]
        {
            MakeDevice("1", "Device 1", manufacturer: "Intel Corporation"),
            MakeDevice("2", "Device 2", manufacturer: "AMD Inc."),
            MakeDevice("3", "Device 3", manufacturer: "Intel Corporation"),
        };
        var query = new DeviceQuery(new FakeDeviceProvider(devices));

        var results = await query.ByManufacturer("Intel").ToListAsync();

        Assert.Equal(2, results.Count);
        Assert.All(results, d => Assert.Contains("Intel", d.Manufacturer));
    }
}
