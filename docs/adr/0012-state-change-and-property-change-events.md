---
title: "ADR-0012: Monitor Provider State-Change and Property-Change Events"
status: "Accepted"
status_note: "Shipped. Decision 2 (periodic property re-scan) **superseded by [ADR-0054](0054-windows-property-freshness-events-over-polling.md)** (2026-06-08); Decisions 1 and 3 stand."
date: "2026-07-14"
authors: "@charles8051 (proposal)"
tags: ["architecture", "decision"]
supersedes: ""
superseded_by: ""
depends_on: ["0004-two-level-device-state-model.md", "0005-property-change-events.md", "0009-setupapi-windows-provider.md", "0010-udev-linux-provider.md", "0011-iokit-macos-provider.md"]
---

# ADR-0012: Monitor Provider State-Change and Property-Change Events

**Tracks:** `WindowsDeviceMonitorProvider`, `LinuxDeviceMonitorProvider`, `MacOSDeviceMonitorProvider`  
**Depends on:** ADR-0004 (two-level state model), ADR-0005 (property change events), ADR-0009 (SetupAPI Windows provider), ADR-0010 (Linux provider), ADR-0011 (macOS provider)  
**Supersedes:** (none — extends the monitor provider contracts established in ADR-0004 and ADR-0005)

---

## Context

### What ADR-0004 and ADR-0005 promised

ADR-0004 defined four monitor events:

| Event | Meaning |
|---|---|
| `DeviceAppeared` | Device entered the OS device tree |
| `DeviceDisappeared` | Device left the OS device tree |
| `DeviceConnected` | Device became physically active (driver started, hardware present) |
| `DeviceDisconnected` | Device became physically inactive (driver stopped, hardware removed) |

ADR-0005 added a fifth event (`DevicePropertyChanged`) for in-flight property mutations on
connected devices, and made an explicit design commitment: **no application-level polling**,
because the Windows implementation had `WMI __InstanceModificationEvent`, Linux had UPower D-Bus
`PropertiesChanged`, and macOS had IOKit `IOServiceAddInterestNotification`.

### What ADR-0009 broke

The migration from WMI to SetupAPI/cfgmgr32 removed the only Windows surface that delivered
both `DeviceConnected`/`DeviceDisconnected` state transitions for soft events (driver start/stop
without interface arrival) and `DevicePropertyChanged`. Specifically:

1. **Soft state changes** — a Bluetooth device going out of range does not remove its device
   interface or instance from the tree; it only clears `DN_STARTED` on the device node. WMI
   `__InstanceModificationEvent` detected this via `ConfigManagerErrorCode` comparison. The
   current cfgmgr32 implementation using `CM_NOTIFY_FILTER_TYPE_DEVICEINTERFACE` has no
   equivalent.

2. **`DevicePropertyChanged`** — cfgmgr32 has no property-change action. The complete
   `CM_NOTIFY_ACTION` enum for `CM_NOTIFY_FILTER_TYPE_DEVICEINSTANCE` is:
   `DEVICEINSTANCEENUMERATED`, `DEVICEINSTANCESTARTED`, `DEVICEINSTANCEREMOVED`.
   There is no `DEVICEINSTANCEPROPERTYCHANGED`.

The current `WindowsDeviceMonitorProvider` declares `DevicePropertyChanged` and `DeviceConnected`/
`DeviceDisconnected` on the interface but fires only `DeviceAppeared`/`DeviceDisappeared`.
The remaining events are dead.

### The WMI polling detail

ADR-0005 stated "no application-level polling" based on the assumption that modification events
were push. They were not. WMI `__InstanceModificationEvent` uses a `WITHIN 2` polling clause
internally — the WMI service polls `Win32_PnPEntity` every two seconds and delivers the diff.
The application did not hold a timer, but the latency was 0–2 seconds and the CPU cost existed.
This matters for the decision below: restoring equivalent behaviour with an explicit `PeriodicTimer`
is functionally identical to what WMI was doing, not a regression.

### The `[UnmanagedCallersOnly]` gap

ADR-0009's primary justification was NativeAOT compatibility. The ADR-0009 implementation sketch
and the implemented code both use `CmNotifyCallback`, a managed `delegate` type annotated with
`[UnmanagedFunctionPointer]`. The `[LibraryImport]` source generator cannot generate code for
managed delegate parameters — it falls back to `Marshal.GetFunctionPointerForDelegate`, which is
reflection-based and not AOT-safe. As a result, `device-dump.cs` still carries
`#:property PublishAot=false` despite the stated goal of removing it.

The correct AOT pattern for native callbacks is `[UnmanagedCallersOnly]` on a static method with
the provider instance passed through the native `pContext`/`refCon` parameter via `GCHandle`. This
pattern applies to all three platforms.

---

## Decision Drivers

| Concern | Requirement |
|---|---|
| Restore `DeviceConnected`/`DeviceDisconnected` | Hard events (driver start/stop with interface churn): ≤50 ms. Soft events (driver state flip without interface change): best-effort, ~2 s is acceptable |
| Restore `DevicePropertyChanged` on Windows | Functionally equivalent to WMI: ~2 s latency, no regression for consumers |
| AOT safety | All three monitor providers must be publishable with `PublishAot=true` |
| Cross-platform contract | All three providers must fire the same five events for the same physical stimuli |
| No new NuGet dependencies | WMI (`System.Management`) must not be re-introduced |
| Preserve ADR-0005 public API | `DeviceWatcher.PropertyChanged` event args and diff model remain unchanged |

---

## Decisions

### Decision 1 — Restore hard connect/disconnect via `CM_NOTIFY_FILTER_TYPE_DEVICEINSTANCE`

`WindowsDeviceMonitorProvider` registers a **second** `CM_Register_Notification` with:

```c
CM_NOTIFY_FILTER filter = {
    .cbSize     = sizeof(CM_NOTIFY_FILTER),
    .FilterType = CM_NOTIFY_FILTER_TYPE_DEVICEINSTANCE,
    .Flags      = CM_NOTIFY_FILTER_FLAG_ALL_DEVICE_INSTANCES, // 0x00000002
};
```

`CM_NOTIFY_FILTER_FLAG_ALL_DEVICE_INSTANCES` is a `Flags` field value (not `FilterType`) that
tells cfgmgr32 to deliver notifications for every device instance without requiring a specific
`InstanceId` to be named. Combined with `DEVICEINSTANCESTARTED` and `DEVICEINSTANCEREMOVED`:

| Action | Provider fires |
|---|---|
| `CM_NOTIFY_ACTION_DEVICEINSTANCESTARTED` | `DeviceConnected` |
| `CM_NOTIFY_ACTION_DEVICEINSTANCEREMOVED` | `DeviceDisappeared` (watcher cascades `DeviceDisconnected`) |

The event data for device-instance actions carries `CM_NOTIFY_EVENT_DATA.DeviceInstance.InstanceId`
(a `WCHAR[ANYSIZE_ARRAY]` at a fixed offset from the struct base), so the callback can call
`DevNodeHelper.LocateDevNode(instanceId)` and rebuild a `DeviceInfo` snapshot.

The provider holds two `CmNotifyHandle` instances: `_interfaceNotifyHandle` (existing) and
`_instanceNotifyHandle` (new). Both are disposed in `DisposeAsync`. Because both callbacks share
the same AOT shim, only one static method is needed (see Decision 3).

**Why not replace the interface filter with the instance filter?**  
`DEVICEINTERFACEARRIVAL` and `DEVICEINTERFACEREMOVAL` carry the symbolic link, which maps to a
specific interface GUID and allows the arrival event to be correlated with a device interface (e.g.
serial port, audio endpoint). `DEVICEINSTANCESTARTED`/`DEVICEINSTANCEREMOVED` carry an instance ID
but no interface GUID. Both registrations are needed for full coverage.

---

### Decision 2 — Restore soft connect/disconnect and property changes via a periodic re-scan

> **Superseded by [ADR-0054](0054-windows-property-freshness-events-over-polling.md) (2026-06-08).** Profiling on HD620 fleet hardware showed this whole-tree `PeriodicTimer` re-scan costs ~16% of a core (~320 ms per 2 s tick) in every process hosting a watcher — far above the "single-digit milliseconds" assumed below — while delivering property-change events no audited consumer usefully consumes (battery is a consumer-side poll; see ADR-0054). The scan is removed; Windows property freshness moves to OS event sources / consumer-scoped polls. Decisions 1 and 3 of this ADR stand.

There is no cfgmgr32 action for property changes and no notification for soft state flips.
A `PeriodicTimer` background loop in `WindowsDeviceMonitorProvider` provides equivalent
behaviour to the WMI `WITHIN 2` clause.

**Loop behaviour:**

1. On `StartAsync`, seed `_lastKnownDevices` by calling
   `DevNodeHelper.EnumerateDeviceInstances()` and building a `DeviceInfo` per device. This
   dictionary is the "previous snapshot" cache, keyed on `DeviceInfo.Id`.
2. Every `PropertyScanInterval` (default: 2 seconds), re-enumerate all present device instances.
3. For each device, compare against the cached snapshot using `DeviceInfoDiff.Compute`:
   - If `IsConnected` flipped `false → true`: fire `DeviceConnected`.
   - If `IsConnected` flipped `true → false`: fire `DeviceDisconnected`.
   - If any other property changed: fire `DevicePropertyChanged(previous, current)`.
   - If the device is new (not in cache): add it without firing (the interface notification
     already handled `DeviceAppeared`).
   - If a previously known device is no longer present: remove from cache without firing (the
     interface/instance notification already handled `DeviceDisappeared`).
4. Update the cache with the fresh snapshot.

Access to `_lastKnownDevices` is guarded by a `Lock` (`lock` in C# 13 / `object` lock in .NET 8).
The scan task is started from `StartAsync` and cancelled by a `CancellationTokenSource` that is
cancelled and awaited in `DisposeAsync`.

**Why application-level polling despite ADR-0005?**  
ADR-0005's "no application-level polling" was predicated on WMI delivering modification events.
WMI did not push — it polled at `WITHIN 2`. The application-visible behaviour (events arriving
with ~2 s latency) is preserved. The difference is that the polling now happens explicitly in the
provider rather than inside the WMI service. The public API contract (event-driven) is unchanged.

**`PropertyScanInterval` is a constructor parameter** with a default of `TimeSpan.FromSeconds(2)`.
It is intentionally not exposed on the public `IDeviceMonitorProvider` interface — it is a
Windows implementation detail.

---

### Decision 3 — `[UnmanagedCallersOnly]` callback pattern for all platforms

All three monitor providers use the following pattern for native callbacks, replacing managed
`delegate` types:

```csharp
// In the provider class:
private GCHandle _selfHandle;

// In StartAsync — allocate before registering the notification:
_selfHandle = GCHandle.Alloc(this);

// AOT-safe static shim (Windows example):
[UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
private static int NotificationShim(
    nint hNotify, nint context, int action, nint eventData, int eventDataSize)
{
    var self = (WindowsDeviceMonitorProvider)GCHandle.FromIntPtr(context).Target!;
    return self.OnDeviceNotification(hNotify, action, eventData, eventDataSize);
}

// In DisposeAsync — after CM_Unregister_Notification:
if (_selfHandle.IsAllocated) _selfHandle.Free();
```

The `pContext` / `refCon` parameter (present in all three platform APIs) carries
`GCHandle.ToIntPtr(_selfHandle)`. This pattern:

- Is fully AOT-safe: the static method is compiled to a stable native entry point; no IL stub,
  no `Marshal.GetFunctionPointerForDelegate`.
- Requires only one `GCHandle` per monitor instance regardless of how many notification
  registrations are active.
- Is guarded against early collection: `GCHandle.Alloc` pins a strong reference; the provider
  instance cannot be collected while the notification is registered.
- `GCHandle.Free` is idempotent when paired with a `IsAllocated` guard, preventing double-free
  in concurrent `DisposeAsync` calls.

The `CM_Register_Notification` P/Invoke declaration changes to use `unsafe delegate*`:

```csharp
[LibraryImport("cfgmgr32.dll")]
internal static partial int CM_Register_Notification(
    ref CM_NOTIFY_FILTER pFilter,
    nint pContext,
    delegate* unmanaged[Stdcall]<nint, nint, int, nint, int, int> pCallback,
    out nint pNotifyContext);
```

The `CmNotifyCallback` delegate type and its `[UnmanagedFunctionPointer]` attribute are removed.
`DevNodeHelper` changes from `partial class` in this area to `unsafe` for the function pointer
declaration.

---

## Platform-specific property change mechanisms

The following table documents the native surface each platform uses so that the
`[UnmanagedCallersOnly]` pattern and the polling fallback can be applied consistently:

| | Windows | Linux | macOS |
|---|---|---|---|
| **Hard connect** | `CM_NOTIFY_ACTION_DEVICEINSTANCESTARTED` | udev action `"add"` | `kIOMatchedNotification` |
| **Hard disconnect** | `CM_NOTIFY_ACTION_DEVICEINSTANCEREMOVED` | udev action `"remove"` | `kIOTerminatedNotification` |
| **Soft connect/disconnect** | `PeriodicTimer` diff on `IsConnected` | udev action `"bind"` / `"unbind"` | `kIOGeneralInterest` (`kIOMessageServiceIsTerminated` / `kIOMessageServiceIsSuspended`) |
| **Property changed** | `PeriodicTimer` diff | udev action `"change"` | `kIOGeneralInterest` (`kIOMessageServicePropertyChange`) |
| **Notification callback** | `[UnmanagedCallersOnly]` + `GCHandle` (static shim) | Background `Task` (poll on `udev_monitor_get_fd`); no callback | `[UnmanagedCallersOnly]` + `GCHandle` (IOServiceMatchingCallback) |
| **Soft-event latency** | ~2 s (scan interval) | Immediate (uevent) | Immediate (IOKit interest) |

**Linux `"change"` action:** `udev_monitor_receive_device` returns an action string for each
event. `"add"`/`"remove"` map to `DeviceAppeared`/`DeviceDisappeared`. `"change"` indicates a
property mutation on an existing device; the monitor provider reads a fresh snapshot and runs
`DeviceInfoDiff.Compute` against the cached previous snapshot, then fires `DevicePropertyChanged`.
`"bind"`/`"unbind"` indicate driver association changes and map to `DeviceConnected`/
`DeviceDisconnected`.

**macOS `kIOGeneralInterest`:** After initial enumeration, `IOServiceAddInterestNotification` is
called for each discovered service with `kIOGeneralInterest`. The interest callback receives a
`messageType` code. `kIOMessageServiceIsSuspended` and `kIOMessageServiceIsTerminated` are the
soft-disconnect signals; `kIOMessageServicePropertyChange` (unofficial but reliable on 10.14+)
triggers a re-read of IOKit properties and a `DeviceInfoDiff.Compute` comparison.

---

## Implementation Sketches

### Windows — second notification registration

```csharp
// WindowsDeviceMonitorProvider.StartAsync (additions):

// Second registration — device instance start/stop
var instanceFilter = new DevNodeHelper.CM_NOTIFY_FILTER
{
    cbSize     = Marshal.SizeOf<DevNodeHelper.CM_NOTIFY_FILTER>(),
    Flags      = DevNodeHelper.CM_NOTIFY_FILTER_FLAG_ALL_DEVICE_INSTANCES,
    FilterType = DevNodeHelper.CM_NOTIFY_FILTER_TYPE_DEVICEINSTANCE,
};
int r2 = DevNodeHelper.CM_Register_Notification(
    ref instanceFilter, GCHandle.ToIntPtr(_selfHandle),
    &NotificationShim, out nint rawInstanceHandle);
if (r2 != 0)
    throw new DeviceProviderException($"CM_Register_Notification (instance) failed: {r2}");
_instanceNotifyHandle = new DevNodeHelper.CmNotifyHandle(rawInstanceHandle);

// In _instanceNotifyHandle callback, action dispatch:
// CM_NOTIFY_ACTION_DEVICEINSTANCESTARTED → HandleInstanceStarted(instanceId)
// CM_NOTIFY_ACTION_DEVICEINSTANCEREMOVED → HandleInstanceRemoved(instanceId)
```

### Windows — DevNodeHelper additions needed

```csharp
// New constants in DevNodeHelper:
internal const int CM_NOTIFY_FILTER_FLAG_ALL_DEVICE_INSTANCES = 0x00000002;
internal const int CM_NOTIFY_ACTION_DEVICEINSTANCEENUMERATED  = 7;
internal const int CM_NOTIFY_ACTION_DEVICEINSTANCESTARTED     = 8;
internal const int CM_NOTIFY_ACTION_DEVICEINSTANCEREMOVED     = 9;

// New helper — reads InstanceId from DeviceInstance event data:
internal static string? ReadInstanceIdFromEventData(nint eventData, int eventDataSize)
{
    // CM_NOTIFY_EVENT_DATA DeviceInstance layout:
    //   offset 0:  FilterType (int, 4 bytes)
    //   offset 4:  Reserved   (int, 4 bytes)
    //   offset 8:  InstanceId (null-terminated UTF-16 string)
    const int instanceIdOffset = 8;
    if (eventData == 0 || eventDataSize < instanceIdOffset + 2) return null;
    return Marshal.PtrToStringUni(eventData + instanceIdOffset);
}
```

### Windows — periodic property scan

```csharp
// WindowsDeviceMonitorProvider — property scan fields:
private readonly TimeSpan _propertyScanInterval;
private CancellationTokenSource? _scanCts;
private Task? _scanTask;
private readonly object _cacheLock = new();
private readonly Dictionary<string, DeviceInfo> _lastKnownDevices = new();

// Seed cache in StartAsync (after registrations succeed):
lock (_cacheLock)
{
    foreach (var (devInst, id) in DevNodeHelper.EnumerateDeviceInstances())
    {
        var info = WindowsDeviceProvider.ToDeviceInfo(devInst, id);
        _lastKnownDevices[id] = info;
    }
}
_scanCts  = new CancellationTokenSource();
_scanTask = Task.Run(() => ScanLoopAsync(_scanCts.Token));

// Scan loop:
private async Task ScanLoopAsync(CancellationToken ct)
{
    using var timer = new PeriodicTimer(_propertyScanInterval);
    while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
    {
        foreach (var (devInst, id) in DevNodeHelper.EnumerateDeviceInstances())
        {
            DeviceInfo? previous;
            lock (_cacheLock)
                _lastKnownDevices.TryGetValue(id, out previous);

            if (previous is null) continue; // new device — interface notification handles it

            DeviceInfo current;
            try { current = WindowsDeviceProvider.ToDeviceInfo(devInst, id); }
            catch { continue; }

            var changed = DeviceInfoDiff.Compute(previous, current);
            if (changed.Count == 0) continue;

            lock (_cacheLock) _lastKnownDevices[id] = current;

            if (changed.Contains(nameof(DeviceInfo.IsConnected)))
            {
                var args = new DeviceChangeEventArgs(current);
                if (current.IsConnected) DeviceConnected?.Invoke(this, args);
                else                     DeviceDisconnected?.Invoke(this, args);
            }

            DevicePropertyChanged?.Invoke(this,
                new DeviceModificationEventArgs(previous, current));
        }
    }
}

// DisposeAsync:
if (_scanCts is not null)
{
    await _scanCts.CancelAsync().ConfigureAwait(false);
    await (_scanTask ?? Task.CompletedTask).ConfigureAwait(false);
    _scanCts.Dispose();
}
```

### Cross-platform — `[UnmanagedCallersOnly]` shim structure

```csharp
// Shown for Windows; macOS follows the identical pattern with IOServiceMatchingCallback:

private GCHandle _selfHandle;

public Task StartAsync(DeviceFilter filter, CancellationToken ct = default)
{
    ct.ThrowIfCancellationRequested();
    if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
        throw new InvalidOperationException(
            "StartAsync has already been called. Dispose and create a new monitor to restart.");

    _selfHandle = GCHandle.Alloc(this);
    // ... register notification passing GCHandle.ToIntPtr(_selfHandle) as pContext ...
}

[UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
private static int NotificationShim(
    nint hNotify, nint context, int action, nint eventData, int eventDataSize)
{
    var self = (WindowsDeviceMonitorProvider)GCHandle.FromIntPtr(context).Target!;
    return self.OnDeviceNotification(hNotify, action, eventData, eventDataSize);
}

public async ValueTask DisposeAsync()
{
    // ... cancel scan, await scan task, dispose handles ...
    if (_selfHandle.IsAllocated) _selfHandle.Free();
}
```

Note that `Interlocked.CompareExchange(ref _started, 1, 0)` also fixes the double-start contract
violation identified in the ADR-0009 post-implementation review. `_started` is an `int` field
(0 = unstarted, 1 = started).

---

## Consequences

**Positive:**

- `DevicePropertyChanged` is restored on Windows with equivalent user-visible latency (~2 s) to
  the original WMI implementation.
- Soft connect/disconnect events (Bluetooth out-of-range, driver suspend) are restored on Windows.
- `[UnmanagedCallersOnly]` makes all three monitor providers fully NativeAOT-safe.
  `#:property PublishAot=false` in `device-dump.cs` can be removed once the AOT shim is in place.
- The double-start contract violation identified in the ADR-0009 post-review is fixed via
  `Interlocked.CompareExchange` in the same refactor.
- Linux and macOS monitor providers can deliver property change events at native latency (immediate)
  because their platform APIs carry modification events natively.
- The `GCHandle` + static shim pattern is uniform across all three platforms, simplifying
  cross-platform code review.

**Negative / risks:**

- **Windows scan loop adds CPU cost.** Re-enumerating and re-reading all device properties every
  2 seconds is more visible than the WMI equivalent (which ran inside a service). On typical
  hardware with ~100–300 devices, this is measured in single-digit milliseconds. Profiling should
  confirm this before reducing the default interval.
- **Lock contention between scan loop and notification callbacks.** Both paths write
  `_lastKnownDevices`. The `lock (_cacheLock)` guard must be applied consistently. Keeping the
  lock duration short (just the dictionary read/write, not the `ToDeviceInfo` call) is essential.
- **Event duplication.** A hard-connect event can arrive from both the `DEVICEINTERFACEARRIVAL`
  callback and the next scan-loop tick (if the scan runs before `_lastKnownDevices` is updated
  by the callback). Mitigation: the scan loop skips devices not already in the cache (new devices
  are added by the notification callback path), and the watcher layer's `_knownConnectedIds`
  set provides deduplication above the provider level.
- **`GCHandle` leak if `DisposeAsync` is never called.** The `CmNotifyHandle` finalizer will
  unregister the notification, but `_selfHandle.Free()` is only called in `DisposeAsync`. A future
  improvement is to free the `GCHandle` inside `CmNotifyHandle.ReleaseHandle` (which runs under
  the finalizer), but this requires the handle to carry a reference to the `GCHandle`.
- **macOS `kIOMessageServicePropertyChange`** is not a documented constant in the public IOKit
  headers. The value `0xe0000100` is stable on macOS 10.14–15.x and used by system frameworks,
  but Apple's stance is that per-service property change notifications should go through
  `IOServiceMatchingNotification` with updated match criteria. This may need revision on future
  macOS releases.
