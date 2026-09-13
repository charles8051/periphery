using Periphery.Camera.Internal;
using Periphery.Camera.Testing;
using Periphery.Camera.Tests.Fakes;
using Periphery.Testing;

namespace Periphery.Camera.Tests;

/// <summary>
/// Race tests around <c>StopProducerAsync</c>: the two paths that reach
/// it (producer-driven via <c>CaptureAsync</c>'s <c>finally</c> block and
/// caller-driven via <c>DisposeAsync</c>) used to share unguarded
/// nullable field accesses and could NRE on whichever caller lost the
/// dispose race. Repro from the field: device-lost mid-capture, where
/// the producer faults and the router disposes the session in response.
/// </summary>
[Collection("Camera")]
public sealed class StopProducerRaceTests
{
    [Fact]
    public async Task ConcurrentDispose_DuringDeviceLost_DoesNotNullRef()
    {
        // The backend's stop is held at a gate, so whichever path stops the
        // producer first is parked inside StopProducerAsync until the test lets
        // it go. The session's clock only shows the test when that happens.
        var time = new TimerSignalingFakeTimeProvider();
        var stopGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new InMemoryCameraBackend { BlockStopUntil = stopGate.Task };
        var session = await TestHelpers.CreateSessionWithBackend(backend, timeProvider: time);

        // Start the capture in a task so we can race a Dispose against
        // CaptureAsync's own finally-driven stop. The CaptureAsync task
        // throws the injected CameraDeviceLostException; that's expected
        // and is what we catch below.
        var captureTask = Task.Run(async () =>
        {
            try
            {
                int count = 0;
                await foreach (var frame in session.CaptureAsync())
                {
                    frame.Dispose();
                    if (++count == 3)
                    {
                        backend.FaultOnNextRead = new CameraDeviceLostException(
                            "Device disconnected", "test");
                    }
                }
            }
            catch (CameraDeviceLostException) { /* expected */ }
        });

        // The device loss ends the capture, and CaptureAsync's finally stops the
        // producer. Its backend stop is held at the gate, so it arms the stop
        // budget on the session's clock: at that point the finally is inside
        // StopProducerAsync, holding the stop lock.
        await TestHelpers.TimerArmedAsync(time, BoundedTeardown.StopCaptureBudget);

        // The race: external dispose while CaptureAsync's finally is running.
        // DisposeAsync reaches StopProducerAsync before its first await, so it
        // is already queued behind the finally when the gate opens. Without the
        // single-flight guard in StopProducerAsync, one of the two paths NREs on
        // whichever nullable field the other path just nulled. With the fix,
        // both return cleanly.
        var dispose = session.DisposeAsync().AsTask();
        stopGate.SetResult();
        await dispose.WaitAsync(TimeSpan.FromSeconds(5));

        // The capture task must also complete cleanly (caught its own
        // expected device-lost exception). A failure here would surface
        // as either an unhandled NullReferenceException from inside
        // StopProducerAsync, or a hang.
        await captureTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RepeatedDisposeAsync_IsIdempotent_NoNRE()
    {
        var backend = new InMemoryCameraBackend();
        var session = await TestHelpers.CreateSessionWithBackend(backend);

        // Run a quick capture so the producer fields (channel, CTS, task)
        // are all populated — otherwise DisposeAsync's "if (IsCapturing)"
        // gate skips the stop path and the race never matters.
        using var cts = new CancellationTokenSource();
        await foreach (var frame in session.CaptureAsync(ct: cts.Token))
        {
            frame.Dispose();
            cts.Cancel();
        }

        // Race many concurrent DisposeAsync calls. The first one does
        // the work; the rest must observe the same fully-stopped state
        // and return cleanly. Pre-fix: one of the second-through-last
        // calls NREs on the just-nulled _producerCts / _channel.
        var disposeTasks = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(async () => await session.DisposeAsync()))
            .ToArray();

        await Task.WhenAll(disposeTasks).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task DisposeAsync_RacingProducerFault_NoUnobservedException()
    {
        // Producer faults inside ProducerLoopAsync write to _captureFault
        // and complete the channel. CaptureAsync's finally then runs
        // StopProducerAsync. If DisposeAsync arrives concurrently, both
        // need to converge on a fully-stopped state without NREing.
        var backend = new InMemoryCameraBackend();
        var session = await TestHelpers.CreateSessionWithBackend(backend);

        backend.FaultOnNextRead = new CameraDeviceLostException(
            "Pre-emptive fault before any frame is read", "test");

        var captureTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var frame in session.CaptureAsync())
                    frame.Dispose();
            }
            // Either outcome is valid:
            //   - CaptureAsync starts first → producer throws the
            //     injected fault → CaptureAsync rethrows it from
            //     finally → CameraDeviceLostException.
            //   - DisposeAsync wins the race → next CaptureAsync call
            //     sees _disposed=true and throws ObjectDisposedException
            //     up-front.
            // The fix specifically guards against NullReferenceException
            // (the pre-fix failure mode); both legitimate exceptions
            // above are fine.
            catch (CameraDeviceLostException) { /* expected */ }
            catch (ObjectDisposedException) { /* also fine — dispose won */ }
        });

        // Hammer DisposeAsync while the fault is unwinding.
        var disposeTask = Task.Run(async () => await session.DisposeAsync());

        await Task.WhenAll(captureTask, disposeTask)
            .WaitAsync(TimeSpan.FromSeconds(5));
    }
}
