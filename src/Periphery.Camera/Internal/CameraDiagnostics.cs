// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Diagnostics.Metrics;
using System.Reflection;

namespace Periphery.Camera.Internal;

/// <summary>
/// The <c>Periphery.Camera</c> package's <see cref="Meter"/> and canonical
/// instruments. Per the logging-and-diagnostics standards
/// (<c>docs/patterns/logging-and-diagnostics.md</c>), each published
/// <c>Periphery.*</c> package exposes exactly one Meter named after the
/// package; instruments follow OpenTelemetry semantic-convention naming.
/// </summary>
internal static class CameraDiagnostics
{
    /// <summary>The single Meter for the <c>Periphery.Camera</c> package.</summary>
    internal static readonly Meter Meter = new(
        name: "Periphery.Camera",
        version: typeof(CameraDiagnostics).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(CameraDiagnostics).Assembly.GetName().Version?.ToString()
            ?? "0.0.0");

    /// <summary>
    /// Total camera frames delivered to consumers. Incremented at the same
    /// site as <see cref="CameraSession"/>'s internal frame counter — see
    /// the single-source-of-truth rule in the logging standards doc.
    /// </summary>
    internal static readonly Counter<long> FramesProduced = Meter.CreateCounter<long>(
        name: "periphery.camera.frames_produced",
        unit: "{frame}",
        description: "Camera frames delivered to consumers.");

    /// <summary>
    /// Total camera frames Periphery discarded under the configured
    /// <see cref="BufferExhaustionPolicy"/> — evicted from the delivery queue,
    /// or refused because no pool buffer was free. Frames the platform dropped
    /// upstream are not in here; nothing counts those.
    /// </summary>
    internal static readonly Counter<long> FramesDropped = Meter.CreateCounter<long>(
        name: "periphery.camera.frames_dropped",
        unit: "{frame}",
        description: "Camera frames dropped because the pipeline was full.");

    /// <summary>
    /// Times the producer parked because the pipeline was full. Recorded on
    /// entry to the stall, so a producer parked right now has already
    /// incremented this.
    /// </summary>
    internal static readonly Counter<long> ProducerStalls = Meter.CreateCounter<long>(
        name: "periphery.camera.producer_stalls",
        unit: "{stall}",
        description: "Times the capture producer parked waiting on the consumer.");

    /// <summary>
    /// How long each producer stall lasted. Recorded when the stall ends. The
    /// sum is capture time during which the platform's read was not being
    /// called, which is where the uncounted upstream loss happens (ADR-0082 D5).
    /// </summary>
    internal static readonly Histogram<double> ProducerStallDuration = Meter.CreateHistogram<double>(
        name: "periphery.camera.producer_stall_ms",
        unit: "ms",
        description: "Duration of each capture-producer stall.");

    /// <summary>
    /// Number of leased frames currently held by consumers (incremented
    /// on lease, decremented on dispose). Goes up and down — modeled as
    /// an <see cref="UpDownCounter{T}"/>.
    /// </summary>
    internal static readonly UpDownCounter<int> OutstandingLeases = Meter.CreateUpDownCounter<int>(
        name: "periphery.camera.outstanding_leases",
        unit: "{lease}",
        description: "Leased camera frames currently held by consumers.");
}
