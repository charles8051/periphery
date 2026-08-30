// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.Generic;
using System.Runtime.Versioning;

namespace Periphery.Linux;

/// <summary>
/// Maps <see cref="DeviceCategory"/> values to Linux udev subsystem strings
/// and resolves subsystems (with optional udev property hints) back to categories.
/// Parallel to <see cref="Windows.WindowsCategoryMap"/> on Windows.
/// </summary>
[SupportedOSPlatform("linux")]
internal static class LinuxCategoryMap
{
    /// <summary>
    /// Returns the udev subsystem strings that correspond to the given <paramref name="category"/>,
    /// or an empty array for <see cref="DeviceCategory.All"/> (no filter — enumerate all subsystems).
    /// </summary>
    internal static string[] GetSubsystems(DeviceCategory category) => category switch
    {
        DeviceCategory.All       => [],
        DeviceCategory.Usb       => ["usb"],
        DeviceCategory.Bluetooth => ["bluetooth"],
        DeviceCategory.Network   => ["net"],
        DeviceCategory.Display   => ["drm"],
        DeviceCategory.Monitor   => ["drm"],
        DeviceCategory.Hid       => ["hid", "input"],
        DeviceCategory.Keyboard  => ["input"],
        DeviceCategory.Mouse     => ["input"],
        DeviceCategory.Audio     => ["sound"],
        DeviceCategory.Storage   => ["block"],
        DeviceCategory.Ports     => ["tty"],
        DeviceCategory.Battery   => ["power_supply"],
        DeviceCategory.Camera    => ["video4linux"],
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, "Unknown device category.")
    };

    /// <summary>
    /// Resolves a udev subsystem and device properties to a <see cref="DeviceCategory"/>.
    /// Uses property hints (<c>ID_INPUT_KEYBOARD</c>, <c>ID_INPUT_MOUSE</c>, etc.) to
    /// disambiguate shared subsystems (e.g. <c>input</c> → Keyboard vs. Mouse vs. Hid).
    /// Returns <see cref="DeviceCategory.All"/> if no mapping is found.
    /// </summary>
    internal static DeviceCategory ResolveCategory(string? subsystem, IntPtr device)
    {
        if (subsystem is null) return DeviceCategory.All;

        return subsystem switch
        {
            "usb"          => DeviceCategory.Usb,
            "bluetooth"    => DeviceCategory.Bluetooth,
            "net"          => DeviceCategory.Network,
            "drm"          => DeviceCategory.Display,
            "hid"          => DeviceCategory.Hid,
            "input"        => ResolveInputCategory(device),
            "sound"        => DeviceCategory.Audio,
            "block"        => DeviceCategory.Storage,
            "tty"          => DeviceCategory.Ports,
            "power_supply" => DeviceCategory.Battery,
            "video4linux"  => DeviceCategory.Camera,
            _              => DeviceCategory.All,
        };
    }

    /// <summary>
    /// Disambiguates <c>input</c> subsystem devices using udev property hints.
    /// </summary>
    private static DeviceCategory ResolveInputCategory(IntPtr device)
    {
        if (device == IntPtr.Zero) return DeviceCategory.Hid;

        if (UdevInterop.GetPropertyValue(device, "ID_INPUT_KEYBOARD") == "1")
            return DeviceCategory.Keyboard;
        if (UdevInterop.GetPropertyValue(device, "ID_INPUT_MOUSE") == "1")
            return DeviceCategory.Mouse;

        return DeviceCategory.Hid;
    }

    /// <summary>
    /// Infers the bus type from the udev <c>ID_BUS</c> property or subsystem.
    /// </summary>
    internal static BusType InferBusType(string? idBus, string? subsystem)
    {
        // Prefer explicit ID_BUS property when available
        if (idBus is not null)
        {
            return idBus.ToLowerInvariant() switch
            {
                "usb"       => BusType.USB,
                "pci"       => BusType.PCI,
                "bluetooth" => BusType.Bluetooth,
                "hid"       => BusType.HID,
                "scsi"      => BusType.SCSI,
                "ide"       => BusType.IDE,
                "acpi"      => BusType.ACPI,
                _           => BusType.Unknown,
            };
        }

        // Fall back to subsystem inference
        return subsystem switch
        {
            "usb"       => BusType.USB,
            "bluetooth" => BusType.Bluetooth,
            "hid"       => BusType.HID,
            "input"     => BusType.HID,
            "drm"       => BusType.Display,
            "sound"     => BusType.HDAudio,
            _           => BusType.Unknown,
        };
    }
}
