// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Drawing;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Periphery;

/// <summary>
/// Serializes <see cref="Rectangle"/> as a compact string <c>"X,Y WxH"</c>
/// (e.g. <c>"0,0 2560x1440"</c>).
/// </summary>
public sealed class RectangleJsonConverter : JsonConverter<Rectangle>
{
    /// <inheritdoc/>
    public override Rectangle Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var s = reader.GetString();
        if (s is null) return default;
        // format: "X,Y WxH"
        var space = s.IndexOf(' ');
        if (space < 0) return default;
        var comma = s.IndexOf(',');
        if (comma < 0 || comma > space) return default;
        var x = s.IndexOf('x', space);
        if (x < 0) return default;
        return new Rectangle(
            int.Parse(s.AsSpan(0, comma)),
            int.Parse(s.AsSpan(comma + 1, space - comma - 1)),
            int.Parse(s.AsSpan(space + 1, x - space - 1)),
            int.Parse(s.AsSpan(x + 1)));
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, Rectangle value, JsonSerializerOptions options)
        => writer.WriteStringValue($"{value.X},{value.Y} {value.Width}x{value.Height}");
}
