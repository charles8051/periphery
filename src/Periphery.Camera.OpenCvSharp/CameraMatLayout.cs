// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using OpenCvSharp;

namespace Periphery.Camera.OpenCvSharp;

/// <summary>
/// The format → <c>cv::Mat</c> mapping table, as a pure total function. The
/// OpenCV-facing counterpart to <see cref="CameraFrameLayout"/>, and the piece
/// of this package worth reading first.
/// </summary>
/// <remarks>
/// <para>
/// Pure (ADR-0052 grain): same input → same output, no IO, no clock, no mutable
/// state, and — the property that matters here — <b>no native call</b>.
/// <c>MatType</c> is a managed struct and <c>ColorConversionCodes</c> a managed
/// enum, so describing a frame never loads <c>OpenCvSharpExtern</c>. The whole
/// table is therefore testable on a machine with no OpenCV native payload
/// installed, which is what keeps the mapping covered on every CI leg including
/// macOS, where no current first-party runtime package exists.
/// </para>
/// <para>
/// <b>Every arm is written out.</b> The <c>switch</c> below has one arm per
/// <see cref="CameraPixelFormat"/> member and no <c>_ =&gt;</c> catch-all, so a
/// format added to the enum fails this build instead of silently acquiring a
/// wrong default shape.
/// </para>
/// <para>
/// <b>Why no stride parameter.</b> An earlier design took the frame's stride and
/// returned whether a zero-copy header was representable at it, because a padded
/// I420 plane cannot be read as one <c>(h*3/2) × w</c> surface. ADR-0081 D1
/// removed the case: every uncompressed frame Periphery delivers has tight rows,
/// and the pool asserts it. The shape is a function of format and size alone,
/// and <see cref="CameraMatShape.Step"/> is the stride the frame is required to
/// have. <see cref="CameraFrameMatExtensions.AsMat"/> checks the frame against
/// it rather than adapting to it.
/// </para>
/// </remarks>
public static class CameraMatLayout
{
    /// <summary>
    /// The <c>Mat</c> shape for one uncompressed format at one size.
    /// </summary>
    /// <param name="format">The frame's pixel format.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="width"/> or <paramref name="height"/> is not positive;
    /// either is odd for a 4:2:0 format; or <paramref name="width"/> is odd for
    /// a 4:2:2 one. Subsampled chroma has no whole extent at an odd dimension
    /// along the subsampled axis, so the shape is undefined rather than merely
    /// awkward — the frame pool refuses to deliver a 4:2:0 frame at an odd
    /// dimension, and OpenCV's own <c>CvtHelper</c> asserts an even width for
    /// both families.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// <paramref name="format"/> is <see cref="CameraPixelFormat.Mjpeg"/> or
    /// <see cref="CameraPixelFormat.Unknown"/>. Neither has a matrix shape: a
    /// compressed blob has no rows (ADR-0081 D7), and an unrecognised format has
    /// no known pixel layout at all.
    /// </exception>
    public static CameraMatShape Describe(CameraPixelFormat format, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        int step = CameraFrameLayout.BytesPerRow(format, width);

        switch (format)
        {
            // ── Packed RGB / BGR ───────────────────────────────────────
            // Bgr24 is the destination format, so its "conversion" is the
            // absence of one. Named as a distinct path rather than folded into
            // a no-op cvtColor code, because there isn't one: BGR2BGR does not
            // exist.
            case CameraPixelFormat.Bgr24:
                return new(height, width, MatType.CV_8UC3, step, null, CameraMatBgrPath.AlreadyBgr);
            case CameraPixelFormat.Rgb24:
                return new(height, width, MatType.CV_8UC3, step,
                    ColorConversionCodes.RGB2BGR, CameraMatBgrPath.CvtColor);
            case CameraPixelFormat.Bgra32:
                return new(height, width, MatType.CV_8UC4, step,
                    ColorConversionCodes.BGRA2BGR, CameraMatBgrPath.CvtColor);
            case CameraPixelFormat.Rgba32:
                // One step, not RGBA2BGRA then BGRA2BGR: RGBA2BGR exists and
                // does both. Unreachable from either shipping backend today —
                // neither MfFormatMap nor V4l2FormatMap produces it — but a
                // consumer can synthesise the frame, and an arm here costs
                // nothing next to an arm that throws for a format the enum says
                // is legal.
                return new(height, width, MatType.CV_8UC4, step,
                    ColorConversionCodes.RGBA2BGR, CameraMatBgrPath.CvtColor);
            case CameraPixelFormat.Argb32:
                // OpenCV has no ARGB2* code at all — the *2BGR family covers
                // RGB, BGR, RGBA, BGRA and the YUV layouts, and stops there. So
                // this row is the one that needs MixChannels, and it is the only
                // asymmetry in the table.
                return new(height, width, MatType.CV_8UC4, step,
                    null, CameraMatBgrPath.ArgbShuffle);

            // ── Packed YUV ─────────────────────────────────────────────
            // Two bytes per pixel, read as two channels. cvtColor's YUY2 / UYVY
            // codes expect exactly that shape. Width must be even and height
            // need not be: 4:2:2 subsamples horizontally only.
            case CameraPixelFormat.Yuy2:
                return Yuv422(height, width, step, ColorConversionCodes.YUV2BGR_YUY2);
            case CameraPixelFormat.Uyvy:
                return Yuv422(height, width, step, ColorConversionCodes.YUV2BGR_UYVY);

            // ── Grayscale ──────────────────────────────────────────────
            case CameraPixelFormat.Gray8:
                return new(height, width, MatType.CV_8UC1, step,
                    ColorConversionCodes.GRAY2BGR, CameraMatBgrPath.CvtColor);
            case CameraPixelFormat.Gray16:
                // CV_16UC1 is the right header and AsMat / ToMat serve it
                // correctly. Only the BGR leg is refused: see
                // CameraMatBgrPath.CallerDefined.
                return new(height, width, MatType.CV_16UC1, step,
                    null, CameraMatBgrPath.CallerDefined);

            // ── Planar 4:2:0 ───────────────────────────────────────────
            // No MatType describes these; the shape is the trick. See
            // CameraMatShape's remarks for why it is exact under ADR-0081.
            case CameraPixelFormat.Nv12:
                return Yuv420(height, width, step, ColorConversionCodes.YUV2BGR_NV12);
            case CameraPixelFormat.Nv21:
                return Yuv420(height, width, step, ColorConversionCodes.YUV2BGR_NV21);
            case CameraPixelFormat.I420:
                return Yuv420(height, width, step, ColorConversionCodes.YUV2BGR_I420);
            case CameraPixelFormat.Yv12:
                return Yuv420(height, width, step, ColorConversionCodes.YUV2BGR_YV12);

            // ── No matrix shape ────────────────────────────────────────
            case CameraPixelFormat.Mjpeg:
                throw new NotSupportedException(
                    "MJPEG is compressed and has no Mat shape — there is nothing for a "
                        + "header to describe and no way to wrap it without a decode. Call "
                        + "ToBgr(), which decodes it, or Cv2.ImDecode(frame.ContiguousBuffer.Span) "
                        + "if you want to choose the ImreadModes yourself.");
            case CameraPixelFormat.Unknown:
                throw new NotSupportedException(
                    "Unknown has no pixel layout, so no Mat shape can be derived for it. A "
                        + "frame reporting it came from a backend that could not map the "
                        + "platform's fourcc; treat its bytes as opaque.");

            default:
                // Not a catch-all for a shape: an unrecognised enum value is a
                // format added without a row in this table, and the only honest
                // answer is to say so.
                throw new NotSupportedException(
                    $"{format} is not a known CameraPixelFormat and has no Mat shape. "
                        + "Add a row to CameraMatLayout.Describe.");
        }

        static CameraMatShape Yuv422(int height, int width, int step, ColorConversionCodes code)
        {
            // A YUY2 or UYVY macropixel is two image pixels sharing one U and one
            // V, so an odd width leaves a final pixel with no partner and no
            // chroma. The row is describable — BytesPerRow happily returns
            // width * 2 — and the image is not, which is the shape of every bug
            // this table exists to prevent. OpenCV agrees and asserts an even
            // width inside cvtColor; refusing here turns that into an
            // exception with a reason attached (Peanut Gallery turn 1).
            if ((width & 1) != 0)
                throw new ArgumentOutOfRangeException(
                    nameof(width),
                    $"A 4:2:2 frame needs an even width; got {width}. Each two-pixel macropixel "
                        + "shares one chroma sample, so an odd width has a pixel with none.");

            return new CameraMatShape(
                height, width, MatType.CV_8UC2, step, code, CameraMatBgrPath.CvtColor);
        }

        static CameraMatShape Yuv420(int height, int width, int step, ColorConversionCodes code)
        {
            // Rejected rather than floored. A 4:2:0 frame at an odd dimension has
            // no chroma extent the plane layout and the frame size agree on, the
            // pool refuses to deliver one (FrameCopy.RejectOddChromaGeometry),
            // and OpenCV asserts an even width inside cvtColor. Flooring here
            // would build a header that reads past the buffer.
            if ((width & 1) != 0 || (height & 1) != 0)
                throw new ArgumentOutOfRangeException(
                    nameof(width),
                    $"A 4:2:0 frame needs even dimensions; got {width}x{height}. Half-resolution "
                        + "chroma has no whole extent at an odd width or height.");

            return new CameraMatShape(
                height * 3 / 2, width, MatType.CV_8UC1, step, code, CameraMatBgrPath.CvtColor);
        }
    }

    /// <summary>
    /// <see cref="Describe"/> without the throw for a format that has no matrix
    /// shape. Returns <see langword="false"/> for
    /// <see cref="CameraPixelFormat.Mjpeg"/> and
    /// <see cref="CameraPixelFormat.Unknown"/>; still throws for a size that
    /// cannot carry the format, because that is a caller error rather than a
    /// property of the format.
    /// </summary>
    public static bool TryDescribe(
        CameraPixelFormat format, int width, int height, out CameraMatShape shape)
    {
        if (!HasMatShape(format))
        {
            shape = default;
            return false;
        }

        shape = Describe(format, width, height);
        return true;
    }

    /// <summary>
    /// Whether <see cref="Describe"/> yields a shape for this format — true for
    /// every uncompressed format, false for
    /// <see cref="CameraPixelFormat.Mjpeg"/> and
    /// <see cref="CameraPixelFormat.Unknown"/>.
    /// </summary>
    public static bool HasMatShape(CameraPixelFormat format) =>
        format is not (CameraPixelFormat.Mjpeg or CameraPixelFormat.Unknown);

    /// <summary>
    /// Whether <see cref="CameraFrameMatExtensions.ToBgr"/> can convert this
    /// format. False only for <see cref="CameraPixelFormat.Gray16"/>, whose
    /// 16-to-8-bit range mapping is the device's decision, and for
    /// <see cref="CameraPixelFormat.Unknown"/>.
    /// </summary>
    public static bool CanConvertToBgr(CameraPixelFormat format) => format switch
    {
        CameraPixelFormat.Mjpeg => true,
        CameraPixelFormat.Unknown => false,
        CameraPixelFormat.Gray16 => false,
        _ => true,
    };

    // Used by AsMat to turn an out-of-contract frame into an exception instead
    // of a skewed image. Not a mitigation for padding — ADR-0081 D1 removed that
    // case for every frame the pool delivers — but ICameraFrame is a public
    // interface anyone can implement, and a header built at the wrong step reads
    // progressively shifted rows with no fault. One comparison is cheap enough
    // to make the invariant checked at the boundary rather than assumed across
    // it.
    internal static void ValidateAgainst(
        in CameraMatShape shape, CameraFramePin pin, CameraPixelFormat format)
    {
        if (pin.Stride != shape.Step)
        {
            throw new NotSupportedException(
                $"A {format} frame {pin.Width}x{pin.Height} must have a {shape.Step}-byte plane-0 "
                    + $"stride and this one reports {pin.Stride}. Every uncompressed frame "
                    + "Periphery delivers has tight rows (ADR-0081 D1); a frame that does not is "
                    + "not from the pool, and wrapping it at the stated step would read skewed "
                    + "rows without faulting.");
        }

        if (pin.Length < shape.ByteLength)
        {
            throw new NotSupportedException(
                $"A {format} frame {pin.Width}x{pin.Height} needs {shape.ByteLength} bytes and "
                    + $"this one has {pin.Length}. A Mat header over it would read past the "
                    + "buffer.");
        }
    }

    // Deliberately not a public overload of Describe: a caller holding a frame
    // wants AsMat, and a caller holding numbers wants the pure form. This exists
    // so the two extension methods agree on how a frame is measured.
    internal static CameraMatShape DescribeFrame(ICameraFrame frame) =>
        Describe(frame.PixelFormat, frame.Width, frame.Height);
}
