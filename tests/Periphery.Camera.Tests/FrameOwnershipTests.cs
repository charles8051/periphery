using Periphery.Camera.Tests.Fakes;

namespace Periphery.Camera.Tests;

/// <summary>
/// Verifies the explicit ownership contract from ADR-0035 Decision 8:
/// leased frames are stable until disposed, owned copies are independent,
/// and the pool is never allowed to revoke active leases.
/// </summary>
[Collection("Camera")]
public sealed class FrameOwnershipTests
{
    [Fact]
    public async Task LeasedFrame_DataAccessible_UntilDisposed()
    {
        await using var session = await TestHelpers.CreateSessionWithBackend();

        await session.StartCaptureAsync();
        var frame = await session.ReadFrameAsync();

        Assert.True(frame.ContiguousBuffer.Length > 0);
        Assert.Equal(640, frame.Width);

        frame.Dispose();
    }

    [Fact]
    public async Task LeasedFrame_Copy_CreatesIndependentOwned()
    {
        await using var session = await TestHelpers.CreateSessionWithBackend();

        await session.StartCaptureAsync();
        using var leased = await session.ReadFrameAsync();

        using var owned = leased.Copy();

        Assert.Equal(leased.Width, owned.Width);
        Assert.Equal(leased.Height, owned.Height);
        Assert.Equal(leased.PixelFormat, owned.PixelFormat);
        Assert.Equal(leased.Timestamp, owned.Timestamp);
        Assert.Equal(leased.ContiguousBuffer.Length, owned.ContiguousBuffer.Length);
    }

    [Fact]
    public async Task OwnedFrame_Survives_LeaseDisposal()
    {
        await using var session = await TestHelpers.CreateSessionWithBackend();

        await session.StartCaptureAsync();
        OwnedCameraFrame owned;

        using (var leased = await session.ReadFrameAsync())
        {
            owned = leased.Copy();
        }

        Assert.Equal(640, owned.Width);
        Assert.True(owned.ContiguousBuffer.Length > 0);
        owned.Dispose();
    }

    [Fact]
    public async Task LeasedFrame_Implements_ICameraFrame()
    {
        await using var session = await TestHelpers.CreateSessionWithBackend();

        await session.StartCaptureAsync();
        using var frame = await session.ReadFrameAsync();

        ICameraFrame iface = frame;
        Assert.Equal(640, iface.Width);
        Assert.Equal(480, iface.Height);
        Assert.True(iface.IsContiguous);
        Assert.Equal(1, iface.PlaneCount);
    }

    [Fact]
    public async Task OwnedFrame_Implements_ICameraFrame()
    {
        await using var session = await TestHelpers.CreateSessionWithBackend();

        await session.StartCaptureAsync();
        using var leased = await session.ReadFrameAsync();
        using var owned = leased.Copy();

        ICameraFrame iface = owned;
        Assert.Equal(640, iface.Width);
    }

    [Fact]
    public async Task LeasedFrame_GetPlane_ReturnsValidPlane()
    {
        await using var session = await TestHelpers.CreateSessionWithBackend();

        await session.StartCaptureAsync();
        using var frame = await session.ReadFrameAsync();

        var plane = frame.GetPlane(0);
        Assert.True(plane.Buffer.Length > 0);
        Assert.Equal(640, plane.Width);
        Assert.Equal(480, plane.Height);
        Assert.True(plane.Stride > 0);
    }

    [Fact]
    public async Task LeasedFrame_GetPlane_OutOfRange_Throws()
    {
        await using var session = await TestHelpers.CreateSessionWithBackend();

        await session.StartCaptureAsync();
        using var frame = await session.ReadFrameAsync();

        Assert.Throws<ArgumentOutOfRangeException>(() => frame.GetPlane(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => frame.GetPlane(1));
    }

    [Fact]
    public async Task LeasedFrame_AfterDispose_GetPlaneThrows()
    {
        await using var session = await TestHelpers.CreateSessionWithBackend();

        await session.StartCaptureAsync();
        var frame = await session.ReadFrameAsync();
        frame.Dispose();

        Assert.Throws<ObjectDisposedException>(() => frame.GetPlane(0));
    }

    [Fact]
    public async Task LeasedFrame_AfterDispose_CopyThrows()
    {
        await using var session = await TestHelpers.CreateSessionWithBackend();

        await session.StartCaptureAsync();
        var frame = await session.ReadFrameAsync();
        frame.Dispose();

        Assert.Throws<ObjectDisposedException>(() => frame.Copy());
    }

    [Fact]
    public async Task Disposing_Lease_DecrementsOutstandingCount()
    {
        await using var session = await TestHelpers.CreateSessionWithBackend();

        await session.StartCaptureAsync();
        var frame = await session.ReadFrameAsync();
        await session.StopCaptureAsync();

        Assert.Equal(1, session.Metrics.OutstandingLeases);
        frame.Dispose();
        Assert.Equal(0, session.Metrics.OutstandingLeases);
    }

    // ── Ref-counting semantics (ADR-0035 §8b) ─────────────────────────

    [Fact]
    public async Task LeasedFrame_AddRef_ReturnsSameInstance()
    {
        await using var session = await TestHelpers.CreateSessionWithBackend();
        await session.StartCaptureAsync();
        using var frame = await session.ReadFrameAsync();

        var second = frame.AddRef();
        try
        {
            Assert.Same(frame, second);
        }
        finally
        {
            second.Dispose();
        }
    }

    [Fact]
    public async Task LeasedFrame_AddRef_DelaysPoolReturnUntilLastDispose()
    {
        await using var session = await TestHelpers.CreateSessionWithBackend();
        await session.StartCaptureAsync();
        var frame = await session.ReadFrameAsync();
        await session.StopCaptureAsync();

        // Two references in flight, one outstanding pool buffer.
        var second = frame.AddRef();
        Assert.Equal(1, session.Metrics.OutstandingLeases);

        // Drop one reference — pool buffer still in flight.
        frame.Dispose();
        Assert.Equal(1, session.Metrics.OutstandingLeases);

        // Drop the second — pool buffer recycles.
        second.Dispose();
        Assert.Equal(0, session.Metrics.OutstandingLeases);
    }

    [Fact]
    public async Task LeasedFrame_OutstandingLeases_CountsBuffers_NotReferences()
    {
        await using var session = await TestHelpers.CreateSessionWithBackend();
        await session.StartCaptureAsync();
        using var frame = await session.ReadFrameAsync();
        await session.StopCaptureAsync();

        // Three references, one buffer.
        var b = frame.AddRef();
        var c = frame.AddRef();
        try
        {
            Assert.Equal(1, session.Metrics.OutstandingLeases);
        }
        finally
        {
            b.Dispose();
            c.Dispose();
        }
    }

    [Fact]
    public async Task LeasedFrame_AddRefAfterFinalDispose_Throws()
    {
        await using var session = await TestHelpers.CreateSessionWithBackend();
        await session.StartCaptureAsync();
        var frame = await session.ReadFrameAsync();
        await session.StopCaptureAsync();

        frame.Dispose(); // refcount → 0, buffer back in pool
        Assert.Throws<ObjectDisposedException>(() => frame.AddRef());
    }

    [Fact]
    public async Task OwnedFrame_AddRef_ReturnsSameInstance_NoPoolSideEffect()
    {
        await using var session = await TestHelpers.CreateSessionWithBackend();
        await session.StartCaptureAsync();
        OwnedCameraFrame owned;
        using (var leased = await session.ReadFrameAsync())
        {
            owned = leased.Copy();
        }
        await session.StopCaptureAsync();

        // Owned frames don't participate in the pool; the lease released
        // its buffer when its using-block ended.
        Assert.Equal(0, session.Metrics.OutstandingLeases);

        var second = owned.AddRef();
        Assert.Same(owned, second);
        second.Dispose();
        owned.Dispose();

        // No exception, no pool change.
        Assert.Equal(0, session.Metrics.OutstandingLeases);
    }

    [Fact]
    public async Task OwnedFrame_AddRefAfterFinalDispose_Throws()
    {
        await using var session = await TestHelpers.CreateSessionWithBackend();
        await session.StartCaptureAsync();
        OwnedCameraFrame owned;
        using (var leased = await session.ReadFrameAsync())
        {
            owned = leased.Copy();
        }
        owned.Dispose();
        Assert.Throws<ObjectDisposedException>(() => owned.AddRef());
    }
}
