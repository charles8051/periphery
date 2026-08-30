namespace Periphery.Camera.Tests;

public sealed class CameraFormatSelectorsTests
{
    private static CameraFormat F(int w, int h, CameraPixelFormat pf, int fps) =>
        new(w, h, pf, new Rational(fps), new Rational(fps), CameraTransport.Uncompressed);

    private static readonly CameraFormat[] Sample =
    [
        F(640,  480,  CameraPixelFormat.Yuy2,  30),
        F(1280, 720,  CameraPixelFormat.Mjpeg, 30),
        F(1280, 720,  CameraPixelFormat.Mjpeg, 60),
        F(1280, 720,  CameraPixelFormat.Yuy2,  30),
        F(1920, 1080, CameraPixelFormat.Mjpeg, 30),
        F(3840, 2160, CameraPixelFormat.Mjpeg, 30),
    ];

    [Fact]
    public void WithPixelFormat_KeepsOnlyMatching()
    {
        var mjpeg = Sample.WithPixelFormat(CameraPixelFormat.Mjpeg).ToList();
        Assert.Equal(4, mjpeg.Count);
        Assert.All(mjpeg, f => Assert.Equal(CameraPixelFormat.Mjpeg, f.PixelFormat));
    }

    [Fact]
    public void WithAnyPixelFormat_KeepsAnyInSet()
    {
        var either = Sample
            .WithAnyPixelFormat(CameraPixelFormat.Mjpeg, CameraPixelFormat.Yuy2)
            .ToList();
        Assert.Equal(Sample.Length, either.Count);
    }

    [Fact]
    public void WithAnyPixelFormat_EmptySet_IsNoOp()
    {
        var same = Sample.WithAnyPixelFormat().ToList();
        Assert.Equal(Sample.Length, same.Count);
    }

    [Fact]
    public void WithinBox_FiltersInclusiveOnBothAxes()
    {
        var inBox = Sample.WithinBox(1280, 720).ToList();
        // 640x480, plus three 1280x720 entries (Mjpeg30, Mjpeg60, Yuy2 30).
        Assert.Equal(4, inBox.Count);
        Assert.All(inBox, f => Assert.True(f.Width <= 1280 && f.Height <= 720));
    }

    [Fact]
    public void WithinBox_RejectsZeroOrNegative()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Sample.WithinBox(0, 720).ToList());
        Assert.Throws<ArgumentOutOfRangeException>(() => Sample.WithinBox(1280, -1).ToList());
    }

    [Fact]
    public void AtLeastResolution_FiltersInclusiveOnBothAxes()
    {
        var hd = Sample.AtLeastResolution(1920, 1080).ToList();
        Assert.Equal(2, hd.Count);
        Assert.All(hd, f => Assert.True(f.Width >= 1920 && f.Height >= 1080));
    }

    [Fact]
    public void AtLeastFrameRate_FiltersByMaxFrameRate()
    {
        var hi = Sample.AtLeastFrameRate(new Rational(60)).ToList();
        var single = Assert.Single(hi);
        Assert.Equal(60, single.MaxFrameRate.ToDouble());
    }

    [Fact]
    public void ByHighestArea_OrdersByPixelArea()
    {
        var ordered = Sample.ByHighestArea().ToList();
        Assert.Equal(3840, ordered[0].Width);
        Assert.Equal(640, ordered[^1].Width);
    }

    [Fact]
    public void ByHighestFrameRate_OrdersByFps()
    {
        var ordered = Sample.ByHighestFrameRate().ToList();
        Assert.Equal(60, ordered[0].MaxFrameRate.ToDouble());
    }

    [Fact]
    public void ByHighestArea_ThenByHighestFrameRate_BreaksTiesByFps()
    {
        var ordered = Sample
            .WithPixelFormat(CameraPixelFormat.Mjpeg)
            .WithinBox(1280, 720)
            .ByHighestArea()
            .ThenByHighestFrameRate()
            .ToList();

        Assert.Equal(2, ordered.Count);
        Assert.Equal(60, ordered[0].MaxFrameRate.ToDouble());
        Assert.Equal(30, ordered[1].MaxFrameRate.ToDouble());
    }

    [Fact]
    public void GoldenChain_PicksHighestMjpegInBox()
    {
        var chosen = Sample
            .WithPixelFormat(CameraPixelFormat.Mjpeg)
            .WithinBox(1280, 720)
            .ByHighestArea()
            .ThenByHighestFrameRate()
            .FirstOrDefault();

        Assert.NotNull(chosen);
        Assert.Equal(1280, chosen!.Width);
        Assert.Equal(720, chosen.Height);
        Assert.Equal(CameraPixelFormat.Mjpeg, chosen.PixelFormat);
        Assert.Equal(60, chosen.MaxFrameRate.ToDouble());
    }

    [Fact]
    public void PreferPixelFormat_StablyPlacesMatchesFirst()
    {
        var ordered = Sample
            .WithinBox(1280, 720)
            .PreferPixelFormat(CameraPixelFormat.Yuy2)
            .ToList();

        // First two entries must be Yuy2; remainder Mjpeg.
        Assert.Equal(CameraPixelFormat.Yuy2, ordered[0].PixelFormat);
        Assert.Equal(CameraPixelFormat.Yuy2, ordered[1].PixelFormat);
        Assert.Equal(CameraPixelFormat.Mjpeg, ordered[2].PixelFormat);
        Assert.Equal(CameraPixelFormat.Mjpeg, ordered[3].PixelFormat);
    }

    [Fact]
    public void PreferPixelFormat_FallbackChain_PicksPreferredFirstThenAny()
    {
        // No Mjpeg present anywhere.
        var yuyOnly = new[]
        {
            F(640, 480, CameraPixelFormat.Yuy2, 30),
            F(1280, 720, CameraPixelFormat.Yuy2, 30),
        };

        var chosen = yuyOnly
            .PreferPixelFormat(CameraPixelFormat.Mjpeg)
            .ThenByHighestArea()
            .First();

        Assert.Equal(CameraPixelFormat.Yuy2, chosen.PixelFormat);
        Assert.Equal(1280, chosen.Width);
    }

    [Fact]
    public void NullSource_Throws()
    {
        IEnumerable<CameraFormat>? nil = null;
        Assert.Throws<ArgumentNullException>(() => nil!.WithPixelFormat(CameraPixelFormat.Mjpeg));
        Assert.Throws<ArgumentNullException>(() => nil!.WithinBox(1280, 720));
        Assert.Throws<ArgumentNullException>(() => nil!.ByHighestArea());
    }
}
