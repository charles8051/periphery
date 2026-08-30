// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Text.Json.Serialization;

namespace Periphery;

/// <summary>
/// USB signalling speed.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<UsbSpeed>))]
public enum UsbSpeed
{
    /// <summary>USB 1.0 Low Speed — 1.5 Mbps.</summary>
    Low,

    /// <summary>USB 1.1 Full Speed — 12 Mbps.</summary>
    Full,

    /// <summary>USB 2.0 High Speed — 480 Mbps.</summary>
    High,

    /// <summary>USB 3.0 (Gen 1) SuperSpeed — 5 Gbps.</summary>
    Super,

    /// <summary>USB 3.1 (Gen 2) SuperSpeed+ — 10 Gbps.</summary>
    SuperPlus,

    /// <summary>USB 3.2 (Gen 2×2) SuperSpeed+ — 20 Gbps.</summary>
    SuperPlusx2,

    /// <summary>USB4 — 40 Gbps.</summary>
    Usb4,
}
