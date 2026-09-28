// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery;

/// <summary>
/// The Bluetooth transport a device node belongs to. BR/EDR and LE devices sit on different
/// node shapes and are served by different libraries (ADR-0085 D2).
/// </summary>
public enum BluetoothTransport
{
    /// <summary>Not determined.</summary>
    Unknown = 0,

    /// <summary>Classic Bluetooth. On Windows, a <c>BTHENUM\DEV_…</c> node.</summary>
    BrEdr,

    /// <summary>Bluetooth Low Energy. On Windows, a <c>BTHLE\DEV_…</c> node.</summary>
    LowEnergy,
}
