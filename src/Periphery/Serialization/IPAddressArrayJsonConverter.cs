// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Periphery;

/// <summary>
/// Serializes <see cref="ImmutableArray{T}">ImmutableArray</see>&lt;<see cref="IPAddress"/>&gt;?
/// as a JSON array of IP address strings.
/// </summary>
public sealed class IPAddressArrayJsonConverter : JsonConverter<ImmutableArray<IPAddress>?>
{
    // Must handle null tokens directly because ImmutableArray<T> is a value type —
    // STJ does not automatically short-circuit for Nullable<struct> converters.
    /// <inheritdoc/>
    public override bool HandleNull => true;

    /// <inheritdoc/>
    public override ImmutableArray<IPAddress>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;

        var builder = ImmutableArray.CreateBuilder<IPAddress>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType == JsonTokenType.String)
                builder.Add(IPAddress.Parse(reader.GetString()!));
        }
        return builder.ToImmutable();
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, ImmutableArray<IPAddress>? value, JsonSerializerOptions options)
    {
        if (!value.HasValue) { writer.WriteNullValue(); return; }
        writer.WriteStartArray();
        foreach (var ip in value.Value)
            writer.WriteStringValue(ip.ToString());
        writer.WriteEndArray();
    }
}
