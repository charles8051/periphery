using Periphery.Camera.Tests.Fakes;

namespace Periphery.Camera.Tests;

/// <summary>
/// Verifies that CameraSession is single-capture: overlapping capture
/// operations are rejected with InvalidOperationException.
/// </summary>
[Collection("Camera")]
public sealed class ConcurrentCaptureTests
{
    [Fact]
    public async Task StartCaptureAsync_WhileCapturing_Throws()
    {
        await using var session = await TestHelpers.CreateSessionWithBackend();

        await session.StartCaptureAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.StartCaptureAsync());

        await session.StopCaptureAsync();
    }

    [Fact]
    public async Task CaptureAsync_AfterStopCapture_CanRestart()
    {
        await using var session = await TestHelpers.CreateSessionWithBackend();

        // First capture run
        using var cts1 = new CancellationTokenSource();
        int count1 = 0;
        await foreach (var frame in session.CaptureAsync(ct: cts1.Token))
        {
            frame.Dispose();
            if (++count1 >= 2) cts1.Cancel();
        }

        // Second capture run
        using var cts2 = new CancellationTokenSource();
        int count2 = 0;
        await foreach (var frame in session.CaptureAsync(ct: cts2.Token))
        {
            frame.Dispose();
            if (++count2 >= 2) cts2.Cancel();
        }

        Assert.True(count1 >= 2, $"Expected at least 2 frames in first run, got {count1}");
        Assert.True(count2 >= 2, $"Expected at least 2 frames in second run, got {count2}");
    }

    [Fact]
    public async Task StartCaptureAsync_AfterCaptureAsyncCompletes_Works()
    {
        await using var session = await TestHelpers.CreateSessionWithBackend();

        using var cts = new CancellationTokenSource();
        await foreach (var frame in session.CaptureAsync(ct: cts.Token))
        {
            frame.Dispose();
            cts.Cancel();
        }

        await session.StartCaptureAsync();
        using var f = await session.ReadFrameAsync();
        Assert.NotNull(f);
        await session.StopCaptureAsync();
    }
}
