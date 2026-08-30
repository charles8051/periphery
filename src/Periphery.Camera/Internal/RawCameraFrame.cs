// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Camera.Internal;

/// <summary>
/// Backend-produced frame data before it enters the library-owned pool.
/// The backing memory is valid only until the next <see cref="ICameraBackend.ReadRawFrameAsync"/>
/// call — the pool must copy the data promptly.
/// </summary>
internal readonly struct RawCameraFrame
{
    public required ReadOnlyMemory<byte> Data { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required CameraPixelFormat PixelFormat { get; init; }
    public required TimeSpan Timestamp { get; init; }
    public required int PlaneCount { get; init; }

    /// <summary>
    /// Where every plane sits in <see cref="Data"/>. Populated for every
    /// uncompressed frame (ADR-0081 D3), single-plane packed formats included;
    /// <see langword="null"/> only for MJPEG and Unknown, which have no rows.
    /// </summary>
    public IReadOnlyList<RawPlaneDescriptor>? Planes { get; init; }

    /// <summary>
    /// Whether each plane's rows are stored bottom-to-top — Media Foundation's
    /// negative-pitch surfaces (ADR-0081 D8). The descriptor's
    /// <see cref="RawPlaneDescriptor.Offset"/> still names the plane's first
    /// stored row, so image row <c>r</c> of a bottom-up plane sits at
    /// <c>Offset + (Height - 1 - r) * Stride</c>. The pool flips it in the same
    /// pass that removes the row padding; nothing downstream of the pool ever
    /// sees a bottom-up frame.
    /// </summary>
    public bool BottomUp { get; init; }
}

/// <summary>
/// Describes where a single plane sits within the contiguous <see cref="RawCameraFrame.Data"/>
/// buffer. Offset + Length identify the slice; Stride is the per-row byte stride.
/// </summary>
internal readonly struct RawPlaneDescriptor
{
    public required int Offset { get; init; }
    public required int Length { get; init; }
    public required int Stride { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
}
