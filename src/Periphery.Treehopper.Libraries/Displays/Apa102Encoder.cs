// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;

namespace Periphery.Treehopper.Libraries.Displays;

/// <summary>
/// Pure APA102 wire-format encoder. Converts a <see cref="LedFrame"/> to the
/// byte stream that the APA102 chain expects over SPI. No IO, no state.
/// </summary>
/// <remarks>
/// Wire format (MSB-first; the strip drives SPI in mode 1,1 — see
/// <see cref="Apa102Strip"/>):
/// <code>
///   [start: 4 × 0x00]
///   per LED: [0b111 + 5-bit brightness | B | G | R]
///   [end: ceil(N / 16) × 0x00]
/// </code>
/// The end frame provides the clock pulses needed to latch the last LED in the
/// chain (APA102 uses a "global clock" scheme — each IC passes the signal one
/// clock cycle late).
/// </remarks>
internal static class Apa102Encoder
{
    /// <summary>
    /// Encodes <paramref name="frame"/> to an APA102 SPI byte array.
    /// Allocates once; the caller may cache and re-use the buffer.
    /// </summary>
    public static byte[] Encode(LedFrame frame)
    {
        int n = frame.Pixels.Length;
        // End frame: ceil(N/16) bytes of 0xFF (≥ N/2 clock pulses → each byte = 8 clocks)
        int endLen = (n + 15) / 16;
        var buf = new byte[4 + n * 4 + endLen]; // start + LEDs + end (start is 0x00-filled by default)

        int pos = 4; // skip the start frame (already 0x00)
        byte header = (byte)(0xE0 | Math.Clamp((int)frame.Brightness, 0, 31));
        foreach (var px in frame.Pixels)
        {
            buf[pos++] = header;
            buf[pos++] = px.B; // APA102 order: Blue first
            buf[pos++] = px.G;
            buf[pos++] = px.R;
        }
        // End frame left as 0x00 (the buffer is already zero-initialised). Only the
        // extra clock edges matter; zeros are safer than 0xFF, which a long chain can
        // misread as the leading 111-brightness bits of another LED frame. This
        // matches the upstream Treehopper.Libraries driver.
        return buf;
    }
}
