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
/// <para>Two notification registrations are active after <see cref="StartAsync"/>:</para>
/// <list type="bullet">
/// <item>Interface filter (<c>CM_NOTIFY_FILTER_TYPE_DEVICEINTERFACE</c>):
///   arrival → <see cref="DeviceAppeared"/> (+ <see cref="DeviceActivated"/> if active);
///   removal → <see cref="DeviceDisappeared"/>.</item>
/// <item>Instance filter (<c>CM_NOTIFY_FILTER_TYPE_DEVICEINSTANCE</c>,
///   <c>CM_NOTIFY_FILTER_FLAG_ALL_DEVICE_INSTANCES</c>):
///   <c>DEVICEINSTANCESTARTED</c> → <see cref="DeviceActivated"/>;
///   <c>DEVICEINSTANCEREMOVED</c> → <see cref="DeviceDisappeared"/>.</item>
/// </list>
/// <para><b>Monitor DisplayConfig freshness (ADR-0066).</b> Beyond the two
/// registrations, a <see cref="WindowsDisplayChangeSink"/> runs a hidden-window
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
    private DevNodeHelper.CmNotifyHandle? _interfaceNotifyHandle;
    private DevNodeHelper.CmNotifyHandle? _instanceNotifyHandle;
    private int _started; // 0 = unstarted, 1 = started (Interlocked)

    // Last-known DeviceInfo per instance id. Populated by the arrival/start
    // notification callbacks and the StartAsync seed; consumed by the removal
    // callbacks. Post ADR-0054 (the whole-tree property scan is gone) this cache
    // is removal-only, with two jobs shared with the Linux/macOS providers:
    //   1. Deduplicate the interface- and instance-removal notifications that
    //      both fire for one hard unplug, so DeviceDisappeared fires once.
    //   2. Supply the last-known DeviceInfo (VID/PID/category/name) on removal —
    //      the devnode is gone by then, so TryBuildDeviceInfo would only yield an
    //      id-only stub that the watcher/tracker filters reject.
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

            // Registration 1 — device interface arrivals / removals
            var interfaceFilter = new DevNodeHelper.CM_NOTIFY_FILTER
            {
                cbSize     = Marshal.SizeOf<DevNodeHelper.CM_NOTIFY_FILTER>(),
                FilterType = DevNodeHelper.CM_NOTIFY_FILTER_TYPE_DEVICEINTERFACE,
                ClassGuid  = Guid.Empty,
            };

            int r1 = DevNodeHelper.CM_Register_Notification(
                ref interfaceFilter,
                GCHandle.ToIntPtr(_selfHandle),
                &NotificationShim,
                out nint rawInterfaceHandle);

            if (r1 != 0)
                throw new DeviceProviderException(
                    $"CM_Register_Notification (interface) failed with error code {r1}.");

            _interfaceNotifyHandle = new DevNodeHelper.CmNotifyHandle(rawInterfaceHandle);

            // Registration 2 — device instance start / stop for all instances
            var instanceFilter = new DevNodeHelper.CM_NOTIFY_FILTER
            {
                cbSize     = Marshal.SizeOf<DevNodeHelper.CM_NOTIFY_FILTER>(),
                Flags      = DevNodeHelper.CM_NOTIFY_FILTER_FLAG_ALL_DEVICE_INSTANCES,
                FilterType = DevNodeHelper.CM_NOTIFY_FILTER_TYPE_DEVICEINSTANCE,
            };

            int r2 = DevNodeHelper.CM_Register_Notification(
                ref instanceFilter,
                GCHandle.ToIntPtr(_selfHandle),
                &NotificationShim,
                out nint rawInstanceHandle);

            if (r2 != 0)
                throw new DeviceProviderException(
                    $"CM_Register_Notification (instance) failed with error code {r2}.");

            _instanceNotifyHandle = new DevNodeHelper.CmNotifyHandle(rawInstanceHandle);

            // Seed the last-known-device cache with the current device snapshot so
            // that removals of devices already present at start dedupe and carry
            // their last-known DeviceInfo (see the field comment above). This is the
            // Windows analogue of the Linux/macOS SeedCache.
            //
            // The seed uses the plain ToDeviceInfo build — category, VID/PID, name,
            // everything the watcher/tracker filters match on — and deliberately
            // skips the enrichment pipeline that the removed scan loop ran for diff
            // stability, keeping startup cheap. TryAdd (not assignment) so that an
            // arrival callback firing between registration and here is not clobbered.
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
        _interfaceNotifyHandle?.Dispose();
        _interfaceNotifyHandle = null;
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
    private void PublishMonitorEvents(DeviceInfo device, bool raiseAppeared, bool raiseActivated)
    {
        lock (_cacheLock)
        {
            if (_lastKnownDevices.TryGetValue(device.Id, out var prior))
                device = WindowsMonitorEnrichment.MergeArrival(device, prior);
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
                case DevNodeHelper.CM_NOTIFY_ACTION_DEVICEINTERFACEARRIVAL:
                    HandleDeviceArrival(eventData, eventDataSize);
                    break;

                case DevNodeHelper.CM_NOTIFY_ACTION_DEVICEINTERFACEREMOVAL:
                    HandleDeviceRemoval(eventData, eventDataSize);
                    break;

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

    private void HandleDeviceArrival(nint eventData, int eventDataSize)
    {
        string? symbolicLink = DevNodeHelper.ReadSymbolicLinkFromEventData(eventData, eventDataSize);
        if (symbolicLink is null) return;

        string? instanceId = DevNodeHelper.ParseInstanceIdFromSymbolicLink(symbolicLink);
        if (instanceId is null) return;

        DeviceInfo? device = WindowsDeviceProvider.TryBuildDeviceInfo(instanceId);
        if (device is null)
        {
            _logger.LogDebug(
                "Device arrived ({SymbolicLink}) but info could not be read for {InstanceId}; driver may not be loaded yet",
                symbolicLink, instanceId);
            return;
        }

        // Monitors go through the ordered publish path (merge enrichment forward +
        // raise under the gate + request a refresh); everything else keeps the
        // plain cache-then-raise path so non-monitor arrivals aren't serialized
        // against the display refresh.
        if (device.Category == DeviceCategory.Monitor)
        {
            PublishMonitorEvents(device, raiseAppeared: true, raiseActivated: device.IsActive);
            return;
        }

        lock (_cacheLock)
            _lastKnownDevices[device.Id] = device;

        _logger.LogDebug("Device appeared: {DeviceId} ({DeviceName})", device.Id, device.Name ?? "(unnamed)");
        DeviceAppeared?.Invoke(this, new DeviceChangeEventArgs(device));

        if (device.IsActive)
        {
            _logger.LogDebug("Device activated: {DeviceId} ({DeviceName})", device.Id, device.Name ?? "(unnamed)");
            DeviceActivated?.Invoke(this, new DeviceChangeEventArgs(device));
        }
    }

    private void HandleDeviceRemoval(nint eventData, int eventDataSize)
    {
        string? symbolicLink = DevNodeHelper.ReadSymbolicLinkFromEventData(eventData, eventDataSize);
        if (symbolicLink is null) return;

        string? instanceId = DevNodeHelper.ParseInstanceIdFromSymbolicLink(symbolicLink);
        if (instanceId is null) return;

        // First-wins: whichever of HandleDeviceRemoval / HandleInstanceRemoved runs first
        // claims the cache entry. The second finds nothing and returns, preventing the
        // double-DeviceDisappeared that would otherwise fire on hard removal.
        DeviceInfo? cached;
        lock (_cacheLock)
        {
            _lastKnownDevices.Remove(instanceId, out cached);
            _monitorAnnouncements.Forget(instanceId);
        }

        // Interface removal is authoritative: fire even if the device was never in the
        // cache (e.g. arrived while TryBuildDeviceInfo was failing). In that case
        // TryBuildDeviceInfo may also return null, so fall back to an ID-only stub.
        DeviceInfo device = cached
            ?? WindowsDeviceProvider.TryBuildDeviceInfo(instanceId)
            ?? new DeviceInfo { Id = instanceId };

        _logger.LogDebug("Device disappeared: {DeviceId} ({DeviceName})", device.Id, device.Name ?? "(unnamed)");
        DeviceDisappeared?.Invoke(this, new DeviceChangeEventArgs(device));
    }

    private void HandleInstanceStarted(nint eventData, int eventDataSize)
    {
        string? instanceId = DevNodeHelper.ReadInstanceIdFromEventData(eventData, eventDataSize);
        if (instanceId is null) return;

        DeviceInfo? device = WindowsDeviceProvider.TryBuildDeviceInfo(instanceId);
        if (device is null) return;

        // Monitors take the ordered publish path (see HandleDeviceArrival) so the
        // Activated payload carries merged enrichment and cannot be overtaken by a
        // concurrent refresh delta.
        if (device.Category == DeviceCategory.Monitor)
        {
            PublishMonitorEvents(device, raiseAppeared: false, raiseActivated: true);
            return;
        }

        lock (_cacheLock)
            _lastKnownDevices[instanceId] = device;

        _logger.LogDebug("Device activated (instance started): {DeviceId} ({DeviceName})", device.Id, device.Name ?? "(unnamed)");
        DeviceActivated?.Invoke(this, new DeviceChangeEventArgs(device));
    }

    // Handles CM_NOTIFY_ACTION_DEVICEINSTANCEENUMERATED (action=7): the OS has
    // re-enumerated the instance (e.g. after a driver reload or sleep-resume)
    // but has not yet started it. Record it in the cache so a later removal of
    // this instance dedupes and carries its last-known DeviceInfo.
    private void HandleInstanceEnumerated(nint eventData, int eventDataSize)
    {
        string? instanceId = DevNodeHelper.ReadInstanceIdFromEventData(eventData, eventDataSize);
        if (instanceId is null) return;

        DeviceInfo? device = WindowsDeviceProvider.TryBuildDeviceInfo(instanceId);
        if (device is null) return;

        lock (_cacheLock)
        {
            // Only seed — don't overwrite a richer snapshot already in cache.
            _lastKnownDevices.TryAdd(instanceId, device);
        }

        _logger.LogDebug("Device enumerated (not yet started): {DeviceId} ({DeviceName})", device.Id, device.Name ?? "(unnamed)");
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
        // connected" here); NOT to interface removal, where the instance can remain present while
        // only an interface goes away.
        if (DevNodeHelper.IsDeviceConnected(instanceId))
        {
            _logger.LogDebug(
                "Ignoring stale instance-removed for {InstanceId}: device currently started (fast re-enable / reset re-enumeration).",
                instanceId);
            return;
        }

        // Only fire if the device is still in the cache. If HandleDeviceRemoval already
        // ran for the same device (hard removal with an interface notification), the entry
        // is already gone and we return without re-firing DeviceDisappeared.
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
        _interfaceNotifyHandle?.Dispose();
        _interfaceNotifyHandle = null;

        if (_selfHandle.IsAllocated)
            _selfHandle.Free();

        _logger.LogInformation("Device monitor stopped.");
        return ValueTask.CompletedTask;
    }
}

