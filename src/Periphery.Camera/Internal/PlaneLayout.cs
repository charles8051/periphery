// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Camera.Internal;

/// <summary>
/// Computes <see cref="RawPlaneDescriptor"/> layouts for an uncompressed pixel
/// format given the row stride of the luma/packed plane. Backend code
/// determines the actual stride from the platform API
/// (<c>IMF2DBuffer.Lock2D</c>, V4L2 <c>VIDIOC_QUERYBUF</c> bytesperline, etc.)
/// and passes it in — chroma stride is then derived per format.
/// </summary>
/// <remarks>
/// <para>
/// Pure and total (ADR-0052 grain): same input → same output, no IO, no clock,
/// no mutable state.
/// </para>
/// <para>
/// I420 and YV12 produce byte-identical descriptor lists; the difference
/// is only in interpretation (plane[1] is U vs V respectively). Consumers
/// disambiguate via <see cref="ICameraFrame.PixelFormat"/>.
/// </para>
/// </remarks>
internal static class PlaneLayout
{
    /// <summary>
    /// Returns plane descriptors for an uncompressed pixel format, including
    /// the single-plane packed and grayscale ones. Returns
    /// <see langword="null"/> only for <see cref="CameraPixelFormat.Mjpeg"/> and
    /// <see cref="CameraPixelFormat.Unknown"/>, which are opaque runs of bytes
    /// with no rows to describe.
    /// </summary>
    /// <remarks>
    /// A packed format used to return <see langword="null"/> here and the pool
    /// recomputed its stride from the width, which is how a padded buffer came
    /// to be described by an unpadded stride (#320). Every uncompressed format
    /// now describes itself per plane, so the pool never infers a layout from
    /// one number (ADR-0081 D3).
    /// </remarks>
    /// <param name="format">The frame's pixel format.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="lumaStride">
    /// Row stride of the luma (Y) plane in bytes. May be larger than
    /// <paramref name="width"/> when the source buffer has padded rows.
    /// </param>
    internal static IReadOnlyList<RawPlaneDescriptor>? DescribePlanes(
        CameraPixelFormat format, int width, int height, int lumaStride)
    {
        switch (format)
        {
            case CameraPixelFormat.Nv12:
            case CameraPixelFormat.Nv21:
            {
                // 2 planes: Y, then interleaved UV (NV12) or VU (NV21).
                // Chroma plane has half the height, full luma stride
                // (each row is (width/2) chroma samples × 2 bytes = width bytes).
                int ySize = lumaStride * height;
                int chromaHeight = height / 2;
                int uvSize = lumaStride * chromaHeight;
                return
                [
                    new RawPlaneDescriptor
                    {
                        Offset = 0,
                        Length = ySize,
                        Stride = lumaStride,
                        Width = width,
                        Height = height,
                    },
                    new RawPlaneDescriptor
                    {
                        Offset = ySize,
                        Length = uvSize,
                        Stride = lumaStride,
                        Width = width / 2,
                        Height = chromaHeight,
                    },
                ];
            }

            case CameraPixelFormat.I420:
            case CameraPixelFormat.Yv12:
            {
                // 3 planes. I420: Y, U, V. YV12: Y, V, U. Byte layout is
                // identical; only consumer interpretation differs.
                int ySize = lumaStride * height;
                int chromaStride = lumaStride / 2;
                int chromaWidth = width / 2;
                int chromaHeight = height / 2;
                int chromaSize = chromaStride * chromaHeight;
                return
                [
                    new RawPlaneDescriptor
                    {
                        Offset = 0,
                        Length = ySize,
                        Stride = lumaStride,
                        Width = width,
                        Height = height,
                    },
                    new RawPlaneDescriptor
                    {
                        Offset = ySize,
                        Length = chromaSize,
                        Stride = chromaStride,
                        Width = chromaWidth,
                        Height = chromaHeight,
                    },
                    new RawPlaneDescriptor
                    {
                        Offset = ySize + chromaSize,
                        Length = chromaSize,
                        Stride = chromaStride,
                        Width = chromaWidth,
                        Height = chromaHeight,
                    },
                ];
            }

            case CameraPixelFormat.Mjpeg:
            case CameraPixelFormat.Unknown:
                // Compressed or unrecognised: one opaque run, no rows.
                return null;

            default:
                // Packed and grayscale: one plane spanning the buffer, at
                // whatever stride the producer measured.
                return
                [
                    new RawPlaneDescriptor
                    {
                        Offset = 0,
                        Length = lumaStride * height,
                        Stride = lumaStride,
                        Width = width,
                        Height = height,
                    },
                ];
        }
    }

    /// <summary>
    /// The layout an uncompressed frame has when its rows are tight — the shape
    /// the pool delivers under ADR-0081 D1, and the target every source layout is
    /// copied into. <see langword="null"/> for MJPEG / Unknown, per
    /// <see cref="DescribePlanes"/>.
    /// </summary>
    internal static IReadOnlyList<RawPlaneDescriptor>? DescribeTightPlanes(
        CameraPixelFormat format, int width, int height) =>
        DescribePlanes(format, width, height, CameraFrameLayout.BytesPerRow(format, width));

    /// <summary>
    /// Whether a delivered frame's bytes can be walked as one linear run —
    /// <see cref="ICameraFrame.IsContiguous"/>, shared by the leased and owned
    /// frames so the two cannot answer it differently.
    /// </summary>
    /// <remarks>
    /// ADR-0081 D5: false only when the bytes cannot be walked linearly, which
    /// is multiple planes or padding between rows. MJPEG is unconditionally
    /// true — a compressed frame is one opaque run with no rows to pad, and
    /// "can I hand <c>ContiguousBuffer</c> straight to a decoder" is the
    /// question a consumer asks it (D7). Under D1 no uncompressed frame the pool
    /// delivers is ever padded, so in practice this reduces to plane count and
    /// the answer is unchanged from before the invariant; the definition is what
    /// changed, and with it the name's meaning.
    /// </remarks>
    internal static bool IsContiguous(CameraPixelFormat format, CameraPlane[] planes)
    {
        if (format == CameraPixelFormat.Mjpeg)
            return true;
        if (planes.Length != 1)
            return false;

        var plane = planes[0];
        return plane.Stride * plane.Height == plane.Buffer.Length;
    }
}
