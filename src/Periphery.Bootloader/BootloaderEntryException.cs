// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;

namespace Periphery.Bootloader;

/// <summary>
/// The app-to-bootloader mode switch failed: the expected bootloader did not re-enumerate within
/// the timeout, or the safety gate refused a device that did not match the entry's
/// <see cref="IBootloaderEntry.ExpectedBootloader"/>. Distinct from a flash-protocol failure (which
/// the flash callback surfaces), this is a failure of the <see cref="BootloaderEntryOrchestrator"/>
/// reboot/wait/gate stage — before any byte is written.
/// </summary>
public sealed class BootloaderEntryException : BootloaderException
{
    public BootloaderEntryException(string message) : base(message) { }
    public BootloaderEntryException(string message, Exception innerException) : base(message, innerException) { }
}
