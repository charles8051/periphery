using System.Collections.Immutable;
using System.Drawing;
using System.Net;
using System.Net.NetworkInformation;
using System.Reflection;

namespace Periphery.Tests;

/// <summary>
/// Unit tests for <see cref="DeviceInfoDiff"/>.
/// </summary>
public class DeviceInfoDiffTests
{
    private static DeviceInfo Base() => new()
    {
        Id                      = "DEV\\001",
        Name                    = "Test Device",
        Category                = DeviceCategory.Usb,
        Manufacturer            = "Acme",
        IsActive              = true,
        Status                  = DeviceStatus.OK,
        BusType                 = BusType.USB,
        BatteryChargePercent    = 80,
        BatteryStatus           = BatteryStatus.Charging,
        IsExternalPowerConnected = true,
    };

    // ── No changes ─────────────────────────────────────────────────────

    [Fact]
    public void Compute_IdenticalSnapshots_ReturnsEmptySet()
    {
        var a = Base();
        var b = Base();

        Assert.Empty(DeviceInfoDiff.Compute(a, b));
    }

    // ── Scalar changes ─────────────────────────────────────────────────

    [Fact]
    public void Compute_BatteryChargePercent_Detected()
    {
        var prev = Base();
        var curr = prev with { BatteryChargePercent = 79 };

        Assert.Contains(nameof(DeviceInfo.BatteryChargePercent), DeviceInfoDiff.Compute(prev, curr));
    }

    [Fact]
    public void Compute_IsExternalPowerConnected_Detected()
    {
        var prev = Base();
        var curr = prev with { IsExternalPowerConnected = false };

        Assert.Contains(nameof(DeviceInfo.IsExternalPowerConnected), DeviceInfoDiff.Compute(prev, curr));
    }

    [Fact]
    public void Compute_IsActive_Detected()
    {
        var prev = Base();
        var curr = prev with { IsActive = false };

        Assert.Contains(nameof(DeviceInfo.IsActive), DeviceInfoDiff.Compute(prev, curr));
    }

    [Fact]
    public void Compute_Name_Detected()
    {
        var prev = Base();
        var curr = prev with { Name = "Renamed Device" };

        Assert.Contains(nameof(DeviceInfo.Name), DeviceInfoDiff.Compute(prev, curr));
    }

    [Fact]
    public void Compute_DisplayResolution_Detected()
    {
        var prev = Base() with { DisplayResolution = new Size(1920, 1080) };
        var curr = prev with { DisplayResolution = new Size(3840, 2160) };

        Assert.Contains(nameof(DeviceInfo.DisplayResolution), DeviceInfoDiff.Compute(prev, curr));
    }

    [Fact]
    public void Compute_DriverVersion_Detected()
    {
        var prev = Base() with { DriverVersion = new Version(1, 0, 0) };
        var curr = prev with { DriverVersion = new Version(2, 0, 0) };

        Assert.Contains(nameof(DeviceInfo.DriverVersion), DeviceInfoDiff.Compute(prev, curr));
    }

    // ── Special-cased types ────────────────────────────────────────────

    [Fact]
    public void Compute_MacAddress_ChangedBytes_Detected()
    {
        var prev = Base() with { MacAddress = new PhysicalAddress([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]) };
        var curr = prev with { MacAddress = new PhysicalAddress([0x00, 0x11, 0x22, 0x33, 0x44, 0xFF]) };

        Assert.Contains(nameof(DeviceInfo.MacAddress), DeviceInfoDiff.Compute(prev, curr));
    }

    [Fact]
    public void Compute_MacAddress_SameBytes_NotDetected()
    {
        var prev = Base() with { MacAddress = new PhysicalAddress([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]) };
        var curr = prev with { MacAddress = new PhysicalAddress([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]) };

        Assert.DoesNotContain(nameof(DeviceInfo.MacAddress), DeviceInfoDiff.Compute(prev, curr));
    }

    [Fact]
    public void Compute_MacAddress_NullToValue_Detected()
    {
        var prev = Base() with { MacAddress = null };
        var curr = prev with { MacAddress = new PhysicalAddress([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]) };

        Assert.Contains(nameof(DeviceInfo.MacAddress), DeviceInfoDiff.Compute(prev, curr));
    }

    [Fact]
    public void Compute_IPAddresses_DifferentAddresses_Detected()
    {
        var prev = Base() with { IPAddresses = ImmutableArray.Create(IPAddress.Parse("192.168.1.1")) };
        var curr = prev with { IPAddresses = ImmutableArray.Create(IPAddress.Parse("10.0.0.1")) };

        Assert.Contains(nameof(DeviceInfo.IPAddresses), DeviceInfoDiff.Compute(prev, curr));
    }

    [Fact]
    public void Compute_IPAddresses_SameAddresses_NotDetected()
    {
        var addr = ImmutableArray.Create(IPAddress.Parse("192.168.1.1"));
        var prev = Base() with { IPAddresses = addr };
        var curr = prev with { IPAddresses = ImmutableArray.Create(IPAddress.Parse("192.168.1.1")) };

        Assert.DoesNotContain(nameof(DeviceInfo.IPAddresses), DeviceInfoDiff.Compute(prev, curr));
    }

    [Fact]
    public void Compute_IPAddresses_DifferentCount_Detected()
    {
        var prev = Base() with { IPAddresses = ImmutableArray.Create(IPAddress.Parse("192.168.1.1")) };
        var curr = prev with
        {
            IPAddresses = ImmutableArray.Create(
                IPAddress.Parse("192.168.1.1"),
                IPAddress.Parse("192.168.1.2"))
        };

        Assert.Contains(nameof(DeviceInfo.IPAddresses), DeviceInfoDiff.Compute(prev, curr));
    }

    // ── Properties bag is excluded ─────────────────────────────────────

    [Fact]
    public void Compute_PropertiesBagChange_NotDetected()
    {
        var prev = Base() with { Properties = new Dictionary<string, object?> { ["Key"] = "old" }.ToImmutableDictionary() };
        var curr = prev with { Properties = new Dictionary<string, object?> { ["Key"] = "new" }.ToImmutableDictionary() };

        // The raw Properties bag is excluded from the diff to avoid noise.
        Assert.Empty(DeviceInfoDiff.Compute(prev, curr));
    }

    // ── Multiple changes in one event ──────────────────────────────────

    [Fact]
    public void Compute_MultipleChanges_AllReported()
    {
        var prev = Base();
        var curr = prev with
        {
            BatteryChargePercent     = 50,
            IsExternalPowerConnected = false,
            BatteryStatus            = BatteryStatus.Discharging,
        };

        var changed = DeviceInfoDiff.Compute(prev, curr);

        Assert.Contains(nameof(DeviceInfo.BatteryChargePercent),     changed);
        Assert.Contains(nameof(DeviceInfo.IsExternalPowerConnected), changed);
        Assert.Contains(nameof(DeviceInfo.BatteryStatus),            changed);
    }

    // ── Rotation (issue #163) ──────────────────────────────────────────

    [Fact]
    public void Compute_PureRotation_WithIdenticalBounds_IsDetected()
    {
        // The primary panel at (0,0): rotating it cannot move the origin, and a
        // square-ish layout can leave the footprint unchanged too. Before
        // DisplayOrientation existed, this snapshot pair was byte-identical and
        // no DevicePropertyChanged was raised at all — the rotation was invisible.
        var prev = Base() with
        {
            Category           = DeviceCategory.Monitor,
            DisplayBounds      = new Rectangle(0, 0, 1280, 1280),
            DisplayOrientation = DisplayOrientation.Landscape,
        };
        var curr = prev with { DisplayOrientation = DisplayOrientation.Portrait };

        var changed = DeviceInfoDiff.Compute(prev, curr);

        Assert.Contains(nameof(DeviceInfo.DisplayOrientation), changed);
        Assert.DoesNotContain(nameof(DeviceInfo.DisplayBounds), changed);
    }

    [Fact]
    public void Compute_RotationBecomingKnown_IsDetected()
    {
        // null (unmeasured — non-Windows, or no DisplayConfig path) → measured.
        var prev = Base() with { Category = DeviceCategory.Monitor };
        var curr = prev with { DisplayOrientation = DisplayOrientation.Landscape };

        Assert.Contains(nameof(DeviceInfo.DisplayOrientation), DeviceInfoDiff.Compute(prev, curr));
    }

    [Fact]
    public void Compute_RotationBecomingUnknown_IsDetected()
    {
        // The inverse, and the one a live system actually hits: the monitor falls
        // out of the DisplayConfig map (driver restart, indirect-virtual source
        // detach) so a measured orientation reverts to unmeasured. `null` means
        // "no longer known", never "unrotated", so it must still diff.
        var prev = Base() with
        {
            Category           = DeviceCategory.Monitor,
            DisplayOrientation = DisplayOrientation.Portrait,
        };
        var curr = prev with { DisplayOrientation = null };

        Assert.Contains(nameof(DeviceInfo.DisplayOrientation), DeviceInfoDiff.Compute(prev, curr));
    }

    // ── Reflection guard: all typed properties are covered ────────────

    /// <summary>
    /// Ensures every typed property on <see cref="DeviceInfo"/> is covered
    /// by <see cref="DeviceInfoDiff.Compute"/>. Detects the case where a new
    /// property is added to DeviceInfo but forgotten in the diff helper.
    /// </summary>
    [Fact]
    public void AllTypedProperties_AreCoveredByDiff()
    {
        // The excluded properties: Id (identity, never changes for same device)
        // and Properties (raw OS bag, intentionally excluded per ADR-0005).
        var excluded = new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(DeviceInfo.Id),
            nameof(DeviceInfo.Properties),
        };

        // Build two snapshots that differ on every typed property.
        var prev = new DeviceInfo
        {
            Id                       = "DEV\\001",
            Name                     = "A",
            Category                 = DeviceCategory.Usb,
            Manufacturer             = "X",
            ClassGuid                = Guid.NewGuid(),
            ContainerId              = Guid.NewGuid(),
            ClassName                = "USB",
            VendorId                 = new HardwareId(0x0001),
            ProductId                = new HardwareId(0x0002),
            SerialNumber             = "SN-1",
            IsActive                 = true,
            Status                   = DeviceStatus.OK,
            BusType                  = BusType.USB,
            LocationPath             = "path-a",
            Driver                   = "drv-a",
            DriverVersion            = new Version(1, 0),
            MacAddress               = new PhysicalAddress([0x00, 0x00, 0x00, 0x00, 0x00, 0x01]),
            IPAddresses              = ImmutableArray.Create(IPAddress.Parse("1.1.1.1")),
            Network                  = new IPNetwork(IPAddress.Parse("192.168.0.0"), 24),
            DisplayResolution        = new Size(1920, 1080),
            DisplayBounds            = new Rectangle(0, 0, 1920, 1080),
            DisplayOrientation       = DisplayOrientation.Landscape,
            MonitorName              = "Monitor A",
            DisplayPhysicalSizeInInches = 24.0f,
            DisplayDpi               = new SizeF(96f, 96f),
            DisplayPhysicalConnector = DisplayConnector.DisplayPort,
            DisplayConnectionKind    = DisplayConnectionKind.Wired,
            DisplayUsageKind         = DisplayUsageKind.Standard,
            DisplayMaxLuminanceInNits = 400f,
            DisplayMaxAvgLuminanceInNits = 300f,
            DisplayMinLuminanceInNits = 0.1f,
            DriveType                = DriveType.Fixed,
            ParentId                 = "parent-a",
            PortNumber               = 1,
            UsbSpeed                 = UsbSpeed.Super,
            MaxPowerMilliamps        = 500,
            UsbClassCode             = new UsbClassCode(0x03, 0x01, 0x01),
            HidUsagePage             = 0x0001,
            HidUsage                 = 0x0002,
            HidMaxInputReportLength  = 64,
            HidMaxOutputReportLength = 64,
            HidMaxFeatureReportLength = 32,
            PortName                 = new SerialPortName("COM1"),
            BatteryChargePercent     = 100,
            BatteryStatus            = BatteryStatus.Charging,
            IsExternalPowerConnected = true,
            IsBatteryLow             = false,
            Subsystem                = "usb",
            IOServiceClass           = "IOUSBDevice",
            Tags                     = [DeviceTags.Hid],
        };

        var curr = new DeviceInfo
        {
            Id                       = "DEV\\001",   // same Id
            Name                     = "B",
            Category                 = DeviceCategory.Bluetooth,
            Manufacturer             = "Y",
            ClassGuid                = Guid.NewGuid(),
            ContainerId              = Guid.NewGuid(),
            ClassName                = "Bluetooth",
            VendorId                 = new HardwareId(0x0003),
            ProductId                = new HardwareId(0x0004),
            SerialNumber             = "SN-2",
            IsActive                 = false,
            Status                   = DeviceStatus.Error,
            BusType                  = BusType.Bluetooth,
            LocationPath             = "path-b",
            Driver                   = "drv-b",
            DriverVersion            = new Version(2, 0),
            MacAddress               = new PhysicalAddress([0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]),
            IPAddresses              = ImmutableArray.Create(IPAddress.Parse("2.2.2.2")),
            Network                  = new IPNetwork(IPAddress.Parse("10.0.0.0"), 8),
            DisplayResolution        = new Size(3840, 2160),
            DisplayBounds            = new Rectangle(1920, 0, 3840, 2160),
            DisplayOrientation       = DisplayOrientation.Portrait,
            MonitorName              = "Monitor B",
            DisplayPhysicalSizeInInches = 32.0f,
            DisplayDpi               = new SizeF(138f, 138f),
            DisplayPhysicalConnector = DisplayConnector.Hdmi,
            DisplayConnectionKind    = DisplayConnectionKind.Wireless,
            DisplayUsageKind         = DisplayUsageKind.HeadMounted,
            DisplayMaxLuminanceInNits = 600f,
            DisplayMaxAvgLuminanceInNits = 450f,
            DisplayMinLuminanceInNits = 0.05f,
            DriveType                = DriveType.Removable,
            ParentId                 = "parent-b",
            PortNumber               = 2,
            UsbSpeed                 = UsbSpeed.High,
            MaxPowerMilliamps        = 100,
            UsbClassCode             = new UsbClassCode(0x08, 0x06, 0x50),
            HidUsagePage             = 0x000D,
            HidUsage                 = 0x0005,
            HidMaxInputReportLength  = 128,
            HidMaxOutputReportLength = 128,
            HidMaxFeatureReportLength = 64,
            PortName                 = new SerialPortName("COM2"),
            BatteryChargePercent     = 0,
            BatteryStatus            = BatteryStatus.Discharging,
            IsExternalPowerConnected = false,
            IsBatteryLow             = true,
            Subsystem                = "bt",
            IOServiceClass           = "IOBluetoothDevice",
            Tags                     = [DeviceTags.Hid, DeviceTags.Battery],
        };

        var changed = DeviceInfoDiff.Compute(prev, curr);

        // Every typed property on DeviceInfo (except excluded ones) should appear.
        var allProps = typeof(DeviceInfo)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .Where(n => !excluded.Contains(n))
            .ToList();

        var missing = allProps.Except(changed).ToList();
        Assert.True(missing.Count == 0,
            $"DeviceInfoDiff.Compute did not report changes for: {string.Join(", ", missing)}. " +
            "Add these properties to DeviceInfoDiff.Compute.");
    }
}
