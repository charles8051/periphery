// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;

namespace Periphery.Bootloader;

/// <summary>A bootloader / flashing failure.</summary>
/// <remarks>
/// TODO(ADR-0024): derive from Periphery's <c>DeviceEnumerationException</c> per the
/// extension-package exception-hierarchy rule once the contract graduates. Deriving from
/// <see cref="Exception"/> keeps this stub dependency-light.
/// </remarks>
public class BootloaderException : Exception
{
    public BootloaderException(string message) : base(message) { }
    public BootloaderException(string message, Exception innerException) : base(message, innerException) { }
}
