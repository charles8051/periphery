// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;

namespace Periphery;

/// <summary>
/// Well-known capability tag values used by core enrichers when
/// populating <see cref="DeviceInfo.Tags"/>. Extension packages may
/// add their own tag values directly — the tag set is open string-
/// keyed — but should document them alongside any enricher that
/// emits them.
/// </summary>
/// <remarks>
/// <para>Tag comparison is ordinal and case-sensitive (matching
/// <see cref="DeviceInfo.Tags"/>'s <see cref="System.Collections.Immutable.ImmutableHashSet{T}"/>
/// default). Reference the constants here rather than spelling
/// literals at call sites so a typo can't silently produce a
/// never-matching filter.</para>
/// <para>The constants list grows incrementally as enrichers gain
/// new classification rules. New tags don't require coordination
/// across providers — they only need to be documented next to the
/// enricher that emits them.</para>
/// </remarks>
public static class DeviceTags
{
    /// <summary>
    /// HID-protocol device. Emitted by HID enricher(s) for any
    /// device whose USB interface advertises a HID descriptor,
    /// regardless of its <see cref="DeviceInfo.Category"/>.
    /// </summary>
    public const string Hid = "Hid";

    /// <summary>
    /// Device exposes a battery / power-supply surface — laptop
    /// battery, ACPI battery, HID-class UPS, etc. Emitted by
    /// battery enricher(s) when charge level or AC line status
    /// can be read for the device.
    /// </summary>
    public const string Battery = "Battery";

    /// <summary>
    /// Device exposes an audio playback or capture endpoint.
    /// Emitted by audio enricher(s) when populated.
    /// </summary>
    public const string Audio = "Audio";

    /// <summary>
    /// Sensor device — accelerometer, gyroscope, ambient-light, etc.
    /// Emitted by the core <see cref="SensorEnricher"/> for a device on the
    /// Windows <c>Sensor</c> setup class, the Linux <c>iio</c> subsystem, or the
    /// macOS HID sensor usage page (<c>0x20</c>). Replaces the former
    /// <c>DeviceCategory.Sensor</c> (ADR-0051).
    /// </summary>
    /// <remarks>
    /// <para>A GNSS/GPS receiver reached as a <em>serial port</em> does not carry
    /// this tag (ADR-0051 OQ-004). It is a virtual COM port identified by VID/PID,
    /// not by any of the three sensor signals above; it enumerates as
    /// <see cref="DeviceCategory.Ports"/> and carries a <c>Gps</c> tag instead
    /// (ADR-0050).</para>
    /// <para>That is a statement about the port node, not about the hardware. On
    /// Windows, a vendor driver package can enumerate the same physical dongle a
    /// second time as a GNSS location sensor; that node is a distinct
    /// <see cref="DeviceInfo"/> and <em>does</em> legitimately carry
    /// <c>Sensor</c> when it matches the Windows <c>Sensor</c> setup class. Two
    /// device nodes, one dongle — do not suppress the sensor tag on the second
    /// because of the first.</para>
    /// </remarks>
    public const string Sensor = "Sensor";

    /// <summary>
    /// Smart-card reader (CCID). Emitted by the core <see cref="SmartCardEnricher"/>
    /// for the Windows <c>SmartCardReader</c> setup class, the macOS
    /// <c>IOUSBSmartCardController</c> class, or USB device class <c>0x0B</c>.
    /// Replaces the former <c>DeviceCategory.SmartCard</c> (ADR-0051).
    /// </summary>
    public const string SmartCard = "SmartCard";

    /// <summary>
    /// Imaging device — scanner or still-image / PTP camera. Emitted by the core
    /// <see cref="ImagingEnricher"/> for the Windows <c>Image</c> setup class or
    /// USB device class <c>0x06</c>. Replaces the former
    /// <c>DeviceCategory.Imaging</c> (ADR-0051).
    /// </summary>
    public const string Imaging = "Imaging";

    /// <summary>
    /// Biometric reader — fingerprint, facial-recognition, etc. Emitted by the
    /// core <see cref="BiometricEnricher"/> for the Windows <c>Biometric</c> setup
    /// class. No standard cross-platform USB signal exists (readers are
    /// vendor-specific), so detection is Windows-only today. Replaces the former
    /// <c>DeviceCategory.Biometric</c> (ADR-0051).
    /// </summary>
    public const string Biometric = "Biometric";

    /// <summary>
    /// Printer or print queue. Emitted by the core <see cref="PrinterEnricher"/>
    /// for the Windows <c>Printer</c>, <c>PnpPrinters</c>, or <c>PrintQueue</c>
    /// setup class, or USB device class <c>0x07</c>. Replaces the former
    /// <c>DeviceCategory.Printer</c> (ADR-0051).
    /// </summary>
    public const string Printer = "Printer";

    /// <summary>
    /// Returns <c>true</c> when <paramref name="device"/> carries
    /// <paramref name="tag"/> — explicitly via <see cref="DeviceInfo.Tags"/>
    /// or implicitly via its <see cref="DeviceInfo.Category"/>'s
    /// enum-member name (the ADR-0047 Option B fallback).
    /// <see cref="DeviceCategory.All"/> never matches any specific tag
    /// (the catch-all category isn't a capability claim).
    /// </summary>
    /// <remarks>
    /// Single source of truth for the Tags-or-Category rule. Used by
    /// <see cref="DeviceFilter.WithTag(string)"/> (and friends) for query-
    /// time filtering, and exposed here so callers working on already-
    /// enumerated <see cref="DeviceInfo"/> lists can apply the same rule
    /// without re-deriving it. Comparison is ordinal, case-sensitive.
    /// </remarks>
    public static bool Carries(DeviceInfo device, string tag)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);

        if (device.Tags.Contains(tag))
            return true;
        if (device.Category == DeviceCategory.All)
            return false;
        var categoryName = Enum.GetName(device.Category);
        return categoryName is not null
            && string.Equals(categoryName, tag, StringComparison.Ordinal);
    }
}
