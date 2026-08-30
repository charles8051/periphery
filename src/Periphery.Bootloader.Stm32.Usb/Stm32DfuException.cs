// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Bootloader.Stm32.Usb;

/// <summary>A DFU protocol / flashing failure, optionally carrying the device status that caused it.</summary>
public sealed class Stm32DfuException : BootloaderException
{
    public Stm32DfuException(string message) : base(message) { }

    public Stm32DfuException(string message, DfuStatus status) : base(message) => Status = status;

    /// <summary>The DFU status reported by the device when the failure was detected, if any.</summary>
    public DfuStatus? Status { get; }
}
