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
}
