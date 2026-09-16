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
/// A <see cref="FactAttribute"/> that skips off Windows instead of passing.
/// </summary>
/// <remarks>
/// An early <c>return</c> for <c>!OperatingSystem.IsWindows()</c> reports as
/// Passed having run nothing, and the automatic CI legs are Linux and macOS, so a
/// Windows-only guard reads green on every push while never executing. Skipped
/// says so. Same idiom as <c>OpenCvFactAttribute</c> in the interop test project.
/// </remarks>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "Media Foundation is Windows-only.";
    }
}

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
    [WindowsFact]
    public async Task FailedOpen_ThenDispose_ReleasesRuntimeExactlyOnce()
    {
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

    [WindowsFact]
    public async Task OpenAfterDispose_IsRefused()
    {
        var backend = new MfCameraBackend(CameraTestFormats.CreateDeviceInfo(
            id: "periphery-no-such-camera-" + Guid.NewGuid().ToString("N"),
            name: "nonexistent"));

        // Never opened, so disposal takes no MF reference and this touches no
        // native code. The point is the refusal, not the teardown.
        await backend.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => backend.OpenAsync(CancellationToken.None));
    }

    [WindowsFact]
    public async Task ConcurrentDispose_ReleasesRuntimeExactlyOnce()
    {
        // Same stand-in client as above: it is what a double release would shut
        // Media Foundation down underneath.
        MfRuntime.EnsureStarted();
        int aliveWithOneClient = MfRuntime.RefCount;
        try
        {
            // The claim is a compare-and-set, so a single pass proves nothing: two
            // sequential disposals are correct even with a plain check-then-set,
            // and only two that interleave inside the check are not.
            //
            // Asymmetric on purpose, and the asymmetry is worth stating plainly.
            // The PASS direction is exact. Task.WhenAll cannot return before the
            // winning disposal's Task.Run, which contains the Release, has
            // completed, so the RefCount read below has a real happens-before and
            // no loaded runner can make this fail spuriously. The FAIL direction is
            // probabilistic: the window is two instructions wide, and an
            // independent review measured the reintroduced check-then-set red in 10
            // of 17 runs at 32 iterations on a 24-core box. A single-vCPU runner
            // detects less than that. Read a green as "no evidence of a double
            // release" rather than as proof; the proof is that the claim is one
            // Interlocked.Exchange a reviewer can read.
            for (int i = 0; i < 256; i++)
            {
                var backend = new MfCameraBackend(CameraTestFormats.CreateDeviceInfo(
                    id: "periphery-no-such-camera-" + Guid.NewGuid().ToString("N"),
                    name: "nonexistent"));

                try
                {
                    // A failed open still took one EnsureStarted before it failed,
                    // which is the reference the racing disposals compete to
                    // release. Dispose in a finally for the same reason the test
                    // above does: an open that throws something unmapped, or that
                    // unexpectedly succeeds, would otherwise strand that reference
                    // for the life of the process so MFShutdown never runs.
                    await Assert.ThrowsAnyAsync<CameraException>(
                        () => backend.OpenAsync(CancellationToken.None));

                    using var start = new Barrier(2);
                    await Task.WhenAll(
                        Task.Run(async () => { start.SignalAndWait(); await backend.DisposeAsync(); }),
                        Task.Run(async () => { start.SignalAndWait(); await backend.DisposeAsync(); }));
                }
                finally
                {
                    await backend.DisposeAsync();
                }

                Assert.Equal(aliveWithOneClient, MfRuntime.RefCount);
            }
        }
        finally
        {
            MfRuntime.Release();
        }
    }

    [WindowsFact]
    public async Task SecondOpen_IsRefused_AndDoesNotTakeASecondRuntimeReference()
    {
        // Sequential, single-threaded, no disposal in the middle. Before the
        // already-opened check, a second open took a second EnsureStarted that
        // _mfStarted, being a bool, did not record, and overwrote _source/_reader
        // with no Shutdown on the pair it replaced. DisposeAsync then released one
        // of the two references, so MFShutdown never ran again for the process and
        // the orphaned source kept holding the device.
        MfRuntime.EnsureStarted();
        int aliveWithOneClient = MfRuntime.RefCount;
        try
        {
            var backend = new MfCameraBackend(CameraTestFormats.CreateDeviceInfo(
                id: "periphery-no-such-camera-" + Guid.NewGuid().ToString("N"),
                name: "nonexistent"));

            try
            {
                // The first open fails in activation, after taking the reference.
                await Assert.ThrowsAnyAsync<CameraException>(
                    () => backend.OpenAsync(CancellationToken.None));

                // The backend is spent either way, so the retry is refused rather
                // than allowed to acquire a second time.
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => backend.OpenAsync(CancellationToken.None));
            }
            finally
            {
                await backend.DisposeAsync();
            }

            // One open, one reference, one release.
            Assert.Equal(aliveWithOneClient, MfRuntime.RefCount);
        }
        finally
        {
            MfRuntime.Release();
        }
    }
}
