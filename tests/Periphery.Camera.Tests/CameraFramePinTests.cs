using System.Buffers;
using System.Runtime.InteropServices;
using Periphery.Camera.Testing;

namespace Periphery.Camera.Tests;

/// <summary>
/// #316: pinning a frame's pixels for a native API without letting the pointer
/// outlive the reference that keeps the buffer alive.
/// </summary>
/// <remarks>
/// <para>
/// Every pixel expectation here is hand-derived from the format definition and
/// asserted at a literal byte offset through <see cref="CameraFramePin.Scan0"/>.
/// Nothing asks <c>CameraFrameLayout</c> or <c>PlaneLayout</c> what it expects:
/// the pin reads its stride and extents from those, so a test that computed its
/// expectations the same way would agree with any change they made together —
/// the failure mode ADR-0081's own tests were written to avoid.
/// </para>
/// <para>
/// The arithmetic, once, for the 640x480 frames below. Gray8 is 1 byte per
/// pixel, so 640 bytes per row and 307 200 per frame. YUY2 is 2, so 1280 and
/// 614 400. BGRA32 is 4, so 2560 and 1 228 800. NV12 is a 640x480 luma plane of
/// 307 200 bytes followed by one interleaved chroma plane of 320x240 two-byte
/// samples, which is 640 bytes per row over 240 rows = 153 600, total 460 800.
/// I420 splits that chroma into two 320-byte-per-row, 240-row planes of 76 800
/// each, so plane 1 starts at 307 200 and plane 2 at 384 000, same 460 800
/// total. A driver padding Gray8 rows to 704 bytes (the 64-byte boundary Media
/// Foundation rounds to) makes the source 337 920, and ADR-0081 D1 says the
/// consumer never sees it.
/// </para>
/// </remarks>
public sealed class CameraFramePinTests
{
    private const int W = 640;
    private const int H = 480;

    private static readonly CameraFormat Bgra32Vga = new(
        W, H, CameraPixelFormat.Bgra32, new Rational(15), new Rational(30),
        CameraTransport.Uncompressed);

    // ── Pixels through the pointer ─────────────────────────────────────

    [Fact]
    public async Task Pin_PackedFrame_ReportsTheRowWidth_AndTheBytesAreReadableAtIt()
    {
        // HorizontalGradient ramps each row's meaningful bytes 0,1,2,… wrapping
        // at 256, so a pointer landing one row off, or a stride one pixel wide,
        // reads a value that is not the one hand-derived below.
        await using var backend = new InMemoryCameraBackend(formats: [Bgra32Vga])
        {
            MaxFrames = 1,
            FrameFactory = CameraFramePatterns.HorizontalGradient,
        };
        await using var session = await CameraTestHarness.OpenSessionAsync(
            backend, new CameraConfiguration(Bgra32Vga));
        await session.StartCaptureAsync();
        using var frame = await session.ReadFrameAsync();

        using var pin = frame.Pin();

        Assert.Equal(2560, pin.Stride);
        Assert.Equal(1_228_800, pin.Length);
        Assert.Equal(W, pin.Width);
        Assert.Equal(H, pin.Height);
        Assert.Equal(CameraPixelFormat.Bgra32, pin.PixelFormat);
        Assert.NotEqual(nint.Zero, pin.Scan0);

        // Row 0 runs 0,1,…,255,0,1,… and ends at column 2559, which is
        // 2559 & 0xFF = 255. Row 1 starts over at 0.
        Assert.Equal(0, Marshal.ReadByte(pin.Scan0, 0));
        Assert.Equal(255, Marshal.ReadByte(pin.Scan0, 255));
        Assert.Equal(0, Marshal.ReadByte(pin.Scan0, 256));
        Assert.Equal(255, Marshal.ReadByte(pin.Scan0, 2559));
        Assert.Equal(0, Marshal.ReadByte(pin.Scan0, 2560));
        Assert.Equal(1, Marshal.ReadByte(pin.Scan0, 2561));

        // Last row starts at 479 * 2560 = 1 226 240 and ends at 1 228 799.
        Assert.Equal(0, Marshal.ReadByte(pin.Scan0, 1_226_240));
        Assert.Equal(255, Marshal.ReadByte(pin.Scan0, 1_228_799));
    }

    [Fact]
    public async Task Pin_PaddedSource_ReportsTheDeliveredStride_NotTheDriversOne()
    {
        // The source pads Gray8 rows from 640 to 704 and leaves the padding at
        // zero. Under ADR-0081 D1 the pool de-pads, so the pin must report 640
        // and byte 641 must be column 1 of row 1 (value 1), not padding (0).
        await using var backend = new InMemoryCameraBackend
        {
            MaxFrames = 1,
            OverridePixelFormat = CameraPixelFormat.Gray8,
            OverrideStride = 704,
            FrameFactory = CameraFramePatterns.HorizontalGradient,
        };
        await using var session = await CameraTestHarness.OpenSessionAsync(backend);
        await session.StartCaptureAsync();
        using var frame = await session.ReadFrameAsync();

        using var pin = frame.Pin();

        Assert.Equal(640, pin.Stride);
        Assert.Equal(307_200, pin.Length);

        Assert.Equal(0, Marshal.ReadByte(pin.Scan0, 0));
        Assert.Equal(127, Marshal.ReadByte(pin.Scan0, 639));   // 639 & 0xFF
        Assert.Equal(0, Marshal.ReadByte(pin.Scan0, 640));     // row 1, column 0
        Assert.Equal(1, Marshal.ReadByte(pin.Scan0, 641));     // padding would be 0
    }

    [Fact]
    public async Task Pin_Mjpeg_ReportsZeroStride_AndTheWholeBlob()
    {
        // FrameIndexConstant fills every byte with the frame index, so frame 1
        // is all 0x01. The fake's MJPEG frame is the worst-case 640*480/2.
        await using var backend = new InMemoryCameraBackend
        {
            MaxFrames = 1,
            OverridePixelFormat = CameraPixelFormat.Mjpeg,
        };
        await using var session = await CameraTestHarness.OpenSessionAsync(backend);
        await session.StartCaptureAsync();
        using var frame = await session.ReadFrameAsync();

        using var pin = frame.Pin();

        // 0 says "there are no rows". The plane underneath reports 640 as a
        // neutral filler; relaying that would hand a caller an invented row
        // width for a compressed blob.
        Assert.Equal(0, pin.Stride);
        Assert.Equal(640, frame.GetPlane(0).Stride);

        Assert.Equal(153_600, pin.Length);
        Assert.Equal(CameraPixelFormat.Mjpeg, pin.PixelFormat);
        Assert.Equal(1, Marshal.ReadByte(pin.Scan0, 0));
        Assert.Equal(1, Marshal.ReadByte(pin.Scan0, 153_599));

        // PinPlane agrees rather than reporting the filler.
        using var planePin = frame.PinPlane(0);
        Assert.Equal(0, planePin.Stride);
        Assert.Equal(153_600, planePin.Length);
    }

    // ── Planes ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(CameraPixelFormat.Nv12)]
    [InlineData(CameraPixelFormat.Nv21)]
    public async Task PinPlane_TwoPlane420_LandsOnEachPlane(CameraPixelFormat format)
    {
        await using var backend = new InMemoryCameraBackend
        {
            MaxFrames = 1,
            OverridePixelFormat = format,
            FrameFactory = CameraFramePatterns.PlaneConstant(0x11, 0x22),
        };
        await using var session = await CameraTestHarness.OpenSessionAsync(backend);
        await session.StartCaptureAsync();
        using var frame = await session.ReadFrameAsync();

        using var whole = frame.Pin();
        using var luma = frame.PinPlane(0);
        using var chroma = frame.PinPlane(1);

        // Whole-frame pin: the luma stride over the tiled 460 800 bytes.
        Assert.Equal(640, whole.Stride);
        Assert.Equal(460_800, whole.Length);
        Assert.Equal(W, whole.Width);
        Assert.Equal(H, whole.Height);

        Assert.Equal(whole.Scan0, luma.Scan0);
        Assert.Equal(640, luma.Stride);
        Assert.Equal(307_200, luma.Length);
        Assert.Equal(640, luma.Width);
        Assert.Equal(480, luma.Height);
        Assert.Equal(0x11, Marshal.ReadByte(luma.Scan0, 0));
        Assert.Equal(0x11, Marshal.ReadByte(luma.Scan0, 307_199));

        // Interleaved chroma: 320 two-byte samples per row, so Width is 320
        // while the row is still 640 bytes wide.
        Assert.Equal(307_200, chroma.Scan0 - whole.Scan0);
        Assert.Equal(640, chroma.Stride);
        Assert.Equal(153_600, chroma.Length);
        Assert.Equal(320, chroma.Width);
        Assert.Equal(240, chroma.Height);
        Assert.Equal(0x22, Marshal.ReadByte(chroma.Scan0, 0));
        Assert.Equal(0x22, Marshal.ReadByte(chroma.Scan0, 153_599));

        // The plane boundary is where PlaneConstant says it is, read through the
        // whole-frame pointer at a literal offset.
        Assert.Equal(0x11, Marshal.ReadByte(whole.Scan0, 307_199));
        Assert.Equal(0x22, Marshal.ReadByte(whole.Scan0, 307_200));
    }

    [Theory]
    [InlineData(CameraPixelFormat.I420)]
    [InlineData(CameraPixelFormat.Yv12)]
    public async Task PinPlane_ThreePlane420_LandsOnEachPlane(CameraPixelFormat format)
    {
        await using var backend = new InMemoryCameraBackend
        {
            MaxFrames = 1,
            OverridePixelFormat = format,
            FrameFactory = CameraFramePatterns.PlaneConstant(0x11, 0x22, 0x33),
        };
        await using var session = await CameraTestHarness.OpenSessionAsync(backend);
        await session.StartCaptureAsync();
        using var frame = await session.ReadFrameAsync();

        using var whole = frame.Pin();
        using var y = frame.PinPlane(0);
        using var u = frame.PinPlane(1);
        using var v = frame.PinPlane(2);

        Assert.Equal(640, whole.Stride);
        Assert.Equal(460_800, whole.Length);

        Assert.Equal(0, y.Scan0 - whole.Scan0);
        Assert.Equal(640, y.Stride);
        Assert.Equal(307_200, y.Length);
        Assert.Equal(0x11, Marshal.ReadByte(y.Scan0, 0));

        // Split chroma: 320 samples per row of one byte each, so the stride
        // halves where the interleaved NV12 plane above kept the full 640.
        Assert.Equal(307_200, u.Scan0 - whole.Scan0);
        Assert.Equal(320, u.Stride);
        Assert.Equal(76_800, u.Length);
        Assert.Equal(320, u.Width);
        Assert.Equal(240, u.Height);
        Assert.Equal(0x22, Marshal.ReadByte(u.Scan0, 0));
        Assert.Equal(0x22, Marshal.ReadByte(u.Scan0, 76_799));

        Assert.Equal(384_000, v.Scan0 - whole.Scan0);
        Assert.Equal(320, v.Stride);
        Assert.Equal(76_800, v.Length);
        Assert.Equal(0x33, Marshal.ReadByte(v.Scan0, 0));
        Assert.Equal(0x33, Marshal.ReadByte(v.Scan0, 76_799));

        Assert.Equal(0x22, Marshal.ReadByte(whole.Scan0, 383_999));
        Assert.Equal(0x33, Marshal.ReadByte(whole.Scan0, 384_000));
    }

    [Fact]
    public async Task PinPlane_IndexZero_IsValidOnASinglePlaneFrame()
    {
        await using var backend = new InMemoryCameraBackend
        {
            MaxFrames = 1,
            FrameFactory = CameraFramePatterns.HorizontalGradient,
        };
        await using var session = await CameraTestHarness.OpenSessionAsync(backend);
        await session.StartCaptureAsync();
        using var frame = await session.ReadFrameAsync();

        Assert.Equal(1, frame.PlaneCount);

        using var whole = frame.Pin();
        using var plane = frame.PinPlane(0);

        // YUY2 VGA: 2 bytes per pixel, one plane spanning the frame.
        Assert.Equal(whole.Scan0, plane.Scan0);
        Assert.Equal(1280, plane.Stride);
        Assert.Equal(614_400, plane.Length);
        Assert.Equal(640, plane.Width);
        Assert.Equal(480, plane.Height);
    }

    [Fact]
    public async Task PinPlane_OutOfRange_Throws()
    {
        await using var backend = new InMemoryCameraBackend { MaxFrames = 1 };
        await using var session = await CameraTestHarness.OpenSessionAsync(backend);
        await session.StartCaptureAsync();
        using var frame = await session.ReadFrameAsync();

        Assert.Throws<ArgumentOutOfRangeException>(() => frame.PinPlane(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => frame.PinPlane(1));

        // The range check runs before the reference is taken, so a rejected call
        // leaves nothing behind for the pool to wait on.
        Assert.Equal(1, frame.RefCount);
    }

    [Fact]
    public async Task Pin_OwnedFrame_Works()
    {
        await using var backend = new InMemoryCameraBackend
        {
            MaxFrames = 1,
            FrameFactory = CameraFramePatterns.HorizontalGradient,
        };
        await using var session = await CameraTestHarness.OpenSessionAsync(backend);
        await session.StartCaptureAsync();
        using var leased = await session.ReadFrameAsync();
        using var owned = leased.Copy();

        using var pin = owned.Pin();

        Assert.Equal(1280, pin.Stride);
        Assert.Equal(614_400, pin.Length);
        Assert.Equal(1, Marshal.ReadByte(pin.Scan0, 1281));
    }

    // ── The reference is genuinely held ────────────────────────────────

    [Fact]
    public async Task Pin_HoldsTheReference_SoDisposingTheFrameDoesNotReturnTheBuffer()
    {
        await using var backend = new InMemoryCameraBackend
        {
            MaxFrames = 1,
            FrameFactory = CameraFramePatterns.HorizontalGradient,
        };
        await using var session = await CameraTestHarness.OpenSessionAsync(backend);
        await session.StartCaptureAsync();
        var frame = await session.ReadFrameAsync();

        Assert.Equal(1, session.Metrics.OutstandingLeases);

        var pin = frame.Pin();
        Assert.Equal(2, frame.RefCount);

        frame.Dispose();

        // CameraFramePool decrements OutstandingLeases in exactly one place, its
        // Return, so 1 here is the assertion "the buffer is still leased" stated
        // against the counter the pool itself maintains.
        Assert.Equal(1, session.Metrics.OutstandingLeases);
        Assert.Equal(1, frame.RefCount);

        // And the pixels are still the ones the pattern wrote. YUY2 row 1
        // column 1: row 0 spans bytes 0..1279, so 1281 is the ramp's second
        // byte of the second row.
        Assert.Equal(0, Marshal.ReadByte(pin.Scan0, 1280));
        Assert.Equal(1, Marshal.ReadByte(pin.Scan0, 1281));

        pin.Dispose();

        Assert.Equal(0, session.Metrics.OutstandingLeases);
        Assert.Equal(0, frame.RefCount);
    }

    [Fact]
    public async Task Pin_AfterTheFramesFinalDispose_ThrowsObjectDisposed()
    {
        await using var backend = new InMemoryCameraBackend { MaxFrames = 1 };
        await using var session = await CameraTestHarness.OpenSessionAsync(backend);
        await session.StartCaptureAsync();
        var frame = await session.ReadFrameAsync();

        using (frame.Pin())
        {
            // Held: the frame's own Dispose drops it to one reference, not zero.
            frame.Dispose();
            Assert.Equal(1, frame.RefCount);
            frame.AddRef().Dispose();
        }

        Assert.Equal(0, frame.RefCount);
        Assert.Throws<ObjectDisposedException>(() => frame.AddRef());
        Assert.Throws<ObjectDisposedException>(() => frame.Pin());
        Assert.Throws<ObjectDisposedException>(() => frame.PinPlane(0));
    }

    [Fact]
    public async Task Pin_DoubleDispose_DoesNotOverReleaseThePooledFrame()
    {
        await using var backend = new InMemoryCameraBackend { MaxFrames = 1 };
        await using var session = await CameraTestHarness.OpenSessionAsync(backend);
        await session.StartCaptureAsync();
        using var frame = await session.ReadFrameAsync();

        var pin = frame.Pin();
        pin.Dispose();
        pin.Dispose();
        pin.Dispose();

        // Without the guard the second Dispose decrements past the frame's own
        // reference, which LeasedCameraFrame throws on in DEBUG and silently
        // corrupts the pool's accounting in RELEASE.
        Assert.Equal(1, frame.RefCount);
        Assert.Equal(1, session.Metrics.OutstandingLeases);
    }

    [Fact]
    public async Task Scan0_AfterDispose_Throws()
    {
        await using var backend = new InMemoryCameraBackend { MaxFrames = 1 };
        await using var session = await CameraTestHarness.OpenSessionAsync(backend);
        await session.StartCaptureAsync();
        using var frame = await session.ReadFrameAsync();

        var pin = frame.Pin();
        pin.Dispose();

        Assert.Throws<ObjectDisposedException>(() => pin.Scan0);

        // The geometry describes the frame rather than the mapping, so it stays
        // readable — a caller can still log what it had.
        Assert.Equal(1280, pin.Stride);
        Assert.Equal(614_400, pin.Length);
    }

    [Fact]
    public void Pin_NullFrame_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => ((ICameraFrame)null!).Pin());
        Assert.Throws<ArgumentNullException>(() => ((ICameraFrame)null!).PinPlane(0));
    }

    // ── Ordering, observed ─────────────────────────────────────────────

    [Fact]
    public void Dispose_UnpinsBeforeDroppingTheReference()
    {
        var log = new List<string>();
        var frame = new RecordingFrame(new byte[16], log);

        var pin = frame.Pin();
        Assert.Equal(["addref", "pin"], log);

        pin.Dispose();

        // Releasing first would let the buffer back into the pool, be refilled
        // by the next frame, and still be held fixed at an address this object
        // hands out. That is the silent-stale-pixels bug the type exists to
        // prevent, so the order is asserted rather than left to a comment.
        Assert.Equal(["addref", "pin", "unpin", "release"], log);
    }

    [Fact]
    public void Dispose_IsIdempotent_AcrossTheHandleAndTheReference()
    {
        var log = new List<string>();
        var frame = new RecordingFrame(new byte[16], log);

        var pin = frame.Pin();
        pin.Dispose();
        pin.Dispose();
        pin.Dispose();

        Assert.Equal(["addref", "pin", "unpin", "release"], log);
    }

    [Fact]
    public void Dispose_WhenUnpinThrows_StillDropsTheReference()
    {
        // A MemoryManager is entitled to throw from Unpin. Before the finally
        // this left the reference held past a guard that then refused to try
        // again, so the pool would have waited on that lease forever (Peanut
        // Gallery turn 1).
        var log = new List<string>();
        var frame = new RecordingFrame(new byte[16], log) { ThrowOnUnpin = true };

        var pin = frame.Pin();

        Assert.Throws<InvalidOperationException>(pin.Dispose);
        Assert.Equal(["addref", "pin", "unpin", "release"], log);
    }

    [Fact]
    public void Pin_WhenConstructionThrowsAfterPinning_UnpinsAndDropsTheReference()
    {
        // The pin succeeds and then a constructor argument throws. Rolling back
        // only the reference would leave the manager pinned with nobody owning
        // the handle (Peanut Gallery turn 1).
        var log = new List<string>();
        var frame = new RecordingFrame(new byte[16], log) { ThrowOnPixelFormatAccess = 2 };

        Assert.Throws<InvalidOperationException>(() => frame.Pin());

        // Access 1 is the stride lookup, before any reference is taken; access 2
        // is the constructor argument, after the region is pinned.
        Assert.Equal(["addref", "pin", "unpin", "release"], log);
    }

    [Fact]
    public void Pin_WhenTheRollbackUnpinAlsoThrows_StillDropsTheReference()
    {
        // Both failures at once: construction throws after the region is pinned,
        // and then the rollback's own Unpin throws. The reference still has to
        // come back, or the pool waits on that lease forever (Peanut Gallery
        // turn 2).
        var log = new List<string>();
        var frame = new RecordingFrame(new byte[16], log)
        {
            ThrowOnPixelFormatAccess = 2,
            ThrowOnUnpin = true,
        };

        Assert.Throws<InvalidOperationException>(() => frame.Pin());

        Assert.Equal(["addref", "pin", "unpin", "release"], log);
    }

    /// <summary>
    /// A frame whose bytes come from a <see cref="MemoryManager{T}"/> rather than
    /// an array, logging every step of the pin protocol.
    /// </summary>
    /// <remarks>
    /// Two jobs. It makes the acquire/release order observable, which nothing
    /// about a pooled frame is. And it exercises the
    /// <see cref="MemoryManager{T}"/>-backed path the V4L2 backend has, which is
    /// why the pin uses <see cref="ReadOnlyMemory{T}.Pin"/> rather than
    /// <c>GCHandle.Alloc</c> — <c>GCHandle</c> cannot pin this at all.
    /// </remarks>
    private sealed class RecordingFrame : ICameraFrame
    {
        private readonly RecordingMemoryManager _manager;
        private readonly List<string> _log;
        private int _pixelFormatReads;

        public RecordingFrame(byte[] data, List<string> log)
        {
            _manager = new RecordingMemoryManager(data, log);
            _log = log;
            Width = data.Length / 4;
            Height = 4;
        }

        /// <summary>Make <see cref="MemoryManager{T}.Unpin"/> throw, as a manager
        /// over a revoked mapping would.</summary>
        public bool ThrowOnUnpin
        {
            get => _manager.ThrowOnUnpin;
            init => _manager.ThrowOnUnpin = value;
        }

        /// <summary>1-based index of the <see cref="PixelFormat"/> read that
        /// should throw, or 0 for none. Lets a test fail the pin's constructor
        /// argument specifically, after the region is already pinned.</summary>
        public int ThrowOnPixelFormatAccess { get; init; }

        public int Width { get; }
        public int Height { get; }
        public TimeSpan Timestamp => TimeSpan.Zero;

        public CameraPixelFormat PixelFormat =>
            ++_pixelFormatReads == ThrowOnPixelFormatAccess
                ? throw new InvalidOperationException("PixelFormat is unavailable.")
                : CameraPixelFormat.Gray8;

        public int PlaneCount => 1;
        public bool IsContiguous => true;
        public ReadOnlyMemory<byte> ContiguousBuffer => _manager.Memory;

        public CameraPlane GetPlane(int index) =>
            index == 0
                ? new CameraPlane(_manager.Memory, Width, Width, Height)
                : throw new ArgumentOutOfRangeException(nameof(index));

        public ICameraFrame AddRef()
        {
            _log.Add("addref");
            return this;
        }

        public void Dispose() => _log.Add("release");
    }

    private sealed class RecordingMemoryManager(byte[] data, List<string> log) : MemoryManager<byte>
    {
        private GCHandle _handle;

        public bool ThrowOnUnpin { get; set; }

        public override Span<byte> GetSpan() => data;

        public override unsafe MemoryHandle Pin(int elementIndex = 0)
        {
            log.Add("pin");
            _handle = GCHandle.Alloc(data, GCHandleType.Pinned);
            return new MemoryHandle(
                (byte*)_handle.AddrOfPinnedObject() + elementIndex, default, this);
        }

        public override void Unpin()
        {
            log.Add("unpin");
            Free();
            if (ThrowOnUnpin)
                throw new InvalidOperationException("Unpin failed.");
        }

        protected override void Dispose(bool disposing) => Free();

        private void Free()
        {
            if (_handle.IsAllocated)
                _handle.Free();
        }
    }
}
