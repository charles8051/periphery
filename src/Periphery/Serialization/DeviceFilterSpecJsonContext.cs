// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Periphery;

/// <summary>
/// Source-generated <see cref="JsonSerializerContext"/> for
/// <see cref="DeviceFilterSpec"/>, giving a trim-safe / AOT-compatible path.
/// </summary>
/// <remarks>
/// <para>
/// The options here are deliberately identical to
/// <see cref="DeviceInfoJsonContext"/>'s — camelCase, nulls omitted — and a test
/// asserts they stay that way. Two contexts drifting on casing would be the same
/// class of bug as three fluent surfaces drifting on criteria.
/// </para>
/// <para>
/// <see cref="DeviceFilterSpec"/> is declared
/// <see cref="JsonUnmappedMemberHandling.Disallow"/>, so a misspelled or
/// wrongly-cased member throws instead of binding to an empty spec that matches
/// everything.
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false
)]
[JsonSerializable(typeof(DeviceFilterSpec))]
[JsonSerializable(typeof(Dictionary<string, DeviceFilterSpec>))]
public partial class DeviceFilterSpecJsonContext : JsonSerializerContext { }
