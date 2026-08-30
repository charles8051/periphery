using Microsoft.Extensions.Time.Testing;
using Periphery.Camera.Testing;
using Periphery.Camera.Tests.Fakes;

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
        var time = new FakeTimeProvider();
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
        await AdvanceUntilCompleteAsync(time, backend.ReadHangReached, captureTask);

        await Assert.ThrowsAsync<CameraTimeoutException>(() => captureTask);
    }

    // ── Frame-timeout EXPIRY branch (pull) ──────────────────────────────

    [Fact]
    public async Task ReadFrameAsync_FrameTimeoutExpires_ThrowsCameraTimeout()
    {
        var time = new FakeTimeProvider();
        var backend = new InMemoryCameraBackend { HangOnRead = true };
        await using var session = await TestHelpers.CreateSessionWithBackend(backend, timeProvider: time);

        await session.StartCaptureAsync();

        var readTask = session.ReadFrameAsync(new CameraCaptureOptions(FrameTimeout));

        await AdvanceUntilCompleteAsync(time, backend.ReadHangReached, readTask);

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
    /// Advances the fake clock until <paramref name="operation"/> completes,
    /// then returns <em>without</em> observing its result — the caller asserts
    /// the fault (<c>CameraTimeoutException</c>) itself. The producer is already
    /// confirmed parked (<paramref name="producerParked"/>), so the operation
    /// can only finish via the frame-timeout firing. There is a small window
    /// between the producer parking and the consumer constructing its timeout
    /// timer; we close it by advancing in a short bounded poll — every
    /// <see cref="FakeTimeProvider.Advance"/> past the timeout trips whatever
    /// timer currently exists, so the first advance after the consumer's timer
    /// is registered fires it. This keeps the test deterministic (no fixed sleep
    /// proportional to the timeout) while tolerating start ordering.
    /// </summary>
    private static async Task AdvanceUntilCompleteAsync(
        FakeTimeProvider time, Task producerParked, Task operation)
    {
        await producerParked.WaitAsync(TimeSpan.FromSeconds(10));

        for (int i = 0; i < 500 && !operation.IsCompleted; i++)
        {
            // Advance well past the frame-timeout so a freshly-registered timer
            // is already due.
            time.Advance(FrameTimeout + TimeSpan.FromSeconds(1));
            // Yield real time so the consumer's continuation (the timer callback
            // -> cancellation -> WaitToReadAsync unwind) can run before the next
            // probe. This is scheduler hand-off, not a timeout-length wait.
            await Task.Delay(10);
        }

        // Wait for completion via a bystander so we don't surface the fault here;
        // the test body's Assert.ThrowsAsync is what inspects it.
        await Task.WhenAny(operation, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.True(operation.IsCompleted, "Frame-timeout did not fire within the advance budget.");
    }
}
