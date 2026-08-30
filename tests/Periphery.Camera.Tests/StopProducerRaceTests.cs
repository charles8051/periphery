using Periphery.Camera.Testing;
using Periphery.Camera.Tests.Fakes;

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
        var backend = new InMemoryCameraBackend();
        var session = await TestHelpers.CreateSessionWithBackend(backend);

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

        // Wait until the producer has actually started faulting so the
        // dispose lands while CaptureAsync's finally is also trying to
        // stop the producer. A small delay is sufficient — the fault
        // injection above hits within microseconds at the in-process
        // synthetic-backend frame rate.
        await Task.Delay(TimeSpan.FromMilliseconds(100));

        // The race: external dispose while CaptureAsync's finally is
        // (likely) running. Without the single-flight guard in
        // StopProducerAsync, one of the two paths NREs on whichever
        // nullable field the other path just nulled. With the fix, both
        // return cleanly.
        await session.DisposeAsync();

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
