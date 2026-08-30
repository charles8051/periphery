namespace Periphery.Camera.Tests.Fakes;

/// <summary>
/// Minimal ref-counted <see cref="ICameraFrame"/> for sink tests. Mirrors
/// the production lease semantics (atomic refcount, AddRef-after-zero
/// throws) so tests assert the same contract real frames enforce.
/// </summary>
internal sealed class FakeFrame : ICameraFrame
{
    private readonly CameraPlane[] _planes;
    private int _refCount = 1;

    /// <summary>True when the refcount has been driven to zero (i.e. all references disposed).</summary>
    public bool Disposed => Volatile.Read(ref _refCount) <= 0;

    /// <summary>Current refcount. Exposed for tests asserting AddRef/Dispose balance.</summary>
    public int RefCount => Volatile.Read(ref _refCount);

    public FakeFrame(
        byte[] data,
        int width = 1,
        int height = 1,
        CameraPixelFormat pixelFormat = CameraPixelFormat.Mjpeg,
        TimeSpan? timestamp = null)
    {
        ContiguousBuffer = data;
        Width = width;
        Height = height;
        PixelFormat = pixelFormat;
        Timestamp = timestamp ?? TimeSpan.Zero;
        _planes =
        [
            new CameraPlane(Buffer: data, Stride: data.Length, Width: width, Height: height),
        ];
    }

    public int Width { get; }
    public int Height { get; }
    public CameraPixelFormat PixelFormat { get; }
    public TimeSpan Timestamp { get; }
    public int PlaneCount => _planes.Length;
    public bool IsContiguous => true;
    public ReadOnlyMemory<byte> ContiguousBuffer { get; }
    public CameraPlane GetPlane(int index) => _planes[index];

    public ICameraFrame AddRef()
    {
        while (true)
        {
            int current = Volatile.Read(ref _refCount);
            if (current <= 0)
                throw new ObjectDisposedException(nameof(FakeFrame));
            if (Interlocked.CompareExchange(ref _refCount, current + 1, current) == current)
                return this;
        }
    }

    public void Dispose()
    {
        int newCount = Interlocked.Decrement(ref _refCount);
        if (newCount < 0)
        {
            // Idempotent under stress; tests that care about double-Dispose use RefCount.
            Interlocked.Increment(ref _refCount);
        }
    }
}
