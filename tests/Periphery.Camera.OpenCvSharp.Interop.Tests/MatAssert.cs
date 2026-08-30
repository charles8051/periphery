using OpenCvSharp;
using Periphery.Camera.OpenCvSharp.Tests;
using Periphery.Camera.Testing;

namespace Periphery.Camera.OpenCvSharp.Interop.Tests;

/// <summary>
/// Shared pieces for the interop suite: a frame whose bytes are written out
/// literally, and pixel assertions that name the pixel they failed on.
/// </summary>
internal static class MatAssert
{
    /// <summary>
    /// A frame factory returning exactly these bytes. Everything the interop
    /// tests assert is derived by hand from the array passed here, so the
    /// expectation never routes through the code under test.
    /// </summary>
    internal static Func<CameraFrameSpec, byte[]> Literal(params byte[] bytes) =>
        _ => (byte[])bytes.Clone();

    /// <summary>Runs <paramref name="body"/> over a frame carrying exactly
    /// <paramref name="bytes"/>.</summary>
    internal static Task WithFrameAsync(
        CameraPixelFormat format, int width, int height, byte[] bytes, Action<ICameraFrame> body) =>
        FrameCapture.WithOneFrameAsync(format, width, height, Literal(bytes), body);

    /// <summary>
    /// Asserts one BGR pixel. <paramref name="tolerance"/> is 0 for the formats
    /// whose conversion is a channel move and 2 for the YUV ones, whose
    /// expectations come from BT.601's fixed-point coefficients and can differ
    /// by a unit between OpenCV's scalar and SIMD paths.
    /// </summary>
    internal static void Bgr(Mat mat, int row, int col, int b, int g, int r, int tolerance = 0)
    {
        Assert.Equal(MatType.CV_8UC3, mat.Type());

        var px = mat.At<Vec3b>(row, col);
        Near(b, px.Item0, tolerance, $"blue at ({row},{col})");
        Near(g, px.Item1, tolerance, $"green at ({row},{col})");
        Near(r, px.Item2, tolerance, $"red at ({row},{col})");
    }

    private static void Near(int expected, int actual, int tolerance, string what)
    {
        if (Math.Abs(expected - actual) > tolerance)
            throw new Xunit.Sdk.XunitException(
                $"{what}: expected {expected} (+/-{tolerance}) but got {actual}.");
    }
}
