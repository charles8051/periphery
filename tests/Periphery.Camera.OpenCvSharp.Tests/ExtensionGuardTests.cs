using Periphery.Camera.Testing;

namespace Periphery.Camera.OpenCvSharp.Tests;

/// <summary>
/// The argument and lifetime checks on the three entry points, which all fire
/// before any OpenCV call and so belong with the tests that need no native
/// payload.
/// </summary>
public class ExtensionGuardTests
{
    [Fact]
    public void EveryEntryPoint_RejectsANullFrame()
    {
        ICameraFrame? frame = null;

        Assert.Throws<ArgumentNullException>(() => frame!.AsMat());
        Assert.Throws<ArgumentNullException>(() => frame!.ToMat());
        Assert.Throws<ArgumentNullException>(() => frame!.ToBgr());
    }

    [Theory]
    // A released frame in a format the method refuses is two bugs at once, and
    // the release is the one that has to surface: a NotSupportedException here
    // would send the caller to change their pixel format when the real problem
    // is that they kept a frame past its lease. AsMat pins before it can refuse
    // anything, so the two refusal paths that return early — ToMat on MJPEG and
    // ToBgr on Gray16 — have to check liveness explicitly (Peanut Gallery
    // turn 1).
    [InlineData(CameraPixelFormat.Mjpeg)]
    [InlineData(CameraPixelFormat.Gray16)]
    public async Task ARefusedFormat_StillReportsAReleasedFrameAsReleased(CameraPixelFormat format)
    {
        ICameraFrame? escaped = null;

        await FrameCapture.WithOneFrameAsync(
            format, 16, 8, CameraFramePatterns.FrameIndexConstant, frame => escaped = frame);

        // Live, these two throw NotSupportedException; the tests for that live
        // in the interop suite. Released, both report the release instead.
        Assert.Throws<ObjectDisposedException>(() => escaped!.ToMat());
        Assert.Throws<ObjectDisposedException>(() => escaped!.ToBgr());
        Assert.Throws<ObjectDisposedException>(() => escaped!.AsMat());
    }
}
