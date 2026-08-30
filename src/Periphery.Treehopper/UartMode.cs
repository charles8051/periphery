// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Treehopper;

/// <summary>The operating mode of the hardware UART.</summary>
public enum UartMode : byte
{
    /// <summary>Standard RS-232-style asynchronous serial on the TX and RX pins.</summary>
    Uart = 0,

    /// <summary>
    /// 1-Wire mode: the TX pin becomes open-drain and TX/RX are tied together to form
    /// a single 1-Wire bus (Dallas/Maxim). Drives 1-Wire peripherals such as the DS18B20.
    /// </summary>
    OneWire = 1,
}
