using System.Runtime.InteropServices;
using Periphery.Camera.Testing;

namespace Periphery.Camera.OpenCvSharp.Tests;

/// <summary>
/// What a frame from the pool actually looks like, checked against the mapping
/// table. No OpenCV call anywhere in here — the point is that the table and the
/// frame agree before a native library is involved at all.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is where the <c>Pin()</c> assumption gets confirmed.</b> #316 shipped
/// <see cref="CameraFramePinning.Pin"/> pinning a planar frame's whole buffer at
/// the luma stride, and #316 recorded that as unverified against the format
/// table. The tests below check it format by format: for NV12 and NV21 OpenCV
/// reads chroma at <c>Scan0 + step * height</c> and at the same <c>step</c>, and
/// for I420 and YV12 it walks each chroma row as <c>width / 2</c> bytes followed
/// by <c>step - width / 2</c>. Both descriptions have to match where the frame
/// actually puts its planes, and both do — but only because the rows are tight.
/// </para>
/// </remarks>
public class DeliveredFrameShapeTests
{
    public static TheoryData<CameraPixelFormat> Uncompressed
    {
        get
        {
            var data = new TheoryData<CameraPixelFormat>();
            foreach (var format in Enum.GetValues<CameraPixelFormat>())
            {
                if (CameraMatLayout.HasMatShape(format))
                    data.Add(format);
            }
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Uncompressed))]
    public async Task PinnedFrame_MatchesTheDescribedShape(CameraPixelFormat format)
    {
        await FrameCapture.WithOneFrameAsync(
            format, 64, 32, CameraFramePatterns.RowIndex, frame =>
            {
                var shape = CameraMatLayout.Describe(format, frame.Width, frame.Height);

                using var pin = frame.Pin();

                Assert.Equal(shape.Step, pin.Stride);
                Assert.Equal(shape.ByteLength, pin.Length);
                Assert.Equal(frame.ContiguousBuffer.Length, pin.Length);
            });
    }

    [Theory]
    [InlineData(CameraPixelFormat.Nv12)]
    [InlineData(CameraPixelFormat.Nv21)]
    public async Task Nv12Family_StacksChromaWhereOpenCvLooksForIt(CameraPixelFormat format)
    {
        // 64x32 NV12: luma is 64 x 32 = 2048 bytes, chroma is 64 x 16 = 1024,
        // total 3072. OpenCV reads the chroma plane from Scan0 + step * height
        // = +2048, at step 64.
        await FrameCapture.WithOneFrameAsync(
            format, 64, 32, CameraFramePatterns.RowIndex, frame =>
            {
                Assert.Equal(2, frame.PlaneCount);

                var luma = frame.GetPlane(0);
                var chroma = frame.GetPlane(1);

                Assert.Equal(64, luma.Stride);
                Assert.Equal(0, OffsetOf(frame, luma));
                Assert.Equal(2048, luma.Buffer.Length);

                Assert.Equal(64, chroma.Stride);
                Assert.Equal(2048, OffsetOf(frame, chroma));
                Assert.Equal(1024, chroma.Buffer.Length);
                // Half as many samples per row, each one an interleaved UV pair,
                // so the row is still 64 bytes wide.
                Assert.Equal(32, chroma.Width);
                Assert.Equal(16, chroma.Height);

                Assert.Equal(3072, frame.ContiguousBuffer.Length);
            });
    }

    [Theory]
    [InlineData(CameraPixelFormat.I420)]
    [InlineData(CameraPixelFormat.Yv12)]
    public async Task I420Family_PacksChromaAsOpenCvWalksIt(CameraPixelFormat format)
    {
        // 64x32 I420: luma 2048 bytes, then two 32 x 16 = 512-byte chroma planes
        // at +2048 and +2560. OpenCV advances width/2 = 32 bytes then
        // step - width/2 = 64 - 32 = 32 more, i.e. a contiguous run of 32-byte
        // rows — which is exactly a 32-byte chroma stride. The two descriptions
        // coincide only because step == width, and step == width is ADR-0081 D1.
        await FrameCapture.WithOneFrameAsync(
            format, 64, 32, CameraFramePatterns.RowIndex, frame =>
            {
                Assert.Equal(3, frame.PlaneCount);

                var luma = frame.GetPlane(0);
                var first = frame.GetPlane(1);
                var second = frame.GetPlane(2);

                Assert.Equal(64, luma.Stride);
                Assert.Equal(0, OffsetOf(frame, luma));

                Assert.Equal(32, first.Stride);
                Assert.Equal(2048, OffsetOf(frame, first));
                Assert.Equal(512, first.Buffer.Length);

                Assert.Equal(32, second.Stride);
                Assert.Equal(2560, OffsetOf(frame, second));
                Assert.Equal(512, second.Buffer.Length);

                Assert.Equal(3072, frame.ContiguousBuffer.Length);
            });
    }

    [Fact]
    public async Task Pin_ReportsPlaneZerosStrideNotAPlaneOfItsOwn()
    {
        // The whole-buffer pin on a planar frame reports the luma stride, which
        // is what the single-Mat trick wants. PinPlane is the call for a plane's
        // own stride, and on I420 chroma that is a different number — 32 against
        // 64. Asserted together so the two entry points cannot quietly converge.
        await FrameCapture.WithOneFrameAsync(
            CameraPixelFormat.I420, 64, 32, CameraFramePatterns.RowIndex, frame =>
            {
                using var whole = frame.Pin();
                using var chroma = frame.PinPlane(1);

                Assert.Equal(64, whole.Stride);
                Assert.Equal(3072, whole.Length);

                Assert.Equal(32, chroma.Stride);
                Assert.Equal(512, chroma.Length);
            });
    }

    [Fact]
    public async Task Mjpeg_HasNoStrideAndNoShape()
    {
        // ADR-0081 D7: a compressed frame is one opaque run. The pin says 0
        // rather than inventing a row width, and the table refuses it outright.
        await FrameCapture.WithOneFrameAsync(
            CameraPixelFormat.Mjpeg, 64, 32, CameraFramePatterns.FrameIndexConstant, frame =>
            {
                using var pin = frame.Pin();

                Assert.Equal(0, pin.Stride);
                Assert.True(pin.Length > 0);
                Assert.False(CameraMatLayout.HasMatShape(frame.PixelFormat));
            });
    }

    // ── The invariant the table is built on ────────────────────────────

    [Theory]
    [InlineData(CameraPixelFormat.Bgr24, 192, 256)]
    [InlineData(CameraPixelFormat.Yuy2, 128, 192)]
    [InlineData(CameraPixelFormat.Nv12, 64, 128)]
    [InlineData(CameraPixelFormat.I420, 64, 128)]
    public async Task PaddedSource_IsDeliveredTight(
        CameraPixelFormat format, int naturalStride, int paddedStride)
    {
        // The backend pads the source rows; the pool de-pads on the copy it was
        // making anyway. What the mapping table sees is the delivered frame, and
        // the delivered frame is tight — which is why the table has no padded
        // case and no stride parameter.
        await FrameCapture.WithOneFrameAsync(
            format, 64, 32, CameraFramePatterns.RowIndex,
            frame =>
            {
                Assert.Equal(naturalStride, frame.GetPlane(0).Stride);

                var shape = CameraMatLayout.Describe(format, 64, 32);
                Assert.Equal(naturalStride, shape.Step);

                using var pin = frame.Pin();
                Assert.Equal(naturalStride, pin.Stride);
            },
            overrideStride: paddedStride);
    }

    [Fact]
    public async Task PaddedSource_LeavesRowsAtTheirTightOffsets()
    {
        // RowIndex writes row n's index into the first byte of row n and leaves
        // the rest zero. At a 256-byte source stride and a 192-byte delivered
        // one, a de-pad that did not happen would put row 1's marker at byte 256
        // instead of 192. Offsets are multiplied out by hand: 0, 192, 384, 576.
        await FrameCapture.WithOneFrameAsync(
            CameraPixelFormat.Bgr24, 64, 32, CameraFramePatterns.RowIndex,
            frame =>
            {
                var bytes = frame.ContiguousBuffer.Span;

                Assert.Equal(0, bytes[0]);
                Assert.Equal(1, bytes[192]);
                Assert.Equal(2, bytes[384]);
                Assert.Equal(3, bytes[576]);
                Assert.Equal(31, bytes[5952]);

                // Nothing lives between the markers.
                Assert.Equal(0, bytes[1]);
                Assert.Equal(0, bytes[191]);
            },
            overrideStride: 256);
    }

    private static int OffsetOf(ICameraFrame frame, CameraPlane plane)
    {
        Assert.True(
            MemoryMarshal.TryGetArray(frame.ContiguousBuffer, out var whole),
            "The frame's buffer is not array-backed, so plane offsets cannot be read.");
        Assert.True(
            MemoryMarshal.TryGetArray(plane.Buffer, out var segment),
            "The plane's buffer is not array-backed, so its offset cannot be read.");
        Assert.Same(whole.Array, segment.Array);

        return segment.Offset - whole.Offset;
    }
}
