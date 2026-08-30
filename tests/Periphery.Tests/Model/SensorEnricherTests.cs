namespace Periphery.Tests;

/// <summary>
/// Behaviour pinning for <see cref="SensorEnricher"/> (ADR-0051) — the core
/// enricher that replaces <c>DeviceCategory.Sensor</c> with the
/// <see cref="DeviceTags.Sensor"/> capability tag, detecting the sensor signal
/// from whichever per-platform <see cref="DeviceInfo"/> field the provider
/// populated.
/// </summary>
public class SensorEnricherTests
{
    private readonly SensorEnricher _enricher = new();
    private static readonly Guid SensorClassGuid =
        Guid.Parse(Periphery.Windows.DeviceClassGuids.Sensor);

    [Fact]
    public async Task Tags_WindowsSensorClassDevice()
    {
        var device = new DeviceInfo { Id = "w", Category = DeviceCategory.All, ClassGuid = SensorClassGuid };
        var enriched = await _enricher.EnrichAsync(device, CancellationToken.None);
        Assert.Contains(DeviceTags.Sensor, enriched.Tags);
    }

    [Fact]
    public async Task Tags_LinuxIioDevice()
    {
        var device = new DeviceInfo { Id = "l", Category = DeviceCategory.All, Subsystem = "iio" };
        var enriched = await _enricher.EnrichAsync(device, CancellationToken.None);
        Assert.Contains(DeviceTags.Sensor, enriched.Tags);
    }

    [Fact]
    public async Task Tags_MacOSHidSensorUsagePage()
    {
        var device = new DeviceInfo { Id = "m", Category = DeviceCategory.Hid, HidUsagePage = 0x20 };
        var enriched = await _enricher.EnrichAsync(device, CancellationToken.None);
        Assert.Contains(DeviceTags.Sensor, enriched.Tags);
    }

    [Fact]
    public async Task DoesNotTag_NonSensorDevice()
    {
        // HID device on the Generic Desktop page (0x01), no sensor signal.
        var device = new DeviceInfo { Id = "x", Category = DeviceCategory.Hid, HidUsagePage = 0x01 };
        var enriched = await _enricher.EnrichAsync(device, CancellationToken.None);
        Assert.DoesNotContain(DeviceTags.Sensor, enriched.Tags);
        Assert.Same(device, enriched);
    }

    [Fact]
    public async Task Idempotent_AlreadyTagged_ReturnsSameInstance()
    {
        var device = new DeviceInfo { Id = "i", Subsystem = "iio", Tags = [DeviceTags.Sensor] };
        var enriched = await _enricher.EnrichAsync(device, CancellationToken.None);
        Assert.Same(device, enriched);
    }

    [Fact]
    public void Declares_EmitsTagsAndPerPlatformScope()
    {
        Assert.Contains(DeviceTags.Sensor, _enricher.EmitsTags);
        Assert.Contains(Periphery.Windows.DeviceClassGuids.Sensor, _enricher.Scope.WindowsClassGuids);
        Assert.Contains("iio", _enricher.Scope.LinuxSubsystems);
        Assert.Contains("IOHIDDevice", _enricher.Scope.MacOSClasses);
    }

    [Fact]
    public void Instance_IsRegisteredByModuleInitializer()
    {
        // The core assembly's [ModuleInitializer] auto-registers the singleton.
        Assert.Contains(SensorEnricher.Instance, DeviceEnrichers.Snapshot());
    }
}
