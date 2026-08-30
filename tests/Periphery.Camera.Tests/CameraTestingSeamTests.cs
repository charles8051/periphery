using Microsoft.Extensions.Time.Testing;
using Periphery.Camera.Testing;

namespace Periphery.Camera.Tests;

/// <summary>
/// Exercises the public <c>Periphery.Camera.Testing</c> seam (ADR-0065) the way a
/// downstream consumer does — using only the shipped package types, no access to
/// Periphery internals. These stand in for a consumer (e.g. a kiosk that wraps
/// <see cref="CameraSession"/> in its own capture pump) proving it can drive
/// capture, faults, and the wedged-stream timeout with no camera.
/// </summary>
[Collection("Camera")]
public sealed class CameraTestingSeamTests
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(5);

    // ── Scope: the builder path (For(deviceInfo).OpenAsync) resolves to the fake ──

    [Fact]
    public async Task Scope_BuilderOpen_StreamsFramesFromInMemoryBackend()
    {
        // A consumer whose code opens the camera from a DeviceInfo itself (the
        // common case) installs a scope; the builder's snapshot+capture double
        // open each gets a fresh backend via the factory overload.
        using (CameraTestScope.Install(_ => new InMemoryCameraBackend()))
        {
            await using var session = await CameraSession
                .For(CameraTestFormats.CreateDeviceInfo())
                .PreferYuy2()
                .OpenAsync();

            int seen = 0;
            await foreach (var frame in session.CaptureAsync())
            {
                using (frame) seen++;
                if (seen >= 3) break;
            }

            Assert.Equal(3, seen);
            Assert.Equal(CameraPixelFormat.Yuy2, session.Configuration.Format.PixelFormat);
        }
    }

    [Fact]
    public async Task Scope_WiresTheInstalledBackendIdentity()
    {
        var backend = new InMemoryCameraBackend(nativeEndpointId: "test://seam-id");
        using (CameraTestScope.Install(backend))
        {
            await using var device = await CameraDevice.OpenAsync(CameraTestFormats.CreateDeviceInfo());
            Assert.Equal("test://seam-id", device.NativeEndpointId);
        }
    }

    [Fact]
    public async Task Scope_Nested_RestoresOuterOnDispose()
    {
        using (CameraTestScope.Install(new InMemoryCameraBackend(nativeEndpointId: "outer")))
        {
            using (CameraTestScope.Install(new InMemoryCameraBackend(nativeEndpointId: "inner")))
            {
                await using var d = await CameraDevice.OpenAsync(CameraTestFormats.CreateDeviceInfo());
                Assert.Equal("inner", d.NativeEndpointId);
            }

            await using var outer = await CameraDevice.OpenAsync(CameraTestFormats.CreateDeviceInfo());
            Assert.Equal("outer", outer.NativeEndpointId);
        }
    }

    // ── Lifecycle fidelity: one instance == one device lifecycle ─────────

    [Fact]
    public async Task Scope_SingleInstance_SecondOpenAfterDisposeThrows()
    {
        // The fake does not revive on re-open: a disposed backend stays disposed,
        // the same as a real one. That is why the single-instance overload is for
        // single-open code only, and why multi-open paths (the builder's
        // snapshot+capture pair) need the per-open factory overload.
        var backend = new InMemoryCameraBackend();
        using (CameraTestScope.Install(backend))
        {
            await using (await CameraDevice.OpenAsync(CameraTestFormats.CreateDeviceInfo()))
                Assert.True(backend.IsOpen);

            Assert.True(backend.IsDisposed);
            Assert.False(backend.IsOpen);

            await Assert.ThrowsAsync<ObjectDisposedException>(
                () => CameraDevice.OpenAsync(CameraTestFormats.CreateDeviceInfo()));
        }
    }

    // ── Harness: single-open path, inspect the one backend directly ──────

    [Fact]
    public async Task Harness_OpenSessionAsync_CapturesWithoutGlobalState()
    {
        var backend = new InMemoryCameraBackend();
        await using var session = await CameraTestHarness.OpenSessionAsync(backend);

        int seen = 0;
        await foreach (var frame in session.CaptureAsync())
        {
            using (frame) seen++;
            if (seen >= 2) break;
        }

        Assert.Equal(2, seen);
        Assert.True(backend.FrameCounter >= 2);
    }

    // ── Fault injection: a mid-stream driver fault reaches the consumer ──

    [Fact]
    public async Task FaultOnNextRead_SurfacesToConsumerPump()
    {
        var backend = new InMemoryCameraBackend
        {
            FaultOnNextRead = new IOException("simulated device loss"),
        };
        await using var session = await CameraTestHarness.OpenSessionAsync(backend);

        var ex = await Assert.ThrowsAsync<IOException>(async () =>
        {
            await foreach (var frame in session.CaptureAsync())
                frame.Dispose();
        });
        Assert.Equal("simulated device loss", ex.Message);
    }

    // ── The incident: a wedged stream, driven deterministically to timeout ──

    [Fact]
    public async Task HangOnRead_WithFakeClock_ThrowsCameraTimeout()
    {
        var time = new FakeTimeProvider();
        // The stream stalls — the producer never returns a frame — exactly the
        // UVC wedge the seam exists to let consumers test. A FakeTimeProvider
        // drives the session's frame-timeout without a real wait.
        var backend = new InMemoryCameraBackend { HangOnRead = true };
        await using var session = await CameraTestHarness.OpenSessionAsync(backend, timeProvider: time);

        var captureTask = Task.Run(async () =>
        {
            await foreach (var frame in session.CaptureAsync(new CameraCaptureOptions(FrameTimeout)))
                frame.Dispose();
        });

        await backend.ReadHangReached.WaitAsync(TimeSpan.FromSeconds(10));
        for (int i = 0; i < 500 && !captureTask.IsCompleted; i++)
        {
            time.Advance(FrameTimeout + TimeSpan.FromSeconds(1));
            await Task.Delay(10);
        }

        await Assert.ThrowsAsync<CameraTimeoutException>(() => captureTask);
    }
}
