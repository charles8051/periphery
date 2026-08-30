// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Drawing;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Periphery;

/// <summary>
/// Serializes <see cref="SizeF"/> as a compact string <c>"W x H"</c> with one decimal place
/// (e.g. <c>"93.6x93.6"</c>).
/// </summary>
public sealed class SizeFJsonConverter : JsonConverter<SizeF>
{
    /// <inheritdoc/>
    public override SizeF Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var s = reader.GetString();
        if (s is null) return default;
        var x = s.IndexOf('x');
        if (x < 0) return default;
        return new SizeF(
            float.Parse(s.AsSpan(0, x), CultureInfo.InvariantCulture),
            float.Parse(s.AsSpan(x + 1), CultureInfo.InvariantCulture));
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, SizeF value, JsonSerializerOptions options)
        => writer.WriteStringValue(
            string.Create(CultureInfo.InvariantCulture, $"{value.Width:G}x{value.Height:G}"));
}
