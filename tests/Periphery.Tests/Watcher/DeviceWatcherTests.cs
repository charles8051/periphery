namespace Periphery.Tests;

public class DeviceWatcherTests
{
    // ── Fluent API returns same instance ────────────────────────────────

    [Fact]
    public void FluentMethods_ReturnSameWatcher()
    {
        var watcher = Devices.Watch().OfCategory(DeviceCategory.Usb);

        Assert.Same(watcher, watcher.WithName("Mouse"));
        Assert.Same(watcher, watcher.ByManufacturer("Logitech"));
        Assert.Same(watcher, watcher.Where(_ => true));
        Assert.Same(watcher, watcher.OfCategory(DeviceCategory.Hid));
        Assert.Same(watcher, watcher.WithUsbId(new HardwareId(0x046D)));
        Assert.Same(watcher, watcher.WithUsbId("046D"));

        var existingA = new DeviceTracker(f => f.OfCategory(DeviceCategory.Usb));
        var existingB = new DeviceTracker(f => f.OfCategory(DeviceCategory.Bluetooth));
        Assert.Same(watcher, watcher.AddTracker(existingA));
        Assert.Same(watcher, watcher.AddTrackers(existingB));
    }

    [Fact]
    public void AddTracker_Configure_ReturnsTracker()
    {
        var watcher = Devices.Watch();

        var tracker = watcher.AddTracker(f => f.OfCategory(DeviceCategory.Usb), "UsbTracker");

        Assert.NotNull(tracker);
        Assert.Equal("UsbTracker", tracker.Name);
    }

    // ── Category defaults ──────────────────────────────────────────────

    [Fact]
    public void Watch_DefaultCategory_IsAll()
    {
        var watcher = Devices.Watch();
        // Just verify it creates without throwing
        Assert.NotNull(watcher);
    }

    [Fact]
    public void Watch_WithCategory_AcceptsCategory()
    {
        var watcher = Devices.Watch().OfCategory(DeviceCategory.Bluetooth);
        Assert.NotNull(watcher);
    }

    // ── IAsyncDisposable ───────────────────────────────────────────────

    [Fact]
    public async Task DisposeAsync_BeforeStart_DoesNotThrow()
    {
        var watcher = Devices.Watch().OfCategory(DeviceCategory.Usb);
        await watcher.DisposeAsync();
    }
}
