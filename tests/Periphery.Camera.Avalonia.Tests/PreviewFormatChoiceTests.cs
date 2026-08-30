namespace Periphery.Camera.Avalonia.Tests;

/// <summary>
/// #318: what the preview asks the camera for, replacing
/// <c>AllowOnlyPixelFormats(Mjpeg)</c>.
/// </summary>
public sealed class PreviewFormatChoiceTests
{
    private static CameraFormat Format(
        int width, int height, CameraPixelFormat pixelFormat, int fps = 30) =>
        new(width, height, pixelFormat, new Rational(fps), new Rational(fps),
            pixelFormat == CameraPixelFormat.Mjpeg
                ? CameraTransport.Compressed
                : CameraTransport.Uncompressed);

    [Fact]
    public void Select_NoDisplayableFormat_ReturnsNull()
    {
        // A camera that only offers UYVY and I420. Failing at OpenAsync is the
        // stated policy for anything outside the displayable set.
        CameraFormat[] formats =
        [
            Format(1280, 720, CameraPixelFormat.Uyvy),
            Format(640, 480, CameraPixelFormat.I420),
        ];

        Assert.Null(PreviewFormatChoice.Select(formats, 1280, 720));
    }

    [Fact]
    public void Select_EmptyList_ReturnsNull()
    {
        Assert.Null(PreviewFormatChoice.Select([], 1280, 720));
    }

    [Fact]
    public void Select_PrefersTheLargestFormatInsideTheBox()
    {
        CameraFormat[] formats =
        [
            Format(640, 480, CameraPixelFormat.Mjpeg),
            Format(1280, 720, CameraPixelFormat.Mjpeg),
            Format(1920, 1080, CameraPixelFormat.Mjpeg),
        ];

        var chosen = PreviewFormatChoice.Select(formats, 1280, 720);

        Assert.NotNull(chosen);
        Assert.Equal(1280, chosen.Width);
        Assert.Equal(720, chosen.Height);
    }

    [Fact]
    public void Select_RejectsFormatsWiderOrTallerThanTheBox()
    {
        // 1600x600 fits the height but not the width; area alone would pick it
        // over 1280x720.
        CameraFormat[] formats =
        [
            Format(1600, 600, CameraPixelFormat.Bgra32),
            Format(1280, 720, CameraPixelFormat.Mjpeg),
        ];

        var chosen = PreviewFormatChoice.Select(formats, 1280, 720);

        Assert.NotNull(chosen);
        Assert.Equal(CameraPixelFormat.Mjpeg, chosen.PixelFormat);
    }

    [Fact]
    public void Select_AtEqualSize_PrefersTheHigherFrameRate()
    {
        // The C270 shape: 720p YUY2 at 10 fps alongside 720p MJPEG at 30. Frame
        // rate outranks the format preference, so the slow raw mode loses even
        // though a copy is cheaper than a decode.
        CameraFormat[] formats =
        [
            Format(1280, 720, CameraPixelFormat.Yuy2, fps: 10),
            Format(1280, 720, CameraPixelFormat.Mjpeg, fps: 30),
        ];

        var chosen = PreviewFormatChoice.Select(formats, 1280, 720);

        Assert.NotNull(chosen);
        Assert.Equal(CameraPixelFormat.Mjpeg, chosen.PixelFormat);
    }

    [Fact]
    public void Select_AtEqualSizeAndRate_PrefersTheCheaperPath()
    {
        // This is where the ranking is worth something: same picture, same rate,
        // three ways to get it. BGRA32 is one row copy, MJPEG is a decode, NV12
        // is a per-pixel conversion.
        CameraFormat[] formats =
        [
            Format(1280, 720, CameraPixelFormat.Nv12),
            Format(1280, 720, CameraPixelFormat.Mjpeg),
            Format(1280, 720, CameraPixelFormat.Bgra32),
        ];

        var chosen = PreviewFormatChoice.Select(formats, 1280, 720);

        Assert.NotNull(chosen);
        Assert.Equal(CameraPixelFormat.Bgra32, chosen.PixelFormat);
    }

    [Fact]
    public void Select_MjpegOutranksBothConverters()
    {
        CameraFormat[] formats =
        [
            Format(640, 480, CameraPixelFormat.Yuy2),
            Format(640, 480, CameraPixelFormat.Nv12),
            Format(640, 480, CameraPixelFormat.Mjpeg),
        ];

        var chosen = PreviewFormatChoice.Select(formats, 1280, 720);

        Assert.NotNull(chosen);
        Assert.Equal(CameraPixelFormat.Mjpeg, chosen.PixelFormat);
    }

    [Fact]
    public void Select_Nv12OutranksYuy2()
    {
        CameraFormat[] formats =
        [
            Format(640, 480, CameraPixelFormat.Yuy2),
            Format(640, 480, CameraPixelFormat.Nv12),
        ];

        var chosen = PreviewFormatChoice.Select(formats, 1280, 720);

        Assert.NotNull(chosen);
        Assert.Equal(CameraPixelFormat.Nv12, chosen.PixelFormat);
    }

    [Fact]
    public void Select_YuyvOnlyCamera_Opens()
    {
        // The case the conversion work exists for: no MJPEG anywhere, and the
        // control used to refuse to open at all.
        CameraFormat[] formats =
        [
            Format(320, 240, CameraPixelFormat.Yuy2),
            Format(640, 480, CameraPixelFormat.Yuy2),
        ];

        var chosen = PreviewFormatChoice.Select(formats, 1280, 720);

        Assert.NotNull(chosen);
        Assert.Equal(CameraPixelFormat.Yuy2, chosen.PixelFormat);
        Assert.Equal(640, chosen.Width);
    }

    [Fact]
    public void Select_OddWidthYuy2_IsNotChosen()
    {
        // A converter has no last macropixel to read at an odd width, so the
        // format is not displayable however large it is.
        CameraFormat[] formats =
        [
            Format(641, 480, CameraPixelFormat.Yuy2),
            Format(320, 240, CameraPixelFormat.Mjpeg),
        ];

        var chosen = PreviewFormatChoice.Select(formats, 1280, 720);

        Assert.NotNull(chosen);
        Assert.Equal(CameraPixelFormat.Mjpeg, chosen.PixelFormat);
    }

    [Fact]
    public void Select_IsIndependentOfTheListsOrder()
    {
        CameraFormat[] formats =
        [
            Format(1280, 720, CameraPixelFormat.Mjpeg),
            Format(1280, 720, CameraPixelFormat.Bgra32),
            Format(640, 480, CameraPixelFormat.Yuy2),
            Format(1280, 720, CameraPixelFormat.Nv12),
        ];

        var forward = PreviewFormatChoice.Select(formats, 1280, 720);
        var reversed = PreviewFormatChoice.Select([.. formats.Reverse()], 1280, 720);

        Assert.Equal(forward, reversed);
    }

    [Fact]
    public void DescribeNoMatch_NamesTheBox_TheWantedSet_AndWhatTheCameraOffered()
    {
        CameraFormat[] formats = [Format(1920, 1080, CameraPixelFormat.Uyvy, fps: 25)];

        string message = PreviewFormatChoice.DescribeNoMatch(formats, 1280, 720);

        Assert.Contains("1280x720", message, StringComparison.Ordinal);
        Assert.Contains("Bgra32", message, StringComparison.Ordinal);
        Assert.Contains("Yuy2", message, StringComparison.Ordinal);
        Assert.Contains("1920x1080", message, StringComparison.Ordinal);
        Assert.Contains("Uyvy", message, StringComparison.Ordinal);
        Assert.Contains("25", message, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeNoMatch_CameraWithNoFormats_SaysSo()
    {
        string message = PreviewFormatChoice.DescribeNoMatch([], 1280, 720);

        Assert.Contains("advertised none", message, StringComparison.Ordinal);
    }
}
