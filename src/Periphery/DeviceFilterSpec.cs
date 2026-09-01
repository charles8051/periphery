// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;

namespace Periphery;

/// <summary>
/// The data form of a <see cref="DeviceFilter"/>: one property per criterion,
/// bindable from <c>IConfiguration</c> or JSON, and replayed onto a filter with
/// <see cref="DeviceFilter.Apply"/>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="DeviceFilter"/> is configured through an
/// <c>Action&lt;DeviceFilter&gt;</c> delegate. That is the right runtime shape and
/// the wrong configuration shape — a delegate cannot be deserialised, diffed,
/// logged, or round-tripped — so every consumer that drives device selection
/// from configuration ends up hand-writing a DTO and an if-ladder that replays
/// it. This is that DTO, owned here so it cannot fall behind the filter.
/// </para>
/// <para>
/// <b>Every criterion except <see cref="DeviceFilter.Where"/> has a property
/// here</b>, and a test enforces it. <c>Where</c> takes a delegate and is
/// excluded by construction, not by omission.
/// </para>
/// <para>
/// <b>String comparison is not configurable.</b> Every string criterion uses its
/// method default, <see cref="StringComparison.OrdinalIgnoreCase"/>. Exposing
/// the comparison would put <c>CurrentCulture</c> within reach of a config file
/// and make matching depend on machine locale. Adding it later is additive if a
/// real need appears.
/// </para>
/// <para>
/// <b>There is no <c>ToSpec()</c>, and there will not be.</b> A
/// <see cref="DeviceFilter"/> keeps no record of which method produced which
/// predicate — they all collapse into one list — and a filter carrying a
/// <see cref="DeviceFilter.Where"/> lambda has no data form at all. The
/// conversion is one-way by construction.
/// </para>
/// <para>
/// <b>Binding a JSON array merges by index.</b> That is
/// <c>IConfiguration</c>'s behaviour, not this type's: a base file with
/// <c>"allTags": ["Usb","Hid"]</c> overridden by <c>"allTags": ["Usb"]</c>
/// yields <c>["Usb","Hid"]</c>, because the override replaces index 0 only. Set
/// tag arrays in one layer, or clear them explicitly.
/// </para>
/// <para>
/// <b>A misspelled member is rejected on the JSON path only.</b> This type is
/// declared <see cref="JsonUnmappedMemberHandling.Disallow"/>, so
/// <c>System.Text.Json</c> throws on an unknown or wrongly-cased member rather
/// than binding it to an empty spec that matches every device. That attribute
/// means nothing to <c>IConfiguration</c>, which is case-insensitive and
/// silently ignores keys it does not recognise — so <c>"catgory"</c> binds to a
/// spec with no category and no error.
/// </para>
/// <para>
/// Ask the binder for the same strictness explicitly:
/// <code>
/// config.Get&lt;DeviceFilterSpec&gt;(o =&gt; o.ErrorOnUnknownConfiguration = true);
/// </code>
/// It throws naming every unrecognised key. Prefer it wherever the
/// configuration is operator-written, because the failure it prevents is a
/// filter that silently matches more devices than intended.
/// </para>
/// </remarks>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DeviceFilterSpec
{
    // ── Classification ─────────────────────────────────────────────────

    /// <summary>Replays as <see cref="DeviceFilter.OfCategory"/>.</summary>
    public DeviceCategory? Category { get; init; }

    /// <summary>
    /// Every listed tag must be present. Replays as
    /// <see cref="DeviceFilter.WithAllTags"/>. Null or empty is no criterion.
    /// </summary>
    public string[]? AllTags { get; init; }

    /// <summary>
    /// At least one listed tag must be present. Replays as
    /// <see cref="DeviceFilter.WithAnyTag"/>. Null or empty is no criterion —
    /// note this differs from calling <see cref="DeviceFilter.WithAnyTag"/> with
    /// an empty array directly, which matches nothing.
    /// </summary>
    public string[]? AnyTags { get; init; }

    // ── Text ───────────────────────────────────────────────────────────

    /// <summary>Substring of <see cref="DeviceInfo.Name"/>. Replays as <see cref="DeviceFilter.WithName"/>.</summary>
    public string? DeviceName { get; init; }

    /// <summary>Substring of <see cref="DeviceInfo.Manufacturer"/>. Replays as <see cref="DeviceFilter.ByManufacturer"/>.</summary>
    public string? Manufacturer { get; init; }

    /// <summary>Substring of <see cref="DeviceInfo.Driver"/>. Replays as <see cref="DeviceFilter.WithDriver"/>.</summary>
    public string? Driver { get; init; }

    // ── Identity ───────────────────────────────────────────────────────

    /// <summary>
    /// USB vendor ID, hex. Replays as <see cref="DeviceFilter.WithUsbId(string, string?)"/>.
    /// Unparseable values throw from <see cref="DeviceFilter.Apply"/>.
    /// </summary>
    public string? VendorId { get; init; }

    /// <summary>USB product ID, hex. Requires <see cref="VendorId"/>.</summary>
    public string? ProductId { get; init; }

    /// <summary>Exact <see cref="DeviceInfo.SerialNumber"/>. Replays as <see cref="DeviceFilter.WithSerialNumber"/>.</summary>
    public string? SerialNumber { get; init; }

    /// <summary>Exact <see cref="DeviceInfo.Id"/>. Replays as <see cref="DeviceFilter.WithId"/>.</summary>
    public string? Id { get; init; }

    /// <summary>Prefix of <see cref="DeviceInfo.Id"/>. Replays as <see cref="DeviceFilter.WithIdStartsWith"/>.</summary>
    public string? IdStartsWith { get; init; }

    /// <summary>Exact <see cref="DeviceInfo.ParentId"/>. Replays as <see cref="DeviceFilter.WithParent"/>.</summary>
    public string? ParentId { get; init; }

    /// <summary>
    /// Windows PnP container id. Replays as <see cref="DeviceFilter.WithContainerId"/>.
    /// <para><b>Write it dashed</b> (<c>D</c> format). <c>IConfiguration</c> accepts
    /// braced and undashed forms; <c>System.Text.Json</c> accepts only dashed, so a
    /// braced value binds from config and throws from JSON.</para>
    /// </summary>
    public Guid? ContainerId { get; init; }

    /// <summary>
    /// MAC address, e.g. <c>"00-11-22-33-44-55"</c>. Replays as
    /// <see cref="DeviceFilter.WithMacAddress"/>. Unparseable values throw from
    /// <see cref="DeviceFilter.Apply"/>.
    /// </summary>
    public string? MacAddress { get; init; }

    /// <summary>
    /// Serial port name, e.g. <c>"COM3"</c> or <c>"/dev/ttyUSB0"</c>. Replays as
    /// <see cref="DeviceFilter.WithPortName(SerialPortName)"/>. Unparseable
    /// values throw from <see cref="DeviceFilter.Apply"/>.
    /// </summary>
    public string? PortName { get; init; }

    // ── Enumerated state ───────────────────────────────────────────────

    /// <summary>Replays as <see cref="DeviceFilter.WithBusType"/>.</summary>
    public BusType? BusType { get; init; }

    /// <summary>Replays as <see cref="DeviceFilter.WithStatus"/>.</summary>
    public DeviceStatus? Status { get; init; }

    /// <summary>Replays as <see cref="DeviceFilter.WithDriveType"/>.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<DriveType>))]
    public DriveType? DriveType { get; init; }

    /// <summary>Replays as <see cref="DeviceFilter.WithUsbSpeed"/>.</summary>
    public UsbSpeed? UsbSpeed { get; init; }

    /// <summary>Replays as <see cref="DeviceFilter.WithBatteryStatus"/>.</summary>
    public BatteryStatus? BatteryStatus { get; init; }

    /// <summary>
    /// <c>true</c> keeps only active devices, <c>false</c> only inactive.
    /// Replays as <see cref="DeviceFilter.Active"/>. Null is no criterion —
    /// the tri-state is why this is not a plain <c>bool</c>, since
    /// <see cref="DeviceFilter.Active"/> itself defaults to <c>true</c>.
    /// </summary>
    public bool? Active { get; init; }

    /// <summary>
    /// Replays as <see cref="DeviceFilter.PhysicalOnly"/> or
    /// <see cref="DeviceFilter.VirtualOnly"/>.
    /// </summary>
    public DevicePhysicality? Physicality { get; init; }

    // ── Display ────────────────────────────────────────────────────────

    /// <summary>
    /// Minimum display width in pixels. Must be set together with
    /// <see cref="MinHeight"/>; one without the other throws from
    /// <see cref="DeviceFilter.Apply"/>.
    /// </summary>
    public int? MinWidth { get; init; }

    /// <summary>Minimum display height in pixels. See <see cref="MinWidth"/>.</summary>
    public int? MinHeight { get; init; }

    // ── Derived ────────────────────────────────────────────────────────

    /// <summary>
    /// True when at least one criterion is set. Null and empty tag arrays do not
    /// count, matching what <see cref="DeviceFilter.Apply"/> replays.
    /// </summary>
    /// <remarks>
    /// Answerable without building a filter, so a consumer can reject an empty
    /// bound configuration against its own configuration key rather than against
    /// a parameter name the operator never wrote.
    /// </remarks>
    [JsonIgnore]
    public bool HasAnyCriteria =>
        Category.HasValue
        || AllTags is { Length: > 0 }
        || AnyTags is { Length: > 0 }
        || DeviceName is not null
        || Manufacturer is not null
        || Driver is not null
        || VendorId is not null
        || ProductId is not null
        || SerialNumber is not null
        || Id is not null
        || IdStartsWith is not null
        || ParentId is not null
        || ContainerId.HasValue
        || MacAddress is not null
        || PortName is not null
        || BusType.HasValue
        || Status.HasValue
        || DriveType.HasValue
        || UsbSpeed.HasValue
        || BatteryStatus.HasValue
        || Active.HasValue
        || Physicality.HasValue
        || MinWidth.HasValue
        || MinHeight.HasValue;

    // ── Equality ───────────────────────────────────────────────────────
    //
    // Hand-written because the compiler compares string[] by REFERENCE, so two
    // specs bound from the same JSON would be unequal. ADR-0047 records the same
    // surprise on DeviceInfo.Tags; that one was mitigated by routing comparison
    // through a diff helper, which a config DTO has no equivalent of — "did the
    // bound configuration change?" is asked with ==. Tag order is meaningless
    // for both AND and OR, so the comparison is set-based and Ordinal, matching
    // DeviceTags.Carries.

    /// <inheritdoc/>
    public bool Equals(DeviceFilterSpec? other) =>
        other is not null
        && Category == other.Category
        && TagsEqual(AllTags, other.AllTags)
        && TagsEqual(AnyTags, other.AnyTags)
        && DeviceName == other.DeviceName
        && Manufacturer == other.Manufacturer
        && Driver == other.Driver
        && VendorId == other.VendorId
        && ProductId == other.ProductId
        && SerialNumber == other.SerialNumber
        && Id == other.Id
        && IdStartsWith == other.IdStartsWith
        && ParentId == other.ParentId
        && ContainerId == other.ContainerId
        && MacAddress == other.MacAddress
        && PortName == other.PortName
        && BusType == other.BusType
        && Status == other.Status
        && DriveType == other.DriveType
        && UsbSpeed == other.UsbSpeed
        && BatteryStatus == other.BatteryStatus
        && Active == other.Active
        && Physicality == other.Physicality
        && MinWidth == other.MinWidth
        && MinHeight == other.MinHeight;

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Category);
        AddTags(ref hash, AllTags);
        AddTags(ref hash, AnyTags);
        hash.Add(DeviceName);
        hash.Add(Manufacturer);
        hash.Add(Driver);
        hash.Add(VendorId);
        hash.Add(ProductId);
        hash.Add(SerialNumber);
        hash.Add(Id);
        hash.Add(IdStartsWith);
        hash.Add(ParentId);
        hash.Add(ContainerId);
        hash.Add(MacAddress);
        hash.Add(PortName);
        hash.Add(BusType);
        hash.Add(Status);
        hash.Add(DriveType);
        hash.Add(UsbSpeed);
        hash.Add(BatteryStatus);
        hash.Add(Active);
        hash.Add(Physicality);
        hash.Add(MinWidth);
        hash.Add(MinHeight);
        return hash.ToHashCode();
    }

    /// <summary>
    /// Null and empty are the same absent criterion; order does not matter, and
    /// neither do duplicates — <c>["Printer"]</c> and
    /// <c>["Printer","Printer"]</c> replay identically, so they compare equal.
    /// </summary>
    private static bool TagsEqual(string[]? a, string[]? b)
    {
        var left = (a is { Length: > 0 } ? a : []).ToHashSet(StringComparer.Ordinal);
        var right = (b is { Length: > 0 } ? b : []).ToHashSet(StringComparer.Ordinal);
        return left.SetEquals(right);
    }

    private static void AddTags(ref HashCode hash, string[]? tags)
    {
        if (tags is not { Length: > 0 })
        {
            hash.Add(0);
            return;
        }
        // Order- and duplicate-independent, to agree with TagsEqual.
        var distinct = tags.ToHashSet(StringComparer.Ordinal);
        var acc = 0;
        foreach (var tag in distinct)
            acc ^= StringComparer.Ordinal.GetHashCode(tag);
        hash.Add(distinct.Count);
        hash.Add(acc);
    }

    // ── Description ────────────────────────────────────────────────────

    /// <summary>
    /// The criteria this spec sets, for diagnostics and operator UI.
    /// </summary>
    /// <remarks>
    /// <b>Not a stable format.</b> <see cref="DeviceProfile.FromSpec"/> puts this
    /// in its exception message; do not parse it. It echoes identifying values
    /// (serial numbers, MAC addresses, container ids) verbatim, so treat it the
    /// way you would treat the configuration file it came from.
    /// </remarks>
    public override string ToString()
    {
        var parts = new List<string>();
        void Add(string label, object? value)
        {
            if (value is not null)
                parts.Add($"{label}={value}");
        }

        Add(nameof(Category), Category);
        if (AllTags is { Length: > 0 })
            parts.Add($"{nameof(AllTags)}=[{string.Join(",", AllTags)}]");
        if (AnyTags is { Length: > 0 })
            parts.Add($"{nameof(AnyTags)}=[{string.Join(",", AnyTags)}]");
        Add(nameof(DeviceName), DeviceName);
        Add(nameof(Manufacturer), Manufacturer);
        Add(nameof(Driver), Driver);
        Add(nameof(VendorId), VendorId);
        Add(nameof(ProductId), ProductId);
        Add(nameof(SerialNumber), SerialNumber);
        Add(nameof(Id), Id);
        Add(nameof(IdStartsWith), IdStartsWith);
        Add(nameof(ParentId), ParentId);
        Add(nameof(ContainerId), ContainerId);
        Add(nameof(MacAddress), MacAddress);
        Add(nameof(PortName), PortName);
        Add(nameof(BusType), BusType);
        Add(nameof(Status), Status);
        Add(nameof(DriveType), DriveType);
        Add(nameof(UsbSpeed), UsbSpeed);
        Add(nameof(BatteryStatus), BatteryStatus);
        Add(nameof(Active), Active);
        Add(nameof(Physicality), Physicality);
        Add(nameof(MinWidth), MinWidth);
        Add(nameof(MinHeight), MinHeight);

        return parts.Count == 0 ? "(no criteria)" : string.Join(", ", parts);
    }
}
