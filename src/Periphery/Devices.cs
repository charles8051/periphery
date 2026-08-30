// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Periphery;

/// <summary>
/// The main entry point for device discovery.
///
/// <example>
/// <b>One-shot (all connected devices):</b>
/// <code>
/// var usb = await Devices.Enumerate()
///     .OfCategory(DeviceCategory.Usb)
///     .Active()
///     .ToListAsync();
/// </code>
///
/// <b>Filtered query (filters pushed to OS):</b>
/// <code>
/// var mice = await Devices.Enumerate()
///     .OfCategory(DeviceCategory.Hid)
///     .WithName("Mouse")
///     .ByManufacturer("Logitech")
///     .Active()
///     .OrderBy(d => d.Name)
///     .Take(5)
///     .ToListAsync();
/// </code>
///
/// <b>Watch (real-time events):</b>
/// <code>
/// await using var watcher = Devices.Watch()
///     .OfCategory(DeviceCategory.Usb);
/// watcher.Activated += (_, e) => Console.WriteLine($"+ {e.Device.Name}");
/// await watcher.StartAsync();
/// </code>
/// </example>
/// </summary>
public static class Devices
{
    // ── Query ──────────────────────────────────────────────────────────

    /// <summary>
    /// Start building a device query. Chain filters, then materialise
    /// with <c>ToListAsync</c>, <c>FirstOrDefaultAsync</c>, or
    /// <c>await foreach</c>.
    /// </summary>
    public static DeviceQuery Enumerate()
        => new();

    // ── Watch ──────────────────────────────────────────────────────────

    /// <summary>
    /// Create a watcher for real-time connect/disconnect events.
    /// </summary>
    public static DeviceWatcher Watch()
        => new();
}
