using OpenCvSharp;

namespace Periphery.Camera.OpenCvSharp.Interop.Tests;

/// <summary>
/// The MJPEG decision, asserted rather than left to be discovered at run time.
/// </summary>
/// <remarks>
/// <para>
/// MJPEG is the default 1080p30 mode on most UVC webcams, so this is not an
/// edge case a consumer can arrange to avoid. The decision, in three parts:
/// </para>
/// <list type="number">
/// <item><description><c>AsMat</c> throws. A compressed blob has no rows and no
/// element type, so there is no header to build and no zero-copy path to
/// offer.</description></item>
/// <item><description><c>ToMat</c> throws. It promises a copy of the frame's
/// pixels in the frame's own format, and a byte-for-byte copy of JPEG is a
/// <c>1 x n</c> vector nobody wants under that name.</description></item>
/// <item><description><c>ToBgr</c> decodes. This is what makes <c>ToBgr</c>
/// total over every format a camera can deliver, and it is the reason the
/// package has a third method rather than two.</description></item>
/// </list>
/// <para>
/// Both refusals name <c>ToBgr</c> in the message, so a caller who reaches for
/// the wrong one is told which one is right rather than told no.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public class MjpegMatTests
{
    private const int Width = 32;
    private const int Height = 16;

    // A flat left half at 64 and a flat right half at 192, in neutral grey.
    // Flat regions and constant chroma are where JPEG is most nearly lossless,
    // so the decoded values can be asserted as literals with a small tolerance
    // instead of by comparing against a second decode.
    private static byte[] EncodeTwoToneJpeg()
    {
        using var source = new Mat(Height, Width, MatType.CV_8UC3, Scalar.All(64));
        using (var right = new Mat(source, new Rect(Width / 2, 0, Width / 2, Height)))
            right.SetTo(Scalar.All(192));

        Cv2.ImEncode(".jpg", source, out byte[] encoded, new ImageEncodingParam(ImwriteFlags.JpegQuality, 100));
        return encoded;
    }

    [OpenCvFact]
    public async Task ToBgr_DecodesTheFrame()
    {
        byte[] jpeg = EncodeTwoToneJpeg();

        await MatAssert.WithFrameAsync(CameraPixelFormat.Mjpeg, Width, Height, jpeg, frame =>
        {
            using var bgr = frame.ToBgr();

            Assert.Equal(Height, bgr.Rows);
            Assert.Equal(Width, bgr.Cols);
            Assert.Equal(MatType.CV_8UC3, bgr.Type());

            // Sampled well inside each half; the tolerance covers JPEG's ringing
            // near the tone boundary without hiding a wrong decode.
            MatAssert.Bgr(bgr, 8, 4, 64, 64, 64, tolerance: 4);
            MatAssert.Bgr(bgr, 8, 27, 192, 192, 192, tolerance: 4);
        });
    }

    [OpenCvFact]
    public async Task AsMat_RefusesAndNamesToBgr()
    {
        byte[] jpeg = EncodeTwoToneJpeg();

        await MatAssert.WithFrameAsync(CameraPixelFormat.Mjpeg, Width, Height, jpeg, frame =>
        {
            var ex = Assert.Throws<NotSupportedException>(() => frame.AsMat());

            Assert.Contains("ToBgr()", ex.Message, StringComparison.Ordinal);
            Assert.Contains("Cv2.ImDecode", ex.Message, StringComparison.Ordinal);
        });
    }

    [OpenCvFact]
    public async Task ToMat_RefusesAndNamesToBgr()
    {
        byte[] jpeg = EncodeTwoToneJpeg();

        await MatAssert.WithFrameAsync(CameraPixelFormat.Mjpeg, Width, Height, jpeg, frame =>
        {
            var ex = Assert.Throws<NotSupportedException>(() => frame.ToMat());

            Assert.Contains("ToBgr()", ex.Message, StringComparison.Ordinal);
        });
    }

    [OpenCvFact]
    public async Task ToBgr_ReportsUndecodableBytesRatherThanReturningAnEmptyMat()
    {
        // Cv2.ImDecode signals failure with an empty Mat, not an exception. A
        // caller who did not check would go on to index a 0x0 image and get an
        // OpenCV assertion somewhere else entirely.
        byte[] garbage = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4, 5, 6, 7, 8];

        await MatAssert.WithFrameAsync(CameraPixelFormat.Mjpeg, Width, Height, garbage, frame =>
        {
            var ex = Assert.Throws<System.IO.InvalidDataException>(() => frame.ToBgr());

            Assert.Contains("12-byte", ex.Message, StringComparison.Ordinal);
        });
    }

    [OpenCvFact]
    public async Task Pin_ReportsTheBlobLengthAndNoStride()
    {
        // ADR-0081 D7. The decoder wants Length; Stride has nothing to say.
        byte[] jpeg = EncodeTwoToneJpeg();

        await MatAssert.WithFrameAsync(CameraPixelFormat.Mjpeg, Width, Height, jpeg, frame =>
        {
            using var pin = frame.Pin();

            Assert.Equal(0, pin.Stride);
            Assert.Equal(jpeg.Length, pin.Length);
            Assert.Equal(Width, pin.Width);
            Assert.Equal(Height, pin.Height);
        });
    }
}
