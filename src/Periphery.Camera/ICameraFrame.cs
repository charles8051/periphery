// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Camera;

/// <summary>
/// Read-only surface shared by <see cref="LeasedCameraFrame"/> and
/// <see cref="OwnedCameraFrame"/>. Consumers that do not need to reason about
/// ownership can accept this interface.
/// </summary>
/// <remarks>
/// <para>
/// Frames are <b>ref-counted</b> (ADR-0035 §8b). A frame produced by the
/// pool starts at refcount = 1 — that initial reference belongs to the
/// consumer the frame is delivered to. Each <see cref="AddRef"/> call
/// adds one reference; each <see cref="System.IDisposable.Dispose"/> call
/// drops one. The backing buffer (or the GC root, for owned frames)
/// releases when the count reaches zero.
/// </para>
/// <para>
/// Consumers that hold a frame for one logical scope (the typical
/// <c>using (var frame = …) { … }</c> pattern) need not call
/// <see cref="AddRef"/> at all — the initial refcount of 1 is dropped
/// to 0 by the single <see cref="System.IDisposable.Dispose"/>, returning
/// the buffer to the pool. <see cref="AddRef"/> is the path for shared
/// retention (preview + record + inference fan-out, double-buffered
/// rendering surfaces, etc.).
/// </para>
/// <para>
/// Calling <see cref="AddRef"/> after the final <see cref="System.IDisposable.Dispose"/>
/// is a use-after-release bug and throws <see cref="System.ObjectDisposedException"/>.
/// </para>
/// <para>
/// <b>Substrate independence (ADR-0045).</b> <see cref="ICameraFrame"/>
/// once inherited its reference-counting contract from an external graph
/// substrate, so frames could flow through that runtime without a wrapper.
/// It no longer does. The interface declares the camera-frame shape
/// directly and carries no third-party contract types, so adapting a frame
/// to any particular pipeline runtime is the consumer's job and costs this
/// package no dependency.
/// </para>
/// </remarks>
public interface ICameraFrame : System.IDisposable
{
    /// <summary>Frame width in pixels.</summary>
    int Width { get; }

    /// <summary>Frame height in pixels.</summary>
    int Height { get; }

    /// <summary>Presentation timestamp from the producer's clock.</summary>
    System.TimeSpan Timestamp { get; }

    CameraPixelFormat PixelFormat { get; }
    int PlaneCount { get; }

    /// <summary>
    /// Whether <see cref="ContiguousBuffer"/> can be read as one run of bytes —
    /// false only when the frame has multiple planes or padding between rows
    /// (ADR-0081 D5). Always true for <see cref="CameraPixelFormat.Mjpeg"/>,
    /// which is one opaque compressed run with no rows to pad.
    /// </summary>
    /// <remarks>
    /// Every uncompressed frame Periphery delivers has tight rows (ADR-0081 D1),
    /// so in practice this is true exactly when <see cref="PlaneCount"/> is 1.
    /// </remarks>
    bool IsContiguous { get; }

    ReadOnlyMemory<byte> ContiguousBuffer { get; }
    CameraPlane GetPlane(int index);

    /// <summary>
    /// Atomically adds one reference to this frame and returns the same
    /// instance for fluent usage. Each <see cref="AddRef"/> requires a
    /// balancing <see cref="System.IDisposable.Dispose"/>; the buffer
    /// returns to the pool only when all references have disposed.
    /// </summary>
    /// <returns>This frame instance.</returns>
    /// <exception cref="System.ObjectDisposedException">
    /// The frame's reference count is already zero (its backing buffer
    /// has returned to the pool). Holding a stale reference past
    /// <see cref="System.IDisposable.Dispose"/> and calling
    /// <see cref="AddRef"/> on it is a use-after-release bug.
    /// </exception>
    ICameraFrame AddRef();
}
