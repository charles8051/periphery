// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Collections.Immutable;

namespace Periphery.Treehopper.Control;

/// <summary>
/// The app's complete view of one board: identity, connection, the 20-pin live state,
/// firmware sub-state, and the latest I2C scan. Immutable; folded by the reducer.
/// </summary>
/// <param name="Id">Platform-native device id (the board's key in <see cref="AppState.Boards"/>).</param>
/// <param name="Serial">Serial number, if reported.</param>
/// <param name="Name">Device name, if reported.</param>
/// <param name="Version">Firmware version (raw bcdDevice code), if read.</param>
/// <param name="Connection">Application or bootloader.</param>
/// <param name="Pins">The 20 pins (index 0–19).</param>
/// <param name="Firmware">Firmware status / progress.</param>
/// <param name="I2cResponders">Addresses that ACKed the last I2C scan, or null if never scanned.</param>
/// <param name="I2cScanning">True while an I2C scan is in flight.</param>
/// <param name="LastError">The most recent operation error for this board, if any.</param>
public sealed record BoardView(
    DeviceId Id,
    string? Serial,
    string? Name,
    int? Version,
    BoardConnection Connection,
    ImmutableArray<PinView> Pins,
    FirmwareView Firmware,
    ImmutableArray<byte>? I2cResponders = null,
    bool I2cScanning = false,
    string? LastError = null)
{
    /// <summary>Number of I/O pins on a Treehopper (mirrors <c>TreehopperWire.PinCount</c>).</summary>
    public const int PinCount = 20;

    /// <summary>A stable display label: serial if present, else the device id.</summary>
    public string Label => string.IsNullOrWhiteSpace(Serial) ? Id.Value : Serial!;

    /// <summary>Creates a fresh board view from a discovery identity, with all pins reserved.</summary>
    public static BoardView FromIdentity(BoardIdentity id)
    {
        var pins = ImmutableArray.CreateBuilder<PinView>(PinCount);
        for (int i = 0; i < PinCount; i++)
            pins.Add(new PinView(i, PinMode.Reserved, High: false, Adc: 0));

        return new BoardView(
            id.Id, id.Serial, id.Name, id.Version, id.Connection,
            pins.MoveToImmutable(), FirmwareView.Initial);
    }

    /// <summary>
    /// Re-derives the idle firmware status from <see cref="Version"/> and
    /// <paramref name="target"/>. No-op while a flash is in progress
    /// (<see cref="FirmwareStatus.Updating"/>), so live progress is never clobbered.
    /// </summary>
    internal BoardView WithIdleFirmware(int? target)
    {
        if (Firmware.Status == FirmwareStatus.Updating)
            return this;
        return this with { Firmware = new FirmwareView(FirmwareView.DeriveIdle(Version, target)) };
    }
}
