using System;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Periphery;
using Periphery.Camera.Testing;
using Periphery.Camera.Windows;
using Xunit;

namespace Periphery.Camera.Tests;

/// <summary>
/// Media Foundation runtime teardown. <see cref="MfRuntime"/> ref-counts
/// MFStartup/MFShutdown across concurrently-open backends. A failed
/// <see cref="MfCameraBackend.OpenAsync"/> must release the runtime exactly once:
/// the open path takes one <c>EnsureStarted</c>, and the caller's
/// <c>DisposeAsync</c> is the sole owner of the matching <c>Release</c> (gated by
/// <c>_mfStarted</c>). This pins that a failed open followed by a dispose nets one
/// release, not two — a second decrement can reach zero and call MFShutdown under
/// another backend that is still using Media Foundation.
/// </summary>
/// <remarks>
/// Windows-only: MFStartup is a Media Foundation P/Invoke and
/// <see cref="MfCameraBackend"/> is Windows-annotated. The whole assembly runs
/// serially (<c>DisableTestParallelization</c>), so the process-global ref count
/// is observed without interference from other classes.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class MfRuntimeTeardownTests
{
    [Fact]
    public async Task FailedOpen_ThenDispose_ReleasesRuntimeExactlyOnce()
    {
        // MFStartup does not exist off Windows; skip rather than DllNotFoundException.
        if (!OperatingSystem.IsWindows())
            return;

        // Stand in for a second backend holding Media Foundation alive. The ref
        // count exists precisely so one backend's failed open cannot shut MF down
        // under another that is still using it.
        MfRuntime.EnsureStarted();
        int aliveWithOneClient = MfRuntime.RefCount;
        try
        {
            // A device id that matches nothing, so the open fails in device
            // activation regardless of what cameras the host actually has. By then
            // OpenAsync has already run EnsureStarted, so the matching Release must
            // happen exactly once — no more, no less.
            var backend = new MfCameraBackend(CameraTestFormats.CreateDeviceInfo(
                id: "periphery-no-such-camera-" + Guid.NewGuid().ToString("N"),
                name: "nonexistent"));

            // Dispose in a finally so an unexpected outcome — the open succeeding,
            // or throwing something other than CameraException — still releases the
            // backend's own reference instead of leaking it into later MF tests. The
            // release-once assertion is what this test is actually about, so it runs
            // after disposal.
            try
            {
                await Assert.ThrowsAnyAsync<CameraException>(
                    () => backend.OpenAsync(CancellationToken.None));
            }
            finally
            {
                await backend.DisposeAsync();
            }

            // Disposing the failed backend released the one EnsureStarted the open
            // took. The count must return to our stand-in client, not below it.
            Assert.Equal(aliveWithOneClient, MfRuntime.RefCount);
        }
        finally
        {
            MfRuntime.Release();
        }
    }

    [Fact]
    public async Task OpenAfterDispose_IsRefused()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var backend = new MfCameraBackend(CameraTestFormats.CreateDeviceInfo(
            id: "periphery-no-such-camera-" + Guid.NewGuid().ToString("N"),
            name: "nonexistent"));

        // Never opened, so disposal takes no MF reference and this touches no
        // native code. The point is the refusal, not the teardown.
        await backend.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => backend.OpenAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ConcurrentDispose_ReleasesRuntimeExactlyOnce()
    {
        if (!OperatingSystem.IsWindows())
            return;

        // Same stand-in client as above: it is what a double release would shut
        // Media Foundation down underneath.
        MfRuntime.EnsureStarted();
        int aliveWithOneClient = MfRuntime.RefCount;
        try
        {
            // The claim is a compare-and-set, so a single pass proves nothing: two
            // sequential disposals are correct even with a plain check-then-set,
            // and only two that interleave inside the check are not. Racing a fresh
            // backend repeatedly makes the interleaving likely without making the
            // pass direction probabilistic -- under a correct claim the count is
            // exact on every iteration, so this test cannot fail spuriously.
            for (int i = 0; i < 32; i++)
            {
                var backend = new MfCameraBackend(CameraTestFormats.CreateDeviceInfo(
                    id: "periphery-no-such-camera-" + Guid.NewGuid().ToString("N"),
                    name: "nonexistent"));

                // A failed open still took one EnsureStarted before it failed, which
                // is the reference the racing disposals compete to release.
                await Assert.ThrowsAnyAsync<CameraException>(
                    () => backend.OpenAsync(CancellationToken.None));

                using var start = new Barrier(2);
                await Task.WhenAll(
                    Task.Run(async () => { start.SignalAndWait(); await backend.DisposeAsync(); }),
                    Task.Run(async () => { start.SignalAndWait(); await backend.DisposeAsync(); }));

                Assert.Equal(aliveWithOneClient, MfRuntime.RefCount);
            }
        }
        finally
        {
            MfRuntime.Release();
        }
    }
}
