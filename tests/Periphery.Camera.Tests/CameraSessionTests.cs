using Periphery.Camera.Testing;
using Periphery.Camera.Tests.Fakes;

namespace Periphery.Camera.Tests;

[Collection("Camera")]
public sealed class CameraSessionTests
{
    // ── Convenience factory ────────────────────────────────────────────

    [Fact]
    public async Task OpenAsync_Convenience_CreatesSession()
    {
        await using var session = await CameraSession.OpenAsync(
            TestHelpers.CreateDeviceInfo(), TestHelpers.DefaultConfig);

        Assert.NotNull(session.Device);
        Assert.Equal(TestHelpers.DefaultConfig, session.Configuration);
        Assert.False(session.IsCapturing);
    }

    [Fact]
    public async Task OpenAsync_Convenience_OwnsDevice()
    {
        var backend = TestHelpers.InstallSingleTestBackend();
        var session = await CameraSession.OpenAsync(
            TestHelpers.CreateDeviceInfo(), TestHelpers.DefaultConfig);

        await session.DisposeAsync();

        Assert.True(backend.IsDisposed);
        TestHelpers.InstallTestBackendFactory();
    }

    [Fact]
    public async Task OpenAsync_Convenience_InvalidFormat_DisposesDevice()
    {
        var backend = TestHelpers.InstallSingleTestBackend();
        var badFormat = new CameraFormat(9999, 9999, CameraPixelFormat.Unknown,
            new Rational(1), new Rational(1), CameraTransport.Uncompressed);

        await Assert.ThrowsAsync<CameraConfigurationException>(
            () => CameraSession.OpenAsync(TestHelpers.CreateDeviceInfo(), new(badFormat)));

        Assert.True(backend.IsDisposed);
        TestHelpers.InstallTestBackendFactory();
    }

    // ── Advanced factory ───────────────────────────────────────────────

    [Fact]
    public async Task OpenSessionAsync_Advanced_DoesNotOwnDevice()
    {
        var backend = new InMemoryCameraBackend();
        await using var device = TestHelpers.CreateDeviceWithBackend(backend);

        var session = await device.OpenSessionAsync(TestHelpers.DefaultConfig);
        await session.DisposeAsync();

        Assert.False(backend.IsDisposed);
    }

    [Fact]
    public async Task OpenSessionAsync_Advanced_TwoSessions_Throws()
    {
        var backend = new InMemoryCameraBackend();
        await using var device = TestHelpers.CreateDeviceWithBackend(backend);

        await using var session1 = await device.OpenSessionAsync(TestHelpers.DefaultConfig);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => device.OpenSessionAsync(TestHelpers.DefaultConfig));
    }

    [Fact]
    public async Task OpenSessionAsync_Advanced_AfterDispose_CanReopen()
    {
        var backend = new InMemoryCameraBackend();
        await using var device = TestHelpers.CreateDeviceWithBackend(backend);

        var session1 = await device.OpenSessionAsync(TestHelpers.DefaultConfig);
        await session1.DisposeAsync();

        await using var session2 = await device.OpenSessionAsync(TestHelpers.DefaultConfig);
        Assert.NotNull(session2);
    }

    // ── CaptureAsync (streaming) ───────────────────────────────────────

    [Fact]
    public async Task CaptureAsync_YieldsFrames()
    {
        await using var session = await TestHelpers.CreateSessionWithBackend();

        using var cts = new CancellationTokenSource();
        int count = 0;
        await foreach (var frame in session.CaptureAsync(ct: cts.Token))
        {
            using (frame)
            {
                Assert.Equal(640, frame.Width);
                Assert.Equal(480, frame.Height);
                Assert.Equal(CameraPixelFormat.Yuy2, frame.PixelFormat);
                Assert.True(frame.ContiguousBuffer.Length > 0);
            }
            if (++count >= 5) cts.Cancel();
        }
        // Producer may prefetch one or two past cancel; assert "at
        // least 5" to verify the foreach actually yielded frames.
        Assert.True(count >= 5, $"Expected at least 5 frames yielded, got {count}.");
    }

    [Fact]
    public async Task CaptureAsync_SetsIsCapturing()
    {
        await using var session = await TestHelpers.CreateSessionWithBackend();

        Assert.False(session.IsCapturing);

        using var cts = new CancellationTokenSource();
        await foreach (var frame in session.CaptureAsync(ct: cts.Token))
        {
            Assert.True(session.IsCapturing);
            frame.Dispose();
            cts.Cancel();
        }

        Assert.False(session.IsCapturing);
    }

    [Fact]
    public async Task CaptureAsync_Cancellation_CompletesGracefully()
    {
        await using var session = await TestHelpers.CreateSessionWithBackend();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        int count = 0;
        await foreach (var frame in session.CaptureAsync(ct: cts.Token))
        {
            frame.Dispose();
            count++;
        }
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task CaptureAsync_UpdatesMetrics()
    {
        await using var session = await TestHelpers.CreateSessionWithBackend();

        using var cts = new CancellationTokenSource();
        int count = 0;
        await foreach (var frame in session.CaptureAsync(ct: cts.Token))
        {
            frame.Dispose();
            if (++count >= 3) cts.Cancel();
        }

        // The producer may have prefetched one or two frames past the
        // cancellation request; assert "at least" rather than exact
        // equality. The test's intent is "metrics counted produced
        // frames", not "the consumer stopped on exactly frame 3".
        var metrics = session.Metrics;
        Assert.True(
            metrics.FramesProduced >= 3,
            $"Expected metrics.FramesProduced >= 3, got {metrics.FramesProduced}."
        );
        Assert.NotNull(metrics.LastFrameTimestamp);
    }

    // ── Pull-based capture ─────────────────────────────────────────────

    [Fact]
    public async Task StartCapture_ReadFrame_StopCapture()
    {
        await using var session = await TestHelpers.CreateSessionWithBackend();

        await session.StartCaptureAsync();
        Assert.True(session.IsCapturing);

        using var frame = await session.ReadFrameAsync();
        Assert.Equal(640, frame.Width);

        await session.StopCaptureAsync();
        Assert.False(session.IsCapturing);
    }

    [Fact]
    public async Task ReadFrameAsync_WithoutStart_Throws()
    {
        await using var session = await TestHelpers.CreateSessionWithBackend();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.ReadFrameAsync());
    }

    [Fact]
    public async Task StopCaptureAsync_WhenNotCapturing_DoesNotThrow()
    {
        await using var session = await TestHelpers.CreateSessionWithBackend();
        await session.StopCaptureAsync();
    }

    // ── Disposal ───────────────────────────────────────────────────────

    [Fact]
    public async Task DisposeAsync_StopsCapture()
    {
        var backend = new InMemoryCameraBackend();
        var session = await TestHelpers.CreateSessionWithBackend(backend);

        await session.StartCaptureAsync();
        Assert.True(backend.IsCapturing);

        await session.DisposeAsync();
        Assert.False(backend.IsCapturing);
    }

    [Fact]
    public async Task DisposeAsync_Idempotent()
    {
        var session = await TestHelpers.CreateSessionWithBackend();
        await session.DisposeAsync();
        await session.DisposeAsync();
    }

    [Fact]
    public async Task Methods_AfterDispose_ThrowObjectDisposed()
    {
        var session = await TestHelpers.CreateSessionWithBackend();
        await session.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.StartCaptureAsync());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.ReadFrameAsync());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.StopCaptureAsync());
    }
}
