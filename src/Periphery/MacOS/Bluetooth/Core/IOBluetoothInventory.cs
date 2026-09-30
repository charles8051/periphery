// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Collections.Immutable;

namespace Periphery.MacOS.Bluetooth.Core;

/// <summary>One bond as IOBluetooth's <c>pairedDevices</c> reports it.</summary>
internal readonly record struct IOBluetoothBond(string Address, string? Name, bool Connected, uint ClassOfDevice);

/// <summary>
/// A connected Bluetooth HID device's IOKit node: its <c>DeviceAddress</c> and <c>Transport</c>. It
/// exists only while the link is up, and reading it needs no Bluetooth permission.
/// </summary>
internal readonly record struct HidLink(string Address, string Transport);

/// <summary>
/// <c>CBManagerAuthorization</c>: whether this process may use Bluetooth. macOS decides it per
/// responsible process, so a console program inherits its terminal's answer.
/// </summary>
internal enum BluetoothAuthorization
{
    NotDetermined = 0,
    Restricted = 1,
    Denied = 2,
    AllowedAlways = 3,
}

/// <summary>An edge the IOBluetooth watch raises for one bond.</summary>
internal enum IOBluetoothEdgeKind
{
    Appeared,
    Disappeared,
    Activated,
    Deactivated,
    PropertyChanged,
}

/// <summary>One edge. <see cref="Previous"/> is set for <see cref="IOBluetoothEdgeKind.PropertyChanged"/>.</summary>
internal readonly record struct IOBluetoothEdge(IOBluetoothEdgeKind Kind, DeviceInfo Device, DeviceInfo? Previous = null);

/// <summary>
/// ADR-0093 as pure functions: whether to ask IOBluetooth, what a bond maps to, and the edges between
/// two snapshots. The shell reads the authorization and the bonds.
/// </summary>
internal static class IOBluetoothInventory
{
    /// <summary>D3: the prefix of a bond's <see cref="DeviceInfo.Id"/>.</summary>
    internal const string IdPrefix = "iobluetooth:";

    /// <summary>D1: only a query that can match a bond asks IOBluetooth.</summary>
    internal static bool ShouldQuery(DeviceFilter filter) =>
        (filter.Category is null or DeviceCategory.All or DeviceCategory.Bluetooth)
        && filter.VendorId is null
        && filter.ProductId is null;

    /// <summary>
    /// D2: why IOBluetooth is not asked, or <see langword="null"/> when it may be. Asking without
    /// permission returns no bonds and no error, and could prompt, so it is asked only when allowed.
    /// </summary>
    internal static string? Unavailable(BluetoothAuthorization authorization) => authorization switch
    {
        BluetoothAuthorization.AllowedAlways => null,
        BluetoothAuthorization.NotDetermined =>
            "This process has no Bluetooth permission, so bonded Bluetooth devices are not reported. " +
            "Run it from a terminal that has Bluetooth access, or request access in the app.",
        BluetoothAuthorization.Denied =>
            "Bluetooth access is denied to this process, so bonded Bluetooth devices are not reported.",
        BluetoothAuthorization.Restricted =>
            "Bluetooth access is restricted on this Mac, so bonded Bluetooth devices are not reported.",
        _ => $"Bluetooth authorization is {(int)authorization}, which is unknown, so bonded Bluetooth devices are not reported.",
    };

    /// <summary>D3: the bonds as devices, one per address.</summary>
    internal static ImmutableArray<DeviceInfo> Map(
        IEnumerable<IOBluetoothBond> bonds, ImmutableDictionary<BluetoothAddress, BluetoothTransports>? known = null) =>
        [.. bonds.Select(bond => ToDeviceInfo(bond, known)).OfType<DeviceInfo>().DistinctBy(d => d.Id)];

    /// <summary>The transport an IOKit HID node's <c>Transport</c> names, or <see cref="BluetoothTransports.None"/>.</summary>
    internal static BluetoothTransports TransportOf(string transport) => transport switch
    {
        "Bluetooth Low Energy" => BluetoothTransports.LowEnergy,
        "Bluetooth" => BluetoothTransports.BrEdr,
        _ => BluetoothTransports.None,
    };

    /// <summary>
    /// D3 amendment: adds what <paramref name="links"/> show to what is <paramref name="known"/>.
    /// A transport once seen stays known, since a peripheral does not stop supporting it when its link
    /// drops.
    /// </summary>
    internal static ImmutableDictionary<BluetoothAddress, BluetoothTransports> Learn(
        ImmutableDictionary<BluetoothAddress, BluetoothTransports> known, IEnumerable<HidLink> links)
    {
        foreach (var link in links)
        {
            var transport = TransportOf(link.Transport);
            if (transport != BluetoothTransports.None && BluetoothAddress.TryParse(link.Address, out var address))
                known = known.SetItem(address, known.GetValueOrDefault(address) | transport);
        }
        return known;
    }

    /// <summary>
    /// D3: one bond. <see langword="null"/> when its address does not parse. <paramref name="known"/>
    /// adds the transports its HID node has shown.
    /// </summary>
    internal static DeviceInfo? ToDeviceInfo(
        IOBluetoothBond bond, ImmutableDictionary<BluetoothAddress, BluetoothTransports>? known = null)
    {
        if (!BluetoothAddress.TryParse(bond.Address, out var address))
            return null;

        return new DeviceInfo
        {
            Id = IdPrefix + address,
            Name = string.IsNullOrEmpty(bond.Name) ? null : bond.Name,
            Category = DeviceCategory.Bluetooth,
            BusType = BusType.Bluetooth,
            Status = DeviceStatus.OK,
            IsActive = bond.Connected,
            MacAddress = address.ToPhysicalAddress(),
            // A class of device is BR/EDR evidence. LE evidence comes only from a HID node seen connected.
            BluetoothTransports = (bond.ClassOfDevice != 0 ? BluetoothTransports.BrEdr : BluetoothTransports.None)
                | (known?.GetValueOrDefault(address) ?? BluetoothTransports.None),
        };
    }

    /// <summary>
    /// D4: the edges from <paramref name="held"/> to <paramref name="snapshot"/>, and the bonds held
    /// after. An unavailable snapshot keeps what is held and raises nothing.
    /// </summary>
    internal static (ImmutableDictionary<DeviceId, DeviceInfo> Held, ImmutableArray<IOBluetoothEdge> Edges) Step(
        ImmutableDictionary<DeviceId, DeviceInfo> held, ImmutableArray<DeviceInfo>? snapshot)
    {
        if (snapshot is not { } devices)
            return (held, []);

        var after = devices.ToImmutableDictionary(d => d.Id);
        var edges = ImmutableArray.CreateBuilder<IOBluetoothEdge>();
        foreach (var id in held.Keys.OrderBy(id => id.Value, StringComparer.OrdinalIgnoreCase))
        {
            if (!after.ContainsKey(id))
                edges.Add(new(IOBluetoothEdgeKind.Disappeared, held[id]));
        }

        foreach (var id in after.Keys.OrderBy(id => id.Value, StringComparer.OrdinalIgnoreCase))
        {
            var current = after[id];
            if (!held.TryGetValue(id, out var previous))
            {
                edges.Add(new(IOBluetoothEdgeKind.Appeared, current));
                if (current.IsActive)
                    edges.Add(new(IOBluetoothEdgeKind.Activated, current));
                continue;
            }

            if (DeviceInfoDiff.Compute(previous, current).Count == 0)
                continue;
            if (previous.IsActive != current.IsActive)
                edges.Add(new(current.IsActive ? IOBluetoothEdgeKind.Activated : IOBluetoothEdgeKind.Deactivated, current));
            edges.Add(new(IOBluetoothEdgeKind.PropertyChanged, current, previous));
        }

        return (after, edges.ToImmutable());
    }
}
