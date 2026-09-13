using Periphery.Testing;

namespace Periphery.FlashAnything.Tests;

/// <summary>
/// The runtime behaviour <see cref="ProbeStepper.StartNextCycleInlineAsync"/> relies on, pinned so a
/// change in it fails here by name instead of leaving the probe-and-flash exclusion test checking
/// nothing.
/// </summary>
public class InlineAdvancePreconditionTests
{
    [Fact]
    public async Task Advancing_off_the_test_context_resumes_a_waiting_delay_before_Advance_returns()
    {
        var time = new TimerSignalingFakeTimeProvider();
        using var cts = new CancellationTokenSource();
        bool resumed = false;

        var waiting = WaitAsTheProbeLoopDoesAsync(time, cts.Token, () => resumed = true);
        var due = await time.NextTimerArmedAsync().AsTask().WaitAsync(ServiceWait.Bound);

        await Task.Run(() =>
        {
            time.Advance(due);
            Assert.True(resumed, "advancing the clock did not run the waiting continuation inline");
        });
        await waiting;
    }

    // SerialProbeLoop awaits its delay with ConfigureAwait(false), on a token it can cancel.
    private static async Task WaitAsTheProbeLoopDoesAsync(TimeProvider time, CancellationToken ct, Action resumed)
    {
        await Task.Delay(TimeSpan.FromSeconds(1), time, ct).ConfigureAwait(false);
        resumed();
    }
}
