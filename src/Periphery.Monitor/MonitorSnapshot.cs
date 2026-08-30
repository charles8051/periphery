// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Monitor;

/// <summary>
/// Handle-gated monitor-control snapshot returned by
/// <see cref="MonitorDevice.ReadCapabilitiesAsync"/> (ADR-0026 Option D):
/// which control planes the monitor offers, its parsed MCCS capabilities
/// when the DDC exchange succeeded, and the live mode state when the
/// display-mode plane exists.
/// </summary>
public sealed record MonitorSnapshot(
    bool SupportsVcp,
    bool SupportsDisplayMode,
    MccsCapabilities? Capabilities,
    DisplayMode? CurrentMode,
    MonitorOrientation? Orientation);
