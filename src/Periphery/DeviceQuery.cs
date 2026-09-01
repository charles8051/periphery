// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Periphery;

/// <summary>
/// A composable, lazy device query. Chain filters with fluent methods, then
/// materialise results with <c>await foreach</c>, <see cref="ToListAsync"/>,
/// or <see cref="FirstOrDefaultAsync"/>.
/// Implements <see cref="IAsyncEnumerable{T}"/> so standard LINQ works too.
///
/// <example>
/// <code>
/// var mice = await Devices.Enumerate()
///     .OfCategory(DeviceCategory.Hid)
///     .WithName("Mouse")
///     .OrderBy(d => d.Name)
///     .Take(5)
///     .ToListAsync();
/// </code>
/// </example>
/// </summary>
public sealed class DeviceQuery : IAsyncEnumerable<DeviceInfo>
{
    private static readonly ILogger<DeviceQuery> _logger = 
        PeripheryLoggerFactory.CreateLogger<DeviceQuery>();

    private readonly DeviceFilter _filter = new();
    private Func<DeviceInfo, object?>? _orderBy;
    private bool _descending;
    private int? _limit;
    private readonly IDeviceProvider? _providerOverride;

    internal DeviceQuery() { }

    /// <summary>
    /// Creates a query backed by a custom provider.
    /// Use this constructor in tests to inject a <see cref="IDeviceProvider"/>
    /// implementation that returns a predefined device list without touching OS APIs.
    /// </summary>
    /// <param name="provider">
    /// The provider used to enumerate devices. Must not be <see langword="null"/>.
    /// </param>
    public DeviceQuery(IDeviceProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _providerOverride = provider;
    }

    // ── Fluent filters ─────────────────────────────────────────────────

    /// <summary>
    /// Applies every criterion set on <paramref name="spec"/>. See
    /// <see cref="DeviceFilter.Apply"/> for the replay semantics and the
    /// parse-failure contract.
    /// </summary>
    public DeviceQuery Apply(DeviceFilterSpec spec)
    {
        _filter.Apply(spec);
        return this;
    }

    /// <summary>Filter to a specific device category.</summary>
    public DeviceQuery OfCategory(DeviceCategory category)
    {
        _filter.OfCategory(category);
        return this;
    }

    /// <summary>Keep only devices matching <paramref name="predicate"/>.</summary>
    public DeviceQuery Where(Func<DeviceInfo, bool> predicate)
    {
        _filter.Where(predicate);
        return this;
    }

    /// <summary>Keep only devices whose <see cref="DeviceInfo.Name"/> contains <paramref name="text"/>.</summary>
    public DeviceQuery WithName(string text, StringComparison comparison = StringComparison.OrdinalIgnoreCase)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        _filter.WithName(text, comparison);
        return this;
    }

    /// <summary>Keep only devices matching a USB VID/PID pair.</summary>
    public DeviceQuery WithUsbId(HardwareId vendorId, HardwareId? productId = null)
    {
        _filter.WithUsbId(vendorId, productId);
        return this;
    }

    /// <summary>Keep only devices matching a USB VID/PID pair (parsed from strings).</summary>
    public DeviceQuery WithUsbId(string vendorId, string? productId = null)
    {
        _filter.WithUsbId(vendorId, productId);
        return this;
    }

    /// <summary>Keep only devices from <paramref name="manufacturer"/>.</summary>
    public DeviceQuery ByManufacturer(string manufacturer, StringComparison comparison = StringComparison.OrdinalIgnoreCase)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manufacturer);
        _filter.ByManufacturer(manufacturer, comparison);
        return this;
    }

    /// <summary>Keep only physically active devices. Pass <c>false</c> to include only inactive devices.</summary>
    public DeviceQuery Active(bool active = true)
    {
        _filter.Active(active);
        return this;
    }

    /// <summary>Keep only the device with the specified platform-native identifier (exact match).</summary>
    public DeviceQuery WithId(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        _filter.WithId(id);
        return this;
    }

    /// <summary>Keep only devices with the specified serial number (exact match).</summary>
    public DeviceQuery WithSerialNumber(string serialNumber)
    {
        _filter.WithSerialNumber(serialNumber);
        return this;
    }

    /// <summary>
    /// Keep only devices whose <see cref="DeviceInfo.Id"/> starts with
    /// <paramref name="prefix"/>. Useful for matching by hardware model rather
    /// than instance — for example, <c>"DISPLAY\\MS_0003\\"</c> matches every
    /// Microsoft-EDID monitor of model <c>MS_0003</c> regardless of which
    /// per-machine instance hash Windows assigned.
    /// </summary>
    public DeviceQuery WithIdStartsWith(string prefix, StringComparison comparison = StringComparison.OrdinalIgnoreCase)
    {
        _filter.WithIdStartsWith(prefix, comparison);
        return this;
    }

    /// <summary>
    /// Keep only devices whose <see cref="DeviceInfo.ContainerId"/> matches
    /// <paramref name="containerId"/> — the Windows PnP grouping of every
    /// interface belonging to one physical device. On platforms that do not
    /// populate <see cref="DeviceInfo.ContainerId"/> (Linux, macOS) this filter
    /// never matches. Not durable across Bluetooth re-pairing (ADR-0083).
    /// </summary>
    public DeviceQuery WithContainerId(Guid containerId)
    {
        _filter.WithContainerId(containerId);
        return this;
    }

    /// <summary>Keep only devices on the specified bus type.</summary>
    public DeviceQuery WithBusType(BusType busType)
    {
        _filter.WithBusType(busType);
        return this;
    }

    /// <summary>Keep only devices with the specified status.</summary>
    public DeviceQuery WithStatus(DeviceStatus status)
    {
        _filter.WithStatus(status);
        return this;
    }

    /// <summary>
    /// Keep only storage devices of the specified drive type.
    /// <para><b>Relevant categories:</b> <see cref="DeviceCategory.Storage"/>.</para>
    /// </summary>
    public DeviceQuery WithDriveType(DriveType driveType)
    {
        _filter.WithDriveType(driveType);
        return this;
    }

    /// <summary>
    /// Keep only devices with the specified MAC address.
    /// <para><b>Relevant categories:</b> <see cref="DeviceCategory.Network"/>,
    /// <see cref="DeviceCategory.Bluetooth"/>.</para>
    /// </summary>
    public DeviceQuery WithMacAddress(PhysicalAddress macAddress)
    {
        _filter.WithMacAddress(macAddress);
        return this;
    }

    /// <summary>
    /// Keep only devices whose active driver or service name contains
    /// <paramref name="text"/>.
    /// </summary>
    public DeviceQuery WithDriver(string text, StringComparison comparison = StringComparison.OrdinalIgnoreCase)
    {
        _filter.WithDriver(text, comparison);
        return this;
    }

    /// <summary>
    /// Keep only displays whose native resolution is at least
    /// <paramref name="minWidth"/> × <paramref name="minHeight"/> pixels.
    /// <para><b>Relevant categories:</b> <see cref="DeviceCategory.Monitor"/>,
    /// <see cref="DeviceCategory.Display"/>.</para>
    /// </summary>
    public DeviceQuery WithMinResolution(int minWidth, int minHeight)
    {
        _filter.WithMinResolution(minWidth, minHeight);
        return this;
    }

    /// <summary>
    /// Keep only devices with the specified negotiated USB speed.
    /// <para><b>Relevant categories:</b> <see cref="DeviceCategory.Usb"/>.</para>
    /// </summary>
    public DeviceQuery WithUsbSpeed(UsbSpeed speed)
    {
        _filter.WithUsbSpeed(speed);
        return this;
    }

    /// <summary>
    /// Keep only devices whose parent in the device tree matches
    /// <paramref name="parentId"/>.
    /// </summary>
    public DeviceQuery WithParent(string parentId)
    {
        _filter.WithParent(parentId);
        return this;
    }

    /// <summary>
    /// Keep only devices mapped to the specified OS serial port name.
    /// <para><b>Relevant categories:</b> <see cref="DeviceCategory.Ports"/>.</para>
    /// </summary>
    public DeviceQuery WithPortName(string portName)
    {
        _filter.WithPortName(portName);
        return this;
    }

    /// <summary>
    /// Keep only devices mapped to the specified OS serial port name.
    /// <para><b>Relevant categories:</b> <see cref="DeviceCategory.Ports"/>.</para>
    /// </summary>
    public DeviceQuery WithPortName(SerialPortName portName)
    {
        _filter.WithPortName(portName);
        return this;
    }

    /// <summary>
    /// Keep only battery devices with the specified power state.
    /// <para><b>Relevant categories:</b> <see cref="DeviceCategory.Battery"/>.</para>
    /// </summary>
    public DeviceQuery WithBatteryStatus(BatteryStatus status)
    {
        _filter.WithBatteryStatus(status);
        return this;
    }

    /// <summary>
    /// Keep only physical devices, excluding software/virtual devices.
    /// Filters out devices with <see cref="BusType.Software"/>.
    /// </summary>
    /// <remarks>
    /// Virtual devices include virtual network adapters, software audio endpoints,
    /// print queues, and other software-enumerated devices.
    /// </remarks>
    public DeviceQuery PhysicalOnly()
    {
        _filter.PhysicalOnly();
        return this;
    }

    /// <summary>
    /// Keep only virtual/software devices, excluding physical hardware.
    /// Matches devices with <see cref="BusType.Software"/>.
    /// </summary>
    /// <remarks>
    /// Virtual devices include virtual network adapters (VPN, Hyper-V, loopback),
    /// software audio endpoints, print queues, and other software-enumerated devices.
    /// </remarks>
    public DeviceQuery VirtualOnly()
    {
        _filter.VirtualOnly();
        return this;
    }

    // ── Capability tag filters (ADR-0047) ──────────────────────────────

    /// <summary>
    /// Keep only devices that carry <paramref name="tag"/> as either an
    /// explicit <see cref="DeviceInfo.Tags"/> entry or via their
    /// <see cref="DeviceInfo.Category"/> (matched by enum-member name).
    /// See <see cref="DeviceTags"/> for well-known values.
    /// </summary>
    public DeviceQuery WithTag(string tag)
    {
        _filter.WithTag(tag);
        return this;
    }

    /// <summary>
    /// Keep only devices that carry every tag in <paramref name="tags"/>
    /// (logical AND). See <see cref="WithTag"/> for matching semantics.
    /// </summary>
    public DeviceQuery WithAllTags(params string[] tags)
    {
        _filter.WithAllTags(tags);
        return this;
    }

    /// <summary>
    /// Keep only devices that carry at least one tag in <paramref name="tags"/>
    /// (logical OR). See <see cref="WithTag"/> for matching semantics.
    /// </summary>
    public DeviceQuery WithAnyTag(params string[] tags)
    {
        _filter.WithAnyTag(tags);
        return this;
    }

    // ── Ordering / limiting ────────────────────────────────────────

    /// <summary>Order results by a key.</summary>
    public DeviceQuery OrderBy<TKey>(Func<DeviceInfo, TKey> keySelector, bool descending = false)
    {
        ArgumentNullException.ThrowIfNull(keySelector);
        _orderBy = d => keySelector(d);
        _descending = descending;
        return this;
    }

    /// <summary>Take at most <paramref name="count"/> results.</summary>
    public DeviceQuery Take(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        _limit = count;
        return this;
    }

    // ── Materialisation ────────────────────────────────────────────────

    /// <summary>Materialise all matching devices into a list.</summary>
    public async Task<IReadOnlyList<DeviceInfo>> ToListAsync(CancellationToken ct = default)
    {
        var results = new List<DeviceInfo>();
        await foreach (var device in this.WithCancellation(ct).ConfigureAwait(false))
            results.Add(device);
        return results;
    }

    /// <summary>Return the first matching device, or <c>null</c>.</summary>
    public async Task<DeviceInfo?> FirstOrDefaultAsync(CancellationToken ct = default)
    {
        await foreach (var device in this.WithCancellation(ct).ConfigureAwait(false))
            return device;
        return null;
    }

    /// <summary>Count matching devices.</summary>
    public async Task<int> CountAsync(CancellationToken ct = default)
    {
        int count = 0;
        await foreach (var _ in this.WithCancellation(ct).ConfigureAwait(false))
            count++;
        return count;
    }

    /// <summary>Return <c>true</c> if at least one device matches.</summary>
    public async Task<bool> AnyAsync(CancellationToken ct = default)
        => await FirstOrDefaultAsync(ct).ConfigureAwait(false) is not null;

    // ── IAsyncEnumerable ───────────────────────────────────────────────

    public async IAsyncEnumerator<DeviceInfo> GetAsyncEnumerator(
        CancellationToken ct = default)
    {
        _logger.LogDebug("Starting device query enumeration");

        var provider = _providerOverride ?? DeviceProviderFactory.GetProvider();
        var stream = provider.EnumerateAsync(_filter, ct);

        var buffer = new List<DeviceInfo>();
        int totalEnumerated = 0;

        await foreach (var device in stream.WithCancellation(ct).ConfigureAwait(false))
        {
            totalEnumerated++;

            // All filtering happens in-memory via Matches()
            if (_filter.Matches(device))
            {
                buffer.Add(device);
                _logger.LogTrace("Device matched filter: {DeviceId} ({DeviceName})", 
                    device.Id, device.Name ?? "(unnamed)");
            }
        }

        _logger.LogDebug(
            "Query enumeration completed. Total: {TotalEnumerated}, Matched: {MatchedCount}", 
            totalEnumerated, buffer.Count);

        // Use ordinal string comparison so that sort order is deterministic and
        // culture-independent (matches StringComparison.Ordinal semantics).
        var keyComparer = Comparer<object?>.Create(static (x, y) =>
            x is string sx && y is string sy
                ? string.Compare(sx, sy, StringComparison.Ordinal)
                : Comparer<object?>.Default.Compare(x, y));

        IEnumerable<DeviceInfo> results = _orderBy switch
        {
            not null when _descending => buffer.OrderByDescending(d => _orderBy(d), keyComparer),
            not null => buffer.OrderBy(d => _orderBy(d), keyComparer),
            _ => buffer
        };

        if (_limit.HasValue)
        {
            results = results.Take(_limit.Value);
            _logger.LogDebug("Applying limit: {Limit}", _limit.Value);
        }

        int yieldedCount = 0;
        foreach (var device in results)
        {
            yieldedCount++;
            yield return device;
        }

        _logger.LogInformation(
            "Device query completed. Enumerated: {TotalEnumerated}, Matched: {MatchedCount}, Yielded: {YieldedCount}", 
            totalEnumerated, buffer.Count, yieldedCount);
    }
}
