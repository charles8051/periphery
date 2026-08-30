// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Periphery;

/// <summary>Serializes <see cref="SerialPortName"/> as a plain string (e.g. <c>"COM3"</c>).</summary>
public sealed class SerialPortNameJsonConverter : JsonConverter<SerialPortName>
{
    /// <inheritdoc/>
    public override SerialPortName Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => SerialPortName.Parse(reader.GetString()!);

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, SerialPortName value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString());
}
