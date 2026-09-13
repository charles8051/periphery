using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Periphery.Usb.Tests.Fakes;

/// <summary>
/// A <see cref="TimeProvider"/> that records the due time of every timer requested of it and
/// fires none of them. For asserting whether a component armed a deadline at all, which a
/// clock that advances cannot answer without waiting to see if something happens.
/// </summary>
/// <remarks>
/// Simulates nothing, so it is not a hand-rolled fake clock (ADR-0089 D1): no timer here ever
/// runs its callback.
/// </remarks>
internal sealed class TimerRecordingTimeProvider : TimeProvider
{
    private readonly ConcurrentQueue<TimeSpan> _dueTimes = new();

    /// <summary>The due times of the timers requested so far, in request order.</summary>
    public IReadOnlyCollection<TimeSpan> RequestedDueTimes => _dueTimes.ToArray();

    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period
    )
    {
        _dueTimes.Enqueue(dueTime);
        return new InertTimer();
    }

    private sealed class InertTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose() { }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
