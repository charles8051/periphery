// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Usb;

/// <summary>
/// Immutable snapshot of the standard USB device descriptor.
/// </summary>
public sealed record UsbDeviceDescriptor
{
    /// <summary>USB specification release in BCD (e.g. <c>0x0200</c> = USB 2.0).</summary>
    public required ushort UsbVersion { get; init; }

    /// <summary>Device class code (<c>bDeviceClass</c>); <c>0x00</c> means "defined at interface level".</summary>
    public required byte DeviceClass { get; init; }

    /// <summary>Device subclass code (<c>bDeviceSubClass</c>).</summary>
    public byte DeviceSubClass { get; init; }

    /// <summary>Device protocol code (<c>bDeviceProtocol</c>).</summary>
    public byte DeviceProtocol { get; init; }

    /// <summary>Maximum packet size for endpoint 0 (<c>bMaxPacketSize0</c>).</summary>
    public required byte MaxPacketSize0 { get; init; }

    /// <summary>USB Vendor ID (<c>idVendor</c>).</summary>
    public required HardwareId VendorId { get; init; }

    /// <summary>USB Product ID (<c>idProduct</c>).</summary>
    public required HardwareId ProductId { get; init; }

    /// <summary>Device release number in BCD (<c>bcdDevice</c>).</summary>
    public ushort DeviceVersion { get; init; }

    /// <summary>Number of configurations the device supports (<c>bNumConfigurations</c>).</summary>
    public byte ConfigurationCount { get; init; }
}
