using System.IO.Pipelines;
using Periphery.Testing;

namespace Periphery.Bootloader.Stm32.Serial.Tests;

/// <summary>
/// Runs a programmer and the part it talks to as a deterministic simulation on a fake clock
/// (ADR-0089). A deadline expires only when the test advances the clock, and the test advances it
/// only when nothing else can run, so a slow machine cannot turn an answer into a timeout.
/// </summary>
internal static class Simulation
{
    /// <summary>
    /// Pipe options for a hand-rolled transport in a simulation. Readers continue inline, as
    /// <see cref="FakeStm32Bootloader"/>'s do when it is given a clock.
    /// </summary>
    public static PipeOptions InlinePipes { get; } =
        new(readerScheduler: PipeScheduler.Inline, writerScheduler: PipeScheduler.Inline, useSynchronizationContext: false);

    /// <summary>
    /// Runs <paramref name="body"/> with a fresh clock, for a programmer talking to a fake built on
    /// the same clock.
    /// </summary>
    /// <remarks>
    /// The fake's pipes continue inline, so a call into the programmer returns only once the
    /// programmer and the fake are both waiting on a timer or done. That needs no
    /// <see cref="SynchronizationContext"/> on the thread: .NET does not inline a task's awaiting
    /// continuation while one is current, and xUnit installs one, so the body runs on the thread pool.
    /// </remarks>
    public static Task RunAsync(Func<TimerSignalingFakeTimeProvider, Task> body) =>
        Task.Run(() => body(new TimerSignalingFakeTimeProvider()));

    /// <summary>
    /// Advances the clock to the earliest pending timer until <paramref name="run"/> completes. Inside
    /// <see cref="RunAsync"/> nothing runnable is left whenever this checks, so that is exactly
    /// what a patient clock does next, and the pair sees the same order of events on every run.
    /// </summary>
    public static async Task DriveAsync(TimerSignalingFakeTimeProvider time, Task run)
    {
        while (!run.IsCompleted)
            Assert.True(time.AdvanceToNextPendingTimer(), "the programmer is waiting on something other than a timer");
        await run;
    }

    /// <inheritdoc cref="DriveAsync(TimerSignalingFakeTimeProvider, Task)"/>
    public static async Task<T> DriveAsync<T>(TimerSignalingFakeTimeProvider time, Task<T> run)
    {
        await DriveAsync(time, (Task)run);
        return await run;
    }
}
