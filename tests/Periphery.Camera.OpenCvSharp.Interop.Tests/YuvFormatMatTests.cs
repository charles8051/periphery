using OpenCvSharp;
using Periphery.Camera.OpenCvSharp.Tests;

namespace Periphery.Camera.OpenCvSharp.Interop.Tests;

/// <summary>
/// The packed-YUV and 4:2:0 rows of the table.
/// </summary>
/// <remarks>
/// <para>
/// <b>Where the expected numbers come from.</b> OpenCV converts all of these
/// with BT.601 limited range and fixed-point coefficients — CY = 1220542,
/// CUB = 2116026, CUG = -409993, CVG = -852492, CVR = 1673527, all at a 20-bit
/// shift with a <c>1 &lt;&lt; 19</c> rounding term. Working the three cases used
/// below by hand:
/// </para>
/// <list type="bullet">
/// <item><description>Y=16, U=V=128: the luma term is <c>(16-16) * CY = 0</c> and
/// both chroma terms are 0, so every channel is <c>524288 &gt;&gt; 20 = 0</c>.
/// BGR (0,0,0).</description></item>
/// <item><description>Y=235, U=V=128: <c>219 * CY = 267298698</c>, so every
/// channel is <c>267822986 &gt;&gt; 20 = 255</c>. BGR
/// (255,255,255).</description></item>
/// <item><description>Y=128, U=240, V=128: luma <c>112 * CY = 136700704</c>;
/// blue adds <c>CUB * 112</c> and saturates at 255, green adds
/// <c>CUG * 112</c> giving 87, red is unchanged at 130. BGR
/// (255,87,130).</description></item>
/// <item><description>Y=128, U=128, V=240: red adds <c>CVR * 112</c> and
/// saturates at 255, green adds <c>CVG * 112</c> giving 39, blue is 130. BGR
/// (130,39,255).</description></item>
/// </list>
/// <para>
/// The last two are the pair that tells the byte-identical formats apart. NV12
/// and NV21 carry the same 12 bytes and differ only in whether the chroma pair
/// is UV or VU; I420 and YV12 differ only in whether plane 1 is U or V. Feeding
/// both the same bytes must produce a blue frame for one and a red frame for the
/// other, and nothing else distinguishes them.
/// </para>
/// <para>
/// Tolerance is 2 everywhere: the arithmetic is integer, but OpenCV's scalar and
/// SIMD paths can land a unit apart and the point of these assertions is the
/// channel and plane wiring, not the last bit of the colorimetry.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public class YuvFormatMatTests
{
    private const int Tol = 2;

    [OpenCvFact]
    public async Task Yuy2_ReadsLumaFromTheEvenBytes()
    {
        // Y0 U Y1 V per pixel pair. Columns alternate black, white.
        byte[] bytes =
        [
            16, 128, 235, 128, 16, 128, 235, 128,
            16, 128, 235, 128, 16, 128, 235, 128,
        ];

        await MatAssert.WithFrameAsync(CameraPixelFormat.Yuy2, 4, 2, bytes, frame =>
        {
            using var bgr = frame.ToBgr();

            Assert.Equal(2, bgr.Rows);
            Assert.Equal(4, bgr.Cols);

            MatAssert.Bgr(bgr, 0, 0, 0, 0, 0, Tol);
            MatAssert.Bgr(bgr, 0, 1, 255, 255, 255, Tol);
            MatAssert.Bgr(bgr, 0, 2, 0, 0, 0, Tol);
            MatAssert.Bgr(bgr, 0, 3, 255, 255, 255, Tol);
            MatAssert.Bgr(bgr, 1, 0, 0, 0, 0, Tol);
            MatAssert.Bgr(bgr, 1, 3, 255, 255, 255, Tol);
        });
    }

    [OpenCvFact]
    public async Task Uyvy_ReadsLumaFromTheOddBytes()
    {
        // U Y0 V Y1 — the same picture as the YUY2 case with the pairs rotated
        // one byte. If the two rows of the table shared a conversion code this
        // frame would come back a flat mid-grey instead.
        byte[] bytes =
        [
            128, 16, 128, 235, 128, 16, 128, 235,
            128, 16, 128, 235, 128, 16, 128, 235,
        ];

        await MatAssert.WithFrameAsync(CameraPixelFormat.Uyvy, 4, 2, bytes, frame =>
        {
            using var bgr = frame.ToBgr();

            MatAssert.Bgr(bgr, 0, 0, 0, 0, 0, Tol);
            MatAssert.Bgr(bgr, 0, 1, 255, 255, 255, Tol);
            MatAssert.Bgr(bgr, 1, 2, 0, 0, 0, Tol);
            MatAssert.Bgr(bgr, 1, 3, 255, 255, 255, Tol);
        });
    }

    // 4x2 4:2:0: eight luma bytes then four chroma bytes. Luma alternates
    // black and white along each row; chroma is one 2x1 row of two samples.
    private static byte[] Yuv420(byte c0, byte c1, byte c2, byte c3) =>
    [
        16, 235, 16, 235,
        16, 235, 16, 235,
        c0, c1, c2, c3,
    ];

    [OpenCvTheory]
    [InlineData(CameraPixelFormat.Nv12)]
    [InlineData(CameraPixelFormat.Nv21)]
    [InlineData(CameraPixelFormat.I420)]
    [InlineData(CameraPixelFormat.Yv12)]
    public async Task Yuv420_StacksChromaUnderLumaAndKeepsTheColumns(CameraPixelFormat format)
    {
        // Neutral chroma, so all four formats agree and the assertion is purely
        // about the (h*3/2) x w shape: 12 bytes, three Mat rows of four, luma in
        // the top two.
        byte[] bytes = Yuv420(128, 128, 128, 128);

        await MatAssert.WithFrameAsync(format, 4, 2, bytes, frame =>
        {
            using (var scope = frame.AsMat())
            {
                Assert.Equal(3, scope.Mat.Rows);
                Assert.Equal(4, scope.Mat.Cols);
                Assert.Equal(MatType.CV_8UC1, scope.Mat.Type());
                Assert.Equal(4, (int)scope.Mat.Step());

                // The header sees the raw plane bytes, luma first.
                Assert.Equal(16, scope.Mat.At<byte>(0, 0));
                Assert.Equal(235, scope.Mat.At<byte>(0, 1));
                Assert.Equal(128, scope.Mat.At<byte>(2, 0));
            }

            using var bgr = frame.ToBgr();

            Assert.Equal(2, bgr.Rows);
            Assert.Equal(4, bgr.Cols);

            MatAssert.Bgr(bgr, 0, 0, 0, 0, 0, Tol);
            MatAssert.Bgr(bgr, 0, 1, 255, 255, 255, Tol);
            MatAssert.Bgr(bgr, 1, 2, 0, 0, 0, Tol);
            MatAssert.Bgr(bgr, 1, 3, 255, 255, 255, Tol);
        });
    }

    [OpenCvTheory]
    // Chroma bytes 240,128 twice. NV12 reads the pair as (U,V) so U=240 and the
    // frame goes blue; NV21 reads it as (V,U) so V=240 and it goes red.
    [InlineData(CameraPixelFormat.Nv12, 255, 87, 130)]
    [InlineData(CameraPixelFormat.Nv21, 130, 39, 255)]
    public async Task Nv12AndNv21_DifferOnlyInChromaOrder(
        CameraPixelFormat format, int b, int g, int r)
    {
        byte[] bytes = [128, 128, 128, 128, 128, 128, 128, 128, 240, 128, 240, 128];

        await MatAssert.WithFrameAsync(format, 4, 2, bytes, frame =>
        {
            using var bgr = frame.ToBgr();

            MatAssert.Bgr(bgr, 0, 0, b, g, r, Tol);
            MatAssert.Bgr(bgr, 1, 3, b, g, r, Tol);
        });
    }

    [OpenCvTheory]
    // Plane 1 is [240,240] and plane 2 is [128,128]. I420 calls plane 1 U, so
    // U=240 and the frame goes blue; YV12 calls it V, so V=240 and it goes red.
    [InlineData(CameraPixelFormat.I420, 255, 87, 130)]
    [InlineData(CameraPixelFormat.Yv12, 130, 39, 255)]
    public async Task I420AndYv12_DifferOnlyInPlaneOrder(
        CameraPixelFormat format, int b, int g, int r)
    {
        byte[] bytes = [128, 128, 128, 128, 128, 128, 128, 128, 240, 240, 128, 128];

        await MatAssert.WithFrameAsync(format, 4, 2, bytes, frame =>
        {
            using var bgr = frame.ToBgr();

            MatAssert.Bgr(bgr, 0, 0, b, g, r, Tol);
            MatAssert.Bgr(bgr, 1, 3, b, g, r, Tol);
        });
    }

    [OpenCvFact]
    public async Task Nv12_SurvivesAPaddedSourceStride()
    {
        // The backend pads the source luma rows to 64 bytes; the pool de-pads
        // before delivery, so what AsMat wraps is still the tight 3x4 surface.
        // This is the case that used to need a mitigation and no longer does.
        byte[] bytes =
        [
            16, 235, 16, 235,
            16, 235, 16, 235,
            128, 128, 128, 128,
        ];

        await FrameCapture.WithOneFrameAsync(
            CameraPixelFormat.Nv12, 4, 2,
            spec =>
            {
                // At a 64-byte stride the source frame is 64 * 2 + 64 * 1 = 192
                // bytes: two padded luma rows and one padded chroma row.
                var padded = new byte[spec.FrameSize];
                for (int row = 0; row < 2; row++)
                    Array.Copy(bytes, row * 4, padded, row * 64, 4);
                Array.Copy(bytes, 8, padded, 128, 4);
                return padded;
            },
            frame =>
            {
                using var scope = frame.AsMat();
                Assert.Equal(4, (int)scope.Mat.Step());

                using var bgr = frame.ToBgr();
                MatAssert.Bgr(bgr, 0, 0, 0, 0, 0, Tol);
                MatAssert.Bgr(bgr, 0, 1, 255, 255, 255, Tol);
                MatAssert.Bgr(bgr, 1, 3, 255, 255, 255, Tol);
            },
            overrideStride: 64);
    }
}
