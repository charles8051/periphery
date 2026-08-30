// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Usb;

/// <summary>
/// Direction of a USB transfer, matching bit 7 of an endpoint address and the
/// direction bit of a control-transfer <c>bmRequestType</c>.
/// </summary>
public enum UsbTransferDirection
{
    /// <summary>Host → device (OUT). Endpoint address / request-type bit 7 clear.</summary>
    HostToDevice = 0,

    /// <summary>Device → host (IN). Endpoint address / request-type bit 7 set.</summary>
    DeviceToHost = 1,
}
