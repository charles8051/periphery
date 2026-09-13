using Periphery.Testing;

namespace Periphery.FlashAnything.Tests;

/// <summary>Waits on what the service reports instead of polling it (ADR-0089 D2).</summary>
internal static class ServiceWait
{
    /// <summary>Bounds a wait so a service that never gets there fails instead of hanging (ADR-0089 D5).</summary>
    public static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Completes once <paramref name="until"/> holds for the service's state, checked on entry and on
    /// every <see cref="FlashAnythingService.StateChanged"/>.
    /// </summary>
    public static async Task UntilAsync(FlashAnythingService svc, Func<AppState, bool> until)
    {
        var met = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(AppState state)
        {
            if (until(state))
                met.TrySetResult();
        }

        svc.StateChanged += Handler;
        try
        {
            if (until(svc.State))
                return;
            await met.Task.WaitAsync(Bound);
        }
        finally
        {
            svc.StateChanged -= Handler;
        }
    }
}

/// <summary>
/// Runs a service's probe loop one cycle at a time on a fake clock, for a test with one bound bridge.
/// </summary>
/// <remarks>
/// A probe loop probes, reports what the cycle found, and then arms its cadence timer. The timer being
/// armed is therefore the loop's own signal that the cycle, including everything it reported, is
/// finished. Nothing else in the service arms a timer on this clock.
/// </remarks>
internal sealed class ProbeStepper(TimerSignalingFakeTimeProvider time)
{
    private TimeSpan _nextDue;

    /// <summary>Waits until the loop has finished a cycle and is waiting for the next one.</summary>
    public async Task ParkedAsync() =>
        _nextDue = await time.NextTimerArmedAsync().AsTask().WaitAsync(ServiceWait.Bound);

    /// <summary>Runs <paramref name="cycles"/> more cycles, waiting for each to finish.</summary>
    public async Task StepAsync(int cycles = 1)
    {
        for (int i = 0; i < cycles; i++)
        {
            time.Advance(_nextDue);
            await ParkedAsync();
        }
    }

    /// <summary>
    /// Starts the next cycle from a thread with no SynchronizationContext. The loop's continuation then
    /// runs inside <c>FakeTimeProvider.Advance</c>, so when this completes the loop has gone as
    /// far as it can without waiting: into the provider's open, or onto a gate someone else holds.
    /// <see cref="InlineAdvancePreconditionTests"/> pins that behaviour.
    /// </summary>
    public Task StartNextCycleInlineAsync() => Task.Run(() => time.Advance(_nextDue));
}
