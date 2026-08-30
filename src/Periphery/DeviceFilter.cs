// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Net.NetworkInformation;

namespace Periphery;

/// <summary>
/// Composable set of device predicates shared by <see cref="DeviceQuery"/>,
/// <see cref="DeviceWatcher"/>, and tracked device handles. All filters
/// are evaluated in-memory via <see cref="Matches"/>. Some structured
/// properties (category, name, manufacturer, USB IDs) are also exposed
/// as typed fields so that platform providers <em>may</em> use them as
/// hints to narrow OS-level queries — but correctness never depends on
/// provider cooperation.
/// </summary>
/// <remarks>
/// <para>This type appears in delegate signatures
/// (<c>Action&lt;DeviceFilter&gt;</c>) exposed by <see cref="DeviceTracker"/>,
/// <see cref="DeviceProfile"/>, and
/// <see cref="DeviceWatcher.AddTracker(Action{DeviceFilter}, string?)"/>, where it
/// is configured through the delegate. It is also a <b>first-class standalone
/// predicate value</b>: construct one with <c>new DeviceFilter()</c>, chain the
/// fluent criteria, and ask <see cref="Matches"/> directly. That is how
/// <c>Periphery.Bootloader.IBootloaderEntry.ExpectedBootloader</c> describes "the
/// device a rebooted board re-enumerates as" (ADR-0063) — a filter that both seeds a
/// <see cref="DeviceWatcher"/> and acts as the safety gate via <see cref="Matches"/>.</para>
/// </remarks>
public sealed class DeviceFilter
{
    private readonly List<Func<DeviceInfo, bool>> _predicates = [];
    private HashSet<string>? _relevantTags;

    /// <summary>Creates an empty filter. Chain the fluent <c>With*</c> / <c>Of*</c>
    /// criteria, then evaluate with <see cref="Matches"/> or hand it to a query/watcher.</summary>
    public DeviceFilter() { }

    // ── Structured properties ──────────────────────────────────────────
    // These typed fields mirror a subset of DeviceInfo. Platform providers
    // may inspect them to narrow OS queries, but every filter is always
    // re-evaluated in-memory by Matches() regardless.

    /// <summary>Target device category, or <c>null</c> for all categories.</summary>
    internal DeviceCategory? Category { get; private set; }

    /// <summary>Name substring to match against <see cref="DeviceInfo.Name"/>.</summary>
    internal string? NameContains { get; private set; }

    /// <summary>String comparison mode for the <see cref="NameContains"/> filter.</summary>
    internal StringComparison NameComparison { get; private set; } = StringComparison.OrdinalIgnoreCase;

    /// <summary>Manufacturer substring to match against <see cref="DeviceInfo.Manufacturer"/>.</summary>
    internal string? ManufacturerContains { get; private set; }

    /// <summary>String comparison mode for the <see cref="ManufacturerContains"/> filter.</summary>
    internal StringComparison ManufacturerComparison { get; private set; } = StringComparison.OrdinalIgnoreCase;

    /// <summary>USB Vendor ID to match.</summary>
    internal HardwareId? VendorId { get; private set; }

    /// <summary>USB Product ID to match (if set, <see cref="VendorId"/> must also be set).</summary>
    internal HardwareId? ProductId { get; private set; }

    /// <summary>Whether lambda predicates have been added.</summary>
    internal bool HasLambdaPredicates => _predicates.Count > 0;

    /// <summary>Whether at least one filter criterion has been configured.</summary>
    internal bool HasAnyCriteria =>
        Category.HasValue ||
        NameContains is not null ||
        ManufacturerContains is not null ||
        VendorId.HasValue ||
        ProductId.HasValue ||
        _predicates.Count > 0;

    /// <summary>
    /// Whether the filter can match <see cref="DeviceCategory.Monitor"/> devices.
    /// True when no category is set (all categories pass) or the category is Monitor.
    /// </summary>
    internal bool NeedsMonitorEnrichment =>
        !Category.HasValue ||
        Category.Value == DeviceCategory.All ||
        Category.Value == DeviceCategory.Monitor;

    /// <summary>
    /// Whether the filter can match <see cref="DeviceCategory.Battery"/> devices.
    /// True when no category is set (all categories pass) or the category is Battery.
    /// </summary>
    internal bool NeedsBatteryEnrichment =>
        !Category.HasValue ||
        Category.Value == DeviceCategory.All ||
        Category.Value == DeviceCategory.Battery;

    /// <summary>
    /// The capability tags this filter references via <see cref="WithTag"/>,
    /// <see cref="WithAllTags"/>, or <see cref="WithAnyTag"/>; empty when the
    /// filter has no tag predicate. A provider may union the
    /// <see cref="DeviceEnrichers.ScopeForTags(IReadOnlySet{string})"/> of
    /// these tags into an otherwise category-less OS query so a bare tag
    /// filter scans only the relevant subsystems instead of every device
    /// (ADR-0051 §5). Like every structured property here it is a hint only —
    /// <see cref="Matches"/> remains the source of truth.
    /// </summary>
    internal IReadOnlySet<string> RelevantTags =>
        _relevantTags is { Count: > 0 } ? _relevantTags : s_noTags;

    private static readonly IReadOnlySet<string> s_noTags = ImmutableHashSet<string>.Empty;

    // ── Fluent filters ─────────────────────────────────────────────────

    /// <summary>Filter to a specific device category.</summary>
    public DeviceFilter OfCategory(DeviceCategory category)
    {
        Category = category;
        return this;
    }

    /// <summary>Keep only devices matching <paramref name="predicate"/>.</summary>
    public DeviceFilter Where(Func<DeviceInfo, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        _predicates.Add(predicate);
        return this;
    }

    /// <summary>Keep only devices whose <see cref="DeviceInfo.Name"/> contains <paramref name="text"/>.</summary>
    public DeviceFilter WithName(string text, StringComparison comparison = StringComparison.OrdinalIgnoreCase)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        NameContains = text;
        NameComparison = comparison;
        return this;
    }

    /// <summary>Keep only devices matching a USB VID/PID pair.</summary>
    public DeviceFilter WithUsbId(HardwareId vendorId, HardwareId? productId = null)
    {
        VendorId = vendorId;
        ProductId = productId;
        return this;
    }

    /// <summary>Keep only devices matching a USB VID/PID pair (parsed from strings).</summary>
    public DeviceFilter WithUsbId(string vendorId, string? productId = null)
    {
        if (!HardwareId.TryParse(vendorId, out var vid))
            return Where(_ => false);

        HardwareId? pid = null;
        if (productId is not null)
        {
            if (!HardwareId.TryParse(productId, out var parsedPid))
                return Where(_ => false);
            pid = parsedPid;
        }

        return WithUsbId(vid, pid);
    }

    /// <summary>Keep only devices from <paramref name="manufacturer"/>.</summary>
    public DeviceFilter ByManufacturer(string manufacturer, StringComparison comparison = StringComparison.OrdinalIgnoreCase)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manufacturer);
        ManufacturerContains = manufacturer;
        ManufacturerComparison = comparison;
        return this;
    }

    /// <summary>Keep only physically active devices. Pass <c>false</c> to include only inactive devices.</summary>
    public DeviceFilter Active(bool active = true)
        => Where(d => d.IsActive == active);

    // ── Convenience filters ────────────────────────────────────────────
    // Typed wrappers over Where() for common DeviceInfo properties.
    // These improve discoverability without needing first-class
    // structured properties — they compose as lambda predicates.

    /// <summary>Keep only the device with the specified platform-native identifier.
    /// Matched <b>case-insensitively</b> — device instance IDs are case-insensitive
    /// by contract (see <see cref="DeviceId"/>).</summary>
    public DeviceFilter WithId(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return Where(d => d.Id == id);
    }

    /// <summary>Keep only devices with the specified serial number (exact match).</summary>
    public DeviceFilter WithSerialNumber(string serialNumber)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serialNumber);
        return Where(d => string.Equals(d.SerialNumber, serialNumber, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Keep only devices whose <see cref="DeviceInfo.Id"/> starts with
    /// <paramref name="prefix"/>. Useful for matching by hardware model
    /// rather than instance — for example, <c>"DISPLAY\\MS_0003\\"</c>
    /// matches every Microsoft-EDID monitor of model <c>MS_0003</c>
    /// regardless of which per-machine instance hash Windows assigned.
    /// </summary>
    public DeviceFilter WithIdStartsWith(string prefix, StringComparison comparison = StringComparison.OrdinalIgnoreCase)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        return Where(d => d.Id.Value.StartsWith(prefix, comparison));
    }

    /// <summary>
    /// Keep only devices whose <see cref="DeviceInfo.ContainerId"/> matches
    /// <paramref name="containerId"/>. The container id is a Windows PnP
    /// concept that groups every interface belonging to one physical device
    /// and is stable across reboots and port reseats. On platforms where
    /// the provider does not populate <see cref="DeviceInfo.ContainerId"/>
    /// (Linux, macOS) this filter will never match.
    /// </summary>
    public DeviceFilter WithContainerId(Guid containerId)
        => Where(d => d.ContainerId == containerId);

    /// <summary>Keep only devices on the specified bus type.</summary>
    public DeviceFilter WithBusType(BusType busType)
        => Where(d => d.BusType == busType);

    /// <summary>Keep only devices with the specified status.</summary>
    public DeviceFilter WithStatus(DeviceStatus status)
        => Where(d => d.Status == status);

    /// <summary>
    /// Keep only storage devices of the specified drive type.
    /// <para><b>Relevant categories:</b> <see cref="DeviceCategory.Storage"/>.</para>
    /// </summary>
    public DeviceFilter WithDriveType(DriveType driveType)
        => Where(d => d.DriveType == driveType);

    /// <summary>
    /// Keep only devices with the specified MAC address.
    /// <para><b>Relevant categories:</b> <see cref="DeviceCategory.Network"/>,
    /// <see cref="DeviceCategory.Bluetooth"/>.</para>
    /// </summary>
    public DeviceFilter WithMacAddress(PhysicalAddress macAddress)
    {
        ArgumentNullException.ThrowIfNull(macAddress);
        return Where(d => d.MacAddress is not null && d.MacAddress.Equals(macAddress));
    }

    /// <summary>
    /// Keep only devices whose active driver or service name contains
    /// <paramref name="text"/>.
    /// </summary>
    public DeviceFilter WithDriver(string text, StringComparison comparison = StringComparison.OrdinalIgnoreCase)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        return Where(d => d.Driver?.Contains(text, comparison) == true);
    }

    /// <summary>
    /// Keep only displays whose native resolution is at least
    /// <paramref name="minWidth"/> × <paramref name="minHeight"/> pixels.
    /// <para><b>Relevant categories:</b> <see cref="DeviceCategory.Monitor"/>,
    /// <see cref="DeviceCategory.Display"/>.</para>
    /// </summary>
    public DeviceFilter WithMinResolution(int minWidth, int minHeight)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minHeight);
        return Where(d => d.DisplayResolution is { } res
            && res.Width >= minWidth && res.Height >= minHeight);
    }

    /// <summary>
    /// Keep only devices with the specified negotiated USB speed.
    /// <para><b>Relevant categories:</b> <see cref="DeviceCategory.Usb"/>.</para>
    /// </summary>
    public DeviceFilter WithUsbSpeed(UsbSpeed speed)
        => Where(d => d.UsbSpeed == speed);

    /// <summary>
    /// Keep only devices whose parent in the device tree matches
    /// <paramref name="parentId"/>.
    /// </summary>
    public DeviceFilter WithParent(string parentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentId);
        return Where(d => d.ParentId == parentId);
    }

    /// <summary>
    /// Keep only devices mapped to the specified OS serial port name.
    /// <para><b>Relevant categories:</b> <see cref="DeviceCategory.Ports"/>.</para>
    /// </summary>
    public DeviceFilter WithPortName(string portName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(portName);
        return Where(d => d.PortName is { } pn
            && string.Equals(pn.Value, portName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Keep only devices mapped to the specified OS serial port name.
    /// <para><b>Relevant categories:</b> <see cref="DeviceCategory.Ports"/>.</para>
    /// </summary>
    public DeviceFilter WithPortName(SerialPortName portName)
        => Where(d => d.PortName == portName);

    /// <summary>
    /// Keep only battery devices with the specified power state.
    /// <para><b>Relevant categories:</b> <see cref="DeviceCategory.Battery"/>.</para>
    /// </summary>
    public DeviceFilter WithBatteryStatus(BatteryStatus status)
        => Where(d => d.BatteryStatus == status);

    // ── Capability tag filters (ADR-0047) ──────────────────────────────
    //
    // The tag predicates also consult `device.Category` by name as a
    // fallback: a tag query like `WithTag("Hid")` matches a device whose
    // Tags set explicitly contains "Hid" OR whose Category is
    // DeviceCategory.Hid. This keeps the query surface uniform
    // (`WithTag(...)` is the one idiom for capability questions) without
    // requiring enrichers to redundantly emit a tag for the Category their
    // device is already classified under — see ADR-0047 §4.

    /// <summary>
    /// Keep only devices that carry <paramref name="tag"/> as either an
    /// explicit <see cref="DeviceInfo.Tags"/> entry or as their
    /// <see cref="DeviceInfo.Category"/> (matched by enum-member name).
    /// </summary>
    /// <remarks>
    /// Tags are populated during enrichment and represent cross-cutting
    /// capabilities (see <see cref="DeviceTags"/> for well-known values,
    /// ADR-0047 for rationale). The Category fallback means consumers can
    /// uniformly use <c>WithTag</c> for any capability question, including
    /// ones whose answer comes from OS classification alone. Matching rule
    /// is shared with <see cref="DeviceTags.Carries"/> for use on already-
    /// enumerated device lists.
    /// </remarks>
    public DeviceFilter WithTag(string tag)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        TrackTag(tag);
        return Where(d => DeviceTags.Carries(d, tag));
    }

    /// <summary>
    /// Keep only devices that carry every tag in <paramref name="tags"/>
    /// (logical AND). Each tag is matched via <see cref="WithTag"/>'s
    /// Tags-or-Category rule. An empty argument list matches every device.
    /// </summary>
    public DeviceFilter WithAllTags(params string[] tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        if (tags.Length == 0)
            return this;
        for (int i = 0; i < tags.Length; i++)
            TrackTag(tags[i]);
        return Where(d =>
        {
            for (int i = 0; i < tags.Length; i++)
                if (!DeviceTags.Carries(d, tags[i])) return false;
            return true;
        });
    }

    /// <summary>
    /// Keep only devices that carry at least one tag in <paramref name="tags"/>
    /// (logical OR). Each tag is matched via <see cref="WithTag"/>'s
    /// Tags-or-Category rule. An empty argument list matches no devices.
    /// </summary>
    public DeviceFilter WithAnyTag(params string[] tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        if (tags.Length == 0)
            return Where(_ => false);
        for (int i = 0; i < tags.Length; i++)
            TrackTag(tags[i]);
        return Where(d =>
        {
            for (int i = 0; i < tags.Length; i++)
                if (DeviceTags.Carries(d, tags[i])) return true;
            return false;
        });
    }

    /// <summary>
    /// Captures a referenced capability tag for <see cref="RelevantTags"/>.
    /// Null/blank tags are ignored here — the matching predicate still throws
    /// on them at evaluation time via <see cref="DeviceTags.Carries"/> — which
    /// keeps the relevant-tags hint clean without changing match semantics.
    /// </summary>
    private void TrackTag(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return;
        (_relevantTags ??= new HashSet<string>(StringComparer.Ordinal)).Add(tag);
    }

    /// <summary>
    /// Keep only physical devices, excluding software/virtual devices.
    /// Filters out devices with <see cref="BusType.Software"/>.
    /// </summary>
    /// <remarks>
    /// Virtual devices include virtual network adapters, software audio endpoints,
    /// print queues, and other software-enumerated devices. They typically appear
    /// under <c>SWD\</c> (Software Device) on Windows, <c>/sys/devices/virtual</c>
    /// on Linux, or non-hardware IOService nodes on macOS.
    /// </remarks>
    public DeviceFilter PhysicalOnly()
        => Where(d => d.BusType != BusType.Software);

    /// <summary>
    /// Keep only virtual/software devices, excluding physical hardware.
    /// Matches devices with <see cref="BusType.Software"/>.
    /// </summary>
    /// <remarks>
    /// Virtual devices include virtual network adapters (VPN, Hyper-V, loopback),
    /// software audio endpoints, print queues, and other software-enumerated devices.
    /// </remarks>
    public DeviceFilter VirtualOnly()
        => Where(d => d.BusType == BusType.Software);

    // ── Internal — cloning ──────────────────────────────────────────────

    /// <summary>
    /// Copies all structured properties and lambda predicates from this
    /// filter onto <paramref name="target"/>. Used by
    /// <see cref="MultiDeviceTracker"/> to create child tracker filters
    /// that combine the group's criteria with a per-device identity filter.
    /// </summary>
    internal void CopyTo(DeviceFilter target)
    {
        if (Category.HasValue) target.Category = Category;
        if (NameContains is not null)
        {
            target.NameContains = NameContains;
            target.NameComparison = NameComparison;
        }
        if (ManufacturerContains is not null)
        {
            target.ManufacturerContains = ManufacturerContains;
            target.ManufacturerComparison = ManufacturerComparison;
        }
        if (VendorId.HasValue) target.VendorId = VendorId;
        if (ProductId.HasValue) target.ProductId = ProductId;
        foreach (var predicate in _predicates)
            target._predicates.Add(predicate);
        if (_relevantTags is { Count: > 0 })
        {
            target._relevantTags ??= new HashSet<string>(StringComparer.Ordinal);
            target._relevantTags.UnionWith(_relevantTags);
        }
    }

    // ── Evaluation ─────────────────────────────────────────────────────

    /// <summary>
    /// Returns <c>true</c> if <paramref name="device"/> passes every filter
    /// configured on this instance — both structured properties and lambda
    /// predicates. This is the single source of truth for filter evaluation;
    /// all matching decisions flow through here.
    /// </summary>
    public bool Matches(DeviceInfo device)
    {
        if (Category.HasValue && Category.Value != DeviceCategory.All && device.Category != Category.Value)
            return false;

        if (NameContains is not null && device.Name?.Contains(NameContains, NameComparison) != true)
            return false;

        if (ManufacturerContains is not null && device.Manufacturer?.Contains(ManufacturerContains, ManufacturerComparison) != true)
            return false;

        if (VendorId.HasValue && device.VendorId != VendorId)
            return false;

        if (ProductId.HasValue && device.ProductId != ProductId)
            return false;

        for (int i = 0; i < _predicates.Count; i++)
        {
            if (!_predicates[i](device))
                return false;
        }

        return true;
    }
}
