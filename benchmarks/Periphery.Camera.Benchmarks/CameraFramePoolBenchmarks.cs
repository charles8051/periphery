using BenchmarkDotNet.Attributes;
using Periphery.Camera.Internal;

namespace Periphery.Camera.Benchmarks;

/// <summary>
/// Microbenchmarks for the lease/return round-trip on a hot
/// <see cref="CameraFramePool"/>. Measures pure pool overhead — no channel,
/// no producer thread, no backend. Establishes the floor below which the
/// full session pipeline cannot go.
/// </summary>
[MemoryDiagnoser]
public class CameraFramePoolBenchmarks
{
    private CameraFramePool _pool = null!;
    private byte[] _data = null!;
    private RawCameraFrame _raw;

    /// <summary>Image height; width is derived as 16:9. NV12 throughout.</summary>
    [Params(720, 1080, 2160)]
    public int Height { get; set; }

    /// <summary>
    /// Whether the source arrives with padded luma rows, as Media Foundation
    /// delivers NV12 on the hardware measured for ADR-0081. False takes the
    /// pool's bulk-copy fast path; true takes the per-plane row loop that
    /// de-pads. One 64-byte row of padding, which is what MF's rule adds — the
    /// three widths here are 1280, 1920 and 3840, all already 64-aligned and so
    /// all delivered tight by that rule, so the padding is imposed rather than
    /// derived in order to price the two paths at the same image size.
    /// </summary>
    /// <remarks>
    /// This case did not exist before, and its absence is why nothing caught the
    /// allocation the padding cost: the pool seeds at the tight size, so every
    /// padded frame used to miss its pooled buffer and allocate a fresh one,
    /// while a benchmark built only at <c>lumaStride: width</c> reported the
    /// zero steady-state allocation the design intends.
    /// </remarks>
    [Params(false, true)]
    public bool PaddedSource { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        int width = (Height * 16 / 9 / 2) * 2; // round to even for sub-sampled chroma
        int stride = PaddedSource ? width + 64 : width;
        int tightSize = width * Height * 3 / 2;

        _pool = new CameraFramePool();
        // Seeded tight, the way CameraSession seeds it — a padded source must
        // still land in a pooled buffer.
        _pool.Seed(tightSize, bufferCount: 1);
        _data = new byte[stride * Height * 3 / 2];

        _raw = new RawCameraFrame
        {
            Data = _data,
            Width = width,
            Height = Height,
            PixelFormat = CameraPixelFormat.Nv12,
            Timestamp = TimeSpan.Zero,
            PlaneCount = 2,
            Planes = PlaneLayout.DescribePlanes(CameraPixelFormat.Nv12, width, Height, stride),
        };
    }

    /// <summary>
    /// Round-trip lease + return on a primed pool. The hot path: producer
    /// hands off a frame, consumer disposes it, buffer recycles. Steady-state
    /// allocation should be zero (besides the LeasedCameraFrame wrapper itself).
    /// </summary>
    [Benchmark]
    public void TryDeliver_AndReturn()
    {
        var frame = _pool.TryDeliver(in _raw)!;
        frame.Dispose();
    }
}
