// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;

namespace Periphery.Treehopper.Libraries.Displays;

/// <summary>
/// A 24-bit sRGB colour (red, green, blue). Used as the pixel type throughout
/// the LED strip API — no alpha channel; the strip is opaque.
/// </summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    // ── Common colours ─────────────────────────────────────────────────

    public static readonly Rgb Black   = new(0,   0,   0);
    public static readonly Rgb White   = new(255, 255, 255);
    public static readonly Rgb Red     = new(255, 0,   0);
    public static readonly Rgb Green   = new(0,   255, 0);
    public static readonly Rgb Blue    = new(0,   0,   255);
    public static readonly Rgb Yellow  = new(255, 255, 0);
    public static readonly Rgb Cyan    = new(0,   255, 255);
    public static readonly Rgb Magenta = new(255, 0,   255);
    public static readonly Rgb Orange  = new(255, 140, 0);
    public static readonly Rgb Purple  = new(128, 0,   128);

    // ── Constructors / factories ───────────────────────────────────────

    /// <summary>
    /// Returns the colour at the given hue (0–360), saturation (0–1), and
    /// value / brightness (0–1).
    /// </summary>
    public static Rgb FromHsv(double hue, double saturation, double value)
    {
        hue = ((hue % 360) + 360) % 360; // normalise to [0, 360)
        double c = value * saturation;
        double x = c * (1 - Math.Abs(hue / 60 % 2 - 1));
        double m = value - c;

        double r, g, b;
        int sector = (int)(hue / 60);
        switch (sector)
        {
            case 0: r = c; g = x; b = 0; break;
            case 1: r = x; g = c; b = 0; break;
            case 2: r = 0; g = c; b = x; break;
            case 3: r = 0; g = x; b = c; break;
            case 4: r = x; g = 0; b = c; break;
            default: r = c; g = 0; b = x; break;
        }
        return new Rgb(
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }

    // ── Arithmetic ─────────────────────────────────────────────────────

    /// <summary>Scales each channel by <paramref name="factor"/> (clamped 0–1).</summary>
    public Rgb Scale(double factor)
    {
        factor = Math.Clamp(factor, 0.0, 1.0);
        return new Rgb(
            (byte)Math.Round(R * factor),
            (byte)Math.Round(G * factor),
            (byte)Math.Round(B * factor));
    }

    /// <summary>
    /// Linearly interpolates between this colour and <paramref name="target"/>
    /// at <paramref name="t"/> (0 = this, 1 = target).
    /// </summary>
    public Rgb Lerp(Rgb target, double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        return new Rgb(
            (byte)Math.Round(R + (target.R - R) * t),
            (byte)Math.Round(G + (target.G - G) * t),
            (byte)Math.Round(B + (target.B - B) * t));
    }

    // ── Formatting ─────────────────────────────────────────────────────

    /// <inheritdoc />
    public override string ToString() => $"#{R:X2}{G:X2}{B:X2}";
}
