// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Text.Json.Serialization;

namespace Periphery;

/// <summary>
/// Cross-platform device status. Limited to states that every supported
/// platform (Windows, Linux, macOS) can reliably determine.
/// <para>
/// Platform-specific detail (e.g. Windows CIM status strings, Linux sysfs
/// power states) is preserved in <see cref="DeviceInfo.Properties"/> under
/// the <c>"RawStatus"</c> key when richer information is available.
/// </para>
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<DeviceStatus>))]
public enum DeviceStatus
{
    /// <summary>Status could not be determined.</summary>
    Unknown = 0,

    /// <summary>Device is functioning normally.</summary>
    OK,

    /// <summary>Device has encountered an error or is not working properly.</summary>
    Error,

    /// <summary>Device has been intentionally disabled by the user or system policy.</summary>
    Disabled,
}
