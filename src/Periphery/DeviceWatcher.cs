// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Periphery;

/// <summary>
/// Watches for real-time device connections and disconnections.
/// Configure filters before calling <see cref="StartAsync"/>, then
/// dispose when done. Implements <see cref="IAsyncDisposable"/> for
/// <c>await using</c> cleanup.
///
/// <example>
/// <code>
/// await using var watcher = Devices.Watch()
///     .OfCategory(DeviceCategory.Usb)
///     .WithName("Mouse")
///     .ByManufacturer("Logitech");
///
/// watcher.Activated += (_, e) => Console.WriteLine($"+ {e.Device.Name}");
/// watcher.Deactivated += (_, e) => Console.WriteLine($"- {e.Device.Name}");
///
/// await watcher.StartAsync();
/// </code>
/// </example>
/// </summary>
/// <remarks>
/// <para><b>Lifecycle:</b> Configure → Start → Dispose. To change filters,
/// dispose this watcher and create a new one.</para>
/// <para><b>Tracking:</b> Use <see cref="AddTracker(Action{DeviceFilter}, string?)"/> to create
/// per-device trackers, or <see cref="AddTrackers(DeviceTracker[])"/> to re-attach
/// existing trackers from a previous watcher. Trackers survive disposal and
/// can be re-used across watcher lifetimes.</para>
/// <para><b>Thread Safety:</b></para>
/// <list type="bullet">
/// <item><description>
/// Fluent filter methods and <see cref="AddTracker(Action{DeviceFilter}, string?)"/> are NOT
/// thread-safe. Configure all filters and trackers before calling
/// <see cref="StartAsync"/>.
/// </description></item>
/// <item><description>
/// <see cref="StartAsync"/> is thread-safe; only the first call initializes
/// the watcher, subsequent calls throw <see cref="InvalidOperationException"/>.
/// </description></item>
/// <item><description>
/// Event handlers are invoked on thread-pool threads. Handlers requiring
/// UI-thread dispatch must marshal themselves.
/// </description></item>
/// <item><description>
/// <see cref="DisposeAsync"/> is idempotent and thread-safe.
/// </description></item>
/// </list>
/// <para><b>No tag filters, deliberately.</b> <see cref="DeviceFilter.WithTag(string)"/>,
/// <see cref="DeviceFilter.WithAllTags(string[])"/> and
/// <see cref="DeviceFilter.WithAnyTag(string[])"/> exist on
/// <see cref="DeviceFilter"/> and <see cref="DeviceQuery"/> but have no
/// watcher-level counterpart, because a watcher would match them
/// asymmetrically.</para>
/// <para>Tags are produced by the enrichment pipeline. The Windows monitor
/// provider seeds its last-known-device cache with the plain unenriched build
/// and skips enrichment to keep startup cheap, and that cached record is what a
/// removal event carries. A watcher filtered on a tag would therefore see
/// <see cref="Appeared"/> — the startup snapshot runs the query provider, which
/// does enrich — and never see <see cref="Disappeared"/>, leaking the device as
/// permanently present. Linux and macOS enrich inside their single device build
/// and do not have the asymmetry, so the feature would also be
/// platform-divergent. Filter a watcher on
/// <see cref="OfCategory(DeviceCategory)"/> or another unenriched field, and
/// apply tag predicates to the devices it reports.</para>
/// </remarks>
public sealed class DeviceWatcher : IAsyncDisposable
{
    private static readonly ILogger<DeviceWatcher> _logger =
        PeripheryLoggerFactory.CreateLogger<DeviceWatcher>();

    private readonly DeviceFilter _filter = new();
    private readonly List<DeviceTracker> _trackers = [];
    private readonly List<MultiDeviceTracker> _multiTrackers = [];
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);

    private IDeviceMonitorProvider? _provider;
    private bool _started;
    private bool _disposed;
    private int _appearedEventCount;
    private int _activatedEventCount;
    private int _deactivatedEventCount;
    private int _disappearedEventCount;

    // Provider overrides — set only via the injecting constructors.
    private readonly IDeviceProvider? _providerOverride;
    private readonly IDeviceMonitorProvider? _monitorOverride;

    // Mints a monitor provider per start attempt, as production does via
    // DeviceProviderFactory. Distinct from _monitorOverride: an override is one
    // caller-owned instance the watcher must not dispose, whereas a factory
    // hands the attempt an instance it owns and disposes on rollback. Internal
    // so a test can exercise the owned-provider path, which is the production
    // one and is otherwise unreachable without real OS providers.
    private readonly Func<IDeviceMonitorProvider>? _monitorFactory;

    // Tracks device IDs for which we've fired Activated, so we can
    // cascade a Deactivated event when a device disappears.
    // DeviceId carries case-insensitive equality, so the set is keyed
    // case-insensitively without an explicit comparer.
    private readonly HashSet<DeviceId> _knownConnectedIds = new();

    // Caches the most recent DeviceInfo snapshot per device ID, seeded during
    // StartAsync and updated on each PropertyChanged event from the provider.
    // Used as the "previous" snapshot for diff computation.
    private readonly Dictionary<DeviceId, DeviceInfo> _deviceCache = new();

    internal DeviceWatcher() { }

    /// <summary>
    /// Creates a watcher backed by custom providers.
    /// Use this constructor in tests to inject <see cref="IDeviceProvider"/> and
    /// <see cref="IDeviceMonitorProvider"/> implementations that return a predefined
    /// device set and fire simulated events without touching OS APIs.
    /// </summary>
    /// <param name="provider">
    /// Provider used to enumerate the initial device snapshot. Must not be <see langword="null"/>.
    /// </param>
    /// <param name="monitor">
    /// Provider used to receive real-time device events. Must not be <see langword="null"/>.
    /// </param>
    public DeviceWatcher(IDeviceProvider provider, IDeviceMonitorProvider monitor)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(monitor);
        _providerOverride = provider;
        _monitorOverride = monitor;
    }

    /// <summary>
    /// Creates a watcher that mints a monitor provider per start attempt, the
    /// way production does. Unlike the instance-injecting constructor, the
    /// watcher <b>owns</b> what the factory returns and disposes it when an
    /// attempt is rolled back.
    /// </summary>
    internal DeviceWatcher(IDeviceProvider provider, Func<IDeviceMonitorProvider> monitorFactory)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(monitorFactory);
        _providerOverride = provider;
        _monitorFactory = monitorFactory;
    }

    // ── Fluent filters ─────────────────────────────────────────────────

    /// <summary>Filter to a specific device category.</summary>
    public DeviceWatcher OfCategory(DeviceCategory category)
    {
        ThrowIfDisposed();
        ThrowIfStarted();
        _filter.OfCategory(category);
        return this;
    }

    /// <summary>Keep only devices matching <paramref name="predicate"/>.</summary>
    public DeviceWatcher Where(Func<DeviceInfo, bool> predicate)
    {
        ThrowIfDisposed();
        ThrowIfStarted();
        ArgumentNullException.ThrowIfNull(predicate);
        _filter.Where(predicate);
        return this;
    }

    /// <summary>Keep only devices whose <see cref="DeviceInfo.Name"/> contains <paramref name="text"/>.</summary>
    public DeviceWatcher WithName(string text, StringComparison comparison = StringComparison.OrdinalIgnoreCase)
    {
        ThrowIfDisposed();
        ThrowIfStarted();
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        _filter.WithName(text, comparison);
        return this;
    }

    /// <summary>Keep only devices matching a USB VID/PID pair.</summary>
    public DeviceWatcher WithUsbId(HardwareId vendorId, HardwareId? productId = null)
    {
        ThrowIfDisposed();
        ThrowIfStarted();
        _filter.WithUsbId(vendorId, productId);
        return this;
    }

    /// <summary>Keep only devices matching a USB VID/PID pair (parsed from strings).</summary>
    public DeviceWatcher WithUsbId(string vendorId, string? productId = null)
    {
        ThrowIfDisposed();
        ThrowIfStarted();
        _filter.WithUsbId(vendorId, productId);
        return this;
    }

    /// <summary>Keep only devices from <paramref name="manufacturer"/>.</summary>
    public DeviceWatcher ByManufacturer(string manufacturer, StringComparison comparison = StringComparison.OrdinalIgnoreCase)
    {
        ThrowIfDisposed();
        ThrowIfStarted();
        ArgumentException.ThrowIfNullOrWhiteSpace(manufacturer);
        _filter.ByManufacturer(manufacturer, comparison);
        return this;
    }

    /// <summary>Keep only the device with the specified platform-native identifier (exact match).</summary>
    public DeviceWatcher WithId(string id)
    {
        ThrowIfDisposed();
        ThrowIfStarted();
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        _filter.WithId(id);
        return this;
    }

    /// <summary>Keep only devices with the specified serial number (exact match).</summary>
    public DeviceWatcher WithSerialNumber(string serialNumber)
    {
        ThrowIfDisposed();
        ThrowIfStarted();
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
    /// <remarks>
    /// Safe on every event path: <see cref="DeviceInfo.Id"/> is carried by the
    /// unenriched device build the monitor providers use for their last-known
    /// cache, so arrivals and departures match symmetrically. Contrast the tag
    /// filters, which are deliberately absent — see the type remarks.
    /// </remarks>
    public DeviceWatcher WithIdStartsWith(string prefix, StringComparison comparison = StringComparison.OrdinalIgnoreCase)
    {
        ThrowIfDisposed();
        ThrowIfStarted();
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
    /// <remarks>
    /// Populated by the unenriched build on every Windows path, so it matches
    /// symmetrically on arrival and departure. The one exception is a removal
    /// for a device that was never cached, where the provider synthesises an
    /// id-only <see cref="DeviceInfo"/> with a null container id.
    /// </remarks>
    public DeviceWatcher WithContainerId(Guid containerId)
    {
        ThrowIfDisposed();
        ThrowIfStarted();
        _filter.WithContainerId(containerId);
        return this;
    }

    /// <summary>Keep only devices on the specified bus type.</summary>
    public DeviceWatcher WithBusType(BusType busType)
    {
        ThrowIfDisposed();
        ThrowIfStarted();
        _filter.WithBusType(busType);
        return this;
    }

    /// <summary>Keep only devices with the specified status.</summary>
    public DeviceWatcher WithStatus(DeviceStatus status)
    {
        ThrowIfDisposed();
        ThrowIfStarted();
        _filter.WithStatus(status);
        return this;
    }

    /// <summary>
    /// Keep only storage devices of the specified drive type.
    /// <para><b>Relevant categories:</b> <see cref="DeviceCategory.Storage"/>.</para>
    /// </summary>
    public DeviceWatcher WithDriveType(DriveType driveType)
    {
        ThrowIfDisposed();
        ThrowIfStarted();
        _filter.WithDriveType(driveType);
        return this;
    }

    /// <summary>
    /// Keep only devices with the specified MAC address.
    /// <para><b>Relevant categories:</b> <see cref="DeviceCategory.Network"/>,
    /// <see cref="DeviceCategory.Bluetooth"/>.</para>
    /// </summary>
    public DeviceWatcher WithMacAddress(PhysicalAddress macAddress)
    {
        ThrowIfDisposed();
        ThrowIfStarted();
        _filter.WithMacAddress(macAddress);
        return this;
    }

    /// <summary>
    /// Keep only devices whose active driver or service name contains
    /// <paramref name="text"/>.
    /// </summary>
    public DeviceWatcher WithDriver(string text, StringComparison comparison = StringComparison.OrdinalIgnoreCase)
    {
        ThrowIfDisposed();
        ThrowIfStarted();
        _filter.WithDriver(text, comparison);
        return this;
    }

    /// <summary>
    /// Keep only displays whose native resolution is at least
    /// <paramref name="minWidth"/> × <paramref name="minHeight"/> pixels.
    /// <para><b>Relevant categories:</b> <see cref="DeviceCategory.Monitor"/>,
    /// <see cref="DeviceCategory.Display"/>.</para>
    /// </summary>
    public DeviceWatcher WithMinResolution(int minWidth, int minHeight)
    {
        ThrowIfDisposed();
        ThrowIfStarted();
        _filter.WithMinResolution(minWidth, minHeight);
        return this;
    }

    /// <summary>
    /// Keep only devices with the specified negotiated USB speed.
    /// <para><b>Relevant categories:</b> <see cref="DeviceCategory.Usb"/>.</para>
    /// </summary>
    public DeviceWatcher WithUsbSpeed(UsbSpeed speed)
    {
        ThrowIfDisposed();
        ThrowIfStarted();
        _filter.WithUsbSpeed(speed);
        return this;
    }

    /// <summary>
    /// Keep only devices whose parent in the device tree matches
    /// <paramref name="parentId"/>.
    /// </summary>
    public DeviceWatcher WithParent(string parentId)
    {
        ThrowIfDisposed();
        ThrowIfStarted();
        _filter.WithParent(parentId);
        return this;
    }

    /// <summary>
    /// Keep only devices mapped to the specified OS serial port name.
    /// <para><b>Relevant categories:</b> <see cref="DeviceCategory.Ports"/>.</para>
    /// </summary>
    public DeviceWatcher WithPortName(string portName)
    {
        ThrowIfDisposed();
        ThrowIfStarted();
        _filter.WithPortName(portName);
        return this;
    }

    /// <summary>
    /// Keep only devices mapped to the specified OS serial port name.
    /// <para><b>Relevant categories:</b> <see cref="DeviceCategory.Ports"/>.</para>
    /// </summary>
    public DeviceWatcher WithPortName(SerialPortName portName)
    {
        ThrowIfDisposed();
        ThrowIfStarted();
        _filter.WithPortName(portName);
        return this;
    }

    /// <summary>
    /// Keep only battery devices with the specified power state.
    /// <para><b>Relevant categories:</b> <see cref="DeviceCategory.Battery"/>.</para>
    /// </summary>
    public DeviceWatcher WithBatteryStatus(BatteryStatus status)
    {
        ThrowIfDisposed();
        ThrowIfStarted();
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
    public DeviceWatcher PhysicalOnly()
    {
        ThrowIfDisposed();
        ThrowIfStarted();
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
    public DeviceWatcher VirtualOnly()
    {
        ThrowIfDisposed();
        ThrowIfStarted();
        _filter.VirtualOnly();
        return this;
    }

    // ── Tracking ───────────────────────────────────────────────────────

    /// <summary>
    /// Create a new <see cref="DeviceTracker"/> with the specified filter and
    /// register it with this watcher. Must be called before <see cref="StartAsync"/>.
    /// </summary>
    /// <param name="configure">Configures the tracker's filter criteria.</param>
    /// <param name="name">Optional human-readable label for the tracker.</param>
    /// <returns>The new tracker — hold a reference to read state and subscribe to events.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown if the watcher has already been started or disposed.
    /// </exception>
    public DeviceTracker AddTracker(Action<DeviceFilter> configure, string? name = null)
    {
        ThrowIfDisposed();
        ThrowIfStarted();
        ArgumentNullException.ThrowIfNull(configure);

        var filter = new DeviceFilter();
        configure(filter);

        if (!filter.HasAnyCriteria)
            throw new ArgumentException(
                "The configure delegate must set at least one filter criterion. " +
                "A tracker with no criteria would match every device.",
                nameof(configure));

        var tracker = new DeviceTracker(filter, name);
        RegisterTracker(tracker);

        _logger.LogDebug("Tracker '{Name}' registered (total: {Count})", name ?? "(unnamed)", _trackers.Count);
        return tracker;
    }

    /// <summary>
    /// Register an existing <see cref="DeviceTracker"/> instance with this watcher.
    /// Trackers retain their event handlers and <see cref="IObserver{T}"/> subscriptions
    /// from prior watcher lifetimes. Must be called before <see cref="StartAsync"/>.
    /// </summary>
    /// <returns>This watcher, for fluent chaining.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown if the watcher has already been started or disposed, or if the tracker
    /// is already bound to another active watcher.
    /// </exception>
    public DeviceWatcher AddTracker(DeviceTracker tracker)
    {
        ThrowIfDisposed();
        ThrowIfStarted();
        RegisterTracker(tracker);

        _logger.LogDebug("Re-attached 1 tracker (total: {Total})", _trackers.Count);
        return this;
    }

    /// <summary>
    /// Re-attach one or more existing <see cref="DeviceTracker"/> instances to this watcher.
    /// Trackers retain their event handlers and <see cref="IObserver{T}"/> subscriptions
    /// from prior watcher lifetimes. Must be called before <see cref="StartAsync"/>.
    /// </summary>
    /// <returns>This watcher, for fluent chaining.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown if the watcher has already been started or disposed, or if any tracker
    /// is already bound to another active watcher.
    /// </exception>
    public DeviceWatcher AddTrackers(params DeviceTracker[] trackers)
    {
        ThrowIfDisposed();
        ThrowIfStarted();
        ArgumentNullException.ThrowIfNull(trackers);

        foreach (var tracker in trackers)
            RegisterTracker(tracker);

        _logger.LogDebug("Re-attached {NewCount} tracker(s) (total: {Total})",
            trackers.Length, _trackers.Count);
        return this;
    }

    /// <summary>
    /// Re-attach a collection of existing <see cref="DeviceTracker"/> instances to this watcher.
    /// </summary>
    /// <returns>This watcher, for fluent chaining.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown if the watcher has already been started or disposed, or if any tracker
    /// is already bound to another active watcher.
    /// </exception>
    public DeviceWatcher AddTrackers(IEnumerable<DeviceTracker> trackers)
    {
        ThrowIfDisposed();
        ThrowIfStarted();
        ArgumentNullException.ThrowIfNull(trackers);

        int count = 0;
        foreach (var tracker in trackers)
        {
            RegisterTracker(tracker);
            count++;
        }

        _logger.LogDebug("Re-attached {NewCount} tracker(s) (total: {Total})",
            count, _trackers.Count);
        return this;
    }

    private void RegisterTracker(DeviceTracker tracker)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        tracker.Bind(this);
        _trackers.Add(tracker);
    }

    /// <summary>
    /// Replay every device currently in the watcher's
    /// <see cref="_deviceCache"/> through <paramref name="tracker"/>'s
    /// non-notifying replay hook. Called by
    /// <see cref="DeviceTracker.Reconfigure"/> /
    /// <see cref="DeviceTracker.ReplaceProfiles"/> while the tracker
    /// holds its own <c>_lock</c> — see ADR-0046.
    /// </summary>
    /// <remarks>
    /// Takes a snapshot of the cache under its lock + iterates outside
    /// (no nested locks). The tracker's own lock guarantees no concurrent
    /// device-event processing during the replay; events that arrive
    /// during the snapshot or iteration apply on top of the new state
    /// when the tracker releases its lock.
    /// </remarks>
    internal void ReplayKnownDevicesTo(DeviceTracker tracker)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        DeviceInfo[] snapshot;
        lock (_deviceCache)
        {
            snapshot = new DeviceInfo[_deviceCache.Count];
            _deviceCache.Values.CopyTo(snapshot, 0);
        }
        foreach (var device in snapshot)
            tracker.ReplayDeviceInternal(device);
    }

    /// <summary>
    /// The watcher's current known-device snapshot, read straight from the
    /// in-memory cache. This is a <b>cheap cached read</b>: it returns the
    /// devices the watcher already enumerated during <see cref="StartAsync"/>
    /// (kept current by subsequent <see cref="PropertyChanged"/> events) and
    /// triggers <b>no fresh OS device-tree walk</b> — in particular no
    /// per-device cfgmgr32 property read on Windows. Use it instead of
    /// re-running <see cref="Devices.Enumerate()"/>
    /// when a watcher is already running and you only need the set it has
    /// already paid for.
    /// </summary>
    /// <remarks>
    /// <para><b>Filter state:</b> the returned set honours this watcher's
    /// filters (<see cref="OfCategory"/>, <see cref="Where"/>,
    /// <see cref="WithName"/>, <see cref="WithUsbId(HardwareId, HardwareId?)"/>,
    /// …). The cache is only seeded for devices that pass the watcher-level
    /// filter, so this is the watcher's <i>filtered</i> known set, not the raw
    /// whole-tree snapshot.</para>
    /// <para><b>When valid:</b> empty before <see cref="StartAsync"/> and until
    /// the initial snapshot settles. After <see cref="StartAsync"/> returns the
    /// snapshot is complete. The cache reflects the initial filtered snapshot
    /// plus any <see cref="PropertyChanged"/> updates; on platforms that push
    /// arrivals/removals (and via the watcher's own event stream), prefer the
    /// live <see cref="Appeared"/>/<see cref="Disappeared"/> events to track
    /// hot-plug changes that post-date the snapshot.</para>
    /// <para><b>Thread safety:</b> returns a coherent point-in-time copy taken
    /// under the cache lock; concurrent provider events mutating the cache do
    /// not tear the returned list. The list is a snapshot and does not update
    /// after the call returns.</para>
    /// </remarks>
    public IReadOnlyList<DeviceInfo> KnownDevices
    {
        get
        {
            lock (_deviceCache)
            {
                var snapshot = new DeviceInfo[_deviceCache.Count];
                _deviceCache.Values.CopyTo(snapshot, 0);
                return snapshot;
            }
        }
    }

    // ── Group Tracking ─────────────────────────────────────────────────

    /// <summary>
    /// Create a new <see cref="MultiDeviceTracker"/> with the specified filter and
    /// register it with this watcher. Must be called before <see cref="StartAsync"/>.
    /// The group tracker dynamically creates child <see cref="DeviceTracker"/>
    /// instances for each unique device that matches the filter.
    /// </summary>
    /// <param name="configure">Configures the group's filter criteria.</param>
    /// <param name="name">Optional human-readable label for the group.</param>
    /// <returns>The new group tracker.</returns>
    public MultiDeviceTracker AddMultiTracker(Action<DeviceFilter> configure, string? name = null)
    {
        ThrowIfDisposed();
        ThrowIfStarted();
        ArgumentNullException.ThrowIfNull(configure);

        var multiTracker = new MultiDeviceTracker(configure, name);
        RegisterMultiTracker(multiTracker);

        _logger.LogDebug("Group tracker '{Name}' registered (total groups: {Count})",
            name ?? "(unnamed)", _multiTrackers.Count);
        return multiTracker;
    }

    /// <summary>
    /// Register an existing <see cref="MultiDeviceTracker"/> instance with this
    /// watcher. Must be called before <see cref="StartAsync"/>.
    /// </summary>
    /// <returns>This watcher, for fluent chaining.</returns>
    public DeviceWatcher AddMultiTracker(MultiDeviceTracker multiTracker)
    {
        ThrowIfDisposed();
        ThrowIfStarted();
        RegisterMultiTracker(multiTracker);

        _logger.LogDebug("Re-attached group tracker (total groups: {Total})", _multiTrackers.Count);
        return this;
    }

    private void RegisterMultiTracker(MultiDeviceTracker multiTracker)
    {
        ArgumentNullException.ThrowIfNull(multiTracker);
        multiTracker.Bind(this);
        _multiTrackers.Add(multiTracker);
    }

    // ── Events ─────────────────────────────────────────────────────────

    /// <summary>
    /// Raised when a matching device enters the OS device tree
    /// (installed, paired, plugged in). Fires for every known device
    /// during the initial snapshot.
    /// </summary>
    public event EventHandler<DeviceChangeEventArgs>? Appeared;

    /// <summary>
    /// Raised when a matching device leaves the OS device tree
    /// (uninstalled, unpaired, unplugged).
    /// </summary>
    public event EventHandler<DeviceChangeEventArgs>? Disappeared;

    /// <summary>
    /// Raised when a matching device becomes physically active
    /// (driver started, hardware present and working). For USB devices
    /// this fires simultaneously with <see cref="Appeared"/>; for
    /// Bluetooth devices it fires when the device comes into range.
    /// </summary>
    public event EventHandler<DeviceChangeEventArgs>? Activated;

    /// <summary>
    /// Raised when a matching device becomes physically inactive
    /// (driver stopped, hardware disconnected). Also fires as a
    /// cascade when an active device <see cref="Disappeared">disappears</see>.
    /// </summary>
    public event EventHandler<DeviceChangeEventArgs>? Deactivated;

    /// <summary>
    /// Raised when one or more properties on a matching device change value
    /// between OS-delivered modification events. Provides both the previous
    /// and current <see cref="DeviceInfo"/> snapshots and the set of property
    /// names that changed.
    /// </summary>
    /// <remarks>
    /// <para>Detection is event-driven from native OS push: UPower D-Bus
    /// <c>PropertiesChanged</c> / udev <c>change</c> on Linux; IOKit
    /// <c>kIOGeneralInterest</c> on macOS. Windows cfgmgr32 has no property-change
    /// action, and per ADR-0054 Periphery no longer synthesizes one with a
    /// whole-tree poll, so on Windows this event is dormant — it would fire only if
    /// a specific OS property notification were wired for a specific property. Keep
    /// a mutable property fresh on Windows via that property's own OS signal, or by
    /// polling the single device that owns it.</para>
    /// <para>Fires for all property changes including
    /// <see cref="DeviceInfo.IsActive"/> transitions, which are
    /// complementary to <see cref="Activated"/>/<see cref="Deactivated"/>.</para>
    /// </remarks>
    public event EventHandler<DevicePropertyChangedEventArgs>? PropertyChanged;

    // ── Lifecycle ──────────────────────────────────────────────────────

    /// <summary>
    /// Begin watching. Fires <see cref="Activated"/> for every device
    /// already active, then continues raising events for future changes.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown if the watcher has already been started.
    /// </exception>
    /// <exception cref="ObjectDisposedException">
    /// Thrown if the watcher has been disposed.
    /// </exception>
    /// <exception cref="DeviceProviderException">
    /// Thrown if the underlying platform provider fails to initialize.
    /// </exception>
    public async Task StartAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();

        await _lifecycleLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_started)
                throw new InvalidOperationException("The watcher has already been started.");

            _logger.LogInformation("Starting device watcher");

            ResetAttemptCounters();

            // Provider registration + the initial device snapshot are blocking,
            // synchronous OS work: SetupAPI/cfgmgr32 on Windows has no async API,
            // so the monitor provider's registration runs inline and the snapshot
            // enumeration's only await (the enrichment pipeline) completes
            // synchronously when no async enrichers are registered — i.e. the whole
            // enumeration runs on the caller's thread. Offload it to the thread pool
            // so a caller on a latency-sensitive thread (e.g. a UI thread opening a
            // device-provisioning view) isn't frozen by a full unfiltered device
            // walk. This honours the watcher's contract that events fire on
            // thread-pool threads; an awaiting caller still observes the same
            // post-condition (provider started + snapshot complete) on return.
            // The attempt owns everything it creates until it commits. Nothing is
            // written to _provider or _started until both the registration and the
            // snapshot have succeeded, so a failed attempt leaves no provider-side
            // state behind and the same instance can be started again with its
            // trackers and subscriptions intact.
            await Task.Run(async () =>
            {
                IDeviceMonitorProvider? provider = null;

                // True only when THIS attempt created the provider. A caller-supplied
                // one (from the injecting constructor, which is public) belongs to the
                // caller — disposing it on rollback would leave a retry re-using a
                // disposed instance.
                bool ownsProvider = false;

                try
                {
                    // 1. Start event watchers FIRST so no events are lost
                    provider =
                        _monitorOverride
                        ?? _monitorFactory?.Invoke()
                        ?? DeviceProviderFactory.GetMonitorProvider();

                    // Everything except a caller-supplied instance is ours.
                    ownsProvider = _monitorOverride is null;

                    provider.DeviceAppeared += OnProviderAppeared;
                    provider.DeviceDisappeared += OnProviderDisappeared;
                    provider.DeviceActivated += OnProviderActivated;
                    provider.DeviceDeactivated += OnProviderDeactivated;
                    provider.DevicePropertyChanged += OnProviderPropertyChanged;

                    // When trackers or group trackers are registered, the OS subscription
                    // must be unfiltered so that events for all tracked categories arrive.
                    // The watcher-level filter still applies to global events in-memory.
                    // Since ADR-0054 removed the Windows whole-tree property scan, this
                    // breadth no longer feeds any periodic re-walk — it only widens live
                    // event fan-out (and, on Linux/macOS, the subsystem/class subscription).
                    var providerFilter = (_trackers.Count > 0 || _multiTrackers.Count > 0)
                        ? new DeviceFilter() : _filter;
                    await provider.StartAsync(providerFilter, ct).ConfigureAwait(false);

                    // 2. Snapshot already-active devices via the query provider
                    //    Events that arrive during the snapshot are handled by the
                    //    monitor provider above — the watcher-then-snapshot ordering
                    //    guarantees no device is missed.
                    await SnapshotCurrentDevicesAsync(ct).ConfigureAwait(false);
                }
                catch
                {
                    await RollBackAttemptAsync(provider, ownsProvider).ConfigureAwait(false);
                    throw;
                }

                // Commit. _provider before _started: DisposeAsync releases on the
                // provider being non-null, so publishing the flag first would open a
                // window where a concurrent dispose sees a started watcher with no
                // provider to release.
                _provider = provider;
                _started = true;
            }, ct).ConfigureAwait(false);

            _logger.LogInformation("Device watcher started");
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private void ResetAttemptCounters()
    {
        _appearedEventCount = 0;
        _activatedEventCount = 0;
        _deactivatedEventCount = 0;
        _disappearedEventCount = 0;
    }

    /// <summary>
    /// Undoes everything a failed start attempt created, so the same watcher can
    /// be started again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Dispose before clearing.</b> Detaching a handler does not stop one that
    /// has already been entered, because the delegate list was captured at the
    /// point of invocation. Both platform providers make dispose a join
    /// (<c>CmNotifyHandle.ReleaseHandle</c> blocks until an in-progress callback
    /// returns; the Linux monitor awaits its reader task), so disposing first is
    /// what guarantees no handler is still running when the caches are touched.
    /// </para>
    /// <para>
    /// <b><see cref="_knownConnectedIds"/> is deliberately NOT cleared.</b> The
    /// handlers go live at the <c>+=</c> above, before <c>StartAsync</c> returns
    /// on either provider, so a live arrival during the attempt may already have
    /// raised <see cref="Activated"/> to the consumer and recorded its id here.
    /// Clearing it would make the eventual <see cref="Disappeared"/> find
    /// <c>wasConnected == false</c> and never cascade <see cref="Deactivated"/>,
    /// orphaning an event the consumer has already seen.
    /// </para>
    /// <para>
    /// <see cref="_deviceCache"/> <i>is</i> cleared, because
    /// <see cref="KnownDevices"/> documents itself as empty until a start
    /// settles, and a failed attempt must not leave it reporting a snapshot that
    /// never completed.
    /// </para>
    /// <para>
    /// <b>Neither choice is clean, and the residue is known.</b> Keeping the ids
    /// is right for a device that is still attached — the retry sees it again
    /// and does not re-raise <see cref="Activated"/>. It is wrong for one
    /// unplugged between the failure and the retry: the handlers are detached,
    /// so no <see cref="Disappeared"/> can arrive to remove the id, and a later
    /// replug is then suppressed as already-connected. Clearing swaps one fault
    /// for the other. Reconciling the two properly means diffing the retry's
    /// snapshot against what the failed attempt recorded, which belongs with the
    /// rest of the cross-attempt state work rather than here.
    /// </para>
    /// </remarks>
    private async Task RollBackAttemptAsync(IDeviceMonitorProvider? provider, bool ownsProvider)
    {
        if (provider is not null)
        {
            provider.DeviceAppeared -= OnProviderAppeared;
            provider.DeviceDisappeared -= OnProviderDisappeared;
            provider.DeviceActivated -= OnProviderActivated;
            provider.DeviceDeactivated -= OnProviderDeactivated;
            provider.DevicePropertyChanged -= OnProviderPropertyChanged;

            if (ownsProvider)
            {
                // A failing dispose must not mask the fault that caused the rollback.
                try
                {
                    await provider.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Disposing the provider after a failed start attempt threw");
                }
            }
        }

        lock (_deviceCache)
            _deviceCache.Clear();
        ResetAttemptCounters();

        _logger.LogDebug("Rolled back a failed device-watcher start attempt");
    }

    // ── Snapshot ───────────────────────────────────────────────────────

    /// <summary>
    /// Enumerates all OS-known devices and raises <see cref="Appeared"/>
    /// for each one. For devices that are also physically active
    /// (<see cref="DeviceInfo.IsActive"/>), also raises <see cref="Activated"/>.
    /// Evaluates all registered trackers against the snapshot.
    /// </summary>
    private async Task SnapshotCurrentDevicesAsync(CancellationToken ct)
    {
        _logger.LogDebug("Snapshotting OS-known devices");

        var queryProvider = _providerOverride ?? DeviceProviderFactory.GetProvider();

        // When trackers or group trackers exist, query all devices (unfiltered) so
        // each tracker sees its matches. This is the one-time startup snapshot — a
        // single enumeration, not the per-tick whole-tree scan that ADR-0054 removed.
        var queryFilter = (_trackers.Count > 0 || _multiTrackers.Count > 0)
            ? new DeviceFilter() : _filter;
        int snapshotCount = 0;

        await foreach (var device in queryProvider.EnumerateAsync(queryFilter, ct).ConfigureAwait(false))
        {
            // Global events: apply watcher-level filter
            if (_filter.Matches(device))
            {
                snapshotCount++;
                Interlocked.Increment(ref _appearedEventCount);
                _logger.LogDebug("Snapshot appeared (#{Count}): {DeviceId} ({DeviceName})",
                    snapshotCount, device.Id, device.Name ?? "(unnamed)");

                Appeared?.Invoke(this, new DeviceChangeEventArgs(device));

                    // Seed the property-change cache with the initial snapshot.
                    lock (_deviceCache) _deviceCache[device.Id] = device;

                    // Guard on the Add, exactly as OnProviderActivated does. The
                    // provider goes live before the snapshot walk begins, so a device
                    // that arrived during the walk has already had Activated raised;
                    // without this the snapshot raises it a second time.
                    if (device.IsActive)
                    {
                        bool isNew;
                        lock (_knownConnectedIds)
                            isNew = _knownConnectedIds.Add(device.Id);

                        if (isNew)
                        {
                            Interlocked.Increment(ref _activatedEventCount);
                            Activated?.Invoke(this, new DeviceChangeEventArgs(device));
                        }
                    }
            }

            // Per-tracker fan-out: always notify appeared
            FanOutAppeared(device);

            // Per-group-tracker fan-out: always notify appeared
            FanOutGroupAppeared(device);

            // Per-tracker fan-out: notify activated if active
            if (device.IsActive)
            {
                FanOutActivated(device);
                FanOutGroupActivated(device);
            }
        }

        _logger.LogInformation("Device snapshot completed. Devices found: {Count}", snapshotCount);

        // The initial snapshot has settled. Trackers that the fan-out matched have
        // already left DeviceActivityStatus.Unknown via Resolve(); signal every
        // bound tracker once so any still-Unknown (unmatched) tracker resolves to
        // its determined state (Absent) and emits the single Unknown -> Absent
        // transition. The hook early-returns for already-resolved trackers, so this
        // is a no-op for matched ones. Group trackers (MultiDeviceTracker) need no
        // call — their children are created already-matched and never sit Unknown.
        foreach (var tracker in _trackers)
            tracker.OnInitialEnumerationComplete();
    }

    /// <summary>
    /// Stop watching and release OS resources.
    /// This method is idempotent and thread-safe.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        bool weDisposedIt = false;
        try
        {
            if (_disposed) return;

            if (_started)
            {
                _logger.LogInformation(
                    "Stopping device watcher. Events raised - Appeared: {AppearedCount}, Activated: {ActivatedCount}, Deactivated: {DeactivatedCount}, Disappeared: {DisappearedCount}",
                    _appearedEventCount, _activatedEventCount, _deactivatedEventCount, _disappearedEventCount);
            }

            // Keyed on the resource, not the flag. A start attempt now commits
            // _started only after it succeeds, so a rollback that failed to null
            // _provider would otherwise leak its cfgmgr32 registration or udev fd
            // with no second chance to release it.
            {
                if (_provider is not null)
                {
                    _provider.DeviceAppeared -= OnProviderAppeared;
                    _provider.DeviceDisappeared -= OnProviderDisappeared;
                    _provider.DeviceActivated -= OnProviderActivated;
                    _provider.DeviceDeactivated -= OnProviderDeactivated;
                    _provider.DevicePropertyChanged -= OnProviderPropertyChanged;
                    await _provider.DisposeAsync().ConfigureAwait(false);
                    _provider = null;
                }

                lock (_knownConnectedIds) _knownConnectedIds.Clear();
                _started = false;
            }

            // Unbind all trackers — sets them inert (IsPresent/IsActive → false,
            // subscribers notified) but leaves event wiring intact for re-use.
            // Must happen regardless of _started: Track() calls Bind() before
            // StartAsync(), so trackers are bound even if the watcher never started.
            foreach (var tracker in _trackers)
                tracker.Unbind();
            _trackers.Clear();

            foreach (var group in _multiTrackers)
                group.Unbind();
            _multiTrackers.Clear();

            _disposed = true;
            weDisposedIt = true;
            _logger.LogDebug("Device watcher disposed");
        }
        finally
        {
            // Only the thread that actually performed disposal should release and
            // dispose the semaphore. Concurrent calls that find _disposed already
            // true return early; the semaphore is already disposed at that point,
            // so attempting Release() would throw ObjectDisposedException.
            if (weDisposedIt)
            {
                _lifecycleLock.Release();
                _lifecycleLock.Dispose();
            }
        }
    }

    // ── Internal — provider event handlers ───────────────────────────────

    private void OnProviderAppeared(object? sender, DeviceChangeEventArgs e)
    {
        if (_filter.Matches(e.Device))
        {
            Interlocked.Increment(ref _appearedEventCount);
            _logger.LogDebug("Device appeared (event #{Count}): {DeviceId} ({DeviceName})",
                _appearedEventCount, e.Device.Id, e.Device.Name ?? "(unnamed)");
            Appeared?.Invoke(this, e);
        }
        else
        {
            _logger.LogTrace("Device appeared but filtered out: {DeviceId} ({DeviceName})",
                e.Device.Id, e.Device.Name ?? "(unnamed)");
        }

        FanOutAppeared(e.Device);
        FanOutGroupAppeared(e.Device);
    }

    private void OnProviderActivated(object? sender, DeviceChangeEventArgs e)
    {
        // _knownConnectedIds.Add returns false when the ID is already present, which
        // happens when both a DEVICEINTERFACEARRIVAL (HandleDeviceArrival) and a
        // DEVICEINSTANCESTARTED (HandleInstanceStarted) fire for the same hard plug-in.
        // Returning early deduplicates Activated and FanOutActivated for that case.
        bool isNew;
        lock (_knownConnectedIds)
            isNew = _knownConnectedIds.Add(e.Device.Id);

        if (!isNew) return;

        if (_filter.Matches(e.Device))
        {
            Interlocked.Increment(ref _activatedEventCount);
            _logger.LogDebug("Device activated (event #{Count}): {DeviceId} ({DeviceName})",
                _activatedEventCount, e.Device.Id, e.Device.Name ?? "(unnamed)");
            Activated?.Invoke(this, e);
        }
        else
        {
            _logger.LogTrace("Device activated but filtered out: {DeviceId} ({DeviceName})",
                e.Device.Id, e.Device.Name ?? "(unnamed)");
        }

        FanOutActivated(e.Device);
        FanOutGroupActivated(e.Device);
    }

    private void OnProviderDeactivated(object? sender, DeviceChangeEventArgs e)
    {
        lock (_knownConnectedIds) _knownConnectedIds.Remove(e.Device.Id);

        if (_filter.Matches(e.Device))
        {
            Interlocked.Increment(ref _deactivatedEventCount);
            _logger.LogDebug("Device deactivated (event #{Count}): {DeviceId} ({DeviceName})",
                _deactivatedEventCount, e.Device.Id, e.Device.Name ?? "(unnamed)");
            Deactivated?.Invoke(this, e);
        }
        else
        {
            _logger.LogTrace("Device deactivated but filtered out: {DeviceId} ({DeviceName})",
                e.Device.Id, e.Device.Name ?? "(unnamed)");
        }

        FanOutDeactivated(e.Device);
        FanOutGroupDeactivated(e.Device);
    }

    private void OnProviderDisappeared(object? sender, DeviceChangeEventArgs e)
    {
        // If this device was active, cascade a Deactivated event first.
        // The Remove must be atomic with the check to avoid double-cascades
        // when a Deactivated + Disappeared arrive on concurrent threads.
        bool wasConnected;
        lock (_knownConnectedIds) wasConnected = _knownConnectedIds.Remove(e.Device.Id);
        if (wasConnected)
        {
            if (_filter.Matches(e.Device))
            {
                Interlocked.Increment(ref _deactivatedEventCount);
                _logger.LogDebug("Device deactivated (cascade from disappeared): {DeviceId} ({DeviceName})",
                    e.Device.Id, e.Device.Name ?? "(unnamed)");
                Deactivated?.Invoke(this, e);
            }

            FanOutDeactivated(e.Device);
            FanOutGroupDeactivated(e.Device);
        }

        if (_filter.Matches(e.Device))
        {
            Interlocked.Increment(ref _disappearedEventCount);
            _logger.LogDebug("Device disappeared (event #{Count}): {DeviceId} ({DeviceName})",
                _disappearedEventCount, e.Device.Id, e.Device.Name ?? "(unnamed)");
            Disappeared?.Invoke(this, e);
        }
        else
        {
            _logger.LogTrace("Device disappeared but filtered out: {DeviceId} ({DeviceName})",
                e.Device.Id, e.Device.Name ?? "(unnamed)");
        }

        FanOutDisappeared(e.Device);
        FanOutGroupDisappeared(e.Device);
    }

    private void OnProviderPropertyChanged(object? sender, DeviceModificationEventArgs e)
    {
        // Update the cache regardless of filter — trackers may be watching
        // devices that don't match the watcher-level filter.
        lock (_deviceCache) _deviceCache[e.Current.Id] = e.Current;

        var changedProperties = DeviceInfoDiff.Compute(e.Previous, e.Current);
        if (changedProperties.Count == 0) return;

        var args = new DevicePropertyChangedEventArgs(e.Previous, e.Current, changedProperties);

        if (_filter.Matches(e.Current))
            PropertyChanged?.Invoke(this, args);

        FanOutPropertyChanged(e.Previous, e.Current, changedProperties);
        FanOutGroupPropertyChanged(e.Previous, e.Current, changedProperties);
    }

    // ── Internal — tracker fan-out ─────────────────────────────────────

    private void FanOutAppeared(DeviceInfo device)
    {
        foreach (var tracker in _trackers)
        {
            if (tracker.Matches(device))
                tracker.OnDeviceAppeared(device);
        }
    }

    private void FanOutActivated(DeviceInfo device)
    {
        foreach (var tracker in _trackers)
        {
            if (tracker.Matches(device))
                tracker.OnDeviceConnected(device);
        }
    }

    private void FanOutDeactivated(DeviceInfo device)
    {
        foreach (var tracker in _trackers)
        {
            if (tracker.Matches(device))
                tracker.OnDeviceDisconnected(device);
        }
    }

    private void FanOutDisappeared(DeviceInfo device)
    {
        foreach (var tracker in _trackers)
        {
            if (tracker.Matches(device))
                tracker.OnDeviceDisappeared(device);
        }
    }

    private void FanOutPropertyChanged(DeviceInfo previous, DeviceInfo current, IReadOnlySet<string> changedProperties)
    {
        foreach (var tracker in _trackers)
            tracker.OnDevicePropertyChanged(previous, current, changedProperties);
    }

    // ── Internal — group tracker fan-out ────────────────────────────────

    private void FanOutGroupAppeared(DeviceInfo device)
    {
        foreach (var group in _multiTrackers)
            group.OnDeviceAppeared(device);
    }

    private void FanOutGroupActivated(DeviceInfo device)
    {
        foreach (var group in _multiTrackers)
            group.OnDeviceActivated(device);
    }

    private void FanOutGroupDeactivated(DeviceInfo device)
    {
        foreach (var group in _multiTrackers)
            group.OnDeviceDeactivated(device);
    }

    private void FanOutGroupDisappeared(DeviceInfo device)
    {
        foreach (var group in _multiTrackers)
            group.OnDeviceDisappeared(device);
    }

    private void FanOutGroupPropertyChanged(DeviceInfo previous, DeviceInfo current, IReadOnlySet<string> changedProperties)
    {
        foreach (var group in _multiTrackers)
            group.OnDevicePropertyChanged(previous, current, changedProperties);
    }

    private void ThrowIfStarted()
    {
        if (_started)
            throw new InvalidOperationException("Cannot modify filters after the watcher has started.");
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(DeviceWatcher));
    }
}
