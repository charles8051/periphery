// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace Periphery;

/// <summary>
/// OS serial port name for COM/serial devices. Wraps the platform-specific
/// port string (<c>"COM3"</c>, <c>"/dev/ttyUSB0"</c>, <c>"/dev/cu.usbserial-1420"</c>)
/// with validation and value equality.
/// </summary>
/// <remarks>
/// <para>Use <see cref="Value"/> to get the string ready for
/// <c>new System.IO.Ports.SerialPort(portName.Value)</c>.</para>
/// <para>This type does not parse or validate the platform-specific format —
/// it only guarantees the value is non-null and non-empty. The format varies
/// across platforms and Periphery treats it as opaque.</para>
/// </remarks>
[JsonConverter(typeof(SerialPortNameJsonConverter))]
public readonly record struct SerialPortName
{
    /// <summary>
    /// The OS serial port name string, ready for <c>new SerialPort()</c>.
    /// </summary>
    public string Value { get; }

    /// <summary>
    /// Creates a new <see cref="SerialPortName"/> from a port name string.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown if <paramref name="value"/> is null, empty, or whitespace.
    /// </exception>
    public SerialPortName(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    // ── Parsing ────────────────────────────────────────────────────────

    /// <summary>
    /// Parse a serial port name string.
    /// </summary>
    /// <exception cref="FormatException">
    /// Thrown if <paramref name="s"/> is null, empty, or whitespace.
    /// </exception>
    public static SerialPortName Parse(string s)
        => TryParse(s, out var result)
            ? result
            : throw new FormatException($"'{s}' is not a valid serial port name.");

    public static bool TryParse(string? s, [MaybeNullWhen(false)] out SerialPortName result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(s)) return false;
        result = new SerialPortName(s);
        return true;
    }

    // ── Formatting ─────────────────────────────────────────────────────

    /// <summary>Returns the port name string.</summary>
    public override string ToString() => Value;
}
