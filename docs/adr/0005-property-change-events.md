---
title: "ADR-0005: Property Change Events"
status: "Accepted"
status_note: "Shipped - `DevicePropertyChangedEventArgs`, `DeviceInfoDiff`, and `PropertyChanged` on `DeviceWatcher` / `DeviceTracker`."
date: "2025-07-15"
authors: "@charles8051 (proposal)"
tags: ["architecture", "decision"]
supersedes: ""
superseded_by: ""
depends_on: ["0003-device-category-expansion.md", "0004-two-level-device-state-model.md"]
---

# ADR-0005: Property Change Events

**Tracks:** Live-updating device properties, periodic re-snapshot, property diff events  
**Depends on:** ADR-0003 (Battery category), ADR-0004 (two-level state model)

---

## Context

ADR-0004 established four watcher events that model **discrete state transitions** — a device entering/leaving the OS device tree (`Appeared`/`Disappeared`) and becoming physically active/inactive (`Connected`/`Disconnected`). These work well for binary lifecycle events.

However, several device categories expose properties that change **while a device remains connected**. A laptop battery goes from 73% to 72% charge. A network adapter renegotiates from 1 Gbps to 100 Mbps after a cable swap. A monitor switches from 4K@60Hz to 1080p@120Hz. None of these are connect/disconnect events — they're property mutations on an existing, connected device.

Today, the only way to observe these changes is periodic re-enumeration:

```csharp
// Polling pattern — works, but no event-driven notification
while (true)
{
    var battery = await Devices.Enumerate()
        .OfCategory(DeviceCategory.Battery)
        .FirstOrDefaultAsync();

    Console.WriteLine($"Battery: {battery?.BatteryChargePercent}%");
    await Task.Delay(TimeSpan.FromSeconds(60));
}
```

This ADR proposes a fifth watcher event for property changes, evaluates which categories benefit, and defines the polling/push hybrid model required by platform APIs.

---

## Decision

Add a `PropertyChanged` event to `DeviceWatcher` that fires whenever any non-lifecycle property on a connected device changes between OS-delivered snapshots. Detection is **purely event-driven** — an initial snapshot is taken at watcher start to seed the cache, and all subsequent changes are received as OS modification events (`__InstanceModificationEvent` on Windows, UPower D-Bus on Linux, IOKit notifications on macOS). No application-level polling timer is introduced.

Event args carry both the previous and current `DeviceInfo` snapshots, plus an `IReadOnlySet<string>` of the property names that changed. `IsConnected` transitions are included in the diff — both `Connected`/`Disconnected` and `PropertyChanged` fire for the same transition; they are complementary, not mutually exclusive.

`DeviceTracker` already implements `INotifyPropertyChanged`. Because `DeviceTracker.Device` is replaced with a new snapshot on each property change, XAML path bindings such as `{Binding Device.BatteryChargePercent}` update automatically with no additional infrastructure.

---

## Decision Drivers

- **Battery is the forcing function** — ADR-0003 adds `BatteryChargePercent`, `BatteryStatus`, and `IsExternalPowerConnected` to `DeviceInfo`. Users will immediately ask "how do I get notified when these change?"
- **Multiple categories benefit** — Battery, Network, Display, and Bluetooth all have live-updating properties discoverable through OS enumeration APIs.
- **Snapshot model is insufficient** — `DeviceInfo` is an immutable snapshot. Without events, consumers must poll and diff manually.
- **Current event model has no slot for this** — `Appeared`/`Disappeared`/`Connected`/`Disconnected` are all binary state transitions. A property going from 73→72 doesn't fit any of them.
- **Consistency with `DeviceInfo` immutability** — the event should deliver two snapshots (before/after), not mutate the record.

---

## Proposed Changes

### 1. New event on `DeviceWatcher`

```csharp
/// <summary>
/// Raised when one or more properties on a connected device change value
/// between snapshots. The event args provide both the previous and current
/// <see cref="DeviceInfo"/> — consumers diff whichever properties they
/// care about.
/// </summary>
/// <remarks>
/// <para>Property changes are detected by OS modification events
/// (<c>__InstanceModificationEvent</c> on Windows, UPower D-Bus on Linux,
/// IOKit on macOS). No application-level polling occurs.</para>
/// <para>This event fires for all property changes including
/// <see cref="DeviceInfo.IsConnected"/> transitions. When connection state
/// changes, both this event and <see cref="Connected"/>/<see cref="Disconnected"/>
/// fire — they are complementary.</para>
/// </remarks>
public event EventHandler<DevicePropertyChangedEventArgs>? PropertyChanged;
```

### 2. New event args

```csharp
/// <summary>
/// Provides the previous and current <see cref="DeviceInfo"/> snapshots
/// when a device property changes, along with the set of property names
/// that differ between the two snapshots.
/// </summary>
public sealed class DevicePropertyChangedEventArgs : EventArgs
{
    /// <summary>The device snapshot before the change.</summary>
    public DeviceInfo Previous { get; }

    /// <summary>The device snapshot after the change.</summary>
    public DeviceInfo Current { get; }

    /// <summary>
    /// The names of properties that changed between <see cref="Previous"/>
    /// and <see cref="Current"/>. Names match the C# property names on
    /// <see cref="DeviceInfo"/> (e.g. <c>"BatteryChargePercent"</c>).
    /// </summary>
    public IReadOnlySet<string> ChangedProperties { get; }
}
```

Consumers can check `ChangedProperties` to avoid re-reading every property, or diff specific ones directly:

```csharp
watcher.PropertyChanged += (_, e) =>
{
    if (e.ChangedProperties.Contains(nameof(DeviceInfo.BatteryChargePercent)))
        Console.WriteLine($"Battery: {e.Current.BatteryChargePercent}%");

    if (e.ChangedProperties.Contains(nameof(DeviceInfo.IsExternalPowerConnected)))
        Console.WriteLine(e.Current.IsExternalPowerConnected == true
            ? "Plugged in" : "On battery");
};
```

### 3. No application-level polling

There is no `PropertySnapshotInterval` property and no timer. The OS delivers property change notifications through the same event subscription used for `Connected`/`Disconnected` (WMI `__InstanceModificationEvent` on Windows; UPower D-Bus on Linux; IOKit on macOS). The watcher seeds its `DeviceInfo` cache during the initial snapshot on `StartAsync`, then maintains it by applying each incoming event.

This means latency is determined entirely by the platform event delivery rate (~2 seconds for WMI; immediate for netlink/D-Bus/IOKit) — not by any application-level configuration.

---

## Detection Model

### Pure push — no application-level polling

All property changes are delivered as OS events. The watcher subscribes once and receives both lifecycle events (connect/disconnect) and property-mutation events through the same stream:

| Platform | Event source | Latency |
|---|---|---|
| **Windows** | WMI `__InstanceModificationEvent` (already subscribed per ADR-0004) | ~2s (WMI polling interval) |
| **Linux** | UPower D-Bus `PropertiesChanged`, netlink `RTMGRP_LINK` | Immediate |
| **macOS** | IOKit `IOServiceAddInterestNotification`, `SCNetworkReachability` | Immediate |

There is no timer, no periodic re-enumeration, and no `PropertySnapshotInterval` configuration.

### Diff algorithm

On each incoming modification event:

1. Build a new `DeviceInfo` snapshot from the event payload.
2. Look up the cached previous snapshot by device `Id`.
3. Compute `ChangedProperties` — the set of property names that differ.
4. If `ChangedProperties` is non-empty, fire `PropertyChanged` with both snapshots and the set.
5. Update the cache.

`ChangedProperties` is computed by a hand-maintained comparison helper (`DeviceInfoDiff.Compute`) that checks each named property individually and returns the names of those that differ. This avoids reflection and produces the `nameof`-string set that event args expose.

### IsConnected in the diff

`IsConnected` is **included** in the diff like any other property. When a device transitions from connected to disconnected, both `Disconnected` (from ADR-0004) and `PropertyChanged` (with `"IsConnected"` in `ChangedProperties`) fire. The two events are complementary — `Disconnected` is for lifecycle handlers, `PropertyChanged` is for general property-change handlers. No deduplication is needed; consumers choose which event fits their use case.

### Zero overhead when unused

The diff and `PropertyChanged` raise path is guarded by a null-check on the event delegate:

```csharp
if (PropertyChanged is not null)
    RaisePropertyChangedIfDifferent(previous, current);
```

No allocations occur if no handlers are subscribed, consistent with standard .NET event conventions.

---

## Categories That Benefit

### Tier 1 — Strong justification (ship with this ADR)

| Category | Properties that change | Event source |
|---|---|---|
| **Battery** | `BatteryChargePercent`, `BatteryStatus`, `IsExternalPowerConnected` | WMI `__InstanceModificationEvent` / UPower D-Bus `PropertiesChanged` / IOKit |
| **Network** | Link speed, operational state, IP addresses | `NetworkChange` (.NET) / netlink `RTMGRP_LINK` / `SCNetworkReachability` |
| **Display** | `DisplayResolution`, `DisplayBounds`, HDR mode (future) | Win32 `WM_DISPLAYCHANGE` / xrandr / IOKit `IOServiceAddInterestNotification` |
| **Bluetooth** | Peripheral battery level (HID Battery Service) | WMI `__InstanceModificationEvent` / BlueZ D-Bus `PropertiesChanged` / IOKit |

### Tier 2 — Future candidates (defer)

| Category | Properties | Why defer |
|---|---|---|
| **Printer** | Queue status (idle/printing/error) | Requires WMI `Win32_Printer` polling; niche use case |
| **Storage** | SMART health status | Requires elevated access on most platforms |
| **Monitor** | Brightness level | Continuous (slider); better as a stream than event |
| **Sensor** | Ambient light, accelerometer, GPS | Continuous high-frequency data; needs a subscription/stream model, not snapshot diffs |

### Out of scope (permanently)

| Category | Why |
|---|---|
| Audio volume/mute | Subsystem-specific API (`IMMNotificationClient` / PulseAudio), not PnP enumeration |
| Audio default device | Same — endpoint routing, not device property change |
| Ink/toner levels | Requires SNMP or vendor-specific protocols — device I/O, not discovery |

---

## Impact on Existing Types

| Type | Change |
|---|---|
| `DeviceWatcher` | Add `PropertyChanged` event; diff on existing `__InstanceModificationEvent` path; seed cache in `StartAsync` |
| `DevicePropertyChangedEventArgs` | New class with `Previous`, `Current`, `ChangedProperties` |
| `DeviceInfoDiff` | New internal static helper — computes `IReadOnlySet<string>` of changed property names |
| `DeviceTracker` | Replace `Device` with new snapshot on each property change; existing `INotifyPropertyChanged` implementation then notifies bindings automatically |
| `IDeviceMonitorProvider` | Add `DevicePropertyChanged` event; providers fire it from modification event callbacks |
| `WindowsDeviceMonitorProvider` | Route `OnDeviceModified` to `DevicePropertyChanged` in addition to connection-state transitions |
| Tests | `DeviceWatcherTests` for property change detection, null-handler fast path, `ChangedProperties` correctness |

No breaking changes to existing API. The `PropertyChanged` event is purely additive.

---

## Implementation Complexity

### Provider work per platform

| Platform | Event source | What's already done | Remaining work |
|---|---|---|---|
| **Windows** | WMI `__InstanceModificationEvent` | ADR-0004 subscribes; `OnDeviceModified` branches on connection state | Route non-connection-state modifications to `DevicePropertyChanged`; build new `DeviceInfo` from `TargetInstance` |
| **Linux** | UPower D-Bus `PropertiesChanged` | Nothing yet | Subscribe to UPower for battery/BT; netlink `RTMGRP_LINK` for network |
| **macOS** | IOKit `IOServiceAddInterestNotification` | Nothing yet | Register interest notifications for battery and network services |

### Thread safety

- `OnDeviceModified` is already called on the WMI event callback thread (Windows) or an OS notification thread (Linux/macOS). Property change events are raised on the same thread, consistent with ADR-0004 §Thread Safety.
- The `DeviceInfo` cache (`ConcurrentDictionary<string, DeviceInfo>`) is already maintained by the watcher for connect/disconnect dedup. Property change detection reads and writes the same cache under the same concurrency model.

### Performance

- **Zero cost when unused.** The diff is skipped entirely when `PropertyChanged` is null. No allocations, no comparison work.
- **Cost when active.** One `DeviceInfoDiff.Compute` call per modification event per tracked device. This is a handful of field comparisons and a small set allocation when a change is found — negligible.
- **No timer infrastructure.** No `System.Threading.Timer`, no lock around start/stop, no polling overhead.

---

## Resolved Design Questions

1. **No polling.** Detection is entirely event-driven. The OS event delivery rate (WMI `WITHIN 2`, D-Bus, IOKit) is sufficient for all Tier 1 categories. No `PropertySnapshotInterval`, no timer, no application-level re-enumeration.

2. **`ChangedProperties` is included in event args.** `DevicePropertyChangedEventArgs` exposes `IReadOnlySet<string> ChangedProperties`. The set is computed by `DeviceInfoDiff.Compute`, a hand-maintained helper that checks each named property. Pre-1.0, so the API surface is not yet locked.

3. **`DeviceTracker` fires for the resolved `Device` only.** Per ADR-0006, the tracker latches to a single resolved device once a profile matches. `PropertyChanged` on the tracker fires only when the properties of that resolved device change — not for devices in lower-priority profiles or ambiguous sets.

4. **No debounce.** Events fire on every OS-delivered modification. On Windows, WMI modification events are rate-limited to the `WITHIN 2` interval by the OS, making rapid bursts impossible in practice. Other platforms deliver events at natural OS pace.

5. **No category-scoped polling.** The watcher does not need to know which categories have live-updating properties; it simply diffs whatever the OS delivers.

6. **`IsConnected` is included in the diff.** When connection state changes, both `Connected`/`Disconnected` (lifecycle) and `PropertyChanged` (data) fire. Consumers choose which event fits their use case; both carry correct information.

---

## INotifyPropertyChanged Compatibility

`DeviceTracker` already implements `INotifyPropertyChanged`. No additional type is needed.

When a property change event arrives, the tracker replaces `Device` with the new `DeviceInfo` snapshot and raises `PropertyChanged("Device")`. The XAML binding engine re-evaluates any path that starts with `Device`, so nested bindings update automatically:

```xml
<!-- All of these update live as device properties change -->
<TextBlock Text="{Binding Device.BatteryChargePercent}" />
<TextBlock Text="{Binding Device.DisplayResolution}" />
<TextBlock Text="{Binding Device.IPAddresses}" />
```

Lifecycle properties (`IsConnected`, `IsPresent`, `Device`) already raise `PropertyChanged` today via `StateChanged`. Property changes extend this naturally — the same `INotifyPropertyChanged` contract, no new event type needed for binding.

### Rejected alternative: `ObservableDevice` wrapper

A dedicated wrapper class with flat promoted properties (`BatteryChargePercent` directly on the wrapper rather than at `Device.BatteryChargePercent`) was considered. **Rejected** because `DeviceTracker` already satisfies the binding contract through path binding, and adding a new public type for a purely ergonomic shortcut introduces maintenance burden (every new `DeviceInfo` property requires a matching update to the wrapper) with minimal benefit. The path binding approach is idiomatic and already familiar to XAML developers.

### Rejected alternative: mutable `DeviceInfo` with `internal set`

Convert `DeviceInfo` from a `record` to a `class` with `internal set` accessors and implement `INotifyPropertyChanged` directly. **Rejected** because `DeviceInfo` is also returned from `Devices.Enumerate()` as a snapshot — those instances would be indistinguishable from the live tracker instance. Consumer code comparing a saved reference against `tracker.Device` would silently always be equal (same object). Record equality used by `DeviceInfoDiff.Compute` would also break.

---

## Consequences

### Positive

- **Battery, network, and display changes are observable without polling.** The long-standing consumer workaround (timer + manual diff) is eliminated.
- **No API surface added to `DeviceInfo`.** The record stays a pure snapshot type.
- **Zero overhead when unused.** No timer, no allocations if `PropertyChanged` is null.
- **`ChangedProperties` eliminates defensive re-reads.** Consumers can check the set once and act only on changed fields.
- **XAML path binding works for free.** `DeviceTracker` already implements `INotifyPropertyChanged`; replacing `Device` on each property change is sufficient for `{Binding Device.BatteryChargePercent}` to update live.
- **`DeviceTracker` filtering to resolved `Device`** (per ADR-0006) means the tracker's `PropertyChanged` event carries the same single-device semantics as the rest of the tracker API.

### Negative / Risks

- **`DeviceInfoDiff.Compute` must be maintained by hand.** Every new property added to `DeviceInfo` must also be added to the diff helper, or it will be silently invisible to `PropertyChanged`. A test that enumerates all `DeviceInfo` properties via reflection and asserts they appear in the diff helper is the recommended guard.
- **Both `Disconnected` and `PropertyChanged` fire on connection-state transitions.** Consumers who subscribe to both must be prepared for two events on disconnect. This is documented but is a potential source of double-handling bugs.
- **Windows WMI `WITHIN 2` latency.**

---

## Alternatives Considered

### A. No events — polling only

Consumers call `Devices.Enumerate()` on their own timer. Simple, no new API surface.

**Rejected because:** Every consumer reimplements the same timer + diff logic. The watcher already has the infrastructure (device cache, event dispatch, lifecycle management). Centralizing it avoids duplication and ensures consistent behavior.

### B. `IObservable<DeviceInfo>` stream per device

Each connected device exposes a hot observable that pushes new snapshots on change.

**Deferred because:** Requires per-device observable management, subscription lifecycle, and backpressure handling. Significantly more complex than a single watcher event. May be the right model for Tier 2 (sensors) in the future, but overkill for Tier 1 (battery, network, display).

### C. Mutable `DeviceInfo` with `INotifyPropertyChanged`

Make `DeviceInfo` mutable and implement `INotifyPropertyChanged`.

**Rejected because:** Breaks the core immutability invariant. Records provide structural equality, `with` expressions, and thread-safety guarantees. Mutability would undermine all of these.

---

## References

- ADR-0003 — Battery category and `BatteryChargePercent` / `BatteryStatus` / `IsExternalPowerConnected` properties
- ADR-0004 — Two-level state model (`Appeared`/`Disappeared`/`Connected`/`Disconnected`)
- ADR-0002 — Device tree topology (topology properties are static, not live-updating)
- `docs/ARCHITECTURE.md` §6 — Concurrency model, event dispatch threading
- `Periphery/DeviceWatcher.cs` — Current event model and `_knownConnectedIds` cache
- [Windows `WM_DISPLAYCHANGE`](https://learn.microsoft.com/en-us/windows/win32/gdi/wm-displaychange) — Push notification for display resolution changes
- [.NET `NetworkChange` class](https://learn.microsoft.com/en-us/dotnet/api/system.net.networkinformation.networkchange) — Push notification for network changes
- [Linux netlink `RTMGRP_LINK`](https://man7.org/linux/man-pages/man7/rtnetlink.7.html) — Push notification for network link state
