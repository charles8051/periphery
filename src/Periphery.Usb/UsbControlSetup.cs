// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Usb;

/// <summary>
/// The 8-byte SETUP header for a USB control transfer. The data-stage length is
/// supplied by the buffer passed to
/// <see cref="UsbDevice.ControlTransferAsync(UsbControlSetup, System.Memory{byte}, System.Threading.CancellationToken)"/>.
/// </summary>
public readonly record struct UsbControlSetup
{
    /// <summary><c>bmRequestType</c> — direction (bit 7), type (bits 6-5), recipient (bits 4-0).</summary>
    public required byte RequestType { get; init; }

    /// <summary><c>bRequest</c> — the request code.</summary>
    public required byte Request { get; init; }

    /// <summary><c>wValue</c> — request-specific value (e.g. descriptor type/index).</summary>
    public ushort Value { get; init; }

    /// <summary><c>wIndex</c> — request-specific index (often an interface or endpoint number).</summary>
    public ushort Index { get; init; }

    /// <summary>Transfer direction, decoded from bit 7 of <see cref="RequestType"/>.</summary>
    public UsbTransferDirection Direction =>
        (RequestType & 0x80) != 0
            ? UsbTransferDirection.DeviceToHost
            : UsbTransferDirection.HostToDevice;
}
