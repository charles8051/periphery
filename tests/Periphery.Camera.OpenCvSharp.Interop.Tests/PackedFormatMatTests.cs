using OpenCvSharp;

namespace Periphery.Camera.OpenCvSharp.Interop.Tests;

/// <summary>
/// The packed RGB and grayscale rows of the table, converted and read back
/// pixel by pixel.
/// </summary>
/// <remarks>
/// Every frame here is 4x2 with a distinct value in every channel of every
/// pixel, so a converter that transposes, shifts a column, drops a byte per
/// pixel, or swaps two channels lands on a number that belongs to somewhere
/// else. Expectations are written as literals derived from the input array by
/// hand.
/// </remarks>
[Trait("Category", "Integration")]
public class PackedFormatMatTests
{
    // 4x2 BGR triples: (10,20,30) (40,50,60) (70,80,90) (100,110,120)
    //                  (130,140,150) (160,170,180) (190,200,210) (220,230,240)
    private static readonly byte[] Bgr24Bytes =
    [
        10, 20, 30, 40, 50, 60, 70, 80, 90, 100, 110, 120,
        130, 140, 150, 160, 170, 180, 190, 200, 210, 220, 230, 240,
    ];

    [OpenCvFact]
    public async Task Bgr24_ArrivesUnchanged()
    {
        await MatAssert.WithFrameAsync(CameraPixelFormat.Bgr24, 4, 2, Bgr24Bytes, frame =>
        {
            using var bgr = frame.ToBgr();

            Assert.Equal(2, bgr.Rows);
            Assert.Equal(4, bgr.Cols);

            MatAssert.Bgr(bgr, 0, 0, 10, 20, 30);
            MatAssert.Bgr(bgr, 0, 1, 40, 50, 60);
            MatAssert.Bgr(bgr, 0, 2, 70, 80, 90);
            MatAssert.Bgr(bgr, 0, 3, 100, 110, 120);
            MatAssert.Bgr(bgr, 1, 0, 130, 140, 150);
            MatAssert.Bgr(bgr, 1, 1, 160, 170, 180);
            MatAssert.Bgr(bgr, 1, 2, 190, 200, 210);
            MatAssert.Bgr(bgr, 1, 3, 220, 230, 240);
        });
    }

    [OpenCvFact]
    public async Task Bgr24_AsMatIsTheSameBytesInPlace()
    {
        await MatAssert.WithFrameAsync(CameraPixelFormat.Bgr24, 4, 2, Bgr24Bytes, frame =>
        {
            using var pin = frame.Pin();
            using var scope = frame.AsMat();

            // Zero-copy, stated as an address rather than as an intention.
            Assert.Equal(pin.Scan0, scope.Mat.Data);
            Assert.Equal(12, (int)scope.Mat.Step());
            Assert.Equal(MatType.CV_8UC3, scope.Mat.Type());

            MatAssert.Bgr(scope.Mat, 0, 0, 10, 20, 30);
            MatAssert.Bgr(scope.Mat, 1, 3, 220, 230, 240);
        });
    }

    [OpenCvFact]
    public async Task Rgb24_HasItsRedAndBlueSwapped()
    {
        // Same bytes as Bgr24Bytes, read as R,G,B. Pixel 0 is R=10 G=20 B=30, so
        // BGR is (30,20,10) — the reverse of the Bgr24 row above, which is what
        // proves the RGB2BGR arm fires rather than nothing at all.
        await MatAssert.WithFrameAsync(CameraPixelFormat.Rgb24, 4, 2, Bgr24Bytes, frame =>
        {
            using var bgr = frame.ToBgr();

            MatAssert.Bgr(bgr, 0, 0, 30, 20, 10);
            MatAssert.Bgr(bgr, 0, 3, 120, 110, 100);
            MatAssert.Bgr(bgr, 1, 0, 150, 140, 130);
            MatAssert.Bgr(bgr, 1, 3, 240, 230, 220);
        });
    }

    // 4x2 four-channel frames. Channel 3 of each pixel is 250 - pixelIndex, a
    // value distinct from every colour byte, so a conversion that keeps the
    // wrong channel is visible rather than plausible.
    private static readonly byte[] FourChannelBytes =
    [
        10, 20, 30, 250, 40, 50, 60, 249, 70, 80, 90, 248, 100, 110, 120, 247,
        130, 140, 150, 246, 160, 170, 180, 245, 190, 200, 210, 244, 220, 230, 240, 243,
    ];

    [OpenCvFact]
    public async Task Bgra32_DropsAlphaAndKeepsOrder()
    {
        await MatAssert.WithFrameAsync(CameraPixelFormat.Bgra32, 4, 2, FourChannelBytes, frame =>
        {
            using var bgr = frame.ToBgr();

            MatAssert.Bgr(bgr, 0, 0, 10, 20, 30);
            MatAssert.Bgr(bgr, 0, 3, 100, 110, 120);
            MatAssert.Bgr(bgr, 1, 3, 220, 230, 240);
        });
    }

    [OpenCvFact]
    public async Task Rgba32_ReversesTheColourChannelsAndDropsAlpha()
    {
        // R=10 G=20 B=30 A=250 -> BGR (30,20,10). One RGBA2BGR, not two hops
        // through BGRA.
        await MatAssert.WithFrameAsync(CameraPixelFormat.Rgba32, 4, 2, FourChannelBytes, frame =>
        {
            using var bgr = frame.ToBgr();

            MatAssert.Bgr(bgr, 0, 0, 30, 20, 10);
            MatAssert.Bgr(bgr, 0, 3, 120, 110, 100);
            MatAssert.Bgr(bgr, 1, 3, 240, 230, 220);
        });
    }

    [OpenCvFact]
    public async Task Argb32_ShufflesPastTheLeadingAlpha()
    {
        // A=10 R=20 G=30 B=250 -> BGR (250,30,20). The alpha here is the *first*
        // byte, so a converter that treated the frame as RGBA would emit
        // (250,20,10) and a converter that treated it as BGRA would emit
        // (10,20,30). Neither matches.
        await MatAssert.WithFrameAsync(CameraPixelFormat.Argb32, 4, 2, FourChannelBytes, frame =>
        {
            using var bgr = frame.ToBgr();

            MatAssert.Bgr(bgr, 0, 0, 250, 30, 20);
            MatAssert.Bgr(bgr, 0, 1, 249, 60, 50);
            MatAssert.Bgr(bgr, 0, 3, 247, 120, 110);
            MatAssert.Bgr(bgr, 1, 0, 246, 150, 140);
            MatAssert.Bgr(bgr, 1, 3, 243, 240, 230);
        });
    }

    [OpenCvFact]
    public async Task Gray8_WidensToThreeEqualChannels()
    {
        byte[] bytes = [10, 20, 30, 40, 50, 60, 70, 80];

        await MatAssert.WithFrameAsync(CameraPixelFormat.Gray8, 4, 2, bytes, frame =>
        {
            using var bgr = frame.ToBgr();

            MatAssert.Bgr(bgr, 0, 0, 10, 10, 10);
            MatAssert.Bgr(bgr, 0, 3, 40, 40, 40);
            MatAssert.Bgr(bgr, 1, 0, 50, 50, 50);
            MatAssert.Bgr(bgr, 1, 3, 80, 80, 80);
        });
    }

    [OpenCvFact]
    public async Task Gray16_WrapsAsSixteenBitAndRefusesBgr()
    {
        // Little-endian ushorts: 0x1234, 0x5678, 0x9ABC, 0xDEF0 on row 0 and
        // 0x0001, 0x0100, 0xFFFF, 0x8000 on row 1.
        byte[] bytes =
        [
            0x34, 0x12, 0x78, 0x56, 0xBC, 0x9A, 0xF0, 0xDE,
            0x01, 0x00, 0x00, 0x01, 0xFF, 0xFF, 0x00, 0x80,
        ];

        await MatAssert.WithFrameAsync(CameraPixelFormat.Gray16, 4, 2, bytes, frame =>
        {
            using (var scope = frame.AsMat())
            {
                Assert.Equal(MatType.CV_16UC1, scope.Mat.Type());
                Assert.Equal(8, (int)scope.Mat.Step());

                Assert.Equal(0x1234, scope.Mat.At<ushort>(0, 0));
                Assert.Equal(0xDEF0, scope.Mat.At<ushort>(0, 3));
                Assert.Equal(0x0001, scope.Mat.At<ushort>(1, 0));
                Assert.Equal(0x8000, scope.Mat.At<ushort>(1, 3));
            }

            using (var owned = frame.ToMat())
            {
                Assert.Equal(MatType.CV_16UC1, owned.Type());
                Assert.Equal(0xFFFF, owned.At<ushort>(1, 2));
            }

            // The one refusal in ToBgr, and the message has to be actionable:
            // it names the two OpenCV calls that make the choice the caller's.
            var ex = Assert.Throws<NotSupportedException>(() => frame.ToBgr());
            Assert.Contains("Cv2.Normalize", ex.Message, StringComparison.Ordinal);
            Assert.Contains("Cv2.ConvertScaleAbs", ex.Message, StringComparison.Ordinal);
        });
    }

    [OpenCvFact]
    public async Task ToMat_KeepsTheCaptureFormatRatherThanConverting()
    {
        await MatAssert.WithFrameAsync(CameraPixelFormat.Rgb24, 4, 2, Bgr24Bytes, frame =>
        {
            using var raw = frame.ToMat();

            // Still RGB. ToMat copies; it does not interpret.
            Assert.Equal(MatType.CV_8UC3, raw.Type());
            var px = raw.At<Vec3b>(0, 0);
            Assert.Equal(10, px.Item0);
            Assert.Equal(20, px.Item1);
            Assert.Equal(30, px.Item2);
        });
    }
}
