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
using Periphery.MacOS.Bluetooth;
using Periphery.MacOS.Bluetooth.Core;
using Periphery.MacOS.Core;

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

    /// <summary>ADR-0093 D2: one instance for the process, so each reason for no bonds is logged once.</summary>
    internal static IOBluetoothDeviceSource SharedBluetooth { get; } = new(
        IOBluetoothInterop.ReadAuthorization,
        IOBluetoothInterop.ReadBonds,
        PeripheryLoggerFactory.CreateLogger<IOBluetoothDeviceSource>());

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

        // ADR-0093 D1: bonded Bluetooth devices live in IOBluetooth, not in the IOKit registry.
        if (IOBluetoothInventory.ShouldQuery(filter))
        {
            foreach (var device in SharedBluetooth.Enumerate(ct))
                yield return device;
        }

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
            device = IOKitDeviceMap.Unreadable(deviceId, ioKitClass);
        }
        else
        {
            IOKitProperties snapshot;
            try
            {
                snapshot = ReadProperties(properties);
            }
            finally
            {
                IOKitInterop.CFRelease(properties);
            }

            device = WithNetwork(IOKitDeviceMap.ToDeviceInfo(deviceId, ioKitClass, snapshot), snapshot, networkInfo);
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

    /// <summary>Reads the keys <see cref="IOKitDeviceMap"/> names from a registry entry's properties.</summary>
    private static IOKitProperties ReadProperties(IntPtr properties)
    {
        var values = ImmutableDictionary.CreateBuilder<string, object>(StringComparer.Ordinal);
        foreach (var key in IOKitDeviceMap.StringKeys)
            if (IOKitInterop.GetCFStringValue(properties, key) is { } text)
                values[key] = text;
        foreach (var key in IOKitDeviceMap.NumberKeys)
            if (IOKitInterop.GetCFNumberLongValue(properties, key) is { } number)
                values[key] = number;
        foreach (var key in IOKitDeviceMap.BoolKeys)
            if (IOKitInterop.GetCFBooleanValue(properties, key) is { } flag)
                values[key] = flag;
        foreach (var key in IOKitDeviceMap.DataKeys)
            if (IOKitInterop.GetCFDataValue(properties, key) is { } data)
                values[key] = data;
        return new IOKitProperties(values.ToImmutable());
    }

    /// <summary>Adds the addresses of the interface the entry names in <c>BSD Name</c>.</summary>
    private static DeviceInfo WithNetwork(
        DeviceInfo device, IOKitProperties properties, Dictionary<string, NetworkInterfaceEntry>? networkInfo)
    {
        if (properties.String("BSD Name") is not { } interfaceName || networkInfo is null
            || !networkInfo.TryGetValue(interfaceName, out var entry))
        {
            return device;
        }

        return device with
        {
            IPAddresses = entry.Addresses.Count > 0 ? [.. entry.Addresses] : null,
            Network = entry.Network,
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
