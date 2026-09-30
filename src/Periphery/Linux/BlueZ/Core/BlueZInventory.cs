// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Collections.Immutable;
using System.Net.NetworkInformation;
using Periphery.Linux.DBus.Core;
using BlueZObjects = System.Collections.Immutable.ImmutableDictionary<string,
    System.Collections.Immutable.ImmutableDictionary<string,
        System.Collections.Immutable.ImmutableDictionary<string, Periphery.Linux.DBus.Core.DBusValue>>>;

namespace Periphery.Linux.BlueZ.Core;

/// <summary>How a BlueZ error reply is treated (ADR-0091 D3).</summary>
internal enum BlueZFailureKind
{
    /// <summary>BlueZ is not running. Expected on hosts without Bluetooth.</summary>
    Absent,

    /// <summary>Bus policy refuses this process. Not retried for the life of the process.</summary>
    AccessDenied,

    /// <summary>Anything else.</summary>
    Error,
}

/// <summary>
/// Pure decisions for the BlueZ leg of Linux enumeration (ADR-0091 D1, D3, D4): whether a filter
/// could match a BlueZ device, how a <c>GetManagedObjects</c> reply maps to devices, and how an error
/// reply is treated.
/// </summary>
internal static class BlueZInventory
{
    internal const string Service = "org.bluez";
    internal const string DeviceInterface = "org.bluez.Device1";
    internal const string AdapterInterface = "org.bluez.Adapter1";
    internal const string ObjectManagerInterface = "org.freedesktop.DBus.ObjectManager";
    internal const string ManagedObjectsSignature = "a{oa{sa{sv}}}";

    /// <summary>The prefix of every BlueZ device's <see cref="DeviceInfo.Id"/>.</summary>
    internal const string IdPrefix = "bluez:";

    /// <summary>
    /// D1: the BlueZ leg runs only when the filter could match a BlueZ device. Its category is unset,
    /// <see cref="DeviceCategory.All"/> or <see cref="DeviceCategory.Bluetooth"/>, and it has no
    /// vendor or product hint, which a BlueZ device never carries.
    /// </summary>
    internal static bool ShouldQuery(DeviceFilter filter) =>
        (filter.Category is null or DeviceCategory.All or DeviceCategory.Bluetooth)
        && filter.VendorId is null
        && filter.ProductId is null;

    /// <summary>D3: the treatment of an error reply, by its name.</summary>
    internal static BlueZFailureKind ClassifyError(string errorName) => errorName switch
    {
        "org.freedesktop.DBus.Error.NameHasNoOwner" or "org.freedesktop.DBus.Error.ServiceUnknown" => BlueZFailureKind.Absent,
        "org.freedesktop.DBus.Error.AccessDenied" => BlueZFailureKind.AccessDenied,
        _ => BlueZFailureKind.Error,
    };

    /// <summary>The D4 <see cref="DeviceInfo.Id"/> of a bond: adapter address, then device address.</summary>
    internal static string IdFor(BluetoothAddress adapter, BluetoothAddress device) => $"{IdPrefix}{adapter}/{device}";

    /// <summary>
    /// D1 and D4: one <see cref="DeviceInfo"/> per bonded <c>org.bluez.Device1</c> in a
    /// <c>GetManagedObjects</c> reply, ordered by object path. An object whose address or adapter
    /// cannot be read is left out. When two bonded objects map to one <c>Id</c>, the connected one
    /// wins, then the lower object path by ordinal comparison.
    /// </summary>
    internal static ImmutableArray<DeviceInfo> Map(DBusValue managedObjects) => Map(ReadObjects(managedObjects));

    /// <summary>The same mapping over objects already read: path, then interface, then property.</summary>
    internal static ImmutableArray<DeviceInfo> Map(BlueZObjects objects)
    {
        var candidates = new List<(string Path, DeviceInfo Device)>();
        foreach (var path in objects.Keys.Order(StringComparer.Ordinal))
        {
            if (objects[path].TryGetValue(DeviceInterface, out var device)
                && IsBonded(device)
                && ToDeviceInfo(path, device, objects) is { } info)
                candidates.Add((path, info));
        }

        return candidates
            .GroupBy(c => c.Device.Id.Value, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(c => c.Device.IsActive).ThenBy(c => c.Path, StringComparer.Ordinal).First().Device)
            .ToImmutableArray();
    }

    /// <summary>
    /// D1: bonded is <c>Bonded</c> where BlueZ has it (5.65 and later), and <c>Paired</c> where it
    /// does not. A pairing whose keys were not stored is not a bond.
    /// </summary>
    internal static bool IsBonded(IReadOnlyDictionary<string, DBusValue> device) =>
        device.ContainsKey("Bonded") ? Flag(device, "Bonded") : Flag(device, "Paired");

    private static DeviceInfo? ToDeviceInfo(
        string path,
        IReadOnlyDictionary<string, DBusValue> device,
        BlueZObjects objects)
    {
        if (!BluetoothAddress.TryParse(Text(device, "Address", 's'), out var address))
            return null;

        if (Text(device, "Adapter", 'o') is not { } adapterPath
            || !objects.TryGetValue(adapterPath, out var adapterInterfaces)
            || !adapterInterfaces.TryGetValue(AdapterInterface, out var adapter)
            || !BluetoothAddress.TryParse(Text(adapter, "Address", 's'), out var adapterAddress))
            return null;

        return new DeviceInfo
        {
            Id = IdFor(adapterAddress, address),
            Name = NameOf(device, address),
            Category = DeviceCategory.Bluetooth,
            BusType = BusType.Bluetooth,
            Status = DeviceStatus.OK,
            IsActive = Flag(device, "Connected"),
            MacAddress = ToPhysicalAddress(address),
            LocationPath = path,
        };
    }

    // D4: Alias, except BlueZ's fallback of the address with dashes, which it uses when no name has
    // been read. DeviceInfo reports an unknown name as null.
    private static string? NameOf(IReadOnlyDictionary<string, DBusValue> device, BluetoothAddress address)
    {
        string? name = Text(device, "Name", 's');
        string? alias = Text(device, "Alias", 's') ?? name;
        if (name is null
            && alias is not null
            && alias.Equals(address.ToString().Replace(':', '-'), StringComparison.OrdinalIgnoreCase))
            return null;
        return alias;
    }

    private static PhysicalAddress ToPhysicalAddress(BluetoothAddress address)
    {
        var bytes = new byte[6];
        for (int i = 0; i < 6; i++)
            bytes[i] = (byte)(address.Value >> (40 - 8 * i));
        return new PhysicalAddress(bytes);
    }

    private static bool Flag(IReadOnlyDictionary<string, DBusValue> properties, string name) =>
        properties.TryGetValue(name, out var value) && value is DBusVariant { Value: DBusBoolean { Value: true } };

    private static string? Text(IReadOnlyDictionary<string, DBusValue> properties, string name, char code) =>
        properties.TryGetValue(name, out var value) && value is DBusVariant { Value: DBusString s } && s.Code == code
            ? s.Value
            : null;

    /// <summary>
    /// <c>a{oa{sa{sv}}}</c> as path, then interface, then property. Entries of another shape are
    /// skipped.
    /// </summary>
    internal static BlueZObjects ReadObjects(DBusValue managedObjects)
    {
        var objects = BlueZObjects.Empty.WithComparers(StringComparer.Ordinal).ToBuilder();
        if (managedObjects is not DBusArray { Items: var entries })
            return objects.ToImmutable();

        foreach (var entry in entries)
        {
            if (entry is DBusDictEntry { Key: DBusString { Code: 'o', Value: var path }, Value: var interfaces })
                objects[path] = ReadInterfaces(interfaces);
        }
        return objects.ToImmutable();
    }

    /// <summary><c>a{sa{sv}}</c> as interface, then property.</summary>
    internal static ImmutableDictionary<string, ImmutableDictionary<string, DBusValue>> ReadInterfaces(DBusValue interfaces)
    {
        var result = ImmutableDictionary.CreateBuilder<string, ImmutableDictionary<string, DBusValue>>(StringComparer.Ordinal);
        if (interfaces is not DBusArray { Items: var entries })
            return result.ToImmutable();

        foreach (var entry in entries)
        {
            if (entry is DBusDictEntry { Key: DBusString { Value: var name }, Value: var properties })
                result[name] = ReadProperties(properties);
        }
        return result.ToImmutable();
    }

    /// <summary><c>a{sv}</c> as property name to variant.</summary>
    internal static ImmutableDictionary<string, DBusValue> ReadProperties(DBusValue properties)
    {
        var result = ImmutableDictionary.CreateBuilder<string, DBusValue>(StringComparer.Ordinal);
        if (properties is not DBusArray { Items: var entries })
            return result.ToImmutable();

        foreach (var entry in entries)
        {
            if (entry is DBusDictEntry { Key: DBusString { Value: var property }, Value: var value })
                result[property] = value;
        }
        return result.ToImmutable();
    }
}
