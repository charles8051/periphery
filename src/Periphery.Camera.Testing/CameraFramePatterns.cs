// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Camera.Testing;

/// <summary>
/// Ready-made <see cref="InMemoryCameraBackend.FrameFactory"/> generators. Each
/// writes a pattern a test can assert against after a conversion, a plane walk,
/// or a row walk — so a stride, offset, or plane-order bug produces different
/// pixels instead of the same uniform block.
/// </summary>
/// <remarks>
/// <para>
/// Pure functions of the <see cref="CameraFrameSpec"/> (ADR-0052 grain): same
/// spec in, same bytes out. No clock, no IO, no state carried between frames —
/// the frame index is on the spec.
/// </para>
/// <para>
/// Except for <see cref="FrameIndexConstant"/>, these write only the meaningful
/// bytes of each row and leave <see cref="CameraFramePlaneSpec.RowBytes"/>-to-
/// <see cref="CameraFramePlaneSpec.Stride"/> padding at zero. A consumer that
/// mistakes stride for row width therefore reads zeros where it expects data
/// rather than plausible-looking pixels.
/// </para>
/// </remarks>
public static class CameraFramePatterns
{
    /// <summary>
    /// Fills every byte of the buffer — padding included — with the low byte of
    /// the frame index. The backend's default, and the only pattern that says
    /// nothing about layout: it exists so consecutive frames are distinguishable
    /// and so lifecycle tests written before this API keep the bytes they had.
    /// </summary>
    public static byte[] FrameIndexConstant(CameraFrameSpec spec)
    {
        var data = new byte[spec.FrameSize];
        Array.Fill(data, (byte)(spec.FrameIndex & 0xFF));
        return data;
    }

    /// <summary>
    /// Writes each row's index into that row's first byte, leaving the rest of
    /// the row zero. Reading row <c>n</c> of a plane at the wrong stride lands on
    /// a byte that is not <c>n</c>, which is the whole padded-stride failure mode
    /// in one assertion (#320).
    /// </summary>
    /// <remarks>Row indices wrap at 256, so assert against
    /// <c>row &amp; 0xFF</c> on frames taller than that.</remarks>
    public static byte[] RowIndex(CameraFrameSpec spec)
    {
        var data = new byte[spec.FrameSize];
        foreach (var plane in spec.GetPlanes())
        {
            for (int row = 0; row < plane.Height; row++)
                data[plane.Offset + (row * plane.Stride)] = (byte)(row & 0xFF);
        }
        return data;
    }

    /// <summary>
    /// Ramps each row's meaningful bytes 0, 1, 2, … wrapping at 256, so byte
    /// <c>i</c> of every row holds <c>i &amp; 0xFF</c>. A converter that shifts
    /// columns, swaps channel order, or drops a byte per pixel moves the ramp
    /// visibly.
    /// </summary>
    public static byte[] HorizontalGradient(CameraFrameSpec spec)
    {
        var data = new byte[spec.FrameSize];
        foreach (var plane in spec.GetPlanes())
        {
            for (int row = 0; row < plane.Height; row++)
            {
                int start = plane.Offset + (row * plane.Stride);
                for (int i = 0; i < plane.RowBytes; i++)
                    data[start + i] = (byte)(i & 0xFF);
            }
        }
        return data;
    }

    /// <summary>
    /// Fills each plane's rows with its own constant, so a consumer that reads
    /// the wrong plane, or computes the wrong chroma offset, gets a value from
    /// the plane next door.
    /// </summary>
    /// <param name="planeValues">One value per plane, in plane order — Y then UV
    /// for NV12 / NV21, Y then U then V for I420 (V then U for YV12), a single
    /// value for a packed format. Supplying a different count than the frame's
    /// <see cref="CameraFrameSpec.PlaneCount"/> throws when the frame is
    /// generated.</param>
    public static Func<CameraFrameSpec, byte[]> PlaneConstant(params byte[] planeValues)
    {
        ArgumentNullException.ThrowIfNull(planeValues);
        if (planeValues.Length == 0)
            throw new ArgumentException("At least one plane value is required.", nameof(planeValues));

        // Copy: the returned generator must stay pure even if the caller reuses
        // or mutates the array it passed in.
        byte[] values = [.. planeValues];

        return spec =>
        {
            var planes = spec.GetPlanes();
            if (planes.Count != values.Length)
                throw new InvalidOperationException(
                    $"PlaneConstant was given {values.Length} value(s) but a {spec.PixelFormat} "
                        + $"frame has {planes.Count} plane(s).");

            var data = new byte[spec.FrameSize];
            for (int p = 0; p < planes.Count; p++)
            {
                var plane = planes[p];
                for (int row = 0; row < plane.Height; row++)
                {
                    data.AsSpan(plane.Offset + (row * plane.Stride), plane.RowBytes)
                        .Fill(values[p]);
                }
            }
            return data;
        };
    }
}
