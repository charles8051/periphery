// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.Versioning;

namespace Periphery.Usb.Linux;

/// <summary>
/// Pure mapping from libusb's native descriptor structs to Periphery's
/// platform-neutral descriptor records. The Linux counterpart to
/// <see cref="UsbDescriptors"/>: where the Windows backend fetches raw bytes
/// and decodes them with <see cref="UsbDescriptors.ParseDeviceDescriptor"/>,
/// libusb hands back an already-parsed <see cref="LibUsbInterop.DeviceDescriptor"/>
/// struct, so the deepening here is the same one — keep the field mapping a
/// pure, separately-callable function rather than fusing it into the open path
/// (ADR-0052, functional core / imperative shell).
/// </summary>
[SupportedOSPlatform("linux")]
internal static class LibUsbDescriptors
{
    /// <summary>
    /// Maps a native <c>libusb_device_descriptor</c> onto an immutable
    /// <see cref="UsbDeviceDescriptor"/>. Pure: a value-to-value transform with
    /// no IO or handle. libusb has already byte-decoded the multi-byte fields
    /// (<c>bcdUSB</c>, <c>idVendor</c>, <c>idProduct</c>, <c>bcdDevice</c>) into
    /// host-order scalars, so this is the field rename that
    /// <see cref="UsbDescriptors.ParseDeviceDescriptor"/> arrives at from raw
    /// bytes on Windows.
    /// </summary>
    public static UsbDeviceDescriptor ToDeviceDescriptor(in LibUsbInterop.DeviceDescriptor raw) => new()
    {
        UsbVersion = raw.BcdUsb,
        DeviceClass = raw.DeviceClass,
        DeviceSubClass = raw.DeviceSubClass,
        DeviceProtocol = raw.DeviceProtocol,
        MaxPacketSize0 = raw.MaxPacketSize0,
        VendorId = new HardwareId(raw.IdVendor),
        ProductId = new HardwareId(raw.IdProduct),
        DeviceVersion = raw.BcdDevice,
        ConfigurationCount = raw.NumConfigurations,
    };
}
