// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Diagnostics.Metrics;
using System.Reflection;

namespace Periphery;

/// <summary>
/// The core <c>Periphery</c> package's <see cref="Meter"/> and its instruments.
/// Per <c>docs/patterns/logging-and-diagnostics.md</c> §7, each published
/// <c>Periphery.*</c> package exposes exactly one Meter named after the package;
/// instruments follow OpenTelemetry semantic-convention naming
/// (<c>periphery.&lt;subsystem&gt;.&lt;measure&gt;</c>).
/// </summary>
/// <remarks>
/// The standards doc reserved <c>"Periphery"</c> as this package's meter name
/// from the start, but nothing had claimed it — the core package shipped without
/// a Meter while <c>Periphery.Camera</c> had one. Issue #225 is what finally
/// needed it: an isolated handler fault is swallowed, and a log record alone is
/// not a signal a caller can rely on, because a logging configuration above Error
/// for the category drops it silently.
/// </remarks>
internal static class PeripheryDiagnostics
{
    /// <summary>The single Meter for the core <c>Periphery</c> package.</summary>
    internal static readonly Meter Meter = new(
        name: "Periphery",
        version: typeof(PeripheryDiagnostics).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(PeripheryDiagnostics).Assembly.GetName().Version?.ToString()
            ?? "0.0.0");

    /// <summary>
    /// Event-handler exceptions caught and swallowed by <see cref="EventIsolation"/>
    /// so they could not unwind the platform provider's notification pump
    /// (issue #143). Tagged with <c>periphery.event</c>, the name of the event
    /// whose subscriber threw.
    /// </summary>
    /// <remarks>
    /// The counter, not the log record, is the signal that survives
    /// configuration. It is incremented at the same site that logs, so the two
    /// cannot drift — the single-source-of-truth rule in the standards doc. A
    /// non-zero value means consumer code is failing somewhere a crash would
    /// previously have made obvious; the log record carries which handler, and
    /// the tag carries which event.
    /// </remarks>
    internal static readonly Counter<long> HandlerFaults = Meter.CreateCounter<long>(
        name: "periphery.events.handler_faults",
        unit: "{fault}",
        description: "Event-handler exceptions caught and swallowed to protect the notification pump.");

    /// <summary>
    /// Exceptions thrown by a notified <see cref="DeviceTracker"/> or
    /// <see cref="MultiDeviceTracker"/> while the watcher was fanning an event out
    /// to it, caught so the remaining targets still hear about the device
    /// (issue #143). Tagged with <c>periphery.event</c>.
    /// </summary>
    /// <remarks>
    /// Deliberately not folded into <see cref="HandlerFaults"/>. A tracker's own
    /// event subscribers are isolated inside the tracker, and a fault there lands
    /// on <see cref="HandlerFaults"/> under that tracker's event name. What
    /// reaches the watcher's fan-out is the tracker failing in its own code —
    /// a library-internal fault, not consumer code misbehaving. Counting the two
    /// together would make the more alarming of them unfindable.
    /// </remarks>
    internal static readonly Counter<long> TargetFaults = Meter.CreateCounter<long>(
        name: "periphery.events.target_faults",
        unit: "{fault}",
        description: "Exceptions thrown by a tracker being notified of a device event.");

    /// <summary>
    /// Exceptions thrown by a caller-supplied filter predicate
    /// (<see cref="DeviceFilter.Where(System.Func{DeviceInfo, bool})"/>) while the
    /// watcher was deciding whether a device matched (issue #229). Tagged with
    /// <c>periphery.event</c> and with <see cref="FilterFallbackTag"/>, the answer
    /// used in the predicate's place.
    /// </summary>
    /// <remarks>
    /// Kept apart from <see cref="HandlerFaults"/> even though both are consumer
    /// code, because the consequence differs and so does the response. A handler
    /// that throws loses one notification. A filter that throws makes the watcher
    /// guess about membership, and the guess is directional — see
    /// <c>DeviceWatcher.MatchesIsolated</c>. The tag says which way it went, so a
    /// run of suppressed arrivals and a run of announced removals are
    /// distinguishable rather than one undifferentiated number.
    /// </remarks>
    internal static readonly Counter<long> FilterFaults = Meter.CreateCounter<long>(
        name: "periphery.events.filter_faults",
        unit: "{fault}",
        description: "Exceptions thrown by a caller-supplied filter predicate during a device event.");

    /// <summary>Tag naming the event whose handler, target or filter threw.</summary>
    internal const string EventTag = "periphery.event";

    /// <summary>
    /// Tag on <see cref="FilterFaults"/> recording the answer substituted for the
    /// predicate: <c>announced</c> or <c>suppressed</c>.
    /// </summary>
    internal const string FilterFallbackTag = "periphery.filter_fallback";
}
