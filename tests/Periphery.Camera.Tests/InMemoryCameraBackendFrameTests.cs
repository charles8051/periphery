using Periphery.Camera.Internal;
using Periphery.Camera.Testing;

namespace Periphery.Camera.Tests;

/// <summary>
/// Covers what <see cref="InMemoryCameraBackend"/> puts in a frame: its geometry
/// against <see cref="CameraFrameLayout"/>, and the three hooks that let a test
/// choose the bytes (#321).
/// </summary>
/// <remarks>
/// The drift guard is the point of the file. The fake shipped its own
/// bytes-per-pixel table that disagreed with production on six of fourteen
/// formats, and nothing failed — a constant-filled buffer of the wrong size
/// satisfies every lifecycle test in the suite. Geometry is therefore asserted
/// against <see cref="CameraFrameLayout"/> for every enum member, so a new format
/// or a changed rate is covered without anyone remembering to add a case.
/// </remarks>
[Collection("Camera")]
public sealed class InMemoryCameraBackendFrameTests
{
    private const int W = 640;
    private const int H = 480;

    public static TheoryData<CameraPixelFormat> AllPixelFormats
    {
        get
        {
            var data = new TheoryData<CameraPixelFormat>();
            foreach (var format in Enum.GetValues<CameraPixelFormat>())
                data.Add(format);
            return data;
        }
    }

    // ── Drift guard: geometry is CameraFrameLayout's, for every format ──

    [Theory]
    [MemberData(nameof(AllPixelFormats))]
    public async Task GeneratedFrame_Geometry_MatchesCameraFrameLayout(CameraPixelFormat format)
    {
        await using var backend = new InMemoryCameraBackend { OverridePixelFormat = format };
        var raw = await ReadOneRawFrameAsync(backend);

        Assert.Equal(CameraFrameLayout.FrameSize(format, W, H), raw.Data.Length);
        Assert.Equal(CameraFrameLayout.PlaneCount(format), raw.PlaneCount);
        Assert.Equal(format, raw.PixelFormat);
    }

    /// <summary>
    /// Every format's descriptors cover the buffer with no gap and no overhang.
    /// A self-consistency check across all fourteen formats — the descriptors and
    /// the buffer come from the same layout code, so the hand-computed NV12 and
    /// I420 expectations below are what pin the offsets themselves.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllPixelFormats))]
    public async Task GeneratedFrame_Planes_TileTheBufferInOrder(CameraPixelFormat format)
    {
        await using var backend = new InMemoryCameraBackend { OverridePixelFormat = format };
        var raw = await ReadOneRawFrameAsync(backend);

        if (raw.Planes is null)
        {
            // ADR-0081 D3: only the two opaque formats may leave Planes null.
            // Every uncompressed frame describes itself per plane, packed
            // single-plane ones included, so the pool never has to infer a
            // layout from one stride.
            Assert.Contains(format, new[] { CameraPixelFormat.Mjpeg, CameraPixelFormat.Unknown });
            Assert.Equal(1, raw.PlaneCount);
            return;
        }

        Assert.Equal(raw.PlaneCount, raw.Planes.Count);
        int expectedOffset = 0;
        foreach (var plane in raw.Planes)
        {
            Assert.Equal(expectedOffset, plane.Offset);
            Assert.Equal(plane.Stride * plane.Height, plane.Length);
            expectedOffset += plane.Length;
        }
        Assert.Equal(raw.Data.Length, expectedOffset);
    }

    [Fact]
    public async Task GeneratedFrame_Nv12_IsTwelveBitsPerPixel_NotEight()
    {
        // The bug this fixes, stated as a number: the old generator charged NV12
        // 1 byte/px, so the chroma half of every frame did not exist.
        await using var backend = new InMemoryCameraBackend
        {
            OverridePixelFormat = CameraPixelFormat.Nv12,
        };
        var raw = await ReadOneRawFrameAsync(backend);

        Assert.Equal(460_800, raw.Data.Length);
        Assert.Equal(2, raw.PlaneCount);
    }

    // ── Independent oracle for the 4:2:0 layouts ───────────────────────

    // The theory above and the backend both get their descriptors from
    // PlaneLayout, so a regression inside PlaneLayout would satisfy both
    // (Peanut Gallery turn 1). These two pin the layouts to numbers worked out
    // from the format definitions instead. 4:2:0 chroma is quarter-resolution,
    // so at 640x480: luma 640 x 480 = 307 200 bytes; NV12's interleaved UV plane
    // is 320 x 240 samples x 2 bytes = 153 600; I420's U and V are 320 x 240 x 1
    // = 76 800 each.

    [Fact]
    public async Task GeneratedFrame_Nv12_PlanesSitAtTheirDefinedOffsets()
    {
        await using var backend = new InMemoryCameraBackend
        {
            OverridePixelFormat = CameraPixelFormat.Nv12,
        };
        var raw = await ReadOneRawFrameAsync(backend);

        var planes = raw.Planes!;
        Assert.Equal(2, planes.Count);
        AssertPlane(planes[0], offset: 0, length: 307_200, stride: 640, width: 640, height: 480);
        AssertPlane(planes[1], offset: 307_200, length: 153_600, stride: 640, width: 320, height: 240);
    }

    [Fact]
    public async Task GeneratedFrame_I420_PlaneBytesLandAtTheirDefinedOffsets()
    {
        // Asserted on the bytes, not on the descriptors: a swapped plane order or
        // a chroma offset off by one plane fails here even if the metadata agrees
        // with itself.
        await using var backend = new InMemoryCameraBackend
        {
            OverridePixelFormat = CameraPixelFormat.I420,
            FrameFactory = CameraFramePatterns.PlaneConstant(0x11, 0x22, 0x33),
        };
        var raw = await ReadOneRawFrameAsync(backend);

        var planes = raw.Planes!;
        Assert.Equal(3, planes.Count);
        AssertPlane(planes[0], offset: 0, length: 307_200, stride: 640, width: 640, height: 480);
        AssertPlane(planes[1], offset: 307_200, length: 76_800, stride: 320, width: 320, height: 240);
        AssertPlane(planes[2], offset: 384_000, length: 76_800, stride: 320, width: 320, height: 240);

        var data = raw.Data.ToArray();
        Assert.Equal(460_800, data.Length);
        Assert.Equal((byte)0x11, data[0]);
        Assert.Equal((byte)0x11, data[307_199]);
        Assert.Equal((byte)0x22, data[307_200]);
        Assert.Equal((byte)0x22, data[383_999]);
        Assert.Equal((byte)0x33, data[384_000]);
        Assert.Equal((byte)0x33, data[460_799]);
    }

    // ── Default behaviour: unchanged bytes ─────────────────────────────

    [Fact]
    public async Task Default_FillsEveryByteWithTheFrameIndex()
    {
        await using var backend = new InMemoryCameraBackend();
        ICameraBackend io = backend;
        await OpenAndStartAsync(io);

        for (int expected = 1; expected <= 3; expected++)
        {
            var raw = await io.ReadRawFrameAsync(default);
            Assert.Equal(W * H * 2, raw.Data.Length);   // YUY2, 16 bits/px
            Assert.All(raw.Data.ToArray(), b => Assert.Equal((byte)expected, b));
        }
    }

    // ── FrameFactory ───────────────────────────────────────────────────

    [Fact]
    public async Task FrameFactory_SuppliesTheDeliveredBytes()
    {
        await using var backend = new InMemoryCameraBackend
        {
            // Known bytes at known offsets: the capability the constant fill
            // could not express.
            FrameFactory = spec =>
            {
                var data = new byte[spec.FrameSize];
                data[0] = 0xDE;
                data[^1] = 0xAD;
                return data;
            },
        };

        await using var session = await CameraTestHarness.OpenSessionAsync(backend);
        await session.StartCaptureAsync();
        using var frame = await session.ReadFrameAsync();

        var pixels = frame.ContiguousBuffer.ToArray();
        Assert.Equal((byte)0xDE, pixels[0]);
        Assert.Equal((byte)0xAD, pixels[^1]);
    }

    [Fact]
    public async Task FrameFactory_SeesTheFrameIndexAndFormat()
    {
        var seen = new List<CameraFrameSpec>();
        await using var backend = new InMemoryCameraBackend
        {
            OverridePixelFormat = CameraPixelFormat.I420,
            FrameFactory = spec =>
            {
                seen.Add(spec);
                return new byte[spec.FrameSize];
            },
        };
        ICameraBackend io = backend;
        await OpenAndStartAsync(io);

        await io.ReadRawFrameAsync(default);
        await io.ReadRawFrameAsync(default);

        Assert.Equal(new[] { 1, 2 }, seen.Select(s => s.FrameIndex).ToArray());
        Assert.All(seen, s =>
        {
            Assert.Equal(CameraPixelFormat.I420, s.PixelFormat);
            Assert.Equal(W, s.Width);
            Assert.Equal(H, s.Height);
            Assert.Equal(W, s.Stride);
            Assert.Equal(3, s.PlaneCount);
        });
    }

    [Fact]
    public async Task FrameFactory_WrongLength_ThrowsNamingBothSizes()
    {
        await using var backend = new InMemoryCameraBackend
        {
            FrameFactory = _ => new byte[7],
        };
        ICameraBackend io = backend;
        await OpenAndStartAsync(io);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await io.ReadRawFrameAsync(default));
        Assert.Contains("7 bytes", ex.Message);
        Assert.Contains("614400", ex.Message);
    }

    // ── Built-in patterns ──────────────────────────────────────────────

    [Fact]
    public async Task RowIndex_PutsTheRowNumberAtEveryRowStart()
    {
        await using var backend = new InMemoryCameraBackend
        {
            OverridePixelFormat = CameraPixelFormat.Gray8,
            FrameFactory = CameraFramePatterns.RowIndex,
        };
        var raw = await ReadOneRawFrameAsync(backend);

        var data = raw.Data.ToArray();
        for (int row = 0; row < H; row++)
            Assert.Equal((byte)(row & 0xFF), data[row * W]);
    }

    [Fact]
    public async Task HorizontalGradient_RampsAcrossEachRow()
    {
        await using var backend = new InMemoryCameraBackend
        {
            OverridePixelFormat = CameraPixelFormat.Gray8,
            FrameFactory = CameraFramePatterns.HorizontalGradient,
        };
        var raw = await ReadOneRawFrameAsync(backend);

        var data = raw.Data.ToArray();
        for (int col = 0; col < W; col++)
            Assert.Equal((byte)(col & 0xFF), data[(5 * W) + col]);
    }

    [Fact]
    public async Task PlaneConstant_FillsEachNv12PlaneWithItsOwnValue()
    {
        await using var backend = new InMemoryCameraBackend
        {
            OverridePixelFormat = CameraPixelFormat.Nv12,
            FrameFactory = CameraFramePatterns.PlaneConstant(0x10, 0x80),
        };

        await using var session = await CameraTestHarness.OpenSessionAsync(backend);
        await session.StartCaptureAsync();
        using var frame = await session.ReadFrameAsync();

        Assert.Equal(2, frame.PlaneCount);
        Assert.All(frame.GetPlane(0).Buffer.ToArray(), b => Assert.Equal((byte)0x10, b));
        Assert.All(frame.GetPlane(1).Buffer.ToArray(), b => Assert.Equal((byte)0x80, b));
    }

    [Fact]
    public async Task PlaneConstant_WrongPlaneCount_Throws()
    {
        await using var backend = new InMemoryCameraBackend
        {
            OverridePixelFormat = CameraPixelFormat.I420,
            FrameFactory = CameraFramePatterns.PlaneConstant(0x10, 0x80),
        };
        ICameraBackend io = backend;
        await OpenAndStartAsync(io);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await io.ReadRawFrameAsync(default));
        Assert.Contains("3 plane(s)", ex.Message);
    }

    [Fact]
    public void PlaneConstant_NoValues_Throws()
        => Assert.Throws<ArgumentException>(() => CameraFramePatterns.PlaneConstant());

    // ── OverrideStride ─────────────────────────────────────────────────

    [Fact]
    public async Task OverrideStride_ProducesPaddedRowsAtTheStride()
    {
        // The fixture #320 needs: rows padded to a 64-byte boundary, the way
        // Media Foundation pads NV12 luma on real hardware (640 -> 704 here).
        // Asserted on the raw frame, because that is where the padding exists —
        // the pool now removes it on the way out (ADR-0081 D1), and what a
        // session delivers from this same backend is TightRowInvariantTests'
        // subject.
        const int PaddedStride = 704;
        const int YPlaneSize = PaddedStride * H;        // 337 920
        await using var backend = new InMemoryCameraBackend
        {
            OverridePixelFormat = CameraPixelFormat.Nv12,
            OverrideStride = PaddedStride,
            FrameFactory = CameraFramePatterns.RowIndex,
        };
        var raw = await ReadOneRawFrameAsync(backend);

        // Hand-computed rather than asked of CameraFrameLayout: 704 x 480 luma
        // plus 704 x 240 chroma is 506 880 bytes, against a tight 460 800.
        Assert.Equal(506_880, raw.Data.Length);
        Assert.True(
            raw.Data.Length > CameraFrameLayout.FrameSize(CameraPixelFormat.Nv12, W, H),
            "a padded frame must be larger than a tight one");
        AssertPlane(raw.Planes![0], offset: 0, length: YPlaneSize,
            stride: PaddedStride, width: W, height: H);
        AssertPlane(raw.Planes[1], offset: YPlaneSize, length: 168_960,
            stride: PaddedStride, width: 320, height: 240);

        // Sizes and strides alone would still pass if the rows were written at
        // the tight pitch inside a padded allocation (Peanut Gallery turn 2), so
        // walk the row markers. Both planes, at literal offsets.
        var data = raw.Data.ToArray();
        foreach (int row in new[] { 0, 1, 2, 255, 256, 479 })
            Assert.Equal((byte)(row & 0xFF), data[row * PaddedStride]);
        foreach (int row in new[] { 0, 1, 239 })
            Assert.Equal((byte)row, data[YPlaneSize + (row * PaddedStride)]);

        // The tight-pitch position of row 1 is padding, and RowIndex leaves
        // padding alone — this is the byte that would read 1 under the bug above.
        Assert.Equal((byte)0, data[W]);
    }

    [Fact]
    public async Task OverrideStride_WithRowIndex_SkewsAWidthWalkAndNotAStrideWalk()
    {
        // Why the hook exists: walking rows by width instead of stride reads the
        // wrong byte, and only a padded frame can show it.
        const int PaddedStride = 704;
        await using var backend = new InMemoryCameraBackend
        {
            OverridePixelFormat = CameraPixelFormat.Gray8,
            OverrideStride = PaddedStride,
            FrameFactory = CameraFramePatterns.RowIndex,
        };
        var raw = await ReadOneRawFrameAsync(backend);
        var data = raw.Data.ToArray();

        Assert.Equal(PaddedStride * H, raw.Data.Length);
        for (int row = 0; row < H; row++)
            Assert.Equal((byte)(row & 0xFF), data[row * PaddedStride]);

        // The same walk at width instead of stride lands in the middle of row 1,
        // which RowIndex leaves at zero.
        Assert.Equal((byte)0, data[1 * W]);
    }

    [Fact]
    public async Task OverrideStride_NarrowerThanNatural_Throws()
    {
        await using var backend = new InMemoryCameraBackend { OverrideStride = 320 };
        ICameraBackend io = backend;
        await OpenAndStartAsync(io);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await io.ReadRawFrameAsync(default));
        Assert.Contains("narrower", ex.Message);
    }

    [Fact]
    public async Task OverrideStride_OnMjpeg_IsIgnoredRatherThanValidated()
    {
        // A compressed blob has no rows for a stride to describe, so the hook is
        // documented as ignored for MJPEG — including the natural-stride floor,
        // which would otherwise reject a value on a format the hook never reaches
        // (Peanut Gallery turn 1).
        await using var backend = new InMemoryCameraBackend
        {
            OverridePixelFormat = CameraPixelFormat.Mjpeg,
            OverrideStride = 1,
        };
        var raw = await ReadOneRawFrameAsync(backend);

        Assert.Equal(CameraFrameLayout.FrameSize(CameraPixelFormat.Mjpeg, W, H), raw.Data.Length);
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private static void AssertPlane(
        RawPlaneDescriptor plane, int offset, int length, int stride, int width, int height)
    {
        Assert.Equal(offset, plane.Offset);
        Assert.Equal(length, plane.Length);
        Assert.Equal(stride, plane.Stride);
        Assert.Equal(width, plane.Width);
        Assert.Equal(height, plane.Height);
    }

    private static async Task OpenAndStartAsync(ICameraBackend io)
    {
        await io.OpenAsync(default);
        await io.ConfigureAsync(new CameraConfiguration(CameraTestFormats.Vga), default);
        await io.StartCaptureAsync(default);
    }

    /// <summary>
    /// Reads one frame straight off the backend. Deliberately below
    /// <c>CameraSession</c>: the raw frame carries the plane descriptors and the
    /// exact generated length, which is what the geometry assertions are about.
    /// </summary>
    private static async Task<RawCameraFrame> ReadOneRawFrameAsync(InMemoryCameraBackend backend)
    {
        ICameraBackend io = backend;
        await OpenAndStartAsync(io);
        return await io.ReadRawFrameAsync(default);
    }
}
