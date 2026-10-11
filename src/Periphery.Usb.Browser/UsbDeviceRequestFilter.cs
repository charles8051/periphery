// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Usb.Browser;

/// <summary>
/// One filter for the browser's device chooser, the WebUSB <c>USBDeviceFilter</c>. A device is
/// offered when it matches every field that is set. A device matching any filter in the list is
/// offered.
/// </summary>
/// <remarks>
/// WebUSB requires <see cref="VendorId"/> with <see cref="ProductId"/>, <see cref="ClassCode"/>
/// with <see cref="SubclassCode"/>, and <see cref="SubclassCode"/> with <see cref="ProtocolCode"/>.
/// <see cref="BrowserUsb.RequestDeviceAsync"/> checks this before it opens the chooser.
/// </remarks>
public sealed record UsbDeviceRequestFilter
{
    /// <summary>The USB vendor ID to match.</summary>
    public HardwareId? VendorId { get; init; }

    /// <summary>The USB product ID to match. Requires <see cref="VendorId"/>.</summary>
    public HardwareId? ProductId { get; init; }

    /// <summary>The class code to match, on the device or on any of its interfaces.</summary>
    public byte? ClassCode { get; init; }

    /// <summary>The subclass code to match. Requires <see cref="ClassCode"/>.</summary>
    public byte? SubclassCode { get; init; }

    /// <summary>The protocol code to match. Requires <see cref="SubclassCode"/>.</summary>
    public byte? ProtocolCode { get; init; }

    /// <summary>The serial number to match.</summary>
    public string? SerialNumber { get; init; }
}
