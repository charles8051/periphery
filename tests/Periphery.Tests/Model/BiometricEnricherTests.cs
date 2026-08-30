namespace Periphery.Tests;

/// <summary>
/// Behaviour pinning for <see cref="BiometricEnricher"/> (ADR-0051) — replaces
/// <c>DeviceCategory.Biometric</c> with the <see cref="DeviceTags.Biometric"/>
/// capability tag, detected from the Windows Biometric setup class.
/// </summary>
public class BiometricEnricherTests
{
    private readonly BiometricEnricher _enricher = new();
    private static readonly Guid BiometricClassGuid =
        Guid.Parse(Periphery.Windows.DeviceClassGuids.Biometric);

    [Fact]
    public async Task Tags_WindowsBiometricClass()
    {
        var d = new DeviceInfo { Id = "w", Category = DeviceCategory.All, ClassGuid = BiometricClassGuid };
        var e = await _enricher.EnrichAsync(d, CancellationToken.None);
        Assert.Contains(DeviceTags.Biometric, e.Tags);
    }

    [Fact]
    public async Task DoesNotTag_VendorSpecificUsbDevice()
    {
        // Deliberate: USB class 0xFF (vendor-specific) is NOT treated as biometric —
        // it would over-match. Biometric detection is Windows-class-GUID only.
        var d = new DeviceInfo { Id = "x", Category = DeviceCategory.Usb, UsbClassCode = new UsbClassCode(0xFF, 0x00, 0x00) };
        var e = await _enricher.EnrichAsync(d, CancellationToken.None);
        Assert.DoesNotContain(DeviceTags.Biometric, e.Tags);
        Assert.Same(d, e);
    }

    [Fact]
    public void Declares_EmitsTagsAndScope()
    {
        Assert.Contains(DeviceTags.Biometric, _enricher.EmitsTags);
        Assert.Contains(Periphery.Windows.DeviceClassGuids.Biometric, _enricher.Scope.WindowsClassGuids);
    }

    [Fact]
    public void Instance_IsRegisteredByModuleInitializer()
    {
        Assert.Contains(BiometricEnricher.Instance, DeviceEnrichers.Snapshot());
    }
}
