// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Camera;

/// <summary>
/// A frame whose backing memory is owned by the consumer. Created via
/// <see cref="LeasedCameraFrame.Copy"/> or by the pool's overflow path.
/// Can be retained indefinitely.
/// </summary>
/// <remarks>
/// <para>
/// Implements the same ref-counted contract as
/// <see cref="LeasedCameraFrame"/> for interface uniformity (ADR-0035 §8b),
/// but the byte buffer is GC-managed rather than pool-managed:
/// <see cref="Dispose"/> at refcount zero is a no-op — the GC reclaims
/// the bytes once no managed roots remain. <see cref="AddRef"/> exists
/// so an owned frame is interchangeable with a leased one in code that
/// targets <see cref="ICameraFrame"/> generically.
/// </para>
/// </remarks>
public sealed class OwnedCameraFrame : ICameraFrame
{
    private readonly CameraPlane[] _planes;
    private int _refCount;

    internal OwnedCameraFrame(
        ReadOnlyMemory<byte> contiguousBuffer,
        int width, int height,
        CameraPixelFormat pixelFormat,
        TimeSpan timestamp,
        CameraPlane[] planes)
    {
        ContiguousBuffer = contiguousBuffer;
        Width = width;
        Height = height;
        PixelFormat = pixelFormat;
        Timestamp = timestamp;
        _planes = planes;
        _refCount = 1;
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
    /// The current reference count. Exposed for diagnostics; consumers should
    /// not branch on it for correctness — use <see cref="AddRef"/> and
    /// <see cref="Dispose"/> instead.
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
        while (true)
        {
            int current = Volatile.Read(ref _refCount);
            if (current <= 0)
                throw new ObjectDisposedException(
                    nameof(OwnedCameraFrame),
                    "Cannot AddRef a frame whose reference count has already reached zero.");
            if (Interlocked.CompareExchange(ref _refCount, current + 1, current) == current)
                return this;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        int newCount = Interlocked.Decrement(ref _refCount);
        if (newCount < 0)
        {
            Interlocked.Increment(ref _refCount);
#if DEBUG
            throw new ObjectDisposedException(
                nameof(OwnedCameraFrame),
                "Frame disposed more times than its reference count permits.");
#endif
            // RELEASE: silently no-op.
        }
        // No pool return; the byte[] is GC-managed.
    }
}
