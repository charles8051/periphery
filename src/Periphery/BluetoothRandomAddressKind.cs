// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery;

/// <summary>
/// The sub-type of an LE random device address, given by its two most significant bits
/// (Bluetooth Core Specification, Vol 6, Part B, §1.3.2). Read with
/// <see cref="BluetoothAddress.ClassifyAsRandom"/>.
/// </summary>
public enum BluetoothRandomAddressKind
{
    /// <summary>Top bits <c>00</c>. Changes periodically and cannot be resolved to an identity.</summary>
    NonResolvablePrivate = 0b00,

    /// <summary>Top bits <c>01</c>. Rotates. A host holding the device's IRK resolves it to the device's identity address.</summary>
    ResolvablePrivate = 0b01,

    /// <summary>Top bits <c>10</c>. Reserved by the specification.</summary>
    Reserved = 0b10,

    /// <summary>Top bits <c>11</c>. Fixed at least until the device power-cycles, and usually for its lifetime.</summary>
    Static = 0b11,
}
