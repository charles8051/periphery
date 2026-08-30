using Avalonia.Platform;

namespace Periphery.Camera.Avalonia.Tests;

/// <summary>
/// #318: which camera formats the preview claims it can display, and what it
/// does with each.
/// </summary>
/// <remarks>
/// <para>
/// The exhaustive case below is the one that matters. Driving it off
/// <c>Enum.GetValues</c> means a new <see cref="CameraPixelFormat"/> member
/// fails this test until someone decides, in writing, whether the preview can
/// show it.
/// </para>
/// <para>
/// The theory signatures carry <see cref="CameraPixelFormat"/> and
/// <see cref="bool"/> rather than the internal <c>PreviewPixelPath</c>, because
/// an xunit test class has to be public and a public method cannot take an
/// internal parameter. The path mapping is asserted in method bodies instead.
/// </para>
/// </remarks>
public sealed class PreviewPixelFormatsTests
{
    private const int W = 640;
    private const int H = 480;

    /// <summary>Every <see cref="CameraPixelFormat"/> and whether the preview can display it.</summary>
    public static TheoryData<CameraPixelFormat, bool> EveryFormat() => new()
    {
        { CameraPixelFormat.Unknown, false },
        { CameraPixelFormat.Mjpeg, true },
        { CameraPixelFormat.Yuy2, true },
        { CameraPixelFormat.Uyvy, false },
        { CameraPixelFormat.Nv12, true },
        { CameraPixelFormat.I420, false },
        { CameraPixelFormat.Yv12, false },
        { CameraPixelFormat.Nv21, false },
        { CameraPixelFormat.Rgb24, false },
        { CameraPixelFormat.Bgr24, false },
        { CameraPixelFormat.Rgba32, true },
        { CameraPixelFormat.Bgra32, true },
        { CameraPixelFormat.Argb32, false },
        { CameraPixelFormat.Gray8, false },
        { CameraPixelFormat.Gray16, false },
    };

    [Theory]
    [MemberData(nameof(EveryFormat))]
    public void TryGetPath_AcceptsOrRejectsEveryFormat(CameraPixelFormat format, bool expected)
    {
        Assert.Equal(expected, PreviewPixelFormats.TryGetPath(format, W, H, out _));
    }

    [Fact]
    public void EveryFormat_CoversTheEnum()
    {
        // The theory data above is a hand-written table; this is what keeps it
        // honest when the enum grows.
        var covered = EveryFormat().Select(row => (CameraPixelFormat)row[0]!).ToHashSet();
        Assert.Equal(Enum.GetValues<CameraPixelFormat>().ToHashSet(), covered);
    }

    [Fact]
    public void TryGetPath_SendsEachDisplayableFormatDownItsOwnPath()
    {
        Assert.True(PreviewPixelFormats.TryGetPath(CameraPixelFormat.Mjpeg, W, H, out var mjpeg));
        Assert.Equal(PreviewPixelPath.DecodeJpeg, mjpeg);

        Assert.True(PreviewPixelFormats.TryGetPath(CameraPixelFormat.Bgra32, W, H, out var bgra));
        Assert.Equal(PreviewPixelPath.CopyBgra, bgra);

        Assert.True(PreviewPixelFormats.TryGetPath(CameraPixelFormat.Rgba32, W, H, out var rgba));
        Assert.Equal(PreviewPixelPath.CopyRgba, rgba);

        Assert.True(PreviewPixelFormats.TryGetPath(CameraPixelFormat.Yuy2, W, H, out var yuy2));
        Assert.Equal(PreviewPixelPath.ConvertYuy2, yuy2);

        Assert.True(PreviewPixelFormats.TryGetPath(CameraPixelFormat.Nv12, W, H, out var nv12));
        Assert.Equal(PreviewPixelPath.ConvertNv12, nv12);
    }

    [Fact]
    public void Displayable_IsExactlyTheFormatsThatMap()
    {
        foreach (var format in Enum.GetValues<CameraPixelFormat>())
        {
            bool displayable = PreviewPixelFormats.TryGetPath(format, W, H, out _);
            Assert.Equal(displayable, PreviewPixelFormats.Displayable.Contains(format));
            Assert.Equal(displayable, PreviewPixelFormats.Rank(format) != int.MaxValue);
        }
    }

    [Fact]
    public void Displayable_RanksNativeSurfaceFormatsAboveDecodeAboveConversion()
    {
        // The stated policy: a format Skia can take as-is beats a decode, and a
        // decode beats a managed per-pixel conversion.
        Assert.Equal<CameraPixelFormat[]>(
            [
                CameraPixelFormat.Bgra32,
                CameraPixelFormat.Rgba32,
                CameraPixelFormat.Mjpeg,
                CameraPixelFormat.Nv12,
                CameraPixelFormat.Yuy2,
            ],
            [.. PreviewPixelFormats.Displayable]);
    }

    [Theory]
    [InlineData(0, 480)]
    [InlineData(640, 0)]
    [InlineData(-2, 480)]
    public void TryGetPath_NonPositiveDimensions_AreNotDisplayable(int width, int height)
    {
        Assert.False(PreviewPixelFormats.TryGetPath(CameraPixelFormat.Bgra32, width, height, out _));
        Assert.False(PreviewPixelFormats.TryGetPath(CameraPixelFormat.Mjpeg, width, height, out _));
    }

    [Fact]
    public void TryGetPath_OddWidth_RejectsTheConvertersAndNotTheCopies()
    {
        Assert.False(PreviewPixelFormats.TryGetPath(CameraPixelFormat.Yuy2, 641, 480, out _));
        Assert.False(PreviewPixelFormats.TryGetPath(CameraPixelFormat.Nv12, 641, 480, out _));
        Assert.True(PreviewPixelFormats.TryGetPath(CameraPixelFormat.Bgra32, 641, 481, out _));
        Assert.True(PreviewPixelFormats.TryGetPath(CameraPixelFormat.Mjpeg, 641, 481, out _));
    }

    [Fact]
    public void TryGetPath_OddHeight_RejectsNv12Only()
    {
        // YUY2 subsamples horizontally only, so an odd height is fine for it.
        Assert.False(PreviewPixelFormats.TryGetPath(CameraPixelFormat.Nv12, 640, 481, out _));
        Assert.True(PreviewPixelFormats.TryGetPath(CameraPixelFormat.Yuy2, 640, 481, out _));
    }

    // ── The Avalonia half of the mapping ───────────────────────────────

    [Fact]
    public void SurfaceKey_EveryBgraPath_TargetsBgra8888()
    {
        foreach (var path in new[]
                 {
                     PreviewPixelPath.CopyBgra,
                     PreviewPixelPath.ConvertYuy2,
                     PreviewPixelPath.ConvertNv12,
                 })
        {
            var key = PreviewSurfaceKey.For(W, H, path);
            Assert.Equal(PixelFormats.Bgra8888, key.Format);
            Assert.Equal(W, key.Width);
            Assert.Equal(H, key.Height);
        }
    }

    [Fact]
    public void SurfaceKey_RgbaCopy_TargetsRgba8888()
    {
        Assert.Equal(
            PixelFormats.Rgba8888, PreviewSurfaceKey.For(W, H, PreviewPixelPath.CopyRgba).Format);
    }

    [Fact]
    public void SurfaceKey_AlphaIsOpaque()
    {
        // Media Foundation's RGB32 is BGRX with the fourth byte zero. Under
        // Premul or Unpremul that zero reads as fully transparent and the whole
        // preview disappears. This constant is the only thing standing between
        // the control and a blank window on an RGB32 camera.
        Assert.Equal(AlphaFormat.Opaque, PreviewSurfaceKey.Alpha);
    }

    [Fact]
    public void SurfaceKey_SeparatesGeometryAndFormat_AndNothingElse()
    {
        var key = PreviewSurfaceKey.For(W, H, PreviewPixelPath.CopyBgra);

        // Two paths that write BGRA into the same geometry share a surface.
        Assert.Equal(key, PreviewSurfaceKey.For(W, H, PreviewPixelPath.ConvertYuy2));
        Assert.NotEqual(key, PreviewSurfaceKey.For(W + 2, H, PreviewPixelPath.CopyBgra));
        Assert.NotEqual(key, PreviewSurfaceKey.For(W, H + 2, PreviewPixelPath.CopyBgra));
        Assert.NotEqual(key, PreviewSurfaceKey.For(W, H, PreviewPixelPath.CopyRgba));
    }
}
