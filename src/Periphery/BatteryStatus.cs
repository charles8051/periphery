// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Text.Json.Serialization;

namespace Periphery;

/// <summary>
/// Battery power state.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<BatteryStatus>))]
public enum BatteryStatus
{
    /// <summary>Status could not be determined.</summary>
    Unknown,

    /// <summary>Battery is charging.</summary>
    Charging,

    /// <summary>Battery is discharging (running on battery power).</summary>
    Discharging,

    /// <summary>Battery is full.</summary>
    Full,

    /// <summary>
    /// External power is connected but battery is not charging
    /// (e.g. conservation mode, charge limit reached).
    /// </summary>
    NotCharging,
}
