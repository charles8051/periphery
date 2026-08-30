namespace Periphery.Camera.Avalonia.Tests;

/// <summary>
/// #318: the strided row copy and the two YUV → BGRA converters that put a raw
/// camera frame into a <c>WriteableBitmap</c>.
/// </summary>
/// <remarks>
/// <para>
/// No Avalonia app, headless or otherwise. These call the same functions
/// <c>CameraPreview</c> calls, over plain arrays, so nothing here can be
/// satisfied by the headless render interface's habit of accepting every format
/// and handing back an <c>Rgba8888</c> buffer at <c>width * 4</c>.
/// </para>
/// <para>
/// Every expected pixel is a literal from <see cref="Bt601"/>, derived from the
/// published coefficients in that file's remarks. Nothing asks the converter
/// what it should produce.
/// </para>
/// </remarks>
public sealed class PreviewPixelsTests
{
    // ── CopyRows ───────────────────────────────────────────────────────

    [Fact]
    public void CopyRows_EqualTightStrides_IsByteIdentical()
    {
        // 3 rows of 4 bytes, values 0..11 so a row swap or an off-by-one is a
        // different number rather than a different-looking picture.
        byte[] source = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11];
        var destination = new byte[12];

        PreviewPixels.CopyRows(source, 4, destination, 4, rowBytes: 4, height: 3);

        Assert.Equal(source, destination);
    }

    [Fact]
    public void CopyRows_PaddedSource_DropsThePadding()
    {
        // Rows of 4 meaningful bytes at a stride of 6. Bytes 4 and 5 of each row
        // are padding and must not reach the destination.
        byte[] source =
        [
            0, 1, 2, 3, 0xEE, 0xEE,
            4, 5, 6, 7, 0xEE, 0xEE,
        ];
        var destination = new byte[8];

        PreviewPixels.CopyRows(source, 6, destination, 4, rowBytes: 4, height: 2);

        Assert.Equal<byte[]>([0, 1, 2, 3, 4, 5, 6, 7], destination);
    }

    [Fact]
    public void CopyRows_PaddedDestination_LeavesThePaddingUntouched()
    {
        // The case that actually happens: Periphery's rows are tight (ADR-0081
        // D1) and Avalonia's RowBytes is wider than width * bpp.
        byte[] source = [1, 2, 3, 4, 5, 6];
        var destination = new byte[12];
        Array.Fill(destination, (byte)0x7F);

        PreviewPixels.CopyRows(source, 3, destination, 6, rowBytes: 3, height: 2);

        Assert.Equal<byte[]>(
            [
                1, 2, 3, 0x7F, 0x7F, 0x7F,
                4, 5, 6, 0x7F, 0x7F, 0x7F,
            ],
            destination);
    }

    [Fact]
    public void CopyRows_BothPaddedByDifferentAmounts_LandsEveryRowAtItsOwnStride()
    {
        byte[] source =
        [
            1, 2, 0xEE, 0xEE, 0xEE,
            3, 4, 0xEE, 0xEE, 0xEE,
            5, 6, 0xEE, 0xEE, 0xEE,
        ];
        var destination = new byte[9];

        PreviewPixels.CopyRows(source, 5, destination, 3, rowBytes: 2, height: 3);

        Assert.Equal<byte[]>([1, 2, 0, 3, 4, 0, 5, 6, 0], destination);
    }

    [Fact]
    public void CopyRows_ZeroHeight_IsANoOp()
    {
        var destination = new byte[4];

        PreviewPixels.CopyRows([], 0, destination, 0, rowBytes: 0, height: 0);

        Assert.Equal<byte[]>([0, 0, 0, 0], destination);
    }

    [Fact]
    public void CopyRows_ShortSource_ThrowsRatherThanReadingPastTheEnd()
    {
        // Three rows of four asked for, eleven bytes supplied.
        var source = new byte[11];
        var destination = new byte[12];

        Assert.Throws<ArgumentException>(
            () => PreviewPixels.CopyRows(source, 4, destination, 4, rowBytes: 4, height: 3));
    }

    [Fact]
    public void CopyRows_StrideNarrowerThanARow_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => PreviewPixels.CopyRows(new byte[12], 3, new byte[12], 4, rowBytes: 4, height: 3));
        Assert.Throws<ArgumentException>(
            () => PreviewPixels.CopyRows(new byte[12], 4, new byte[12], 3, rowBytes: 4, height: 3));
    }

    // ── YUY2 → BGRA ────────────────────────────────────────────────────

    [Fact]
    public void Yuy2ToBgra_OneMacropixel_SharesTheChromaPairAcrossBothPixels()
    {
        // Y0 U Y1 V. Both pixels take the red chroma; the luma differs, so a
        // converter that read Y once would produce two identical pixels.
        byte[] source = [Bt601.RedY, Bt601.RedU, Bt601.BlackY, Bt601.RedV];
        var destination = new byte[8];

        PreviewPixels.Yuy2ToBgra(source, 4, destination, 8, width: 2, height: 1);

        Bt601.AssertPixel(destination, 0, Bt601.Red, "pixel 0 (Y=81 with red chroma)");
        // Y=16 with red chroma: C=0, D=-38, E=112.
        //   B = 0 - 19,608 + 128 = -19,480 → clamped to 0
        //   G = 0 + 3,800 - 23,296 + 128 = -19,368 → clamped to 0
        //   R = 0 + 45,808 + 128 = 45,936 >> 8 = 179
        Bt601.AssertPixel(destination, 4, [0, 0, 179, 255], "pixel 1 (Y=16 with red chroma)");
    }

    [Fact]
    public void Yuy2ToBgra_GreyRamp_ConvertsEachLumaIndependently()
    {
        // Neutral chroma throughout, so only the luma term is exercised and each
        // pixel is its own hand-derived grey.
        byte[] source =
        [
            Bt601.BlackY, Bt601.NeutralU, Bt601.GreyY, Bt601.NeutralV,
            Bt601.WhiteY, Bt601.NeutralU, Bt601.GreyY, Bt601.NeutralV,
        ];
        var destination = new byte[16];

        PreviewPixels.Yuy2ToBgra(source, 8, destination, 16, width: 4, height: 1);

        Bt601.AssertPixel(destination, 0, Bt601.Black, "pixel 0");
        Bt601.AssertPixel(destination, 4, Bt601.Grey, "pixel 1");
        Bt601.AssertPixel(destination, 8, Bt601.White, "pixel 2");
        Bt601.AssertPixel(destination, 12, Bt601.Grey, "pixel 3");
    }

    [Fact]
    public void Yuy2ToBgra_PaddedStridesBothSides_KeepsEveryRowOnItsOwnColours()
    {
        // Row 0 is red then blue, row 1 is blue then red. Source rows are 8 bytes
        // at a stride of 12; destination rows are 16 bytes at a stride of 20. A
        // stride confusion on either side lands a row on the other row's colour
        // or on the 0xEE / 0x7F filler.
        byte[] source =
        [
            Bt601.RedY, Bt601.RedU, Bt601.RedY, Bt601.RedV,
            Bt601.BlueY, Bt601.BlueU, Bt601.BlueY, Bt601.BlueV,
            0xEE, 0xEE, 0xEE, 0xEE,

            Bt601.BlueY, Bt601.BlueU, Bt601.BlueY, Bt601.BlueV,
            Bt601.RedY, Bt601.RedU, Bt601.RedY, Bt601.RedV,
            0xEE, 0xEE, 0xEE, 0xEE,
        ];
        var destination = new byte[40];
        Array.Fill(destination, (byte)0x7F);

        PreviewPixels.Yuy2ToBgra(source, 12, destination, 20, width: 4, height: 2);

        Bt601.AssertPixel(destination, 0, Bt601.Red, "row 0 pixel 0");
        Bt601.AssertPixel(destination, 4, Bt601.Red, "row 0 pixel 1");
        Bt601.AssertPixel(destination, 8, Bt601.Blue, "row 0 pixel 2");
        Bt601.AssertPixel(destination, 12, Bt601.Blue, "row 0 pixel 3");
        Bt601.AssertPixel(destination, 20, Bt601.Blue, "row 1 pixel 0");
        Bt601.AssertPixel(destination, 24, Bt601.Blue, "row 1 pixel 1");
        Bt601.AssertPixel(destination, 28, Bt601.Red, "row 1 pixel 2");
        Bt601.AssertPixel(destination, 32, Bt601.Red, "row 1 pixel 3");

        // Row 0's destination padding is bytes 16..19, row 1's is 36..39.
        Assert.Equal<byte[]>([0x7F, 0x7F, 0x7F, 0x7F], destination[16..20]);
        Assert.Equal<byte[]>([0x7F, 0x7F, 0x7F, 0x7F], destination[36..40]);
    }

    [Fact]
    public void Yuy2ToBgra_OddWidth_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => PreviewPixels.Yuy2ToBgra(new byte[6], 6, new byte[12], 12, width: 3, height: 1));
    }

    [Fact]
    public void Yuy2ToBgra_ShortSource_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => PreviewPixels.Yuy2ToBgra(new byte[7], 8, new byte[32], 16, width: 4, height: 2));
    }

    // ── NV12 → BGRA ────────────────────────────────────────────────────

    [Fact]
    public void Nv12ToBgra_OneChromaPairCoversATwoByTwoBlock()
    {
        // 2x2, all four luma samples different, one UV pair. The four pixels must
        // differ only by luma.
        byte[] luma = [Bt601.BlackY, Bt601.GreyY, Bt601.WhiteY, Bt601.GreyY];
        byte[] chroma = [Bt601.NeutralU, Bt601.NeutralV];
        var destination = new byte[16];

        PreviewPixels.Nv12ToBgra(luma, 2, chroma, 2, destination, 8, width: 2, height: 2);

        Bt601.AssertPixel(destination, 0, Bt601.Black, "row 0 pixel 0");
        Bt601.AssertPixel(destination, 4, Bt601.Grey, "row 0 pixel 1");
        Bt601.AssertPixel(destination, 8, Bt601.White, "row 1 pixel 0");
        Bt601.AssertPixel(destination, 12, Bt601.Grey, "row 1 pixel 1");
    }

    [Fact]
    public void Nv12ToBgra_ChromaIsIndexedByBlock_NotByPixel()
    {
        // 4x4. Chroma row 0 is (red, blue) and chroma row 1 is (blue, red), so
        // the four 2x2 blocks are red, blue / blue, red. Reading the chroma plane
        // at the pixel's own column, or at the pixel's own row, puts a colour in
        // the wrong quadrant.
        byte[] luma =
        [
            Bt601.RedY, Bt601.RedY, Bt601.BlueY, Bt601.BlueY,
            Bt601.RedY, Bt601.RedY, Bt601.BlueY, Bt601.BlueY,
            Bt601.BlueY, Bt601.BlueY, Bt601.RedY, Bt601.RedY,
            Bt601.BlueY, Bt601.BlueY, Bt601.RedY, Bt601.RedY,
        ];

        byte[] chroma =
        [
            Bt601.RedU, Bt601.RedV, Bt601.BlueU, Bt601.BlueV,
            Bt601.BlueU, Bt601.BlueV, Bt601.RedU, Bt601.RedV,
        ];
        var destination = new byte[64];

        PreviewPixels.Nv12ToBgra(luma, 4, chroma, 4, destination, 16, width: 4, height: 4);

        for (int row = 0; row < 4; row++)
        {
            bool topHalf = row < 2;
            for (int column = 0; column < 4; column++)
            {
                bool leftHalf = column < 2;
                var expected = topHalf == leftHalf ? Bt601.Red : Bt601.Blue;
                Bt601.AssertPixel(
                    destination, (row * 16) + (column * 4), expected, $"row {row} pixel {column}");
            }
        }
    }

    [Fact]
    public void Nv12ToBgra_PaddedDestination_LeavesThePaddingUntouched()
    {
        byte[] luma = [Bt601.GreenY, Bt601.GreenY, Bt601.GreenY, Bt601.GreenY];
        byte[] chroma = [Bt601.GreenU, Bt601.GreenV];
        var destination = new byte[24];
        Array.Fill(destination, (byte)0x7F);

        PreviewPixels.Nv12ToBgra(luma, 2, chroma, 2, destination, 12, width: 2, height: 2);

        // Green's blue channel is 1, not 0 — a converter that clamped the whole
        // pixel to zero would still look green and would fail here.
        Bt601.AssertPixel(destination, 0, Bt601.Green, "row 0 pixel 0");
        Bt601.AssertPixel(destination, 4, Bt601.Green, "row 0 pixel 1");
        Bt601.AssertPixel(destination, 12, Bt601.Green, "row 1 pixel 0");
        Bt601.AssertPixel(destination, 16, Bt601.Green, "row 1 pixel 1");
        Assert.Equal<byte[]>([0x7F, 0x7F, 0x7F, 0x7F], destination[8..12]);
        Assert.Equal<byte[]>([0x7F, 0x7F, 0x7F, 0x7F], destination[20..24]);
    }

    [Theory]
    [InlineData(3, 2)]
    [InlineData(2, 3)]
    public void Nv12ToBgra_OddDimensions_Throw(int width, int height)
    {
        Assert.Throws<ArgumentException>(
            () => PreviewPixels.Nv12ToBgra(
                new byte[64], width, new byte[64], width, new byte[256], width * 4, width, height));
    }

    [Fact]
    public void Nv12ToBgra_ShortChromaPlane_Throws()
    {
        // 4x4 needs 8 chroma bytes; 7 are supplied.
        Assert.Throws<ArgumentException>(
            () => PreviewPixels.Nv12ToBgra(
                new byte[16], 4, new byte[7], 4, new byte[64], 16, width: 4, height: 4));
    }
}
