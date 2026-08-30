// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Usb;

/// <summary>
/// USB endpoint transfer type, matching the low two bits of an endpoint
/// descriptor's <c>bmAttributes</c> field.
/// </summary>
public enum UsbTransferType
{
    /// <summary>Control transfer (endpoint 0).</summary>
    Control = 0,

    /// <summary>Isochronous transfer (streaming, no retry).</summary>
    Isochronous = 1,

    /// <summary>Bulk transfer (reliable, best-effort latency).</summary>
    Bulk = 2,

    /// <summary>Interrupt transfer (bounded latency, small payloads).</summary>
    Interrupt = 3,
}
