// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.Immutable;

namespace Periphery.Treehopper.Libraries.Displays;

/// <summary>
/// An immutable snapshot of a full LED strip's pixel colours at one point in
/// time. Passed to <see cref="Apa102Strip.TickAsync"/> (or produced by
/// <see cref="LedAnimation.Render"/>). (ADR-0052 DEC-005.)
/// </summary>
/// <param name="Pixels">
/// One <see cref="Rgb"/> per LED, ordered from index 0 (first in the chain)
/// to index <c>Pixels.Length − 1</c> (last).
/// </param>
/// <param name="Brightness">
/// APA102 global 5-bit brightness (0–31) applied to every pixel. Default 31
/// (maximum). Use this to dim the whole strip without altering the RGB ratios;
/// for smooth fades prefer scaling the <see cref="Rgb"/> values instead.
/// </param>
public sealed record LedFrame(ImmutableArray<Rgb> Pixels, byte Brightness = 31)
{
    /// <summary>Number of LEDs in this frame.</summary>
    public int Count => Pixels.Length;

    /// <summary>
    /// Creates a frame of <paramref name="count"/> pixels all set to
    /// <paramref name="color"/>.
    /// </summary>
    public static LedFrame Solid(int count, Rgb color, byte brightness = 31)
    {
        var pixels = new Rgb[count];
        Array.Fill(pixels, color);
        return new(ImmutableArray.Create(pixels), brightness);
    }

    /// <summary>Creates a frame with all pixels set to black (off).</summary>
    public static LedFrame Off(int count) => Solid(count, Rgb.Black);
}
