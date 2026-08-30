using OpenCvSharp;
using Periphery.Camera.OpenCvSharp.Tests;
using Periphery.Camera.Testing;

namespace Periphery.Camera.OpenCvSharp.Interop.Tests;

/// <summary>
/// Every <see cref="CameraPixelFormat"/> through every entry point, once.
/// </summary>
/// <remarks>
/// The per-format suites assert the pixels. This one asserts the coverage: a
/// format added to the enum, or a table row that starts producing a wrong-shaped
/// result, fails here without anyone remembering to add a case. What it checks
/// is the contract each method states — the shape that comes back, or the
/// documented exception — for all fifteen members.
/// </remarks>
[Trait("Category", "Integration")]
public class FormatCoverageTests
{
    public static TheoryData<CameraPixelFormat> AllFormats
    {
        get
        {
            var data = new TheoryData<CameraPixelFormat>();
            foreach (var format in Enum.GetValues<CameraPixelFormat>())
                data.Add(format);
            return data;
        }
    }

    [OpenCvTheory]
    [MemberData(nameof(AllFormats))]
    public async Task EveryFormat_EitherConvertsToBgrOrRefusesAsDocumented(CameraPixelFormat format)
    {
        if (format == CameraPixelFormat.Unknown)
        {
            // Unknown never reaches a frame: the backends map an unrecognised
            // fourcc to it and then decline the format, so there is nothing to
            // capture. Its refusal is asserted in the layout suite.
            Assert.False(CameraMatLayout.CanConvertToBgr(format));
            return;
        }

        if (format == CameraPixelFormat.Mjpeg)
        {
            // Covered by MjpegMatTests, which needs real JPEG bytes rather than
            // the neutral pattern this test uses.
            Assert.True(CameraMatLayout.CanConvertToBgr(format));
            return;
        }

        await FrameCapture.WithOneFrameAsync(
            format, 8, 4, CameraFramePatterns.PlaneConstant(NeutralPlanes(format)), frame =>
            {
                if (!CameraMatLayout.CanConvertToBgr(format))
                {
                    Assert.Throws<NotSupportedException>(() => frame.ToBgr());
                    return;
                }

                using var bgr = frame.ToBgr();

                Assert.Equal(MatType.CV_8UC3, bgr.Type());
                Assert.Equal(4, bgr.Rows);
                Assert.Equal(8, bgr.Cols);
            });
    }

    [OpenCvTheory]
    [MemberData(nameof(AllFormats))]
    public async Task EveryUncompressedFormat_WrapsAndCopiesAtTheDescribedShape(
        CameraPixelFormat format)
    {
        if (!CameraMatLayout.HasMatShape(format))
            return;

        var shape = CameraMatLayout.Describe(format, 8, 4);

        await FrameCapture.WithOneFrameAsync(
            format, 8, 4, CameraFramePatterns.PlaneConstant(NeutralPlanes(format)), frame =>
            {
                using (var scope = frame.AsMat())
                {
                    Assert.Equal(shape.Rows, scope.Mat.Rows);
                    Assert.Equal(shape.Cols, scope.Mat.Cols);
                    Assert.Equal(shape.Type, scope.Mat.Type());
                    Assert.Equal(shape.Step, (int)scope.Mat.Step());

                    using var pin = frame.Pin();
                    Assert.Equal(pin.Scan0, scope.Mat.Data);
                }

                using var owned = frame.ToMat();

                Assert.Equal(shape.Rows, owned.Rows);
                Assert.Equal(shape.Cols, owned.Cols);
                Assert.Equal(shape.Type, owned.Type());
            });
    }

    // Mid-scale luma and neutral chroma, one value per plane. Neutral is 128 for
    // every plane of every format here, which keeps the pattern total over the
    // table without a per-format expectation — the pixel values are asserted in
    // the suites that care about them.
    private static byte[] NeutralPlanes(CameraPixelFormat format)
    {
        var values = new byte[CameraFrameLayout.PlaneCount(format)];
        Array.Fill(values, (byte)128);
        return values;
    }
}
