// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using OpenCvSharp;

namespace Periphery.Camera.OpenCvSharp;

/// <summary>
/// Hands a captured frame to OpenCV. Three entry points, separated by who owns
/// the pixels: <see cref="AsMat"/> borrows them, <see cref="ToMat"/> copies
/// them, and <see cref="ToBgr"/> converts them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Prefer <see cref="AsMat"/>.</b> Wrapping costs nothing measurable and a
/// 1080p YUY2→BGR <c>cvtColor</c> is 0.126 ms, against 1.83 ms for the clone
/// <see cref="ToMat"/> has to make. Copying to be safe buys no throughput — the
/// bottleneck in a capture loop is the work in the loop body, not the buffer it
/// holds — and costs a 4 MB allocation per frame.
/// </para>
/// <para>
/// <b>Do not hold a scope through inference.</b> That is a statement about the
/// loop, not about <see cref="AsMat"/>. A consumer slower than the frame
/// interval parks the producer, because the session's delivery channel is
/// <c>BoundedChannelFullMode.Wait</c>; frames are then lost upstream in Media
/// Foundation or V4L2 where no Periphery counter can see them, and
/// <c>FramesDropped</c> stays at zero while the delivered rate halves. That
/// stall is uninstrumented (<see href="https://github.com/charles8051/periphery/issues/322">#322</see>).
/// Convert inside the scope, hand the result to a bounded queue, and do the
/// heavy work on the other side of it.
/// </para>
/// <para>
/// <b>MJPEG.</b> A compressed frame has no <c>Mat</c> shape, so it has no
/// zero-copy path and no meaningful raw copy either. <see cref="AsMat"/> and
/// <see cref="ToMat"/> both refuse it by name, and <see cref="ToBgr"/> decodes
/// it. That split is the reason <see cref="ToBgr"/> is worth having as a third
/// method: it is the one entry point total over every format a camera can
/// deliver.
/// </para>
/// </remarks>
public static class CameraFrameMatExtensions
{
    /// <summary>
    /// A <c>Mat</c> header over the frame's own pixels. No copy. Valid until the
    /// returned scope is disposed, and not one instruction longer.
    /// </summary>
    /// <remarks>
    /// The scope holds a reference on the frame, so the pool cannot recycle the
    /// buffer underneath the header — but only for as long as the scope lives.
    /// Copy the <c>Mat</c> out of the <c>using</c> and you are back to reading
    /// whatever the pool put there next.
    /// </remarks>
    /// <param name="frame">The frame to wrap. A reference is taken on it and
    /// released when the scope is disposed.</param>
    /// <exception cref="ArgumentNullException"><paramref name="frame"/> is null.</exception>
    /// <exception cref="NotSupportedException">
    /// The frame is <see cref="CameraPixelFormat.Mjpeg"/> or
    /// <see cref="CameraPixelFormat.Unknown"/> — see
    /// <see cref="CameraMatLayout.Describe"/> — or its rows are not tight, which
    /// no frame from the pool is (ADR-0081 D1).
    /// </exception>
    /// <exception cref="ObjectDisposedException">
    /// The frame's reference count has already reached zero.
    /// </exception>
    public static MatScope AsMat(this ICameraFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        // Pin before describing, for the reason CameraFramePin.Create pins after
        // AddRef: the reference is the operation that can refuse, so taking it
        // first is what makes a released frame report the release. Describing
        // first is cheaper and gets the answer wrong for a frame that is both
        // released and in a format with no Mat shape — the caller is told to
        // change their pixel format when the real fault is that they kept a
        // frame past its lease (Peanut Gallery turn 1).
        var pin = frame.Pin();
        try
        {
            var shape = CameraMatLayout.DescribeFrame(frame);
            CameraMatLayout.ValidateAgainst(in shape, pin, frame.PixelFormat);

            // Mat.FromPixelData, not new Mat(rows, cols, type, IntPtr, step).
            // That constructor carries [Obsolete] as of OpenCvSharp 4.13, and
            // the attribute's own message names the reason: "the introduction of
            // 'nint' made overload resolution confusing". CameraFramePin.Scan0
            // is an nint, so this call site is the one the deprecation is about.
            var mat = Mat.FromPixelData(shape.Rows, shape.Cols, shape.Type, pin.Scan0, shape.Step);
            return new MatScope(mat, pin);
        }
        catch
        {
            // The pin is live and owned by nobody if anything after it throws.
            pin.Dispose();
            throw;
        }
    }

    /// <summary>
    /// A <c>Mat</c> holding a copy of the frame's pixels, in the frame's own
    /// format. The caller owns it and it outlives the frame.
    /// </summary>
    /// <remarks>
    /// Same shape <see cref="AsMat"/> produces — an NV12 frame still comes back
    /// as a <c>(height * 3 / 2) × width</c> CV_8UC1 <c>Mat</c>, not as BGR. Use
    /// <see cref="ToBgr"/> when you want an image rather than the capture
    /// format, and note that it is the cheaper of the two for anything that is
    /// not already BGR: <c>Cv2.CvtColor</c> allocates its own destination, so
    /// converting never needs this copy first.
    /// </remarks>
    /// <param name="frame">The frame to copy from. Not retained.</param>
    /// <exception cref="ArgumentNullException"><paramref name="frame"/> is null.</exception>
    /// <exception cref="NotSupportedException">
    /// The frame is <see cref="CameraPixelFormat.Mjpeg"/> — a byte-for-byte copy
    /// of a compressed blob is a <c>1 × n</c> vector of JPEG, which is not what
    /// the name promises; call <see cref="ToBgr"/> — or
    /// <see cref="CameraPixelFormat.Unknown"/>.
    /// </exception>
    /// <exception cref="ObjectDisposedException">
    /// The frame's reference count has already reached zero.
    /// </exception>
    public static Mat ToMat(this ICameraFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        if (frame.PixelFormat == CameraPixelFormat.Mjpeg)
        {
            EnsureLive(frame);
            throw new NotSupportedException(
                "ToMat copies a frame's pixels in the frame's own format, and MJPEG's own "
                    + "format is compressed — the copy would be a 1 x n byte vector of JPEG, not "
                    + "an image. Call ToBgr(), which decodes it, or read frame.ContiguousBuffer "
                    + "if you want the encoded bytes.");
        }

        using var scope = frame.AsMat();
        return scope.Mat.Clone();
    }

    /// <summary>
    /// An owned CV_8UC3 BGR <c>Mat</c> — the shape the rest of OpenCV expects.
    /// Handles every format a Periphery backend can deliver, MJPEG included.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Cheaper than <c>ToMat().CvtColor(...)</c> for every format that needs a
    /// conversion, because <c>Cv2.CvtColor</c> allocates its own destination and
    /// reads the source in place. Only <see cref="CameraPixelFormat.Bgr24"/>
    /// pays for a clone here, and only because there is nothing to convert.
    /// </para>
    /// <para>
    /// <b><see cref="CameraPixelFormat.Gray16"/> is the one refusal.</b>
    /// Narrowing 16 bits to 8 needs a range, and the right range is the
    /// device's: a depth or IR sensor using the bottom of its range comes out
    /// black under a fixed <c>/257</c>, which is a plausible-looking wrong
    /// image rather than an error. Take the CV_16UC1 <c>Mat</c> from
    /// <see cref="AsMat"/> or <see cref="ToMat"/> and pick the mapping —
    /// <c>Cv2.Normalize</c> for autoscaling, <c>Cv2.ConvertScaleAbs</c> for a
    /// known one — then <c>Cv2.CvtColor(..., GRAY2BGR)</c>.
    /// </para>
    /// </remarks>
    /// <param name="frame">The frame to convert. Not retained.</param>
    /// <exception cref="ArgumentNullException"><paramref name="frame"/> is null.</exception>
    /// <exception cref="NotSupportedException">
    /// The frame is <see cref="CameraPixelFormat.Gray16"/> or
    /// <see cref="CameraPixelFormat.Unknown"/>.
    /// </exception>
    /// <exception cref="System.IO.InvalidDataException">
    /// An MJPEG frame's bytes are not a decodable image.
    /// </exception>
    /// <exception cref="ObjectDisposedException">
    /// The frame's reference count has already reached zero.
    /// </exception>
    public static Mat ToBgr(this ICameraFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        if (frame.PixelFormat == CameraPixelFormat.Mjpeg)
            return DecodeJpeg(frame);

        // The scope opens before the format is judged, so every refusal below
        // sits behind a live pin and a released frame reports the release.
        // Gray16's refusal pays for a Mat header it then discards; that is an
        // error path, and it buys one liveness rule for the whole method
        // (Peanut Gallery turn 1).
        using var scope = frame.AsMat();
        var src = scope.Mat;
        var shape = CameraMatLayout.DescribeFrame(frame);

        switch (shape.BgrPath)
        {
            case CameraMatBgrPath.CallerDefined:
                throw new NotSupportedException(
                    $"{frame.PixelFormat} to BGR needs a 16-to-8-bit range mapping, and the "
                        + "right range is the device's — a fixed /257 renders most depth and IR "
                        + "sensors black. Take the CV_16UC1 Mat from AsMat() or ToMat(), apply "
                        + "Cv2.Normalize or Cv2.ConvertScaleAbs, then "
                        + "Cv2.CvtColor(..., GRAY2BGR).");

            case CameraMatBgrPath.AlreadyBgr:
                // Clone, not the scope's Mat: the header dies with the scope and
                // the caller owns what comes back.
                return src.Clone();

            case CameraMatBgrPath.CvtColor:
            {
                var dst = new Mat();
                try
                {
                    Cv2.CvtColor(src, dst, shape.BgrConversion!.Value);
                    return dst;
                }
                catch
                {
                    dst.Dispose();
                    throw;
                }
            }

            case CameraMatBgrPath.ArgbShuffle:
            {
                // A,R,G,B in memory to B,G,R. The pairs are (fromChannel,
                // toChannel): destination blue takes source channel 3, green
                // takes 2, red takes 1, and source channel 0 — alpha — is
                // dropped by not appearing.
                var dst = new Mat(frame.Height, frame.Width, MatType.CV_8UC3);
                try
                {
                    Cv2.MixChannels([src], [dst], [3, 0, 2, 1, 1, 2]);
                    return dst;
                }
                catch
                {
                    dst.Dispose();
                    throw;
                }
            }

            default:
                throw new NotSupportedException(
                    $"{frame.PixelFormat} has no BGR conversion path. Add an arm to "
                        + "CameraFrameMatExtensions.ToBgr.");
        }
    }

    // ToMat's MJPEG refusal is the one path that never reaches a pin of its own,
    // so it takes one and gives it straight back. Without this, a frame that is
    // both already released and MJPEG reports the format and hides the lifetime
    // bug, sending the caller off to change their pixel format. Pin is the
    // library's liveness check — AddRef refuses a frame whose count has reached
    // zero, before an address is taken — and this is an error path, so the pin
    // and its immediate release cost nothing that matters (Peanut Gallery
    // turn 1).
    private static void EnsureLive(ICameraFrame frame) => frame.Pin().Dispose();

    private static Mat DecodeJpeg(ICameraFrame frame)
    {
        // Pinned for the decode, not because ImDecode needs an address — it takes
        // the span directly, with no byte[] copy — but because the pin is the one
        // call that refuses a frame whose reference count has already reached
        // zero, and it holds the reference for the read.
        using var pin = frame.Pin();

        var mat = Cv2.ImDecode(frame.ContiguousBuffer.Span, ImreadModes.Color);
        if (mat.Empty())
        {
            mat.Dispose();
            throw new System.IO.InvalidDataException(
                $"OpenCV could not decode the {pin.Length}-byte MJPEG frame. A camera that "
                    + "delivers a truncated or corrupt JPEG produces this; the frame's bytes are "
                    + "still readable through frame.ContiguousBuffer if you want to inspect them.");
        }

        return mat;
    }
}
