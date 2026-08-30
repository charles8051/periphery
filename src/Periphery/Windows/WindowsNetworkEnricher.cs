// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Periphery.Windows;

/// <summary>
/// Populates <see cref="DeviceInfo.MacAddress"/>, <see cref="DeviceInfo.IPAddresses"/>,
/// and <see cref="DeviceInfo.Network"/> for Network-category devices by correlating
/// SetupAPI device nodes to BCL <see cref="NetworkInterface"/> objects via the registry.
/// <para>
/// Correlation path: <c>HKLM\SYSTEM\CurrentControlSet\Control\Network\{Net class GUID}\
/// {adapter GUID}\Connection\PnpInstanceId</c> maps each adapter GUID (which equals
/// <see cref="NetworkInterface.Id"/> on Windows) back to the SetupAPI instance ID
/// stored in <see cref="DeviceInfo.Id"/>.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsNetworkEnricher
{
    // Net device setup class GUID — constant in devguid.h / DeviceClassGuids.Net
    private const string NetClassGuid = "{4d36e972-e325-11ce-bfc1-08002be10318}";

    private static readonly string s_regPath =
        $@"SYSTEM\CurrentControlSet\Control\Network\{NetClassGuid}";

    // Module-level cache so bulk enumeration reads the registry once.
    // Invalidated after 5 seconds to stay fresh for monitoring paths.
    private static (DateTime Built, IReadOnlyDictionary<string, NetworkInterface> Map)? s_cache;
    private static readonly TimeSpan s_ttl = TimeSpan.FromSeconds(5);
    private static readonly object s_lock = new();

    // ── Public entry point ─────────────────────────────────────────────

    /// <summary>
    /// Applies network enrichment to <paramref name="device"/> if it is in the
    /// <see cref="DeviceCategory.Network"/> category and a matching
    /// <see cref="NetworkInterface"/> can be found.
    /// </summary>
    internal static DeviceInfo Enrich(DeviceInfo device)
    {
        if (device.Category != DeviceCategory.Network)
            return device;

        IReadOnlyDictionary<string, NetworkInterface> map = GetOrBuildMap();
        if (!map.TryGetValue(device.Id, out NetworkInterface? ni))
            return device;

        PhysicalAddress mac = ni.GetPhysicalAddress();
        IPInterfaceProperties props = ni.GetIPProperties();

        // Collect all unicast addresses (IPv4 + IPv6)
        ImmutableArray<IPAddress> addresses = props.UnicastAddresses
            .Select(a => a.Address)
            .ToImmutableArray();

        // Use the first IPv4 unicast address to build the subnet IPNetwork
        UnicastIPAddressInformation? unicast4 = props.UnicastAddresses
            .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);

        IPNetwork? network = unicast4 is not null
            ? new IPNetwork(unicast4.Address, unicast4.PrefixLength)
            : null;

        // Only populate MacAddress when it has non-zero bytes (virtual adapters
        // sometimes report an all-zero address that means "not applicable")
        PhysicalAddress? macAddress = mac.GetAddressBytes().Any(b => b != 0) ? mac : null;

        return device with
        {
            MacAddress   = macAddress,
            IPAddresses  = addresses.Length > 0 ? addresses : null,
            Network      = network,
        };
    }

    // ── Internal helpers ───────────────────────────────────────────────

    internal static IReadOnlyDictionary<string, NetworkInterface> GetOrBuildMap()
    {
        lock (s_lock)
        {
            DateTime now = DateTime.UtcNow;
            if (s_cache is { } cached && now - cached.Built < s_ttl)
                return cached.Map;

            IReadOnlyDictionary<string, NetworkInterface> map = BuildMap();
            s_cache = (now, map);
            return map;
        }
    }

    private static IReadOnlyDictionary<string, NetworkInterface> BuildMap()
    {
        var result = new Dictionary<string, NetworkInterface>(StringComparer.OrdinalIgnoreCase);

        // Index BCL interfaces by their Windows adapter GUID.
        // NetworkInterface.Id on Windows is "{XXXXXXXX-XXXX-XXXX-XXXX-XXXXXXXXXXXX}" (with braces).
        var niByGuid = new Dictionary<string, NetworkInterface>(StringComparer.OrdinalIgnoreCase);
        foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
            niByGuid[ni.Id] = ni;

        using RegistryKey? baseKey = Registry.LocalMachine.OpenSubKey(s_regPath);
        if (baseKey is null)
            return result;

        foreach (string subKeyName in baseKey.GetSubKeyNames())
        {
            using RegistryKey? connectionKey = baseKey.OpenSubKey($@"{subKeyName}\Connection");
            if (connectionKey?.GetValue("PnpInstanceId") is not string pnpId)
                continue;

            // Normalise braces: registry sub-key names may or may not include them
            string adapterGuid = subKeyName.StartsWith('{') ? subKeyName : "{" + subKeyName + "}";

            if (niByGuid.TryGetValue(adapterGuid, out NetworkInterface? ni))
                result[pnpId] = ni;
        }

        return result;
    }
}
