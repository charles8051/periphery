using BenchmarkDotNet.Attributes;

namespace Periphery.Camera.Benchmarks;

/// <summary>
/// Format-selector composition cost. The golden chain
/// (<c>WithPixelFormat → WithinBox → ByHighestArea → ThenByHighestFrameRate
/// → FirstOrDefault</c>) runs once per session open via the builder, so its
/// cost contributes to the open-time budget.
/// </summary>
[MemoryDiagnoser]
public class FormatSelectorBenchmarks
{
    private CameraFormat[] _formats = null!;

    [GlobalSetup]
    public void Setup()
    {
        // 30 formats: 5 resolutions × 3 pixel formats × 2 frame rates.
        // Roughly the size a real USB UVC camera advertises.
        _formats =
        [
            ..BuildSet(640, 480),
            ..BuildSet(1280, 720),
            ..BuildSet(1920, 1080),
            ..BuildSet(2560, 1440),
            ..BuildSet(3840, 2160),
        ];

        static IEnumerable<CameraFormat> BuildSet(int w, int h)
        {
            foreach (var pf in new[] { CameraPixelFormat.Mjpeg, CameraPixelFormat.Yuy2, CameraPixelFormat.Nv12 })
            foreach (var fps in new[] { 30, 60 })
            {
                yield return new CameraFormat(
                    w, h, pf,
                    new Rational(fps), new Rational(fps),
                    CameraTransport.Uncompressed);
            }
        }
    }

    [Benchmark]
    public CameraFormat? GoldenChain_PreferMjpeg_WithinBox()
    {
        return _formats
            .WithPixelFormat(CameraPixelFormat.Mjpeg)
            .WithinBox(1280, 720)
            .ByHighestArea()
            .ThenByHighestFrameRate()
            .FirstOrDefault();
    }

    [Benchmark]
    public CameraFormat? FallbackChain_PreferMjpegThenAny()
    {
        return _formats
            .WithinBox(1280, 720)
            .PreferPixelFormat(CameraPixelFormat.Mjpeg)
            .ThenByHighestArea()
            .ThenByHighestFrameRate()
            .FirstOrDefault();
    }
}
