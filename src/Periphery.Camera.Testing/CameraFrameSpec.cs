// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Periphery.Camera.Internal;

namespace Periphery.Camera.Testing;

/// <summary>
/// The geometry of the frame <see cref="InMemoryCameraBackend"/> is about to
/// synthesise, handed to an <see cref="InMemoryCameraBackend.FrameFactory"/> so
/// it can write known bytes at known offsets.
/// </summary>
/// <remarks>
/// A pure value (ADR-0052 grain). Every number on it is derived from
/// <see cref="CameraFrameLayout"/> and the same internal plane layout the real
/// backends and the frame pool use, so a pattern written against this spec lands
/// at the offsets production reads it back from. That agreement is by
/// construction, not by hand — the fake previously carried its own
/// bytes-per-pixel table and it drifted (#321).
/// </remarks>
/// <param name="PixelFormat">Format of the frame being generated — the
/// configured one, or <see cref="InMemoryCameraBackend.OverridePixelFormat"/>.</param>
/// <param name="Width">Image width in pixels.</param>
/// <param name="Height">Image height in pixels.</param>
/// <param name="Stride">Row stride of the luma/packed plane in bytes. Equals
/// <see cref="CameraFrameLayout.BytesPerRow"/> unless
/// <see cref="InMemoryCameraBackend.OverrideStride"/> forces padded rows.</param>
/// <param name="FrameIndex">1-based index of this frame within the backend's
/// lifetime — the value the default pattern fills the buffer with.</param>
public readonly record struct CameraFrameSpec(
    CameraPixelFormat PixelFormat,
    int Width,
    int Height,
    int Stride,
    int FrameIndex)
{
    /// <summary>Total buffer size in bytes, stride padding included. A factory
    /// must return exactly this many bytes (MJPEG excepted — see
    /// <see cref="InMemoryCameraBackend.FrameFactory"/>).</summary>
    public int FrameSize => CameraFrameLayout.FrameSize(PixelFormat, Width, Height, Stride);

    /// <summary>Number of planes in the generated frame: 1 for packed and
    /// compressed formats, 2 for NV12 / NV21, 3 for I420 / YV12.</summary>
    public int PlaneCount => CameraFrameLayout.PlaneCount(PixelFormat);

    /// <summary>
    /// Where each plane sits in the buffer. Single-plane formats get one
    /// descriptor covering the whole buffer, so a pattern can loop over planes
    /// without branching on format.
    /// </summary>
    public IReadOnlyList<CameraFramePlaneSpec> GetPlanes()
    {
        var described = PlaneLayout.DescribePlanes(PixelFormat, Width, Height, Stride);
        if (described is not null)
        {
            // A second pass at the natural stride supplies each plane's unpadded
            // row width. That number is not recoverable from a padded descriptor
            // alone: NV12 chroma shares the luma stride while I420 chroma halves
            // it, so the padding-to-row relationship differs per format. Asking
            // PlaneLayout twice keeps both answers in one place.
            var natural = PlaneLayout.DescribePlanes(
                PixelFormat, Width, Height, CameraFrameLayout.BytesPerRow(PixelFormat, Width))!;

            var planes = new CameraFramePlaneSpec[described.Count];
            for (int i = 0; i < described.Count; i++)
            {
                var p = described[i];
                planes[i] = new CameraFramePlaneSpec(
                    p.Offset, p.Length, p.Stride, natural[i].Stride, p.Width, p.Height);
            }
            return planes;
        }

        // MJPEG is a compressed blob with no rows: describe it as a single row
        // spanning the buffer rather than Height rows of Stride, which would run
        // past the end of the worst-case size estimate.
        if (PixelFormat == CameraPixelFormat.Mjpeg)
            return [new CameraFramePlaneSpec(0, FrameSize, FrameSize, FrameSize, FrameSize, 1)];

        return
        [
            new CameraFramePlaneSpec(
                0, FrameSize, Stride, CameraFrameLayout.BytesPerRow(PixelFormat, Width), Width, Height)
        ];
    }
}

/// <summary>
/// One plane's position within a generated frame buffer, as
/// <see cref="CameraFrameSpec.GetPlanes"/> reports it.
/// </summary>
/// <param name="Offset">Byte offset of the plane's first row.</param>
/// <param name="Length">Total bytes the plane occupies, padding included.</param>
/// <param name="Stride">Bytes from one row's start to the next.</param>
/// <param name="RowBytes">Meaningful bytes in each row. Equals
/// <paramref name="Stride"/> when the frame is not padded; the difference is the
/// per-row padding.</param>
/// <param name="Width">Plane width in samples — half the image width for the
/// chroma planes of a 4:2:0 format.</param>
/// <param name="Height">Plane height in rows.</param>
public readonly record struct CameraFramePlaneSpec(
    int Offset,
    int Length,
    int Stride,
    int RowBytes,
    int Width,
    int Height);
