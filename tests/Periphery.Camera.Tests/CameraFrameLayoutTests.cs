using Periphery.Camera;
using Xunit;

namespace Periphery.Camera.Tests;

/// <summary>
/// Pure-math tests for <see cref="CameraFrameLayout"/> — the single source of
/// truth for per-format frame dimensions. Runs on every platform (no native
/// dependencies). Locks in the NV12/NV21/I420/YV12 1.5-bytes-per-pixel fix
/// (review findings 2.1 + 6.3): the old copies charged 3 bytes/px, double-
/// allocating the pool seed.
/// </summary>
public sealed class CameraFrameLayoutTests
{
    // ── BitsPerPixel ──────────────────────────────────────────────────

    [Theory]
    [InlineData(CameraPixelFormat.Rgb24, 24)]
    [InlineData(CameraPixelFormat.Bgr24, 24)]
    [InlineData(CameraPixelFormat.Rgba32, 32)]
    [InlineData(CameraPixelFormat.Bgra32, 32)]
    [InlineData(CameraPixelFormat.Argb32, 32)]
    [InlineData(CameraPixelFormat.Yuy2, 16)]
    [InlineData(CameraPixelFormat.Uyvy, 16)]
    [InlineData(CameraPixelFormat.Gray8, 8)]
    [InlineData(CameraPixelFormat.Gray16, 16)]
    // The four planar 4:2:0 formats are 12 bits/px = 1.5 bytes/px — the value
    // the drifted copies got wrong (they charged 24 bits = 3 bytes).
    [InlineData(CameraPixelFormat.Nv12, 12)]
    [InlineData(CameraPixelFormat.Nv21, 12)]
    [InlineData(CameraPixelFormat.I420, 12)]
    [InlineData(CameraPixelFormat.Yv12, 12)]
    public void BitsPerPixel_MatchesFormat(CameraPixelFormat format, int expected)
        => Assert.Equal(expected, CameraFrameLayout.BitsPerPixel(format));

    [Theory]
    [InlineData(CameraPixelFormat.Mjpeg)]   // compressed — no fixed pixel cost
    [InlineData(CameraPixelFormat.Unknown)]
    public void BitsPerPixel_CompressedOrUnknown_Throws(CameraPixelFormat format)
        => Assert.Throws<System.ArgumentException>(() => CameraFrameLayout.BitsPerPixel(format));

    // ── BytesPerRow (luma / packed stride) ────────────────────────────

    [Theory]
    [InlineData(CameraPixelFormat.Rgb24, 640, 1920)]
    [InlineData(CameraPixelFormat.Bgr24, 640, 1920)]
    [InlineData(CameraPixelFormat.Rgba32, 640, 2560)]
    [InlineData(CameraPixelFormat.Bgra32, 640, 2560)]
    [InlineData(CameraPixelFormat.Argb32, 640, 2560)]
    [InlineData(CameraPixelFormat.Yuy2, 640, 1280)]
    [InlineData(CameraPixelFormat.Uyvy, 640, 1280)]
    [InlineData(CameraPixelFormat.Gray8, 640, 640)]
    [InlineData(CameraPixelFormat.Gray16, 640, 1280)]
    // Planar luma plane is 1 byte per pixel-column (chroma stride is derived
    // separately in PlaneLayout).
    [InlineData(CameraPixelFormat.Nv12, 640, 640)]
    [InlineData(CameraPixelFormat.I420, 640, 640)]
    // MJPEG / Unknown have no real stride — neutral width-byte fallback.
    [InlineData(CameraPixelFormat.Mjpeg, 640, 640)]
    public void BytesPerRow_MatchesFormat(CameraPixelFormat format, int width, int expected)
        => Assert.Equal(expected, CameraFrameLayout.BytesPerRow(format, width));

    // ── FrameSize: packed formats = width × height × bytes/px ──────────

    [Theory]
    [InlineData(CameraPixelFormat.Rgb24, 640, 480, 640 * 480 * 3)]
    [InlineData(CameraPixelFormat.Bgr24, 640, 480, 640 * 480 * 3)]
    [InlineData(CameraPixelFormat.Rgba32, 640, 480, 640 * 480 * 4)]
    [InlineData(CameraPixelFormat.Bgra32, 640, 480, 640 * 480 * 4)]
    [InlineData(CameraPixelFormat.Argb32, 640, 480, 640 * 480 * 4)]
    [InlineData(CameraPixelFormat.Yuy2, 640, 480, 640 * 480 * 2)]
    [InlineData(CameraPixelFormat.Uyvy, 640, 480, 640 * 480 * 2)]
    [InlineData(CameraPixelFormat.Gray8, 640, 480, 640 * 480)]
    [InlineData(CameraPixelFormat.Gray16, 640, 480, 640 * 480 * 2)]
    public void FrameSize_PackedFormats_NaturalStride(
        CameraPixelFormat format, int width, int height, int expected)
        => Assert.Equal(expected, CameraFrameLayout.FrameSize(format, width, height));

    // ── FrameSize: 4:2:0 planar = width × height × 3 / 2 (the fix) ─────

    [Theory]
    [InlineData(CameraPixelFormat.Nv12)]
    [InlineData(CameraPixelFormat.Nv21)]
    [InlineData(CameraPixelFormat.I420)]
    [InlineData(CameraPixelFormat.Yv12)]
    public void FrameSize_Planar420_IsOnePointFiveBytesPerPixel(CameraPixelFormat format)
    {
        const int width = 1920;
        const int height = 1080;
        // 1.5 bytes/px, NOT the old (wrong) 3 bytes/px.
        Assert.Equal(width * height * 3 / 2, CameraFrameLayout.FrameSize(format, width, height));
    }

    [Fact]
    public void FrameSize_Nv12_DoesNotDoubleAllocate()
    {
        // Regression guard for the original drift bug: NV12 at 1280×720 is
        // 1,382,400 bytes (1.5 bpp), not 2,764,800 (the old 3-bpp value).
        Assert.Equal(1280 * 720 * 3 / 2, CameraFrameLayout.FrameSize(CameraPixelFormat.Nv12, 1280, 720));
        Assert.NotEqual(1280 * 720 * 3, CameraFrameLayout.FrameSize(CameraPixelFormat.Nv12, 1280, 720));
    }

    [Fact]
    public void FrameSize_PaddedLumaStride_AccountsForPadding()
    {
        // A 16/64-byte-aligned MF buffer reports a luma stride wider than the
        // visible width; the size must use the padded stride.
        const int width = 1280;
        const int height = 720;
        const int paddedStride = 1536;

        // NV12 with padding: paddedStride*height (luma) + half that (chroma).
        int luma = paddedStride * height;
        Assert.Equal(luma + luma / 2,
            CameraFrameLayout.FrameSize(CameraPixelFormat.Nv12, width, height, paddedStride));

        // Packed RGB24 with padding: just paddedStride*height.
        Assert.Equal(paddedStride * height,
            CameraFrameLayout.FrameSize(CameraPixelFormat.Rgb24, width, height, paddedStride));
    }

    [Fact]
    public void FrameSize_Mjpeg_ReturnsGenerousCompressedEstimate()
    {
        // MJPEG has no pixel-exact size; the helper returns a generous buffer
        // (half a byte per pixel) so any single compressed frame fits.
        Assert.Equal(1280 * 720 / 2, CameraFrameLayout.FrameSize(CameraPixelFormat.Mjpeg, 1280, 720));
    }

    // ── PlaneCount ────────────────────────────────────────────────────

    [Theory]
    [InlineData(CameraPixelFormat.Nv12, 2)]
    [InlineData(CameraPixelFormat.Nv21, 2)]
    [InlineData(CameraPixelFormat.I420, 3)]
    [InlineData(CameraPixelFormat.Yv12, 3)]
    [InlineData(CameraPixelFormat.Rgb24, 1)]
    [InlineData(CameraPixelFormat.Bgr24, 1)]
    [InlineData(CameraPixelFormat.Rgba32, 1)]
    [InlineData(CameraPixelFormat.Bgra32, 1)]
    [InlineData(CameraPixelFormat.Argb32, 1)]
    [InlineData(CameraPixelFormat.Yuy2, 1)]
    [InlineData(CameraPixelFormat.Uyvy, 1)]
    [InlineData(CameraPixelFormat.Gray8, 1)]
    [InlineData(CameraPixelFormat.Gray16, 1)]
    [InlineData(CameraPixelFormat.Mjpeg, 1)]
    public void PlaneCount_MatchesFormat(CameraPixelFormat format, int expected)
        => Assert.Equal(expected, CameraFrameLayout.PlaneCount(format));

    // ── Cross-check: PlaneCount agrees with PlaneLayout.DescribePlanes ─

    [Theory]
    [InlineData(CameraPixelFormat.Nv12)]
    [InlineData(CameraPixelFormat.Nv21)]
    [InlineData(CameraPixelFormat.I420)]
    [InlineData(CameraPixelFormat.Yv12)]
    [InlineData(CameraPixelFormat.Rgb24)]
    [InlineData(CameraPixelFormat.Yuy2)]
    [InlineData(CameraPixelFormat.Mjpeg)]
    public void PlaneCount_AgreesWithPlaneLayout(CameraPixelFormat format)
    {
        // The multi-plane offset layout (PlaneLayout) and the scalar plane count
        // (CameraFrameLayout) must never disagree — both derive from the same
        // per-format facts. A null DescribePlanes means a single plane.
        var planes = Internal.PlaneLayout.DescribePlanes(format, 640, 480, 640);
        int describedCount = planes?.Count ?? 1;
        Assert.Equal(describedCount, CameraFrameLayout.PlaneCount(format));
    }
}
