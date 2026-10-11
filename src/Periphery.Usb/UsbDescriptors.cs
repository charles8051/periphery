// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Buffers.Binary;
using System.Collections.Immutable;

namespace Periphery.Usb;

/// <summary>
/// Pure, platform-independent decode of the standard USB descriptor byte
/// layouts (USB 2.0 spec §9.6). Total functions over a raw descriptor
/// <see cref="ReadOnlySpan{T}"/>: same bytes in, same value out, no IO, no
/// handle, no platform call.
/// </summary>
/// <remarks>
/// This is the USB analogue of <c>Periphery.Hid.HidReportDescriptor.Parse</c>:
/// the byte-level decode lives here so it is reachable (and golden-tested)
/// without real hardware, while the platform backends
/// (<c>Windows.WinUsbBackend</c>, <c>Linux.LibUsbBackend</c>) keep only the
/// raw-byte <i>fetch</i> in their IO shell and hand the bytes here. The 18-byte
/// device-descriptor and 9-byte configuration-descriptor headers are fixed by
/// the spec and identical on every platform, so the decode belongs in the pure
/// core (ADR-0052, functional core / imperative shell).
/// </remarks>
internal static class UsbDescriptors
{
    /// <summary>Length of the standard device descriptor (<c>bLength</c> for a DEVICE descriptor).</summary>
    internal const int DeviceDescriptorLength = 18;

    /// <summary>Length of the configuration descriptor header (<c>bLength</c> for a CONFIGURATION descriptor).</summary>
    internal const int ConfigurationHeaderLength = 9;

    /// <summary>
    /// Decodes the standard 18-byte USB device descriptor (USB 2.0 §9.6.1) into
    /// an immutable <see cref="UsbDeviceDescriptor"/>. The multi-byte fields
    /// (<c>bcdUSB</c>, <c>idVendor</c>, <c>idProduct</c>, <c>bcdDevice</c>) are
    /// little-endian per the spec.
    /// </summary>
    /// <param name="raw">
    /// The raw descriptor bytes as returned by <c>GET_DESCRIPTOR(DEVICE)</c>.
    /// Must be at least <see cref="DeviceDescriptorLength"/> bytes; only the
    /// standard 18-byte prefix is read, so a longer span is accepted and its
    /// tail ignored.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="raw"/> is shorter than <see cref="DeviceDescriptorLength"/>.
    /// </exception>
    public static UsbDeviceDescriptor ParseDeviceDescriptor(ReadOnlySpan<byte> raw)
    {
        if (raw.Length < DeviceDescriptorLength)
            throw new ArgumentException(
                $"A USB device descriptor is {DeviceDescriptorLength} bytes; got {raw.Length}.",
                nameof(raw));

        // Layout (offset: field):
        //  0 bLength            1 bDescriptorType
        //  2 bcdUSB (LE u16)
        //  4 bDeviceClass       5 bDeviceSubClass     6 bDeviceProtocol
        //  7 bMaxPacketSize0
        //  8 idVendor (LE u16) 10 idProduct (LE u16)
        // 12 bcdDevice (LE u16)
        // 14 iManufacturer     15 iProduct           16 iSerialNumber
        // 17 bNumConfigurations
        return new UsbDeviceDescriptor
        {
            UsbVersion = BinaryPrimitives.ReadUInt16LittleEndian(raw.Slice(2)),
            DeviceClass = raw[4],
            DeviceSubClass = raw[5],
            DeviceProtocol = raw[6],
            MaxPacketSize0 = raw[7],
            VendorId = new HardwareId(BinaryPrimitives.ReadUInt16LittleEndian(raw.Slice(8))),
            ProductId = new HardwareId(BinaryPrimitives.ReadUInt16LittleEndian(raw.Slice(10))),
            DeviceVersion = BinaryPrimitives.ReadUInt16LittleEndian(raw.Slice(12)),
            ConfigurationCount = raw[17],
        };
    }

    /// <summary>
    /// Decodes the platform-independent fields of the 9-byte configuration
    /// descriptor header (USB 2.0 §9.6.3): <c>bConfigurationValue</c> and
    /// <c>bMaxPower</c> (expanded from 2 mA units to milliamps). Interface and
    /// endpoint enumeration is intentionally left to the backends, which read
    /// it from their platform query surface (WinUSB
    /// <c>QueryInterfaceSettings</c>/<c>QueryPipe</c>, libusb config structs)
    /// rather than re-walking the raw blob here.
    /// </summary>
    /// <param name="raw">
    /// The configuration-descriptor bytes as returned by
    /// <c>GET_DESCRIPTOR(CONFIGURATION)</c>. Must be at least
    /// <see cref="ConfigurationHeaderLength"/> bytes.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="raw"/> is shorter than <see cref="ConfigurationHeaderLength"/>.
    /// </exception>
    public static UsbConfigurationHeader ParseConfigurationHeader(ReadOnlySpan<byte> raw)
    {
        if (raw.Length < ConfigurationHeaderLength)
            throw new ArgumentException(
                $"A USB configuration descriptor header is {ConfigurationHeaderLength} bytes; got {raw.Length}.",
                nameof(raw));

        // Layout (offset: field):
        //  0 bLength            1 bDescriptorType
        //  2 wTotalLength (LE u16)
        //  4 bNumInterfaces
        //  5 bConfigurationValue
        //  6 iConfiguration
        //  7 bmAttributes
        //  8 bMaxPower (2 mA units)
        return new UsbConfigurationHeader(
            ConfigurationValue: raw[5],
            InterfaceCount: raw[4],
            MaxPowerMilliamps: raw[8] * 2);
    }

    private const byte InterfaceDescriptorType = 0x04;
    private const byte EndpointDescriptorType = 0x05;
    private const int InterfaceDescriptorLength = 9;
    private const int EndpointDescriptorLength = 7;

    /// <summary>
    /// Decodes a whole configuration descriptor, as returned by
    /// <c>GET_DESCRIPTOR(CONFIGURATION)</c> with <c>wLength</c> set to the header's
    /// <c>wTotalLength</c>: the header, then alternate setting 0 of each interface with its
    /// endpoints, in the order the descriptor lists them.
    /// </summary>
    /// <remarks>
    /// <para>The result matches what <c>Linux.LibUsbBackend</c> builds from libusb's parsed
    /// structs: alternate setting 0 only, <c>wMaxPacketSize</c> as the raw 16-bit field, and the
    /// transfer type from the low two bits of <c>bmAttributes</c>. Used by backends with no
    /// platform query surface for interfaces, such as WebUSB.</para>
    /// <para>The walk follows each descriptor's <c>bLength</c>. Descriptors of other types (an
    /// interface association, or a class-specific descriptor) are skipped. It stops at the end of
    /// <paramref name="raw"/>, at a <c>bLength</c> below 2, or at a descriptor that runs past the
    /// end, keeping what it decoded before that. An endpoint that precedes every interface
    /// descriptor is ignored.</para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="raw"/> is shorter than <see cref="ConfigurationHeaderLength"/>.
    /// </exception>
    public static UsbConfigurationDescriptor ParseConfiguration(ReadOnlySpan<byte> raw)
    {
        var header = ParseConfigurationHeader(raw);

        var interfaces = ImmutableArray.CreateBuilder<UsbInterfaceDescriptor>();
        UsbInterfaceDescriptor? current = null;
        ImmutableArray<UsbEndpointDescriptor>.Builder? endpoints = null;

        void Flush()
        {
            if (current is not null)
                interfaces.Add(current with { Endpoints = endpoints!.ToImmutable() });
        }

        int offset = raw[0] >= ConfigurationHeaderLength ? raw[0] : ConfigurationHeaderLength;
        while (offset + 2 <= raw.Length)
        {
            int length = raw[offset];
            if (length < 2 || offset + length > raw.Length)
                break;

            var descriptor = raw.Slice(offset, length);
            offset += length;

            switch (descriptor[1])
            {
                case InterfaceDescriptorType when length >= InterfaceDescriptorLength:
                    Flush();
                    // Alternate settings other than 0 are skipped, endpoints and all.
                    if (descriptor[3] != 0)
                    {
                        current = null;
                        endpoints = null;
                        break;
                    }
                    current = new UsbInterfaceDescriptor
                    {
                        InterfaceNumber = descriptor[2],
                        AlternateSetting = descriptor[3],
                        InterfaceClass = descriptor[5],
                        InterfaceSubClass = descriptor[6],
                        InterfaceProtocol = descriptor[7],
                    };
                    endpoints = ImmutableArray.CreateBuilder<UsbEndpointDescriptor>(descriptor[4]);
                    break;

                case EndpointDescriptorType when length >= EndpointDescriptorLength && endpoints is not null:
                    endpoints.Add(new UsbEndpointDescriptor
                    {
                        EndpointAddress = descriptor[2],
                        TransferType = (UsbTransferType)(descriptor[3] & 0x3),
                        MaxPacketSize = BinaryPrimitives.ReadUInt16LittleEndian(descriptor.Slice(4)),
                        Interval = descriptor[6],
                    });
                    break;
            }
        }

        Flush();

        return new UsbConfigurationDescriptor
        {
            ConfigurationValue = header.ConfigurationValue,
            MaxPowerMilliamps = header.MaxPowerMilliamps,
            Interfaces = interfaces.ToImmutable(),
        };
    }
}

/// <summary>
/// The platform-independent fields of a USB configuration descriptor header,
/// decoded by <see cref="UsbDescriptors.ParseConfigurationHeader"/>. The
/// interface list is supplied separately by each backend's platform query
/// surface; this is just the header's scalar fields.
/// </summary>
internal readonly record struct UsbConfigurationHeader(
    byte ConfigurationValue,
    byte InterfaceCount,
    int MaxPowerMilliamps);
