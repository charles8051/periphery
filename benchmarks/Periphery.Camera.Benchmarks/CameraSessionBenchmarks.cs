using BenchmarkDotNet.Attributes;
using Periphery.Camera.Benchmarks.Backends;

namespace Periphery.Camera.Benchmarks;

/// <summary>
/// End-to-end pipeline cost: synthetic backend → producer thread → bounded
/// channel → consumer's <see cref="CameraSession.ReadFrameAsync"/>. The
/// session is opened once in <see cref="GlobalSetup"/> and capture is left
/// running across iterations; each benchmark invocation reads exactly one
/// frame, so the measurement isolates per-frame pipeline overhead from
/// open/close costs.
/// </summary>
[MemoryDiagnoser]
public class CameraSessionBenchmarks
{
    private CameraSession _session = null!;

    /// <summary>Common camera resolutions, measured at NV12 (1.5 bytes/pixel).</summary>
    [Params(720, 1080)]
    public int Height { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        int width = Height * 16 / 9;
        // Round to even (sub-sampled chroma needs it).
        width = (width / 2) * 2;

        var backend = new BenchmarkCameraBackend(width, Height, CameraPixelFormat.Nv12);
        CameraDevice.BackendFactory = _ => backend;

        var device = new DeviceInfo
        {
            Id = "benchmark://camera",
            Name = "Benchmark Camera",
            Category = DeviceCategory.Camera,
        };
        var format = (await CameraDevice.ReadSnapshotAsync(device)).Formats[0];
        _session = await CameraSession.OpenAsync(device, new CameraConfiguration(format));
        await _session.StartCaptureAsync();
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _session.StopCaptureAsync();
        await _session.DisposeAsync();
        CameraDevice.BackendFactory = null;
    }

    /// <summary>
    /// Single-frame read from the consumer side. Measures channel dequeue +
    /// metric increment + lease-handoff + caller-side dispose.
    /// </summary>
    [Benchmark]
    public async Task<int> ReadOneFrame()
    {
        using var frame = await _session.ReadFrameAsync();
        return frame.ContiguousBuffer.Length;
    }
}
