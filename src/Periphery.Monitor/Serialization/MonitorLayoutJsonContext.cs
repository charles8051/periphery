// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Periphery.Monitor.Serialization;

/// <summary>
/// Source-generated JSON contract for the <see cref="MonitorLayout"/> read model
/// (ADR-0059), so a topology snapshot can be emitted as machine-readable output
/// without reflection — by the CLI's <c>monitor layout --json</c>, and by any
/// consumer that wants to record or ship a layout.
/// </summary>
/// <remarks>
/// <para>
/// Enums serialize <b>by name</b>, not by ordinal. This is deliberate and
/// load-bearing for the platform-neutral value contracts: <see cref="MonitorOrientation"/>
/// and <see cref="MonitorOutputTechnology"/> define their numeric values as an
/// opaque serialization detail that no consumer may interpret (ADR-0064,
/// ADR-0070), so emitting <c>"IndirectWired"</c> rather than <c>7</c> keeps the
/// JSON honest to that contract and readable in a smoke-test log.
/// </para>
/// <para>
/// <see cref="Periphery.DeviceId"/> serializes as a bare string via its own
/// converter, so an entry's identity joins to <c>DeviceInfo.Id</c> in the output
/// the same way it does in memory.
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true,
    WriteIndented = false)]
[JsonSerializable(typeof(MonitorLayout))]
[JsonSerializable(typeof(MonitorLayoutEntry))]
[JsonSerializable(typeof(List<MonitorLayoutEntry>))]
[JsonSerializable(typeof(DisplayMode))]
[JsonSerializable(typeof(DisplayPosition))]
[JsonSerializable(typeof(DisplaySize))]
[JsonSerializable(typeof(DeviceId))]
public partial class MonitorLayoutJsonContext : JsonSerializerContext { }
