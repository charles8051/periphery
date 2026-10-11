// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Serial.Browser;

/// <summary>
/// One filter for the browser's port chooser, the Web Serial <c>SerialPortFilter</c>. A port
/// matching any filter in the list is offered.
/// </summary>
public sealed record SerialPortRequestFilter
{
    /// <summary>The USB vendor ID of the port's adapter.</summary>
    public ushort? UsbVendorId { get; init; }

    /// <summary>The USB product ID of the port's adapter. Requires <see cref="UsbVendorId"/>.</summary>
    public ushort? UsbProductId { get; init; }

    /// <summary>A Bluetooth RFCOMM service class to offer ports of.</summary>
    public Guid? BluetoothServiceClassId { get; init; }
}

/// <summary>Parity for <see cref="SerialPortOpenOptions"/>.</summary>
public enum SerialPortParity
{
    /// <summary>No parity bit.</summary>
    None,

    /// <summary>Even parity, as the STM32 system bootloader uses.</summary>
    Even,

    /// <summary>Odd parity.</summary>
    Odd,
}

/// <summary>The line settings a port opens with, the Web Serial <c>SerialOptions</c>.</summary>
public sealed record SerialPortOpenOptions
{
    /// <summary>The baud rate.</summary>
    public required int BaudRate { get; init; }

    /// <summary>Data bits per character: 7 or 8.</summary>
    public int DataBits { get; init; } = 8;

    /// <summary>Stop bits: 1 or 2.</summary>
    public int StopBits { get; init; } = 1;

    /// <summary>The parity bit.</summary>
    public SerialPortParity Parity { get; init; } = SerialPortParity.None;

    /// <summary>Whether RTS/CTS hardware flow control is on.</summary>
    public bool HardwareFlowControl { get; init; }

    /// <summary>The size in bytes of the browser's read and write buffers.</summary>
    public int BufferSize { get; init; } = 255;
}
