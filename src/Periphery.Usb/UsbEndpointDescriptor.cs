// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Usb;

/// <summary>
/// Immutable snapshot of a USB endpoint descriptor.
/// </summary>
public sealed record UsbEndpointDescriptor
{
    /// <summary>The raw endpoint address, including the direction bit (bit 7).</summary>
    public required byte EndpointAddress { get; init; }

    /// <summary>Transfer direction, decoded from bit 7 of <see cref="EndpointAddress"/>.</summary>
    public UsbTransferDirection Direction =>
        (EndpointAddress & 0x80) != 0
            ? UsbTransferDirection.DeviceToHost
            : UsbTransferDirection.HostToDevice;

    /// <summary>Endpoint transfer type.</summary>
    public required UsbTransferType TransferType { get; init; }

    /// <summary>Maximum packet size in bytes.</summary>
    public required int MaxPacketSize { get; init; }

    /// <summary>Polling interval (in frames / microframes) for interrupt and isochronous endpoints.</summary>
    public byte Interval { get; init; }
}
