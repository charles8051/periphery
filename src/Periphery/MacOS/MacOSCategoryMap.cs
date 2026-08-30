// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.Generic;
using System.Runtime.Versioning;

namespace Periphery.MacOS;

/// <summary>
/// Maps <see cref="DeviceCategory"/> values to IOKit class name strings for
/// <c>IOServiceMatching()</c> queries, and resolves IOKit class names back to
/// <see cref="DeviceCategory"/> and <see cref="BusType"/> values.
/// </summary>
[SupportedOSPlatform("macos")]
internal static class MacOSCategoryMap
{
    // ── IOKit class name constants ─────────────────────────────────────

    internal const string IOUSBDevice = "IOUSBDevice";
    internal const string IOUSBHostDevice = "IOUSBHostDevice";
    internal const string IOBluetoothDevice = "IOBluetoothDevice";
    internal const string IONetworkInterface = "IONetworkInterface";
    internal const string IODisplayConnect = "IODisplayConnect";
    internal const string IOHIDDevice = "IOHIDDevice";
    internal const string IOAudioDevice = "IOAudioDevice";
    internal const string IOMedia = "IOMedia";
    internal const string AppleSmartBattery = "AppleSmartBattery";

    // Tier 1 — direct IOKit class name mapping (ADR-0013)
    internal const string IOVideoDevice = "IOVideoDevice";
    internal const string IOSerialBSDClient = "IOSerialBSDClient";
    internal const string IOUSBSmartCardController = "IOUSBSmartCardController";

    /// <summary>
    /// Returns the IOKit class name(s) to query for the given <paramref name="category"/>.
    /// <see cref="DeviceCategory.All"/> returns all known IOKit classes.
    /// </summary>
    internal static string[] GetIOKitClasses(DeviceCategory? category) => category switch
    {
        null or DeviceCategory.All => [IOUSBDevice, IOUSBHostDevice, IOBluetoothDevice,
            IONetworkInterface, IODisplayConnect, IOHIDDevice, IOAudioDevice, IOMedia,
            AppleSmartBattery, IOVideoDevice, IOSerialBSDClient, IOUSBSmartCardController],
        DeviceCategory.Usb       => [IOUSBDevice, IOUSBHostDevice],
        DeviceCategory.Bluetooth => [IOBluetoothDevice],
        DeviceCategory.Network   => [IONetworkInterface],
        DeviceCategory.Display   => [IODisplayConnect],
        DeviceCategory.Monitor   => [IODisplayConnect],
        DeviceCategory.Hid       => [IOHIDDevice],
        DeviceCategory.Keyboard  => [IOHIDDevice],
        DeviceCategory.Mouse     => [IOHIDDevice],
        DeviceCategory.Audio     => [IOAudioDevice],
        DeviceCategory.Storage   => [IOMedia],
        DeviceCategory.Battery   => [AppleSmartBattery],

        // Tier 1 — direct IOKit class name mapping (ADR-0013)
        DeviceCategory.Camera    => [IOVideoDevice],
        DeviceCategory.Ports     => [IOSerialBSDClient],

        _ => throw new ArgumentOutOfRangeException(nameof(category), category, "Unknown device category.")
    };

    /// <summary>
    /// Resolves an IOKit class name to a <see cref="DeviceCategory"/>.
    /// Returns <see cref="DeviceCategory.All"/> if no mapping is found.
    /// </summary>
    internal static DeviceCategory ResolveCategory(string? ioKitClassName)
    {
        if (ioKitClassName is null) return DeviceCategory.All;
        if (!s_classToCategory.TryGetValue(ioKitClassName, out var cat))
            return DeviceCategory.All;
        return cat;
    }

    /// <summary>
    /// Resolves a <see cref="DeviceCategory"/> for an IOHIDDevice based on HID usage page/usage.
    /// Falls back to <see cref="DeviceCategory.Hid"/> if the usage is not recognized.
    /// </summary>
    internal static DeviceCategory ResolveHidCategory(int? primaryUsagePage, int? primaryUsage)
    {
        // HID usage page 0x01 (Generic Desktop), usage 0x06 (Keyboard)
        if (primaryUsagePage == 0x01 && primaryUsage == 0x06)
            return DeviceCategory.Keyboard;
        // HID usage page 0x01 (Generic Desktop), usage 0x02 (Mouse)
        if (primaryUsagePage == 0x01 && primaryUsage == 0x02)
            return DeviceCategory.Mouse;
        return DeviceCategory.Hid;
    }

    /// <summary>
    /// Resolves a <see cref="DeviceCategory"/> for a USB device based on the
    /// <c>bDeviceClass</c> field from the USB device descriptor. Always returns
    /// <c>null</c> after ADR-0051: the USB-class categories (Imaging 0x06,
    /// Printer 0x07, smart-card 0x0B) became capability tags emitted by enrichers,
    /// so no class code maps to a category any more. Retained as the macOS
    /// USB-class extension point; the caller falls back to the IOKit class
    /// name–based category.
    /// </summary>
    internal static DeviceCategory? ResolveUsbCategory(int? usbDeviceClass) => usbDeviceClass switch
    {
        _ => null,
    };

    /// <summary>
    /// Infers the <see cref="BusType"/> from an IOKit class name.
    /// </summary>
    internal static BusType InferBusType(string? ioKitClassName) => ioKitClassName switch
    {
        IOUSBDevice or IOUSBHostDevice => BusType.USB,
        IOBluetoothDevice => BusType.Bluetooth,
        IOHIDDevice => BusType.HID,
        IODisplayConnect => BusType.Display,
        IOAudioDevice => BusType.HDAudio,
        IOVideoDevice or IOUSBSmartCardController => BusType.USB,
        _ => BusType.Unknown,
    };

    private static readonly Dictionary<string, DeviceCategory> s_classToCategory =
        new(StringComparer.Ordinal)
        {
            [IOUSBDevice]              = DeviceCategory.Usb,
            [IOUSBHostDevice]          = DeviceCategory.Usb,
            [IOBluetoothDevice]        = DeviceCategory.Bluetooth,
            [IONetworkInterface]       = DeviceCategory.Network,
            [IODisplayConnect]         = DeviceCategory.Display,
            [IOHIDDevice]              = DeviceCategory.Hid,
            [IOAudioDevice]            = DeviceCategory.Audio,
            [IOMedia]                  = DeviceCategory.Storage,
            [AppleSmartBattery]        = DeviceCategory.Battery,
            [IOVideoDevice]            = DeviceCategory.Camera,
            [IOSerialBSDClient]        = DeviceCategory.Ports,
        };
}
