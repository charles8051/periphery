// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Camera.Avalonia;

/// <summary>
/// How <see cref="CameraPreview"/> turns one frame's bytes into pixels Avalonia
/// can draw.
/// </summary>
internal enum PreviewPixelPath
{
    /// <summary>
    /// Hand the compressed blob to Skia's JPEG decoder. The only path that
    /// allocates a bitmap per frame, and the only one that cannot write into a
    /// reused surface.
    /// </summary>
    DecodeJpeg,

    /// <summary>
    /// Row copy into a <c>Bgra8888</c> surface. The frame's bytes already are the
    /// surface's bytes.
    /// </summary>
    CopyBgra,

    /// <summary>Row copy into an <c>Rgba8888</c> surface.</summary>
    CopyRgba,

    /// <summary>Scalar YUY2 → BGRA, written straight into a <c>Bgra8888</c> surface.</summary>
    ConvertYuy2,

    /// <summary>Scalar NV12 → BGRA, written straight into a <c>Bgra8888</c> surface.</summary>
    ConvertNv12,
}

/// <summary>
/// Which <see cref="CameraPixelFormat"/>s <see cref="CameraPreview"/> can put on
/// screen, how each one gets there, and which to ask the camera for first.
/// </summary>
/// <remarks>
/// <para>
/// Pure and total (ADR-0052 grain, functional core): scalars in,
/// scalars out, no Avalonia types and no IO. The Avalonia half of the mapping —
/// which surface format a path writes into — is
/// <see cref="PreviewSurfaceKey.For"/>.
/// </para>
/// <para>
/// <b>Why these five and nothing else.</b> Skia creates a
/// <c>WriteableBitmap</c> natively for exactly <c>Rgb565</c>, <c>Bgra8888</c> and
/// <c>Rgba8888</c>; every other Avalonia pixel format goes through a shim that
/// transcodes the whole image on each <c>Lock()</c> dispose, and no YUV format
/// exists in Avalonia at all. Two camera formats land on the native set with no
/// per-pixel work, MJPEG needs a decode whatever we do, and YUY2 and NV12 are the
/// two raw formats a commodity USB webcam is likely to be the only thing it
/// offers. Everything else — <c>Gray8</c>, <c>Gray16</c>, <c>Bgr24</c>,
/// <c>Rgb24</c>, <c>Argb32</c>, <c>Uyvy</c>, <c>I420</c>, <c>Yv12</c>,
/// <c>Nv21</c> — is left to fail at <c>OpenAsync</c> with a message naming what
/// the camera offered (issue #318).
/// </para>
/// </remarks>
internal static class PreviewPixelFormats
{
    // Preference order, most preferred first. Rank is the index, so the order
    // stated here is the only place it is stated.
    //
    //   Bgra32, Rgba32  Skia's native surface formats. One row copy per frame and
    //                   no per-pixel arithmetic, which is the whole point of #318.
    //   Mjpeg           Skia's JPEG decoder is native and vectorised; the two
    //                   converters below are managed scalar loops. Preferring a
    //                   decode over a conversion is a measurement-free call, but
    //                   it is also the path that shipped and works today.
    //   Nv12            12 bits/px against YUY2's 16, so three quarters of the
    //                   bytes read for the same picture and the same arithmetic.
    //   Yuy2            The last resort, and the one that unblocks the YUYV-only
    //                   webcams that motivated the conversion work.
    private static readonly CameraPixelFormat[] Preference =
    [
        CameraPixelFormat.Bgra32,
        CameraPixelFormat.Rgba32,
        CameraPixelFormat.Mjpeg,
        CameraPixelFormat.Nv12,
        CameraPixelFormat.Yuy2,
    ];

    /// <summary>The formats the control can display, most preferred first.</summary>
    public static IReadOnlyList<CameraPixelFormat> Displayable { get; } = Preference;

    /// <summary>
    /// Position of <paramref name="format"/> in <see cref="Displayable"/> — lower
    /// is preferred — or <see cref="int.MaxValue"/> when the control cannot
    /// display it.
    /// </summary>
    public static int Rank(CameraPixelFormat format)
    {
        int index = Array.IndexOf(Preference, format);
        return index < 0 ? int.MaxValue : index;
    }

    /// <summary>
    /// Resolves the path for a frame or an advertised format, or returns
    /// <see langword="false"/> when the control cannot display it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Dimensions are part of the question, not a separate check. A converter
    /// reads YUY2 two pixels at a time and NV12 in 2×2 blocks, so an odd width
    /// (or an odd height for NV12) has no last macropixel to read and the frame
    /// is undisplayable however renderable its format is. UVC dimensions are
    /// always even, so this rejects nothing real — it keeps the converters total
    /// rather than leaving a partial one to throw from the capture loop.
    /// </para>
    /// </remarks>
    public static bool TryGetPath(
        CameraPixelFormat format, int width, int height, out PreviewPixelPath path)
    {
        path = default;
        if (width <= 0 || height <= 0)
            return false;

        switch (format)
        {
            case CameraPixelFormat.Mjpeg:
                path = PreviewPixelPath.DecodeJpeg;
                return true;
            case CameraPixelFormat.Bgra32:
                path = PreviewPixelPath.CopyBgra;
                return true;
            case CameraPixelFormat.Rgba32:
                path = PreviewPixelPath.CopyRgba;
                return true;
            case CameraPixelFormat.Yuy2 when width % 2 == 0:
                path = PreviewPixelPath.ConvertYuy2;
                return true;
            case CameraPixelFormat.Nv12 when width % 2 == 0 && height % 2 == 0:
                path = PreviewPixelPath.ConvertNv12;
                return true;
            default:
                return false;
        }
    }
}
