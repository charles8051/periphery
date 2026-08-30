using Periphery.Camera.Tests.Fakes;

namespace Periphery.Camera.Tests;

[Collection("Camera")]
public sealed class CameraSessionBuilderTests
{
    private static CameraFormat F(int w, int h, CameraPixelFormat pf, int fps) =>
        new(w, h, pf, new Rational(fps), new Rational(fps), CameraTransport.Uncompressed);

    private static List<CameraFormat> SampleFormats() =>
    [
        F(640,  480,  CameraPixelFormat.Yuy2,  30),
        F(1280, 720,  CameraPixelFormat.Mjpeg, 30),
        F(1280, 720,  CameraPixelFormat.Mjpeg, 60),
        F(1280, 720,  CameraPixelFormat.Yuy2,  30),
        F(1920, 1080, CameraPixelFormat.Mjpeg, 30),
        F(3840, 2160, CameraPixelFormat.Mjpeg, 30),
    ];

    // ── Default selection ──────────────────────────────────────────────

    [Fact]
    public async Task OpenAsync_NoCriteria_PicksHighestArea()
    {
        TestHelpers.InstallTestBackendFactory(formats: SampleFormats());

        await using var session = await CameraSession.For(TestHelpers.CreateDeviceInfo()).OpenAsync();

        Assert.Equal(3840, session.Configuration.Format.Width);
        Assert.Equal(2160, session.Configuration.Format.Height);
        TestHelpers.InstallTestBackendFactory();
    }

    // ── PreferPixelFormat + MaxResolution ──────────────────────────────

    [Fact]
    public async Task OpenAsync_PreferMjpeg_MaxResolution_PicksHighestMjpegInBox()
    {
        TestHelpers.InstallTestBackendFactory(formats: SampleFormats());

        await using var session = await CameraSession.For(TestHelpers.CreateDeviceInfo())
            .PreferMjpeg()
            .MaxResolution(1280, 720)
            .OpenAsync();

        Assert.Equal(1280, session.Configuration.Format.Width);
        Assert.Equal(720, session.Configuration.Format.Height);
        Assert.Equal(CameraPixelFormat.Mjpeg, session.Configuration.Format.PixelFormat);
        // Of the two 1280×720 MJPEG entries, the 60fps one wins.
        Assert.Equal(60, session.Configuration.Format.MaxFrameRate.ToDouble());
        TestHelpers.InstallTestBackendFactory();
    }

    [Fact]
    public async Task OpenAsync_PreferYuy2_FallsBackToOtherFormatsWhenNeeded()
    {
        // No YUY2 above 1280×720, so PreferYuy2 should still produce a result —
        // the highest area within the box (1920×1080 MJPEG).
        var formats = new List<CameraFormat>
        {
            F(640, 480, CameraPixelFormat.Yuy2, 30),
            F(1920, 1080, CameraPixelFormat.Mjpeg, 30),
        };
        TestHelpers.InstallTestBackendFactory(formats: formats);

        await using var session = await CameraSession.For(TestHelpers.CreateDeviceInfo())
            .PreferYuy2()
            .OpenAsync();

        // Yuy2 candidate exists, so it wins despite lower resolution
        // (PreferPixelFormat + ThenByHighestArea ordering).
        Assert.Equal(CameraPixelFormat.Yuy2, session.Configuration.Format.PixelFormat);
        Assert.Equal(640, session.Configuration.Format.Width);
        TestHelpers.InstallTestBackendFactory();
    }

    // ── AllowOnlyPixelFormats (strict) ─────────────────────────────────

    [Fact]
    public async Task OpenAsync_AllowOnlyYuy2_RejectsMjpegEvenAtHigherResolution()
    {
        TestHelpers.InstallTestBackendFactory(formats: SampleFormats());

        await using var session = await CameraSession.For(TestHelpers.CreateDeviceInfo())
            .AllowOnlyPixelFormats(CameraPixelFormat.Yuy2)
            .OpenAsync();

        Assert.Equal(CameraPixelFormat.Yuy2, session.Configuration.Format.PixelFormat);
        Assert.Equal(1280, session.Configuration.Format.Width); // highest YUY2 in sample
        TestHelpers.InstallTestBackendFactory();
    }

    [Fact]
    public void AllowOnlyPixelFormats_Empty_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            CameraSession.For(TestHelpers.CreateDeviceInfo()).AllowOnlyPixelFormats());
    }

    // ── MinResolution / MinFrameRate ───────────────────────────────────

    [Fact]
    public async Task OpenAsync_MinResolution_RejectsSmallerFormats()
    {
        TestHelpers.InstallTestBackendFactory(formats: SampleFormats());

        await using var session = await CameraSession.For(TestHelpers.CreateDeviceInfo())
            .MinResolution(1920, 1080)
            .OpenAsync();

        Assert.True(session.Configuration.Format.Width >= 1920);
        Assert.True(session.Configuration.Format.Height >= 1080);
        TestHelpers.InstallTestBackendFactory();
    }

    [Fact]
    public async Task OpenAsync_MinFrameRate_RejectsSlowerFormats()
    {
        TestHelpers.InstallTestBackendFactory(formats: SampleFormats());

        await using var session = await CameraSession.For(TestHelpers.CreateDeviceInfo())
            .MinFrameRate(new Rational(60))
            .OpenAsync();

        Assert.True(session.Configuration.Format.MaxFrameRate >= new Rational(60));
        TestHelpers.InstallTestBackendFactory();
    }

    // ── UseFormat (escape hatch, ADR-0040 §4a) ─────────────────────────

    [Fact]
    public async Task OpenAsync_UseFormat_Sync_DelegatesToCaller()
    {
        TestHelpers.InstallTestBackendFactory(formats: SampleFormats());
        CameraSnapshot? captured = null;

        await using var session = await CameraSession.For(TestHelpers.CreateDeviceInfo())
            .UseFormat(snap =>
            {
                captured = snap;
                return snap.Formats.First(f => f.Width == 640);
            })
            .OpenAsync();

        Assert.NotNull(captured);
        Assert.Equal(SampleFormats().Count, captured!.Formats.Count);
        Assert.Equal(640, session.Configuration.Format.Width);
        TestHelpers.InstallTestBackendFactory();
    }

    [Fact]
    public async Task OpenAsync_UseFormat_Async_ReceivesCancellationToken()
    {
        TestHelpers.InstallTestBackendFactory(formats: SampleFormats());
        bool ctReceived = false;

        await using var session = await CameraSession.For(TestHelpers.CreateDeviceInfo())
            .UseFormat(async (snap, ct) =>
            {
                ctReceived = ct.CanBeCanceled || ct == CancellationToken.None;
                await Task.Yield();
                return snap.Formats.First(f => f.Width == 1280 && f.PixelFormat == CameraPixelFormat.Yuy2);
            })
            .OpenAsync(CancellationToken.None);

        Assert.True(ctReceived);
        Assert.Equal(1280, session.Configuration.Format.Width);
        Assert.Equal(CameraPixelFormat.Yuy2, session.Configuration.Format.PixelFormat);
        TestHelpers.InstallTestBackendFactory();
    }

    [Fact]
    public async Task OpenAsync_UseFormat_OverridesFluentCriteria()
    {
        // PreferMjpeg + MaxResolution would pick 1280×720 MJPEG; UseFormat
        // wins regardless and picks the 640×480 YUY2.
        TestHelpers.InstallTestBackendFactory(formats: SampleFormats());

        await using var session = await CameraSession.For(TestHelpers.CreateDeviceInfo())
            .PreferMjpeg()
            .MaxResolution(1280, 720)
            .UseFormat(snap => snap.Formats.First(f => f.Width == 640))
            .OpenAsync();

        Assert.Equal(640, session.Configuration.Format.Width);
        Assert.Equal(CameraPixelFormat.Yuy2, session.Configuration.Format.PixelFormat);
        TestHelpers.InstallTestBackendFactory();
    }

    // ── No match throws ────────────────────────────────────────────────

    [Fact]
    public async Task OpenAsync_NoMatchingFormat_ThrowsConfigurationException()
    {
        TestHelpers.InstallTestBackendFactory(formats: SampleFormats());

        var ex = await Assert.ThrowsAsync<CameraConfigurationException>(() =>
            CameraSession.For(TestHelpers.CreateDeviceInfo())
                .MinResolution(7680, 4320) // No 8K format in sample
                .OpenAsync());

        // Message should list the requested criteria and the available formats.
        Assert.Contains("min=7680x4320", ex.Message);
        Assert.Contains("Available formats:", ex.Message);
        Assert.Contains("3840x2160", ex.Message);
        TestHelpers.InstallTestBackendFactory();
    }

    // ── Configuration extras ───────────────────────────────────────────

    [Fact]
    public async Task OpenAsync_TargetFrameRate_FlowsIntoConfiguration()
    {
        TestHelpers.InstallTestBackendFactory(formats: SampleFormats());

        await using var session = await CameraSession.For(TestHelpers.CreateDeviceInfo())
            .MaxResolution(1280, 720)
            .TargetFrameRate(new Rational(15))
            .OpenAsync();

        Assert.Equal(new Rational(15), session.Configuration.TargetFrameRate);
        TestHelpers.InstallTestBackendFactory();
    }

    // ── Session options ────────────────────────────────────────────────

    [Fact]
    public async Task OpenAsync_WithSessionOptions_FullReplacement()
    {
        TestHelpers.InstallTestBackendFactory(formats: SampleFormats());
        var options = new CameraSessionOptions(
            BufferCount: 5,
            ExhaustionPolicy: BufferExhaustionPolicy.StallProducer);

        await using var session = await CameraSession.For(TestHelpers.CreateDeviceInfo())
            .WithSessionOptions(options)
            .OpenAsync();

        Assert.Equal(5, session.Options.BufferCount);
        Assert.Equal(BufferExhaustionPolicy.StallProducer, session.Options.ExhaustionPolicy);
        TestHelpers.InstallTestBackendFactory();
    }

    [Fact]
    public async Task OpenAsync_WithSessionOptions_RecordTransformer()
    {
        TestHelpers.InstallTestBackendFactory(formats: SampleFormats());

        await using var session = await CameraSession.For(TestHelpers.CreateDeviceInfo())
            .WithSessionOptions(o => o with { BufferCount = 7 })
            .OpenAsync();

        Assert.Equal(7, session.Options.BufferCount);
        TestHelpers.InstallTestBackendFactory();
    }

    // ── Argument validation ────────────────────────────────────────────

    [Fact]
    public void For_NullDevice_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => CameraSession.For(null!));
    }

    [Fact]
    public void MaxResolution_NonPositive_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CameraSession.For(TestHelpers.CreateDeviceInfo()).MaxResolution(0, 720));
    }
}
