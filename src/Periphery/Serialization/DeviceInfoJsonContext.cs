// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Periphery;

/// <summary>
/// Source-generated <see cref="JsonSerializerContext"/> for <see cref="DeviceInfo"/>.
/// Provides a trim-safe / AOT-compatible path for JSON serialization.
/// </summary>
/// <remarks>
/// <para>Quick-start:</para>
/// <code>
/// // Serialize a single device (reflection-free)
/// string json = JsonSerializer.Serialize(device, DeviceInfoJsonContext.Default);
///
/// // Serialize a list
/// string json = JsonSerializer.Serialize(devices, DeviceInfoJsonContext.Default);
///
/// // Deserialize
/// DeviceInfo? device = JsonSerializer.Deserialize(json, DeviceInfoJsonContext.Default.DeviceInfo);
/// </code>
/// <para>The context uses camelCase property names and omits null properties by default.
/// For indented output, compose a new options instance:</para>
/// <code>
/// var opts = new JsonSerializerOptions(DeviceInfoJsonContext.Default.Options) { WriteIndented = true };
/// string json = JsonSerializer.Serialize(device, opts);
/// </code>
/// <para><b>Note on <see cref="DeviceInfo.Properties"/>:</b> The bag uses <c>object?</c> values.
/// Common types (<c>string</c>, <c>string[]</c>) are registered and serialize correctly.
/// Custom or unexpected value types may fall back to reflection in non-AOT runtimes
/// or be omitted in full AOT builds.</para>
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(DeviceInfo))]
[JsonSerializable(typeof(List<DeviceInfo>))]
// DeviceId serializes as a bare string via DeviceIdJsonConverter; registered
// here so the generated context exposes a typed DeviceId accessor.
[JsonSerializable(typeof(DeviceId))]
// Concrete types that appear as object? values in DeviceInfo.Properties
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(string[]))]
public partial class DeviceInfoJsonContext : JsonSerializerContext { }
