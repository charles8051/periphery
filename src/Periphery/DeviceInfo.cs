// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json.Serialization;

namespace Periphery;

/// <summary>
/// Immutable snapshot of a discovered device. All properties are populated
/// on a best-effort basis — unavailable values are <c>null</c>.
/// </summary>
public sealed record DeviceInfo
{
    // ── Identity ───────────────────────────────────────────────────────

    /// <summary>
    /// Platform-native unique identifier. Strongly typed as <see cref="DeviceId"/>
    /// so the case-insensitive instance-id identity invariant travels with the
    /// value (see <see cref="DeviceId"/>). Serializes as a bare string.
    /// </summary>
    public required DeviceId Id { get; init; }

    /// <summary>Human-readable device name.</summary>
    public string? Name { get; init; }

    /// <summary>Resolved device category.</summary>
    public DeviceCategory Category { get; init; }

    /// <summary>Device manufacturer / vendor name.</summary>
    public string? Manufacturer { get; init; }

    /// <summary>
    /// Device class GUID (e.g. Windows SetupAPI class, Bluetooth service UUID).
    /// </summary>
    public Guid? ClassGuid { get; init; }

    /// <summary>
    /// Human-readable Windows device class name (e.g. <c>"Keyboard"</c>, <c>"USB Controller"</c>).
    /// Resolved from <see cref="ClassGuid"/> via the Windows SetupAPI class name table.
    /// <c>null</c> on non-Windows platforms and when the class GUID is unrecognized.
    /// </summary>
    public string? ClassName { get; init; }

    /// <summary>
    /// Container ID that groups multiple device interfaces into a single
    /// physical device (e.g. a USB hub that exposes HID + audio).
    /// </summary>
    public Guid? ContainerId { get; init; }

    // ── Hardware IDs ───────────────────────────────────────────────────

    /// <summary>USB Vendor ID or equivalent.</summary>
    public HardwareId? VendorId { get; init; }

    /// <summary>USB Product ID or equivalent.</summary>
    public HardwareId? ProductId { get; init; }

    /// <summary>Serial number reported by the device, if available.</summary>
    public string? SerialNumber { get; init; }

    // ── Status ─────────────────────────────────────────────────────────

    /// <summary>
    /// <c>true</c> when the hardware is physically active — driver started
    /// and not flagged as disconnected. <c>false</c> for devices that are
    /// known to the OS but not currently active (e.g. a Bluetooth device
    /// that is paired but out of range, or a network adapter that is disabled).
    /// </summary>
    /// <remarks>
    /// <para>Reliability varies by platform and device category — see
    /// <c>docs/ARCHITECTURE.md</c> §10.6.2 for per-category details.</para>
    /// <para>On <see cref="DeviceTracker"/>, use <see cref="DeviceTracker.IsActive"/>
    /// (derived from <see cref="DeviceWatcher.Activated"/> events) for the
    /// aggregate "is any matching device active?" question, and
    /// <see cref="DeviceTracker.IsPresent"/> (derived from
    /// <see cref="DeviceWatcher.Appeared"/> events) for "is any matching
    /// device known to the OS?".</para>
    /// </remarks>
    public bool IsActive { get; init; }

    /// <summary>OS-reported device status.</summary>
    public DeviceStatus Status { get; init; }

    // ── Bus / Location ─────────────────────────────────────────────────

    /// <summary>Bus type the device sits on.</summary>
    public BusType BusType { get; init; }

    /// <summary>Bus address or port location path.</summary>
    public string? LocationPath { get; init; }

    // ── Driver ─────────────────────────────────────────────────────────

    /// <summary>Active driver or service name.</summary>
    public string? Driver { get; init; }

    /// <summary>Driver or firmware version, if available.</summary>
    [JsonConverter(typeof(VersionJsonConverter))]
    public Version? DriverVersion { get; init; }

    // ── Network ────────────────────────────────────────────────────────

    /// <summary>
    /// MAC address for network adapters and Bluetooth devices.
    /// Uses <see cref="System.Net.NetworkInformation.PhysicalAddress"/>
    /// from the BCL — supports parsing, formatting, and value equality.
    /// </summary>
    [JsonConverter(typeof(PhysicalAddressJsonConverter))]
    public PhysicalAddress? MacAddress { get; init; }

    /// <summary>IP addresses assigned to a network adapter.</summary>
    [JsonConverter(typeof(IPAddressArrayJsonConverter))]
    public ImmutableArray<IPAddress>? IPAddresses { get; init; }

    /// <summary>Subnet information for a network adapter.</summary>
    [JsonConverter(typeof(IPNetworkJsonConverter))]
    public IPNetwork? Network { get; init; }

    // ── Display / Monitor ──────────────────────────────────────────────

    /// <summary>
    /// Native display resolution in raw pixels (e.g. 1920×1080).
    /// Uses <see cref="System.Drawing.Size"/> from
    /// <c>System.Drawing.Primitives</c> (cross-platform).
    /// Populated on Windows via Win32 DisplayConfig APIs; <c>null</c> on Linux and macOS.
    /// </summary>
    [JsonConverter(typeof(SizeJsonConverter))]
    public Size? DisplayResolution { get; init; }

    /// <summary>
    /// Position and size on the virtual desktop in pixels — the panel's real
    /// on-desktop footprint, so origin and size share one frame of reference and
    /// the rectangle matches what the OS window system reports.
    /// Uses <see cref="System.Drawing.Rectangle"/> from
    /// <c>System.Drawing.Primitives</c> (cross-platform).
    /// <para>
    /// Reflects <see cref="DisplayOrientation"/>: a portrait-rotated 1920×1080
    /// panel reports a 1080×1920 rectangle. To recover the unrotated source
    /// surface, transpose the size back when the orientation is portrait-class.
    /// </para>
    /// </summary>
    [JsonConverter(typeof(RectangleJsonConverter))]
    public Rectangle? DisplayBounds { get; init; }

    /// <summary>
    /// Current rotation relative to the panel's native orientation
    /// (0° / 90° / 180° / 270°).
    /// <para>
    /// Populated on Windows via Win32 DisplayConfig APIs. <c>null</c> means
    /// unmeasured — never "unrotated" — on Windows when no DisplayConfig path
    /// resolves to the device, and on Linux/macOS pending their backends
    /// (see <see cref="Periphery.DisplayOrientation"/>).
    /// A rotation always moves this property, so a pure rotation raises
    /// <c>DevicePropertyChanged</c> even when the panel's origin and footprint
    /// on the virtual desktop are unchanged (issue #163).
    /// </para>
    /// </summary>
    public DisplayOrientation? DisplayOrientation { get; init; }

    /// <summary>
    /// EDID-derived friendly name of the monitor. Populated only for
    /// <see cref="DeviceCategory.Monitor"/> devices; <c>null</c> on every
    /// other category.
    /// <para>
    /// Distinct from <see cref="Name"/>, which is the OS device-tree label
    /// and is populated for every device. Sites that want a generic
    /// per-device friendly string should bind to <see cref="Name"/>; sites
    /// that specifically want the monitor's marketing name (e.g. a display
    /// picker) read <see cref="MonitorName"/>.
    /// </para>
    /// <para>
    /// On Windows, populated via Win32 DisplayConfig APIs with a
    /// registry-EDID fallback (see ADR-0044). <c>null</c> on Linux and macOS.
    /// </para>
    /// </summary>
    public string? MonitorName { get; init; }

    /// <summary>
    /// Physical diagonal size in inches, from EDID.
    /// Not currently populated on any platform; reserved for a future EDID implementation.
    /// </summary>
    public float? DisplayPhysicalSizeInInches { get; init; }

    /// <summary>
    /// Physical DPI as a (horizontal × vertical) pair, derived from native
    /// resolution and physical size. Uses <see cref="System.Drawing.SizeF"/>.
    /// Not currently populated; reserved for a future Win32 <c>GetDpiForMonitor</c> implementation.
    /// </summary>
    [JsonConverter(typeof(SizeFJsonConverter))]
    public SizeF? DisplayDpi { get; init; }

    /// <summary>
    /// Physical connector standard used to connect the monitor
    /// (HDMI, DisplayPort, internal panel, etc.).
    /// Populated on Windows via Win32 DisplayConfig APIs; <c>null</c> on other platforms.
    /// </summary>
    public DisplayConnector? DisplayPhysicalConnector { get; init; }

    /// <summary>
    /// Abstract connection method (wired, wireless, or virtual).
    /// Populated on Windows via Win32 DisplayConfig APIs; <c>null</c> on other platforms.
    /// </summary>
    public DisplayConnectionKind? DisplayConnectionKind { get; init; }

    /// <summary>
    /// Intended usage category (standard monitor, head-mounted display, special-purpose).
    /// Not currently populated; HMD classification has no Win32 equivalent.
    /// </summary>
    public DisplayUsageKind? DisplayUsageKind { get; init; }

    /// <summary>
    /// Maximum peak luminance in nits, from EDID HDR static metadata.
    /// Non-null only for HDR-capable monitors.
    /// Not currently populated; reserved for a future EDID implementation.
    /// </summary>
    public float? DisplayMaxLuminanceInNits { get; init; }

    /// <summary>
    /// Maximum average full-frame luminance in nits, from EDID HDR static metadata.
    /// Non-null only for HDR-capable monitors.
    /// Not currently populated; reserved for a future EDID implementation.
    /// </summary>
    public float? DisplayMaxAvgLuminanceInNits { get; init; }

    /// <summary>
    /// Minimum luminance in nits, from EDID HDR static metadata.
    /// Non-null only for HDR-capable monitors.
    /// Not currently populated; reserved for a future EDID implementation.
    /// </summary>
    public float? DisplayMinLuminanceInNits { get; init; }

    // ── Storage ────────────────────────────────────────────────────────

    /// <summary>
    /// Storage drive classification (Fixed, Removable, Network, etc.).
    /// Uses <see cref="System.IO.DriveType"/> from the BCL.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter<DriveType>))]
    public DriveType? DriveType { get; init; }

    // ── Topology ───────────────────────────────────────────────────────

    /// <summary>
    /// Platform-native ID of this device's parent in the device tree.
    /// <c>null</c> for root devices (e.g. PCI host controllers).
    /// </summary>
    public DeviceId? ParentId { get; init; }

    /// <summary>
    /// Hub or bus port number this device is attached to (1-based).
    /// <c>null</c> if not a bus-attached device or not available.
    /// </summary>
    public int? PortNumber { get; init; }

    // ── USB-specific ───────────────────────────────────────────────────

    /// <summary>
    /// Negotiated USB speed, if the device is on a USB bus.
    /// <c>null</c> for non-USB devices.
    /// </summary>
    public UsbSpeed? UsbSpeed { get; init; }

    /// <summary>
    /// Maximum power the device is configured to draw, in milliamps.
    /// <c>null</c> if not available or not a USB device.
    /// </summary>
    public int? MaxPowerMilliamps { get; init; }

    /// <summary>
    /// USB class/subclass/protocol triple identifying the device function.
    /// <c>null</c> for non-USB devices.
    /// </summary>
    public UsbClassCode? UsbClassCode { get; init; }

    // ── HID-specific ───────────────────────────────────────────────────

    /// <summary>
    /// HID usage page (e.g. <c>0x0001</c> = Generic Desktop).
    /// Populated by <c>HidDeviceEnricher</c> in <c>Periphery.Hid</c> without
    /// opening a handle. <c>null</c> for non-HID devices or when the enricher
    /// has not been registered.
    /// </summary>
    public ushort? HidUsagePage { get; init; }

    /// <summary>
    /// HID usage within the usage page (e.g. <c>0x0005</c> = Gamepad).
    /// Populated alongside <see cref="HidUsagePage"/>. <c>null</c> when not available.
    /// </summary>
    public ushort? HidUsage { get; init; }

    /// <summary>
    /// Maximum input report length in bytes, excluding the report ID byte.
    /// Populated by <c>HidDeviceEnricher</c>. <c>null</c> when not available.
    /// </summary>
    public int? HidMaxInputReportLength { get; init; }

    /// <summary>
    /// Maximum output report length in bytes, excluding the report ID byte.
    /// Populated by <c>HidDeviceEnricher</c>. <c>null</c> when not available.
    /// </summary>
    public int? HidMaxOutputReportLength { get; init; }

    /// <summary>
    /// Maximum feature report length in bytes, excluding the report ID byte.
    /// Populated by <c>HidDeviceEnricher</c>. <c>null</c> when not available.
    /// </summary>
    public int? HidMaxFeatureReportLength { get; init; }

    // ── Serial / COM port ──────────────────────────────────────────────

    /// <summary>
    /// OS serial port name for COM/serial devices.
    /// Use <c>PortName.Value</c> to get the string for
    /// <c>new SerialPort(portName.Value)</c>.
    /// <c>null</c> for non-serial devices.
    /// </summary>
    public SerialPortName? PortName { get; init; }

    // ── Battery / Power ────────────────────────────────────────────────

    /// <summary>
    /// Battery charge level (0–100), if available.
    /// <c>null</c> for non-battery devices.
    /// </summary>
    public int? BatteryChargePercent { get; init; }

    /// <summary>
    /// Battery power state.
    /// <c>null</c> for non-battery devices.
    /// </summary>
    public BatteryStatus? BatteryStatus { get; init; }

    /// <summary>
    /// Whether external power (AC adapter, USB-PD) is connected.
    /// <c>null</c> if not available or not a battery device.
    /// </summary>
    public bool? IsExternalPowerConnected { get; init; }

    /// <summary>
    /// <c>true</c> when the battery is below its critical threshold
    /// (imminent shutdown if not externally powered); <c>false</c> when
    /// the battery is above the threshold; <c>null</c> when low-state is
    /// not known or the device has no battery.
    /// </summary>
    /// <remarks>
    /// <para>Orthogonal to <see cref="BatteryStatus"/>:
    /// <see cref="BatteryStatus"/> describes flow direction (Charging,
    /// Discharging, Full, NotCharging — mutually exclusive);
    /// <see cref="IsBatteryLow"/> describes a charge-level threshold,
    /// and may be true simultaneously with any flow direction. A
    /// discharging battery may be low or not; a charging battery
    /// recovering from depletion may also still be below the low
    /// threshold.</para>
    /// <para>Threshold semantics are platform / device defined — on
    /// Windows, derived from the <c>BATTERY_FLAG_CRITICAL</c> bit of
    /// <c>GetSystemPowerStatus</c>'s <c>BatteryFlag</c>. HID-class UPSs
    /// report low-state on a per-codec basis; see
    /// <c>HidBatterySnapshot.IsBatteryLow</c> for the codec-side reading
    /// (handle-gated, returned via <c>HidBattery.ReadSnapshotAsync</c>
    /// rather than populated here).</para>
    /// </remarks>
    public bool? IsBatteryLow { get; init; }

    // ── Platform Identifiers ────────────────────────────────────────────

    /// <summary>
    /// Linux udev subsystem (e.g. <c>"usb"</c>, <c>"net"</c>, <c>"input"</c>).
    /// <c>null</c> on non-Linux platforms.
    /// </summary>
    public string? Subsystem { get; init; }

    /// <summary>
    /// IOKit service class name on macOS (e.g. <c>"IOUSBDevice"</c>, <c>"IOBluetoothDevice"</c>).
    /// <c>null</c> on non-macOS platforms.
    /// </summary>
    public string? IOServiceClass { get; init; }

    // ── Classification ─────────────────────────────────────────────────

    /// <summary>
    /// Cross-cutting capability tags applied during enrichment. Distinct
    /// from <see cref="Category"/>, which identifies the OS subsystem
    /// that surfaced the device. A single device may carry several tags
    /// (e.g. a HID-class UPS is tagged both <c>"Hid"</c> and <c>"Battery"</c>;
    /// a smart monitor may be tagged both <c>"Monitor"</c> and <c>"Audio"</c>).
    /// </summary>
    /// <remarks>
    /// <para>Tag values are open strings, compared ordinally. Well-known
    /// constants live on <see cref="DeviceTags"/>; consumers should
    /// reference those rather than spelling literals at call sites.</para>
    /// <para>The set is always non-null and defaults to empty. Filter via
    /// <see cref="DeviceFilter.WithTag(string)"/>,
    /// <see cref="DeviceFilter.WithAllTags(string[])"/>, or
    /// <see cref="DeviceFilter.WithAnyTag(string[])"/>.</para>
    /// <para>See ADR-0047 for the design rationale (single-valued
    /// <see cref="Category"/> = OS subsystem identity; multi-valued
    /// <see cref="Tags"/> = capability annotations).</para>
    /// </remarks>
    public ImmutableHashSet<string> Tags { get; init; }
        = ImmutableHashSet<string>.Empty;

    // ── Extensibility ──────────────────────────────────────────────────

    /// <summary>
    /// Raw platform-specific data that has no first-class typed field.
    /// Keys are provider-defined; see <see cref="WellKnownProperties"/> for common keys.
    /// </summary>
    /// <remarks>
    /// This bag is intentionally narrow. Most platform-specific concepts that
    /// are scalar and universally meaningful on their platform are promoted to
    /// typed properties on <see cref="DeviceInfo"/> directly (e.g.
    /// <see cref="ClassName"/>, <see cref="Subsystem"/>, <see cref="IOServiceClass"/>).
    /// Only data that is inherently array-typed or purely diagnostic belongs here.
    /// <para><b>Windows:</b></para>
    /// <list type="bullet">
    /// <item><c>"HardwareID"</c> (string[]) — Raw PnP hardware ID strings; feeds <see cref="UsbClassCode"/></item>
    /// <item><c>"CompatibleID"</c> (string[]) — Raw PnP compatible ID strings; feeds <see cref="UsbClassCode"/></item>
    /// <item><c>"RawStatus"</c> (int) — cfgmgr32 <c>CM_PROB_*</c> problem code (more granular than <see cref="Status"/>)</item>
    /// </list>
    /// </remarks>
    public IReadOnlyDictionary<string, object?> Properties { get; init; }
        = ImmutableDictionary<string, object?>.Empty;

    /// <inheritdoc/>
    public override string ToString()
        => Name is not null ? $"{Name} ({Id})" : Id;
}

/// <summary>
/// Well-known keys for the <see cref="DeviceInfo.Properties"/> bag.
/// Only data that is inherently array-typed or purely diagnostic belongs here;
/// scalar platform-universal strings are promoted to typed properties on
/// <see cref="DeviceInfo"/> instead (e.g. <see cref="DeviceInfo.ClassName"/>,
/// <see cref="DeviceInfo.Subsystem"/>, <see cref="DeviceInfo.IOServiceClass"/>).
/// </summary>
public static class WellKnownProperties
{
    // ── Windows ────────────────────────────────────────────────────────

    /// <summary>
    /// Hardware ID array from Windows PnP.
    /// Raw strings used internally to derive <see cref="DeviceInfo.UsbClassCode"/>.
    /// <para>Example entry: <c>"USB\VID_046D&amp;PID_C52B&amp;REV_2400"</c></para>
    /// </summary>
    public const string HardwareIds = "HardwareID";

    /// <summary>
    /// Compatible ID array from Windows PnP.
    /// Raw strings used internally to derive <see cref="DeviceInfo.UsbClassCode"/>.
    /// <para>Example entry: <c>"USB\Class_03&amp;SubClass_01&amp;Prot_02"</c></para>
    /// </summary>
    public const string CompatibleIds = "CompatibleID";

    /// <summary>
    /// Device problem code from Windows cfgmgr32 (<c>CM_PROB_*</c>).
    /// Provides more detail than the cross-platform <see cref="DeviceInfo.Status"/> enum.
    /// <para>Value is an <c>int</c>: <c>0</c> means no problem,
    /// <c>22</c> (<c>CM_PROB_DISABLED</c>) means disabled by user/policy.</para>
    /// </summary>
    public const string RawStatus = "RawStatus";
}
