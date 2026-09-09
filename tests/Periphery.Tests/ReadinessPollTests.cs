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
    public async Task ADeadlineThatPassesDuringTheProbe_reportsNotReadyRatherThanThrowing()
    {
        // The probe outlives the whole budget, so the deadline is already behind us
        // by the time it is consulted. Reachable without any clock trickery, and
        // the plain contract: not ready in time is null, never an exception.
        var elapsed = await ReadinessPoll.UntilAsync(
            () => { Thread.Sleep(40); return false; },
            timeout: TimeSpan.FromMilliseconds(10), Interval, CancellationToken.None);

        Assert.Null(elapsed);
    }

    [Fact]
    public async Task ADeadlineCrossedBetweenClockReads_reportsNotReadyRatherThanThrowing()
    {
        // The #226 defect: the deadline check and the delay were computed from two
        // separate clock readings, so a deadline crossed between them produced a
        // negative delay and an ArgumentOutOfRangeException out of Task.Delay.
        //
        // A wall clock lands in that window only by luck, which is why the original
        // escaped as an intermittent CI failure. This clock advances on every read,
        // so the second reading of an iteration is always later than the first —
        // exactly the race, made a certainty. With the timeout set between one and
        // two steps, a two-read implementation goes negative on its first pass.
        var clock = new AdvancingOnReadTimeProvider(TimeSpan.FromMilliseconds(10));
        int asked = 0;

        var elapsed = await ReadinessPoll.UntilAsync(
            () => { asked++; return false; },
            timeout: TimeSpan.FromMilliseconds(15),
            interval: TimeSpan.FromMilliseconds(5),
            CancellationToken.None,
            clock);

        Assert.Null(elapsed);

        // Asked twice, so the loop went through a real Task.Delay on the injected
        // clock rather than bailing out before the first one. Without this the
        // test would still pass if the delay path were never reached, which is
        // the failure mode that makes a regression test worthless.
        Assert.Equal(2, asked);
    }

    /// <summary>
    /// A clock that moves forward by a fixed step on every <see cref="GetTimestamp"/>,
    /// so two readings taken back to back are never equal.
    /// </summary>
    /// <remarks>
    /// Only the timestamp side is overridden. <see cref="TimeProvider.CreateTimer"/>
    /// keeps its base implementation, which schedules against real time — verified,
    /// not assumed: a 50 ms <c>Task.Delay</c> on a provider overriding nothing else
    /// completes in ~58 ms of wall clock rather than throwing. So the deadline
    /// arithmetic under test runs on the stepping clock while the awaited delay
    /// runs on the real one, which is what keeps this test both deterministic and
    /// fast.
    /// </remarks>
    private sealed class AdvancingOnReadTimeProvider(TimeSpan step) : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp()
        {
            long now = _ticks;
            _ticks += step.Ticks;
            return now;
        }
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
