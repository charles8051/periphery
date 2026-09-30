// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Collections.Immutable;
using System.Net.NetworkInformation;
using System.Runtime.Versioning;

namespace Periphery.MacOS.Core;

/// <summary>
/// A registry entry's properties as plain values: <see cref="string"/>, <see cref="long"/>,
/// <see cref="bool"/> or <see cref="byte"/>[]. The provider reads the keys <see cref="IOKitDeviceMap"/>
/// names and nothing else.
/// </summary>
internal sealed record IOKitProperties(ImmutableDictionary<string, object> Values)
{
    public static IOKitProperties Empty { get; } = new(ImmutableDictionary<string, object>.Empty);

    public string? String(string key) => Values.GetValueOrDefault(key) as string;

    public long? Long(string key) => Values.GetValueOrDefault(key) is long value ? value : null;

    public int? Int(string key) => Long(key) is long value and >= int.MinValue and <= int.MaxValue ? (int)value : null;

    public bool? Bool(string key) => Values.GetValueOrDefault(key) is bool value ? value : null;

    public byte[]? Data(string key) => Values.GetValueOrDefault(key) as byte[];
}

/// <summary>
/// Maps one IOKit registry entry to a <see cref="DeviceInfo"/>. Pure: the provider reads the
/// entry's properties, and adds network addresses and enrichment afterwards.
/// </summary>
[SupportedOSPlatform("macos")]
internal static class IOKitDeviceMap
{
    /// <summary>Keys read as strings.</summary>
    internal static readonly ImmutableArray<string> StringKeys =
    [
        "USB Product Name", "Product", "IOHIDProduct", "IOTTYDevice", "IOClass",
        "USB Vendor Name", "Manufacturer", "IOHIDManufacturer",
        "USB Serial Number", "SerialNumber", "IOHIDSerialNumber",
        "CFBundleIdentifier", "CFBundleVersion", "BSD Name", "IOCalloutDevice", "IODialinDevice",
    ];

    /// <summary>Keys read as numbers.</summary>
    internal static readonly ImmutableArray<string> NumberKeys =
    [
        "idVendor", "VendorID", "HIDVendorID", "idProduct", "ProductID", "HIDProductID",
        "PrimaryUsagePage", "PrimaryUsage", "bDeviceClass", "sessionID", "CurrentCapacity", "MaxCapacity",
    ];

    /// <summary>Keys read as booleans.</summary>
    internal static readonly ImmutableArray<string> BoolKeys = ["IsCharging", "ExternalConnected"];

    /// <summary>Keys read as data.</summary>
    internal static readonly ImmutableArray<string> DataKeys = ["IOMACAddress"];

    // Classes whose presence in the registry means the device is in use. A USB device instead
    // publishes sessionID, which no IOHIDDevice does (#203).
    private static readonly ImmutableHashSet<string> ActiveWhilePresent =
    [
        MacOSCategoryMap.AppleSmartBattery,
        MacOSCategoryMap.IONetworkInterface,
        MacOSCategoryMap.IODisplayConnect,
        MacOSCategoryMap.IOSerialBSDClient,
        MacOSCategoryMap.IOHIDDevice,
    ];

    /// <summary>The entry when its properties could not be read.</summary>
    internal static DeviceInfo Unreadable(string deviceId, string ioKitClass) => new()
    {
        Id = deviceId,
        Category = MacOSCategoryMap.ResolveCategory(ioKitClass),
        IOServiceClass = ioKitClass,
        BusType = MacOSCategoryMap.InferBusType(ioKitClass),
    };

    internal static DeviceInfo ToDeviceInfo(string deviceId, string ioKitClass, IOKitProperties p)
    {
        // A serial client carries no product name; its tty name identifies it.
        string? name = p.String("USB Product Name")
            ?? p.String("Product")
            ?? p.String("IOHIDProduct")
            ?? (ioKitClass == MacOSCategoryMap.IOSerialBSDClient ? p.String("IOTTYDevice") : null)
            ?? p.String("IOClass");

        // IOHIDDevice publishes VendorID, ProductID, Manufacturer and SerialNumber; a USB device
        // publishes the idVendor and "USB …" forms.
        int? vid = p.Int("idVendor") ?? p.Int("VendorID") ?? p.Int("HIDVendorID");
        int? pid = p.Int("idProduct") ?? p.Int("ProductID") ?? p.Int("HIDProductID");

        DeviceCategory category = MacOSCategoryMap.ResolveCategory(ioKitClass);
        int? hidUsagePage = null;
        int? hidUsage = null;
        if (ioKitClass == MacOSCategoryMap.IOHIDDevice)
        {
            hidUsagePage = p.Int("PrimaryUsagePage");
            hidUsage = p.Int("PrimaryUsage");
            category = MacOSCategoryMap.ResolveHidCategory(hidUsagePage, hidUsage);
        }
        else if (ioKitClass is MacOSCategoryMap.IOUSBDevice or MacOSCategoryMap.IOUSBHostDevice)
        {
            // Tier 2 (ADR-0013): refine USB category via bDeviceClass descriptor field
            category = MacOSCategoryMap.ResolveUsbCategory(p.Int("bDeviceClass")) ?? category;
        }

        string? driverVersion = p.String("CFBundleVersion");

        return new DeviceInfo
        {
            Id = deviceId,
            Name = name,
            Category = category,
            Manufacturer = p.String("USB Vendor Name") ?? p.String("Manufacturer") ?? p.String("IOHIDManufacturer"),
            VendorId = vid is > 0 and <= ushort.MaxValue ? (HardwareId)(ushort)vid.Value : null,
            ProductId = pid is > 0 and <= ushort.MaxValue ? (HardwareId)(ushort)pid.Value : null,
            SerialNumber = p.String("USB Serial Number") ?? p.String("SerialNumber") ?? p.String("IOHIDSerialNumber"),
            IsActive = p.Long("sessionID") is not null || ActiveWhilePresent.Contains(ioKitClass),
            Status = DeviceStatus.OK,
            BusType = MacOSCategoryMap.InferBusType(ioKitClass),
            LocationPath = $"IOService:/{ioKitClass}/{deviceId}",
            Driver = p.String("CFBundleIdentifier"),
            DriverVersion = driverVersion is not null && Version.TryParse(driverVersion, out var version) ? version : null,
            MacAddress = p.Data("IOMACAddress") is { Length: 6 } mac ? new PhysicalAddress(mac) : null,
            PortName = SerialPortOf(ioKitClass, p),
            HidUsagePage = (ushort?)hidUsagePage,
            HidUsage = (ushort?)hidUsage,
            IOServiceClass = ioKitClass,
            Properties = ImmutableDictionary<string, object?>.Empty,
        }.WithBattery(ioKitClass, p);
    }

    // ADR-0013 Tier 1: prefer the callout device (/dev/cu.*), the path for initiating I/O.
    private static SerialPortName? SerialPortOf(string ioKitClass, IOKitProperties p) =>
        ioKitClass == MacOSCategoryMap.IOSerialBSDClient && (p.String("IOCalloutDevice") ?? p.String("IODialinDevice")) is { } path
            ? new SerialPortName(path)
            : null;

    private static DeviceInfo WithBattery(this DeviceInfo device, string ioKitClass, IOKitProperties p)
    {
        if (ioKitClass != MacOSCategoryMap.AppleSmartBattery)
            return device;

        int? current = p.Int("CurrentCapacity");
        int? max = p.Int("MaxCapacity");
        int? percent = current is not null && max is > 0 ? (int)((double)current.Value / max.Value * 100) : null;
        bool? charging = p.Bool("IsCharging");
        bool? external = p.Bool("ExternalConnected");

        return device with
        {
            BatteryChargePercent = percent,
            IsExternalPowerConnected = external,
            BatteryStatus = (charging, external, percent) switch
            {
                (true, _, _) => Periphery.BatteryStatus.Charging,
                (false, true, >= 100) => Periphery.BatteryStatus.Full,
                (false, true, _) => Periphery.BatteryStatus.NotCharging,
                (false, false or null, _) => Periphery.BatteryStatus.Discharging,
                _ => Periphery.BatteryStatus.Unknown,
            },
        };
    }
}
