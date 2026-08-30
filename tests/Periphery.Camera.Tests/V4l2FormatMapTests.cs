using Periphery.Camera;
using Periphery.Camera.Linux;
using Xunit;

namespace Periphery.Camera.Tests;

/// <summary>
/// Pure-mapping tests for the V4L2 fourcc / control-ID tables. These run on
/// every platform — the map has no native dependencies.
/// </summary>
public class V4l2FormatMapTests
{
    [Theory]
    [InlineData('M', 'J', 'P', 'G', CameraPixelFormat.Mjpeg)]
    [InlineData('Y', 'U', 'Y', 'V', CameraPixelFormat.Yuy2)]
    [InlineData('U', 'Y', 'V', 'Y', CameraPixelFormat.Uyvy)]
    [InlineData('N', 'V', '1', '2', CameraPixelFormat.Nv12)]
    [InlineData('N', 'V', '2', '1', CameraPixelFormat.Nv21)]
    [InlineData('Y', 'U', '1', '2', CameraPixelFormat.I420)]
    [InlineData('Y', 'V', '1', '2', CameraPixelFormat.Yv12)]
    [InlineData('B', 'G', 'R', '3', CameraPixelFormat.Bgr24)]
    [InlineData('R', 'G', 'B', '3', CameraPixelFormat.Rgb24)]
    [InlineData('G', 'R', 'E', 'Y', CameraPixelFormat.Gray8)]
    [InlineData('Y', '1', '6', ' ', CameraPixelFormat.Gray16)]
    public void FourCc_MapsToNeutralFormat(char a, char b, char c, char d, CameraPixelFormat expected)
    {
        uint fourcc = V4l2FormatMap.FourCc(a, b, c, d);

        Assert.True(V4l2FormatMap.TryMapPixelFormat(fourcc, out var format));
        Assert.Equal(expected, format);
    }

    [Fact]
    public void FourCc_RoundTripsThroughNeutralFormat()
    {
        // Every mappable neutral format maps back to a fourcc that maps to it.
        foreach (CameraPixelFormat format in System.Enum.GetValues<CameraPixelFormat>())
        {
            if (!V4l2FormatMap.TryMapToFourCc(format, out uint fourcc))
                continue; // Unknown / Rgba32-family have no V4L2 mapping — fine.

            Assert.True(V4l2FormatMap.TryMapPixelFormat(fourcc, out var roundTripped));
            Assert.Equal(format, roundTripped);
        }
    }

    [Fact]
    public void UnknownFourCc_IsRejectedNotMisclassified()
    {
        // H264 compressed payloads have no neutral representation today.
        uint h264 = V4l2FormatMap.FourCc('H', '2', '6', '4');

        Assert.False(V4l2FormatMap.TryMapPixelFormat(h264, out var format));
        Assert.Equal(CameraPixelFormat.Unknown, format);
    }

    [Fact]
    public void JpegAlias_MapsToMjpeg()
    {
        uint jpeg = V4l2FormatMap.FourCc('J', 'P', 'E', 'G');

        Assert.True(V4l2FormatMap.TryMapPixelFormat(jpeg, out var format));
        Assert.Equal(CameraPixelFormat.Mjpeg, format);
    }

    [Fact]
    public void EveryEnumerableControlKind_HasAnId()
    {
        foreach (var kind in V4l2FormatMap.EnumerableControlKinds)
        {
            Assert.True(
                V4l2FormatMap.TryGetControlId(kind, out uint id, out _),
                $"{kind} is listed as enumerable but has no control-ID mapping.");
            Assert.NotEqual(0u, id);
        }
    }

    [Theory]
    [InlineData(CameraControlKind.Exposure)]
    [InlineData(CameraControlKind.Focus)]
    [InlineData(CameraControlKind.WhiteBalance)]
    [InlineData(CameraControlKind.Gain)]
    public void AutoCapableControls_CarryCompanionAutoId(CameraControlKind kind)
    {
        Assert.True(V4l2FormatMap.TryGetControlId(kind, out _, out uint autoId));
        Assert.NotEqual(0u, autoId);
    }

    [Fact]
    public void FourCcToString_RendersAscii()
    {
        Assert.Equal("MJPG", V4l2FormatMap.FourCcToString(V4l2FormatMap.FourCc('M', 'J', 'P', 'G')));
    }
}
