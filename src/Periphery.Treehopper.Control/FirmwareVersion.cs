// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Globalization;

namespace Periphery.Treehopper.Control;

/// <summary>
/// Helpers for the Treehopper firmware version — the raw USB <c>bcdDevice</c> word
/// surfaced as <c>TreehopperBoard.Version</c>. Migrated here as the canonical home as
/// the standalone updater is folded into this app (feature-spec ADR Decision 3).
/// </summary>
public static class FirmwareVersion
{
    /// <summary>
    /// Parses a target version: hex (<c>0x0112</c>) or plain decimal (<c>274</c>). Both
    /// denote the raw <c>bcdDevice</c> word compared against a board's version.
    /// </summary>
    public static bool TryParse(string? text, out int version)
    {
        version = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();

        if (text.StartsWith("0x", System.StringComparison.OrdinalIgnoreCase))
            return int.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out version)
                && version >= 0;

        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out version)
            && version >= 0;
    }

    /// <summary>
    /// Friendly form the upstream SDK uses (<c>code / 100.0</c>, e.g. <c>274</c> →
    /// <c>"2.74"</c>) plus the raw code, so an operator can read either.
    /// </summary>
    public static string Describe(int code) => $"{code / 100.0:0.00} (code {code})";
}
