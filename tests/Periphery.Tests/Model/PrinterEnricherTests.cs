namespace Periphery.Tests;

/// <summary>
/// Behaviour pinning for <see cref="PrinterEnricher"/> (ADR-0051) — replaces
/// <c>DeviceCategory.Printer</c> with the <see cref="DeviceTags.Printer"/>
/// capability tag, detected from the Windows Printer/PnpPrinters/PrintQueue setup
/// classes or USB class 0x07.
/// </summary>
public class PrinterEnricherTests
{
    private readonly PrinterEnricher _enricher = new();

    [Theory]
    [InlineData("Printer")]
    [InlineData("PnpPrinters")]
    [InlineData("PrintQueue")]
    public async Task Tags_WindowsPrinterSetupClasses(string guidName)
    {
        var guid = guidName switch
        {
            "Printer" => Guid.Parse(Periphery.Windows.DeviceClassGuids.Printer),
            "PnpPrinters" => Guid.Parse(Periphery.Windows.DeviceClassGuids.PnpPrinters),
            _ => Guid.Parse(Periphery.Windows.DeviceClassGuids.PrintQueue),
        };
        var d = new DeviceInfo { Id = "w", Category = DeviceCategory.All, ClassGuid = guid };
        var e = await _enricher.EnrichAsync(d, CancellationToken.None);
        Assert.Contains(DeviceTags.Printer, e.Tags);
    }

    [Fact]
    public async Task Tags_UsbPrinterClass0x07()
    {
        var d = new DeviceInfo { Id = "u", Category = DeviceCategory.Usb, UsbClassCode = new UsbClassCode(0x07, 0x00, 0x00) };
        var e = await _enricher.EnrichAsync(d, CancellationToken.None);
        Assert.Contains(DeviceTags.Printer, e.Tags);
    }

    [Fact]
    public async Task DoesNotTag_NonPrinterDevice()
    {
        // A plain HID-class USB device (0x03) is not a printer.
        var d = new DeviceInfo { Id = "x", Category = DeviceCategory.Usb, UsbClassCode = new UsbClassCode(0x03, 0x00, 0x00) };
        var e = await _enricher.EnrichAsync(d, CancellationToken.None);
        Assert.DoesNotContain(DeviceTags.Printer, e.Tags);
        Assert.Same(d, e);
    }

    [Fact]
    public void Declares_EmitsTagsAndScope()
    {
        Assert.Contains(DeviceTags.Printer, _enricher.EmitsTags);
        Assert.Contains(Periphery.Windows.DeviceClassGuids.Printer, _enricher.Scope.WindowsClassGuids);
        Assert.Contains("usb", _enricher.Scope.LinuxSubsystems);
    }

    [Fact]
    public void Instance_IsRegisteredByModuleInitializer()
    {
        Assert.Contains(PrinterEnricher.Instance, DeviceEnrichers.Snapshot());
    }
}
