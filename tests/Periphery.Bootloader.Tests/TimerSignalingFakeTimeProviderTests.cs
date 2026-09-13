using System;
using System.Threading;
using System.Threading.Tasks;
using Periphery.Testing;
using Xunit;

namespace Periphery.Bootloader.Tests;

/// <summary>
/// Pins the contract of <see cref="TimerSignalingFakeTimeProvider.RunAdvancingAsync{T}"/>, which is
/// shared by every test project and has no project of its own. Lives here beside its first user.
/// </summary>
public class TimerSignalingFakeTimeProviderTests
{
    [Fact]
    public async Task ASignalForATimerThatIsAlreadyGone_DoesNotCarryTheNextTimerPastItsDueInstant()
    {
        var time = new TimerSignalingFakeTimeProvider();
        var start = time.GetUtcNow();
        DateTimeOffset firedAt = default;

        await time.RunAdvancingAsync(async () =>
        {
            // A wait that completed as its deadline was armed, the shape of a device event delivered
            // before the timeout. Its signal is still queued when the next timer is armed.
            using (new CancellationTokenSource(TimeSpan.FromHours(1), time)) { }

            await Task.Delay(TimeSpan.FromMilliseconds(750), time);
            firedAt = time.GetUtcNow();
        });

        Assert.Equal(start + TimeSpan.FromMilliseconds(750), firedAt);
    }

    /// <summary>
    /// A timer due now is not pending, because FakeTimeProvider fires it inside CreateTimer. The
    /// helper relies on that, and this test fails if a FakeTimeProvider version stops doing it.
    /// </summary>
    [Fact]
    public async Task AZeroDueTimer_FiresAsItIsArmed_SoTheRunIsNotLeftWaitingOnIt()
    {
        var time = new TimerSignalingFakeTimeProvider();
        var fired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await time.RunAdvancingAsync(
            async () =>
            {
                using var timer = time.CreateTimer(
                    _ => fired.TrySetResult(),
                    null,
                    TimeSpan.Zero,
                    Timeout.InfiniteTimeSpan
                );
                await fired.Task;
            },
            bound: TimeSpan.FromSeconds(5)
        );

        using var deadline = new CancellationTokenSource(TimeSpan.Zero, time);
        Assert.True(deadline.IsCancellationRequested);
    }

    [Fact]
    public async Task TwoTimersPendingAtOnce_Throws_RatherThanChoosingAnOrder()
    {
        var time = new TimerSignalingFakeTimeProvider();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            time.RunAdvancingAsync(() =>
                Task.WhenAll(
                    Task.Delay(TimeSpan.FromSeconds(1), time),
                    Task.Delay(TimeSpan.FromSeconds(2), time)
                )
            )
        );
    }

    [Fact]
    public void AdvanceToNextPendingTimer_FiresTheEarliestFirst_AndReportsWhenNoneIsLeft()
    {
        var time = new TimerSignalingFakeTimeProvider();
        var fired = new List<string>();

        // Armed latest-first, so due order is not arming order.
        using var late = time.CreateTimer(
            _ => fired.Add("late"),
            null,
            TimeSpan.FromSeconds(2),
            Timeout.InfiniteTimeSpan
        );
        using var early = time.CreateTimer(
            _ => fired.Add("early"),
            null,
            TimeSpan.FromSeconds(1),
            Timeout.InfiniteTimeSpan
        );

        Assert.True(time.AdvanceToNextPendingTimer());
        Assert.Equal(["early"], fired);

        Assert.True(time.AdvanceToNextPendingTimer());
        Assert.Equal(["early", "late"], fired);

        Assert.False(time.AdvanceToNextPendingTimer());
    }
}
