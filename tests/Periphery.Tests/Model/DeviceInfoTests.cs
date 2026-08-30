using System.Collections.Immutable;
using System.Drawing;
using System.Net;
using System.Net.NetworkInformation;

namespace Periphery.Tests;

public class DeviceInfoTests
{
    // ── ToString ───────────────────────────────────────────────────────

    [Fact]
    public void ToString_WithName_ReturnsNameAndId()
    {
        var device = new DeviceInfo { Id = "USB\\VID_046D", Name = "Logitech Mouse" };
        Assert.Equal("Logitech Mouse (USB\\VID_046D)", device.ToString());
    }

    [Fact]
    public void ToString_WithoutName_ReturnsId()
    {
        var device = new DeviceInfo { Id = "USB\\VID_046D" };
        Assert.Equal("USB\\VID_046D", device.ToString());
    }

    // ── Defaults ───────────────────────────────────────────────────────

    [Fact]
    public void Defaults_NullableFieldsAreNull()
    {
        var device = new DeviceInfo { Id = "test" };

        Assert.Null(device.Name);
        Assert.Null(device.Manufacturer);
        Assert.Null(device.ClassGuid);
        Assert.Null(device.ClassName);
        Assert.Null(device.ContainerId);
        Assert.Null(device.VendorId);
        Assert.Null(device.ProductId);
        Assert.Null(device.SerialNumber);
        Assert.Null(device.LocationPath);
        Assert.Null(device.Driver);
        Assert.Null(device.DriverVersion);
        Assert.Null(device.MacAddress);
        Assert.Null(device.IPAddresses);
        Assert.Null(device.Network);
        Assert.Null(device.DisplayResolution);
        Assert.Null(device.DisplayBounds);
        Assert.Null(device.DisplayOrientation);
        Assert.Null(device.MonitorName);
        Assert.Null(device.DisplayPhysicalSizeInInches);
        Assert.Null(device.DisplayDpi);
        Assert.Null(device.DisplayPhysicalConnector);
        Assert.Null(device.DisplayConnectionKind);
        Assert.Null(device.DisplayUsageKind);
        Assert.Null(device.DisplayMaxLuminanceInNits);
        Assert.Null(device.DisplayMaxAvgLuminanceInNits);
        Assert.Null(device.DisplayMinLuminanceInNits);
        Assert.Null(device.DriveType);
        Assert.Null(device.ParentId);
        Assert.Null(device.PortNumber);
        Assert.Null(device.UsbSpeed);
        Assert.Null(device.MaxPowerMilliamps);
        Assert.Null(device.UsbClassCode);
        Assert.Null(device.PortName);
        Assert.Null(device.BatteryChargePercent);
        Assert.Null(device.BatteryStatus);
        Assert.Null(device.IsExternalPowerConnected);
        Assert.Null(device.Subsystem);
        Assert.Null(device.IOServiceClass);
    }

    [Fact]
    public void Defaults_ValueTypesHaveDefaultValues()
    {
        var device = new DeviceInfo { Id = "test" };

        Assert.Equal(DeviceCategory.All, device.Category);
        Assert.Equal(DeviceStatus.Unknown, device.Status);
        Assert.Equal(BusType.Unknown, device.BusType);
        Assert.False(device.IsActive);
    }

    [Fact]
    public void Defaults_PropertiesIsEmptyDictionary()
    {
        var device = new DeviceInfo { Id = "test" };
        Assert.NotNull(device.Properties);
        Assert.Empty(device.Properties);
    }

    // ── Property initialization ────────────────────────────────────────

    [Fact]
    public void AllProperties_CanBeInitialized()
    {
        var vid = new HardwareId(0x046D);
        var pid = new HardwareId(0xC077);
        var classGuid = Guid.NewGuid();
        var containerId = Guid.NewGuid();
        var driverVersion = new Version(10, 0, 19041, 1);
        var mac = PhysicalAddress.Parse("00-1A-2B-3C-4D-5E");
        var ipAddresses = ImmutableArray.Create(IPAddress.Loopback);
        var network = new IPNetwork(IPAddress.Parse("192.168.1.0"), 24);
        var resolution = new Size(1920, 1080);
        var bounds = new Rectangle(0, 0, 2560, 1440);
        var props = ImmutableDictionary.CreateRange(new Dictionary<string, object?> { ["Custom"] = "value" });

        var usbClass = new UsbClassCode(0x03, 0x01, 0x02);
        var portName = new SerialPortName("COM3");

        var device = new DeviceInfo
        {
            Id = "USB\\VID_046D&PID_C077\\1234",
            Name = "Logitech Mouse",
            Category = DeviceCategory.Hid,
            Manufacturer = "Logitech",
            ClassGuid = classGuid,
            ClassName = "HID",
            ContainerId = containerId,
            VendorId = vid,
            ProductId = pid,
            SerialNumber = "1234",
            IsActive = true,
            Status = DeviceStatus.OK,
            BusType = BusType.USB,
            LocationPath = "USB\\VID_046D&PID_C077\\1234",
            Driver = "HidUsb",
            DriverVersion = driverVersion,
            MacAddress = mac,
            IPAddresses = ipAddresses,
            Network = network,
            DisplayResolution = resolution,
            DisplayBounds = bounds,
            DisplayOrientation = Periphery.DisplayOrientation.Portrait,
            MonitorName = "ASUS VN248",
            DisplayPhysicalSizeInInches = 23.8f,
            DisplayDpi = new SizeF(93.6f, 93.6f),
            DisplayPhysicalConnector = DisplayConnector.Hdmi,
            DisplayConnectionKind = Periphery.DisplayConnectionKind.Wired,
            DisplayUsageKind = Periphery.DisplayUsageKind.Standard,
            DisplayMaxLuminanceInNits = 400f,
            DisplayMaxAvgLuminanceInNits = 300f,
            DisplayMinLuminanceInNits = 0.3f,
            DriveType = DriveType.Fixed,
            ParentId = "USB\\ROOT_HUB30\\4&1234",
            PortNumber = 3,
            UsbSpeed = Periphery.UsbSpeed.High,
            MaxPowerMilliamps = 500,
            UsbClassCode = usbClass,
            PortName = portName,
            BatteryChargePercent = 85,
            BatteryStatus = Periphery.BatteryStatus.Charging,
            IsExternalPowerConnected = true,
            Subsystem = "usb",
            IOServiceClass = "IOUSBDevice",
            Properties = props,
        };

        Assert.Equal("USB\\VID_046D&PID_C077\\1234", device.Id);
        Assert.Equal("Logitech Mouse", device.Name);
        Assert.Equal(DeviceCategory.Hid, device.Category);
        Assert.Equal("Logitech", device.Manufacturer);
        Assert.Equal(classGuid, device.ClassGuid);
        Assert.Equal("HID", device.ClassName);
        Assert.Equal(containerId, device.ContainerId);
        Assert.Equal(vid, device.VendorId);
        Assert.Equal(pid, device.ProductId);
        Assert.Equal("1234", device.SerialNumber);
        Assert.True(device.IsActive);
        Assert.Equal(DeviceStatus.OK, device.Status);
        Assert.Equal(BusType.USB, device.BusType);
        Assert.Equal("HidUsb", device.Driver);
        Assert.Equal(driverVersion, device.DriverVersion);
        Assert.Equal(mac, device.MacAddress);
        Assert.Equal(ipAddresses, device.IPAddresses);
        Assert.Equal(network, device.Network);
        Assert.Equal(resolution, device.DisplayResolution);
        Assert.Equal(bounds, device.DisplayBounds);
        Assert.Equal(Periphery.DisplayOrientation.Portrait, device.DisplayOrientation);
        Assert.Equal("ASUS VN248", device.MonitorName);
        Assert.Equal(23.8f, device.DisplayPhysicalSizeInInches);
        Assert.Equal(new SizeF(93.6f, 93.6f), device.DisplayDpi);
        Assert.Equal(DisplayConnector.Hdmi, device.DisplayPhysicalConnector);
        Assert.Equal(Periphery.DisplayConnectionKind.Wired, device.DisplayConnectionKind);
        Assert.Equal(Periphery.DisplayUsageKind.Standard, device.DisplayUsageKind);
        Assert.Equal(400f, device.DisplayMaxLuminanceInNits);
        Assert.Equal(300f, device.DisplayMaxAvgLuminanceInNits);
        Assert.Equal(0.3f, device.DisplayMinLuminanceInNits);
        Assert.Equal(DriveType.Fixed, device.DriveType);
        Assert.Equal("USB\\ROOT_HUB30\\4&1234", device.ParentId);
        Assert.Equal(3, device.PortNumber);
        Assert.Equal(Periphery.UsbSpeed.High, device.UsbSpeed);
        Assert.Equal(500, device.MaxPowerMilliamps);
        Assert.Equal(usbClass, device.UsbClassCode);
        Assert.Equal(portName, device.PortName);
        Assert.Equal(85, device.BatteryChargePercent);
        Assert.Equal(Periphery.BatteryStatus.Charging, device.BatteryStatus);
        Assert.True(device.IsExternalPowerConnected);
        Assert.Equal("usb", device.Subsystem);
        Assert.Equal("IOUSBDevice", device.IOServiceClass);
        Assert.Same(props, device.Properties);
    }

    // ── Record equality ────────────────────────────────────────────────

    [Fact]
    public void RecordEquality_SameValues_AreEqual()
    {
        var a = new DeviceInfo { Id = "test", Name = "Device", IsActive = true };
        var b = new DeviceInfo { Id = "test", Name = "Device", IsActive = true };
        Assert.Equal(a, b);
    }

    [Fact]
    public void RecordEquality_SameValuesWithProperties_AreEqual()
    {
        var props = ImmutableDictionary.CreateRange(new Dictionary<string, object?> { ["key"] = "value" });
        var a = new DeviceInfo { Id = "test", Name = "Device", Properties = props };
        var b = new DeviceInfo { Id = "test", Name = "Device", Properties = props };
        Assert.Equal(a, b);
    }

    [Fact]
    public void RecordEquality_DifferentProperties_AreNotEqual()
    {
        var propsA = ImmutableDictionary.CreateRange(new Dictionary<string, object?> { ["key"] = "valueA" });
        var propsB = ImmutableDictionary.CreateRange(new Dictionary<string, object?> { ["key"] = "valueB" });
        var a = new DeviceInfo { Id = "test", Properties = propsA };
        var b = new DeviceInfo { Id = "test", Properties = propsB };
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void RecordEquality_DifferentId_AreNotEqual()
    {
        var a = new DeviceInfo { Id = "test1" };
        var b = new DeviceInfo { Id = "test2" };
        Assert.NotEqual(a, b);
    }

    // ── with expression ────────────────────────────────────────────────

    [Fact]
    public void WithExpression_CreatesModifiedCopy()
    {
        var original = new DeviceInfo { Id = "test", Name = "Original", IsActive = false };
        var copy = original with { IsActive = true };

        Assert.False(original.IsActive);
        Assert.True(copy.IsActive);
        Assert.Equal("Original", copy.Name);
        Assert.Equal("test", copy.Id);
    }

    // ── Tags (ADR-0047) ────────────────────────────────────────────────

    [Fact]
    public void Tags_DefaultsToEmptyImmutableSet()
    {
        var device = new DeviceInfo { Id = "test" };
        Assert.NotNull(device.Tags);
        Assert.Empty(device.Tags);
    }

    [Fact]
    public void Tags_CanBeSetViaInit_AndContainsLookup()
    {
        var device = new DeviceInfo
        {
            Id = "ups",
            Tags = [DeviceTags.Hid, DeviceTags.Battery],
        };
        Assert.Equal(2, device.Tags.Count);
        Assert.Contains(DeviceTags.Hid, device.Tags);
        Assert.Contains(DeviceTags.Battery, device.Tags);
    }

    [Fact]
    public void Tags_AddViaWithExpression_DoesNotMutateOriginal()
    {
        var original = new DeviceInfo { Id = "test" };
        var tagged = original with { Tags = original.Tags.Add(DeviceTags.Battery) };

        Assert.Empty(original.Tags);
        Assert.Single(tagged.Tags);
        Assert.Contains(DeviceTags.Battery, tagged.Tags);
    }

    [Fact]
    public void Tags_RecordEquality_ReferenceBased()
    {
        // FINDING (ADR-0047 spike): the default record-generated Equals
        // uses EqualityComparer<ImmutableHashSet<string>>.Default for the
        // Tags property, which is reference equality. Two records with
        // logically-equal-but-distinct tag sets compare unequal.
        // This may cause DeviceTracker change-detection flicker when an
        // enricher re-runs and produces the same content in a new
        // instance. Documented here as the spike's biggest design
        // surprise; resolution discussed in the ADR's Status update.
        var a = new DeviceInfo { Id = "test", Tags = [DeviceTags.Battery] };
        var b = new DeviceInfo { Id = "test", Tags = [DeviceTags.Battery] };

        // Both records have logically identical Tags, but the underlying
        // ImmutableHashSet instances are distinct. Current behaviour:
        Assert.NotEqual(a, b);

        // Sanity: SetEquals does the right thing (proof that the content
        // matches; equality is the wrong shape, not the data).
        Assert.True(a.Tags.SetEquals(b.Tags));
    }

    [Fact]
    public void Tags_RecordEquality_SameInstance_IsEqual()
    {
        // Records do compare equal when the Tags property shares the
        // same underlying instance — e.g. when produced by a single
        // `with` chain or the default Empty singleton.
        var template = new DeviceInfo { Id = "test", Tags = [DeviceTags.Battery] };
        var copyA = template with { Name = "A" };
        var copyB = template with { Name = "B" };

        // Both copies inherit Tags by reference from `template`.
        Assert.Same(template.Tags, copyA.Tags);
        Assert.Same(template.Tags, copyB.Tags);
        // So with everything else equal, two with-expression copies
        // off the same template ARE equal:
        var siblingA = template with { Name = "X" };
        var siblingB = template with { Name = "X" };
        Assert.Equal(siblingA, siblingB);
    }
}
