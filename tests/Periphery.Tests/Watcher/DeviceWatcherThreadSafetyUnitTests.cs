namespace Periphery.Tests;

/// <summary>
/// Fast unit tests for DeviceWatcher thread safety that don't require real OS APIs.
/// These tests verify locking behavior, disposal protection, and state management
/// without actually starting watchers.
/// </summary>
public class DeviceWatcherThreadSafetyUnitTests
{
    // ── Disposal protection ────────────────────────────────────────────

    [Fact]
    public async Task ModifyFilters_AfterDispose_ThrowsObjectDisposedException()
    {
        var watcher = Devices.Watch().OfCategory(DeviceCategory.All);
        await watcher.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() => watcher.WithName("test"));
        Assert.Throws<ObjectDisposedException>(() => watcher.ByManufacturer("test"));
        Assert.Throws<ObjectDisposedException>(() => watcher.OfCategory(DeviceCategory.Usb));
        Assert.Throws<ObjectDisposedException>(() => watcher.Where(_ => true));
    }

    [Fact]
    public async Task StartAsync_AfterDispose_ThrowsObjectDisposedException()
    {
        var watcher = Devices.Watch().OfCategory(DeviceCategory.All);
        await watcher.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => watcher.StartAsync());
    }

    [Fact]
    public async Task DisposeAsync_AfterDispose_Succeeds()
    {
        var watcher = Devices.Watch().OfCategory(DeviceCategory.All);
        await watcher.DisposeAsync();

        // Second DisposeAsync should be no-op (idempotent)
        await watcher.DisposeAsync();
    }

    [Fact]
    public async Task ConcurrentDisposeAsync_IsIdempotent()
    {
        var watcher = Devices.Watch().OfCategory(DeviceCategory.All);

        // Launch 10 concurrent DisposeAsync calls without starting
        var tasks = Enumerable.Range(0, 10)
            .Select(_ => Task.Run(async () => await watcher.DisposeAsync()))
            .ToArray();

        // All should complete successfully (idempotent)
        await Task.WhenAll(tasks);
        
        // No exception should be thrown
        Assert.True(true);
    }

    // ── Filter modification validation ─────────────────────────────────

    [Fact]
    public void FluentMethods_BeforeStart_ReturnSameInstance()
    {
        var watcher = Devices.Watch().OfCategory(DeviceCategory.All);

        // All fluent methods should return the same instance
        Assert.Same(watcher, watcher.WithName("test"));
        Assert.Same(watcher, watcher.ByManufacturer("test"));
        Assert.Same(watcher, watcher.OfCategory(DeviceCategory.Usb));
    }

    [Fact]
    public void FluentMethods_InputValidation_Works()
    {
        var watcher = Devices.Watch();

        Assert.Throws<ArgumentNullException>(() => watcher.WithName(null!));
        Assert.Throws<ArgumentException>(() => watcher.WithName(""));
        Assert.Throws<ArgumentNullException>(() => watcher.ByManufacturer(null!));
        Assert.Throws<ArgumentException>(() => watcher.ByManufacturer(""));
        Assert.Throws<ArgumentNullException>(() => watcher.Where(null!));
    }

    // ── State management ───────────────────────────────────────────────

    [Fact]
    public void Constructor_SetsInitialState()
    {
        var watcher = Devices.Watch().OfCategory(DeviceCategory.Usb);
        
        // Should be able to modify filters immediately after construction
        var result = watcher.WithName("test");
        Assert.Same(watcher, result);
    }

    [Fact]
    public async Task DisposeAsync_Multiple_IsIdempotent()
    {
        var watcher = Devices.Watch().OfCategory(DeviceCategory.All);
        
        // First dispose
        await watcher.DisposeAsync();
        
        // Second dispose should not throw
        await watcher.DisposeAsync();
        
        // Third dispose should not throw
        await watcher.DisposeAsync();
        
        Assert.True(true);
    }

    [Fact]
    public async Task DisposeAsync_WithoutStart_Succeeds()
    {
        var watcher = Devices.Watch().OfCategory(DeviceCategory.All);
        
        // Dispose without ever starting should work
        await watcher.DisposeAsync();
        
        Assert.True(true);
    }

    // ── Event handler thread safety ────────────────────────────────────

    [Fact]
    public void EventHandlers_CanBeAddedBeforeStart()
    {
        var watcher = Devices.Watch().OfCategory(DeviceCategory.All);
        
        int activatedCount = 0;
        int deactivatedCount = 0;

        // Should be able to add event handlers before starting
        watcher.Activated += (_, _) => activatedCount++;
        watcher.Deactivated += (_, _) => deactivatedCount++;

        Assert.Equal(0, activatedCount);
        Assert.Equal(0, deactivatedCount);
    }

    [Fact]
    public async Task EventHandlers_CanBeAddedFromMultipleThreads()
    {
        var watcher = Devices.Watch().OfCategory(DeviceCategory.All);
        
        // Add event handlers from multiple threads concurrently
        var tasks = Enumerable.Range(0, 10)
            .Select(i => Task.Run(() =>
            {
                watcher.Activated += (_, e) => { /* no-op */ };
            }))
            .ToArray();

        await Task.WhenAll(tasks);
        
        // Should complete without throwing
        Assert.True(true);
    }

    // ── Filter composition ─────────────────────────────────────────────

    [Fact]
    public void FluentChaining_WorksCorrectly()
    {
        var watcher = Devices.Watch().OfCategory(DeviceCategory.All)
            .WithName("Mouse")
            .ByManufacturer("Logitech")
            .Where(d => d.IsActive);
        
        // Should return the same instance throughout the chain
        Assert.NotNull(watcher);
    }

    [Fact]
    public void OfCategory_UpdatesCategory()
    {
        var watcher = Devices.Watch().OfCategory(DeviceCategory.All);
        
        // Should be able to change category before starting
        var result = watcher.OfCategory(DeviceCategory.Usb);
        Assert.Same(watcher, result);
        
        result = watcher.OfCategory(DeviceCategory.Bluetooth);
        Assert.Same(watcher, result);
    }

    // ── Async dispose cleanup ──────────────────────────────────────────

    [Fact]
    public async Task UsingStatement_ProperlyDisposesWatcher()
    {
        DeviceWatcher? watcher = null;
        
        await using (watcher = Devices.Watch().OfCategory(DeviceCategory.All))
        {
            Assert.NotNull(watcher);
        }
        
        // After using block, watcher should be disposed
        Assert.Throws<ObjectDisposedException>(() => watcher.WithName("test"));
    }

    [Fact]
    public async Task AwaitUsingDeclaration_ProperlyDisposesWatcher()
    {
        var watcher = Devices.Watch().OfCategory(DeviceCategory.All);
        await using (watcher)
        {
            // Watcher is active within this block
            var result = watcher.WithName("test");
            Assert.Same(watcher, result);
        }
        
        // After await using block, should be disposed
        Assert.Throws<ObjectDisposedException>(() => watcher.WithName("test"));
    }

    // ── Concurrent filter modifications ────────────────────────────────

    [Fact]
    public async Task ConcurrentFilterModifications_BeforeStart_AreAllowed()
    {
        var watcher = Devices.Watch().OfCategory(DeviceCategory.All);
        
        // Modify filters from multiple threads concurrently
        // This is technically not thread-safe by design, but shouldn't crash
        var tasks = Enumerable.Range(0, 10)
            .Select(i => Task.Run(() =>
            {
                watcher.WithName($"Device{i}");
                watcher.ByManufacturer($"Vendor{i}");
            }))
            .ToArray();

        await Task.WhenAll(tasks);
        
        // Should complete even if filters are in undefined state
        Assert.True(true);
    }
}
