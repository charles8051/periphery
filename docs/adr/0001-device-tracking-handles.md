---
title: "ADR-0001: Device Tracking Handles"
status: "Accepted"
date: "2025-07-15"
authors: "@charles8051 (proposal)"
tags: ["architecture", "decision"]
supersedes: ""
superseded_by: ""
---

# ADR-0001: Device Tracking Handles

**Tracks:** Per-device observable state in `DeviceWatcher`

---

## Context

Today, `DeviceWatcher` monitors a single filter and raises flat `Connected` / `Disconnected` events. Consumers who need to track N specific devices independently must either:

1. **Spin up N watchers** — each with its own OS subscription (WMI event watcher on Windows). This is expensive and scales poorly.
2. **Manually demux events** — subscribe to one watcher and write bespoke routing logic to map each event to the appropriate device state variable.

Neither option provides a batteries-included experience for the common scenario: "I have a known list of devices I care about; tell me when each one comes and goes."

### Proposal Summary

Introduce a `DeviceTrackingHandle` type that:

- Contains **query parameters** sufficient to identify a device (or class of devices) on the system.
- Exposes an **observable connection state** updated by `DeviceWatcher` when OS events arrive.
- Is passed as an array to the watcher, which **aggregates** the handles' query parameters into a single OS-level subscription.
- Receives **fan-out** from watcher events: each incoming event is matched against all handles, and matching handles have their state updated.

---

## Decision Drivers

- **Single OS subscription** — WMI/udev/IOKit watchers are heavyweight; one is far better than N.
- **Per-device state** — UI scenarios (WPF/MAUI binding, dashboard indicators) need per-device `IsPresent` (known to OS) and `IsConnected` (physically active).
- **API consistency** — the solution should feel like the existing fluent filter API.
- **Zero third-party deps** — the library's constraint (see ARCHITECTURE.md §1) rules out the System.Reactive NuGet package. `IObservable<T>` itself is a BCL interface and fair game.
- **Cross-platform honesty** — the abstraction must not assume Windows-specific identification schemes.

---

## Analysis

### 1. Filter Aggregation Strategy

The current `DeviceFilter` models **scalar** structured fields: one category, one name substring, one VID/PID pair. If handle A wants `Category=Usb, VID=046D` and handle B wants `Category=Bluetooth, Name="AirPods"`, there is no way to merge them into a single `DeviceFilter`.

| Strategy | Push-down efficiency | Provider impact | Complexity |
|---|---|---|---|
| **A. Widen to union** — pass `DeviceCategory.All` and no structured filters; match per-handle in-memory | Low — OS returns everything | None | Low |
| **B. Disjunctive filters** — extend `DeviceFilter` to support `OR` of multiple structured filter sets | High | High — providers must emit `OR` clauses in WQL / udev / IOKit | High |
| **C. Shared base filter** — watcher's own fluent filters set the common base; each handle narrows further | Medium — base filter is pushed; per-handle narrowing is in-memory | None | Medium |

**Decision: Option A — widen to union, all matching in-memory.**

When a `DeviceWatcher` has tracked handles, the OS-level subscription uses an **unfiltered** query (`DeviceCategory.All`, no structured properties). Every incoming event is matched against each handle's own `DeviceFilter` in memory. The watcher's own fluent filters (`.OfCategory(...)`, `.ByManufacturer(...)`, etc.) continue to apply to the **global** `Connected` / `Disconnected` events, but they do not constrain what tracked handles can match.

This is the simplest design and the most flexible — handles are fully independent. Each can track a completely different category, manufacturer, or VID/PID without any shared-base constraint.

**Option C rejected:** A shared base filter would force all handles to operate within a single category or manufacturer scope. This is too constraining for the primary use case ("I have a keyboard, a mouse, and AirPods — track all three"). It saves a modest amount of snapshot time (category push-down ≈ 50–150ms faster on Windows) but at the cost of API flexibility.

**Option B rejected:** See §1a below.

#### 1a. Option B Deep-Dive: Disjunctive Filter Push-Down Feasibility

**Resolved:** Option B investigated and **rejected** as a v1 strategy. See rationale below.

##### Per-Platform OR Support

| Platform | Native OR? | Mechanism | Practical constraints |
|---|---|---|---|
| **Windows (WQL)** | ✅ Yes | `WHERE (A OR B) AND C` — full boolean grouping with parentheses. Already used internally by `WhereClassGuidIn` (see `WqlQuery.cs:105`). | WMI has an undocumented limit on the number of `AND`/`OR` keywords per query — large disjunctions can return `WBEM_E_QUOTA_VIOLATION`. For event queries specifically, the `WITHIN` polling interval (currently 2s) is the dominant latency factor, not the WHERE clause. |
| **Linux (udev)** | ❌ No | `udev_monitor_filter_add_match_subsystem_devtype` and tag filters are **AND-only**. There is no API for "subsystem=usb OR subsystem=bluetooth" in a single monitor. | Achieving OR requires either (a) multiple `udev_monitor` instances on separate netlink sockets, or (b) a single unfiltered monitor with in-memory matching. Option (a) defeats the single-subscription goal. Option (b) is functionally identical to Option A/C. |
| **macOS (IOKit)** | ❌ No | `IOServiceAddMatchingNotification` accepts a **single matching dictionary** per registration. Dictionaries express AND conditions (key=value pairs). | Same trade-off as Linux: multiple registrations (one per disjunct) or one broad registration + in-memory filter. |

**Conclusion:** Only Windows natively supports OR, and even there it's constrained. Implementing Option B would mean:
- `WqlQuery` gains a disjunctive builder (moderate — ~50 LOC).
- `DeviceFilter` grows a `List<DeviceFilterClause>` with OR semantics (moderate).
- The Linux provider must either open N netlink sockets (bad) or ignore the disjunctions and filter in-memory anyway (making push-down a Windows-only optimisation).
- The macOS provider faces the same choice.

This creates **platform-divergent behaviour** behind a supposedly uniform abstraction — exactly the kind of leaky abstraction the architecture is designed to avoid.

##### Does Push-Down Even Help?

The real question: if we *could* push OR clauses to the OS on all platforms, would it measurably reduce latency?

**For event subscriptions (the primary tracking use-case):**

The performance characteristics are:

| Phase | Where time is spent | Impact of filter push-down |
|---|---|---|
| **Event delivery** | WMI polls every `WITHIN` seconds (2s). udev/IOKit deliver asynchronously via kernel notifications. | Zero — the OS fires events for hardware changes regardless of query specificity. The filter only determines whether WMI surfaces the event to the consumer. |
| **Event callback** | COM interop marshalling (Windows), netlink message parsing (Linux), mach message parsing (macOS). | Zero — the callback cost is per-event, not per-query. |
| **In-memory fan-out** | Iterate N handles, call `DeviceFilter.Matches()` per handle. | Negligible — `Matches()` is a handful of string comparisons. For N ≤ 100 handles this is nanoseconds vs. the millisecond-scale OS callback. |

**For snapshot queries (initial `SnapshotCurrentDevicesAsync`):**

| Phase | Where time is spent | Impact of filter push-down |
|---|---|---|
| **WMI query execution** | COM interop + WMI repository scan. A `SELECT * FROM Win32_PnPEntity` with no WHERE takes ~200–500ms on typical systems. Adding `WHERE ClassGuid = '{...}'` reduces this to ~50–150ms. | **Meaningful for snapshot only.** Category push-down is the big win. Further narrowing via OR on VID/PID within a category saves marginal time — the category filter alone reduces the result set by 90%+. With Option A, we forgo this benefit in exchange for handle independence (see §1b for mitigation strategies). |
| **DevNodeHelper.IsDevicePhysicallyConnected** | cfgmgr32 P/Invoke per device. | Dominant per-device cost. Reducing device count via category filter helps; further OR-narrowing saves a few P/Invoke calls at most. |

**Bottom line:** Category-level push-down provides ~80–90% of the snapshot performance benefit. Disjunctive VID/PID push-down would shave off a few extra devices at the cost of significant abstraction complexity — and would only actually push down on Windows. Option A accepts the full-scan snapshot cost in exchange for maximum handle flexibility (see §1b for mitigation strategies).

##### Verdict

Option B is **not worth the complexity for v1**. The performance gains are marginal (event latency is unchanged; snapshot latency is dominated by the category filter, not per-device narrowing). The cross-platform abstraction cost is high (Linux and macOS can't natively express OR, so they'd fall back to in-memory filtering anyway, making the "push-down" a Windows-only micro-optimisation).

If profiling later reveals that snapshot performance is a problem for users tracking many device classes simultaneously, the correct response is likely **multiple concurrent queries** (one per category) rather than query-level disjunctions — this would parallelise the OS calls and avoid the single-query OR complexity entirely.

#### 1b. Snapshot Cost Implications of Option A

With Option A, the initial snapshot query (`SnapshotCurrentDevicesAsync`) becomes `SELECT * FROM Win32_PnPEntity` with no WHERE clause. On Windows this is ~200–500ms and returns all PnP devices (~100–300 on a typical system), each requiring a `DevNodeHelper.IsDevicePhysicallyConnected` P/Invoke.

This is a one-time cost at `StartAsync()`. For the ongoing event path (the primary purpose of tracking), there is no performance difference — events arrive at the `WITHIN` interval regardless of query breadth.

If the snapshot cost becomes a concern, two mitigations are available without changing the API:
1. **Parallel snapshot** — run one snapshot query per distinct category found across all handles. This recovers per-category push-down while keeping handles independent.
2. **Lazy snapshot** — skip the upfront snapshot entirely; let handles populate their state as events arrive. Trades initial accuracy for speed.

### 2. Handle Identification Criteria

A handle needs match criteria. Each handle internally holds a `DeviceFilter` instance and reuses `DeviceFilter.Matches()` for evaluation — keeping the logic in one place.

**Decision:** Handles expose the full `DeviceFilter` API — all fluent filter methods **and** arbitrary lambda predicates via `.Where()`. Each handle carries its **own complete filter** including category, since there is no shared base filter (see §1).

`DeviceFilter` now provides convenience methods that cover the most common `DeviceInfo` properties. Combined with `.Where()` for anything remaining, every `DeviceInfo` field is filterable:

| Filter method | `DeviceInfo` property | Match type |
|---|---|---|
| `.OfCategory()` | `Category` | Exact |
| `.WithName()` | `Name` | Substring |
| `.ByManufacturer()` | `Manufacturer` | Substring |
| `.WithUsbId()` | `VendorId` / `ProductId` | Exact |
| `.Connected()` | `IsConnected` | Exact |
| `.WithSerialNumber()` | `SerialNumber` | Exact |
| `.WithBusType()` | `BusType` | Exact |
| `.WithStatus()` | `Status` | Exact |
| `.WithDriveType()` | `DriveType` | Exact |
| `.WithMacAddress()` | `MacAddress` | Exact |
| `.WithDriver()` | `Driver` | Substring |
| `.WithMinResolution()` | `DisplayResolution` | Minimum bounds |
| `.Where(d => ...)` | *Any property* | Custom lambda |

```csharp
// Structured convenience methods
var mouse = watcher.Track(t => t
    .OfCategory(DeviceCategory.Usb)
    .WithUsbId("046D", "C52B"));

var ssd = watcher.Track(t => t
    .OfCategory(DeviceCategory.Storage)
    .WithDriveType(DriveType.Fixed));

// Lambda escape hatch for anything else
var dock = watcher.Track(t => t
    .OfCategory(DeviceCategory.Usb)
    .Where(d => d.SerialNumber == "ABC123"));

var bigMonitor = watcher.Track(t => t
    .OfCategory(DeviceCategory.Monitor)
    .WithMinResolution(3840, 2160));
```

**Open question:** Should `DeviceId` exact-match be supported as a first-class filter? It's the most precise identifier but is platform-native and not stable across reboots on some systems. Users can already filter by ID via `.Where(d => d.Id == "...")` today.

### 3. One Handle ↔ Many Devices

A handle spec like "any Logitech mouse" can match **multiple** physical devices simultaneously. The state model must account for this.

| Model | Semantics | Complexity |
|---|---|---|
| **Set tracking** — `handle.PresentDevices` returns `IReadOnlyList<DeviceInfo>`; `IsPresent` = "at least one" | General, no surprises | Medium — must maintain per-handle device set |
| **Single tracking** — `handle.Device` returns one `DeviceInfo?` | Simpler API | Ambiguous — which device wins if multiple match? |

**Decision: Set tracking.** It matches OS reality (plug in two identical mice → two events) and avoids surprising edge cases. `IsPresent` is a convenience property derived from `PresentDevices.Count > 0`.

A single-device convenience property (`Device`) may be added later if demand warrants it — it would return `PresentDevices.FirstOrDefault()`.

Previously-seen-but-disconnected devices will **not** be tracked in v1. The state model is strictly "what's present right now." If "device was here but is now gone" UI states are needed, consumers can maintain their own history from `StateChanged` events.

### 4. Observable State Model

The zero-third-party-deps constraint rules out the **System.Reactive NuGet package** (operators like `.Select()`, `.Buffer()`, `.Throttle()`). However, `IObservable<T>` and `IObserver<T>` are **BCL interfaces** in the `System` namespace — they ship in the runtime with zero dependencies and have since .NET Framework 4.0.

| What you get | BCL (free) | System.Reactive (NuGet) |
|---|---|---|
| `IObservable<T>` / `IObserver<T>` interfaces | ✅ | ✅ |
| `Observable.Create`, `Subject<T>` | ❌ | ✅ |
| Operators (`.Select`, `.Where`, `.Buffer`, `.Throttle`) | ❌ | ✅ |
| `.Subscribe(Action<T>)` convenience extension | ❌ | ✅ |

This means `DeviceTracker` **can** implement `IObservable<bool>` natively. We manage the observer list internally and call `OnNext` when `IsConnected` changes. Consumers who want Rx operators add System.Reactive on their end — our library never takes the dependency.

The three notification surfaces are not mutually exclusive:

| Approach | Target consumers | Dependencies |
|---|---|---|
| `INotifyPropertyChanged` | WPF/MAUI XAML binding | None (BCL) |
| `StateChanged` event | Simple callback / console apps | None |
| `IObservable<bool>` | Rx consumers, composition pipelines | None (BCL interface; operators require System.Reactive on consumer side) |

**Decision: Implement all three.**

- `INotifyPropertyChanged` — fires `PropertyChanged` for `IsPresent`, `IsConnected`, `PresentDevices`, and `ConnectedDevices`, enabling direct XAML binding.
- `StateChanged` event — simple `EventHandler` for non-XAML consumers who just want a callback.
- `IObservable<bool>` — pushes `true`/`false` on `IsConnected` transitions. Consumers can compose with Rx operators if they bring in System.Reactive, or implement `IObserver<bool>` directly.

All three fire on the thread-pool thread that received the OS event, consistent with the existing `DeviceWatcher.Connected` event pattern. UI dispatch is the consumer's responsibility.

### 5. Handle Lifecycle & Ownership

| Question | Decision | Rationale |
|---|---|---|
| Add handles after `StartAsync()`? | ❌ Not initially | Preserves the existing Configure → Start → Dispose lifecycle. Dynamic registration adds concurrency complexity. |
| Who owns state updates? | The `DeviceWatcher` | Watcher writes to handle state; external code reads. |
| Handle after watcher disposal? | Becomes inert (`IsPresent`/`IsConnected` → `false`, events fire) | Prevents stale state. Subscribers remain attached — they see the `false` transition. Handle itself is not `IDisposable`. |
| Reuse across watchers? | ✅ Yes — at most one *active* watcher at a time | The handle is the object that UI binds to and Rx pipelines subscribe to. Forcing a new handle on every watcher restart would destroy all subscriber wiring. Instead, handles are long-lived: they survive watcher disposal and can be re-attached to a new watcher. |

**Binding enforcement:** Runtime. A handle tracks its current owner (the watcher that called `Track()`). Passing a handle to a second watcher while the first is still active (not disposed) throws `InvalidOperationException`. Once the owning watcher is disposed, the handle is unbound and available for re-attachment.

**State transitions during reuse:**

```
Watcher A creates handle → handle bound, IsPresent/IsConnected reflect reality
Watcher A disposes       → handle unbound, IsPresent/IsConnected → false, subscribers notified
Handle passed to Watcher B → handle bound again
Watcher B starts         → snapshot re-evaluates, IsPresent/IsConnected updated, subscribers notified
```

### 6. API Shape

**Decision: Fluent factory + collection pass-through.**

The fluent factory (`.Track(configure)`) creates new handles. Collection overloads (`.Track(handles)`) re-attach existing ones. Both patterns are supported through `Track` overloads:

```csharp
// ── First-time setup: fluent factory creates handles ───────────

await using var watcher = Devices.Watch();

var mouse    = watcher.Track(t => t.OfCategory(DeviceCategory.Usb).WithUsbId("046D", "C52B"));
var keyboard = watcher.Track(t => t.OfCategory(DeviceCategory.Usb).WithUsbId("046D", "B36A"));
var airpods  = watcher.Track(t => t.OfCategory(DeviceCategory.Bluetooth).WithName("AirPods"));

mouse.StateChanged += (_, _) => UpdateMouseIcon();
mouse.Subscribe(myObserver);

await watcher.StartAsync();
```

```csharp
// ── Watcher restart: pass existing handles ─────────────────────

await watcher.DisposeAsync();
// mouse.IsPresent/IsConnected → false, subscribers notified
// mouse.StateChanged and IObserver subscriptions remain attached

await using var watcher2 = Devices.Watch()
    .Track(mouse, keyboard, airpods);  // re-attach — no new subscriptions needed

await watcher2.StartAsync();
// mouse.IsPresent/IsConnected re-evaluated from snapshot, subscribers notified
```

```csharp
// ── Mixed: re-attach existing + create new ─────────────────────

await using var watcher3 = Devices.Watch()
    .Track(mouse, keyboard);  // existing handles

var monitor = watcher3.Track(t => t
    .OfCategory(DeviceCategory.Monitor)
    .WithMinResolution(3840, 2160));  // new handle

await watcher3.StartAsync();
```

**`Track` overloads:**

| Signature | Returns | Use case |
|---|---|---|
| `Track(Action<DeviceFilter> configure)` | `DeviceTracker` | Create a new handle with fluent filter config |
| `Track(params DeviceTracker[] handles)` | `DeviceWatcher` | Re-attach existing handles (chainable) |
| `Track(IEnumerable<DeviceTracker> handles)` | `DeviceWatcher` | Re-attach a collection (chainable) |

The `params` overload returns `DeviceWatcher` (not `DeviceTracker`) to enable fluent chaining: `Devices.Watch().Track(mouse, keyboard).Track(t => t...)`. The factory overload returns the new `DeviceTracker` so the caller can hold a reference to it.

Note that with Option A's unfiltered OS query (see §1), the watcher no longer needs category/name/manufacturer filters of its own for tracking to work. The watcher's fluent filters remain available for users who only use global events (no handles), preserving backward compatibility.

### 7. Impact on Existing Types

| Type | Change required |
|---|---|
| `DeviceWatcher` | Add `Track()` overloads (factory + collection), maintain `List<DeviceTracker>`, fan-out logic in `OnProviderConnected` / `OnProviderDisconnected`, snapshot matching in `SnapshotCurrentDevicesAsync`. When handles are registered, pass an unfiltered `DeviceFilter` to the provider (overriding any watcher-level fluent filters for the OS query). Watcher-level filters still apply to global events in-memory. On disposal, unbind all handles (set inert) but leave subscriber wiring intact. |
| `DeviceFilter` | None — handles compose their own filter; `Matches()` reused as-is |
| `IDeviceMonitorProvider` | None — aggregation lives in `DeviceWatcher`, provider sees one filter |
| `IDeviceProvider` | None |
| `DeviceInfo` | None |
| `Devices` | None (or optionally add a convenience overload) |

New types to introduce:
- `DeviceTracker` — the handle itself, holds per-handle `DeviceFilter` + observable state + owner tracking for single-watcher enforcement.

### 8. Naming

**Decision: `DeviceTracker`.** It reads naturally in code (`mouse.IsPresent`, `mouse.IsConnected`, `mouse.PresentDevices`, `mouse.ConnectedDevices`) and avoids the `Handle` / `IDisposable` implication of `DeviceTrackingHandle`. Preferred over `TrackedDevice` to avoid confusion with `DeviceInfo`.

### 9. Staged Builder for Category-Specific Filters

**Status:** Investigated and **deferred** to a future ADR. Not in scope for v1.

#### Motivation

`DeviceInfo` has properties that are only meaningful for specific categories:

| Property | Relevant categories |
|---|---|
| `VendorId` / `ProductId` | Usb (primarily) |
| `MacAddress` | Network, Bluetooth |
| `IPAddresses` / `Network` | Network |
| `DisplayResolution` / `DisplayBounds` | Monitor, Display |
| `DriveType` | Storage |

A staged builder would make the API self-documenting — calling `.OfCategory(DeviceCategory.Monitor)` would return a builder that only exposes monitor-relevant filters, preventing nonsensical combinations like filtering by `DisplayResolution` on a USB device.

#### Why Not Now

1. **Fluent return-type problem.** In a fluent chain, every method must return the most-derived builder type. Otherwise, category-specific methods vanish after chaining a common filter:

   ```csharp
   // ❌ .WithName() returns base builder, losing .WithUsbId()
   t.ForUsb().WithName("Mouse").WithUsbId("046D", "C52B")
   //                           ^^^^^ compile error
   ```

   The C# solution is CRTP (`DeviceTrackerBuilder<TSelf> where TSelf : DeviceTrackerBuilder<TSelf>`), which works but infects the base type with a generic parameter, complicating the `Track()` signature and internal storage.

2. **Enum dispatch is a runtime concept.** `.OfCategory(DeviceCategory.Monitor)` can't return `MonitorDeviceBuilder` — the enum value isn't known at compile time. This forces separate entry points (`ForUsb()`, `ForMonitor()`, `ForNetwork()`), fracturing the API surface.

3. **Sparse coverage.** 8 of 12 categories (Hid, Audio, Imaging, Biometric, Sensor, Ports, SmartCard, Printer) have **no** category-specific properties today. Their builder types would be identical to the base, making `ForHid()` / `ForAudio()` feel pointless.

4. **Cross-category properties.** `MacAddress` applies to both Network and Bluetooth. Placing it requires either duplication or a shared interface — both add complexity.

5. **Scope creep.** This decision affects `DeviceQuery` and `DeviceWatcher` too, not just tracking. If we do it, it should be applied consistently across the entire fluent API.

6. **Lambda escape hatch already exists.** Since lambdas are now allowed on handles (see §2), users can always write `.Where(d => d.DisplayResolution?.Width >= 3840)` regardless of category. The risk of the flat approach is "I filtered by DisplayResolution on USB and got no results" — a mild confusion, not a crash.

#### If Revisited

The cleanest approach would be per-category entry points that set the category implicitly:

```csharp
var mouse   = watcher.TrackUsb(t => t.WithUsbId("046D", "C52B"));
var monitor = watcher.TrackDisplay(t => t.WithMinResolution(1920, 1080));
var nic     = watcher.TrackNetwork(t => t.WithMacAddress("AA:BB:CC:DD:EE:FF"));
```

This avoids the CRTP/enum-dispatch problems entirely. Each `Track___()` method returns `DeviceTracker` and accepts a category-specific builder. Base filters (`WithName`, `ByManufacturer`, `Where`) are available on all builders via inheritance.

**Recommendation for v1:** Keep `DeviceFilter` flat. Add `<remarks>` XML doc on category-specific properties noting which categories they're meaningful for. Revisit staged builders in a dedicated ADR once tracking ships and we have real user feedback on discoverability.

---

## Proposed Public API Surface

```csharp
// ── First-time setup: create handles via fluent factory ────────

await using var watcher = Devices.Watch();

var mouse    = watcher.Track(t => t.OfCategory(DeviceCategory.Usb).WithUsbId("046D", "C52B"));
var keyboard = watcher.Track(t => t.OfCategory(DeviceCategory.Usb).WithUsbId("046D", "B36A"));
var airpods  = watcher.Track(t => t.OfCategory(DeviceCategory.Bluetooth).WithName("AirPods"));
var anyLogi  = watcher.Track(t => t.ByManufacturer("Logitech"));
var dock     = watcher.Track(t => t.OfCategory(DeviceCategory.Usb)
    .Where(d => d.SerialNumber == "DOCK-001"));  // lambda escape hatch

// Global events still work alongside tracking (applies watcher-level filter)
watcher.Connected += (_, e) => Log($"+ {e.Device.Name}");

// Subscribe once — survives watcher restarts
mouse.StateChanged += (sender, _) =>
{
    var tracked = (DeviceTracker)sender!;
    UpdateIcon(tracked.IsConnected);
};
mouse.Subscribe(new ConnectionObserver());

await watcher.StartAsync();

// ── Per-device state ───────────────────────────────────────────

Console.WriteLine($"Mouse: present={mouse.IsPresent} connected={mouse.IsConnected}");
Console.WriteLine($"AirPods: present={airpods.IsPresent} connected={airpods.IsConnected}");
Console.WriteLine($"Logitech devices: {anyLogi.ConnectedDevices.Count}");
```

```csharp
// ── Watcher restart: re-attach existing handles ────────────────

await watcher.DisposeAsync();
// All handles → IsPresent/IsConnected = false, subscribers notified

await using var watcher2 = Devices.Watch()
    .Track(mouse, keyboard, airpods, anyLogi, dock);  // re-attach all

await watcher2.StartAsync();
// Snapshot re-evaluates → subscribers fire with current state
// No need to re-wire StateChanged / IObserver — they survived disposal
```

```csharp
// ── Mixed: re-attach existing + create new ─────────────────────

await using var watcher3 = Devices.Watch()
    .Track(mouse, keyboard);

var monitor = watcher3.Track(t => t
    .OfCategory(DeviceCategory.Monitor)
    .WithMinResolution(3840, 2160));

await watcher3.StartAsync();
```

```csharp
// ── DeviceTracker (sketch) ─────────────────────────────────────

public sealed class DeviceTracker : INotifyPropertyChanged, IObservable<bool>
{
    public bool IsPresent { get; }                          // at least one device known to OS
    public bool IsConnected { get; }                         // at least one device physically active
    public IReadOnlyList<DeviceInfo> PresentDevices { get; }  // all OS-known matched
    public IReadOnlyList<DeviceInfo> ConnectedDevices { get; } // all active matched

    // ── Notification surfaces (all three, not mutually exclusive) ──
    public event EventHandler? StateChanged;                  // simple callback
    public event PropertyChangedEventHandler? PropertyChanged; // XAML binding
    public IDisposable Subscribe(IObserver<bool> observer);   // Rx-compatible
}
```

---

## Consequences

### Positive

- **Single OS subscription** for N tracked devices — major efficiency gain.
- **Per-device observable state** enables direct XAML binding.
- **No provider changes** — fan-out logic is entirely in `DeviceWatcher`.
- **Incremental** — existing `Connected` / `Disconnected` events are unaffected; tracking is opt-in.
- **Reusable handles** — event handlers and Rx subscriptions survive watcher disposal. Restart a watcher without re-wiring UI bindings.

### Negative / Risks

- **In-memory fan-out cost** — each OS event is matched against all handles. For small N (typical) this is negligible. If N grows large, consider indexing by category or VID.
- **Unfiltered OS query** — when handles are registered, the OS returns *all* device events. The snapshot query returns all PnP devices (~200–500ms on Windows). This is a one-time cost at `StartAsync()` and does not affect ongoing event latency. See §1b for mitigation strategies if this becomes a concern.
- **Thread-safety surface grows** — `DeviceTracker` state is written on thread-pool threads and read from UI threads. `INotifyPropertyChanged` callers expect this but it should be documented.
- **No dynamic registration** — handles cannot be added after `StartAsync()`. This is intentional for v1 but may be requested.
- **Owner tracking** — each handle must track its current owner watcher and validate on `Track()`. Small runtime cost but adds an internal state field and `InvalidOperationException` path.

---

## Open Questions

1. ~~**Disjunctive filter push-down?**~~ **Resolved — rejected for v1.** See §1a. OR is only natively supported on Windows (WQL); Linux and macOS cannot express disjunctions without multiple OS subscriptions.
2. ~~**Filter aggregation strategy?**~~ **Resolved — Option A (widen to union).** See §1. No shared base filter; each handle carries its own complete filter. OS query is unfiltered when handles are registered. Option C rejected as too constraining.
3. ~~**Validate handle criteria against base filter?**~~ **Resolved — no longer applicable.** There is no shared base filter to validate against. Each handle is self-contained.
4. ~~**Lambda predicates on handles?**~~ **Resolved — allowed.** See §2. With Option A, there is no aggregation to break. Lambdas are evaluated in-memory identically to structured filters. Provides escape hatch for properties without first-class filter methods.
5. ~~**Staged category-specific builders?**~~ **Resolved — deferred to future ADR.** See §9. The fluent return-type problem (CRTP), sparse category coverage (8 of 12 categories have no specific properties), and scope creep (affects all fluent APIs, not just tracking) make this premature for v1. The lambda escape hatch covers the gap.
6. ~~**One-to-many matching model?**~~ **Resolved — set tracking.** See §3. `PresentDevices` returns all OS-known matched devices; `ConnectedDevices` returns all active matched devices; `IsPresent` and `IsConnected` are `Count > 0`. No disconnected-device history in v1.
7. ~~**Observable state model?**~~ **Resolved — all three surfaces.** See §4. `INotifyPropertyChanged` + `StateChanged` event + `IObservable<bool>`. `IObservable<T>` is a BCL interface (not System.Reactive); consumers who want Rx operators bring in the NuGet package themselves. All notifications fire on callback thread; UI dispatch is consumer's responsibility.
8. ~~**Handle reuse across watchers?**~~ **Resolved — yes, reusable.** See §5. Handles are long-lived objects that survive watcher disposal. Event handlers and Rx subscriptions remain attached. At most one active watcher at a time, enforced at runtime via owner tracking. Handles become inert (IsPresent/IsConnected → false) between watchers.
9. ~~**API shape?**~~ **Resolved — fluent factory + collection pass-through.** See §6. `Track(Action<DeviceFilter>)` creates new handles; `Track(params DeviceTracker[])` and `Track(IEnumerable<DeviceTracker>)` re-attach existing ones. Both patterns composable in a single fluent chain.
10. ~~**Support `DeviceId` exact match on handles?**~~ **Deferred.** High precision but non-portable and potentially unstable. Users can filter by ID via `.Where(d => d.Id == "...")` today. Add a first-class method if user feedback shows the lambda is a pain point.
11. ~~**Dynamic handle registration post-start?**~~ **Deferred.** Would require locking the tracker list and re-snapshotting. Revisit in a future ADR if demand materialises.
12. ~~**Naming:**~~ **Resolved — `DeviceTracker`.** Reads naturally (`mouse.IsPresent`, `mouse.IsConnected`). Avoids the `Handle` / `IDisposable` implication of `DeviceTrackingHandle`. Preferred over `TrackedDevice` to avoid confusion with `DeviceInfo`.

---

## Implementation

Implemented in the following commits (see git log for full details):

| Component | File(s) | Summary |
|---|---|---|
| `DeviceTracker` | `Periphery/DeviceTracker.cs` | New type: `INotifyPropertyChanged`, `IObservable<bool>`, `StateChanged` event, set tracking, owner enforcement, reusability across watchers. |
| `DeviceWatcher` | `Periphery/DeviceWatcher.cs` | `Track()` overloads (factory + params + IEnumerable), unfiltered provider query when trackers exist, fan-out in connect/disconnect handlers, snapshot fan-out, unbind on dispose. |
| `DeviceFilter` | `Periphery/DeviceFilter.cs` | 7 new convenience methods: `WithSerialNumber`, `WithBusType`, `WithStatus`, `WithDriveType`, `WithMacAddress`, `WithDriver`, `WithMinResolution`. |
| `DeviceQuery` / `DeviceWatcher` | `Periphery/DeviceQuery.cs`, `Periphery/DeviceWatcher.cs` | Same 7 convenience methods surfaced on both fluent APIs. |
| Tests | `Periphery.Tests/DeviceTrackerTests.cs` | 27 unit tests covering state transitions, all three notification surfaces, ownership lifecycle, filter delegation, snapshot isolation. |
| Examples | `Periphery.Examples/Program.cs` | Examples 12–13: per-device tracking with `StateChanged` and `IObservable<bool>`, tracker reuse across watcher lifetimes. |
| Docs | `docs/ARCHITECTURE.md` | `DeviceTracker` description added; tracking example in §2.1. Filtering model reframed as in-memory-first. |

---

## References

- `docs/ARCHITECTURE.md` — Layering, provider contracts, concurrency model
- `Periphery/DeviceWatcher.cs` — Watcher with `Track()` overloads and fan-out logic
- `Periphery/DeviceTracker.cs` — Per-device observable state handle
- `Periphery/DeviceFilter.cs` — Filter model (reused by tracker matching)
- `Periphery/IDeviceProvider.cs` — Provider interfaces (unchanged by this ADR)
