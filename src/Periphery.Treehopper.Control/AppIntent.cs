// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Treehopper.Control;

/// <summary>
/// What the user wants to do — emitted by a front-end, executed by the app service
/// (Phase 2), which performs the hardware work and emits the resulting
/// <see cref="AppEvent"/>(s). Intents have no reducer; they describe requests, not state
/// changes. A closed union (the reducer never sees these; the service's dispatch does).
/// </summary>
public abstract record AppIntent
{
    private protected AppIntent() { }

    /// <summary>Re-enumerate boards now (in addition to live hotplug).</summary>
    public sealed record RefreshBoards : AppIntent;

    /// <summary>Focus a board (null clears the selection).</summary>
    public sealed record SelectBoard(DeviceId? Id) : AppIntent;

    /// <summary>Set a pin's electrical mode.</summary>
    public sealed record SetPinMode(DeviceId Id, int Pin, PinMode Mode) : AppIntent;

    /// <summary>Drive an output pin high or low.</summary>
    public sealed record DriveOutput(DeviceId Id, int Pin, bool High) : AppIntent;

    /// <summary>Flip an output pin's level.</summary>
    public sealed record ToggleOutput(DeviceId Id, int Pin) : AppIntent;

    /// <summary>Scan the I2C bus for responding addresses.</summary>
    public sealed record ScanI2c(DeviceId Id) : AppIntent;

    /// <summary>Reflash a board's firmware (image/target resolved by the service).</summary>
    public sealed record UpdateFirmware(DeviceId Id) : AppIntent;
}
