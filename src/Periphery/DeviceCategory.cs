// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Text.Json.Serialization;

namespace Periphery;

/// <summary>
/// Broad hardware categories that Periphery can discover across all platforms.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<DeviceCategory>))]
public enum DeviceCategory
{
    /// <summary>All device types. No category filter applied.</summary>
    All = 0,

    /// <summary>USB devices and hubs.</summary>
    Usb,

    /// <summary>Bluetooth radios and paired peripherals.</summary>
    Bluetooth,

    /// <summary>Wired and wireless network adapters.</summary>
    Network,

    /// <summary>GPUs and display adapters.</summary>
    Display,

    /// <summary>Monitors and external screens.</summary>
    Monitor,

    /// <summary>Human Interface Devices — game controllers and other HID-class peripherals.
    /// Does not include keyboards or mice; use <see cref="Keyboard"/> and <see cref="Mouse"/> for those.</summary>
    Hid,

    /// <summary>Keyboards. Maps to the Windows Keyboard device class, distinct from HID.</summary>
    Keyboard,

    /// <summary>Mice and pointing devices. Maps to the Windows Mouse device class, distinct from HID.</summary>
    Mouse,

    /// <summary>Sound cards, speakers, microphones.</summary>
    Audio,

    /// <summary>Disk drives, SSDs, removable storage.</summary>
    Storage,

    /// <summary>Serial and parallel ports.</summary>
    Ports,

    /// <summary>Battery and power supply devices — laptop batteries, UPS units.</summary>
    Battery,

    /// <summary>Webcams and video capture devices.</summary>
    Camera,
}
