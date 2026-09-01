// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Text.Json.Serialization;

namespace Periphery;

/// <summary>
/// Whether a device is real hardware or software-enumerated. The data form of
/// <see cref="DeviceFilter.PhysicalOnly"/> and
/// <see cref="DeviceFilter.VirtualOnly"/>, for
/// <see cref="DeviceFilterSpec.Physicality"/>.
/// </summary>
/// <remarks>
/// Named rather than boolean on purpose. <c>"physicality": "Virtual"</c> reads
/// correctly in a configuration file where <c>"physical": false</c> does not,
/// and both fluent methods today are one-liners over
/// <see cref="BusType.Software"/> — a classification that platform work may yet
/// refine. A named enum can gain a member; a <c>bool?</c> cannot.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<DevicePhysicality>))]
public enum DevicePhysicality
{
    /// <summary>Real hardware — everything except <see cref="BusType.Software"/>.</summary>
    Physical,

    /// <summary>Software-enumerated: virtual adapters, print queues, software audio endpoints.</summary>
    Virtual,
}
