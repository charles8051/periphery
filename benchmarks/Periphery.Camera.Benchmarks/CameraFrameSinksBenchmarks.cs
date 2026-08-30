using BenchmarkDotNet.Attributes;
using Periphery.Camera.Benchmarks.Backends;

namespace Periphery.Camera.Benchmarks;

/// <summary>
/// Cost of the byte-level frame sinks (ADR-0040 §2). Pipes the synthetic
/// backend through <see cref="CameraFrameSinks.WriteContiguousToAsync"/>
/// into <see cref="Stream.Null"/> so we measure pure pipeline+sink overhead
/// with no disk I/O contribution.
/// </summary>
[MemoryDiagnoser]
public class CameraFrameSinksBenchmarks
{
    private CameraSession _session = null!;
    private const int FrameCount = 30;

    [GlobalSetup]
    public async Task Setup()
    {
        var backend = new BenchmarkCameraBackend(1280, 720, CameraPixelFormat.Nv12);
        CameraDevice.BackendFactory = _ => backend;

        var device = new DeviceInfo
        {
            Id = "benchmark://camera",
            Name = "Benchmark Camera",
            Category = DeviceCategory.Camera,
        };
        var format = (await CameraDevice.ReadSnapshotAsync(device)).Formats[0];
        _session = await CameraSession.OpenAsync(device, new CameraConfiguration(format));
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _session.DisposeAsync();
        CameraDevice.BackendFactory = null;
    }

    /// <summary>
    /// Streams <see cref="FrameCount"/> frames into <see cref="Stream.Null"/>.
    /// Measures the per-frame cost of the pipeline + sink, dominated by the
    /// channel dequeue and the synthetic-buffer copy.
    /// </summary>
    [Benchmark]
    public async Task<int> WriteContiguousFramesToNullStream()
    {
        var captureCt = new CancellationTokenSource();
        var capture = _session.CaptureAsync(ct: captureCt.Token);
        var bounded = TakeAsync(capture, FrameCount, captureCt);
        return await bounded.WriteContiguousToAsync(Stream.Null);
    }

    private static async IAsyncEnumerable<LeasedCameraFrame> TakeAsync(
        IAsyncEnumerable<LeasedCameraFrame> source,
        int count,
        CancellationTokenSource captureCt)
    {
        int yielded = 0;
        await foreach (var frame in source)
        {
            yield return frame;
            if (++yielded >= count)
            {
                captureCt.Cancel();
                yield break;
            }
        }
    }
}
