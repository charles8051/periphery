// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;

namespace Periphery.Hid;

/// <summary>
/// An immutable HID report — a report ID byte paired with the report payload.
/// Used for all four transfer directions: input, output, get-feature, set-feature.
/// </summary>
public readonly struct HidReport : IEquatable<HidReport>
{
    /// <summary>The one-byte report identifier. Zero for single-report devices.</summary>
    public byte ReportId { get; }

    /// <summary>The report payload, not including the report ID byte.</summary>
    public ReadOnlyMemory<byte> Data { get; }

    /// <summary>Initialises a new <see cref="HidReport"/>.</summary>
    /// <param name="reportId">The report ID byte.</param>
    /// <param name="data">The report payload (excluding the report ID byte).</param>
    public HidReport(byte reportId, ReadOnlyMemory<byte> data)
    {
        ReportId = reportId;
        Data = data;
    }

    /// <inheritdoc/>
    public bool Equals(HidReport other) =>
        ReportId == other.ReportId && Data.Span.SequenceEqual(other.Data.Span);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is HidReport r && Equals(r);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(ReportId, Data.Length);

    /// <inheritdoc/>
    public override string ToString() => $"HidReport(Id=0x{ReportId:X2}, Length={Data.Length})";

    /// <inheritdoc/>
    public static bool operator ==(HidReport left, HidReport right) => left.Equals(right);

    /// <inheritdoc/>
    public static bool operator !=(HidReport left, HidReport right) => !left.Equals(right);
}
