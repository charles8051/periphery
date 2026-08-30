using Periphery.Camera.Internal;

namespace Periphery.Camera.Benchmarks.Backends;

/// <summary>
/// Synthetic <see cref="ICameraBackend"/> for benchmarks. Returns a single
/// pre-allocated frame buffer on every <see cref="ReadRawFrameAsync"/> call,
/// so benchmark measurements isolate the library's pipeline overhead from
/// the cost of a real driver / interop boundary.
/// </summary>
internal sealed class BenchmarkCameraBackend : ICameraBackend
{
    private readonly byte[] _frameBuffer;
    private readonly CameraFormat _format;
    private readonly IReadOnlyList<RawPlaneDescriptor>? _planes;
    private long _frameIndex;

    public BenchmarkCameraBackend(int width, int height, CameraPixelFormat pixelFormat, int fps = 30)
    {
        _format = new CameraFormat(width, height, pixelFormat,
            new Rational(fps), new Rational(fps), CameraTransport.Uncompressed);

        int frameSize = CameraFrameLayout.FrameSize(pixelFormat, width, height);
        _frameBuffer = new byte[frameSize];

        // Pre-compute plane descriptors once; ReadRawFrameAsync just references them.
        _planes = PlaneLayout.DescribePlanes(pixelFormat, width, height,
            CameraFrameLayout.BytesPerRow(pixelFormat, width));
    }

    public string NativeEndpointId => "benchmark://camera";

    public Task OpenAsync(CancellationToken ct) => Task.CompletedTask;

    public Task<IReadOnlyList<CameraFormat>> GetFormatsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<CameraFormat>>(new[] { _format });

    public Task<IReadOnlyList<CameraControlInfo>> GetControlsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<CameraControlInfo>>(Array.Empty<CameraControlInfo>());

    public Task<CameraControlState?> GetControlAsync(CameraControlKind control, CancellationToken ct) =>
        Task.FromResult<CameraControlState?>(null);

    public Task SetControlAsync(CameraControlKind control, double value, CancellationToken ct) =>
        Task.CompletedTask;

    public Task ResetControlAsync(CameraControlKind control, CancellationToken ct) =>
        Task.CompletedTask;

    public Task ConfigureAsync(CameraConfiguration configuration, CancellationToken ct) =>
        Task.CompletedTask;

    public Task StartCaptureAsync(CancellationToken ct) => Task.CompletedTask;

    public Task<RawCameraFrame> ReadRawFrameAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var index = Interlocked.Increment(ref _frameIndex);
        return Task.FromResult(new RawCameraFrame
        {
            Data = _frameBuffer,
            Width = _format.Width,
            Height = _format.Height,
            PixelFormat = _format.PixelFormat,
            Timestamp = TimeSpan.FromMilliseconds(index * 33.333),
            PlaneCount = _planes?.Count ?? 1,
            Planes = _planes,
        });
    }

    public Task StopCaptureAsync() => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
