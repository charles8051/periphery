// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Periphery.Hid;

/// <summary>Platform abstraction for the four-method HID transfer surface.</summary>
internal interface IHidBackend : IAsyncDisposable
{
    ushort UsagePage { get; }
    ushort Usage { get; }
    int MaxInputReportLength { get; }
    int MaxOutputReportLength { get; }
    int MaxFeatureReportLength { get; }

    // ── Polling transfer: input + output reports ───────────────────────
    Task<HidReport> ReadReportAsync(CancellationToken ct);
    Task WriteReportAsync(HidReport report, CancellationToken ct);

    // ── Control-plane transfer: feature reports (ADR-0048) ─────────────
    //
    // Feature reports are the request/response channel of HID — vendor-
    // defined battery protocols (Megatec Q1), config / calibration reads,
    // and the standard HID Power Device Class status surface all live here.
    // Distinct from input/output reports both physically (separate HID
    // transfer type) and semantically (request/response vs. polling stream).
    Task<HidReport> ReadFeatureReportAsync(byte reportId, CancellationToken ct);
    Task WriteFeatureReportAsync(HidReport report, CancellationToken ct);
}
