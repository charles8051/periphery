// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.Generic;

namespace Periphery.Windows;

/// <summary>
/// Maps <see cref="DeviceCategory"/> values to Windows device setup class GUIDs.
/// </summary>
internal static class WindowsCategoryMap
{
    /// <summary>
    /// Returns the class GUIDs that correspond to the given <paramref name="category"/>,
    /// or an empty array for <see cref="DeviceCategory.All"/> (no filter).
    /// </summary>
    internal static string[] GetClassGuids(DeviceCategory category) => category switch
    {
        DeviceCategory.All       => [],
        DeviceCategory.Usb       => [DeviceClassGuids.Usb],
        DeviceCategory.Bluetooth => [DeviceClassGuids.Bluetooth],
        DeviceCategory.Network   => [DeviceClassGuids.Net],
        DeviceCategory.Display   => [DeviceClassGuids.Display],
        DeviceCategory.Monitor   => [DeviceClassGuids.Monitor],
        DeviceCategory.Hid       => [DeviceClassGuids.HidClass],
        DeviceCategory.Keyboard  => [DeviceClassGuids.Keyboard],
        DeviceCategory.Mouse     => [DeviceClassGuids.Mouse],
        DeviceCategory.Audio     => [DeviceClassGuids.Sound, DeviceClassGuids.Media],
        DeviceCategory.Storage   => [DeviceClassGuids.DiskDrive, DeviceClassGuids.CdRom, DeviceClassGuids.FloppyDisk, DeviceClassGuids.TapeDrive],
        DeviceCategory.Ports     => [DeviceClassGuids.Ports, DeviceClassGuids.MultiportSerial],
        DeviceCategory.Battery   => [DeviceClassGuids.Battery],
        DeviceCategory.Camera    => [DeviceClassGuids.Camera],
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, "Unknown device category.")
    };

    /// <summary>
    /// Resolves a Windows class GUID to a <see cref="DeviceCategory"/>.
    /// Returns <see cref="DeviceCategory.All"/> if no mapping is found.
    /// </summary>
    internal static DeviceCategory ResolveCategory(string? classGuid)
    {
        if (classGuid is null) return DeviceCategory.All;
        if (!s_guidToCategory.TryGetValue(classGuid, out var cat))
            return DeviceCategory.All;
        return cat;
    }

    /// <summary>
    /// Infers the bus type from a Windows device ID prefix.
    /// </summary>
    internal static BusType InferBusType(string deviceId)
    {
        int sep = deviceId.IndexOf('\\');
        if (sep <= 0) return BusType.Unknown;
        return deviceId[..sep].ToUpperInvariant() switch
        {
            "USB"         => BusType.USB,
            "PCI"         => BusType.PCI,
            // Windows spreads Bluetooth across five enumerators, not one. BTHENUM
            // carries BR/EDR devices and their SDP service nodes; BTHLE carries LE
            // devices and BTHLEDEVICE their GATT service nodes; BTHHFENUM carries
            // handsfree audio. BTH carries the radio's own protocol-driver nodes
            // (MS_BTHLE, MS_RFCOMM, MS_BTHPAN, MS_BTHBRB), mapped here for the same
            // reason a USB root hub maps to BusType.USB.
            "BTH"         => BusType.Bluetooth,
            "BTHENUM"     => BusType.Bluetooth,
            "BTHLE"       => BusType.Bluetooth,
            "BTHLEDEVICE" => BusType.Bluetooth,
            "BTHHFENUM"   => BusType.Bluetooth,
            "HID"         => BusType.HID,
            "SWD"         => BusType.Software,
            "HDAUDIO"     => BusType.HDAudio,
            "DISPLAY"     => BusType.Display,
            "SCSI"        => BusType.SCSI,
            "IDE"         => BusType.IDE,
            "ACPI"        => BusType.ACPI,
            _             => BusType.Unknown
        };
    }

    private static readonly Dictionary<string, DeviceCategory> s_guidToCategory =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [DeviceClassGuids.Usb]            = DeviceCategory.Usb,
            [DeviceClassGuids.Bluetooth]      = DeviceCategory.Bluetooth,
            [DeviceClassGuids.Net]            = DeviceCategory.Network,
            [DeviceClassGuids.Display]        = DeviceCategory.Display,
            [DeviceClassGuids.Monitor]        = DeviceCategory.Monitor,
            [DeviceClassGuids.HidClass]       = DeviceCategory.Hid,
            [DeviceClassGuids.Keyboard]       = DeviceCategory.Keyboard,
            [DeviceClassGuids.Mouse]          = DeviceCategory.Mouse,
            [DeviceClassGuids.Sound]          = DeviceCategory.Audio,
            [DeviceClassGuids.Media]          = DeviceCategory.Audio,
            [DeviceClassGuids.DiskDrive]      = DeviceCategory.Storage,
            [DeviceClassGuids.CdRom]          = DeviceCategory.Storage,
            [DeviceClassGuids.FloppyDisk]     = DeviceCategory.Storage,
            [DeviceClassGuids.TapeDrive]      = DeviceCategory.Storage,
            [DeviceClassGuids.Camera]         = DeviceCategory.Camera,
            [DeviceClassGuids.Ports]          = DeviceCategory.Ports,
            [DeviceClassGuids.MultiportSerial]= DeviceCategory.Ports,
            [DeviceClassGuids.Battery]        = DeviceCategory.Battery,
        };
}
