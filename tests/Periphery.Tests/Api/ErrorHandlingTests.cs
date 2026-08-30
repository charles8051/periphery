namespace Periphery.Tests;

/// <summary>
/// Tests for exception handling and error scenarios.
/// Documents expected behavior when device enumeration fails.
/// </summary>
public class ErrorHandlingTests
{
    // ── Exception types ────────────────────────────────────────────────

    [Fact]
    public void DeviceEnumerationException_CanBeConstructed()
    {
        var ex = new DeviceEnumerationException("Test message");
        
        Assert.Equal("Test message", ex.Message);
        Assert.Null(ex.InnerException);
    }

    [Fact]
    public void DeviceEnumerationException_WithInnerException()
    {
        var inner = new InvalidOperationException("Inner");
        var ex = new DeviceEnumerationException("Outer", inner);
        
        Assert.Equal("Outer", ex.Message);
        Assert.Same(inner, ex.InnerException);
    }

    [Fact]
    public void DeviceProviderException_InheritsFromDeviceEnumerationException()
    {
        var ex = new DeviceProviderException("Test");
        
        Assert.IsAssignableFrom<DeviceEnumerationException>(ex);
    }

    [Fact]
    public void DeviceProviderException_CanBeConstructed()
    {
        var ex = new DeviceProviderException("Provider failed");
        
        Assert.Equal("Provider failed", ex.Message);
    }

    // ── Catching exceptions ────────────────────────────────────────────

    [Fact]
    public void DeviceProviderException_CanBeCaughtAsBaseType()
    {
        try
        {
            throw new DeviceProviderException("Test");
        }
        catch (DeviceEnumerationException ex)
        {
            Assert.IsType<DeviceProviderException>(ex);
        }
    }

    [Fact]
    public void DeviceProviderException_CanBeCaughtAsException()
    {
        try
        {
            throw new DeviceProviderException("Test");
        }
        catch (Exception ex)
        {
            Assert.IsType<DeviceProviderException>(ex);
        }
    }

    // ── Input validation errors ────────────────────────────────────────

    [Fact]
    public void DeviceQuery_InvalidInput_ThrowsAppropriateException()
    {
        var query = Devices.Enumerate();
        
        // ArgumentNullException for null predicates
        Assert.Throws<ArgumentNullException>(() => query.Where(null!));
        
        // ArgumentNullException for null strings (ThrowIfNullOrWhiteSpace throws ArgumentNullException for null)
        Assert.Throws<ArgumentNullException>(() => query.WithName(null!));
        Assert.Throws<ArgumentNullException>(() => query.ByManufacturer(null!));
        
        // ArgumentException for empty strings
        Assert.Throws<ArgumentException>(() => query.WithName(""));
        Assert.Throws<ArgumentException>(() => query.ByManufacturer("")) ;
        
        // ArgumentOutOfRangeException for invalid Take values
        Assert.Throws<ArgumentOutOfRangeException>(() => query.Take(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => query.Take(-1));
    }

    [Fact]
    public void DeviceWatcher_InvalidInput_ThrowsAppropriateException()
    {
        var watcher = Devices.Watch();
        
        Assert.Throws<ArgumentNullException>(() => watcher.Where(null!));
        Assert.Throws<ArgumentNullException>(() => watcher.WithName(null!));
        Assert.Throws<ArgumentNullException>(() => watcher.ByManufacturer(null!));
        
        Assert.Throws<ArgumentException>(() => watcher.WithName(""));
        Assert.Throws<ArgumentException>(() => watcher.ByManufacturer(""));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task DeviceWatcher_StartTwice_ThrowsInvalidOperationException()
    {
        await using var watcher = Devices.Watch();
        
        await watcher.StartAsync();
        
        await Assert.ThrowsAsync<InvalidOperationException>(() => watcher.StartAsync());
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task DeviceWatcher_ModifyAfterStart_ThrowsInvalidOperationException()
    {
        await using var watcher = Devices.Watch();
        
        // Start the watcher
        await watcher.StartAsync();
        
        // Should throw when trying to modify filters
        Assert.Throws<InvalidOperationException>(() => watcher.WithName("test"));
        Assert.Throws<InvalidOperationException>(() => watcher.ByManufacturer("test"));
        Assert.Throws<InvalidOperationException>(() => watcher.OfCategory(DeviceCategory.Usb));
    }

    // ── Cancellation ───────────────────────────────────────────────────

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Enumerate_WithCancelledToken_ThrowsOperationCanceledException()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await Devices.Enumerate().Active().ToListAsync(cts.Token);
        });
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ToListAsync_WithCancelledToken_ThrowsOperationCanceledException()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();
        
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await Devices.Enumerate().ToListAsync(cts.Token);
        });
    }

    // ── Empty results (not errors) ─────────────────────────────────────

    [Fact]
    [Trait("Category", "Integration")]
    public async Task FindAsync_WithImpossibleFilter_ReturnsEmptyList()
    {
        var devices = await Devices.Enumerate()
            .WithName("__NONEXISTENT_DEVICE_XYZ_123__")
            .ToListAsync();
        
        Assert.NotNull(devices);
        Assert.Empty(devices);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task FirstOrDefaultAsync_WithNoMatches_ReturnsNull()
    {
        var device = await Devices.Enumerate()
            .WithName("__NONEXISTENT_DEVICE_XYZ_123__")
            .FirstOrDefaultAsync();
        
        Assert.Null(device);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CountAsync_WithNoMatches_ReturnsZero()
    {
        var count = await Devices.Enumerate()
            .WithName("__NONEXISTENT_DEVICE_XYZ_123__")
            .CountAsync();
        
        Assert.Equal(0, count);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task AnyAsync_WithNoMatches_ReturnsFalse()
    {
        var hasAny = await Devices.Enumerate()
            .WithName("__NONEXISTENT_DEVICE_XYZ_123__")
            .AnyAsync();
        
        Assert.False(hasAny);
    }

    // ── Platform availability ──────────────────────────────────────────

    [Fact]
    public void DeviceProviderFactory_OnUnsupportedPlatform_DocumentedBehavior()
    {
        // This test documents that on unsupported platforms,
        // DeviceProviderFactory.GetProvider() should throw PlatformNotSupportedException
        
        // On Windows, this should succeed
        if (OperatingSystem.IsWindows())
        {
            var exception = Record.Exception(() => DeviceProviderFactory.GetProvider());
            Assert.Null(exception);
        }
        
        // On Linux/macOS (not yet implemented), it should throw
        // This is documented behavior, not tested directly here
    }

    // ── DeviceInfo validation ──────────────────────────────────────────

    [Fact]
    public void DeviceInfo_RequiresId()
    {
        // DeviceInfo.Id is required (marked with 'required' keyword)
        // This test documents the requirement
        
        var device = new DeviceInfo { Id = "test" };
        Assert.Equal("test", device.Id);
    }

    // Note: DeviceInfo.Id is required via the 'required' modifier, which means
    // it must be set during object initialization. The compiler enforces this,
    // so no runtime test for null is needed.

    // ── HardwareId parsing errors ──────────────────────────────────────

    [Fact]
    public void HardwareId_Parse_InvalidInput_ThrowsFormatException()
    {
        Assert.Throws<FormatException>(() => HardwareId.Parse("INVALID"));
        Assert.Throws<FormatException>(() => HardwareId.Parse("ZZZZ"));
        Assert.Throws<FormatException>(() => HardwareId.Parse("12345678")); // Too long
    }

    [Fact]
    public void HardwareId_TryParse_InvalidInput_ReturnsFalse()
    {
        Assert.False(HardwareId.TryParse("INVALID", out _));
        Assert.False(HardwareId.TryParse("ZZZZ", out _));
        Assert.False(HardwareId.TryParse("", out _));
        Assert.False(HardwareId.TryParse(null, out _));
    }
}
