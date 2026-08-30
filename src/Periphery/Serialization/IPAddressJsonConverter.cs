// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Periphery;

/// <summary>Serializes <see cref="IPAddress"/> as a string (e.g. <c>"192.168.1.1"</c>).</summary>
public sealed class IPAddressJsonConverter : JsonConverter<IPAddress>
{
    /// <inheritdoc/>
    public override IPAddress? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var s = reader.GetString();
        return s is null ? null : IPAddress.Parse(s);
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, IPAddress value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString());
}
