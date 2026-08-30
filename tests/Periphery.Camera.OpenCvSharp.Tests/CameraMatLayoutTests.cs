using OpenCvSharp;

namespace Periphery.Camera.OpenCvSharp.Tests;

/// <summary>
/// The mapping table, asserted row by row against hand-derived numbers.
/// </summary>
/// <remarks>
/// Every expectation below is written out, not computed. 640x480 was chosen so
/// each step is a number a reader can multiply in their head: 640 x 3 = 1920 for
/// a 24-bit format, 640 x 4 = 2560 for a 32-bit one, 640 x 2 = 1280 for a 16-bit
/// one, and 640 for the 8-bit and 4:2:0 luma rows.
/// </remarks>
public class CameraMatLayoutTests
{
    // ── The table ──────────────────────────────────────────────────────

    [Theory]
    // format, rows, cols, MatType, step, cvtColor code, BGR path
    [InlineData(CameraPixelFormat.Bgr24, 480, 640, "CV_8UC3", 1920,
        null, CameraMatBgrPath.AlreadyBgr)]
    [InlineData(CameraPixelFormat.Rgb24, 480, 640, "CV_8UC3", 1920,
        ColorConversionCodes.RGB2BGR, CameraMatBgrPath.CvtColor)]
    [InlineData(CameraPixelFormat.Bgra32, 480, 640, "CV_8UC4", 2560,
        ColorConversionCodes.BGRA2BGR, CameraMatBgrPath.CvtColor)]
    [InlineData(CameraPixelFormat.Rgba32, 480, 640, "CV_8UC4", 2560,
        ColorConversionCodes.RGBA2BGR, CameraMatBgrPath.CvtColor)]
    [InlineData(CameraPixelFormat.Argb32, 480, 640, "CV_8UC4", 2560,
        null, CameraMatBgrPath.ArgbShuffle)]
    [InlineData(CameraPixelFormat.Yuy2, 480, 640, "CV_8UC2", 1280,
        ColorConversionCodes.YUV2BGR_YUY2, CameraMatBgrPath.CvtColor)]
    [InlineData(CameraPixelFormat.Uyvy, 480, 640, "CV_8UC2", 1280,
        ColorConversionCodes.YUV2BGR_UYVY, CameraMatBgrPath.CvtColor)]
    [InlineData(CameraPixelFormat.Gray8, 480, 640, "CV_8UC1", 640,
        ColorConversionCodes.GRAY2BGR, CameraMatBgrPath.CvtColor)]
    [InlineData(CameraPixelFormat.Gray16, 480, 640, "CV_16UC1", 1280,
        null, CameraMatBgrPath.CallerDefined)]
    // 4:2:0: 480 luma rows + 240 rows of chroma stacked underneath = 720.
    [InlineData(CameraPixelFormat.Nv12, 720, 640, "CV_8UC1", 640,
        ColorConversionCodes.YUV2BGR_NV12, CameraMatBgrPath.CvtColor)]
    [InlineData(CameraPixelFormat.Nv21, 720, 640, "CV_8UC1", 640,
        ColorConversionCodes.YUV2BGR_NV21, CameraMatBgrPath.CvtColor)]
    [InlineData(CameraPixelFormat.I420, 720, 640, "CV_8UC1", 640,
        ColorConversionCodes.YUV2BGR_I420, CameraMatBgrPath.CvtColor)]
    [InlineData(CameraPixelFormat.Yv12, 720, 640, "CV_8UC1", 640,
        ColorConversionCodes.YUV2BGR_YV12, CameraMatBgrPath.CvtColor)]
    public void Describe_MatchesTheTable(
        CameraPixelFormat format,
        int expectedRows,
        int expectedCols,
        string expectedMatType,
        int expectedStep,
        ColorConversionCodes? expectedConversion,
        CameraMatBgrPath expectedPath)
    {
        var shape = CameraMatLayout.Describe(format, width: 640, height: 480);

        Assert.Equal(expectedRows, shape.Rows);
        Assert.Equal(expectedCols, shape.Cols);
        Assert.Equal(expectedMatType, shape.Type.ToString());
        Assert.Equal(expectedStep, shape.Step);
        Assert.Equal(expectedConversion, shape.BgrConversion);
        Assert.Equal(expectedPath, shape.BgrPath);
    }

    [Fact]
    public void Describe_DoesNotSwapRowsAndColumns()
    {
        // A square frame cannot catch a transposed shape and 640x480 is the size
        // the table above uses, so this asserts the other orientation once.
        var shape = CameraMatLayout.Describe(CameraPixelFormat.Bgr24, width: 176, height: 144);

        Assert.Equal(144, shape.Rows);
        Assert.Equal(176, shape.Cols);
        Assert.Equal(528, shape.Step);
    }

    // ── Totality over the enum ─────────────────────────────────────────

    [Fact]
    public void Describe_CoversEveryEnumMember()
    {
        // A format added to CameraPixelFormat without a row in Describe reaches
        // the default arm and throws with a message naming the file to edit.
        // This test is how that arrives as a red build rather than as a wrong
        // shape at run time.
        foreach (var format in Enum.GetValues<CameraPixelFormat>())
        {
            if (format is CameraPixelFormat.Mjpeg or CameraPixelFormat.Unknown)
            {
                Assert.Throws<NotSupportedException>(
                    () => CameraMatLayout.Describe(format, 640, 480));
                Assert.False(CameraMatLayout.HasMatShape(format));
                continue;
            }

            var shape = CameraMatLayout.Describe(format, 640, 480);
            Assert.True(shape.Rows > 0, $"{format} produced {shape.Rows} rows.");
            Assert.True(shape.Cols > 0, $"{format} produced {shape.Cols} columns.");
            Assert.True(CameraMatLayout.HasMatShape(format));
        }
    }

    [Fact]
    public void Describe_MjpegMessageNamesTheDecodingCall()
    {
        var ex = Assert.Throws<NotSupportedException>(
            () => CameraMatLayout.Describe(CameraPixelFormat.Mjpeg, 640, 480));

        Assert.Contains("ToBgr()", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(CameraPixelFormat.Mjpeg)]
    [InlineData(CameraPixelFormat.Unknown)]
    public void TryDescribe_IsFalseForTheFormatsWithNoShape(CameraPixelFormat format)
    {
        Assert.False(CameraMatLayout.TryDescribe(format, 640, 480, out var shape));
        Assert.Equal(default, shape);
    }

    [Fact]
    public void TryDescribe_IsTrueForAnUncompressedFormat()
    {
        Assert.True(CameraMatLayout.TryDescribe(CameraPixelFormat.Nv12, 640, 480, out var shape));
        Assert.Equal(720, shape.Rows);
    }

    // ── Anti-drift against CameraFrameLayout ───────────────────────────

    public static TheoryData<int, int> Sizes =>
        new() { { 2, 2 }, { 16, 16 }, { 176, 144 }, { 640, 480 }, { 848, 480 }, { 1920, 1080 } };

    [Theory]
    [MemberData(nameof(Sizes))]
    public void Describe_SpansExactlyTheFrame(int width, int height)
    {
        // The equation that binds this table to the core. CameraFrameLayout is an
        // independent oracle here: it is what the pool sizes its buffers from and
        // what the tight-row invariant is stated in terms of, and it knows nothing
        // about OpenCV. A Mat header that spans a different number of bytes than
        // the frame holds either reads past the buffer or leaves pixels behind.
        foreach (var format in Enum.GetValues<CameraPixelFormat>())
        {
            if (!CameraMatLayout.HasMatShape(format))
                continue;

            var shape = CameraMatLayout.Describe(format, width, height);

            Assert.Equal(
                CameraFrameLayout.FrameSize(format, width, height),
                shape.ByteLength);
            Assert.Equal(
                CameraFrameLayout.BytesPerRow(format, width),
                shape.Step);
        }
    }

    [Fact]
    public void Describe_StepIsWhatMatWouldComputeForItself()
    {
        // Step equals Cols * elemSize for every row, which is what Mat.AUTO_STEP
        // would derive. Stating it anyway is what makes the equation above
        // checkable; this asserts the two agree so the stated number can never
        // be a second, different truth.
        foreach (var format in Enum.GetValues<CameraPixelFormat>())
        {
            if (!CameraMatLayout.HasMatShape(format))
                continue;

            var shape = CameraMatLayout.Describe(format, 640, 480);
            long elemSize = shape.Type.Channels * BytesPerChannel(shape.Type.ToString());

            Assert.Equal(shape.Cols * elemSize, shape.Step);
        }

        // Spelled out rather than read off MatType.Depth, so the expectation
        // does not route through the same struct the assertion is about.
        static int BytesPerChannel(string matType) => matType switch
        {
            "CV_8UC1" or "CV_8UC2" or "CV_8UC3" or "CV_8UC4" => 1,
            "CV_16UC1" => 2,
            _ => throw new Xunit.Sdk.XunitException($"Unexpected MatType {matType} in the table."),
        };
    }

    [Fact]
    public void BgrConversion_IsPresentExactlyOnTheCvtColorPath()
    {
        foreach (var format in Enum.GetValues<CameraPixelFormat>())
        {
            if (!CameraMatLayout.HasMatShape(format))
                continue;

            var shape = CameraMatLayout.Describe(format, 640, 480);

            Assert.Equal(
                shape.BgrPath == CameraMatBgrPath.CvtColor,
                shape.BgrConversion.HasValue);
        }
    }

    // ── Sizes the shape refuses ────────────────────────────────────────

    [Theory]
    [InlineData(CameraPixelFormat.Nv12)]
    [InlineData(CameraPixelFormat.Nv21)]
    [InlineData(CameraPixelFormat.I420)]
    [InlineData(CameraPixelFormat.Yv12)]
    public void Describe_RefusesOddDimensionsFor420(CameraPixelFormat format)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CameraMatLayout.Describe(format, 641, 480));
        Assert.Throws<ArgumentOutOfRangeException>(() => CameraMatLayout.Describe(format, 640, 481));
    }

    [Theory]
    [InlineData(CameraPixelFormat.Yuy2)]
    [InlineData(CameraPixelFormat.Uyvy)]
    public void Describe_RefusesAnOddWidthFor422(CameraPixelFormat format)
    {
        // A macropixel is two pixels sharing one chroma sample, so an odd width
        // leaves a pixel with none. The row length is still describable, which
        // is what makes this worth refusing rather than passing through to an
        // OpenCV assertion inside cvtColor.
        Assert.Throws<ArgumentOutOfRangeException>(() => CameraMatLayout.Describe(format, 641, 480));

        // 4:2:2 subsamples horizontally only, so an odd height is fine.
        var shape = CameraMatLayout.Describe(format, 640, 481);
        Assert.Equal(481, shape.Rows);
        Assert.Equal(640, shape.Cols);
        Assert.Equal(1280, shape.Step);
    }

    [Fact]
    public void Describe_AllowsOddDimensionsForAnUnsubsampledFormat()
    {
        var shape = CameraMatLayout.Describe(CameraPixelFormat.Gray8, 641, 481);

        Assert.Equal(481, shape.Rows);
        Assert.Equal(641, shape.Cols);
        Assert.Equal(641, shape.Step);

        var bgr = CameraMatLayout.Describe(CameraPixelFormat.Bgr24, 641, 481);
        Assert.Equal(1923, bgr.Step);
    }

    [Theory]
    [InlineData(0, 480)]
    [InlineData(640, 0)]
    [InlineData(-1, 480)]
    [InlineData(640, -1)]
    public void Describe_RefusesNonPositiveDimensions(int width, int height)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => CameraMatLayout.Describe(CameraPixelFormat.Bgr24, width, height));
    }

    // ── The BGR capability predicate ───────────────────────────────────

    [Theory]
    [InlineData(CameraPixelFormat.Mjpeg, true)]
    [InlineData(CameraPixelFormat.Bgr24, true)]
    [InlineData(CameraPixelFormat.Nv12, true)]
    [InlineData(CameraPixelFormat.Argb32, true)]
    [InlineData(CameraPixelFormat.Gray16, false)]
    [InlineData(CameraPixelFormat.Unknown, false)]
    public void CanConvertToBgr_NamesTheTwoRefusals(CameraPixelFormat format, bool expected)
    {
        Assert.Equal(expected, CameraMatLayout.CanConvertToBgr(format));
    }

    [Fact]
    public void Rows_AreThreeDivisibleForEvery420Height()
    {
        // OpenCV's CvtHelper asserts height % 3 == 0 on a FROM_YUV source and
        // produces a (rows * 2 / 3) destination. An even frame height always
        // satisfies it, because height * 3 / 2 = 3 * (height / 2) — but the
        // property is what makes the (h*3/2) trick legal, so it is asserted
        // rather than reasoned about.
        for (int height = 2; height <= 1080; height += 2)
        {
            var shape = CameraMatLayout.Describe(CameraPixelFormat.Nv12, 640, height);
            Assert.Equal(0, shape.Rows % 3);
            Assert.Equal(height, shape.Rows * 2 / 3);
        }
    }
}
