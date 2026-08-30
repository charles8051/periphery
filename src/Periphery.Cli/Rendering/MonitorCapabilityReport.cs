// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Text.Json.Serialization;

namespace Periphery.Cli.Rendering;

/// <summary>
/// The machine-readable row behind <c>periphery monitor list --json</c>: one
/// monitor's control-plane availability and live state, flattened.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately <b>flat</b> and CLI-local. Flat because a smoke check across a
/// fleet wants to diff and assert on scalars, not walk a nested capability tree;
/// CLI-local because this is a presentation shape, and putting it in
/// <c>Periphery.Monitor</c> would export a reporting format as if it were part of
/// the monitor contract.
/// </para>
/// <para>
/// <paramref name="Error"/> is how a per-monitor failure survives into the JSON.
/// A monitor whose capability read throws <b>any</b> exception other than
/// cancellation still emits a row carrying the exception type and message —
/// because a rig where one panel refuses DDC/CI, or whose driver faults with a
/// <c>COMException</c>, is exactly the result a smoke check needs to capture.
/// Dropping the row would make a driver fault look like an absent device, and
/// letting it propagate would discard the healthy rows alongside it.
/// </para>
/// </remarks>
internal sealed record MonitorCapabilityReport(
    string Id,
    string? Name,
    bool? SupportsVcp,
    bool? SupportsDisplayMode,
    string? CurrentMode,
    string? Orientation,
    string? MccsVersion,
    string? Model,
    string? Error);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(List<MonitorCapabilityReport>))]
[JsonSerializable(typeof(MonitorCapabilityReport))]
internal sealed partial class MonitorCapabilityJsonContext : JsonSerializerContext { }
