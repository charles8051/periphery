// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Periphery;

/// <summary>Serializes <see cref="HardwareId"/> as a zero-padded hex string (e.g. <c>"046D"</c>).</summary>
public sealed class HardwareIdJsonConverter : JsonConverter<HardwareId>
{
    /// <inheritdoc/>
    public override HardwareId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => HardwareId.Parse(reader.GetString()!);

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, HardwareId value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString());
}
