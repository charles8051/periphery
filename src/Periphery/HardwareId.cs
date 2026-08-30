// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json.Serialization;

namespace Periphery;

/// <summary>
/// A 16-bit hardware identifier (USB VID/PID or equivalent).
/// Stores the canonical <see cref="ushort"/> value and renders as
/// zero-padded uppercase hex (<c>"046D"</c>).
/// </summary>
[JsonConverter(typeof(HardwareIdJsonConverter))]
public readonly record struct HardwareId : IFormattable
{
    /// <summary>The raw 16-bit numeric value.</summary>
    public ushort Value { get; }

    public HardwareId(ushort value) => Value = value;

    // ── Parsing ────────────────────────────────────────────────────────

    /// <summary>
    /// Parse a hex string with or without a <c>0x</c> prefix, or a plain
    /// decimal string. Throws <see cref="FormatException"/> on failure.
    /// </summary>
    public static HardwareId Parse(string s)
        => TryParse(s, out var id)
            ? id
            : throw new FormatException($"'{s}' is not a valid 16-bit hardware ID.");

    public static bool TryParse(string? s, [MaybeNullWhen(false)] out HardwareId result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(s)) return false;

        // "0x1234" or "0X1234"
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            s = s[2..];

        // Try hex first (most common in hardware contexts), then decimal.
        if (ushort.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort hex))
        {
            result = new HardwareId(hex);
            return true;
        }

        if (ushort.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out ushort dec))
        {
            result = new HardwareId(dec);
            return true;
        }

        return false;
    }

    // ── Formatting ─────────────────────────────────────────────────────

    /// <summary>Zero-padded uppercase hex (e.g. <c>"046D"</c>).</summary>
    public override string ToString() => Value.ToString("X4");

    public string ToString(string? format, IFormatProvider? formatProvider)
        => Value.ToString(format ?? "X4", formatProvider);

    public static implicit operator ushort(HardwareId id) => id.Value;
    public static explicit operator HardwareId(ushort value) => new(value);
}
