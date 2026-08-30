using System.Collections.Immutable;

namespace Periphery.Hid.Tests;

/// <summary>
/// Behaviour pinning for the ADR-0026-compliant <see cref="HidBatteryEnricher"/>.
/// Pure metadata logic — no I/O, no handle opens, no calls into HidDevice.
/// Tests use a fresh enricher instance to stay independent of the
/// process-wide <see cref="DeviceEnrichers"/> registry (the singleton
/// <see cref="HidBatteryEnricher.Instance"/> is registered there by the
/// module initializer and is exercised end-to-end by integration tests).
/// </summary>
[Collection(nameof(HidQuirksTestCollection))]
public class HidBatteryEnricherTests : IDisposable
{
    private readonly HidBatteryEnricher _enricher = new();

    public HidBatteryEnricherTests() => HidQuirks.ResetForTests();
    public void Dispose() => HidQuirks.ResetForTests();

    [Fact]
    public async Task Enrich_WayTechHidDevice_AddsBatteryTag()
    {
        // WayTech (Cypress 0665:5161) is in the baseline registration.
        var device = new DeviceInfo
        {
            Id = "ups-id",
            Category = DeviceCategory.Hid,
            BusType = BusType.HID,
            VendorId = new HardwareId(0x0665),
            ProductId = new HardwareId(0x5161),
        };

        var enriched = await _enricher.EnrichAsync(device, CancellationToken.None);

        Assert.Contains(DeviceTags.Battery, enriched.Tags);
    }

    [Fact]
    public async Task Enrich_DoesNotPopulateBatteryFields()
    {
        // ADR-0026 invariant: enricher is OS-metadata only. Battery
        // fields on DeviceInfo stay null — they're populated only when
        // the OS itself reports them (system batteries via core's
        // WindowsBatteryEnricher), never via a snapshot read.
        var device = new DeviceInfo
        {
            Id = "ups-id",
            Category = DeviceCategory.Hid,
            BusType = BusType.HID,
            VendorId = new HardwareId(0x0665),
            ProductId = new HardwareId(0x5161),
        };

        var enriched = await _enricher.EnrichAsync(device, CancellationToken.None);

        Assert.Null(enriched.BatteryChargePercent);
        Assert.Null(enriched.BatteryStatus);
        Assert.Null(enriched.IsExternalPowerConnected);
        Assert.Null(enriched.IsBatteryLow);
    }

    [Fact]
    public void CanEnrich_NonHidCategory_ReturnsFalse()
    {
        var device = new DeviceInfo
        {
            Id = "usb-id",
            Category = DeviceCategory.Usb,
            BusType = BusType.HID,
            VendorId = new HardwareId(0x0665),
            ProductId = new HardwareId(0x5161),
        };

        Assert.False(_enricher.CanEnrich(device));
    }

    [Fact]
    public void CanEnrich_NonHidBusType_ReturnsFalse()
    {
        // Real case from enumeration of the WayTech UPS: composite
        // device exposes both a USB-bus parent (Category=Hid, BusType=USB)
        // and a HID-bus child (Category=Hid, BusType=HID), both with VID
        // 0665 PID 5161. Only the HID-bus child has a HID device interface
        // CreateFile can open; tagging the USB parent would cause
        // downstream snapshot reads to fail with ERROR_FILE_NOT_FOUND.
        var usbParent = new DeviceInfo
        {
            Id = @"USB\VID_0665&PID_5161\5&293A402A&0&10",
            Category = DeviceCategory.Hid,
            BusType = BusType.USB,
            VendorId = new HardwareId(0x0665),
            ProductId = new HardwareId(0x5161),
        };

        Assert.False(_enricher.CanEnrich(usbParent));
    }

    [Fact]
    public async Task EnrichAsync_NonMatchingCanEnrich_ReturnsSameInstance()
    {
        var device = new DeviceInfo
        {
            Id = "usb-id",
            Category = DeviceCategory.Usb,
            VendorId = new HardwareId(0x0665),
            ProductId = new HardwareId(0x5161),
        };

        var enriched = await _enricher.EnrichAsync(device, CancellationToken.None);
        Assert.Same(device, enriched);
    }

    [Fact]
    public async Task EnrichAsync_MissingVendorId_ReturnsSameInstance()
    {
        var device = new DeviceInfo
        {
            Id = "no-vid",
            Category = DeviceCategory.Hid,
            BusType = BusType.HID,
            VendorId = null,
            ProductId = new HardwareId(0x5161),
        };

        Assert.Same(device, await _enricher.EnrichAsync(device, CancellationToken.None));
    }

    [Fact]
    public async Task EnrichAsync_MissingProductId_ReturnsSameInstance()
    {
        var device = new DeviceInfo
        {
            Id = "no-pid",
            Category = DeviceCategory.Hid,
            BusType = BusType.HID,
            VendorId = new HardwareId(0x0665),
            ProductId = null,
        };

        Assert.Same(device, await _enricher.EnrichAsync(device, CancellationToken.None));
    }

    [Fact]
    public async Task EnrichAsync_UnknownVidPid_ReturnsSameInstance()
    {
        var device = new DeviceInfo
        {
            Id = "unknown-hid",
            Category = DeviceCategory.Hid,
            BusType = BusType.HID,
            VendorId = new HardwareId(0xDEAD),
            ProductId = new HardwareId(0xBEEF),
        };

        // No codec in HidQuirks for DEAD:BEEF → no Battery tag.
        var enriched = await _enricher.EnrichAsync(device, CancellationToken.None);
        Assert.Same(device, enriched);
        Assert.DoesNotContain(DeviceTags.Battery, enriched.Tags);
    }

    [Fact]
    public async Task EnrichAsync_AlreadyTagged_DoesNotDuplicate()
    {
        // BusType.HID set so the test reaches the Tags.Contains
        // short-circuit (the CanEnrich guard would otherwise short-circuit
        // first and the test would pass for the wrong reason).
        var device = new DeviceInfo
        {
            Id = "already-tagged",
            Category = DeviceCategory.Hid,
            BusType = BusType.HID,
            VendorId = new HardwareId(0x0665),
            ProductId = new HardwareId(0x5161),
            Tags = [DeviceTags.Battery, DeviceTags.Hid],
        };

        var enriched = await _enricher.EnrichAsync(device, CancellationToken.None);

        // Idempotent — re-enriching doesn't grow the set.
        Assert.Same(device, enriched);
        Assert.Equal(2, enriched.Tags.Count);
    }

    [Fact]
    public async Task EnrichAsync_NullDevice_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => _enricher.EnrichAsync(null!, CancellationToken.None));
    }

    [Fact]
    public void CanEnrich_NullDevice_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => _enricher.CanEnrich(null!));
    }

    [Fact]
    public void Instance_IsRegisteredByModuleInitializer()
    {
        // Periphery.Hid's [ModuleInitializer] auto-registers
        // HidBatteryEnricher.Instance with DeviceEnrichers so consumers
        // don't have to call Enrich themselves. Any test that loads
        // Periphery.Hid (i.e. every test in this assembly) implicitly
        // triggers the initializer.
        Assert.Contains(HidBatteryEnricher.Instance, DeviceEnrichers.Snapshot());
    }

    [Fact]
    public void EmitsTags_DeclaresBatteryOnly()
    {
        // ADR-0051 §5 declarative half. EnrichAsync only ever adds the Battery
        // tag, so that is the complete EmitsTags set.
        Assert.Contains(DeviceTags.Battery, _enricher.EmitsTags);
        Assert.Single(_enricher.EmitsTags);
    }

    [Fact]
    public void Scope_CoversHidSubsystemOnEveryPlatform()
    {
        // A HID-class UPS enumerates under the HID subsystem on each platform;
        // the declared scope lets a bare WithTag(Battery) query reach it once
        // provider activation lands. Tokens mirror the HID arms of the three
        // platform category maps.
        var scope = _enricher.Scope;
        Assert.Contains(Periphery.Windows.DeviceClassGuids.HidClass, scope.WindowsClassGuids);
        Assert.Contains("hid", scope.LinuxSubsystems);
        Assert.Contains("IOHIDDevice", scope.MacOSClasses);
    }
}
