// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Collections.Immutable;

namespace Periphery.Linux.Core;

/// <summary>An edge the udev monitor raises.</summary>
internal enum UdevEdgeKind
{
    Appeared,
    Disappeared,
    Activated,
    Deactivated,
    PropertyChanged,
}

/// <summary>One edge. <see cref="Previous"/> is set for <see cref="UdevEdgeKind.PropertyChanged"/>.</summary>
internal readonly record struct UdevEdge(UdevEdgeKind Kind, DeviceInfo Device, DeviceInfo? Previous = null);

/// <summary>
/// One udev event. <paramref name="Device"/> is what the shell read at <paramref name="Syspath"/>,
/// or <see langword="null"/> when the action needs none or the device maps to none.
/// <paramref name="OldSyspath"/> is the syspath a <c>move</c> left.
/// </summary>
internal readonly record struct UdevEvent(string Action, string Syspath, DeviceInfo? Device = null, string? OldSyspath = null);

/// <summary>The devices held after an event, and the edges it raised.</summary>
internal readonly record struct UdevStep(ImmutableDictionary<DeviceId, DeviceInfo> Devices, ImmutableArray<UdevEdge> Edges);

/// <summary>
/// Turns udev events into device edges against the devices last seen. Pure: the monitor shell
/// reads the event and the device, and raises the edges.
/// </summary>
internal static class UdevDispatch
{
    /// <summary>Whether the shell must read the device at the event's syspath before stepping.</summary>
    internal static bool NeedsDevice(string action) => action is "add" or "bind" or "change" or "move";

    /// <summary>
    /// The syspath a <c>move</c> left, from its <c>DEVPATH_OLD</c>. libudev's syspath is <c>/sys</c>
    /// followed by the devpath.
    /// </summary>
    internal static string? OldSyspath(string? devpathOld) =>
        string.IsNullOrEmpty(devpathOld) ? null : "/sys" + devpathOld;

    internal static UdevStep Step(ImmutableDictionary<DeviceId, DeviceInfo> devices, UdevEvent e) => e.Action switch
    {
        "add" => Add(devices, e.Device),
        "remove" => Remove(devices, e.Syspath),
        "bind" => Bind(devices, e.Device),
        "unbind" => Unbind(devices, e.Syspath),
        "change" => Change(devices, e.Syspath, e.Device),
        "move" => Move(devices, e.OldSyspath, e.Device),
        _ => new(devices, []),
    };

    private static UdevStep Add(ImmutableDictionary<DeviceId, DeviceInfo> devices, DeviceInfo? device)
    {
        if (device is null)
            return new(devices, []);

        ImmutableArray<UdevEdge> edges = device.IsActive
            ? [new(UdevEdgeKind.Appeared, device), new(UdevEdgeKind.Activated, device)]
            : [new(UdevEdgeKind.Appeared, device)];
        return new(devices.SetItem(device.Id, device), edges);
    }

    // A device the monitor never saw still gets its edge, as a bare Id.
    private static UdevStep Remove(ImmutableDictionary<DeviceId, DeviceInfo> devices, DeviceId id)
    {
        var device = devices.GetValueOrDefault(id) ?? new DeviceInfo { Id = id };
        return new(devices.Remove(id), [new(UdevEdgeKind.Disappeared, device)]);
    }

    private static UdevStep Bind(ImmutableDictionary<DeviceId, DeviceInfo> devices, DeviceInfo? device) =>
        device is null
            ? new(devices, [])
            : new(devices.SetItem(device.Id, device), [new(UdevEdgeKind.Activated, device)]);

    private static UdevStep Unbind(ImmutableDictionary<DeviceId, DeviceInfo> devices, DeviceId id)
    {
        var device = devices.GetValueOrDefault(id) ?? new DeviceInfo { Id = id };
        return new(devices, [new(UdevEdgeKind.Deactivated, device)]);
    }

    private static UdevStep Change(ImmutableDictionary<DeviceId, DeviceInfo> devices, DeviceId id, DeviceInfo? current)
    {
        if (current is null)
            return new(devices, []);

        var previous = devices.GetValueOrDefault(id);
        devices = devices.SetItem(current.Id, current);
        if (previous is null)
            return new(devices, []);

        var changed = DeviceInfoDiff.Compute(previous, current);
        if (changed.Count == 0)
            return new(devices, []);

        var edges = ImmutableArray.CreateBuilder<UdevEdge>(2);
        if (changed.Contains(nameof(DeviceInfo.IsActive)))
            edges.Add(new(current.IsActive ? UdevEdgeKind.Activated : UdevEdgeKind.Deactivated, current));
        edges.Add(new(UdevEdgeKind.PropertyChanged, current, previous));
        return new(devices, edges.ToImmutable());
    }

    // The kernel gave the device a new parent, so its syspath, and with it its Id, changed (#304).
    // DeviceInfo has no rename edge, so the old Id leaves and the new one arrives.
    private static UdevStep Move(ImmutableDictionary<DeviceId, DeviceInfo> devices, string? oldSyspath, DeviceInfo? device)
    {
        if (oldSyspath is null)
            return Add(devices, device);

        var left = Remove(devices, oldSyspath);
        var arrived = Add(left.Devices, device);
        return new(arrived.Devices, left.Edges.AddRange(arrived.Edges));
    }
}
