// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Usb.Browser.Core;

/// <summary>
/// The shell's token for one WebUSB <c>USBDevice</c> object. The pure core never holds a JS
/// object; the shell keeps the map from handle to object.
/// </summary>
/// <remarks>
/// A replug produces a new <c>USBDevice</c> object, so it gets a new handle. The handle is not an
/// identity; <see cref="UsbGrant.Id"/> is.
/// </remarks>
internal readonly record struct GrantHandle(int Value);

/// <summary>What a granted WebUSB device reports about itself, read from its <c>USBDevice</c>.</summary>
internal sealed record UsbGrantFacts
{
    /// <summary><c>USBDevice.vendorId</c>.</summary>
    public required ushort VendorId { get; init; }

    /// <summary><c>USBDevice.productId</c>.</summary>
    public required ushort ProductId { get; init; }

    /// <summary>
    /// <c>USBDevice.serialNumber</c>: <see langword="null"/> when the device has no serial-number
    /// string descriptor.
    /// </summary>
    public string? SerialNumber { get; init; }

    /// <summary><c>USBDevice.productName</c>.</summary>
    public string? ProductName { get; init; }

    /// <summary><c>USBDevice.manufacturerName</c>.</summary>
    public string? ManufacturerName { get; init; }

    /// <summary><c>USBDevice.deviceClass</c>.</summary>
    public byte DeviceClass { get; init; }

    /// <summary><c>USBDevice.deviceSubclass</c>.</summary>
    public byte DeviceSubclass { get; init; }

    /// <summary><c>USBDevice.deviceProtocol</c>.</summary>
    public byte DeviceProtocol { get; init; }

    /// <summary>
    /// Whether Chromium can persist this device's grant. It keys a persisted grant on vendor ID,
    /// product ID and serial number, and persists none for a device without a serial.
    /// </summary>
    public bool HasSerialNumber => !string.IsNullOrEmpty(SerialNumber);

    /// <summary>
    /// Whether <paramref name="other"/> has the same persisted-grant key: vendor ID, product ID
    /// and serial number. Serials compare <see cref="StringComparison.OrdinalIgnoreCase"/>, as
    /// <see cref="DeviceId"/> does, so two ids that would compare equal are always twins here.
    /// </summary>
    public bool SharesGrantKeyWith(UsbGrantFacts other)
        => HasSerialNumber
           && other.HasSerialNumber
           && VendorId == other.VendorId
           && ProductId == other.ProductId
           && string.Equals(SerialNumber, other.SerialNumber, StringComparison.OrdinalIgnoreCase);
}
