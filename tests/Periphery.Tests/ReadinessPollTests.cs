using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Periphery.Tests;

/// <summary>
/// The shell-side poll behind a readiness check (periphery #251). It sits in a recovery
/// path, so the failure modes worth pinning are the ones that would strand a caller: a
/// hang, a spin, or a verdict rendered without ever asking.
/// </summary>
public class ReadinessPollTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(5);

    [Fact]
    public async Task AlreadyReady_returnsAtOnceAndAsksExactlyOnce()
    {
        int asked = 0;
        var elapsed = await ReadinessPoll.UntilAsync(
            () => { asked++; return true; },
            timeout: TimeSpan.FromSeconds(30), Interval, CancellationToken.None);

        Assert.NotNull(elapsed);
        Assert.Equal(1, asked);
        // Never pays an interval it did not need — the common case for a device that came
        // back before the platform call even returned.
        Assert.True(elapsed!.Value < TimeSpan.FromSeconds(1), $"took {elapsed}");
    }

    [Fact]
    public async Task ReadyLater_keepsPollingAndReports()
    {
        int asked = 0;
        var elapsed = await ReadinessPoll.UntilAsync(
            () => ++asked >= 4,
            timeout: TimeSpan.FromSeconds(30), Interval, CancellationToken.None);

        Assert.NotNull(elapsed);
        Assert.Equal(4, asked);
    }

    [Fact]
    public async Task NeverReady_givesUpAtTheTimeoutRatherThanHanging()
    {
        var watch = Stopwatch.StartNew();
        var elapsed = await ReadinessPoll.UntilAsync(
            () => false,
            timeout: TimeSpan.FromMilliseconds(120), Interval, CancellationToken.None);

        Assert.Null(elapsed);
        // Bounded on both sides: it must actually wait, and must not overrun the deadline
        // waiting out a final whole interval.
        Assert.InRange(watch.Elapsed, TimeSpan.FromMilliseconds(80), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AlreadyReady_winsOverAnExpiredTimeout()
    {
        // The predicate is asked before the deadline is consulted, so a subject that is
        // plainly ready is never reported as a failure on a technicality.
        var elapsed = await ReadinessPoll.UntilAsync(
            () => true, timeout: TimeSpan.Zero, Interval, CancellationToken.None);

        Assert.NotNull(elapsed);
    }

    [Fact]
    public async Task Cancellation_propagatesRatherThanReportingNotReady()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ReadinessPoll.UntilAsync(
                () => false, timeout: TimeSpan.FromSeconds(30), Interval, cts.Token).AsTask());
    }

    [Fact]
    public async Task AProbeThatThrows_surfacesRatherThanCountingAsNotReady()
    {
        // A probe that cannot answer is a real fault. Swallowing it here would turn a broken
        // readiness check into a silent full-timeout wait on every reset.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ReadinessPoll.UntilAsync(
                () => throw new InvalidOperationException("probe failed"),
                timeout: TimeSpan.FromSeconds(30), Interval, CancellationToken.None).AsTask());
    }
}
