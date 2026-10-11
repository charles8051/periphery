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
}
