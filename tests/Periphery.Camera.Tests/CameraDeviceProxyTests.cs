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

    // ── The stall case: no second deadline, and no stall-specific exception ──

    // ADR-0084 D5 proposed CameraSessionOptions.StallTimeout plus a
    // CameraStallException so the proxy could recover a wedged stream. Issue #219
    // cut both, on the grounds that CameraCaptureOptions.FrameTimeout already
    // produces a typed, recoverable fault for a stream that stops delivering.
    // This is that claim, driven end to end through the proxy: the backend parks
    // on every read, the pump's next-frame wait expires, and the recovery policy
    // is handed a CameraTimeoutException — no new option and no new type in the
    // path. The short FrameTimeout only shortens the test; the default five
    // seconds runs the identical code.
    [Fact]
    public async Task StalledStream_ReachesTheRecoveryPolicyAsATimeout_WithNoStallSpecificSurface()
    {
        bool stalled = true;
        using var scope = CameraTestScope.Install(
            _ => new InMemoryCameraBackend { HangOnRead = Volatile.Read(ref stalled) });

        var tracker = CreateTracker();
        var policy = new RecordingRetryPolicy(TimeSpan.FromMilliseconds(50));
        var framesAfterRecovery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var proxy = CameraDeviceProxy.Create(
            tracker,
            onFrame: (_, _) => { framesAfterRecovery.TrySetResult(); return Task.CompletedTask; },
            captureOptions: new CameraCaptureOptions(TimeSpan.FromMilliseconds(200)),
            recoveryPolicy: policy);

        SimulateConnect(tracker, ActiveCamera());

        var fault = await policy.FirstFault.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.IsType<CameraTimeoutException>(fault);

        // And the ladder is a real ladder: let the camera come good and the next
        // retry delivers frames without the consumer doing anything.
        Volatile.Write(ref stalled, false);
        await framesAfterRecovery.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    // The gentlest rung for a wedged camera is the platform's, not a
    // camera-specific one (issue #123's fourth direction, declined). A USB-backed
    // camera is already in the reset strategy table with both rungs, and the
    // escalating policy walks them — so reaching the reset ladder from this proxy
    // is a policy argument, with no camera code involved.
    //
    // The table is asserted rather than DeviceReset.PlatformDefault, because the
    // platform mechanism is Windows-only: PlatformDefault is NullDeviceReset off
    // Windows and advertises nothing. That is a pre-existing property of ADR-0060's
    // core, not something the camera decision changes.
    [Fact]
    public void AUsbCameraIsResettableByTheTable_AndTheEscalatingPolicyWalksBothRungs()
    {
        var camera = TestHelpers.CreateDeviceInfo(@"USB\VID_046D&PID_0825\CAM") with
        {
            IsActive = true,
            BusType = BusType.USB,
        };

        Assert.True(ResetStrategyMap.IsUsbBacked(camera));
        var strategies = ResetStrategyMap.ForTransport(camera);
        Assert.Equal(
            [ResetKind.UsbPortCycle, ResetKind.PnpDisableEnable],
            strategies.Select(s => s.Kind));

        // Attempt 1 is the sanity retry; attempts 2 and 3 take the rungs in order.
        var policy = new EscalatingResetRecoveryPolicy();
        var wedged = new CameraTeardownPendingException(
            "wedged", camera.Id, Task.CompletedTask, TimeSpan.FromSeconds(9));

        Assert.IsType<RecoveryDirective.Retry>(policy.Decide(Context(1)));
        Assert.Equal(ResetKind.UsbPortCycle, Rung(2));
        Assert.Equal(ResetKind.PnpDisableEnable, Rung(3));

        RecoveryContext Context(int attempt) => new(
            Attempt: attempt,
            ResetCount: 0,
            LastFault: wedged,
            Device: camera,
            AvailableResets: strategies);

        ResetKind Rung(int attempt) =>
            Assert.IsType<RecoveryDirective.Reset>(policy.Decide(Context(attempt))).Strategy.Kind;
    }

    // On Windows the same camera reaches those rungs through the mechanism the
    // proxy uses by default, with no camera-specific IDeviceReset.
    [Fact]
    public void OnWindows_ThePlatformResetAdvertisesThoseRungsForACamera()
    {
        // cfgmgr32 reset does not exist off Windows; PlatformDefault is
        // NullDeviceReset there and correctly advertises nothing.
        if (!OperatingSystem.IsWindows())
            return;

        var camera = TestHelpers.CreateDeviceInfo(@"USB\VID_046D&PID_0825\CAM") with
        {
            IsActive = true,
            BusType = BusType.USB,
        };

        Assert.Equal(
            [ResetKind.UsbPortCycle, ResetKind.PnpDisableEnable],
            DeviceReset.PlatformDefault.StrategiesFor(camera).Select(s => s.Kind));
    }

    // A policy that records the first fault it is asked about and retries fast,
    // so a recovery path can be observed without waiting out the default
    // 1-2-4-5s backoff.
    private sealed class RecordingRetryPolicy(TimeSpan delay) : IRecoveryPolicy
    {
        private readonly TaskCompletionSource<Exception?> _first =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<Exception?> FirstFault => _first.Task;

        public RecoveryDirective Decide(RecoveryContext context)
        {
            _first.TrySetResult(context.LastFault);
            return new RecoveryDirective.Retry(delay);
        }
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
