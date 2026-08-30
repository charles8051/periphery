// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Text.Json.Serialization;

namespace Periphery;

/// <summary>
/// The hardware bus a device is attached to.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<BusType>))]
public enum BusType
{
    /// <summary>Bus type could not be determined.</summary>
    Unknown = 0,

    /// <summary>Universal Serial Bus.</summary>
    USB,

    /// <summary>Peripheral Component Interconnect.</summary>
    PCI,

    /// <summary>Bluetooth wireless.</summary>
    Bluetooth,

    /// <summary>Human Interface Device.</summary>
    HID,

    /// <summary>Software device (virtual).</summary>
    Software,

    /// <summary>High Definition Audio.</summary>
    HDAudio,

    /// <summary>Display adapter bus.</summary>
    Display,

    /// <summary>Small Computer System Interface.</summary>
    SCSI,

    /// <summary>Integrated Drive Electronics.</summary>
    IDE,

    /// <summary>Advanced Configuration and Power Interface.</summary>
    ACPI,
}
