// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Treehopper.Control;

/// <summary>
/// The identity payload carried by a discovery event — the bits the watcher (and an
/// optional version read) can know about a board without holding a live session.
/// </summary>
/// <param name="Id">Platform-native device id (stable key for a given connection).
/// Typed <see cref="Periphery.DeviceId"/>, not <c>string</c>: Windows re-enumerates the
/// same board with different casing in its instance id (issue #231), and
/// <c>DeviceId</c> compares <see cref="System.StringComparison.OrdinalIgnoreCase"/>.</param>
/// <param name="Serial">Serial number, if reported.</param>
/// <param name="Name">Device name, if reported.</param>
/// <param name="Version">Firmware version (raw bcdDevice code), if read.</param>
/// <param name="Connection">Whether the board is in the application or the bootloader.</param>
public sealed record BoardIdentity(
    DeviceId Id,
    string? Serial = null,
    string? Name = null,
    int? Version = null,
    BoardConnection Connection = BoardConnection.Application);
