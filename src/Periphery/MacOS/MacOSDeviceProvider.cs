// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Periphery.MacOS;

/// <summary>
/// macOS implementation of <see cref="IDeviceProvider"/> using IOKit P/Invoke
/// for device enumeration and property retrieval.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class MacOSDeviceProvider : IDeviceProvider
{
    private static readonly ILogger<MacOSDeviceProvider> _logger =
        PeripheryLoggerFactory.CreateLogger<MacOSDeviceProvider>();

    public async IAsyncEnumerable<DeviceInfo> EnumerateAsync(
        DeviceFilter filter,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        _logger.LogDebug("Starting device enumeration via IOKit");

        if (!IOKitInterop.IsIOKitAvailable())
            throw new DeviceProviderException(
                "IOKit.framework could not be loaded. Ensure you are running on macOS.");

        int deviceCount = 0;
        int skippedCount = 0;

        // Build network interface IP lookup once for the entire enumeration
        var networkInfo = BuildNetworkInterfaceInfo();

        // Push category filter down to IOKit class selection
        string[] ioKitClasses = MacOSCategoryMap.GetIOKitClasses(
            filter.Category.HasValue && filter.Category.Value != DeviceCategory.All
                ? filter.Category.Value
                : null);

        _logger.LogInformation("Enumerating devices via IOKit (classes: {ClassFilter})",
            ioKitClasses.Length == 0 ? "none" : string.Join(", ", ioKitClasses));

        // Track seen registry entry IDs to deduplicate across IOUSBDevice / IOUSBHostDevice
        var seenEntryIds = new HashSet<ulong>();

        foreach (var ioKitClass in ioKitClasses)
        {
            ct.ThrowIfCancellationRequested();

            IntPtr matchingDict = IOKitInterop.IOServiceMatching(ioKitClass);
            if (matchingDict == IntPtr.Zero)
            {
                _logger.LogWarning("IOServiceMatching returned null for class {Class}", ioKitClass);
                continue;
            }

            // IOServiceGetMatchingServices consumes the matchingDict reference (no CFRelease needed)
            int kr = IOKitInterop.IOServiceGetMatchingServices(
                IOKitInterop.kIOMasterPortDefault, matchingDict, out uint iterator);

            if (kr != IOKitInterop.kIOReturnSuccess)
            {
                _logger.LogWarning(
                    "IOServiceGetMatchingServices failed for class {Class}: kr=0x{KernReturn:X8}",
                    ioKitClass, kr);
                continue;
            }

            try
            {
                uint service;
                while ((service = IOKitInterop.IOIteratorNext(iterator)) != 0)
                {
                    ct.ThrowIfCancellationRequested();
                    DeviceInfo? device = null;
                    try
                    {
                        // Deduplicate by registry entry ID
                        int idKr = IOKitInterop.IORegistryEntryGetRegistryEntryID(service, out ulong entryId);
                        if (idKr == IOKitInterop.kIOReturnSuccess && !seenEntryIds.Add(entryId))
                            continue; // Already seen from another IOKit class query

                        device = ToDeviceInfo(service, ioKitClass, networkInfo);
                        deviceCount++;
                    }
                    catch (Exception ex)
                    {
                        skippedCount++;
                        _logger.LogWarning(ex,
                            "Failed to parse IOKit service (class: {Class}), skipping. Total skipped: {SkippedCount}",
                            ioKitClass, skippedCount);

                        System.Diagnostics.Debug.WriteLine(
                            $"Failed to parse IOKit service ({ioKitClass}): {ex.Message}");
                    }
                    finally
                    {
                        IOKitInterop.IOObjectRelease(service);
                    }

                    if (device is not null)
                        yield return device;
                }
            }
            finally
            {
                IOKitInterop.IOObjectRelease(iterator);
            }
        }

        _logger.LogInformation(
            "Device enumeration completed. Found: {DeviceCount}, Skipped: {SkippedCount}",
            deviceCount, skippedCount);

        // Satisfy the compiler: async iterator must contain at least one await
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>
    /// Builds a <see cref="DeviceInfo"/> from an IOKit service entry.
    /// </summary>
    internal static DeviceInfo ToDeviceInfo(
        uint service, string ioKitClass,
        Dictionary<string, NetworkInterfaceEntry>? networkInfo = null)
    {
        // Read the registry entry ID as the unique device identifier
        IOKitInterop.IORegistryEntryGetRegistryEntryID(service, out ulong entryId);
        string deviceId = entryId.ToString();

        // Read all properties from the service entry
        int kr = IOKitInterop.IORegistryEntryCreateCFProperties(
            service, out IntPtr properties, IntPtr.Zero, 0);

        DeviceInfo device;
        if (kr != IOKitInterop.kIOReturnSuccess || properties == IntPtr.Zero)
        {
            device = new DeviceInfo
            {
                Id = deviceId,
                Category = MacOSCategoryMap.ResolveCategory(ioKitClass),
                IOServiceClass = ioKitClass,
                BusType = MacOSCategoryMap.InferBusType(ioKitClass),
            };
        }
        else
        {
            try
            {
                device = BuildDeviceInfoFromProperties(service, deviceId, ioKitClass, properties, networkInfo);
            }
            finally
            {
                IOKitInterop.CFRelease(properties);
            }
        }

        // ADR-0051 §5: run the registered enricher pass so capability tags
        // (and any future typed enrichment) are present on every macOS device,
        // matching the Windows provider. Centralised in the single builder that
        // every enumerate and monitor path funnels through, so no call site is
        // missed and monitor diffs compare enriched-against-enriched.
        return EnrichmentPipeline.RunRegisteredSync(device, CancellationToken.None, _logger);
    }

    /// <summary>
    /// Attempts to build a <see cref="DeviceInfo"/> from a registry entry ID string.
    /// Used by the monitor provider when a device arrives.
    /// Returns <c>null</c> if the service cannot be read.
    /// </summary>
    internal static DeviceInfo? TryBuildDeviceInfo(uint service, string ioKitClass)
    {
        try
        {
            return ToDeviceInfo(service, ioKitClass);
        }
        catch
        {
            return null;
        }
    }

    private static DeviceInfo BuildDeviceInfoFromProperties(
        uint service, string deviceId, string ioKitClass, IntPtr properties,
        Dictionary<string, NetworkInterfaceEntry>? networkInfo)
    {
        // Name: try multiple known keys
        string? name = IOKitInterop.GetCFStringValue(properties, "USB Product Name")
            ?? IOKitInterop.GetCFStringValue(properties, "Product")
            ?? IOKitInterop.GetCFStringValue(properties, "IOHIDProduct")
            ?? IOKitInterop.GetCFStringValue(properties, "IOClass");

        // Manufacturer
        string? manufacturer = IOKitInterop.GetCFStringValue(properties, "USB Vendor Name")
            ?? IOKitInterop.GetCFStringValue(properties, "IOHIDManufacturer");

        // VendorId / ProductId
        HardwareId? vendorId = null;
        HardwareId? productId = null;
        int? vidInt = IOKitInterop.GetCFNumberIntValue(properties, "idVendor")
            ?? IOKitInterop.GetCFNumberIntValue(properties, "HIDVendorID");
        int? pidInt = IOKitInterop.GetCFNumberIntValue(properties, "idProduct")
            ?? IOKitInterop.GetCFNumberIntValue(properties, "HIDProductID");
        if (vidInt is > 0 and <= ushort.MaxValue)
            vendorId = (HardwareId)(ushort)vidInt.Value;
        if (pidInt is > 0 and <= ushort.MaxValue)
            productId = (HardwareId)(ushort)pidInt.Value;

        // Serial number
        string? serialNumber = IOKitInterop.GetCFStringValue(properties, "USB Serial Number")
            ?? IOKitInterop.GetCFStringValue(properties, "IOHIDSerialNumber");

        // Category (resolve from IOKit class, with subcategory refinement)
        DeviceCategory category = MacOSCategoryMap.ResolveCategory(ioKitClass);
        int? hidUsagePage = null;
        int? hidUsage = null;
        if (ioKitClass == MacOSCategoryMap.IOHIDDevice)
        {
            hidUsagePage = IOKitInterop.GetCFNumberIntValue(properties, "PrimaryUsagePage");
            hidUsage = IOKitInterop.GetCFNumberIntValue(properties, "PrimaryUsage");
            category = MacOSCategoryMap.ResolveHidCategory(hidUsagePage, hidUsage);
        }
        else if (ioKitClass is MacOSCategoryMap.IOUSBDevice or MacOSCategoryMap.IOUSBHostDevice)
        {
            // Tier 2 (ADR-0013): refine USB category via bDeviceClass descriptor field
            int? usbDeviceClass = IOKitInterop.GetCFNumberIntValue(properties, "bDeviceClass");
            category = MacOSCategoryMap.ResolveUsbCategory(usbDeviceClass) ?? category;
        }

        // Bus type
        BusType busType = MacOSCategoryMap.InferBusType(ioKitClass);

        // IsActive — presence of sessionID property indicates an active session
        bool isConnected = IOKitInterop.GetCFNumberLongValue(properties, "sessionID") is not null;

        // Driver info
        string? driver = IOKitInterop.GetCFStringValue(properties, "CFBundleIdentifier");
        string? driverVersionStr = IOKitInterop.GetCFStringValue(properties, "CFBundleVersion");
        Version? driverVersion = driverVersionStr is not null && Version.TryParse(driverVersionStr, out var ver) ? ver : null;

        // MAC address (IONetworkController stores as 6-byte CFData under "IOMACAddress")
        PhysicalAddress? macAddress = null;
        byte[]? macBytes = IOKitInterop.GetCFDataValue(properties, "IOMACAddress");
        if (macBytes is { Length: 6 })
            macAddress = new PhysicalAddress(macBytes);

        // Network interface name → IP addresses and network
        ImmutableArray<IPAddress>? ipAddresses = null;
        IPNetwork? network = null;
        string? interfaceName = IOKitInterop.GetCFStringValue(properties, "BSD Name");
        if (interfaceName is not null && networkInfo is not null &&
            networkInfo.TryGetValue(interfaceName, out var netEntry))
        {
            if (netEntry.Addresses.Count > 0)
                ipAddresses = [.. netEntry.Addresses];
            network = netEntry.Network;
        }

        // Battery info
        int? batteryChargePercent = null;
        BatteryStatus? batteryStatus = null;
        bool? isExternalPowerConnected = null;
        if (ioKitClass == MacOSCategoryMap.AppleSmartBattery)
        {
            int? currentCapacity = IOKitInterop.GetCFNumberIntValue(properties, "CurrentCapacity");
            int? maxCapacity = IOKitInterop.GetCFNumberIntValue(properties, "MaxCapacity");
            if (currentCapacity is not null && maxCapacity is not null and > 0)
                batteryChargePercent = (int)((double)currentCapacity.Value / maxCapacity.Value * 100);

            bool? isCharging = IOKitInterop.GetCFBooleanValue(properties, "IsCharging");
            bool? externalConnected = IOKitInterop.GetCFBooleanValue(properties, "ExternalConnected");
            isExternalPowerConnected = externalConnected;

            batteryStatus = (isCharging, externalConnected, batteryChargePercent) switch
            {
                (true, _, _) => Periphery.BatteryStatus.Charging,
                (false, true, >= 100) => Periphery.BatteryStatus.Full,
                (false, true, _) => Periphery.BatteryStatus.NotCharging,
                (false, false or null, _) => Periphery.BatteryStatus.Discharging,
                _ => Periphery.BatteryStatus.Unknown,
            };

            // Battery devices are always considered "connected" if we can read their properties
            isConnected = true;
        }

        // For network interfaces, consider connected if link status is active
        if (ioKitClass == MacOSCategoryMap.IONetworkInterface)
        {
            // Network interfaces in the registry are present → connected
            isConnected = true;
        }

        // Display resolution
        System.Drawing.Size? displayResolution = null;
        if (ioKitClass == MacOSCategoryMap.IODisplayConnect)
        {
            int? hRes = IOKitInterop.GetCFNumberIntValue(properties, "IODisplayPrefsKey");
            // Display resolution from IOKit is complex; basic fallback
            isConnected = true;
        }

        // Serial port name (ADR-0013 Tier 1: IOSerialBSDClient)
        SerialPortName? portName = null;
        if (ioKitClass == MacOSCategoryMap.IOSerialBSDClient)
        {
            // Prefer the callout device (/dev/cu.*) over the dialin device (/dev/tty.*)
            // because callout is the standard path for initiating serial communication.
            string? calloutDevice = IOKitInterop.GetCFStringValue(properties, "IOCalloutDevice");
            string? dialinDevice = IOKitInterop.GetCFStringValue(properties, "IODialinDevice");
            string? portPath = calloutDevice ?? dialinDevice;
            if (portPath is not null)
                portName = new SerialPortName(portPath);

            // Serial port devices are considered connected when present in the registry
            isConnected = true;
        }

        // Location path — use the IOKit class and device ID as a stable location
        string locationPath = $"IOService:/{ioKitClass}/{deviceId}";

        return new DeviceInfo
        {
            Id = deviceId,
            Name = name,
            Category = category,
            Manufacturer = manufacturer,
            VendorId = vendorId,
            ProductId = productId,
            SerialNumber = serialNumber,
            IsActive = isConnected,
            Status = DeviceStatus.OK,
            BusType = busType,
            LocationPath = locationPath,
            Driver = driver,
            DriverVersion = driverVersion,
            MacAddress = macAddress,
            IPAddresses = ipAddresses,
            Network = network,
            BatteryChargePercent = batteryChargePercent,
            BatteryStatus = batteryStatus,
            IsExternalPowerConnected = isExternalPowerConnected,
            DisplayResolution = displayResolution,
            PortName = portName,
            HidUsagePage = (ushort?)hidUsagePage,
            HidUsage = (ushort?)hidUsage,
            IOServiceClass = ioKitClass,
            Properties = ImmutableDictionary<string, object?>.Empty,
        };
    }

    // ── Network info via getifaddrs() ──────────────────────────────────

    internal sealed class NetworkInterfaceEntry
    {
        public List<IPAddress> Addresses { get; } = [];
        public IPNetwork? Network { get; set; }
    }

    /// <summary>
    /// Calls <c>getifaddrs()</c> to build a lookup from BSD interface name to IP addresses.
    /// Returns an empty dictionary if the call fails.
    /// </summary>
    private static Dictionary<string, NetworkInterfaceEntry> BuildNetworkInterfaceInfo()
    {
        var result = new Dictionary<string, NetworkInterfaceEntry>(StringComparer.Ordinal);

        try
        {
            // Use .NET's NetworkInterface API instead of raw getifaddrs for cross-platform safety
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                var entry = new NetworkInterfaceEntry();
                var props = nic.GetIPProperties();

                foreach (var addr in props.UnicastAddresses)
                {
                    entry.Addresses.Add(addr.Address);

                    // Capture the first IPv4 network
                    if (entry.Network is null &&
                        addr.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                        addr.PrefixLength > 0)
                    {
                        entry.Network = new IPNetwork(addr.Address, addr.PrefixLength);
                    }
                }

                if (entry.Addresses.Count > 0)
                    result[nic.Name] = entry;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enumerate network interfaces via getifaddrs()");
        }

        return result;
    }
}
