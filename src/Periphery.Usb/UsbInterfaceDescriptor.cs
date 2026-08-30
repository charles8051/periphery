// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Collections.Immutable;

namespace Periphery.Usb;

/// <summary>
/// Immutable snapshot of a USB interface descriptor and its endpoints.
/// </summary>
public sealed record UsbInterfaceDescriptor
{
    /// <summary>The interface number within the configuration.</summary>
    public required byte InterfaceNumber { get; init; }

    /// <summary>The alternate setting this descriptor describes.</summary>
    public byte AlternateSetting { get; init; }

    /// <summary>USB interface class code (<c>bInterfaceClass</c>).</summary>
    public required byte InterfaceClass { get; init; }

    /// <summary>USB interface subclass code (<c>bInterfaceSubClass</c>).</summary>
    public byte InterfaceSubClass { get; init; }

    /// <summary>USB interface protocol code (<c>bInterfaceProtocol</c>).</summary>
    public byte InterfaceProtocol { get; init; }

    /// <summary>The endpoints exposed by this interface (excluding the implicit control endpoint 0).</summary>
    public ImmutableArray<UsbEndpointDescriptor> Endpoints { get; init; } =
        ImmutableArray<UsbEndpointDescriptor>.Empty;
}
