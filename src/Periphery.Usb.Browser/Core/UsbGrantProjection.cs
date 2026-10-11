// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Usb.Browser.Core;

/// <summary>Projects a WebUSB grant onto <see cref="DeviceInfo"/> (ADR-0092 D5).</summary>
internal static class UsbGrantProjection
{
    /// <summary>
    /// Fills the fields WebUSB has a source for and leaves every other field at its default.
    /// </summary>
    /// <remarks>
    /// <see cref="DeviceInfo.Status"/> is <see cref="DeviceStatus.Unknown"/> because the browser
    /// models no driver. Topology (<see cref="DeviceInfo.ParentId"/>,
    /// <see cref="DeviceInfo.PortNumber"/>, <see cref="DeviceInfo.LocationPath"/>) and
    /// <see cref="DeviceInfo.ContainerId"/> have no browser source and stay unset.
    /// </remarks>
    public static DeviceInfo ToDeviceInfo(UsbGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        var facts = grant.Facts;
        return new DeviceInfo
        {
            Id = grant.Id,
            Name = facts.ProductName,
            Manufacturer = facts.ManufacturerName,
            VendorId = new HardwareId(facts.VendorId),
            ProductId = new HardwareId(facts.ProductId),
            SerialNumber = facts.HasSerialNumber ? facts.SerialNumber : null,
            UsbClassCode = new UsbClassCode(facts.DeviceClass, facts.DeviceSubclass, facts.DeviceProtocol),
            BusType = BusType.USB,
            Status = DeviceStatus.Unknown,
        };
    }
}
