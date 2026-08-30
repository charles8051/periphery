// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Periphery;

/// <summary>Serializes <see cref="Version"/> as a dotted string (e.g. <c>"10.0.19041.1"</c>).</summary>
public sealed class VersionJsonConverter : JsonConverter<Version>
{
    /// <inheritdoc/>
    public override Version? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var s = reader.GetString();
        return s is null ? null : Version.Parse(s);
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, Version value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString());
}
