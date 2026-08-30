// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Text.Json.Serialization;

namespace Periphery;

/// <summary>
/// The intended usage category of a display monitor.
/// <para>
/// <b>No provider populates this today, on any platform</b>, so
/// <see cref="DeviceInfo.DisplayUsageKind"/> is currently always
/// <see langword="null"/>. HMD classification came from the WinRT enricher that
/// ADR-0018 replaced with Win32 DisplayConfig, which has no equivalent
/// (ADR-0018 NEG-002). It is retained as a modelled contract slot for a future
/// backend rather than deleted (ADR-0071).
/// </para>
/// <para>
/// This is a statement about the backends that exist now, not a claim that the
/// value is unknowable. A backend that gains a source for it (a Linux DDC/CI or
/// vendor-proprietary route, a future Win32 API) should populate it and update
/// this remark together with ADR-0071 Decision 3.
/// </para>
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<DisplayUsageKind>))]
public enum DisplayUsageKind
{
    /// <summary>Usage kind could not be determined.</summary>
    Unknown = 0,

    /// <summary>Standard desktop or laptop monitor.</summary>
    Standard,

    /// <summary>Head-mounted display (VR/AR headset).</summary>
    HeadMounted,

    /// <summary>Special-purpose display (kiosk, signage, projector, etc.).</summary>
    SpecialPurpose,
}
