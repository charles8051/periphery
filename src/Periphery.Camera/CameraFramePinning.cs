// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Camera;

/// <summary>
/// Turns a frame into a <see cref="CameraFramePin"/> — a fixed address plus a
/// stride, with the frame's reference held for as long as the address is live.
/// The bridge from <see cref="ReadOnlyMemory{T}"/> to every native imaging API
/// that wants a pointer.
/// </summary>
/// <remarks>
/// Extension methods rather than members on <see cref="ICameraFrame"/> so the
/// interface stays a description of a frame and a consumer implementing it does
/// not have to reimplement the pinning protocol. Both entry points take a
/// reference before they take an address; see
/// <see cref="CameraFramePin"/> for the ordering and for what may and may not be
/// done through the pointer.
/// </remarks>
public static class CameraFramePinning
{
    /// <summary>
    /// Pins the frame's whole buffer — <see cref="ICameraFrame.ContiguousBuffer"/>
    /// — reporting the frame's dimensions and plane 0's stride.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the call for a packed frame, and for a 4:2:0 frame a consumer
    /// wants as one allocation: under ADR-0081 D1 the planes tile the buffer
    /// with no gaps and no row padding, so an NV12 frame pinned this way is the
    /// <c>(height * 3 / 2) × width</c> single-channel surface that OpenCV's
    /// <c>COLOR_YUV2BGR_NV12</c> expects, at the luma stride this pin reports.
    /// </para>
    /// <para>
    /// That stacked shape is exact rather than approximate, because the pool
    /// refuses to deliver a 4:2:0 frame whose width or height is odd
    /// (<c>FrameCopy.RejectOddChromaGeometry</c>). Floored half-resolution
    /// chroma has no extent the plane layout and the frame size agree on at an
    /// odd dimension, and neither UVC nor either backend can negotiate such a
    /// mode, so it is rejected before a buffer is taken rather than rounded.
    /// <see cref="CameraFramePin.Length"/> is the authoritative extent in every
    /// case; the shape is how to read it.
    /// </para>
    /// <para>
    /// It is also the call for MJPEG: the pinned bytes are the compressed blob
    /// to hand a decoder, <see cref="CameraFramePin.Length"/> is its length, and
    /// <see cref="CameraFramePin.Stride"/> is 0 because there are no rows.
    /// </para>
    /// <para>
    /// A consumer that needs one plane at its own stride — I420 chroma is half
    /// the luma stride — wants <see cref="PinPlane"/> instead.
    /// </para>
    /// </remarks>
    /// <param name="frame">The frame to pin. A reference is taken on it and
    /// released when the pin is disposed.</param>
    /// <exception cref="ArgumentNullException"><paramref name="frame"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">
    /// The frame's reference count has already reached zero, so its buffer may
    /// already belong to a later frame. Pinning it would be a use-after-release.
    /// </exception>
    public static CameraFramePin Pin(this ICameraFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        // GetPlane(0) rather than CameraFrameLayout.BytesPerRow(format, width):
        // ADR-0081 D4 keeps Stride on the record precisely so call sites read
        // the stated number instead of re-deriving it, and D1 makes the two
        // equal for every uncompressed frame the pool delivers.
        int stride = StrideOf(frame.PixelFormat, frame.GetPlane(0).Stride);
        return CameraFramePin.Create(
            frame, frame.ContiguousBuffer, stride, frame.Width, frame.Height);
    }

    /// <summary>
    /// Pins one plane of the frame, reporting that plane's own stride and its
    /// sample extents.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Index 0 is valid on every frame, single-plane ones included</b>, where
    /// it pins the same bytes <see cref="Pin"/> does. The two entry points
    /// deliberately overlap rather than being disjoint:
    /// <see cref="ICameraFrame.GetPlane"/> already accepts 0 on a packed frame,
    /// so refusing it here would have the two accessors disagree about the same
    /// frame, and format-generic code looping <c>0 .. PlaneCount - 1</c> would
    /// have to special-case the count it is looping over. Refusing buys no
    /// safety: a wrong index is caught by the range check either way.
    /// </para>
    /// <para>
    /// The difference from <see cref="Pin"/> shows on a 4:2:0 frame, where
    /// <see cref="Pin"/> covers the whole buffer at the luma stride and this
    /// covers one plane at its own. Note that a chroma plane's
    /// <see cref="CameraFramePin.Width"/> counts samples, not bytes: an NV12
    /// chroma plane is <c>width / 2</c> samples wide at a <c>width</c>-byte
    /// stride, because each sample is an interleaved UV pair.
    /// </para>
    /// </remarks>
    /// <param name="frame">The frame to pin. A reference is taken on it and
    /// released when the pin is disposed.</param>
    /// <param name="index">Plane index, <c>0 .. PlaneCount - 1</c>. Y then UV
    /// for NV12 / NV21; Y then U then V for I420, Y then V then U for YV12.</param>
    /// <exception cref="ArgumentNullException"><paramref name="frame"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="index"/> is negative or not less than
    /// <see cref="ICameraFrame.PlaneCount"/>.
    /// </exception>
    /// <exception cref="ObjectDisposedException">
    /// The frame's reference count has already reached zero.
    /// </exception>
    public static CameraFramePin PinPlane(this ICameraFrame frame, int index)
    {
        ArgumentNullException.ThrowIfNull(frame);

        // GetPlane does the range check and the liveness check, so both failures
        // land before a reference is taken and neither can leak one.
        var plane = frame.GetPlane(index);
        return CameraFramePin.Create(
            frame,
            plane.Buffer,
            StrideOf(frame.PixelFormat, plane.Stride),
            plane.Width,
            plane.Height);
    }

    // MJPEG and Unknown are opaque runs with no rows (ADR-0081 D7). The plane
    // they report carries CameraFrameLayout.BytesPerRow as a neutral filler,
    // which is a number that looks like a stride and is not one; a pin that
    // relayed it would be handing a caller an invented row width for a
    // compressed blob. 0 instead — see CameraFramePin.Stride for why 0 and not
    // int?.
    private static int StrideOf(CameraPixelFormat format, int planeStride) =>
        format is CameraPixelFormat.Mjpeg or CameraPixelFormat.Unknown ? 0 : planeStride;
}
