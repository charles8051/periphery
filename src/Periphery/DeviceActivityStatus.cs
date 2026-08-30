// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Text.Json.Serialization;

namespace Periphery;

/// <summary>
/// The resolved activity status of the device tracked by a <see cref="DeviceTracker"/>.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<DeviceActivityStatus>))]
public enum DeviceActivityStatus
{
    /// <summary>
    /// The tracker has not yet determined the device's status: it is unbound,
    /// or bound to a watcher whose initial enumeration has not yet completed.
    /// This is the value a tracker reports before its first determination — it
    /// is distinct from <see cref="Absent"/> ("enumerated and confirmed gone").
    /// A tracker leaves <see cref="Unknown"/> exactly once: when the watcher's
    /// initial enumeration settles (or when a matching device is observed first).
    /// </summary>
    Unknown = 0,

    /// <summary>No matching device is known to the OS (enumerated and confirmed gone).</summary>
    Absent = 1,

    /// <summary>
    /// A matching device is known to the OS (paired, installed, or enumerated)
    /// but is not currently active. Typical for Bluetooth devices that are
    /// paired but out of range.
    /// </summary>
    Present = 2,

    /// <summary>
    /// A matching device is active and ready to use (driver started, hardware working).
    /// </summary>
    Active = 3,
}
