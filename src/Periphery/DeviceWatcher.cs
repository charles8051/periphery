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
    // Guards _trackers and _multiTrackers. Held only for a mutation or a
    // snapshot copy — never while a tracker is being notified, because that runs
    // consumer code and a lock across it would be a deadlock waiting for a
    // handler that blocks. Registration happens before a start and disposal can
    // come from any thread, so copying the lists unsynchronised could tear or
    // throw from CopyTo, and that throw lands outside the per-target try/catch
    // the fan-out relies on (#143 review).
    private readonly object _trackersLock = new();

    // Set inside _trackersLock the moment disposal takes the registrations, which
    // is earlier than _disposed. Registration checks it under the same lock, so a
    // tracker cannot be bound to a watcher that has already walked its list —
    // including from an Unbind subscriber running during that very disposal
    // (#143 review).
    private bool _trackersReleased;

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

    // Caches the most recent DeviceInfo snapshot per device ID. Seeded during
    // StartAsync, maintained by every lifecycle edge (ADR-0087 D3), and updated on
    // each PropertyChanged event from the provider. Used as the "previous" snapshot
    // for diff computation, and replayed into a tracker on Reconfigure.
    private readonly Dictionary<DeviceId, DeviceInfo> _deviceCache = new();

    // Ids the live provider stream handled while the startup walk was in flight
    // (ADR-0087 D2). The monitor provider goes live before SnapshotCurrentDevicesAsync
    // begins, so for any id in here the live stream verdict is strictly fresher than
    // the payload the walk is holding - the walk enumerated it at an earlier instant
    // and has been carrying it ever since.
    //
    // Republishing that payload as an edge is what produces #177 demotion (a stale
    // inactive payload for a device the live stream already started) and its mirror (a
    // stale active payload for a device the live stream already removed, which latches
    // permanently because Disappeared has been consumed). It is also the duplicate
    // startup Appeared: the walk already guards Activated against _knownConnectedIds
    // and has never guarded Appeared.
    //
    // Recorded from the PRESENCE edges only - Appeared and Disappeared. An activity edge
    // says nothing about whether the devnode is in the tree, so it must not suppress the
    // walk's announcement: a device already present at start that merely restarts its
    // driver mid-walk would otherwise have its only Appeared skipped, leaving every tracker
    // that needs presence stuck Absent for a device that never went anywhere.
    //
    // Suppressing activity edges is also unnecessary, because D1 already covers them: the
    // walk's stale payload is reconciled against _knownConnectedIds before any consumer
    // sees it, so a late inactive payload cannot demote a device the live stream activated.
    // Presence supersession and activity reconciliation are complementary, not overlapping.
    //
    // Scoped to the start window and cleared on both edges of it, so this is bounded by
    // the devices that happen to change during one enumeration rather than by the tree.
    // _snapshotInFlight is guarded by the set own lock: the flag and the membership
    // it authorises have to move together.
    private readonly HashSet<DeviceId> _liveStreamHandledIds = new();
    private bool _snapshotInFlight;

    private void NoteLiveStreamHandled(DeviceId id)
    {
        lock (_liveStreamHandledIds)
            if (_snapshotInFlight) _liveStreamHandledIds.Add(id);
    }

    private void OpenSnapshotWindow()
    {
        lock (_liveStreamHandledIds)
        {
            _liveStreamHandledIds.Clear();
            _snapshotInFlight = true;
        }
    }

    private void CloseSnapshotWindow()
    {
        lock (_liveStreamHandledIds)
        {
            _snapshotInFlight = false;
            _liveStreamHandledIds.Clear();
        }
    }

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
    /// <remarks>
    /// <b>A start that fails after registering cannot be retried on this
    /// overload.</b> The instance belongs to the caller, so a rolled-back attempt
    /// will not dispose it — and <see cref="IDeviceMonitorProvider"/> has no stop
    /// or reset, while every shipped implementation latches its start ("dispose
    /// and create a new monitor to restart"). So if <c>StartAsync</c> succeeds on
    /// the provider and the initial snapshot then throws, the retry cannot
    /// re-register it. Use the
    /// <see cref="DeviceWatcher(IDeviceProvider, Func{IDeviceMonitorProvider})"/>
    /// overload where that matters: it mints a provider per attempt, the way
    /// production does, so the watcher owns each one and disposes it on rollback.
    /// </remarks>
    public DeviceWatcher(IDeviceProvider provider, IDeviceMonitorProvider monitor)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(monitor);
        _providerOverride = provider;
        _monitorOverride = monitor;
    }

    /// <summary>
    /// Creates a watcher that mints a monitor provider per start attempt, the
    /// way production does. Unlike
    /// <see cref="DeviceWatcher(IDeviceProvider, IDeviceMonitorProvider)"/>, the
    /// watcher <b>owns</b> what the factory returns and disposes it when an
    /// attempt is rolled back — which is what makes a failed start retryable
    /// even once the registration had succeeded.
    /// </summary>
    /// <param name="provider">
    /// Provider used to enumerate the initial device snapshot. Must not be <see langword="null"/>.
    /// </param>
    /// <param name="monitorFactory">
    /// Invoked once per start attempt. Must return a fresh, unstarted provider
    /// each time; returning the same instance twice reintroduces the limitation
    /// this overload exists to remove.
    /// </param>
    public DeviceWatcher(IDeviceProvider provider, Func<IDeviceMonitorProvider> monitorFactory)
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

        // Bind and add under one lock. Split, disposal could run between them and
        // walk a list the tracker had not reached yet, leaving it bound to a
        // disposed watcher with nothing left to unbind it. Bind takes only the
        // tracker's own lock and raises nothing, so it is safe to hold this one
        // across it.
        lock (_trackersLock)
        {
            ObjectDisposedException.ThrowIf(_trackersReleased, this);
            tracker.Bind(this);
            _trackers.Add(tracker);
        }
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
        {
            // Replay raises StateChanged per device, so one throwing handler must
            // not abandon the rest of the replay and leave the tracker holding a
            // partial view of the tree (#143).
            try
            {
                tracker.ReplayDeviceInternal(device);
            }
            catch (Exception ex)
            {
                LogFanOutFaulted(ex, "Replay", "tracker", tracker.Name, device.Id);
            }
        }
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
    /// …). The underlying cache holds every device the provider reported,
    /// because it is also the replay source for trackers, which may hold devices
    /// the watcher-level filter rejects; this property applies the filter on
    /// read, so it is the watcher's <i>filtered</i> known set, not the raw
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
            DeviceInfo[] cached;
            lock (_deviceCache)
            {
                cached = new DeviceInfo[_deviceCache.Count];
                _deviceCache.Values.CopyTo(cached, 0);
            }

            // The cache is the replay source for trackers (ADR-0087 D3), so it holds every
            // device the provider reported, including ones this watcher's own filter
            // rejects. The public view applies the filter here, outside the lock, because
            // Where() predicates are caller code.
            var known = new List<DeviceInfo>(cached.Length);
            foreach (var device in cached)
                if (_filter.Matches(device)) known.Add(device);
            return known;
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

        lock (_trackersLock)
        {
            ObjectDisposedException.ThrowIf(_trackersReleased, this);
            multiTracker.Bind(this);
            _multiTrackers.Add(multiTracker);
        }
    }

    // ── Events ─────────────────────────────────────────────────────────

    /// <summary>
    /// Raised when a matching device enters the OS device tree
    /// (installed, paired, plugged in). Fires for every known device
    /// during the initial snapshot.
    /// </summary>
    /// <remarks>
    /// <para><b>Do not open a device from this handler.</b> Presence means the OS has an
    /// entry for it; it does not mean the driver has started, so an open here can fail.
    /// Use <see cref="Activated"/> for anything that acquires a handle, opens a port or
    /// starts a session, and prefer <see cref="DeviceProxy"/> /
    /// <see cref="DeviceSessionHost{TSession}"/> over subscribing here at all — they implement the
    /// activity binding and its Windows caveat for you (ADR-0088).</para>
    /// <para>This event is for inventory: does the device exist, do I care about it,
    /// should I list or track it. A handler that needs both does the presence work here
    /// and the I/O on <see cref="Activated"/>.</para>
    /// <para><b>A throwing handler is isolated and cannot break the others.</b>
    /// Subscribers are invoked one at a time, the remaining ones still run, and the
    /// exception never unwinds the platform notification pump that raised it
    /// (issue #143). The fault is recorded twice over: at
    /// <see cref="Microsoft.Extensions.Logging.LogLevel.Error"/>, naming the device
    /// and the handler that failed, and on the
    /// <c>periphery.events.handler_faults</c> meter, which no logging configuration
    /// can filter away. The watcher will not crash the process on a handler's
    /// behalf, so handle errors inside the handler.</para>
    /// </remarks>
    public event EventHandler<DeviceChangeEventArgs>? Appeared;

    /// <summary>
    /// Raised when a matching device leaves the OS device tree
    /// (uninstalled, unpaired, unplugged).
    /// </summary>
    /// <remarks>
    /// <para><b>A throwing handler is isolated and cannot break the others.</b>
    /// Subscribers are invoked one at a time, the remaining ones still run, and the
    /// exception never unwinds the platform notification pump that raised it
    /// (issue #143). The fault is recorded twice over: at
    /// <see cref="Microsoft.Extensions.Logging.LogLevel.Error"/>, naming the device
    /// and the handler that failed, and on the
    /// <c>periphery.events.handler_faults</c> meter, which no logging configuration
    /// can filter away. The watcher will not crash the process on a handler's
    /// behalf, so handle errors inside the handler.</para>
    /// </remarks>
    public event EventHandler<DeviceChangeEventArgs>? Disappeared;

    /// <summary>
    /// Raised when a matching device becomes physically active
    /// (driver started, hardware present and working). For a USB device this
    /// normally follows <see cref="Appeared"/> closely enough to look like one
    /// event; for a Bluetooth device it fires when the device comes into range,
    /// which can be long afterwards.
    /// </summary>
    /// <remarks>
    /// <para><b>Not simultaneous with <see cref="Appeared"/>, and not ordered
    /// against it.</b> The startup snapshot raises them in sequence, presence
    /// first. The live paths come from separate provider callbacks, and on
    /// Windows a devnode is enumerated and started as two notifications with
    /// nothing serialising the two raises. Treat them as independent edges on
    /// independent axes (ADR-0004), not one arrival split in two.</para>
    /// <para>This is the edge to open a device on (ADR-0088). Its pair is
    /// <see cref="Deactivated"/> — but that pairing does not hold on Windows, which
    /// pushes no soft driver-stop signal (ADR-0054), so a handle opened here is torn
    /// down only by <see cref="Disappeared"/>. A device that stops without leaving the
    /// tree, such as a Bluetooth peripheral going out of range, produces no close edge
    /// there at all.</para>
    /// <para><see cref="DeviceProxy"/> and <see cref="DeviceSessionHost{TSession}"/> absorb that
    /// with their reopen and readiness loops. A hand-rolled subscription will not, and
    /// will hold a handle across a stop it never hears about.</para>
    /// <para><b>A throwing handler is isolated and cannot break the others.</b>
    /// Subscribers are invoked one at a time, the remaining ones still run, and the
    /// exception never unwinds the platform notification pump that raised it
    /// (issue #143). The fault is recorded twice over: at
    /// <see cref="Microsoft.Extensions.Logging.LogLevel.Error"/>, naming the device
    /// and the handler that failed, and on the
    /// <c>periphery.events.handler_faults</c> meter, which no logging configuration
    /// can filter away. The watcher will not crash the process on a handler's
    /// behalf, so handle errors inside the handler.</para>
    /// </remarks>
    public event EventHandler<DeviceChangeEventArgs>? Activated;

    /// <summary>
    /// Raised when a matching device becomes physically inactive
    /// (driver stopped, hardware disconnected). Also fires as a
    /// cascade when an active device <see cref="Disappeared">disappears</see>.
    /// </summary>
    /// <remarks>
    /// <para><b>A throwing handler is isolated and cannot break the others.</b>
    /// Subscribers are invoked one at a time, the remaining ones still run, and the
    /// exception never unwinds the platform notification pump that raised it
    /// (issue #143). The fault is recorded twice over: at
    /// <see cref="Microsoft.Extensions.Logging.LogLevel.Error"/>, naming the device
    /// and the handler that failed, and on the
    /// <c>periphery.events.handler_faults</c> meter, which no logging configuration
    /// can filter away. The watcher will not crash the process on a handler's
    /// behalf, so handle errors inside the handler.</para>
    /// </remarks>
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
    /// <para><b>A throwing handler is isolated and cannot break the others.</b>
    /// Subscribers are invoked one at a time, the remaining ones still run, and the
    /// exception never unwinds the platform notification pump that raised it
    /// (issue #143). The fault is recorded twice over: at
    /// <see cref="Microsoft.Extensions.Logging.LogLevel.Error"/>, naming the device
    /// and the handler that failed, and on the
    /// <c>periphery.events.handler_faults</c> meter, which no logging configuration
    /// can filter away. The watcher will not crash the process on a handler's
    /// behalf, so handle errors inside the handler.</para>
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
                    // Open before the provider goes live, so every live edge that races
                    // the walk below is recorded (ADR-0087 D2). Closed in the finally so
                    // a failed start does not leave the window latched open for the
                    // retry, which would make the retry walk skip real devices.
                    OpenSnapshotWindow();

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
                finally
                {
                    CloseSnapshotWindow();
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

        await foreach (var enumerated in queryProvider.EnumerateAsync(queryFilter, ct).ConfigureAwait(false))
        {
            // Reconcile the walk's payload too (ADR-0087 D1), not just the live path.
            //
            // The walk raises Appeared directly rather than through OnProviderAppeared, so
            // it bypassed the reconciliation. That was hidden while activity edges also
            // populated the supersession set - the walk simply skipped an activated device.
            // Once that was narrowed to presence edges only, and rightly so, nothing was
            // left covering a walk payload captured before the live stream activated the
            // device: it would publish IsActive == false for a device already running and
            // demote it.
            //
            // Presence supersession and activity reconciliation each cover one axis, and
            // both have to apply here for the pair to be complete.
            var device = ReconcileActivity(enumerated);

            // The live stream has already spoken for this id, and its verdict is newer
            // than the payload this walk has been carrying since it enumerated (ADR-0087
            // D2). Republishing ours would overwrite fresher truth with older truth.
            // Skipping it also drops the duplicate Appeared the Activated guard below
            // has always avoided on its own axis.
            //
            // The cache is deliberately not written here: the live handler already wrote
            // a fresher payload for this id, and the walk one is the stale one. That does
            // mean the cache keeps the notification-path payload rather than the richer
            // enumeration one for these devices - a known and separate gap (#177).
            bool supersededByLiveStream;
            lock (_liveStreamHandledIds)
                supersededByLiveStream = _liveStreamHandledIds.Contains(device.Id);

            if (supersededByLiveStream)
            {
                _logger.LogDebug(
                    "Snapshot skipped, the live stream already reported it: {DeviceId} ({DeviceName})",
                    device.Id, device.Name ?? "(unnamed)");
                continue;
            }

            // Seed the replay cache BEFORE anything is raised, and for every snapshot
            // device rather than only those the watcher-level filter admits.
            //
            // Unfiltered because the tracker fan-out below is: a tracker can match a device
            // this watcher's filter rejects, and Reconfigure replays from this cache, so
            // caching only the filtered subset silently drops devices the tracker holds.
            // Mirrors OnProviderPropertyChanged, which already caches unconditionally.
            //
            // Before, because a consumer can call Reconfigure from inside its Appeared
            // handler. The raise is synchronous, so a cache write afterwards is too late -
            // the replay would run against a cache that does not yet contain the device the
            // handler was just told about.
            lock (_deviceCache) _deviceCache[device.Id] = device;

            // Global events: apply watcher-level filter
            bool announced = MatchesIsolated(device, nameof(Appeared));
            if (announced)
            {
                snapshotCount++;
                Interlocked.Increment(ref _appearedEventCount);
                _logger.LogDebug("Snapshot appeared (#{Count}): {DeviceId} ({DeviceName})",
                    snapshotCount, device.Id, device.Name ?? "(unnamed)");

                RaiseIsolated(Appeared, new DeviceChangeEventArgs(device), nameof(Appeared), device.Id);
            }

            // The raise above ran consumer code (#201). If the live stream spoke for this
            // id while it ran, its verdict is newer than this payload, exactly as it would
            // have been had it spoken before the check at the top of this loop (D2): stop
            // here rather than announce activity for a device that may just have left,
            // and leave its id out of _knownConnectedIds. If the live stream activated the
            // device instead, the payload is reconciled against that before anything else
            // sees it.
            lock (_liveStreamHandledIds)
                supersededByLiveStream = _liveStreamHandledIds.Contains(device.Id);

            if (supersededByLiveStream)
            {
                _logger.LogDebug(
                    "Snapshot fan-out skipped, the live stream reported it during the raise: {DeviceId} ({DeviceName})",
                    device.Id, device.Name ?? "(unnamed)");
                continue;
            }

            device = ReconcileActivity(device);

            if (announced && device.IsActive)
            {
                // Guard on the Add, exactly as OnProviderActivated does. The provider
                // goes live before the snapshot walk begins, so a device that arrived
                // during the walk has already had Activated raised; without this the
                // snapshot raises it a second time.
                bool isNew;
                lock (_knownConnectedIds)
                    isNew = _knownConnectedIds.Add(device.Id);

                if (isNew)
                {
                    Interlocked.Increment(ref _activatedEventCount);
                    RaiseIsolated(Activated, new DeviceChangeEventArgs(device), nameof(Activated), device.Id);
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
        foreach (var tracker in TrackerSnapshot())
        {
            // Isolated like every other tracker notification (#143): this hook
            // emits the Unknown -> Absent transition, so it raises StateChanged
            // into consumer code, and it runs inside StartAsync — an unisolated
            // throw here failed the start outright rather than the handler.
            try
            {
                tracker.OnInitialEnumerationComplete();
            }
            catch (Exception ex)
            {
                LogFanOutFaulted(ex, "InitialEnumerationComplete", "tracker", tracker.Name, "(none)");
            }
        }
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
            // Isolated (#143): Unbind notifies subscribers, so it runs consumer
            // code on the disposal path. An unisolated throw would abandon the
            // remaining trackers still bound and leave the watcher half-disposed.
            // Take the registrations and empty the lists under the lock, then
            // unbind outside it: Unbind notifies subscribers, so holding the lock
            // across it would run consumer code inside a lock the fan-out needs.
            //
            // Clearing before unbinding rather than after is deliberate. Unbind
            // drops the binding under the tracker's own lock and only then
            // notifies, so by the time anything here can throw the tracker is
            // already unbound — retaining it would keep a reference to a detached
            // tracker on a disposed watcher, with nothing left to retry it.
            DeviceTracker[] trackers;
            MultiDeviceTracker[] groups;
            lock (_trackersLock)
            {
                trackers = [.. _trackers];
                groups = [.. _multiTrackers];
                _trackers.Clear();
                _multiTrackers.Clear();
                // Latched here, not at the end of disposal: the unbind below runs
                // consumer code, and a subscriber that registers a tracker then
                // would add it to a list this method has already walked, leaving
                // it bound forever. Registration is refused from this point.
                _trackersReleased = true;
            }

            foreach (var tracker in trackers)
            {
                try { tracker.Unbind(); }
                catch (Exception ex) { LogFanOutFaulted(ex, "Unbind", "tracker", tracker.Name, "(none)"); }
            }

            foreach (var group in groups)
            {
                try { group.Unbind(); }
                catch (Exception ex) { LogFanOutFaulted(ex, "Unbind", "group tracker", group.Name, "(none)"); }
            }

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

    // The watcher, not the provider, is the authority on activity (ADR-0087 D1). It
    // owns _knownConnectedIds and already cascades Deactivated from Disappeared, so it
    // is the one place that knows whether an Activated has been retracted.
    //
    // A presence edge can carry a payload built before the device started - the OS
    // reports "in the device tree, not yet started" as a real state, and the startup
    // walk can capture a device mid-transition. Left alone, that stale IsActive=false
    // reaches DeviceTrackerResolution, whose Resolve() reads IsActive off the shared
    // snapshot, and demotes a running device to Present with no way back (#177).
    //
    // Reconciling here rather than in the tracker covers trackers, group trackers and
    // the public Appeared event in one place. Consumers that read IsActive straight off
    // the event - readiness gates especially - are exactly the ones a tracker-layer fix
    // would have missed.
    //
    // Only ever upgrades, and only against an Activated that has not been retracted:
    // both Deactivated and Disappeared remove the id from _knownConnectedIds first.
    private DeviceInfo ReconcileActivity(DeviceInfo device)
    {
        if (device.IsActive) return device;

        bool knownActive;
        lock (_knownConnectedIds) knownActive = _knownConnectedIds.Contains(device.Id);
        if (!knownActive) return device;

        _logger.LogDebug(
            "Reconciled a stale inactive presence payload for a device held as active: {DeviceId}",
            device.Id);
        return device with { IsActive = true };
    }

    private void OnProviderAppeared(object? sender, DeviceChangeEventArgs e)
    {
        NoteLiveStreamHandled(e.Device.Id);

        var reconciled = ReconcileActivity(e.Device);
        if (!ReferenceEquals(reconciled, e.Device))
            e = new DeviceChangeEventArgs(reconciled);

        // Arrival maintains the replay cache (ADR-0087 D3). Without this a device that
        // arrived live is invisible to ReplayKnownDevicesTo, so a Reconfigure erases it.
        lock (_deviceCache) _deviceCache[e.Device.Id] = e.Device;

        if (MatchesIsolated(e.Device, nameof(Appeared)))
        {
            Interlocked.Increment(ref _appearedEventCount);
            _logger.LogDebug("Device appeared (event #{Count}): {DeviceId} ({DeviceName})",
                _appearedEventCount, e.Device.Id, e.Device.Name ?? "(unnamed)");
            RaiseIsolated(Appeared, e, nameof(Appeared), e.Device.Id);
        }
        else
        {
            _logger.LogTrace("Device appeared but filtered out: {DeviceId} ({DeviceName})",
                e.Device.Id, e.Device.Name ?? "(unnamed)");
        }

        // The raise above ran consumer code, and a concurrent edge for this id may have
        // landed while it ran (#201). The trackers get the freshest verdict this watcher
        // holds, not the one captured at the top of the handler.
        if (!TryReconcileForFanOut(e.Device, out var forTrackers)) return;

        FanOutAppeared(forTrackers);
        FanOutGroupAppeared(forTrackers);
    }

    // Re-checks both axes for a presence payload that is about to be fanned out, after
    // the public raise has released the thread to consumer code. Presence: a Disappeared
    // that landed in between pruned the replay cache (ADR-0087 D3), and its own fan-out
    // found nothing to remove, so announcing the device now would resurrect it in every
    // tracker. Activity: an Activated that landed in between put the id in
    // _knownConnectedIds, and fanning out the payload reconciled before it would demote
    // the tracker it just activated, after which the dedup guard in OnProviderActivated
    // blocks any recovery. Neither check is an ordering guarantee (#201 is); both remove
    // a permanent outcome.
    private bool TryReconcileForFanOut(DeviceInfo device, out DeviceInfo reconciled)
    {
        bool stillPresent;
        lock (_deviceCache) stillPresent = _deviceCache.ContainsKey(device.Id);
        if (!stillPresent)
        {
            _logger.LogDebug(
                "Fan-out skipped, the device left during its own Appeared raise: {DeviceId} ({DeviceName})",
                device.Id, device.Name ?? "(unnamed)");
            reconciled = device;
            return false;
        }

        reconciled = ReconcileActivity(device);
        return true;
    }

    private void OnProviderActivated(object? sender, DeviceChangeEventArgs e)
    {
        // An Activated edge whose own payload says the device is not active is not
        // evidence of activity (#202), and is treated as no edge at all: nothing below
        // runs, including the replay-cache write, which would otherwise hand a later
        // Reconfigure an inactive snapshot for a device this handler has just declined
        // to call inactive. Two provider raise sites do not gate on the flag, and on
        // Windows the flag comes from a status read that reports false when the read
        // fails. The pure core already refuses to resolve such a payload as Active
        // (ADR-0087, rejected Option 1, says why that fail-safe stays). What must not
        // happen here is the id entering _knownConnectedIds: the dedup guard below would
        // then swallow the genuine activation when it arrives, and on Windows nothing
        // else ever repairs it (ADR-0054).
        if (!e.Device.IsActive)
        {
            _logger.LogDebug(
                "Activated edge carried an inactive payload, not recorded as an activation: {DeviceId} ({DeviceName})",
                e.Device.Id, e.Device.Name ?? "(unnamed)");
            return;
        }

        // Activation maintains the replay cache (ADR-0087 D3). Written before the
        // dedup returns, so a re-raise for an already-known device still refreshes it.
        lock (_deviceCache) _deviceCache[e.Device.Id] = e.Device;

        // _knownConnectedIds.Add returns false when the ID is already present, so a
        // device that is already known-active does not re-raise Activated. The Windows
        // case this was written for (a DEVICEINTERFACEARRIVAL and a
        // DEVICEINSTANCESTARTED both firing for one hard plug-in) no longer exists —
        // issue #177 removed the interface registration — but the guard still holds
        // for providers that can report activation more than once.
        bool isNew;
        lock (_knownConnectedIds)
            isNew = _knownConnectedIds.Add(e.Device.Id);

        if (!isNew) return;

        if (MatchesIsolated(e.Device, nameof(Activated)))
        {
            Interlocked.Increment(ref _activatedEventCount);
            _logger.LogDebug("Device activated (event #{Count}): {DeviceId} ({DeviceName})",
                _activatedEventCount, e.Device.Id, e.Device.Name ?? "(unnamed)");
            RaiseIsolated(Activated, e, nameof(Activated), e.Device.Id);
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

        // Retracts the activity assertion ReconcileActivity reads, so a presence edge
        // arriving after this one is no longer upgraded.
        lock (_knownConnectedIds) _knownConnectedIds.Remove(e.Device.Id);

        lock (_deviceCache) _deviceCache[e.Device.Id] = e.Device;

        if (MatchesIsolated(e.Device, nameof(Deactivated)))
        {
            Interlocked.Increment(ref _deactivatedEventCount);
            _logger.LogDebug("Device deactivated (event #{Count}): {DeviceId} ({DeviceName})",
                _deactivatedEventCount, e.Device.Id, e.Device.Name ?? "(unnamed)");
            RaiseIsolated(Deactivated, e, nameof(Deactivated), e.Device.Id);
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
        NoteLiveStreamHandled(e.Device.Id);

        // Removal prunes the replay cache (ADR-0087 D3). Without this a Reconfigure
        // replays a device the tracker had already correctly dropped, resurrecting it.
        lock (_deviceCache) _deviceCache.Remove(e.Device.Id);

        // If this device was active, cascade a Deactivated event first.
        // The Remove must be atomic with the check to avoid double-cascades
        // when a Deactivated + Disappeared arrive on concurrent threads.
        bool wasConnected;
        lock (_knownConnectedIds) wasConnected = _knownConnectedIds.Remove(e.Device.Id);
        if (wasConnected)
        {
            if (MatchesIsolated(e.Device, nameof(Deactivated)))
            {
                Interlocked.Increment(ref _deactivatedEventCount);
                _logger.LogDebug("Device deactivated (cascade from disappeared): {DeviceId} ({DeviceName})",
                    e.Device.Id, e.Device.Name ?? "(unnamed)");
                RaiseIsolated(Deactivated, e, nameof(Deactivated), e.Device.Id);
            }

            FanOutDeactivated(e.Device);
            FanOutGroupDeactivated(e.Device);
        }

        if (MatchesIsolated(e.Device, nameof(Disappeared)))
        {
            Interlocked.Increment(ref _disappearedEventCount);
            _logger.LogDebug("Device disappeared (event #{Count}): {DeviceId} ({DeviceName})",
                _disappearedEventCount, e.Device.Id, e.Device.Name ?? "(unnamed)");
            RaiseIsolated(Disappeared, e, nameof(Disappeared), e.Device.Id);
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

        if (MatchesIsolated(e.Current, nameof(PropertyChanged)))
            RaiseIsolated(PropertyChanged, args, nameof(PropertyChanged), e.Current.Id);

        FanOutPropertyChanged(e.Previous, e.Current, changedProperties);
        FanOutGroupPropertyChanged(e.Previous, e.Current, changedProperties);
    }

    // ── Internal — filter isolation ─────────────────────────────────────

    /// <summary>
    /// Evaluates the watcher's filter against a device without letting a
    /// caller-supplied predicate escape into the provider's notification pump
    /// (issue #229).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="DeviceFilter.Where(Func{DeviceInfo, bool})"/> stores consumer
    /// delegates and <c>Matches</c> invokes them. Those calls sat outside the
    /// isolation #143 put around every raise and every notification, so a
    /// predicate throwing on one odd device — a null <see cref="DeviceInfo.Name"/>
    /// is enough — unwound the pump and froze the application's whole device view.
    /// The same defect, at the one site that fix did not cover.
    /// </para>
    /// <para>
    /// <b>The substituted answer is directional, and that is the design.</b> On an
    /// arrival the fallback is "no match": a device is not announced on the
    /// strength of a predicate that could not answer. On a removal it is "match":
    /// suppressing a <see cref="Disappeared"/> or <see cref="Deactivated"/>
    /// because the predicate threw would leave a device the consumer was already
    /// told about present forever, with no later edge to correct it. That is the
    /// leak ADR-0084 D1 refused to accept from watcher tag filters — fires
    /// <see cref="Appeared"/>, never fires <see cref="Disappeared"/> — and a
    /// predicate that throws for some devices and not others produces it directly.
    /// A spurious removal for a device nobody tracked is the cheaper error.
    /// </para>
    /// <para>
    /// The direction comes from the event rather than from each call site, so no
    /// site can pick the wrong one, and every fault is counted on
    /// <see cref="PeripheryDiagnostics.FilterFaults"/> tagged with the answer used.
    /// <see cref="KnownDevices"/> deliberately does not use this: a caller is
    /// awaiting that property, so a broken predicate is theirs to see.
    /// </para>
    /// </remarks>
    private bool MatchesIsolated(DeviceInfo device, string eventName)
    {
        try
        {
            return _filter.Matches(device);
        }
        catch (Exception ex)
        {
            bool announced = AnnouncesOnFilterFault(eventName);
            EventIsolation.LogFilterFaulted(_logger, ex, eventName, device.Id, announced);
            return announced;
        }
    }

    /// <summary>
    /// <see cref="MatchesIsolated(DeviceInfo, string)"/> for a tracker's own
    /// profiles, which carry caller predicates for the same reason.
    /// </summary>
    private bool MatchesIsolated(DeviceTracker tracker, DeviceInfo device, string eventName)
    {
        try
        {
            return tracker.Matches(device);
        }
        catch (Exception ex)
        {
            bool announced = AnnouncesOnFilterFault(eventName);
            EventIsolation.LogFilterFaulted(_logger, ex, eventName, device.Id, announced);
            return announced;
        }
    }

    /// <summary>
    /// Whether a filter that threw counts as a match for
    /// <paramref name="eventName"/>: true for the edges that take a device away,
    /// false for the rest.
    /// </summary>
    private static bool AnnouncesOnFilterFault(string eventName) =>
        eventName is nameof(Disappeared) or nameof(Deactivated);

    // ── Internal — isolated dispatch ────────────────────────────────────
    //
    // Nothing below lets one subscriber's exception escape (issue #143). Before
    // this, the watcher contained no catch on any raise path, so a throwing
    // handler unwound whichever thread raised the event — on the live path that
    // is the platform provider's notification pump, and every tracker in the
    // process reports through it. One bad handler therefore left the application
    // silently blind with its trackers frozen at their last reading, the same end
    // state as an unstartable watcher (#140) reached by a different route and with
    // nothing surfacing where a caller could see it. It was also order-dependent:
    // a multicast delegate walk stops at the first throw, so which subscribers
    // survived depended on registration order.
    //
    // ADR-0084 D6 already made the post-commit snapshot drain isolate per handler
    // for its own reasons; this extends the same semantics to every raise and to
    // the tracker fan-out, so the watcher has one dispatch rule rather than two.

    /// <summary>
    /// Raises an event one subscriber at a time so a throwing subscriber neither
    /// unwinds the caller nor suppresses the subscribers behind it.
    /// </summary>
    /// <remarks>
    /// The delegate list is walked explicitly rather than invoked as a multicast
    /// delegate, because a multicast invoke abandons the walk at the first throw.
    /// Each fault is logged at Error against the device, the event and the
    /// handler: a swallowed exception is a bug a consumer can no longer see by
    /// crashing, so the record has to be enough to find it.
    /// </remarks>
    private void RaiseIsolated<TArgs>(
        EventHandler<TArgs>? handlers, TArgs args, string eventName, string deviceId)
        where TArgs : EventArgs
        => EventIsolation.Raise(this, handlers, args, _logger, eventName, deviceId);

    private static void LogFanOutFaulted(Exception ex, string eventName, string kind, string? name, string deviceId)
        => EventIsolation.LogTargetFaulted(_logger, ex, eventName, kind, name, deviceId);

    /// <summary>
    /// A stable copy of the trackers to notify.
    /// </summary>
    /// <remarks>
    /// Snapshotted rather than enumerated live because a notification runs
    /// consumer code, and that code may dispose the watcher — which clears these
    /// lists. Enumerating the live <see cref="List{T}"/> would then throw from
    /// <c>MoveNext</c>, outside the per-target try/catch, and unwind the pump
    /// exactly as an unisolated handler used to. Handler isolation makes this
    /// more reachable rather than less: a handler that disposes now keeps running
    /// where before its own throw would have ended the walk.
    /// </remarks>
    private DeviceTracker[] TrackerSnapshot()
    {
        lock (_trackersLock) return [.. _trackers];
    }

    private MultiDeviceTracker[] GroupSnapshot()
    {
        lock (_trackersLock) return [.. _multiTrackers];
    }

    // ── Internal — tracker fan-out ─────────────────────────────────────

    private void FanOutAppeared(DeviceInfo device) =>
        FanOutToTrackers(device, static (t, d) => t.OnDeviceAppeared(d), nameof(Appeared));

    private void FanOutActivated(DeviceInfo device) =>
        FanOutToTrackers(device, static (t, d) => t.OnDeviceConnected(d), nameof(Activated));

    private void FanOutDeactivated(DeviceInfo device) =>
        FanOutToTrackers(device, static (t, d) => t.OnDeviceDisconnected(d), nameof(Deactivated));

    private void FanOutDisappeared(DeviceInfo device) =>
        FanOutToTrackers(device, static (t, d) => t.OnDeviceDisappeared(d), nameof(Disappeared));

    /// <summary>
    /// Notifies every tracker the device matches, isolating each one.
    /// </summary>
    /// <remarks>
    /// A tracker's notification runs consumer code of its own — a tracker raises
    /// <c>StateChanged</c>, which is what drives every <c>DeviceProxyBase</c> in
    /// the process — so a throw here is as reachable as one from a watcher-level
    /// handler, and left unisolated it would stop the trackers behind it from
    /// ever hearing about the device.
    /// </remarks>
    private void FanOutToTrackers(DeviceInfo device, Action<DeviceTracker, DeviceInfo> notify, string eventName)
    {
        foreach (var tracker in TrackerSnapshot())
        {
            if (!MatchesIsolated(tracker, device, eventName))
                continue;

            try
            {
                notify(tracker, device);
            }
            catch (Exception ex)
            {
                LogFanOutFaulted(ex, eventName, "tracker", tracker.Name, device.Id);
            }
        }
    }

    private void FanOutPropertyChanged(DeviceInfo previous, DeviceInfo current, IReadOnlySet<string> changedProperties)
    {
        foreach (var tracker in TrackerSnapshot())
        {
            try
            {
                tracker.OnDevicePropertyChanged(previous, current, changedProperties);
            }
            catch (Exception ex)
            {
                LogFanOutFaulted(ex, nameof(PropertyChanged), "tracker", tracker.Name, current.Id);
            }
        }
    }

    // ── Internal — group tracker fan-out ────────────────────────────────

    private void FanOutGroupAppeared(DeviceInfo device) =>
        FanOutToGroups(device, static (g, d) => g.OnDeviceAppeared(d), nameof(Appeared));

    private void FanOutGroupActivated(DeviceInfo device) =>
        FanOutToGroups(device, static (g, d) => g.OnDeviceActivated(d), nameof(Activated));

    private void FanOutGroupDeactivated(DeviceInfo device) =>
        FanOutToGroups(device, static (g, d) => g.OnDeviceDeactivated(d), nameof(Deactivated));

    private void FanOutGroupDisappeared(DeviceInfo device) =>
        FanOutToGroups(device, static (g, d) => g.OnDeviceDisappeared(d), nameof(Disappeared));

    private void FanOutToGroups(DeviceInfo device, Action<MultiDeviceTracker, DeviceInfo> notify, string eventName)
    {
        foreach (var group in GroupSnapshot())
        {
            try
            {
                notify(group, device);
            }
            catch (Exception ex)
            {
                LogFanOutFaulted(ex, eventName, "group tracker", group.Name, device.Id);
            }
        }
    }

    private void FanOutGroupPropertyChanged(DeviceInfo previous, DeviceInfo current, IReadOnlySet<string> changedProperties)
    {
        foreach (var group in GroupSnapshot())
        {
            try
            {
                group.OnDevicePropertyChanged(previous, current, changedProperties);
            }
            catch (Exception ex)
            {
                LogFanOutFaulted(ex, nameof(PropertyChanged), "group tracker", group.Name, current.Id);
            }
        }
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
