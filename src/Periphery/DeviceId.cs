// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace Periphery;

/// <summary>
/// A device <em>instance ID</em> (<see cref="DeviceInfo.Id"/>): the
/// platform-native unique identifier periphery threads through every
/// subsystem as the entity key for a discovered device.
/// </summary>
/// <remarks>
/// <para>Windows device instance IDs — and the equivalents periphery surfaces on
/// Linux/macOS — are <b>case-insensitive</b> by contract. The same physical
/// device can re-enumerate with different casing: e.g. after a firmware reboot,
/// or because the snapshot/query path and the change-notification path report
/// the id in different case. Comparing ids case-sensitively makes one device
/// look like two — a phantom that lingers because nothing matches it back to
/// the original.</para>
/// <para>This type carries that invariant in the value itself: <see cref="Equals(DeviceId)"/>,
/// <see cref="GetHashCode"/>, and the <c>==</c>/<c>!=</c> operators all compare
/// <see cref="Value"/> using <see cref="StringComparison.OrdinalIgnoreCase"/>. A
/// dictionary or set keyed by <see cref="DeviceId"/> is therefore case-insensitive
/// without any per-call-site comparer wiring — the invariant lives in exactly one
/// spot. (A <c>record struct</c>'s synthesized equality is case-<i>sensitive</i>,
/// so these members are overridden by hand.)</para>
/// <para>The value is treated as opaque: periphery does not parse or canonicalize
/// the platform-specific format, only guarantee non-null/non-empty identity and
/// case-insensitive comparison.</para>
/// </remarks>
[JsonConverter(typeof(DeviceIdJsonConverter))]
public readonly record struct DeviceId
{
    /// <summary>The platform-native instance-id string.</summary>
    public string Value { get; }

    /// <summary>
    /// Creates a new <see cref="DeviceId"/> from a platform-native id string.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown if <paramref name="value"/> is null, empty, or whitespace.
    /// </exception>
    public DeviceId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    // ── Parsing ────────────────────────────────────────────────────────

    /// <summary>
    /// Parse a device instance-id string.
    /// </summary>
    /// <exception cref="FormatException">
    /// Thrown if <paramref name="s"/> is null, empty, or whitespace.
    /// </exception>
    public static DeviceId Parse(string s)
        => TryParse(s, out var result)
            ? result
            : throw new FormatException($"'{s}' is not a valid device id.");

    public static bool TryParse(string? s, [MaybeNullWhen(false)] out DeviceId result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(s)) return false;
        result = new DeviceId(s);
        return true;
    }

    // ── Equality (case-insensitive, OrdinalIgnoreCase) ─────────────────

    /// <summary>
    /// Case-insensitive equality over <see cref="Value"/>
    /// (<see cref="StringComparison.OrdinalIgnoreCase"/>).
    /// </summary>
    public bool Equals(DeviceId other)
        => string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public override int GetHashCode()
        => Value is null
            ? 0
            : StringComparer.OrdinalIgnoreCase.GetHashCode(Value);

    // ── Conversions / formatting ───────────────────────────────────────

    /// <summary>Returns the underlying instance-id string.</summary>
    public override string ToString() => Value;

    /// <summary>Wraps a platform-native id string as a <see cref="DeviceId"/>.</summary>
    public static implicit operator DeviceId(string value) => new(value);

    /// <summary>Unwraps the underlying instance-id string.</summary>
    public static implicit operator string(DeviceId id) => id.Value;
}
