// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;

namespace Periphery.Bootloader.Stm32.Serial;

/// <summary>An AN3155 (STM32 UART bootloader) protocol or flashing failure.</summary>
public sealed class Stm32SerialException : BootloaderException
{
    public Stm32SerialException(string message) : base(message) { }

    public Stm32SerialException(string message, Exception innerException) : base(message, innerException) { }
}
