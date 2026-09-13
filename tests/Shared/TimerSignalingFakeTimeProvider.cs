#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Time.Testing;

namespace Periphery.Testing;

/// <summary>
/// A <see cref="FakeTimeProvider"/> that reports every timer the code under test arms on it, so a
/// test advances time only once that code is actually waiting on it (ADR-0089 D1, D2).
/// </summary>
/// <remarks>
/// <para>
/// Arming a timer is the component's own signal that it is about to wait: <c>Task.Delay(d, provider)</c>
/// and <c>new CancellationTokenSource(d, provider)</c> both create the timer before the wait they
/// bound begins. Advancing before that point moves a clock nobody is listening to yet, and
/// advancing on a real-time guess is the flake ADR-0089 exists to remove.
/// </para>
/// <para>
/// Timers are still created, ordered and fired by <see cref="FakeTimeProvider"/>. This class only
/// observes creation, so it simulates nothing of its own. A timer re-armed through
/// <see cref="ITimer.Change"/> is not reported, and neither is one armed with an infinite due time.
/// </para>
/// <para>
/// Compiled into every test project from <c>tests/Directory.Build.targets</c>.
/// </para>
/// </remarks>
internal sealed class TimerSignalingFakeTimeProvider : FakeTimeProvider
{
    private readonly Channel<TimeSpan> _armed = Channel.CreateUnbounded<TimeSpan>();
    private readonly List<TimeSpan> _armedDueTimes = new();

    /// <summary>The due time of every timer armed so far, in the order they were armed.</summary>
    public IReadOnlyList<TimeSpan> ArmedDueTimes
    {
        get
        {
            lock (_armedDueTimes)
                return _armedDueTimes.ToArray();
        }
    }

    /// <summary>
    /// Completes with the due time of the next timer armed, including one armed before the call and
    /// not yet taken. The timer exists by then, so advancing by the returned due time fires it.
    /// </summary>
    public ValueTask<TimeSpan> NextTimerArmedAsync(CancellationToken ct = default) =>
        _armed.Reader.ReadAsync(ct);

    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period
    )
    {
        var timer = base.CreateTimer(callback, state, dueTime, period);
        if (dueTime != Timeout.InfiniteTimeSpan)
        {
            lock (_armedDueTimes)
                _armedDueTimes.Add(dueTime);
            _armed.Writer.TryWrite(dueTime);
        }

        return timer;
    }

    /// <summary>
    /// Starts the run with <paramref name="start"/> and awaits it, advancing the clock past each timer
    /// as soon as it is armed.
    /// </summary>
    /// <remarks>
    /// For a sequential flow whose fakes deliver every event before the component arms the deadline
    /// that would otherwise end the wait. Then a timer that gets armed is one the run really is waiting
    /// on, and firing it at once is the same outcome a patient clock would give. It does not suit a
    /// test that must act between arming and expiry. <paramref name="bound"/> only turns a run that
    /// never finishes into a <see cref="TimeoutException"/>.
    /// </remarks>
    public async Task<T> RunAdvancingAsync<T>(Func<Task<T>> start, TimeSpan? bound = null)
    {
        using var failure = new CancellationTokenSource(bound ?? TimeSpan.FromSeconds(30));
        try
        {
            var run = start();
            while (!run.IsCompleted)
            {
                var armed = NextTimerArmedAsync(failure.Token).AsTask();
                if (await Task.WhenAny(run, armed).ConfigureAwait(false) != armed)
                    break;

                TimeSpan due;
                try
                {
                    due = await armed.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw new TimeoutException(
                        "The run did not complete within its bound, and armed no further timer."
                    );
                }

                Advance(due);
            }

            return await run.ConfigureAwait(false);
        }
        finally
        {
            // Retire the read left pending when the run finished first, so it cannot take a timer
            // signal meant for a later NextTimerArmedAsync.
            failure.Cancel();
        }
    }

    /// <inheritdoc cref="RunAdvancingAsync{T}(Func{Task{T}}, TimeSpan?)"/>
    public Task RunAdvancingAsync(Func<Task> start, TimeSpan? bound = null) =>
        RunAdvancingAsync(
            async () =>
            {
                await start().ConfigureAwait(false);
                return true;
            },
            bound
        );
}
