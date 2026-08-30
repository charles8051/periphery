namespace Periphery.Tests;

/// <summary>
/// Behaviour pinning for <see cref="SmartCardEnricher"/> (ADR-0051) — replaces
/// <c>DeviceCategory.SmartCard</c> with the <see cref="DeviceTags.SmartCard"/>
/// capability tag, detected per platform from ClassGuid / IOServiceClass / USB
/// class code.
/// </summary>
public class SmartCardEnricherTests
{
    private readonly SmartCardEnricher _enricher = new();
    private static readonly Guid SmartCardReaderGuid =
        Guid.Parse(Periphery.Windows.DeviceClassGuids.SmartCardReader);

    [Fact]
    public async Task Tags_WindowsSmartCardReaderClass()
    {
        var d = new DeviceInfo { Id = "w", Category = DeviceCategory.All, ClassGuid = SmartCardReaderGuid };
        var e = await _enricher.EnrichAsync(d, CancellationToken.None);
        Assert.Contains(DeviceTags.SmartCard, e.Tags);
    }

    [Fact]
    public async Task Tags_MacOSUsbSmartCardController()
    {
        var d = new DeviceInfo { Id = "m", Category = DeviceCategory.All, IOServiceClass = "IOUSBSmartCardController" };
        var e = await _enricher.EnrichAsync(d, CancellationToken.None);
        Assert.Contains(DeviceTags.SmartCard, e.Tags);
    }

    [Fact]
    public async Task Tags_UsbCcidClass0x0B()
    {
        var d = new DeviceInfo { Id = "u", Category = DeviceCategory.Usb, UsbClassCode = new UsbClassCode(0x0B, 0x00, 0x00) };
        var e = await _enricher.EnrichAsync(d, CancellationToken.None);
        Assert.Contains(DeviceTags.SmartCard, e.Tags);
    }

    [Fact]
    public async Task DoesNotTag_NonSmartCardDevice()
    {
        // A plain HID-class USB device (0x03) is not a smart-card reader.
        var d = new DeviceInfo { Id = "x", Category = DeviceCategory.Usb, UsbClassCode = new UsbClassCode(0x03, 0x00, 0x00) };
        var e = await _enricher.EnrichAsync(d, CancellationToken.None);
        Assert.DoesNotContain(DeviceTags.SmartCard, e.Tags);
        Assert.Same(d, e);
    }

    [Fact]
    public void Declares_EmitsTagsAndScope()
    {
        Assert.Contains(DeviceTags.SmartCard, _enricher.EmitsTags);
        Assert.Contains(Periphery.Windows.DeviceClassGuids.SmartCardReader, _enricher.Scope.WindowsClassGuids);
        Assert.Contains("usb", _enricher.Scope.LinuxSubsystems);
    }

    [Fact]
    public void Instance_IsRegisteredByModuleInitializer()
    {
        Assert.Contains(SmartCardEnricher.Instance, DeviceEnrichers.Snapshot());
    }
}
