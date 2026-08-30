// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Periphery.Camera.Internal;

namespace Periphery.Camera;

/// <summary>
/// A frame delivered from a pooled buffer. Ref-counted (ADR-0035 §8b):
/// disposing the last outstanding reference returns the backing buffer
/// to the pool. The library will never revoke, relocate, or mutate the
/// backing memory while any reference is live.
/// </summary>
/// <remarks>
/// <para>
/// Use <see cref="AddRef"/> to retain the frame for shared consumption
/// (e.g. fan-out across multiple sinks). Use <see cref="Copy"/> to
/// detach an independent <see cref="OwnedCameraFrame"/> whose lifetime
/// is independent of the pool entirely (different escape valve from
/// AddRef — pays bytes, gains pool independence).
/// </para>
/// </remarks>
public sealed class LeasedCameraFrame : ICameraFrame
{
    private readonly CameraFramePool _pool;
    private readonly byte[] _backingBuffer;
    private readonly CameraPlane[] _planes;
    private int _refCount;

    internal LeasedCameraFrame(
        ReadOnlyMemory<byte> contiguousBuffer,
        int width, int height,
        CameraPixelFormat pixelFormat,
        TimeSpan timestamp,
        CameraPlane[] planes,
        CameraFramePool pool)
    {
        ContiguousBuffer = contiguousBuffer;
        Width = width;
        Height = height;
        PixelFormat = pixelFormat;
        Timestamp = timestamp;
        _planes = planes;
        _pool = pool;
        _refCount = 1;

        System.Runtime.InteropServices.MemoryMarshal.TryGetArray(contiguousBuffer, out var segment);
        _backingBuffer = segment.Array!;
    }

    public int Width { get; }
    public int Height { get; }
    public CameraPixelFormat PixelFormat { get; }
    public TimeSpan Timestamp { get; }
    public int PlaneCount => _planes.Length;
    /// <inheritdoc />
    public bool IsContiguous => Internal.PlaneLayout.IsContiguous(PixelFormat, _planes);
    public ReadOnlyMemory<byte> ContiguousBuffer { get; }

    /// <summary>
    /// The current reference count. Exposed for diagnostics and observability
    /// (e.g. multicast examples surfacing pool-buffer-vs-reference counts);
    /// consumers should not branch on it for correctness — use
    /// <see cref="AddRef"/> and <see cref="Dispose"/> instead.
    /// </summary>
    public int RefCount => Volatile.Read(ref _refCount);

    public CameraPlane GetPlane(int index)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _refCount) <= 0, this);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _planes.Length);
        return _planes[index];
    }

    /// <inheritdoc />
    public ICameraFrame AddRef()
    {
        // CAS-loop. AddRef on a frame whose refcount is already 0 is a
        // use-after-release: the buffer is back in the pool and may have
        // been re-leased by another consumer. Reject loudly rather than
        // silently corrupting state by resurrecting the count from 0 → 1.
        while (true)
        {
            int current = Volatile.Read(ref _refCount);
            if (current <= 0)
                throw new ObjectDisposedException(
                    nameof(LeasedCameraFrame),
                    "Cannot AddRef a frame whose backing buffer has already been returned to the pool. " +
                    "Holding a frame reference past Dispose and then calling AddRef is a use-after-release bug.");
            if (Interlocked.CompareExchange(ref _refCount, current + 1, current) == current)
                return this;
        }
    }

    /// <summary>
    /// Creates an owned copy of this frame's data. The copy is independent of
    /// the pool — its lifetime is unbounded by buffer recycling. This is the
    /// "escape the pool entirely" path; <see cref="AddRef"/> is the
    /// "share the pool buffer with another consumer" path.
    /// </summary>
    public OwnedCameraFrame Copy()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _refCount) <= 0, this);
        var data = ContiguousBuffer.ToArray();
        var planes = new CameraPlane[_planes.Length];
        for (int i = 0; i < _planes.Length; i++)
        {
            var p = _planes[i];
            int offset = (int)(p.Buffer.Span.Length > 0
                ? System.Runtime.CompilerServices.Unsafe.ByteOffset(
                    ref _backingBuffer[0],
                    ref System.Runtime.InteropServices.MemoryMarshal.GetReference(p.Buffer.Span))
                : 0);
            planes[i] = new CameraPlane(
                data.AsMemory(offset, p.Buffer.Length),
                p.Stride, p.Width, p.Height);
        }
        return new OwnedCameraFrame(data, Width, Height, PixelFormat, Timestamp, planes);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        int newCount = Interlocked.Decrement(ref _refCount);
        if (newCount == 0)
        {
            _pool.Return(_backingBuffer);
            return;
        }
        if (newCount < 0)
        {
            // Restore the count so subsequent Disposes don't keep
            // double-decrementing into pool corruption territory.
            Interlocked.Increment(ref _refCount);
#if DEBUG
            throw new ObjectDisposedException(
                nameof(LeasedCameraFrame),
                "Frame disposed more times than its reference count permits. " +
                "Each AddRef requires exactly one balancing Dispose; double-Disposing the initial reference is a bug.");
#endif
            // RELEASE: silently no-op to preserve pool integrity. The bug
            // above will surface in DEBUG builds and CI.
        }
    }
}
