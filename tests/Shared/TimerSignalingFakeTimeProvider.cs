#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
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
/// observes them: when each is armed or re-armed, and whether it has since fired or been disposed.
/// It simulates nothing of its own. A timer armed with an infinite due time is not reported.
/// </para>
/// <para>
/// Compiled into every test project from <c>tests/Directory.Build.targets</c>.
/// </para>
/// </remarks>
internal sealed class TimerSignalingFakeTimeProvider : FakeTimeProvider
{
    private readonly Channel<TimeSpan> _armed = Channel.CreateUnbounded<TimeSpan>();
    private readonly List<TimeSpan> _armedDueTimes = new();
    private readonly List<TrackedTimer> _timers = new();

    /// <summary>The due time of every timer armed so far, in the order they were armed.</summary>
    public IReadOnlyList<TimeSpan> ArmedDueTimes
    {
        get
        {
            lock (_timers)
                return _armedDueTimes.ToArray();
        }
    }

    /// <summary>
    /// Completes with the due time of the next timer armed, including one armed before the call and
    /// not yet taken. The timer exists by then, but it may since have fired or been disposed: a
    /// signal says a timer was armed, not that anything is still waiting on it.
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
        var timer = new TrackedTimer(this, base.CreateTimer(callback, state, dueTime, period));
        lock (_timers)
            _timers.Add(timer);
        timer.Arm(dueTime);
        return timer;
    }

    /// <summary>
    /// Starts the run with <paramref name="start"/> and awaits it. Each time the run arms a timer, the
    /// clock advances to the earliest timer still pending, which fires it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For a sequential flow whose fakes deliver every event before the component arms the deadline
    /// that would otherwise end the wait. A timer the run is really waiting on then fires at once,
    /// which is the outcome a patient clock gives. It does not suit a test that must act between
    /// arming and expiry.
    /// </para>
    /// <para>
    /// A signal whose timer has already fired or been disposed, such as the deadline of a wait that
    /// completed as it was armed, advances nothing. The clock only ever moves to the due instant of
    /// a pending timer, so it cannot carry a later timer past its own due instant. If more than one
    /// timer is pending at once the flow is not sequential, and this throws rather than choose an
    /// order for them.
    /// </para>
    /// <para><paramref name="bound"/> only turns a run that never finishes into a <see cref="TimeoutException"/>.</para>
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

                try
                {
                    await armed.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw new TimeoutException(
                        "The run did not complete within its bound, and armed no further timer."
                    );
                }

                var pending = PendingTimers();
                if (pending.Count == 0)
                    continue;

                if (pending.Count > 1)
                    throw new InvalidOperationException(
                        $"RunAdvancingAsync drives one pending timer at a time, and {pending.Count} are pending "
                            + $"(due in {string.Join(", ", pending.Select(t => t.Due - GetUtcNow()))}). "
                            + "Advance explicitly when timers overlap."
                    );

                Advance(pending[0].Due - GetUtcNow());
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

    private List<TrackedTimer> PendingTimers()
    {
        var now = GetUtcNow();
        lock (_timers)
            return _timers.Where(t => t.IsPending(now)).OrderBy(t => t.Due).ToList();
    }

    private void Record(TimeSpan dueTime)
    {
        lock (_timers)
            _armedDueTimes.Add(dueTime);
        _armed.Writer.TryWrite(dueTime);
    }

    /// <summary>Delegates to the fake's own timer, noting when it is due and whether it was disposed.</summary>
    private sealed class TrackedTimer(TimerSignalingFakeTimeProvider owner, ITimer inner) : ITimer
    {
        private volatile bool _disposed;
        private long _dueTicks = long.MaxValue;

        public DateTimeOffset Due => new(Interlocked.Read(ref _dueTicks), TimeSpan.Zero);

        // One-shot semantics: a periodic timer counts as pending only until its first due instant.
        public bool IsPending(DateTimeOffset now) =>
            !_disposed && Interlocked.Read(ref _dueTicks) != long.MaxValue && Due > now;

        public void Arm(TimeSpan dueTime)
        {
            if (dueTime == Timeout.InfiniteTimeSpan)
            {
                Interlocked.Exchange(ref _dueTicks, long.MaxValue);
                return;
            }

            Interlocked.Exchange(ref _dueTicks, (owner.GetUtcNow() + dueTime).UtcTicks);
            owner.Record(dueTime);
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            bool changed = inner.Change(dueTime, period);
            Arm(dueTime);
            return changed;
        }

        public void Dispose()
        {
            _disposed = true;
            inner.Dispose();
        }

        public ValueTask DisposeAsync()
        {
            _disposed = true;
            return inner.DisposeAsync();
        }
    }
}
