// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Usb.Browser.Core;

/// <summary>
/// Decodes <c>bmRequestType</c> into the <c>requestType</c> and <c>recipient</c> strings of a
/// WebUSB <c>USBControlTransferParameters</c>. WebUSB takes the two as names, not bits.
/// </summary>
internal static class WebUsbSetup
{
    /// <summary>
    /// The names for <paramref name="bmRequestType"/>, or <see langword="null"/> when it uses a
    /// reserved type (bits 6-5 = 3) or a reserved recipient (bits 4-0 above 3), which WebUSB cannot
    /// express. The direction bit is ignored; WebUSB takes it from the method called.
    /// </summary>
    public static (string RequestType, string Recipient)? Names(byte bmRequestType)
    {
        string? requestType = ((bmRequestType >> 5) & 0x3) switch
        {
            0 => "standard",
            1 => "class",
            2 => "vendor",
            _ => null,
        };

        string? recipient = (bmRequestType & 0x1F) switch
        {
            0 => "device",
            1 => "interface",
            2 => "endpoint",
            3 => "other",
            _ => null,
        };

        return requestType is null || recipient is null ? null : (requestType, recipient);
    }

    /// <summary>
    /// The names for <paramref name="setup"/> sent through the WebUSB method for
    /// <paramref name="method"/>: <c>controlTransferIn</c> for device-to-host,
    /// <c>controlTransferOut</c> for host-to-device. <see langword="null"/> when the setup's
    /// direction bit names the other method, or when <see cref="Names(byte)"/> has none.
    /// </summary>
    /// <remarks>
    /// WebUSB takes the direction from the method, so a setup sent through the wrong one would go
    /// to the device with its direction bit flipped.
    /// </remarks>
    public static (string RequestType, string Recipient)? NamesFor(UsbControlSetup setup, UsbTransferDirection method)
        => setup.Direction == method ? Names(setup.RequestType) : null;
}
