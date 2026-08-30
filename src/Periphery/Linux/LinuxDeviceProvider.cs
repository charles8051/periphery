// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Periphery.Linux;

/// <summary>
/// Linux implementation of <see cref="IDeviceProvider"/> using libudev
/// (<c>libudev.so.1</c>) for device enumeration and property retrieval.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class LinuxDeviceProvider : IDeviceProvider
{
    private static readonly ILogger<LinuxDeviceProvider> _logger =
        PeripheryLoggerFactory.CreateLogger<LinuxDeviceProvider>();

    public async IAsyncEnumerable<DeviceInfo> EnumerateAsync(
        DeviceFilter filter,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        _logger.LogDebug("Starting device enumeration via libudev");

        IntPtr udev = IntPtr.Zero;
        try
        {
            udev = UdevInterop.udev_new();
            if (udev == IntPtr.Zero)
                throw new DeviceProviderException("Failed to create udev context. Is libudev.so.1 available?");
        }
        catch (DllNotFoundException ex)
        {
            throw new DeviceProviderException(
                "libudev.so.1 is not available on this system. " +
                "Install libudev (e.g. 'apt install libudev-dev' on Debian/Ubuntu, " +
                "'dnf install systemd-devel' on Fedora, or 'apk add eudev-dev' on Alpine).",
                ex);
        }

        try
        {
            int deviceCount = 0;
            int skippedCount = 0;

            // Push category filter down to udev subsystem selection
            string[] subsystems = filter.Category.HasValue && filter.Category.Value != DeviceCategory.All
                ? LinuxCategoryMap.GetSubsystems(filter.Category.Value)
                : [];

            _logger.LogInformation("Enumerating devices via libudev (subsystem filter: {SubsystemFilter})",
                subsystems.Length == 0 ? "all" : string.Join(", ", subsystems));

            IntPtr enumerate = UdevInterop.udev_enumerate_new(udev);
            if (enumerate == IntPtr.Zero)
                throw new DeviceProviderException("Failed to create udev enumerate context.");

            try
            {
                // Add subsystem filters
                foreach (var subsystem in subsystems)
                    UdevInterop.EnumerateAddMatchSubsystem(enumerate, subsystem);

                UdevInterop.udev_enumerate_scan_devices(enumerate);

                var entry = UdevInterop.udev_enumerate_get_list_entry(enumerate);
                while (entry != IntPtr.Zero)
                {
                    ct.ThrowIfCancellationRequested();

                    var syspathPtr = UdevInterop.udev_list_entry_get_name(entry);
                    var syspath = UdevInterop.PtrToString(syspathPtr);

                    if (syspath is not null)
                    {
                        var dev = UdevInterop.DeviceNewFromSyspath(udev, syspath);
                        if (dev != IntPtr.Zero)
                        {
                            DeviceInfo? device = null;
                            try
                            {
                                device = ToDeviceInfo(dev, syspath);
                                deviceCount++;
                            }
                            catch (Exception ex)
                            {
                                skippedCount++;
                                _logger.LogWarning(ex,
                                    "Failed to parse device {Syspath}, skipping. Total skipped: {SkippedCount}",
                                    syspath, skippedCount);
                                Debug.WriteLine($"Failed to parse device {syspath}: {ex.Message}");
                            }
                            finally
                            {
                                UdevInterop.udev_device_unref(dev);
                            }

                            if (device is not null)
                                yield return device;
                        }
                    }

                    entry = UdevInterop.udev_list_entry_get_next(entry);
                }
            }
            finally
            {
                UdevInterop.udev_enumerate_unref(enumerate);
            }

            _logger.LogInformation(
                "Device enumeration completed. Found: {DeviceCount}, Skipped: {SkippedCount}",
                deviceCount, skippedCount);
        }
        finally
        {
            UdevInterop.udev_unref(udev);
        }

        // Satisfy the compiler — async iterator requires at least one await.
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>
    /// Builds a <see cref="DeviceInfo"/> from a udev device handle and its syspath.
    /// </summary>
    internal static DeviceInfo? ToDeviceInfo(IntPtr dev, string syspath)
    {
        if (dev == IntPtr.Zero) return null;

        var subsystemPtr = UdevInterop.udev_device_get_subsystem(dev);
        var subsystem = UdevInterop.PtrToString(subsystemPtr);

        // Read udev properties
        var idModel = UdevInterop.GetPropertyValue(dev, "ID_MODEL");
        var idModelFromDb = UdevInterop.GetPropertyValue(dev, "ID_MODEL_FROM_DATABASE");
        var idVendor = UdevInterop.GetPropertyValue(dev, "ID_VENDOR");
        var idVendorFromDb = UdevInterop.GetPropertyValue(dev, "ID_VENDOR_FROM_DATABASE");
        var idVendorId = UdevInterop.GetPropertyValue(dev, "ID_VENDOR_ID");
        var idModelId = UdevInterop.GetPropertyValue(dev, "ID_MODEL_ID");
        var idSerialShort = UdevInterop.GetPropertyValue(dev, "ID_SERIAL_SHORT");
        var idBus = UdevInterop.GetPropertyValue(dev, "ID_BUS");
        var driver = UdevInterop.GetPropertyValue(dev, "DRIVER");

        // Fall back to sysattr for properties not in the udev database
        // The "name" sysattr covers class devices with a card/device name but
        // no USB-style model properties (video4linux cards, input devices);
        // HID_NAME covers hid-subsystem devices the same way.
        var sysName = UdevInterop.GetSysattrValue(dev, "product")
                      ?? idModelFromDb
                      ?? idModel
                      ?? UdevInterop.GetSysattrValue(dev, "name")
                      ?? UdevInterop.GetPropertyValue(dev, "HID_NAME");
        var sysManufacturer = UdevInterop.GetSysattrValue(dev, "manufacturer")
                              ?? idVendorFromDb
                              ?? idVendor;

        // Fallback: try sysattr values for VID/PID
        idVendorId ??= UdevInterop.GetSysattrValue(dev, "idVendor");
        idModelId ??= UdevInterop.GetSysattrValue(dev, "idProduct");

        // hid-subsystem devices carry their identity in the HID_ID uevent
        // property ("bus:vendor:product" in zero-padded hex, e.g.
        // "0003:00000665:00005161") rather than the USB-specific
        // ID_VENDOR_ID/ID_MODEL_ID — and it is the only identity a non-USB
        // HID device (Bluetooth, I2C, uhid) has at all. Windows surfaces
        // VID/PID for every HID device via its instance ID, so parse HID_ID
        // to keep VID/PID-based filters working cross-platform.
        if (idVendorId is null || idModelId is null)
        {
            var hidId = UdevInterop.GetPropertyValue(dev, "HID_ID");
            if (hidId is not null)
            {
                var parts = hidId.Split(':');
                if (parts.Length == 3 && parts[1].Length >= 4 && parts[2].Length >= 4)
                {
                    idVendorId ??= parts[1][^4..];
                    idModelId ??= parts[2][^4..];
                }
            }
        }

        // Fall back to sysattr for serial number
        idSerialShort ??= UdevInterop.GetSysattrValue(dev, "serial");

        // Fall back to sysattr for driver name
        driver ??= UdevInterop.GetSysattrValue(dev, "driver");

        // Parse hardware IDs
        HardwareId? vendorId = null;
        HardwareId? productId = null;
        if (idVendorId is not null && HardwareId.TryParse(idVendorId, out var vid))
            vendorId = vid;
        if (idModelId is not null && HardwareId.TryParse(idModelId, out var pid))
            productId = pid;

        // Resolve category
        var category = LinuxCategoryMap.ResolveCategory(subsystem, dev);

        // Resolve bus type
        var busType = LinuxCategoryMap.InferBusType(idBus, subsystem);

        // Determine connection status.
        // The `authorized` sysattr is USB-specific: "1" = authorized, "0" = not authorized.
        // For non-USB devices, the attribute is absent (null), meaning the device is active.
        // Therefore: authorized != "0" covers all three cases correctly.
        var authorized = UdevInterop.GetSysattrValue(dev, "authorized");
        bool isConnected = authorized != "0"; // "1" → connected, null (absent) → connected, "0" → not connected

        // Network adapter: read MAC address
        PhysicalAddress? macAddress = null;
        if (subsystem == "net")
        {
            var address = UdevInterop.GetSysattrValue(dev, "address");
            if (address is not null)
            {
                try
                {
                    // Linux formats MAC as colon-separated (e.g. "aa:bb:cc:dd:ee:ff")
                    macAddress = PhysicalAddress.Parse(address.Replace(':', '-').ToUpperInvariant());
                }
                catch
                {
                    // Not a valid MAC address — skip silently
                }
            }
        }

        // Driver version — try reading from the kernel module
        Version? driverVersion = null;
        var moduleVersion = UdevInterop.GetSysattrValue(dev, "version");
        if (moduleVersion is not null && Version.TryParse(moduleVersion, out var ver))
            driverVersion = ver;

        // Replace underscores with spaces in model/vendor names (udev convention)
        sysName = sysName?.Replace('_', ' ');
        sysManufacturer = sysManufacturer?.Replace('_', ' ');

        // Detect virtual/software devices — but only when no concrete bus was
        // inferred. A virtual HID device (uhid) still lives on the HID bus,
        // exactly as Windows reports virtual HID devices, and bus-typed
        // consumers (e.g. HidBatteryEnricher's BusType.HID gate) must keep
        // working against it.
        if (busType == BusType.Unknown
            && syspath.Contains("/devices/virtual/", StringComparison.Ordinal))
            busType = BusType.Software;

        var device = new DeviceInfo
        {
            Id = syspath,
            Name = sysName,
            Category = category,
            Manufacturer = sysManufacturer,
            VendorId = vendorId,
            ProductId = productId,
            SerialNumber = idSerialShort,
            IsActive = isConnected,
            Status = isConnected ? DeviceStatus.OK : DeviceStatus.Disabled,
            BusType = busType,
            LocationPath = syspath,
            Driver = driver,
            DriverVersion = driverVersion,
            MacAddress = macAddress,
            Subsystem = subsystem,
        };

        // ADR-0051 §5: run the registered enricher pass so capability tags are
        // present on every Linux device, matching the Windows provider.
        // Centralised in the single builder that every enumerate and monitor
        // path funnels through, so no call site is missed.
        return EnrichmentPipeline.RunRegisteredSync(device, CancellationToken.None, _logger);
    }
}
