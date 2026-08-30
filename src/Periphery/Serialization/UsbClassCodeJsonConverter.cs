// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Periphery;

/// <summary>Serializes <see cref="UsbClassCode"/> as a hex string (e.g. <c>"03/01/02"</c>).</summary>
public sealed class UsbClassCodeJsonConverter : JsonConverter<UsbClassCode>
{
    /// <inheritdoc/>
    public override UsbClassCode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => UsbClassCode.Parse(reader.GetString()!);

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, UsbClassCode value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString());
}
