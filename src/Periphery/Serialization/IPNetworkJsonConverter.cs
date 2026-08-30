// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Periphery;

/// <summary>Serializes <see cref="IPNetwork"/> as CIDR notation (e.g. <c>"192.168.1.0/24"</c>).</summary>
public sealed class IPNetworkJsonConverter : JsonConverter<IPNetwork>
{
    /// <inheritdoc/>
    public override IPNetwork Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => IPNetwork.Parse(reader.GetString()!);

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, IPNetwork value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString());
}
