// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using Microsoft.Extensions.Logging;

namespace Periphery;

/// <summary>
/// Raises an event one subscriber at a time, so a throwing subscriber neither
/// unwinds the thread that raised it nor suppresses the subscribers behind it
/// (issue #143).
/// </summary>
/// <remarks>
/// <para>
/// The delegate list is walked explicitly rather than invoked as a multicast
/// delegate, because a multicast invoke abandons the walk at the first throw —
/// which made surviving subscribers a function of registration order.
/// </para>
/// <para>
/// Shared by <see cref="DeviceWatcher"/>, <see cref="DeviceTracker"/> and
/// <see cref="MultiDeviceTracker"/> because the hazard is the same at all three
/// layers: the watcher raises on the platform provider's notification pump, and
/// a tracker's own events are raised from inside that raise, so an unisolated
/// tracker event reaches the pump just as surely as an unisolated watcher one.
/// </para>
/// <para>
/// <b>The failure policy, stated plainly.</b> A consumer exception is swallowed.
/// The only signal is an <see cref="LogLevel.Error"/> record, and that signal is
/// <em>not</em> guaranteed: a logger configured above Error for the category, no
/// provider registered at all, or a sink that drops the record each leave the
/// failure entirely invisible while the operation continues as though it
/// succeeded. This is a deliberate trade — an exception that reaches the
/// platform's notification pump takes the whole application's device view down
/// with it, which is worse than a lost log line — but it is a trade, and a
/// consumer whose handler can fail should not rely on this class to tell it.
/// Handle errors inside the handler.
/// </para>
/// <para>
/// Making that signal unconditional needs a counter alongside the log, which is
/// the pairing <c>docs/patterns/logging-and-diagnostics.md</c> §7 prescribes.
/// The core package has no <c>Meter</c> yet, so that is issue #225 rather than
/// something this type can assume.
/// </para>
/// </remarks>
internal static class EventIsolation
{
    /// <summary>
    /// Invokes each subscriber of <paramref name="handlers"/> in turn, logging
    /// and continuing past any that throws.
    /// </summary>
    /// <param name="sender">Passed to each subscriber as the event sender.</param>
    /// <param name="handlers">The event's backing delegate; null is a no-op.</param>
    /// <param name="args">The event payload.</param>
    /// <param name="logger">Where a fault is recorded.</param>
    /// <param name="eventName">Event name, for the log record.</param>
    /// <param name="context">Device id or other locator, for the log record.</param>
    internal static void Raise<TArgs>(
        object sender,
        EventHandler<TArgs>? handlers,
        TArgs args,
        ILogger logger,
        string eventName,
        string context)
    {
        if (handlers is null)
            return;

        var subscribers = handlers.GetInvocationList();
        for (int i = 0; i < subscribers.Length; i++)
        {
            try
            {
                ((EventHandler<TArgs>)subscribers[i])(sender, args);
            }
            catch (Exception ex)
            {
                LogHandlerFaulted(logger, ex, eventName, context, subscribers[i], i, subscribers.Length);
            }
        }
    }

    /// <summary>
    /// Records an isolated handler fault. <b>Never throws.</b>
    /// </summary>
    /// <remarks>
    /// The logging call is itself guarded, because this runs inside the catch
    /// that keeps a handler off the provider's notification pump. An
    /// <see cref="ILogger"/> that throws — a sink that is down, a custom
    /// implementation with a bug, a formatter that faults on one of these
    /// arguments — would otherwise escape from the very place the isolation is
    /// being reported and unwind the pump anyway, which is the defect this
    /// machinery exists to prevent. There is nowhere left to report a failure of
    /// the reporting path, so it is swallowed.
    /// </remarks>
    internal static void LogHandlerFaulted(
        ILogger logger, Exception ex, string eventName, string context,
        Delegate handler, int index, int total)
    {
        try
        {
            var method = handler.Method;
            logger.LogError(
                ex,
                "A {EventName} handler threw and was isolated; the remaining subscribers still ran. "
                    + "Context={Context} Handler={HandlerType}.{HandlerMethod} ({Index} of {Total})",
                eventName,
                context,
                method.DeclaringType?.FullName ?? "(unknown)",
                method.Name,
                index + 1,
                total);
        }
        catch
        {
            // Deliberately empty — see the remarks above.
        }
    }

    /// <summary>
    /// Records an isolated fault from a notified target that is not an event
    /// subscriber (a tracker, a group tracker). <b>Never throws</b>, for the same
    /// reason as <see cref="LogHandlerFaulted"/>.
    /// </summary>
    internal static void LogTargetFaulted(
        ILogger logger, Exception ex, string eventName, string kind, string? name, string context)
    {
        try
        {
            logger.LogError(
                ex,
                "A {Kind} threw while being notified of {EventName} and was isolated; the remaining "
                    + "{Kind}s still ran. Name={TargetName} Context={Context}",
                kind, eventName, kind, name ?? "(unnamed)", context);
        }
        catch
        {
            // Deliberately empty — see LogHandlerFaulted's remarks.
        }
    }
}
