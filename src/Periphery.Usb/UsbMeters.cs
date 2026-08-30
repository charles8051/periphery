// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Diagnostics.Metrics;
using System.Reflection;

namespace Periphery.Usb;

/// <summary>
/// The <c>Periphery.Usb</c> package's <see cref="Meter"/> and canonical instruments,
/// per the repo logging-and-diagnostics standard: one Meter per package, named after
/// the package, with instruments following the OpenTelemetry semantic-convention style
/// (<c>periphery.&lt;subsystem&gt;.&lt;measure&gt;</c>, unit-suffixed). A consumer adds
/// OpenTelemetry / Prometheus / a <c>MeterListener</c> and filters by the Meter name to
/// get a clean per-package view — no log-string parsing required.
/// </summary>
internal static class UsbMeters
{
    /// <summary>The single Meter for the <c>Periphery.Usb</c> package.</summary>
    internal static readonly Meter Meter = new(
        name: "Periphery.Usb",
        version: typeof(UsbMeters).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(UsbMeters).Assembly.GetName().Version?.ToString()
            ?? "0.0.0");

    /// <summary>USB transfers that completed successfully.</summary>
    internal static readonly Counter<long> TransfersTotal = Meter.CreateCounter<long>(
        "periphery.usb.transfers_total",
        unit: "{transfer}",
        description: "USB transfers that completed successfully.");

    /// <summary>USB transfers that faulted or timed out.</summary>
    internal static readonly Counter<long> TransferErrorsTotal = Meter.CreateCounter<long>(
        "periphery.usb.transfer_errors_total",
        unit: "{transfer}",
        description: "USB transfers that faulted or timed out.");

    /// <summary>Latency of a USB transfer from issue to completion.</summary>
    internal static readonly Histogram<double> TransferDuration = Meter.CreateHistogram<double>(
        "periphery.usb.transfer_ms",
        unit: "ms",
        description: "USB transfer latency from issue to completion.");

    /// <summary>USB transfers currently in flight (issued to the backend, not yet completed).</summary>
    internal static readonly UpDownCounter<int> InFlightTransfers = Meter.CreateUpDownCounter<int>(
        "periphery.usb.in_flight_transfers",
        unit: "{transfer}",
        description: "USB transfers currently in flight.");

    /// <summary>
    /// Transfers waiting on their pipe's gate — admitted by the caller but not yet issued
    /// to the backend, because another transfer holds that endpoint (#263).
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="InFlightTransfers"/> on purpose: that instrument means
    /// "the backend is working on this", and folding queued callers into it would report
    /// saturation the hardware is not experiencing. Per-pipe serialisation made queueing
    /// possible for the first time, so it gets its own instrument rather than an existing
    /// one quietly changing meaning — depth here is the direct read on whether a write
    /// path is saturated (the LED-flush question in #260).
    /// </remarks>
    internal static readonly UpDownCounter<int> QueuedTransfers = Meter.CreateUpDownCounter<int>(
        "periphery.usb.queued_transfers",
        unit: "{transfer}",
        description: "USB transfers waiting for their pipe to become free.");

    /// <summary>
    /// Teardowns that gave up waiting for in-flight transfers to report back, and so
    /// deliberately released nothing rather than freeing native resources while a transfer
    /// may still be live (#263 item 2). On Windows that leaks the WinUSB interface handle,
    /// the device handle and the thread-pool bound handle; on Linux the libusb context, the
    /// device handle, the usbfs fd and the event-pump thread.
    /// </summary>
    /// <remarks>
    /// Should be flat zero. A non-zero value means a cancelled transfer never reported back —
    /// no IOCP completion packet on Windows, no LIBUSB_TRANSFER_CANCELLED callback on Linux —
    /// which is a driver-level anomaly worth chasing. It is also the only externally visible
    /// trace of the leak, so it exists to keep that choice honest rather than silent.
    /// </remarks>
    internal static readonly Counter<long> TeardownNotQuiescedTotal = Meter.CreateCounter<long>(
        "periphery.usb.teardown_not_quiesced_total",
        unit: "{teardown}",
        description: "Device teardowns that timed out waiting for in-flight transfers.");
}
