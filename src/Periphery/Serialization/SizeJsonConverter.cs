// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Drawing;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Periphery;

/// <summary>
/// Serializes <see cref="Size"/> as a compact string <c>"WxH"</c>
/// (e.g. <c>"1920x1080"</c>).
/// </summary>
public sealed class SizeJsonConverter : JsonConverter<Size>
{
    /// <inheritdoc/>
    public override Size Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var s = reader.GetString();
        if (s is null) return default;
        var x = s.IndexOf('x');
        if (x < 0) return default;
        return new Size(int.Parse(s.AsSpan(0, x)), int.Parse(s.AsSpan(x + 1)));
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, Size value, JsonSerializerOptions options)
        => writer.WriteStringValue($"{value.Width}x{value.Height}");
}
