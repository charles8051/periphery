using System.Runtime.InteropServices;
using Periphery.Camera.Internal;
using Periphery.Camera.Testing;

namespace Periphery.Camera.Tests;

/// <summary>
/// ADR-0081: every uncompressed frame the pool delivers has tight rows, whatever
/// the producer's stride and whichever way up it stored them.
/// </summary>
/// <remarks>
/// <para>
/// Every expectation here is hand-derived from the format definitions and
/// asserted at literal byte offsets. Nothing asks <see cref="PlaneLayout"/> or
/// <see cref="CameraFrameLayout"/> what it expects, because those are the code
/// under test and a check that re-derives its expectation from them passes
/// through any change the two make together — which is how the same
/// recompute-vs-carry defect reached production twice.
/// </para>
/// <para>
/// The arithmetic, once, for the 640x480 frames below. Luma is 640 x 480 =
/// 307 200 bytes. 4:2:0 chroma is quarter-resolution: NV12 / NV21 interleave U
/// and V into one 320 x 240-sample plane of 2 bytes each, so 640 x 240 = 153 600;
/// I420 / YV12 split them into two 320 x 240 x 1 = 76 800 planes. Either way the
/// frame is 460 800 bytes. A driver padding luma rows to 704 (the 64-byte
/// boundary Media Foundation rounds to) makes the source 506 880 instead.
/// </para>
/// </remarks>
public sealed class TightRowInvariantTests
{
    private const int W = 640;
    private const int H = 480;
    private const int PaddedStride = 704;
    private const int TightFrameSize = 460_800;
    private const int TightYPlaneSize = 307_200;
    private const int PaddedFrameSize = 506_880;

    // ── D1: what a consumer receives ───────────────────────────────────

    [Fact]
    public async Task PaddedSource_DeliversATightFrame_WithEveryPixelInPlace()
    {
        // HorizontalGradient ramps each row's meaningful bytes 0,1,2,… and leaves
        // the padding at zero, so a copy that carried the padding through shows
        // up as zeros interrupting the ramp rather than as a wrong length.
        await using var backend = new InMemoryCameraBackend
        {
            OverridePixelFormat = CameraPixelFormat.Nv12,
            OverrideStride = PaddedStride,
            FrameFactory = CameraFramePatterns.HorizontalGradient,
        };

        await using var session = await CameraTestHarness.OpenSessionAsync(backend);
        await session.StartCaptureAsync();
        using var frame = await session.ReadFrameAsync();

        Assert.Equal(TightFrameSize, frame.ContiguousBuffer.Length);
        Assert.Equal(2, frame.PlaneCount);

        var y = frame.GetPlane(0);
        Assert.Equal(640, y.Stride);
        Assert.Equal(640, y.Width);
        Assert.Equal(480, y.Height);
        Assert.Equal(TightYPlaneSize, y.Buffer.Length);

        var uv = frame.GetPlane(1);
        Assert.Equal(640, uv.Stride);
        Assert.Equal(320, uv.Width);
        Assert.Equal(240, uv.Height);
        Assert.Equal(153_600, uv.Buffer.Length);

        var data = frame.ContiguousBuffer.ToArray();

        // Row 0 of luma: the ramp runs 0..639 and then row 1 starts over. Byte
        // 641 is the discriminator — tight it is 1, padded it is padding (0).
        Assert.Equal((byte)0, data[0]);
        Assert.Equal((byte)127, data[639]);         // 639 & 0xFF
        Assert.Equal((byte)0, data[640]);           // row 1, column 0
        Assert.Equal((byte)1, data[641]);

        // Chroma starts at 307 200 and ramps the same way, 640 bytes per row.
        Assert.Equal((byte)0, data[307_200]);
        Assert.Equal((byte)1, data[307_201]);
        Assert.Equal((byte)127, data[307_839]);     // last byte of chroma row 0
        Assert.Equal((byte)0, data[307_840]);       // chroma row 1, column 0
        Assert.Equal((byte)127, data[460_799]);     // last byte of the frame

        // And the whole thing, against the pattern's documented contract rather
        // than against anything the pool computed. Scanned rather than asserted
        // per byte: 460 800 xUnit assertions cost more than the rest of the
        // suite combined.
        for (int i = 0; i < data.Length; i++)
        {
            if (data[i] == (byte)(i % 640)) continue;
            Assert.Fail($"byte {i} is 0x{data[i]:X2}, expected 0x{i % 640:X2}");
        }
    }

    [Theory]
    [InlineData(CameraPixelFormat.Gray8, 640, 704)]     // 1 byte/px
    [InlineData(CameraPixelFormat.Yuy2, 1280, 1344)]    // 4:2:2 packed, 2 bytes/px
    [InlineData(CameraPixelFormat.Uyvy, 1280, 1344)]
    [InlineData(CameraPixelFormat.Gray16, 1280, 1344)]
    [InlineData(CameraPixelFormat.Rgb24, 1920, 1984)]   // 3 bytes/px
    [InlineData(CameraPixelFormat.Bgr24, 1920, 1984)]
    [InlineData(CameraPixelFormat.Bgra32, 2560, 2624)]  // 4 bytes/px
    [InlineData(CameraPixelFormat.Rgba32, 2560, 2624)]
    [InlineData(CameraPixelFormat.Argb32, 2560, 2624)]
    public async Task PaddedPackedSource_DeliversATightFrame(
        CameraPixelFormat format, int tightStride, int paddedStride)
    {
        // #320's titular case: a packed format whose buffer holds padded rows.
        // Its stride used to be recomputed from the width and reported unpadded,
        // so a consumer walking rows read progressively skewed pixels.
        await using var backend = new InMemoryCameraBackend
        {
            OverridePixelFormat = format,
            OverrideStride = paddedStride,
            FrameFactory = CameraFramePatterns.RowIndex,
        };

        await using var session = await CameraTestHarness.OpenSessionAsync(backend);
        await session.StartCaptureAsync();
        using var frame = await session.ReadFrameAsync();

        Assert.Equal(tightStride * H, frame.ContiguousBuffer.Length);
        Assert.Equal(1, frame.PlaneCount);
        Assert.True(frame.IsContiguous);
        Assert.Equal(tightStride, frame.GetPlane(0).Stride);

        // RowIndex writes the row number into each row's first byte. Walking the
        // delivered buffer at the tight stride must find every one of them; at
        // the source's padded stride it would not.
        var data = frame.ContiguousBuffer.ToArray();
        for (int row = 0; row < H; row++)
            Assert.Equal((byte)(row & 0xFF), data[row * tightStride]);
        Assert.Equal((byte)0, data[1]);
    }

    [Theory]
    [InlineData(CameraPixelFormat.Nv12)]
    [InlineData(CameraPixelFormat.Nv21)]
    public async Task PaddedSemiPlanarSource_PutsBothPlanesAtTheirTightOffsets(
        CameraPixelFormat format)
    {
        // PlaneConstant fills only the meaningful bytes of each row, so any
        // padding that survived the copy reads back as 0x00 sitting between the
        // plane constants.
        await using var backend = new InMemoryCameraBackend
        {
            OverridePixelFormat = format,
            OverrideStride = PaddedStride,
            FrameFactory = CameraFramePatterns.PlaneConstant(0x11, 0x22),
        };

        await using var session = await CameraTestHarness.OpenSessionAsync(backend);
        await session.StartCaptureAsync();
        using var frame = await session.ReadFrameAsync();

        var data = frame.ContiguousBuffer.ToArray();
        Assert.Equal(TightFrameSize, data.Length);
        Assert.Equal((byte)0x11, data[0]);
        Assert.Equal((byte)0x11, data[307_199]);
        Assert.Equal((byte)0x22, data[307_200]);
        Assert.Equal((byte)0x22, data[460_799]);
        Assert.DoesNotContain((byte)0x00, data);

        Assert.Equal(TightYPlaneSize, frame.GetPlane(0).Buffer.Length);
        Assert.Equal(640, frame.GetPlane(0).Stride);
        Assert.Equal(153_600, frame.GetPlane(1).Buffer.Length);
        // Chroma keeps the luma stride: 320 samples of interleaved U and V, two
        // bytes each, is 640 bytes per row.
        Assert.Equal(640, frame.GetPlane(1).Stride);
        Assert.Equal(320, frame.GetPlane(1).Width);
    }

    [Theory]
    [InlineData(CameraPixelFormat.I420)]
    [InlineData(CameraPixelFormat.Yv12)]
    public async Task PaddedPlanarSource_PutsAllThreePlanesAtTheirTightOffsets(
        CameraPixelFormat format)
    {
        await using var backend = new InMemoryCameraBackend
        {
            OverridePixelFormat = format,
            OverrideStride = PaddedStride,
            FrameFactory = CameraFramePatterns.PlaneConstant(0x11, 0x22, 0x33),
        };

        await using var session = await CameraTestHarness.OpenSessionAsync(backend);
        await session.StartCaptureAsync();
        using var frame = await session.ReadFrameAsync();

        var data = frame.ContiguousBuffer.ToArray();
        Assert.Equal(TightFrameSize, data.Length);
        Assert.Equal((byte)0x11, data[0]);
        Assert.Equal((byte)0x11, data[307_199]);
        Assert.Equal((byte)0x22, data[307_200]);
        Assert.Equal((byte)0x22, data[383_999]);
        Assert.Equal((byte)0x33, data[384_000]);
        Assert.Equal((byte)0x33, data[460_799]);
        Assert.DoesNotContain((byte)0x00, data);

        Assert.Equal(3, frame.PlaneCount);
        Assert.Equal(640, frame.GetPlane(0).Stride);
        // Chroma is half-width and one byte per sample, so 320 bytes per row —
        // the source's 704-byte luma stride halved to 352 in the buffer that
        // arrived, and neither number survives to the consumer.
        Assert.Equal(320, frame.GetPlane(1).Stride);
        Assert.Equal(320, frame.GetPlane(2).Stride);
        Assert.Equal(76_800, frame.GetPlane(1).Buffer.Length);
        Assert.Equal(76_800, frame.GetPlane(2).Buffer.Length);
    }

    // ── The pool seed is now exact ─────────────────────────────────────

    [Fact]
    public void PaddedSource_FitsTheTightSeed_WithoutReallocating()
    {
        // CameraSession seeds at CameraFrameLayout.FrameSize, i.e. tight. Before
        // the de-pad, every padded mode overshot that seed and allocated a fresh
        // buffer per frame — 2.3 MB at NV12 1440x1080 on hardware measured for
        // ADR-0081 — on a path whose design intent is zero steady-state
        // allocation. The seeded array coming back out is what says it did not.
        var pool = new CameraFramePool();
        pool.Seed(TightFrameSize, bufferCount: 1);

        using var frame = pool.TryDeliver(PaddedNv12Source(CameraFramePatterns.PlaneConstant(0x11, 0x22)));

        Assert.NotNull(frame);
        Assert.True(MemoryMarshal.TryGetArray(frame!.ContiguousBuffer, out var segment));
        Assert.Equal(TightFrameSize, segment.Array!.Length);
    }

    // ── D2: the bulk fast path, and the two frames it must refuse ──────

    [Fact]
    public void TightTopDownSource_TakesTheBulkCopy()
    {
        var pool = new CameraFramePool();
        pool.Seed(TightFrameSize, bufferCount: 2);

        using (var frame = pool.TryDeliver(TightNv12Source(CameraFramePatterns.PlaneConstant(0x11, 0x22))))
            Assert.NotNull(frame);

        Assert.Equal(1L, pool.BulkCopies);
        Assert.Equal(0L, pool.RowCopies);
    }

    [Fact]
    public void PaddedSource_TakesTheRowLoop()
    {
        var pool = new CameraFramePool();
        pool.Seed(TightFrameSize, bufferCount: 2);

        using (var frame = pool.TryDeliver(PaddedNv12Source(CameraFramePatterns.PlaneConstant(0x11, 0x22))))
            Assert.NotNull(frame);

        Assert.Equal(0L, pool.BulkCopies);
        Assert.Equal(1L, pool.RowCopies);
    }

    [Fact]
    public void TightSourceWithAnInterPlaneGap_IsNotBulkCopied()
    {
        // ADR-0081 D2, hazard one: both strides are tight, but the chroma plane
        // sits 4 096 bytes past the end of the luma plane. A bulk copy would
        // transplant that gap into a destination laid out without one and
        // displace every chroma row — silently, since the result is the right
        // length and the strides are the right numbers.
        const int Gap = 4096;
        var data = new byte[TightYPlaneSize + Gap + 153_600];
        data.AsSpan(0, TightYPlaneSize).Fill(0x11);
        data.AsSpan(TightYPlaneSize, Gap).Fill(0xEE);
        data.AsSpan(TightYPlaneSize + Gap, 153_600).Fill(0x22);

        var raw = new RawCameraFrame
        {
            Data = data,
            Width = W,
            Height = H,
            PixelFormat = CameraPixelFormat.Nv12,
            Timestamp = TimeSpan.Zero,
            PlaneCount = 2,
            Planes =
            [
                new RawPlaneDescriptor
                {
                    Offset = 0, Length = TightYPlaneSize, Stride = 640, Width = 640, Height = 480,
                },
                new RawPlaneDescriptor
                {
                    Offset = TightYPlaneSize + Gap, Length = 153_600,
                    Stride = 640, Width = 320, Height = 240,
                },
            ],
        };

        var pool = new CameraFramePool();
        pool.Seed(TightFrameSize, bufferCount: 1);
        using var frame = pool.TryDeliver(in raw);

        Assert.NotNull(frame);
        Assert.Equal(0L, pool.BulkCopies);

        var delivered = frame!.ContiguousBuffer.ToArray();
        Assert.Equal(TightFrameSize, delivered.Length);
        // The byte a bulk copy would have made 0xEE.
        Assert.Equal((byte)0x22, delivered[307_200]);
        Assert.Equal((byte)0x11, delivered[307_199]);
        Assert.Equal((byte)0x22, delivered[460_799]);
        Assert.DoesNotContain((byte)0xEE, delivered);
    }

    [Fact]
    public void BottomUpSourceAtTheTightPitch_IsNotBulkCopied()
    {
        // ADR-0081 D2, hazard two: |pitch| equals the tight row width, so every
        // stride test says "tight", and a bulk copy would deliver the image
        // upside down. Each stored row is filled with the index of the image row
        // it holds, and storage is bottom-up, so stored row r carries image row
        // 479 - r.
        var data = new byte[W * H];
        for (int stored = 0; stored < H; stored++)
            data.AsSpan(stored * W, W).Fill((byte)((H - 1 - stored) & 0xFF));

        var raw = new RawCameraFrame
        {
            Data = data,
            Width = W,
            Height = H,
            PixelFormat = CameraPixelFormat.Gray8,
            Timestamp = TimeSpan.Zero,
            PlaneCount = 1,
            Planes =
            [
                new RawPlaneDescriptor
                {
                    Offset = 0, Length = W * H, Stride = W, Width = W, Height = H,
                },
            ],
            BottomUp = true,
        };

        var pool = new CameraFramePool();
        pool.Seed(W * H, bufferCount: 1);
        using var frame = pool.TryDeliver(in raw);

        Assert.NotNull(frame);
        Assert.Equal(0L, pool.BulkCopies);

        var delivered = frame!.ContiguousBuffer.ToArray();
        // Image row 0 first. A bulk copy would leave 223 here (479 & 0xFF).
        Assert.Equal((byte)0, delivered[0]);
        Assert.Equal((byte)1, delivered[640]);
        Assert.Equal((byte)223, delivered[479 * 640]);
        for (int row = 0; row < H; row++)
            Assert.Equal((byte)(row & 0xFF), delivered[row * W]);
    }

    // ── D8: a bottom-up 4:2:0 frame keeps its chroma ───────────────────

    [Fact]
    public void BottomUpNv12Source_FlipsBothPlanes()
    {
        // The latent bug ADR-0081 D8 names: the flip used to run in the Media
        // Foundation backend over exactly `height` rows, which is the whole
        // image for RGB and two thirds of a 4:2:0 frame, so a bottom-up NV12
        // frame kept the previous frame's chroma out of the reused buffer.
        // Luma image row i is filled with i & 0xFF; chroma image row j with
        // (255 - j), which no luma row can produce below row 240 and which is
        // never zero — a plane that went uncopied reads back as zeros.
        var data = new byte[TightFrameSize];
        for (int stored = 0; stored < H; stored++)
            data.AsSpan(stored * 640, 640).Fill((byte)((H - 1 - stored) & 0xFF));
        for (int stored = 0; stored < 240; stored++)
            data.AsSpan(TightYPlaneSize + (stored * 640), 640).Fill((byte)(255 - (239 - stored)));

        var raw = new RawCameraFrame
        {
            Data = data,
            Width = W,
            Height = H,
            PixelFormat = CameraPixelFormat.Nv12,
            Timestamp = TimeSpan.Zero,
            PlaneCount = 2,
            Planes =
            [
                new RawPlaneDescriptor
                {
                    Offset = 0, Length = TightYPlaneSize, Stride = 640, Width = 640, Height = 480,
                },
                new RawPlaneDescriptor
                {
                    Offset = TightYPlaneSize, Length = 153_600,
                    Stride = 640, Width = 320, Height = 240,
                },
            ],
            BottomUp = true,
        };

        var pool = new CameraFramePool();
        pool.Seed(TightFrameSize, bufferCount: 1);
        using var frame = pool.TryDeliver(in raw);

        var delivered = frame!.ContiguousBuffer.ToArray();
        Assert.Equal((byte)0, delivered[0]);                    // luma image row 0
        Assert.Equal((byte)223, delivered[479 * 640]);          // luma image row 479
        Assert.Equal((byte)255, delivered[307_200]);            // chroma image row 0
        Assert.Equal((byte)16, delivered[307_200 + (239 * 640)]); // chroma image row 239
        Assert.Equal((byte)16, delivered[460_799]);

        for (int row = 0; row < 240; row++)
            Assert.Equal((byte)(255 - row), delivered[307_200 + (row * 640)]);
    }

    [Fact]
    public void BottomUpPaddedSource_FlipsAndDePadsInOnePass()
    {
        // The two normalisations compose: a padded, bottom-up frame comes out
        // tight and the right way up.
        var data = new byte[PaddedFrameSize];
        for (int stored = 0; stored < H; stored++)
            data.AsSpan(stored * PaddedStride, 640).Fill((byte)((H - 1 - stored) & 0xFF));

        var raw = new RawCameraFrame
        {
            Data = data,
            Width = W,
            Height = H,
            PixelFormat = CameraPixelFormat.Nv12,
            Timestamp = TimeSpan.Zero,
            PlaneCount = 2,
            Planes = PlaneLayout.DescribePlanes(CameraPixelFormat.Nv12, W, H, PaddedStride),
            BottomUp = true,
        };

        var pool = new CameraFramePool();
        pool.Seed(TightFrameSize, bufferCount: 1);
        using var frame = pool.TryDeliver(in raw);

        var delivered = frame!.ContiguousBuffer.ToArray();
        Assert.Equal(TightFrameSize, delivered.Length);
        for (int row = 0; row < H; row++)
        {
            Assert.Equal((byte)(row & 0xFF), delivered[row * 640]);
            Assert.Equal((byte)(row & 0xFF), delivered[(row * 640) + 639]);
        }
    }

    // ── D5 / D7: MJPEG is exempt and contiguous ────────────────────────

    [Fact]
    public void MjpegFrame_IsDeliveredVerbatimAndReportsContiguous()
    {
        // A compressed frame is one opaque run: no rows, no stride, nothing to
        // de-pad, and a length that is whatever the encoder produced rather than
        // anything CameraFrameLayout can predict.
        var blob = new byte[1237];
        for (int i = 0; i < blob.Length; i++)
            blob[i] = (byte)(i & 0xFF);

        var raw = new RawCameraFrame
        {
            Data = blob,
            Width = 1280,
            Height = 720,
            PixelFormat = CameraPixelFormat.Mjpeg,
            Timestamp = TimeSpan.Zero,
            PlaneCount = 1,
            Planes = null,
        };

        var pool = new CameraFramePool();
        pool.Seed(frameSize: 4096, bufferCount: 1);
        using var frame = pool.TryDeliver(in raw);

        Assert.NotNull(frame);
        Assert.Equal(1, frame!.PlaneCount);
        Assert.True(frame.IsContiguous);
        Assert.Equal(1237, frame.ContiguousBuffer.Length);
        Assert.Equal(blob, frame.ContiguousBuffer.ToArray());
        Assert.Equal(1L, pool.BulkCopies);
    }

    [Fact]
    public async Task MultiPlaneFrame_IsNotContiguous()
    {
        await using var backend = new InMemoryCameraBackend
        {
            OverridePixelFormat = CameraPixelFormat.I420,
        };

        await using var session = await CameraTestHarness.OpenSessionAsync(backend);
        await session.StartCaptureAsync();
        using var frame = await session.ReadFrameAsync();

        Assert.Equal(3, frame.PlaneCount);
        Assert.False(frame.IsContiguous);
    }

    // ── A source that cannot be read as its own format ─────────────────

    [Fact]
    public void Plan_SourceStrideNarrowerThanTheRow_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => FrameCopy.Plan(
            CameraPixelFormat.Bgra32, W, H, sourceLength: W * H * 4,
            sourcePlanes:
            [
                new RawPlaneDescriptor
                {
                    // 640 BGRA pixels are 2560 bytes, not 1280.
                    Offset = 0, Length = 1280 * H, Stride = 1280, Width = W, Height = H,
                },
            ],
            bottomUp: false));

        Assert.Contains("2560 bytes per row", ex.Message);
    }

    [Fact]
    public void Plan_SourcePlaneRunningPastTheBuffer_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => FrameCopy.Plan(
            CameraPixelFormat.Nv12, W, H, sourceLength: TightFrameSize,
            sourcePlanes: PlaneLayout.DescribePlanes(CameraPixelFormat.Nv12, W, H, PaddedStride),
            bottomUp: false));

        Assert.Contains("bytes 337920..506880 of the 460800", ex.Message);
    }

    [Theory]
    [InlineData(CameraPixelFormat.Nv12, 641, 480)]
    [InlineData(CameraPixelFormat.Nv21, 640, 481)]
    [InlineData(CameraPixelFormat.I420, 641, 481)]
    [InlineData(CameraPixelFormat.Yv12, 640, 481)]
    public void Plan_Odd420Dimensions_Throws(CameraPixelFormat format, int width, int height)
    {
        // 4:2:0 chroma is half-resolution in both axes and every layout here
        // floors that division, so an odd dimension has no answer both the plane
        // extents and the frame size agree on. Neither UVC nor either backend can
        // negotiate one; rejecting names the dimension where the tight-row
        // invariant would only say the numbers did not add up (Peanut Gallery
        // turn 1).
        var ex = Assert.Throws<ArgumentException>(() => FrameCopy.Plan(
            format, width, height,
            sourceLength: width * height * 3 / 2,
            sourcePlanes: null,
            bottomUp: false));

        Assert.Contains($"{width}x{height}", ex.Message);
    }

    [Fact]
    public void Plan_SourcePlaneShorterThanItsOwnRows_Throws()
    {
        // The descriptor claims a 200 000-byte extent for 480 rows of 640, which
        // is 307 200. Without the check the copy reads the missing 107 200 bytes
        // out of whatever plane follows.
        var ex = Assert.Throws<ArgumentException>(() => FrameCopy.Plan(
            CameraPixelFormat.Gray8, W, H, sourceLength: W * H,
            sourcePlanes:
            [
                new RawPlaneDescriptor
                {
                    Offset = 0, Length = 200_000, Stride = W, Width = W, Height = H,
                },
            ],
            bottomUp: false));

        Assert.Contains("307200 bytes for 480 rows", ex.Message);
        Assert.Contains("200000-byte extent", ex.Message);
    }

    [Fact]
    public void Plan_SourcePlaneClaimingBytesTheBufferDoesNotHave_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => FrameCopy.Plan(
            CameraPixelFormat.Gray8, W, H, sourceLength: 1000,
            sourcePlanes:
            [
                new RawPlaneDescriptor
                {
                    Offset = 0, Length = W * H, Stride = W, Width = W, Height = H,
                },
            ],
            bottomUp: false));

        Assert.Contains("bytes 0..307200 of the 1000", ex.Message);
    }

    [Fact]
    public void PlanningFailure_LeavesThePoolItsBuffers()
    {
        // Plan runs before the dequeue. A backend emitting malformed metadata
        // transiently must not cost a buffer per frame until the pool starts
        // dropping (Peanut Gallery turn 1).
        var pool = new CameraFramePool();
        pool.Seed(TightFrameSize, bufferCount: 1);

        var malformed = new RawCameraFrame
        {
            Data = new byte[TightFrameSize],
            Width = W,
            Height = H,
            PixelFormat = CameraPixelFormat.Nv12,
            Timestamp = TimeSpan.Zero,
            PlaneCount = 2,
            // One plane where NV12 has two.
            Planes =
            [
                new RawPlaneDescriptor
                {
                    Offset = 0, Length = TightYPlaneSize, Stride = 640, Width = 640, Height = 480,
                },
            ],
        };

        for (int i = 0; i < 5; i++)
            Assert.Throws<ArgumentException>(() => pool.TryDeliver(in malformed));

        // The seeded buffer is still there.
        using var frame = pool.TryDeliver(TightNv12Source(CameraFramePatterns.PlaneConstant(0x11, 0x22)));
        Assert.NotNull(frame);
        Assert.Equal(1, pool.OutstandingLeases);
    }

    [Fact]
    public void Plan_WrongPlaneCount_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => FrameCopy.Plan(
            CameraPixelFormat.I420, W, H, sourceLength: TightFrameSize,
            sourcePlanes: PlaneLayout.DescribePlanes(CameraPixelFormat.Nv12, W, H, W),
            bottomUp: false));

        Assert.Contains("3 plane(s)", ex.Message);
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private static RawCameraFrame TightNv12Source(Func<CameraFrameSpec, byte[]> pattern) =>
        Nv12Source(pattern, stride: W);

    private static RawCameraFrame PaddedNv12Source(Func<CameraFrameSpec, byte[]> pattern) =>
        Nv12Source(pattern, PaddedStride);

    /// <summary>
    /// An NV12 frame at a chosen stride, with its bytes from the same public
    /// pattern generators the fake backend uses. Hand-built rather than read off
    /// <see cref="InMemoryCameraBackend"/> only where a test needs to reach the
    /// pool directly — to watch the copy-path counters, or to describe a source
    /// (an inter-plane gap, bottom-up rows) no backend in this repo produces.
    /// </summary>
    private static RawCameraFrame Nv12Source(Func<CameraFrameSpec, byte[]> pattern, int stride)
    {
        var spec = new CameraFrameSpec(CameraPixelFormat.Nv12, W, H, stride, FrameIndex: 1);
        return new RawCameraFrame
        {
            Data = pattern(spec),
            Width = W,
            Height = H,
            PixelFormat = CameraPixelFormat.Nv12,
            Timestamp = TimeSpan.Zero,
            PlaneCount = 2,
            Planes = PlaneLayout.DescribePlanes(CameraPixelFormat.Nv12, W, H, stride),
        };
    }
}
