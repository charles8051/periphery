using Periphery.Camera.Internal;

namespace Periphery.Camera.Tests;

public sealed class PlaneLayoutTests
{
    // ── Opaque formats: no rows to describe ───────────────────────────

    [Theory]
    [InlineData(CameraPixelFormat.Mjpeg)]
    [InlineData(CameraPixelFormat.Unknown)]
    public void DescribePlanes_OpaqueFormats_ReturnNull(CameraPixelFormat format)
    {
        var planes = PlaneLayout.DescribePlanes(format, 640, 480, lumaStride: 640);
        Assert.Null(planes);
    }

    // ── Packed and grayscale: one plane, carrying the measured stride ──

    // These returned null until ADR-0081 D3, which is how a padded buffer came
    // to be described by an unpadded stride (#320): the pool had nothing to
    // carry, so it recomputed. Byte counts below are hand-derived from the
    // format's own definition rather than asked of CameraFrameLayout.

    [Theory]
    [InlineData(CameraPixelFormat.Yuy2, 1280)]      // 4:2:2 packed, 2 bytes/px
    [InlineData(CameraPixelFormat.Uyvy, 1280)]
    [InlineData(CameraPixelFormat.Gray16, 1280)]    // 2 bytes/sample
    [InlineData(CameraPixelFormat.Bgra32, 2560)]    // 4 bytes/px
    [InlineData(CameraPixelFormat.Rgba32, 2560)]
    [InlineData(CameraPixelFormat.Argb32, 2560)]
    [InlineData(CameraPixelFormat.Rgb24, 1920)]     // 3 bytes/px
    [InlineData(CameraPixelFormat.Bgr24, 1920)]
    [InlineData(CameraPixelFormat.Gray8, 640)]      // 1 byte/px
    public void DescribePlanes_PackedFormats_DescribeOneWholeBufferPlane(
        CameraPixelFormat format, int tightStride)
    {
        var planes = PlaneLayout.DescribePlanes(format, 640, 480, tightStride);

        Assert.NotNull(planes);
        var plane = Assert.Single(planes);
        Assert.Equal(0, plane.Offset);
        Assert.Equal(tightStride * 480, plane.Length);
        Assert.Equal(tightStride, plane.Stride);
        Assert.Equal(640, plane.Width);
        Assert.Equal(480, plane.Height);
    }

    [Fact]
    public void DescribePlanes_PackedFormat_CarriesThePaddedStride()
    {
        // 640-pixel BGRA32 rows are 2560 bytes; a driver aligning to 4096 pads
        // each by 1536. The descriptor states the padded pitch, and the plane is
        // 4096 x 480 = 1 966 080 bytes rather than a tight 1 228 800.
        var planes = PlaneLayout.DescribePlanes(
            CameraPixelFormat.Bgra32, width: 640, height: 480, lumaStride: 4096);

        var plane = Assert.Single(planes!);
        Assert.Equal(4096, plane.Stride);
        Assert.Equal(1_966_080, plane.Length);
        Assert.Equal(640, plane.Width);
    }

    // ── DescribeTightPlanes: the layout the pool delivers ─────────────

    [Fact]
    public void DescribeTightPlanes_Nv12_UsesTheNaturalStrideRegardlessOfHardware()
    {
        var tight = PlaneLayout.DescribeTightPlanes(CameraPixelFormat.Nv12, 848, 480);

        Assert.NotNull(tight);
        Assert.Equal(2, tight.Count);
        // 848 is not 64-aligned, so Media Foundation reports 896 for this exact
        // mode on the PW513 (ADR-0081). The tight layout ignores that.
        Assert.Equal(848, tight[0].Stride);
        Assert.Equal(848 * 480, tight[0].Length);
        Assert.Equal(848 * 480, tight[1].Offset);
        Assert.Equal(848, tight[1].Stride);
        Assert.Equal(848 * 240, tight[1].Length);
    }

    [Theory]
    [InlineData(CameraPixelFormat.Mjpeg)]
    [InlineData(CameraPixelFormat.Unknown)]
    public void DescribeTightPlanes_OpaqueFormats_ReturnNull(CameraPixelFormat format)
        => Assert.Null(PlaneLayout.DescribeTightPlanes(format, 640, 480));

    // ── NV12 / NV21 (semi-planar 4:2:0) ──────────────────────────────

    [Fact]
    public void DescribePlanes_Nv12_NaturalStride_BuildsYAndUvPlanes()
    {
        var planes = PlaneLayout.DescribePlanes(
            CameraPixelFormat.Nv12, width: 1920, height: 1080, lumaStride: 1920);

        Assert.NotNull(planes);
        Assert.Equal(2, planes.Count);

        var y = planes[0];
        Assert.Equal(0, y.Offset);
        Assert.Equal(1920 * 1080, y.Length);
        Assert.Equal(1920, y.Stride);
        Assert.Equal(1920, y.Width);
        Assert.Equal(1080, y.Height);

        var uv = planes[1];
        Assert.Equal(1920 * 1080, uv.Offset);
        Assert.Equal(1920 * 540, uv.Length);
        Assert.Equal(1920, uv.Stride);
        // Chroma sample width is half luma; chroma height is half luma;
        // stride matches luma because U/V are interleaved 1:1 (2 bytes per sample).
        Assert.Equal(960, uv.Width);
        Assert.Equal(540, uv.Height);
    }

    [Fact]
    public void DescribePlanes_Nv21_HasIdenticalByteLayoutAsNv12()
    {
        // NV21 differs from NV12 only in the order of U and V within the
        // chroma plane (V then U). The descriptor list — offsets, lengths,
        // strides — is byte-identical. Consumers disambiguate via PixelFormat.
        var nv12 = PlaneLayout.DescribePlanes(CameraPixelFormat.Nv12, 1280, 720, 1280);
        var nv21 = PlaneLayout.DescribePlanes(CameraPixelFormat.Nv21, 1280, 720, 1280);
        Assert.Equal(nv12, nv21);
    }

    [Fact]
    public void DescribePlanes_Nv12_PaddedStride_PropagatesPaddingToBothPlanes()
    {
        // Some MF buffers report a luma stride larger than the visible
        // image width — typically 16- or 64-byte aligned. Both Y and UV
        // planes use the padded stride.
        var planes = PlaneLayout.DescribePlanes(
            CameraPixelFormat.Nv12, width: 1280, height: 720, lumaStride: 1536);

        Assert.NotNull(planes);
        Assert.Equal(1536, planes[0].Stride);
        Assert.Equal(1536 * 720, planes[0].Length);
        Assert.Equal(1536 * 720, planes[1].Offset);
        Assert.Equal(1536, planes[1].Stride);
        Assert.Equal(1536 * 360, planes[1].Length);
    }

    // ── I420 / YV12 (planar 4:2:0) ───────────────────────────────────

    [Fact]
    public void DescribePlanes_I420_NaturalStride_BuildsYUAndVPlanes()
    {
        var planes = PlaneLayout.DescribePlanes(
            CameraPixelFormat.I420, width: 1920, height: 1080, lumaStride: 1920);

        Assert.NotNull(planes);
        Assert.Equal(3, planes.Count);

        var y = planes[0];
        Assert.Equal(0, y.Offset);
        Assert.Equal(1920 * 1080, y.Length);
        Assert.Equal(1920, y.Stride);

        var u = planes[1];
        Assert.Equal(1920 * 1080, u.Offset);
        Assert.Equal(960 * 540, u.Length);
        Assert.Equal(960, u.Stride);
        Assert.Equal(960, u.Width);
        Assert.Equal(540, u.Height);

        var v = planes[2];
        Assert.Equal(1920 * 1080 + 960 * 540, v.Offset);
        Assert.Equal(960 * 540, v.Length);
        Assert.Equal(960, v.Stride);
    }

    [Fact]
    public void DescribePlanes_Yv12_HasIdenticalByteLayoutAsI420()
    {
        // YV12 swaps the U and V plane order vs I420. The descriptor list
        // is byte-identical; consumers disambiguate via PixelFormat.
        var i420 = PlaneLayout.DescribePlanes(CameraPixelFormat.I420, 1280, 720, 1280);
        var yv12 = PlaneLayout.DescribePlanes(CameraPixelFormat.Yv12, 1280, 720, 1280);
        Assert.Equal(i420, yv12);
    }

    [Fact]
    public void DescribePlanes_I420_PaddedStride_HalvesChromaStride()
    {
        // I420 chroma planes always use exactly half the luma stride.
        var planes = PlaneLayout.DescribePlanes(
            CameraPixelFormat.I420, width: 1280, height: 720, lumaStride: 1536);

        Assert.NotNull(planes);
        Assert.Equal(1536, planes[0].Stride);
        Assert.Equal(1536 * 720, planes[0].Length);

        Assert.Equal(768, planes[1].Stride);
        Assert.Equal(768 * 360, planes[1].Length);

        Assert.Equal(768, planes[2].Stride);
        Assert.Equal(768 * 360, planes[2].Length);

        // V offset = Y size + U size.
        Assert.Equal(1536 * 720 + 768 * 360, planes[2].Offset);
    }

    // ── Pool integration: multi-plane raw frames produce multi-plane leases ──

    [Fact]
    public void Pool_DeliversNv12Frame_WithCorrectLeasedPlanes()
    {
        var pool = new CameraFramePool();
        const int width = 1920;
        const int height = 1080;
        int frameSize = width * height * 3 / 2; // NV12: 1.5 bytes per pixel
        pool.Seed(frameSize, bufferCount: 1);

        var raw = new RawCameraFrame
        {
            Data = new byte[frameSize],
            Width = width,
            Height = height,
            PixelFormat = CameraPixelFormat.Nv12,
            Timestamp = TimeSpan.FromMilliseconds(123),
            PlaneCount = 2,
            Planes = PlaneLayout.DescribePlanes(CameraPixelFormat.Nv12, width, height, width),
        };

        using var frame = pool.TryDeliver(in raw);
        Assert.NotNull(frame);
        Assert.Equal(2, frame!.PlaneCount);
        Assert.False(frame.IsContiguous);

        var y = frame.GetPlane(0);
        Assert.Equal(width, y.Width);
        Assert.Equal(height, y.Height);
        Assert.Equal(width, y.Stride);
        Assert.Equal(width * height, y.Buffer.Length);

        var uv = frame.GetPlane(1);
        Assert.Equal(width / 2, uv.Width);
        Assert.Equal(height / 2, uv.Height);
        Assert.Equal(width, uv.Stride);
        Assert.Equal(width * height / 2, uv.Buffer.Length);
    }

    [Fact]
    public void Pool_DeliversI420Frame_WithThreeLeasedPlanes()
    {
        var pool = new CameraFramePool();
        const int width = 640;
        const int height = 480;
        int frameSize = width * height * 3 / 2;
        pool.Seed(frameSize, bufferCount: 1);

        var raw = new RawCameraFrame
        {
            Data = new byte[frameSize],
            Width = width,
            Height = height,
            PixelFormat = CameraPixelFormat.I420,
            Timestamp = TimeSpan.Zero,
            PlaneCount = 3,
            Planes = PlaneLayout.DescribePlanes(CameraPixelFormat.I420, width, height, width),
        };

        using var frame = pool.TryDeliver(in raw);
        Assert.NotNull(frame);
        Assert.Equal(3, frame!.PlaneCount);
        Assert.False(frame.IsContiguous);

        Assert.Equal(width * height, frame.GetPlane(0).Buffer.Length);
        Assert.Equal(width * height / 4, frame.GetPlane(1).Buffer.Length);
        Assert.Equal(width * height / 4, frame.GetPlane(2).Buffer.Length);
    }

    [Fact]
    public void Pool_DeliversMjpegFrame_WithSinglePlane()
    {
        // MJPEG is single-plane — DescribePlanes returns null and the pool
        // falls back to a single CameraPlane covering the whole buffer.
        var pool = new CameraFramePool();
        pool.Seed(frameSize: 1024, bufferCount: 1);

        var raw = new RawCameraFrame
        {
            Data = new byte[1024],
            Width = 1280,
            Height = 720,
            PixelFormat = CameraPixelFormat.Mjpeg,
            Timestamp = TimeSpan.Zero,
            PlaneCount = 1,
            Planes = PlaneLayout.DescribePlanes(CameraPixelFormat.Mjpeg, 1280, 720, 1280),
        };

        using var frame = pool.TryDeliver(in raw);
        Assert.NotNull(frame);
        Assert.Equal(1, frame!.PlaneCount);
        Assert.True(frame.IsContiguous);
    }
}
