// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Ble.InTheHand;

/// <summary>
/// A <see cref="BleDeviceProxy"/> could not open or keep a GATT session: the device did not resolve,
/// did not connect, or its link dropped. <see cref="Exception.InnerException"/> carries 32feet's
/// exception, when there is one.
/// </summary>
public sealed class BleException : Exception
{
    /// <summary>Creates the exception with a message.</summary>
    public BleException(string message)
        : base(message) { }

    /// <summary>Creates the exception with a message and the exception that caused it.</summary>
    public BleException(string message, Exception innerException)
        : base(message, innerException) { }
}
