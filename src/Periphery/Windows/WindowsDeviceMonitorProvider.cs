// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Periphery.Windows;

/// <summary>
/// Windows implementation of <see cref="IDeviceMonitorProvider"/> using cfgmgr32
/// <c>CM_Register_Notification</c> for kernel-level device notifications. The
/// provider does no whole-tree polling (ADR-0054); its lifecycle transitions are
/// event-driven from cfgmgr32, plus one targeted OS push for monitor DisplayConfig
/// freshness (ADR-0066, see below).
/// <para>One notification registration is active after <see cref="StartAsync"/> — the
/// instance filter (<c>CM_NOTIFY_FILTER_TYPE_DEVICEINSTANCE</c> with
/// <c>CM_NOTIFY_FILTER_FLAG_ALL_DEVICE_INSTANCES</c>), which covers every devnode:</para>
/// <list type="bullet">
/// <item><c>DEVICEINSTANCEENUMERATED</c> → <see cref="DeviceAppeared"/>.</item>
/// <item><c>DEVICEINSTANCESTARTED</c> → <see cref="DeviceActivated"/>
///   (preceded by <see cref="DeviceAppeared"/> if the devnode was not already known).</item>
/// <item><c>DEVICEINSTANCEREMOVED</c> → <see cref="DeviceDisappeared"/>.</item>
/// </list>
/// <para><b>Why presence comes from the instance filter (issue #177).</b> There was
/// previously a second registration on <c>CM_NOTIFY_FILTER_TYPE_DEVICEINTERFACE</c>
/// that sourced <see cref="DeviceAppeared"/> from interface arrival. It was registered
/// with <c>ClassGuid = Guid.Empty</c> and no <c>ALL_INTERFACE_CLASSES</c> flag, i.e. for
/// <c>GUID_NULL</c>, which no interface belongs to — so it succeeded and then delivered
/// nothing, and <see cref="DeviceAppeared"/> only ever fired from the
/// <see cref="DeviceWatcher"/> startup snapshot. Setting the flag was measured and is
/// not the fix: a devnode publishes zero or many interfaces, so interface arrival is
/// not a faithful proxy for "a devnode entered the tree" (a USB drive's
/// <c>STORAGE\Volume</c> and <c>SWD\WPDBUSENUM</c> nodes publish none at arrival and
/// still got nothing). Presence is a devnode fact, so it is sourced from the devnode
/// stream, which also gives single-source removal, and makes
/// enumerated-before-started the order cfgmgr32 actually delivered in every plug
/// observed. That ordering is not enforced here: events are raised outside
/// <c>_cacheLock</c>, so concurrent delivery of actions 7 and 8 for one devnode could
/// still surface <see cref="DeviceActivated"/> first. What the cache does guarantee is
/// that exactly one of the two raises <see cref="DeviceAppeared"/>.</para>
/// <para><b>Monitor DisplayConfig freshness (ADR-0066).</b> Beyond that
/// registration, a <see cref="WindowsDisplayChangeSink"/> runs a hidden-window
/// message pump on a dedicated background thread (not polling) that observes
/// <c>WM_DISPLAYCHANGE</c>. On a display change — and on a monitor devnode arrival
/// (which coalesces into the same refresh) — the provider re-runs the DisplayConfig
/// enricher over its cached Monitor-category snapshots and raises
/// <see cref="DevicePropertyChanged"/> with the enriched delta. This is the
/// "targeted, event-driven add for a specific property" ADR-0054 Decision 3
/// explicitly permits; it does not reinstate any tree scan. Monitor arrival/
/// re-appearance also merges the DisplayConfig tier forward from cache so the
/// appeared/activated payload is never a bare clobber (issue #149).</para>
/// <para>cfgmgr32 has no soft driver-stop signal, so soft
/// <see cref="DeviceDeactivated"/> is still not raised on Windows, and
/// <see cref="DevicePropertyChanged"/> fires only for the monitor DisplayConfig
/// tier — no generic property-drift detection. A consumer needing another
/// property's freshness wires that property's own OS signal or polls the single
/// device it cares about. The Linux and macOS providers deliver both from native
/// OS push.</para>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsDeviceMonitorProvider : IDeviceMonitorProvider
{
    private static readonly ILogger<WindowsDeviceMonitorProvider> _logger =
        PeripheryLoggerFactory.CreateLogger<WindowsDeviceMonitorProvider>();

    private GCHandle _selfHandle;
    private DevNodeHelper.CmNotifyHandle? _instanceNotifyHandle;
    private int _started; // 0 = unstarted, 1 = started (Interlocked)

    // Last-known DeviceInfo per instance id. Populated by the enumerated/start
    // notification callbacks and the StartAsync seed; consumed by the removal
    // callback. Post ADR-0054 (the whole-tree property scan is gone) it has three
    // jobs, the last two shared with the Linux/macOS providers:
    //   1. Answer "have we announced this devnode yet?". DEVICEINSTANCEENUMERATED
    //      also fires on re-enumeration (driver reload, sleep-resume) for a devnode
    //      already known, so membership here is what distinguishes a genuine arrival
    //      from a re-enumeration and keeps DeviceAppeared single-fire (issue #177).
    //   2. Supply the last-known DeviceInfo (VID/PID/category/name) on removal —
    //      the devnode is gone by then, so TryBuildDeviceInfo would only yield an
    //      id-only stub that the watcher/tracker filters reject.
    //   3. Hold the DisplayConfig-enriched monitor snapshot the WM_DISPLAYCHANGE
    //      refresh diffs against (ADR-0066).
    private readonly object _cacheLock = new();
    // Keyed case-insensitively: the snapshot/query path and the change-notification
    // path can report the same instance id in different case (see DeviceId), and
    // every DeviceId-keyed map downstream is OrdinalIgnoreCase. A case-sensitive
    // cache here would split one monitor into two entries and emit duplicate /
    // phantom DevicePropertyChanged events on refresh.
    // Keyed by DeviceId rather than string + an explicit StringComparer.OrdinalIgnoreCase:
    // identical semantics, but the invariant lives in the key type. This is the provider
    // where the casing flip is REAL (#231 was observed on Windows instance ids), and it is
    // the one that held the invariant in a comparer argument the other two providers did
    // not copy — which is the argument for moving it into the key type.
    private readonly Dictionary<DeviceId, DeviceInfo> _lastKnownDevices = new();

    // Orders monitor appearance events against the display-change refresh.
    //
    // The hazard (issue #149): an arrival publishes a bare monitor to the cache,
    // and before it raises DeviceAppeared the pump thread (already refreshing from
    // the plug's own WM_DISPLAYCHANGE) snapshots that bare entry, enriches it,
    // writes the enriched value back, and raises DevicePropertyChanged. The tracker
    // drops that event — the device isn't resolved yet — and the arrival's follow-up
    // RequestRefresh then diffs the *already-enriched* cache to nothing, so the
    // enrichment is never re-emitted and the monitor stays bare.
    //
    // The precondition that actually has to hold is "a refresh delta is only raised
    // for a monitor whose appearance has already been raised", so it is recorded as
    // data rather than enforced by holding a lock across the raising (issue #153;
    // ADR-0066 Decision 2a). A monitor mid-publish, or never announced, is skipped
    // by the refresh — neither written back nor raised — and every publish requests
    // a refresh once its events are out, so a skipped monitor is re-driven.
    // Guarded by _cacheLock (the ledger has no lock of its own): the eligibility
    // answer and the cache write it authorises must be one atomic step.
    private readonly MonitorAnnouncementLedger _monitorAnnouncements = new();

    // Hidden-window sink for WM_DISPLAYCHANGE — the OS push signal that lets us
    // re-stamp DisplayConfig fields on Monitor-category devices after a hotplug
    // or a mode change the per-enumeration enrichment path never revisits (#149).
    private WindowsDisplayChangeSink? _displayChangeSink;

    public event EventHandler<DeviceChangeEventArgs>? DeviceAppeared;
    public event EventHandler<DeviceChangeEventArgs>? DeviceDisappeared;
    public event EventHandler<DeviceChangeEventArgs>? DeviceActivated;

    // DeviceDeactivated is part of the IDeviceMonitorProvider contract but is
    // intentionally never raised on Windows after ADR-0054: cfgmgr32 pushes no
    // soft driver-stop signal, and Periphery no longer synthesizes one with a
    // whole-tree poll. It fires from genuine OS push on Linux (udev unbind) and
    // macOS (IOKit). CS0067 (event is never raised) is expected here.
#pragma warning disable CS0067
    public event EventHandler<DeviceChangeEventArgs>? DeviceDeactivated;
#pragma warning restore CS0067

    // DevicePropertyChanged IS raised on Windows for Monitor-category devices:
    // the WM_DISPLAYCHANGE sink (below) re-runs the DisplayConfig enricher and
    // emits the (previous -> enriched) delta so the tracker re-stamps
    // MonitorName / DisplayResolution / DisplayBounds after a hotplug or a mode
    // change the per-enumeration enrichment path never revisits (issue #149).
    // cfgmgr32 still delivers no signal for other properties, so this fires only
    // for the DisplayConfig tier.
    public event EventHandler<DeviceModificationEventArgs>? DevicePropertyChanged;

    public unsafe Task StartAsync(DeviceFilter filter, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
            throw new InvalidOperationException(
                "StartAsync has already been called. Dispose and create a new monitor to restart.");

        _logger.LogInformation("Starting device monitor via CM_Register_Notification");

        try
        {
            _selfHandle = GCHandle.Alloc(this);

            // Start the WM_DISPLAYCHANGE sink BEFORE registering for cfgmgr32
            // notifications: an arrival delivered between registration and sink
            // startup would find _displayChangeSink still null and silently drop its
            // RequestRefresh, leaving that monitor unenriched until some later
            // display change (issue #149). Best-effort — if the hidden window can't
            // be created the sink logs and no-ops, and the provider degrades to its
            // pre-#149 (no display refresh) behaviour rather than failing the
            // whole watcher. A refresh that fires before the cache is seeded simply
            // finds no monitors and does nothing.
            _displayChangeSink = new WindowsDisplayChangeSink(OnDisplayConfigChanged);
            _displayChangeSink.Start();

            // Seed the last-known-device cache with the current device snapshot BEFORE
            // registering, so the cache is complete the moment notifications can arrive.
            // This is the Windows analogue of the Linux/macOS SeedCache.
            //
            // Ordering matters since the cache became the "have we announced this
            // devnode?" answer (issue #177). Registering first leaves it empty for the
            // whole tree walk, and DEVICEINSTANCEENUMERATED also fires on re-enumeration
            // — a driver reload or a sleep-resume tail — so any devnode that re-enumerates
            // during the walk would find TryAdd succeeding and be announced as an arrival
            // it never made.
            //
            // Seeding first inverts the exposure: a device that genuinely arrives between
            // the walk finishing and the registration returning is seen by neither. That
            // window is one API call rather than a whole tree walk, and DeviceWatcher runs
            // its own enumeration after this returns, which is what tells consumers about
            // pre-existing devices in the first place. A brief miss the watcher's snapshot
            // covers beats a spurious arrival nothing corrects.
            //
            // The seed uses the plain ToDeviceInfo build — category, VID/PID, name,
            // everything the watcher/tracker filters match on — and deliberately
            // skips the enrichment pipeline that the removed scan loop ran for diff
            // stability, keeping startup cheap. TryAdd (not assignment) is kept: it is
            // now the belt to the braces, since nothing should be in the cache yet.
            lock (_cacheLock)
            {
                foreach (var (devInst, id) in DevNodeHelper.EnumerateDeviceInstances())
                {
                    try
                    {
                        var seeded = WindowsDeviceProvider.ToDeviceInfo(devInst, id);
                        // Seeded monitors count as announced: consumers learn about
                        // them from the watcher's startup snapshot (which runs the
                        // enrichment pipeline), not from a provider event, so a later
                        // mode change must be free to emit a delta for them.
                        if (_lastKnownDevices.TryAdd(id, seeded) && seeded.Category == DeviceCategory.Monitor)
                            _monitorAnnouncements.MarkAnnounced(id);
                    }
                    catch { /* skip unreadable devices */ }
                }
            }

            // Sole registration — every device instance. Carries arrival
            // (ENUMERATED), activation (STARTED) and removal (REMOVED); see the
            // class remarks for why presence is not sourced from an interface
            // filter (issue #177).
            var instanceFilter = new DevNodeHelper.CM_NOTIFY_FILTER
            {
                cbSize     = Marshal.SizeOf<DevNodeHelper.CM_NOTIFY_FILTER>(),
                Flags      = DevNodeHelper.CM_NOTIFY_FILTER_FLAG_ALL_DEVICE_INSTANCES,
                FilterType = DevNodeHelper.CM_NOTIFY_FILTER_TYPE_DEVICEINSTANCE,
            };

            int r = DevNodeHelper.CM_Register_Notification(
                ref instanceFilter,
                GCHandle.ToIntPtr(_selfHandle),
                &NotificationShim,
                out nint rawInstanceHandle);

            if (r != 0)
                throw new DeviceProviderException(
                    $"CM_Register_Notification (instance) failed with error code {r}.");

            _instanceNotifyHandle = new DevNodeHelper.CmNotifyHandle(rawInstanceHandle);

            _logger.LogInformation("Device notifications registered (events + WM_DISPLAYCHANGE display refresh).");
            return Task.CompletedTask;
        }
        catch (DeviceProviderException)
        {
            CleanupAfterFailedStart();
            throw;
        }
        catch (Exception ex)
        {
            CleanupAfterFailedStart();
            _logger.LogError(ex, "Failed to start device monitor");
            throw new DeviceProviderException($"Failed to start device monitor: {ex.Message}", ex);
        }
    }

    private void CleanupAfterFailedStart()
    {
        _displayChangeSink?.Dispose();
        _displayChangeSink = null;
        _instanceNotifyHandle?.Dispose();
        _instanceNotifyHandle = null;
        if (_selfHandle.IsAllocated)
            _selfHandle.Free();
    }

    // Display change (or monitor arrival) observed by the sink: re-run the
    // DisplayConfig enricher against the cached Monitor-category snapshots and
    // raise DevicePropertyChanged for any whose enriched fields changed. Runs on
    // the sink's pump thread (see WindowsDisplayChangeSink) — off the cfgmgr32
    // [UnmanagedCallersOnly] callback — and, for a display change, only after the
    // topology has settled, so QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS) reflects
    // the new panel/mode.
    private void OnDisplayConfigChanged()
    {
        // Snapshot the monitor entries under the lock, then release it before any
        // IO. Build() and Enrich() call QueryDisplayConfig and can read the EDID
        // registry, so holding _cacheLock across them would stall the cfgmgr32
        // notification callbacks that contend on it.
        List<DeviceInfo> monitors;
        lock (_cacheLock)
        {
            monitors = new List<DeviceInfo>();
            foreach (var d in _lastKnownDevices.Values)
                if (d.Category == DeviceCategory.Monitor && _monitorAnnouncements.IsRefreshEligible(d.Id))
                    monitors.Add(d);
        }
        if (monitors.Count == 0) return;

        WindowsDisplayConfigEnricher enricher;
        try
        {
            enricher = WindowsDisplayConfigEnricher.Build();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DisplayConfig enricher build failed on display change; skipping monitor refresh.");
            return;
        }

        var deltas = WindowsMonitorEnrichment.ComputeDeltas(monitors, enricher.Enrich);
        if (deltas.Count == 0) return;

        // Re-take the cache lock only to write back, and only for entries still
        // present, unchanged since the snapshot, and still refresh-eligible — a
        // concurrent arrival/removal wins, so we never resurrect a removed monitor,
        // clobber a newer snapshot, or enrich a monitor whose appearance is still
        // in flight (that one is left untouched for its publish's own trailing
        // RequestRefresh to re-drive).
        var toRaise = new List<(DeviceInfo Previous, DeviceInfo Current)>();
        lock (_cacheLock)
        {
            foreach (var (previous, current) in deltas)
            {
                if (_lastKnownDevices.TryGetValue(previous.Id, out var live)
                    && ReferenceEquals(live, previous)
                    && _monitorAnnouncements.IsRefreshEligible(previous.Id))
                {
                    _lastKnownDevices[current.Id] = current;
                    toRaise.Add((previous, current));
                }
            }
        }

        // Raised with NO lock held. The fan-out is synchronous into consumer code
        // (DeviceWatcher -> DeviceTracker -> StateChanged / observers), and a handler
        // that applies a display layout makes Windows broadcast WM_DISPLAYCHANGE by
        // SendMessage to this sink's own window — which only this thread services.
        // Holding a provider lock across that let a consumer handler on another
        // thread stall the broadcast (issue #153).
        foreach (var (previous, current) in toRaise)
        {
            _logger.LogDebug("Monitor refreshed on display change: {DeviceId} ({MonitorName})",
                current.Id, current.MonitorName ?? "(unnamed)");
            DevicePropertyChanged?.Invoke(this, new DeviceModificationEventArgs(previous, current));
        }
    }

    // Publishes a monitor payload to the last-known cache (merging DisplayConfig
    // enrichment forward from any prior snapshot) and raises its appearance
    // events. The publish is registered with the announcement ledger for its whole
    // duration so a concurrent display-change refresh skips this monitor rather
    // than emitting a DevicePropertyChanged the tracker would drop (issue #149) —
    // and the events themselves are raised with no lock held (issue #153).
    // Runs the DisplayConfig enricher over a monitor payload. The notification path
    // builds via TryBuildDeviceInfo, which does NOT run this enricher (only
    // WindowsDeviceProvider.EnumerateAsync does), so MonitorName, DisplayResolution,
    // DisplayBounds, orientation, connector and the luminance fields are all null on
    // anything built here.
    //
    // MergeArrival backfills those from a prior cache entry, which covers a
    // re-appearance. It cannot cover a FIRST appearance, because there is no prior by
    // definition — and that is precisely when Appeared is raised, so without this the
    // one Appeared a consumer gets for a newly plugged monitor carries none of the
    // fields a monitor consumer filters on (issue #177).
    //
    // Build() calls QueryDisplayConfig and can read the EDID registry, so this is real
    // IO on the cfgmgr32 callback thread. It is not new expense: the trailing
    // RequestRefresh in PublishMonitorEvents already pays for the same query on every
    // monitor appearance. This moves it before the event instead of after, which is
    // what makes the payload correct at the point a filter sees it.
    private DeviceInfo TryEnrichDisplayConfig(DeviceInfo monitor)
    {
        try
        {
            return WindowsDisplayConfigEnricher.Build().Enrich(monitor);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "DisplayConfig enrichment failed for an appearing monitor; announcing it unenriched: {DeviceId}",
                monitor.Id);
            return monitor;
        }
    }

    // Appeared is raised iff the cache did not already hold this id, decided inside
    // the same lock as the write that claims it. Both call sites want that rule, and
    // deciding it here rather than in the caller closes the check-then-act window
    // between "is it new?" and the claim.
    private void PublishMonitorEvents(DeviceInfo device, bool raiseActivated)
    {
        // Enrich unconditionally rather than only on a guessed first sighting.
        //
        // The obvious optimisation - peek at the cache, enrich only if absent - is wrong,
        // because the peek and the authoritative newness decision are separated by an
        // unlocked gap. A monitor cached at the peek and removed before the lock below
        // makes raiseAppeared true while the payload is still the unenriched notification
        // build, which is exactly the empty first Appeared this is here to prevent. The
        // enrichment has to be part of the same decision, and it cannot be: Build() does
        // IO, and holding _cacheLock across it would stall the cfgmgr32 callbacks that
        // contend on it (issue #153).
        //
        // So pay for it every time. The cost is one QueryDisplayConfig per monitor
        // publish, on a path that already runs one per monitor appearance through the
        // trailing RequestRefresh, for a device class with few instances and rare
        // arrivals. MergeArrival still fills anything the enricher could not.
        device = TryEnrichDisplayConfig(device);

        bool raiseAppeared;
        lock (_cacheLock)
        {
            raiseAppeared = !_lastKnownDevices.TryGetValue(device.Id, out var prior);
            if (!raiseAppeared)
                device = WindowsMonitorEnrichment.MergeArrival(device, prior!);
            _lastKnownDevices[device.Id] = device;
            _monitorAnnouncements.BeginPublish(device.Id);
        }

        try
        {
            if (raiseAppeared)
            {
                _logger.LogDebug("Device appeared: {DeviceId} ({DeviceName})", device.Id, device.Name ?? "(unnamed)");
                DeviceAppeared?.Invoke(this, new DeviceChangeEventArgs(device));
            }

            if (raiseActivated)
            {
                _logger.LogDebug("Device activated: {DeviceId} ({DeviceName})", device.Id, device.Name ?? "(unnamed)");
                DeviceActivated?.Invoke(this, new DeviceChangeEventArgs(device));
            }
        }
        finally
        {
            // In a finally: a throwing consumer handler must not leave the monitor
            // permanently ineligible for refresh.
            lock (_cacheLock)
                _monitorAnnouncements.EndPublish(device.Id);

            // A genuinely-new panel has no enriched prior to merge from, and any
            // refresh that ran while this publish was in flight deliberately skipped
            // it — so always poke the sink once the appearance is out. Coalesced,
            // and runs off this callback thread.
            _displayChangeSink?.RequestRefresh();
        }
    }

    // AOT-safe static callback shim. The GCHandle stored in pContext keeps
    // `this` reachable for the lifetime of the notification registration.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int NotificationShim(
        nint hNotify, nint context, int action, nint eventData, int eventDataSize)
    {
        var self = (WindowsDeviceMonitorProvider)GCHandle.FromIntPtr(context).Target!;
        return self.OnDeviceNotification(hNotify, action, eventData, eventDataSize);
    }

    private int OnDeviceNotification(nint hNotify, int action, nint eventData, int eventDataSize)
    {
        try
        {
            switch (action)
            {
                case DevNodeHelper.CM_NOTIFY_ACTION_DEVICEINSTANCESTARTED:
                    HandleInstanceStarted(eventData, eventDataSize);
                    break;

                case DevNodeHelper.CM_NOTIFY_ACTION_DEVICEINSTANCEENUMERATED:
                    HandleInstanceEnumerated(eventData, eventDataSize);
                    break;

                case DevNodeHelper.CM_NOTIFY_ACTION_DEVICEINSTANCEREMOVED:
                    HandleInstanceRemoved(eventData, eventDataSize);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error in device notification callback (action: {Action})", action);
            System.Diagnostics.Debug.WriteLine($"Error in device notification callback: {ex.Message}");
        }

        return 0; // ERROR_SUCCESS — continue receiving notifications
    }

    private void HandleInstanceStarted(nint eventData, int eventDataSize)
    {
        string? instanceId = DevNodeHelper.ReadInstanceIdFromEventData(eventData, eventDataSize);
        if (instanceId is null) return;

        DeviceInfo? device = WindowsDeviceProvider.TryBuildDeviceInfo(instanceId);
        if (device is null) return;

        // Monitors take the ordered publish path so the payload carries merged
        // enrichment and cannot be overtaken by a concurrent refresh delta. It
        // decides Appeared by cache membership, same rule as below.
        if (device.Category == DeviceCategory.Monitor)
        {
            PublishMonitorEvents(device, raiseActivated: true);
            return;
        }

        // A devnode is normally announced by DEVICEINSTANCEENUMERATED before it is
        // started, so by here it is already in the cache and only Activated is due.
        // Raise Appeared ourselves if it is not: the ADR-0004 invariant is that
        // active implies present, and it should hold on the event stream without
        // depending on cfgmgr32 always delivering action 7 first (issue #177).
        // Decided in the same lock as the write, so a concurrent callback for the
        // same devnode cannot make both sightings look like the first.
        bool firstSighting;
        lock (_cacheLock)
        {
            firstSighting = !_lastKnownDevices.ContainsKey(device.Id);
            _lastKnownDevices[device.Id] = device;
        }

        if (firstSighting)
        {
            _logger.LogDebug("Device appeared (started without a prior enumerate): {DeviceId} ({DeviceName})",
                device.Id, device.Name ?? "(unnamed)");
            DeviceAppeared?.Invoke(this, new DeviceChangeEventArgs(device));
        }

        _logger.LogDebug("Device activated (instance started): {DeviceId} ({DeviceName})", device.Id, device.Name ?? "(unnamed)");
        DeviceActivated?.Invoke(this, new DeviceChangeEventArgs(device));
    }

    // Handles CM_NOTIFY_ACTION_DEVICEINSTANCEENUMERATED (action=7): the devnode has
    // entered the device tree but has not been started yet. This is Periphery's
    // presence edge — it is raised here rather than from an interface arrival because
    // presence is a devnode fact and a devnode publishes zero or many interfaces
    // (issue #177; see the class remarks).
    //
    // Measured on a USB mass-storage plug: action 7 arrives for every devnode of the
    // device (USB, USBSTOR, STORAGE\Volume, SWD\WPDBUSENUM), immediately before action
    // 8 for the same devnode, carrying a DeviceInfo identical to action 8's across the
    // 14 fields compared — Name, Category, ClassName/ClassGuid, VID/PID, SerialNumber,
    // BusType, Status, Driver, ParentId, ContainerId, and the Properties and Tags counts
    // — apart from IsActive.
    //
    // NOT compared, so not claimed: DriveType, PortName, MacAddress, UsbSpeed,
    // BatteryStatus, the display fields, and the CONTENTS of Properties/Tags. Filters
    // matching on those may still see a thinner payload here than at action 8. The
    // measurement also used devnodes this machine had already installed; a first-ever
    // plug, where driver install has not yet written the registry, was not covered.
    //
    // The action also fires on re-enumeration of a devnode already known (driver
    // reload, sleep-resume), which is not an arrival. TryAdd is what tells the two
    // apart: it succeeds only for a devnode the cache has never held, which keeps
    // Appeared single-fire and preserves the old "don't overwrite a richer snapshot"
    // behaviour for the re-enumeration case.
    private void HandleInstanceEnumerated(nint eventData, int eventDataSize)
    {
        string? instanceId = DevNodeHelper.ReadInstanceIdFromEventData(eventData, eventDataSize);
        if (instanceId is null) return;

        DeviceInfo? device = WindowsDeviceProvider.TryBuildDeviceInfo(instanceId);
        if (device is null) return;

        // Monitors take the ordered publish path so the Appeared payload carries
        // merged DisplayConfig enrichment rather than a bare clobber (issue #149).
        // It applies the same "Appeared iff not already cached" rule internally.
        if (device.Category == DeviceCategory.Monitor)
        {
            PublishMonitorEvents(device, raiseActivated: false);
            return;
        }

        bool isNew;
        lock (_cacheLock)
            isNew = _lastKnownDevices.TryAdd(device.Id, device);

        if (!isNew)
        {
            _logger.LogDebug("Device re-enumerated (already known, no Appeared): {DeviceId} ({DeviceName})",
                device.Id, device.Name ?? "(unnamed)");
            return;
        }

        _logger.LogDebug("Device appeared (instance enumerated): {DeviceId} ({DeviceName})", device.Id, device.Name ?? "(unnamed)");
        DeviceAppeared?.Invoke(this, new DeviceChangeEventArgs(device));
    }

    private void HandleInstanceRemoved(nint eventData, int eventDataSize)
    {
        string? instanceId = DevNodeHelper.ReadInstanceIdFromEventData(eventData, eventDataSize);
        if (instanceId is null) return;

        // Stale-removal guard (ADR-0060 Decision 7). A fast disable->enable (an
        // ADR-0060 PnP reset, or a brief OS-driven restart) can deliver this DEVICEINSTANCEREMOVED
        // out of order, after the device has already re-enumerated and started -- firing
        // DeviceDisappeared then would tear down a device that is actually present. Re-check the
        // live devnode: if the instance is started right now, this removal is stale, so drop it
        // and keep tracking. Applied only to instance-removal (a real removal reads "not
        // connected" here).
        if (DevNodeHelper.IsDeviceConnected(instanceId))
        {
            _logger.LogDebug(
                "Ignoring stale instance-removed for {InstanceId}: device currently started (fast re-enable / reset re-enumeration).",
                instanceId);
            return;
        }

        // Only fire if the device is still in the cache. This is now the sole removal
        // source (issue #177 removed the interface registration, so there is no second
        // handler to race), but the guard still earns its place: a devnode the cache
        // never held was never announced, and raising Disappeared for it would be a
        // removal with no matching Appeared.
        DeviceInfo? device;
        lock (_cacheLock)
        {
            if (!_lastKnownDevices.Remove(instanceId, out device))
                return;
            _monitorAnnouncements.Forget(instanceId);
        }

        _logger.LogDebug("Device disappeared (instance removed): {DeviceId}", device.Id);
        DeviceDisappeared?.Invoke(this, new DeviceChangeEventArgs(device));
    }

    public ValueTask DisposeAsync()
    {
        _logger.LogInformation("Stopping device monitor, unregistering notifications");

        _displayChangeSink?.Dispose();
        _displayChangeSink = null;

        _instanceNotifyHandle?.Dispose();
        _instanceNotifyHandle = null;

        if (_selfHandle.IsAllocated)
            _selfHandle.Free();

        _logger.LogInformation("Device monitor stopped.");
        return ValueTask.CompletedTask;
    }
}

