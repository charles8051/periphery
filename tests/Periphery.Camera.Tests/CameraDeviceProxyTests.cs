using Periphery.Camera.Testing;
using Periphery.Camera.Tests.Fakes;

namespace Periphery.Camera.Tests;

/// <summary>
/// Lifecycle coverage for <see cref="CameraDeviceProxy"/> (ADR-0084 D5, issue
/// #139): the camera package's missing lifecycle owner. These drive the tracker
/// edges directly rather than real hardware, so a "replug" is a disconnect
/// followed by a connect on the same tracker.
/// </summary>
[Collection("Camera")]
public sealed class CameraDeviceProxyTests
{
    // CameraTestFormats.CreateDeviceInfo leaves IsActive false, and a tracker
    // only activates on an active device — so the proxy would never open.
    private static DeviceInfo ActiveCamera() =>
        TestHelpers.CreateDeviceInfo() with { IsActive = true };

    private static DeviceTracker CreateTracker()
    {
        var tracker = new DeviceTracker(new DeviceFilter());
        _ = Devices.Watch().AddTracker(tracker);
        return tracker;
    }

    private static void SimulateConnect(DeviceTracker tracker, DeviceInfo device)
    {
        tracker.OnDeviceAppeared(device);
        tracker.OnDeviceConnected(device);
    }

    private static void SimulateDisconnect(DeviceTracker tracker, DeviceInfo device)
    {
        var inactive = device with { IsActive = false };
        tracker.OnDeviceDisconnected(inactive);
        tracker.OnDeviceDisappeared(inactive);
    }

    [Fact]
    public async Task ActivationOpensASession_AndFramesReachTheCallback()
    {
        using var scope = CameraTestScope.Install(_ => new InMemoryCameraBackend());
        var tracker = CreateTracker();
        var threeFrames = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int seen = 0;

        await using var proxy = CameraDeviceProxy.Create(
            tracker,
            onFrame: (_, _) =>
            {
                if (Interlocked.Increment(ref seen) == 3)
                    threeFrames.TrySetResult();
                return Task.CompletedTask;
            });

        SimulateConnect(tracker, ActiveCamera());

        await threeFrames.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(proxy.IsOpen);
    }

    [Fact]
    public async Task FrameIsDisposedOnceTheCallbackReturns()
    {
        using var scope = CameraTestScope.Install(_ => new InMemoryCameraBackend());
        var tracker = CreateTracker();
        var verdict = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        LeasedCameraFrame? previous = null;

        await using var proxy = CameraDeviceProxy.Create(
            tracker,
            onFrame: (frame, _) =>
            {
                // The pump disposes each frame when the callback returns, so by
                // the time the NEXT frame arrives the previous one's buffer is
                // back in the pool and AddRef must refuse it.
                if (previous is { } stale)
                {
                    try
                    {
                        stale.AddRef();
                        verdict.TrySetResult(false);
                    }
                    catch (InvalidOperationException)
                    {
                        verdict.TrySetResult(true);
                    }
                }

                previous = frame;
                return Task.CompletedTask;
            });

        SimulateConnect(tracker, ActiveCamera());

        Assert.True(
            await verdict.Task.WaitAsync(TimeSpan.FromSeconds(10)),
            "the pump must dispose a frame once the callback returns");
    }

    [Fact]
    public async Task ReplugReopens_WithoutConsumerInvolvement()
    {
        using var scope = CameraTestScope.Install(_ => new InMemoryCameraBackend());
        var tracker = CreateTracker();
        var device = ActiveCamera();
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var proxy = CameraDeviceProxy.Create(
            tracker,
            onFrame: (_, _) =>
            {
                opened.TrySetResult();
                return Task.CompletedTask;
            });

        SimulateConnect(tracker, device);
        await opened.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(proxy.IsOpen);

        SimulateDisconnect(tracker, device);
        await WaitUntilAsync(() => !proxy.IsOpen, TimeSpan.FromSeconds(10));

        // The replug: same identity, and nothing asked of the consumer beyond
        // the edge itself. A fresh session is opened and the pump restarted.
        SimulateConnect(tracker, device);
        await WaitUntilAsync(() => proxy.IsOpen, TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task DisposeClosesTheSession()
    {
        using var scope = CameraTestScope.Install(_ => new InMemoryCameraBackend());
        var tracker = CreateTracker();
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var proxy = CameraDeviceProxy.Create(
            tracker,
            onFrame: (_, _) =>
            {
                opened.TrySetResult();
                return Task.CompletedTask;
            });

        SimulateConnect(tracker, ActiveCamera());
        await opened.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(proxy.IsOpen);

        await proxy.DisposeAsync();

        Assert.False(proxy.IsOpen);
    }

    [Fact]
    public async Task OpenAsync_NullProfile_ThrowsArgumentNullException()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            CameraDeviceProxy.OpenAsync(null!, onFrame: (_, _) => Task.CompletedTask));
    }

    [Fact]
    public async Task OpenAsync_NullOnFrame_ThrowsArgumentNullException()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            CameraDeviceProxy.OpenAsync(
                new DeviceProfile(f => f.OfCategory(DeviceCategory.Camera), "cam"),
                onFrame: null!));
    }

    // Assert.ThrowsAsync passes whether the exception arrives synchronously or
    // on the returned task, so it cannot tell the two apart. Calling without
    // awaiting can: a synchronous throw escapes the call itself and fails the
    // test. The other four proxies' OpenAsync factories fault the task, and this
    // one must not be the odd member for the same mistake.
    [Fact]
    public async Task OpenAsync_NullArguments_FaultTheTaskRatherThanThrowingSynchronously()
    {
        Task<CameraDeviceProxy> nullProfile =
            CameraDeviceProxy.OpenAsync(null!, onFrame: (_, _) => Task.CompletedTask);
        Task<CameraDeviceProxy> nullOnFrame = CameraDeviceProxy.OpenAsync(
            new DeviceProfile(f => f.OfCategory(DeviceCategory.Camera), "cam"),
            onFrame: null!);

        await Assert.ThrowsAsync<ArgumentNullException>(() => nullProfile);
        await Assert.ThrowsAsync<ArgumentNullException>(() => nullOnFrame);
    }

    [Fact]
    public void Create_NullTracker_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            CameraDeviceProxy.Create(null!, onFrame: (_, _) => Task.CompletedTask));
    }

    [Fact]
    public void Create_NullOnFrame_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            CameraDeviceProxy.Create(CreateTracker(), onFrame: null!));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(25);
        }

        Assert.True(condition(), "condition was not met within the timeout");
    }
}
