// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Microsoft.Extensions.Logging;

namespace Periphery.Camera.Internal;

/// <summary>
/// Runs a wedge-prone teardown step with a hard budget and abandons it,
/// observably, when it overruns.
/// </summary>
/// <remarks>
/// <para>
/// A wedged USB camera driver can block any native call indefinitely: Media
/// Foundation's Flush, Shutdown, even Release; V4L2's close. So disposal must
/// complete in bounded time whether the driver cooperates or not, and a step
/// that overruns is left running on its thread-pool thread rather than awaited.
/// That reasoning is unchanged.
/// </para>
/// <para>
/// What changed (issue #123) is what an overrun looks like from outside. The
/// work used to be dropped with a line on <c>Console.Error</c>; nothing counted
/// it, no logger saw it, and nothing stopped the next open of the same device
/// from contending with the thread still holding it. Now an overrun increments
/// <see cref="CameraDiagnostics.TeardownsAbandoned"/>, logs at Warning through
/// the owner's logger, and registers the still-running task in
/// <see cref="PendingTeardowns"/> so a reopen fails fast naming it. Its eventual
/// completion is logged too, with its duration recorded in
/// <see cref="CameraDiagnostics.AbandonedTeardownDuration"/>, so "how often does
/// teardown wedge on this fleet, and for how long" has an answer.
/// </para>
/// <para>
/// The three budgets were three literals in three hand-copied versions of this
/// shell (review 2026-06 finding 2.5, issue #115). They are named here once.
/// </para>
/// </remarks>
internal static partial class BoundedTeardown
{
    /// <summary>Budget for the backend's stop-capture call (Flush, STREAMOFF).</summary>
    internal static readonly TimeSpan StopCaptureBudget = TimeSpan.FromSeconds(2);

    /// <summary>Budget for the producer task to leave its blocking read once the backend has stopped.</summary>
    internal static readonly TimeSpan ProducerExitBudget = TimeSpan.FromSeconds(2);

    /// <summary>Budget for the backend's own disposal (Shutdown, Release, close).</summary>
    internal static readonly TimeSpan BackendDisposeBudget = TimeSpan.FromSeconds(3);

    /// <summary>Step names as they appear in log records and in the metric tag.</summary>
    internal static class Steps
    {
        internal const string StopCapture = "stop_capture";
        internal const string ProducerExit = "producer_exit";
        internal const string BackendDispose = "backend_dispose";
    }

    /// <summary>Tag on both teardown instruments naming the step that overran.</summary>
    internal const string StepTag = "periphery.camera.teardown_step";

    /// <summary>
    /// Runs <paramref name="work"/> on the thread pool and waits up to
    /// <paramref name="budget"/> for it.
    /// </summary>
    /// <returns>
    /// <c>true</c> when the work completed within budget. A fault from completed
    /// work is logged at Debug and swallowed, because a teardown error is not
    /// something the disposing caller can act on. <c>false</c> when the work was
    /// abandoned.
    /// </returns>
    internal static async Task<bool> RunOrAbandonAsync(
        Func<Task> work, TimeSpan budget, string deviceId, string step, ILogger logger, TimeProvider clock)
    {
        var task = Task.Run(work);
        if (!await AwaitOrAbandonAsync(task, budget, deviceId, step, logger, clock).ConfigureAwait(false))
            return false;

        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogTeardownStepFaulted(logger, step, deviceId, ex);
        }

        return true;
    }

    /// <summary>
    /// Waits up to <paramref name="budget"/> for already-running
    /// <paramref name="work"/>.
    /// </summary>
    /// <returns>
    /// <c>true</c> when the work completed within budget; the caller then awaits
    /// it for its own outcome. <c>false</c> when it was abandoned. Never throws
    /// for the work's fault.
    /// </returns>
    internal static async Task<bool> AwaitOrAbandonAsync(
        Task work, TimeSpan budget, string deviceId, string step, ILogger logger, TimeProvider clock)
    {
        if (work.IsCompleted)
            return true;

        // Cancel the delay when the work wins, so its timer does not outlive the wait.
        using var timeout = new CancellationTokenSource();
        var winner = await Task.WhenAny(work, Task.Delay(budget, clock, timeout.Token)).ConfigureAwait(false);
        if (winner == work)
        {
            timeout.Cancel();
            return true;
        }

        Abandon(work, budget, deviceId, step, logger, clock);
        return false;
    }

    private static void Abandon(
        Task work, TimeSpan budget, string deviceId, string step, ILogger logger, TimeProvider clock)
    {
        var tag = new KeyValuePair<string, object?>(StepTag, step);
        var pending = PendingTeardowns.Register(deviceId, step, work, clock);
        CameraDiagnostics.TeardownsAbandoned.Add(1, tag);
        LogTeardownAbandoned(logger, step, deviceId, budget.TotalSeconds, pending.Steps.Count);

        long abandonedAt = clock.GetTimestamp();
        work.ContinueWith(
            t =>
            {
                var elapsed = clock.GetElapsedTime(abandonedAt);
                CameraDiagnostics.AbandonedTeardownDuration.Record(elapsed.TotalMilliseconds, tag);
                if (t.IsFaulted)
                    LogAbandonedTeardownFaulted(logger, step, deviceId, elapsed.TotalSeconds, t.Exception!.GetBaseException());
                else
                    LogAbandonedTeardownCompleted(logger, step, deviceId, elapsed.TotalSeconds);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    // ── Source-generated log methods ─────────────────────────────────

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Camera teardown step {Step} on {DeviceId} did not complete within {BudgetSec:F1}s; "
            + "abandoned to a background thread that still holds the device ({PendingSteps} step(s) pending). "
            + "Opens of this device are refused until it completes.")]
    private static partial void LogTeardownAbandoned(
        ILogger logger, string step, string deviceId, double budgetSec, int pendingSteps);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Abandoned camera teardown step {Step} on {DeviceId} completed after {ElapsedSec:F1}s")]
    private static partial void LogAbandonedTeardownCompleted(
        ILogger logger, string step, string deviceId, double elapsedSec);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Abandoned camera teardown step {Step} on {DeviceId} faulted after {ElapsedSec:F1}s")]
    private static partial void LogAbandonedTeardownFaulted(
        ILogger logger, string step, string deviceId, double elapsedSec, Exception ex);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Camera teardown step {Step} on {DeviceId} faulted; disposal continues")]
    private static partial void LogTeardownStepFaulted(
        ILogger logger, string step, string deviceId, Exception ex);
}
