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

    /// <summary>Tag naming the event whose handler threw.</summary>
    internal const string EventTag = "periphery.event";
}
