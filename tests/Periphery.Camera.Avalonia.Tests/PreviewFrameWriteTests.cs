using Periphery.Camera.Testing;

namespace Periphery.Camera.Avalonia.Tests;

/// <summary>
/// #318: a frame from a camera, through the same call the control makes, into
/// the bytes a <c>WriteableBitmap</c> would have held.
/// </summary>
/// <remarks>
/// <para>
/// The frames come from <c>InMemoryCameraBackend</c> so they are real pooled,
/// plane-described frames rather than hand-built stubs — the same shape a
/// backend delivers, with the same de-padding the pool applies (ADR-0081 D1).
/// The destination is a <c>byte[]</c> standing in for the locked framebuffer,
/// which is the only part of the control's write path that needs Avalonia.
/// </para>
/// <para>
/// Every expected pixel is a literal from <see cref="Bt601"/> at a
/// hand-calculated offset. Nothing here computes an expectation from
/// <c>PreviewPixels</c>, <c>CameraFrameLayout</c>, or <c>PlaneLayout</c>.
/// </para>
/// </remarks>
public sealed class PreviewFrameWriteTests
{
    private static CameraFormat Format(int width, int height, CameraPixelFormat pixelFormat) =>
        new(width, height, pixelFormat, new Rational(30), new Rational(30),
            pixelFormat == CameraPixelFormat.Mjpeg
                ? CameraTransport.Compressed
                : CameraTransport.Uncompressed);

    /// <summary>
    /// One frame and the session that owns its buffer. The session outlives the
    /// frame rather than the other way round — the bytes belong to the session's
    /// pool, and the lease has to go back before the pool does.
    /// </summary>
    private sealed class FrameScope(CameraSession session, ICameraFrame frame) : IAsyncDisposable
    {
        public ICameraFrame Frame { get; } = frame;

        public async ValueTask DisposeAsync()
        {
            Frame.Dispose();
            await session.DisposeAsync();
        }
    }

    private static async Task<FrameScope> OneFrameAsync(
        CameraFormat format,
        Func<CameraFrameSpec, byte[]>? factory = null,
        int overrideStride = 0)
    {
        var backend = new InMemoryCameraBackend(formats: [format]) { MaxFrames = 1 };
        if (factory is not null)
            backend.FrameFactory = factory;
        if (overrideStride > 0)
            backend.OverrideStride = overrideStride;

        var session = await CameraTestHarness.OpenSessionAsync(
            backend, new CameraConfiguration(format));
        await session.StartCaptureAsync();
        return new FrameScope(session, await session.ReadFrameAsync());
    }

    // ── BGRA32: the direct row copy ────────────────────────────────────

    [Fact]
    public async Task Write_Bgra32_CopiesEveryRowIntoAPaddedSurface()
    {
        // 8x4 BGRA32: 32 bytes of picture per row. HorizontalGradient writes
        // byte i of every row as i & 0xFF, so a row landing at the wrong offset
        // reads a value that is not the one derived here.
        //
        // The surface is 40 bytes per row — 8 bytes of padding Avalonia is
        // entitled to and the camera knows nothing about.
        await using var scope = await OneFrameAsync(
            Format(8, 4, CameraPixelFormat.Bgra32), CameraFramePatterns.HorizontalGradient);

        var destination = new byte[160];
        Array.Fill(destination, (byte)0x7F);

        PreviewPixels.Write(scope.Frame, PreviewPixelPath.CopyBgra, destination, 40);

        for (int row = 0; row < 4; row++)
        {
            int start = row * 40;
            Assert.Equal(0, destination[start]);
            Assert.Equal(1, destination[start + 1]);
            Assert.Equal(31, destination[start + 31]);
            // Bytes 32..39 of each destination row are padding.
            Assert.Equal<byte[]>(
                [0x7F, 0x7F, 0x7F, 0x7F, 0x7F, 0x7F, 0x7F, 0x7F],
                destination[(start + 32)..(start + 40)]);
        }
    }

    [Fact]
    public async Task Write_Bgra32_TightSurface_IsTheFrameByteForByte()
    {
        await using var scope = await OneFrameAsync(
            Format(8, 4, CameraPixelFormat.Bgra32), CameraFramePatterns.HorizontalGradient);

        var destination = new byte[128];

        PreviewPixels.Write(scope.Frame, PreviewPixelPath.CopyBgra, destination, 32);

        Assert.Equal(scope.Frame.ContiguousBuffer.ToArray(), destination);
    }

    [Fact]
    public async Task Write_Bgra32_ShortSurface_ThrowsBeforeWritingAnything()
    {
        await using var scope = await OneFrameAsync(
            Format(8, 4, CameraPixelFormat.Bgra32), CameraFramePatterns.HorizontalGradient);

        // Three rows' worth of surface for a four-row frame.
        var destination = new byte[96];

        Assert.Throws<ArgumentException>(
            () => PreviewPixels.Write(scope.Frame, PreviewPixelPath.CopyBgra, destination, 32));
        Assert.All(destination, b => Assert.Equal(0, b));
    }

    // ── YUY2 ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Write_Yuy2_ConvertsAPatternedFrame()
    {
        // 4x2. Row 0 is red then blue, row 1 is blue then red.
        await using var scope = await OneFrameAsync(Format(4, 2, CameraPixelFormat.Yuy2), Yuy2Halves);

        var destination = new byte[32];

        PreviewPixels.Write(scope.Frame, PreviewPixelPath.ConvertYuy2, destination, 16);

        Bt601.AssertPixel(destination, 0, Bt601.Red, "row 0 pixel 0");
        Bt601.AssertPixel(destination, 4, Bt601.Red, "row 0 pixel 1");
        Bt601.AssertPixel(destination, 8, Bt601.Blue, "row 0 pixel 2");
        Bt601.AssertPixel(destination, 12, Bt601.Blue, "row 0 pixel 3");
        Bt601.AssertPixel(destination, 16, Bt601.Blue, "row 1 pixel 0");
        Bt601.AssertPixel(destination, 20, Bt601.Blue, "row 1 pixel 1");
        Bt601.AssertPixel(destination, 24, Bt601.Red, "row 1 pixel 2");
        Bt601.AssertPixel(destination, 28, Bt601.Red, "row 1 pixel 3");
    }

    [Fact]
    public async Task Write_Yuy2_PaddedDriverStride_ConvertsTheSame()
    {
        // The driver pads 8-byte rows to 16. ADR-0081 D1 says the pool removes
        // that before the frame is delivered, so the control sees the same
        // picture and never learns the driver padded anything.
        await using var scope = await OneFrameAsync(
            Format(4, 2, CameraPixelFormat.Yuy2), Yuy2Halves, overrideStride: 16);

        Assert.Equal(8, scope.Frame.GetPlane(0).Stride);

        var destination = new byte[32];
        PreviewPixels.Write(scope.Frame, PreviewPixelPath.ConvertYuy2, destination, 16);

        Bt601.AssertPixel(destination, 0, Bt601.Red, "row 0 pixel 0");
        Bt601.AssertPixel(destination, 12, Bt601.Blue, "row 0 pixel 3");
        Bt601.AssertPixel(destination, 16, Bt601.Blue, "row 1 pixel 0");
        Bt601.AssertPixel(destination, 28, Bt601.Red, "row 1 pixel 3");
    }

    // ── NV12 ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Write_Nv12_ReadsBothPlanes()
    {
        // 4x4 in four 2x2 quadrants: red, blue / blue, red. The luma plane is 16
        // bytes at offset 0 and the chroma plane is 8 bytes at offset 16, so a
        // converter that never reached plane 1 would render four grey quadrants.
        await using var scope = await OneFrameAsync(Format(4, 4, CameraPixelFormat.Nv12), Nv12Quadrants);

        Assert.Equal(2, scope.Frame.PlaneCount);

        var destination = new byte[64];
        PreviewPixels.Write(scope.Frame, PreviewPixelPath.ConvertNv12, destination, 16);

        AssertQuadrants(destination, destinationStride: 16);
    }

    [Fact]
    public async Task Write_Nv12_PaddedDriverStride_ConvertsTheSame()
    {
        // Media Foundation rounds the NV12 luma stride to a 64-byte multiple
        // (ADR-0081); here 4 becomes 8, which also moves the chroma plane. The
        // pool de-pads both planes, so the same quadrants come out.
        await using var scope = await OneFrameAsync(
            Format(4, 4, CameraPixelFormat.Nv12), Nv12Quadrants, overrideStride: 8);

        Assert.Equal(4, scope.Frame.GetPlane(0).Stride);

        var destination = new byte[64];
        PreviewPixels.Write(scope.Frame, PreviewPixelPath.ConvertNv12, destination, 16);

        AssertQuadrants(destination, destinationStride: 16);
    }

    [Fact]
    public async Task Write_Nv12_PaddedSurface_LeavesTheSurfacePaddingAlone()
    {
        await using var scope = await OneFrameAsync(Format(4, 4, CameraPixelFormat.Nv12), Nv12Quadrants);

        var destination = new byte[96];
        Array.Fill(destination, (byte)0x7F);

        PreviewPixels.Write(scope.Frame, PreviewPixelPath.ConvertNv12, destination, 24);

        AssertQuadrants(destination, destinationStride: 24);
        for (int row = 0; row < 4; row++)
        {
            Assert.Equal<byte[]>(
                [0x7F, 0x7F, 0x7F, 0x7F, 0x7F, 0x7F, 0x7F, 0x7F],
                destination[((row * 24) + 16)..((row * 24) + 24)]);
        }
    }

    // ── MJPEG is not a raw path ────────────────────────────────────────

    [Fact]
    public async Task Write_DecodeJpeg_Refuses()
    {
        await using var scope = await OneFrameAsync(Format(8, 4, CameraPixelFormat.Mjpeg));

        Assert.Throws<ArgumentException>(
            () => PreviewPixels.Write(scope.Frame, PreviewPixelPath.DecodeJpeg, new byte[128], 32));
    }

    // ── Patterns ───────────────────────────────────────────────────────

    /// <summary>
    /// Each row's macropixels split left/right into one colour and the other,
    /// with the split reversed in the bottom half of the image. Written through
    /// the spec's plane geometry so the same pattern works at a padded stride.
    /// </summary>
    private static byte[] Yuy2Halves(CameraFrameSpec spec)
    {
        var data = new byte[spec.FrameSize];
        var plane = spec.GetPlanes()[0];
        int macropixels = spec.Width / 2;

        for (int row = 0; row < plane.Height; row++)
        {
            int start = plane.Offset + (row * plane.Stride);
            for (int m = 0; m < macropixels; m++)
            {
                bool red = (row < spec.Height / 2) == (m < macropixels / 2);
                data[start + (m * 4)] = red ? Bt601.RedY : Bt601.BlueY;
                data[start + (m * 4) + 1] = red ? Bt601.RedU : Bt601.BlueU;
                data[start + (m * 4) + 2] = red ? Bt601.RedY : Bt601.BlueY;
                data[start + (m * 4) + 3] = red ? Bt601.RedV : Bt601.BlueV;
            }
        }
        return data;
    }

    /// <summary>
    /// Four quadrants — red, blue / blue, red — with the luma plane and the
    /// chroma plane each carrying their half of the answer.
    /// </summary>
    private static byte[] Nv12Quadrants(CameraFrameSpec spec)
    {
        var data = new byte[spec.FrameSize];
        var planes = spec.GetPlanes();
        var luma = planes[0];
        var chroma = planes[1];

        for (int row = 0; row < luma.Height; row++)
        {
            int start = luma.Offset + (row * luma.Stride);
            for (int column = 0; column < luma.RowBytes; column++)
            {
                bool red = (row < spec.Height / 2) == (column < spec.Width / 2);
                data[start + column] = red ? Bt601.RedY : Bt601.BlueY;
            }
        }

        int pairsPerRow = chroma.RowBytes / 2;
        for (int row = 0; row < chroma.Height; row++)
        {
            int start = chroma.Offset + (row * chroma.Stride);
            for (int pair = 0; pair < pairsPerRow; pair++)
            {
                bool red = (row < chroma.Height / 2) == (pair < pairsPerRow / 2);
                data[start + (pair * 2)] = red ? Bt601.RedU : Bt601.BlueU;
                data[start + (pair * 2) + 1] = red ? Bt601.RedV : Bt601.BlueV;
            }
        }
        return data;
    }

    /// <summary>
    /// Asserts the red / blue / blue / red quadrants of a converted 4×4 frame.
    /// </summary>
    private static void AssertQuadrants(byte[] destination, int destinationStride)
    {
        for (int row = 0; row < 4; row++)
        {
            for (int column = 0; column < 4; column++)
            {
                var expected = (row < 2) == (column < 2) ? Bt601.Red : Bt601.Blue;
                Bt601.AssertPixel(
                    destination,
                    (row * destinationStride) + (column * 4),
                    expected,
                    $"row {row} pixel {column}");
            }
        }
    }
}
