// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Treehopper.Control;

/// <summary>A board's firmware status as the app understands it.</summary>
public enum FirmwareStatus
{
    /// <summary>Version and/or target unknown — can't tell.</summary>
    Unknown,

    /// <summary>At or above the target version.</summary>
    UpToDate,

    /// <summary>Below the target version — an update is available.</summary>
    UpdateAvailable,

    /// <summary>A flash is in progress (see <see cref="FirmwareView.Percent"/>).</summary>
    Updating,

    /// <summary>The most recent flash succeeded.</summary>
    Updated,

    /// <summary>The most recent flash failed (see <see cref="FirmwareView.Message"/>).</summary>
    Failed,
}

/// <summary>The firmware sub-state of one board.</summary>
/// <param name="Status">Current status.</param>
/// <param name="Percent">Flash progress 0–100 while <see cref="FirmwareStatus.Updating"/>; else null.</param>
/// <param name="Message">Failure detail while <see cref="FirmwareStatus.Failed"/>; else null.</param>
public sealed record FirmwareView(
    FirmwareStatus Status = FirmwareStatus.Unknown,
    int? Percent = null,
    string? Message = null)
{
    /// <summary>The starting firmware view (status unknown).</summary>
    public static readonly FirmwareView Initial = new();

    /// <summary>
    /// Derives the idle status from a board's current version and the app's target
    /// version (raw bcdDevice codes). Below target → update available; at/above → up to date.
    /// </summary>
    public static FirmwareStatus DeriveIdle(int? version, int? target) =>
        version is null || target is null ? FirmwareStatus.Unknown
        : version < target ? FirmwareStatus.UpdateAvailable
        : FirmwareStatus.UpToDate;
}
