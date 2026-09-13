using Microsoft.Extensions.Time.Testing;
using Periphery.Camera.Testing;
using Periphery.Camera.Tests.Fakes;
using Periphery.Testing;

namespace Periphery.Camera.Tests;

/// <summary>
/// Deterministic coverage of <see cref="CameraSession"/>'s frame-timeout
/// behaviour, driven by an injected <see cref="FakeTimeProvider"/> rather than
/// real wall-clock waits (review finding 2.2 / ADR-0052). The session routes
/// every timeout/delay/elapsed through its <c>TimeProvider</c>, so these tests
/// advance virtual time to exercise the timeout-vs-cancellation decision
/// without sleeping out real milliseconds.
/// </summary>
[Collection("Camera")]
public sealed class CameraSessionClockTests
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(5);

    // ── Pure decision helper ───────────────────────────────────────────

    // ClassifyWaitOutcome is the pure core extracted from WaitForNextFrameAsync:
    // given the two settled cancellation signals, it decides timeout vs cancel.
    // Exhaustive over its 4 inputs — caller-cancel always wins, a timeout is
    // only surfaced when the timeout fired and the caller did NOT cancel.

    // Inputs are the two settled cancellation signals; expectedTimedOut is the
    // single case (timeout fired, caller did not cancel) that yields TimedOut.
    // bool params keep the public test signature off the internal WaitOutcome.
    [Theory]
    [InlineData(false, false, false)]  // neither: caller token tripped some other way
    [InlineData(false, true,  false)]  // caller only
    [InlineData(true,  true,  false)]  // both raced -> caller wins
    [InlineData(true,  false, true)]   // timeout only -> the one timeout case
    public void ClassifyWaitOutcome_IsExhaustiveAndCallerCancelWins(
        bool timeoutRequested, bool callerRequested, bool expectedTimedOut)
    {
        var expected = expectedTimedOut
            ? CameraSession.WaitOutcome.TimedOut
            : CameraSession.WaitOutcome.Cancelled;
        Assert.Equal(expected, CameraSession.ClassifyWaitOutcome(timeoutRequested, callerRequested));
    }

    // ── Frame-timeout EXPIRY branch (streaming) ─────────────────────────

    [Fact]
    public async Task CaptureAsync_FrameTimeoutExpires_ThrowsCameraTimeout()
    {
        var time = new TimerSignalingFakeTimeProvider();
        // Producer parks on its very first read (no frame ever written), so the
        // consumer blocks in the next-frame wait until the timeout elapses.
        var backend = new InMemoryCameraBackend { HangOnRead = true };
        await using var session = await TestHelpers.CreateSessionWithBackend(backend, timeProvider: time);

        var captureOptions = new CameraCaptureOptions(FrameTimeout);

        var captureTask = Task.Run(async () =>
        {
            await foreach (var frame in session.CaptureAsync(captureOptions))
                frame.Dispose();
        });

        // Drive the timeout deterministically: once the consumer has created its
        // timeout timer over the fake clock, advancing past the timeout trips it.
        await ExpireFrameTimeoutAsync(time, captureTask);

        await Assert.ThrowsAsync<CameraTimeoutException>(() => captureTask);
    }

    // ── Frame-timeout EXPIRY branch (pull) ──────────────────────────────

    [Fact]
    public async Task ReadFrameAsync_FrameTimeoutExpires_ThrowsCameraTimeout()
    {
        var time = new TimerSignalingFakeTimeProvider();
        var backend = new InMemoryCameraBackend { HangOnRead = true };
        await using var session = await TestHelpers.CreateSessionWithBackend(backend, timeProvider: time);

        await session.StartCaptureAsync();

        var readTask = session.ReadFrameAsync(new CameraCaptureOptions(FrameTimeout));

        await ExpireFrameTimeoutAsync(time, readTask);

        await Assert.ThrowsAsync<CameraTimeoutException>(() => readTask);
    }

    // ── Caller-cancellation stays cancellation, NOT a timeout ───────────

    [Fact]
    public async Task ReadFrameAsync_CallerCancels_ThrowsOperationCanceled_NotTimeout()
    {
        var time = new FakeTimeProvider();
        var backend = new InMemoryCameraBackend { HangOnRead = true };
        await using var session = await TestHelpers.CreateSessionWithBackend(backend, timeProvider: time);

        await session.StartCaptureAsync();

        using var cts = new CancellationTokenSource();
        var readTask = session.ReadFrameAsync(new CameraCaptureOptions(FrameTimeout), cts.Token);

        // The producer is parked, so the read can only finish by timeout or by
        // caller cancellation. Cancel the caller — and crucially do NOT advance
        // the clock, so the timeout never fires. The outcome must be a plain
        // OperationCanceledException, never reclassified into a timeout.
        await backend.ReadHangReached;
        cts.Cancel();

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => readTask);
        Assert.IsNotType<CameraTimeoutException>(ex);
    }

    [Fact]
    public async Task CaptureAsync_CallerCancels_CompletesGracefully_NotTimeout()
    {
        var time = new FakeTimeProvider();
        var backend = new InMemoryCameraBackend { HangOnRead = true };
        await using var session = await TestHelpers.CreateSessionWithBackend(backend, timeProvider: time);

        using var cts = new CancellationTokenSource();
        var captureOptions = new CameraCaptureOptions(FrameTimeout);

        var captureTask = Task.Run(async () =>
        {
            await foreach (var frame in session.CaptureAsync(captureOptions, cts.Token))
                frame.Dispose();
        });

        // Cancel the caller while the producer is parked and the clock is frozen.
        // CaptureAsync swallows caller-cancellation (yield break) — so the task
        // completes cleanly, and must NOT surface a CameraTimeoutException.
        await backend.ReadHangReached;
        cts.Cancel();

        // Completes without throwing — caller-cancel is graceful end-of-stream.
        await captureTask.WaitAsync(TimeSpan.FromSeconds(10));
    }

    // ── Helper ──────────────────────────────────────────────────────────

    /// <summary>
    /// Waits for the consumer to arm its frame timeout on the fake clock, then advances past it.
    /// The producer parks on every read, so the operation can only finish through that timer. The
    /// caller asserts the fault (<c>CameraTimeoutException</c>) itself, so this only waits for the
    /// operation to finish.
    /// </summary>
    private static async Task ExpireFrameTimeoutAsync(TimerSignalingFakeTimeProvider time, Task operation)
    {
        await TestHelpers.TimerArmedAsync(time, FrameTimeout);
        time.Advance(FrameTimeout);

        // Wait on a continuation so the operation's own fault is left for the caller to inspect.
        await operation.ContinueWith(static _ => { }, TaskScheduler.Default).WaitAsync(TestHelpers.Patience);
    }
}
