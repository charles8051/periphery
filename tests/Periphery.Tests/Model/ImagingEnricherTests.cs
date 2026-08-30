namespace Periphery.Tests;

/// <summary>
/// Behaviour pinning for <see cref="ImagingEnricher"/> (ADR-0051) — replaces
/// <c>DeviceCategory.Imaging</c> with the <see cref="DeviceTags.Imaging"/>
/// capability tag, detected from the Windows Image setup class or USB class 0x06.
/// </summary>
public class ImagingEnricherTests
{
    private readonly ImagingEnricher _enricher = new();
    private static readonly Guid ImageClassGuid =
        Guid.Parse(Periphery.Windows.DeviceClassGuids.Image);

    [Fact]
    public async Task Tags_WindowsImageClass()
    {
        var d = new DeviceInfo { Id = "w", Category = DeviceCategory.All, ClassGuid = ImageClassGuid };
        var e = await _enricher.EnrichAsync(d, CancellationToken.None);
        Assert.Contains(DeviceTags.Imaging, e.Tags);
    }

    [Fact]
    public async Task Tags_UsbStillImageClass0x06()
    {
        var d = new DeviceInfo { Id = "u", Category = DeviceCategory.Usb, UsbClassCode = new UsbClassCode(0x06, 0x00, 0x00) };
        var e = await _enricher.EnrichAsync(d, CancellationToken.None);
        Assert.Contains(DeviceTags.Imaging, e.Tags);
    }

    [Fact]
    public async Task DoesNotTag_NonImagingDevice()
    {
        // A plain HID-class USB device (0x03) is not an imaging device.
        var d = new DeviceInfo { Id = "x", Category = DeviceCategory.Usb, UsbClassCode = new UsbClassCode(0x03, 0x00, 0x00) };
        var e = await _enricher.EnrichAsync(d, CancellationToken.None);
        Assert.DoesNotContain(DeviceTags.Imaging, e.Tags);
        Assert.Same(d, e);
    }

    [Fact]
    public void Declares_EmitsTagsAndScope()
    {
        Assert.Contains(DeviceTags.Imaging, _enricher.EmitsTags);
        Assert.Contains(Periphery.Windows.DeviceClassGuids.Image, _enricher.Scope.WindowsClassGuids);
        Assert.Contains("usb", _enricher.Scope.LinuxSubsystems);
    }

    [Fact]
    public void Instance_IsRegisteredByModuleInitializer()
    {
        Assert.Contains(ImagingEnricher.Instance, DeviceEnrichers.Snapshot());
    }
}
