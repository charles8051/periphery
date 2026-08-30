using Periphery.Camera.Testing;
using Periphery.Camera.Tests.Fakes;

namespace Periphery.Camera.Tests;

/// <summary>
/// Verifies that CameraSession integrates correctly with
/// DeviceSessionHost from ADR-0032 — the reconnect-resilient
/// publication boundary.
/// </summary>
[Collection("Camera")]
public sealed class SessionHostIntegrationTests
{
    [Fact]
    public async Task CameraSession_CanBeUsedAsSessionType()
    {
        var backend = new InMemoryCameraBackend();
        var session = await TestHelpers.CreateSessionWithBackend(backend);

        Assert.IsType<CameraSession>(session);
        Assert.NotNull(session.Device);
        Assert.NotNull(session.DeviceInfo);
        Assert.Equal(TestHelpers.DefaultConfig, session.Configuration);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task CameraSession_SessionCreationDelegate_Signature()
    {
        Func<DeviceInfo, CancellationToken, Task<CameraSession>> createSession = (deviceInfo, ct) =>
            Periphery.Camera.Testing.CameraTestHarness.OpenSessionAsync(
                new InMemoryCameraBackend(),
                TestHelpers.DefaultConfig,
                deviceInfo,
                ct: ct);

        var info = TestHelpers.CreateDeviceInfo();
        await using var session = await createSession(info, CancellationToken.None);

        Assert.Equal(info, session.DeviceInfo);
        Assert.Equal(640, session.Configuration.Format.Width);
    }

    [Fact]
    public async Task CameraSession_WhileSessionActive_CanCapture()
    {
        var backend = new InMemoryCameraBackend();
        await using var session = await TestHelpers.CreateSessionWithBackend(backend);

        Func<CameraSession, CancellationToken, Task> whileActive = async (s, ct) =>
        {
            int count = 0;
            await foreach (var frame in s.CaptureAsync(ct: ct))
            {
                frame.Dispose();
                if (++count >= 3)
                    return;
            }
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await whileActive(session, cts.Token);

        Assert.Equal(3, session.Metrics.FramesProduced);
    }

    [Fact]
    public async Task CameraSession_OnSessionEnded_CanDisposeCleanly()
    {
        var backend = new InMemoryCameraBackend();
        var session = await TestHelpers.CreateSessionWithBackend(backend);

        bool endedCalled = false;
        Func<CameraSession, Task> onEnded = s =>
        {
            endedCalled = true;
            return Task.CompletedTask;
        };

        await onEnded(session);
        await session.DisposeAsync();

        Assert.True(endedCalled);
        Assert.True(backend.IsDisposed);
    }

    [Fact]
    public async Task CameraSession_Metrics_AvailableForSupervision()
    {
        // MaxFrames=1 makes the producer deterministic: it produces one
        // frame and then hangs until the session is disposed. Without
        // this, the producer keeps acquiring leases for queued frames
        // and OutstandingLeases is racy at the assertion site (this
        // test was previously flaky because of exactly that race).
        var backend = new InMemoryCameraBackend { MaxFrames = 1 };
        await using var session = await TestHelpers.CreateSessionWithBackend(backend);

        var beforeMetrics = session.Metrics;
        Assert.Equal(0, beforeMetrics.FramesProduced);
        Assert.Equal(0, beforeMetrics.FramesDropped);
        Assert.Equal(0, beforeMetrics.OutstandingLeases);
        Assert.Null(beforeMetrics.LastFrameTimestamp);

        await session.StartCaptureAsync();
        // No `using` here — explicit Dispose below would otherwise
        // double-dispose at scope exit.
        var frame = await session.ReadFrameAsync();

        var afterMetrics = session.Metrics;
        Assert.Equal(1, afterMetrics.FramesProduced);
        Assert.Equal(1, afterMetrics.OutstandingLeases);
        Assert.NotNull(afterMetrics.LastFrameTimestamp);

        frame.Dispose();
        Assert.Equal(0, session.Metrics.OutstandingLeases);
    }
}
