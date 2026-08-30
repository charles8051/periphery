// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Buffers.Binary;

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
