// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Camera;

/// <summary>
/// The single pure source of truth for per-<see cref="CameraPixelFormat"/> frame
/// dimension scalars: bits-per-pixel, row stride, total frame size, and plane
/// count. Every backend, pool, and benchmark that needs one of these numbers
/// routes through here so the values can never drift between call sites.
/// </summary>
/// <remarks>
/// <para>
/// Pure and total (ADR-0052 grain): same input → same output, no IO, no clock,
/// no mutable state. Stride / size math only — the multi-plane *offset* layout
/// lives in <see cref="Internal.PlaneLayout"/>, which this class is consistent
/// with by construction (both derive from the same per-format facts).
/// </para>
/// <para>
/// Bits-per-pixel rather than bytes: the planar 4:2:0 formats (NV12 / NV21 /
/// I420 / YV12) are 12 bits/px = 1.5 bytes/px, which is not an integer byte
/// count. Exposing the rate in bits keeps every value exact and makes the
/// 4:2:0 cost impossible to round to a wrong whole number — the original
/// drift bug (NV12 charged 3 bytes/px, double the truth).
/// </para>
/// <para>
/// MJPEG is compressed and has no fixed pixel cost, so it has no
/// <see cref="BitsPerPixel"/>; <see cref="FrameSize"/> returns a generous
/// worst-case buffer estimate for it instead.
/// </para>
/// </remarks>
public static class CameraFrameLayout
{
    /// <summary>
    /// Bits per pixel for a packed or planar (uncompressed) format. Planar
    /// 4:2:0 formats are 12 (= 1.5 bytes/px).
    /// </summary>
    /// <exception cref="System.ArgumentException">
    /// Thrown for <see cref="CameraPixelFormat.Mjpeg"/> (compressed — no fixed
    /// pixel cost) and <see cref="CameraPixelFormat.Unknown"/>.
    /// </exception>
    public static int BitsPerPixel(CameraPixelFormat format) => format switch
    {
        CameraPixelFormat.Rgb24 or CameraPixelFormat.Bgr24 => 24,
        CameraPixelFormat.Rgba32 or CameraPixelFormat.Bgra32 or CameraPixelFormat.Argb32 => 32,
        CameraPixelFormat.Yuy2 or CameraPixelFormat.Uyvy => 16,
        CameraPixelFormat.Gray8 => 8,
        CameraPixelFormat.Gray16 => 16,
        // 4:2:0 chroma subsampling: 8 luma bits + 2×(8/4) chroma bits = 12 bits/px.
        CameraPixelFormat.Nv12 or CameraPixelFormat.Nv21
            or CameraPixelFormat.I420 or CameraPixelFormat.Yv12 => 12,
        _ => throw new System.ArgumentException(
            $"{format} has no fixed bits-per-pixel (compressed or unknown).", nameof(format)),
    };

    /// <summary>
    /// Natural (unpadded) row stride in bytes for one image row at
    /// <paramref name="width"/> pixels — i.e. the luma/packed plane stride. A
    /// source buffer may report a larger, alignment-padded stride; backends pass
    /// the platform-reported stride into <see cref="FrameSize"/> in that case.
    /// </summary>
    /// <remarks>
    /// For the 4:2:0 planar formats this is the luma stride (= width); the
    /// chroma stride is half (handled in <see cref="Internal.PlaneLayout"/>).
    /// MJPEG and Unknown have no row stride and return <paramref name="width"/>
    /// as a neutral byte count.
    /// </remarks>
    public static int BytesPerRow(CameraPixelFormat format, int width) => format switch
    {
        CameraPixelFormat.Rgb24 or CameraPixelFormat.Bgr24 => width * 3,
        CameraPixelFormat.Rgba32 or CameraPixelFormat.Bgra32 or CameraPixelFormat.Argb32 => width * 4,
        CameraPixelFormat.Yuy2 or CameraPixelFormat.Uyvy => width * 2,
        CameraPixelFormat.Gray16 => width * 2,
        // Gray8 and the 4:2:0 luma planes are 1 byte per pixel-column; MJPEG /
        // Unknown have no meaningful stride and fall through to the same neutral
        // value (a whole-buffer single plane).
        _ => width,
    };

    /// <summary>
    /// Total frame buffer size in bytes for one <paramref name="width"/> ×
    /// <paramref name="height"/> frame.
    /// </summary>
    /// <param name="format">The frame's pixel format.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="lumaStride">
    /// Row stride of the luma/packed plane in bytes, or 0 to derive it from
    /// <paramref name="width"/> via <see cref="BytesPerRow"/>. Pass the
    /// platform-reported stride when the source buffer has alignment-padded rows
    /// so the size accounts for the padding.
    /// </param>
    /// <remarks>
    /// MJPEG has no pixel-exact size; this returns a generous worst-case buffer
    /// (½ byte/px) so a single compressed frame always fits.
    /// </remarks>
    public static int FrameSize(CameraPixelFormat format, int width, int height, int lumaStride = 0)
    {
        if (format == CameraPixelFormat.Mjpeg)
        {
            // Compressed: typical JPEG is well under 1 byte/px. Half a byte per
            // pixel is a comfortable upper bound for a webcam MJPEG frame.
            return width * height / 2;
        }

        int stride = lumaStride > 0 ? lumaStride : BytesPerRow(format, width);
        int lumaSize = stride * height;

        return format switch
        {
            // 4:2:0: luma plane + a half-size chroma region (NV12/NV21: one
            // interleaved UV plane; I420/YV12: two quarter-size U,V planes).
            CameraPixelFormat.Nv12 or CameraPixelFormat.Nv21
                or CameraPixelFormat.I420 or CameraPixelFormat.Yv12 => lumaSize + lumaSize / 2,
            _ => lumaSize,
        };
    }

    /// <summary>
    /// Number of distinct memory planes for a format: 1 for packed / compressed,
    /// 2 for NV12 / NV21 (Y + interleaved UV), 3 for I420 / YV12 (Y + U + V).
    /// </summary>
    public static int PlaneCount(CameraPixelFormat format) => format switch
    {
        CameraPixelFormat.Nv12 or CameraPixelFormat.Nv21 => 2,
        CameraPixelFormat.I420 or CameraPixelFormat.Yv12 => 3,
        _ => 1,
    };
}
