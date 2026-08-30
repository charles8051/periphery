// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Periphery;

/// <summary>
/// Serializes <see cref="PhysicalAddress"/> as a colon-separated hex string
/// (e.g. <c>"00:1A:2B:3C:4D:5E"</c>).
/// </summary>
public sealed class PhysicalAddressJsonConverter : JsonConverter<PhysicalAddress>
{
    /// <inheritdoc/>
    public override PhysicalAddress? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var s = reader.GetString();
        if (s is null) return null;
        // Accept both "00:1A:2B:3C:4D:5E" and "001A2B3C4D5E"
        return PhysicalAddress.Parse(s.Replace(":", string.Empty).Replace("-", string.Empty));
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, PhysicalAddress value, JsonSerializerOptions options)
        => writer.WriteStringValue(string.Join(":", value.GetAddressBytes().Select(b => b.ToString("X2"))));
}
