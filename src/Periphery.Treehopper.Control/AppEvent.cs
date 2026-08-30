// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Collections.Immutable;

namespace Periphery.Treehopper.Control;

/// <summary>
/// Something that happened, folded into <see cref="AppState"/> by
/// <see cref="AppReducer.Reduce"/>. A closed union — the <c>private protected</c>
/// constructor prevents external variants, so the reducer's switch is total.
/// Events are produced by the app service (Phase 2) from hardware callbacks and from
/// applied intents.
/// </summary>
public abstract record AppEvent
{
    private protected AppEvent() { }

    /// <summary>A board appeared, or its known identity was refreshed.</summary>
    public sealed record BoardDiscovered(BoardIdentity Board) : AppEvent;

    /// <summary>A board disappeared from the bus.</summary>
    public sealed record BoardRemoved(DeviceId Id) : AppEvent;

    /// <summary>A board's firmware version was read.</summary>
    public sealed record BoardVersionRead(DeviceId Id, int Version) : AppEvent;

    /// <summary>The focused board changed (null = nothing selected).</summary>
    public sealed record SelectionChanged(DeviceId? Id) : AppEvent;

    /// <summary>A pin's host-believed mode changed (after a set-mode intent was applied).</summary>
    public sealed record PinModeChanged(DeviceId Id, int Pin, PinMode Mode) : AppEvent;

    /// <summary>
    /// An output pin was driven to a level. Sets the pin to push-pull output and records
    /// the host-known level immediately — the firmware reports input changes, not host-driven
    /// output changes, so the output's level is host-authoritative.
    /// </summary>
    public sealed record OutputDriven(DeviceId Id, int Pin, bool High) : AppEvent;

    /// <summary>A live pin-state report arrived from the board.</summary>
    public sealed record ReportReceived(DeviceId Id, BoardReport Report) : AppEvent;

    /// <summary>The app's firmware target version changed (re-derives every board's status).</summary>
    public sealed record FirmwareTargetSet(int? Target) : AppEvent;

    /// <summary>A flash began on a board.</summary>
    public sealed record FirmwareUpdateStarted(DeviceId Id) : AppEvent;

    /// <summary>Flash progress: <paramref name="RecordsSent"/> of <paramref name="TotalRecords"/>.</summary>
    public sealed record FirmwareProgressed(DeviceId Id, int RecordsSent, int TotalRecords) : AppEvent;

    /// <summary>A flash finished. On success, <paramref name="NewVersion"/> updates the board.</summary>
    public sealed record FirmwareUpdateFinished(
        DeviceId Id, bool Success, int? NewVersion = null, string? Message = null) : AppEvent;

    /// <summary>An I2C scan began on a board.</summary>
    public sealed record I2cScanStarted(DeviceId Id) : AppEvent;

    /// <summary>An I2C scan finished, listing the addresses that responded.</summary>
    public sealed record I2cScanFinished(DeviceId Id, ImmutableArray<byte> Responders) : AppEvent;

    /// <summary>An operation on a board failed; the message is surfaced on the board.</summary>
    public sealed record OperationFailed(DeviceId Id, string Message) : AppEvent;
}
