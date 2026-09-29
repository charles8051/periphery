using Periphery.Camera;
using Periphery.Cli.Commands;

namespace Periphery.Cli.Tests;

/// <summary>
/// The hardware-free parts of <c>periphery camera capture</c>: option parsing,
/// validation, and the warm-up skip that must return every frame it drops.
/// </summary>
public sealed class CameraCaptureCommandTests
{
    [Theory]
    [InlineData("mjpeg", CameraPixelFormat.Mjpeg)]
    [InlineData("MJPEG", CameraPixelFormat.Mjpeg)]
    [InlineData("Nv12", CameraPixelFormat.Nv12)]
    [InlineData("uyvy", CameraPixelFormat.Uyvy)]
    public void TryParsePixelFormat_AcceptsFormatNames(string text, CameraPixelFormat expected)
    {
        Assert.True(CameraCaptureCommand.TryParsePixelFormat(text, out var format));
        Assert.Equal(expected, format);
    }

    [Fact]
    public void TryParsePixelFormat_AnyMeansNoPreference()
    {
        Assert.True(CameraCaptureCommand.TryParsePixelFormat("ANY", out var format));
        Assert.Null(format);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("1")]
    [InlineData("jpeg")]
    [InlineData("")]
    public void TryParsePixelFormat_RejectsOtherText(string text)
    {
        Assert.False(CameraCaptureCommand.TryParsePixelFormat(text, out _));
    }

    [Theory]
    [InlineData("1920x1080", 1920, 1080)]
    [InlineData("640X480", 640, 480)]
    public void TryParseResolution_AcceptsWidthByHeight(string text, int width, int height)
    {
        Assert.True(CameraCaptureCommand.TryParseResolution(text, out int w, out int h));
        Assert.Equal((width, height), (w, h));
    }

    [Theory]
    [InlineData("1920")]
    [InlineData("0x1080")]
    [InlineData("-1x5")]
    [InlineData("+5x5")]
    [InlineData(" 1920x1080")]
    [InlineData("1920x1080x3")]
    [InlineData("x")]
    public void TryParseResolution_RejectsMalformedText(string text)
    {
        Assert.False(CameraCaptureCommand.TryParseResolution(text, out _, out _));
    }

    [Fact]
    public void Validate_DefaultsAreValid()
    {
        Assert.True(new CameraCaptureCommand.Settings().Validate().Successful);
    }

    [Theory]
    [InlineData(0, 0, "mjpeg", null)]
    [InlineData(1, -1, "mjpeg", null)]
    [InlineData(1, 0, "bogus", null)]
    [InlineData(1, 0, "mjpeg", "1920")]
    public void Validate_RejectsBadOptions(int frames, int skip, string format, string? maxResolution)
    {
        var settings = new CameraCaptureCommand.Settings
        {
            Frames = frames, Skip = skip, Format = format, MaxResolution = maxResolution,
        };

        Assert.False(settings.Validate().Successful);
    }

    [Fact]
    public async Task SkipDisposing_DisposesSkippedItems_AndYieldsTheRestUndisposed()
    {
        var items = Enumerable.Range(1, 5).Select(i => new Probe(i)).ToArray();

        var yielded = await CameraCaptureCommand.SkipDisposing(items.ToAsyncEnumerable(), 2).ToListAsync();

        Assert.Equal([3, 4, 5], yielded.Select(p => p.Value));
        Assert.Equal([true, true, false, false, false], items.Select(p => p.Disposed));
    }

    [Fact]
    public async Task SkipDisposing_ZeroPassesEverythingThrough()
    {
        var items = Enumerable.Range(1, 3).Select(i => new Probe(i)).ToArray();

        var yielded = await CameraCaptureCommand.SkipDisposing(items.ToAsyncEnumerable(), 0).ToListAsync();

        Assert.Equal(items, yielded);
        Assert.DoesNotContain(items, p => p.Disposed);
    }

    private sealed class Probe(int value) : IDisposable
    {
        public int Value { get; } = value;
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }
}
