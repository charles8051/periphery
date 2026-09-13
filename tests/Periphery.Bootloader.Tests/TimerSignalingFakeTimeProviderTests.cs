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
}
