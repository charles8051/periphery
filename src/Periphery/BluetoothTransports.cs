// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Text.Json.Serialization;

namespace Periphery;

/// <summary>
/// The Bluetooth transports a peripheral is known to support, as
/// <see cref="DeviceInfo.BluetoothTransports"/> reports them.
/// </summary>
/// <remarks>
/// The values match <see cref="BluetoothTransport"/>, so a single transport converts by a cast.
/// </remarks>
[Flags]
[JsonConverter(typeof(JsonStringEnumConverter<BluetoothTransports>))]
public enum BluetoothTransports
{
    /// <summary>The peripheral's transport is not known.</summary>
    None = 0,

    /// <summary>Classic Bluetooth.</summary>
    BrEdr = 1,

    /// <summary>Bluetooth Low Energy.</summary>
    LowEnergy = 2,
}
