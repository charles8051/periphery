---
title: "ADR-0029: DeviceTracker Edge Events (Appeared / Disappeared / Activated / Deactivated)"
status: "Accepted"
date: "2026-07-15"
authors: "@charles8051 (proposal)"
tags: ["architecture", "decision"]
supersedes: ""
superseded_by: ""
depends_on: ["0004-two-level-device-state-model.md", "0012-state-change-and-property-change-events.md"]
---

# ADR-0029: DeviceTracker Edge Events (Appeared / Disappeared / Activated / Deactivated)

**Tracks:** `DeviceTracker`, `DeviceTrackerState`, `DeviceTrackerTransition`  
**Depends on:** ADR-0004 (two-level state model), ADR-0012 (monitor provider state-change events)  
**Supersedes:** (none)

---

## Context

`DeviceTracker` currently exposes three notification surfaces:

| Surface | What it delivers |
|---|---|
| `INotifyPropertyChanged` | Property name string when `Device`, `ActivityStatus`, `IsActive`, `IsPresent`, or `ActiveProfile` changes |
| `StateChanged` event | Full `DeviceTrackerState` snapshot (`Device`, `ActivityStatus`, `ActiveProfile`) on any change |
| `IObservable<DeviceTrackerState>` | Same snapshot pushed to Rx subscribers; behaves like a BehaviorSubject |

All three surfaces deliver the **current state after the transition**. None of them explicitly
signal the **edge** — the moment of crossing from one state to another.

### The ceremony problem

To react to a specific transition (e.g. "just became Active"), a consumer must maintain
their own previous-state variable and diff it against the new value:

```csharp
// INotifyPropertyChanged approach
private bool _wasActive;

tracker.PropertyChanged += (_, e) =>
{
    if (e.PropertyName == nameof(DeviceTracker.IsActive))
    {
        if (tracker.IsActive && !_wasActive) OnActivated(tracker.Device!);
        if (!tracker.IsActive && _wasActive) OnDeactivated();
        _wasActive = tracker.IsActive;
    }
};
```

```csharp
// StateChanged approach — same pattern, same ceremony
private DeviceActivityStatus _lastStatus = DeviceActivityStatus.Absent;

tracker.StateChanged += (_, state) =>
{
    if (state.IsActive && _lastStatus != DeviceActivityStatus.Active)
        OnActivated(state.Device!);
    else if (!state.IsActive && _lastStatus == DeviceActivityStatus.Active)
        OnDeactivated();
    _lastStatus = state.ActivityStatus;
};
```

```csharp
// Rx approach — DistinctUntilChanged handles the dedup, but requires Rx
tracker.Select(s => s.IsActive)
       .DistinctUntilChanged()
       .Where(x => x)
       .Subscribe(_ => OnActivated(tracker.Device!));
```

Each of these works correctly, but all three require the consumer to understand the
state machine semantics in order to derive what should be a primitive. Consumers
who simply want to know "when did the device turn on?" should not need to understand
`DeviceActivityStatus` at all.

### The `DeviceWatcher` precedent

`DeviceWatcher` already exposes exactly this pattern at the watcher level:

```
DeviceWatcher.Appeared / Disappeared     ← OS tree presence
DeviceWatcher.Activated / Deactivated    ← driver/hardware state
```

`DeviceTracker` is the natural downstream aggregation of those same events, filtered
and resolved through the profile/latch mechanism. The four edge events already exist
as internal methods (`OnDeviceAppeared`, `OnDeviceDisappeared`, `OnDeviceConnected`,
`OnDeviceDisconnected`) — they simply are not exposed publicly.

### The torn-read concern

`DeviceTrackerState` was introduced to solve the torn-read problem: `Device` and
`ActivityStatus` are updated atomically under `_lock`, and the snapshot is captured
once and handed to all notification surfaces simultaneously. Separate events raise
the question of whether `Device` is consistent at the moment the event fires.

For edge events the answer is: **yes**, provided the event is fired after the lock
is released with the post-transition `DeviceTrackerState` snapshot passed as the
event argument — which is exactly how `NotifyChanges` already works.

---

## Proposal

Add four edge events to `DeviceTracker`. Each event carries a `DeviceTrackerTransition`
argument — a new `readonly record struct` holding both the `Before` and `After`
`DeviceTrackerState` snapshots, both captured atomically under `_lock` before any
notification fires.

### New type: `DeviceTrackerTransition`

```csharp
/// <summary>
/// An atomic snapshot of a <see cref="DeviceTracker"/> state transition,
/// delivered as the event argument for the tracker's edge events.
/// Both snapshots are captured under the tracker's internal lock before
/// any notification fires — they are always mutually consistent.
/// </summary>
public readonly record struct DeviceTrackerTransition(
    DeviceTrackerState Before,
    DeviceTrackerState After);
```

### New events

```csharp
/// <summary>A matching device entered the OS device tree.</summary>
public event EventHandler<DeviceTrackerTransition>? Appeared;

/// <summary>A matching device left the OS device tree.</summary>
public event EventHandler<DeviceTrackerTransition>? Disappeared;

/// <summary>A matching device became physically active (driver started).</summary>
public event EventHandler<DeviceTrackerTransition>? Activated;

/// <summary>A matching device became physically inactive (driver stopped).</summary>
public event EventHandler<DeviceTrackerTransition>? Deactivated;
```

### Usage

The simplest cases remain one-liners:

```csharp
tracker.Activated   += (_, t) => OpenHandle(t.After.Device!);
tracker.Deactivated += (_, t) => Console.WriteLine($"Lost: {t.Before.Device!.Name}");
```

`Before` is the primary value on `Disappeared` and `Deactivated` — `t.After.Device`
is null or downgraded at that point, so `t.Before.Device` is the only place the
last-known device still lives without the consumer caching it themselves:

```csharp
// Log the name of the device that just went away — no consumer-side caching needed
tracker.Disappeared += (_, t) => _log.Info($"Unplugged: {t.Before.Device!.Name}");

// Safe handle open — After.Device guaranteed non-null when After.IsActive
tracker.Activated += (_, t) => OpenHandle(t.After.Device!);

// Multi-profile: which profile won this activation
tracker.Activated += (_, t) =>
{
    if (t.After.ActiveProfile == _primaryProfile)
        UseAsPrimary(t.After.Device!);
};

// Atomic status + name update — no torn-read possible
tracker.Activated += (_, t) =>
{
    StatusBadge.Color = Green;
    DeviceLabel.Text = t.After.Device!.Name;
};
```

#### What counts as an edge

| Event | Fires when |
|---|---|
| `Appeared` | `before.IsPresent == false` → `after.IsPresent == true` |
| `Disappeared` | `before.IsPresent == true` → `after.IsPresent == false` |
| `Activated` | `before.IsActive == false` → `after.IsActive == true` |
| `Deactivated` | `before.IsActive == true` → `after.IsActive == false` |

For USB devices, `Appeared` and `Activated` fire simultaneously (same `NotifyChanges`
call, same logical moment). For Bluetooth, they diverge: a device that is paired but
out of range fires `Appeared` but not `Activated`; when it comes into range it fires
`Activated` without a redundant `Appeared`.

#### Implementation sketch

`NotifyChanges` already receives both `before` and `after`. A `DeviceTrackerTransition`
is constructed once and reused across all four checks — no extra allocations:

```csharp
private void NotifyChanges(DeviceTrackerState before, DeviceTrackerState after)
{
    // ... existing StateChanged / IObservable raises ...

    var transition = new DeviceTrackerTransition(before, after);

    if (!before.IsPresent && after.IsPresent)
        Appeared?.Invoke(this, transition);
    if (before.IsPresent && !after.IsPresent)
        Disappeared?.Invoke(this, transition);
    if (!before.IsActive && after.IsActive)
        Activated?.Invoke(this, transition);
    if (before.IsActive && !after.IsActive)
        Deactivated?.Invoke(this, transition);
}
```

---

## Extension: DeviceTracker.PropertyChanged

### Context

`DeviceWatcher.PropertyChanged` fires for all matching devices and carries
`DevicePropertyChangedEventArgs` (Previous, Current, ChangedProperties). A consumer
tracking a specific device via `DeviceTracker` currently has to subscribe to both
`tracker.StateChanged` (to know *which* device is resolved) and `watcher.PropertyChanged`
(to know *what changed*) and correlate them manually. This is the same "ceremony" problem
as the edge events — the information exists, but the consumer has to assemble it.

### Decision

Add a `PropertyChanged` event to `DeviceTracker` that fires only when the resolved
`Device` changes due to a property mutation (not a lifecycle transition). It carries
the same `DevicePropertyChangedEventArgs` as `DeviceWatcher.PropertyChanged`, giving
tracker-scoped consumers the full diff without coupling them to the watcher.

### New event

```csharp
/// <summary>
/// Raised when one or more properties on the resolved <see cref="Device"/> change
/// value between OS-delivered modification events. Provides both the previous and
/// current <see cref="DeviceInfo"/> snapshots and the set of property names that
/// changed. Only fires when a device is resolved — no event when <see cref="Device"/>
/// is <c>null</c>.
/// </summary>
/// <remarks>
/// This event is complementary to <see cref="StateChanged"/> and the edge events:
/// <list type="bullet">
/// <item><see cref="Activated"/>/<see cref="Deactivated"/> fire on lifecycle transitions
/// (<c>IsActive</c> crossing true/false).</item>
/// <item><see cref="PropertyChanged"/> fires when the resolved device's data changes
/// while it remains in the same lifecycle state.</item>
/// </list>
/// <c>IsActive</c> transitions will appear in <c>ChangedProperties</c> when they arrive
/// via <c>OnDevicePropertyChanged</c> — the two events are complementary, not exclusive.
/// </remarks>
public event EventHandler<DevicePropertyChangedEventArgs>? PropertyChanged;
```

### What fires it

`DeviceTracker.OnDevicePropertyChanged` already updates the resolved device snapshot.
The new event fires from there — after `NotifyChanges` raises `StateChanged` and the
edge events — carrying the original `DevicePropertyChangedEventArgs` the watcher
produced. `_device` is checked before firing: if the property change did not affect the
resolved device, the event does not fire.

### Usage

```csharp
// Track battery level on the resolved device only — no watcher coupling
tracker.PropertyChanged += (_, e) =>
{
    if (e.ChangedProperties.Contains(nameof(DeviceInfo.BatteryChargePercent)))
        UpdateBadge(e.Current.BatteryChargePercent);
};

// Combine with Activated for full lifecycle + property coverage
tracker.Activated   += (_, t) => OpenHandle(t.After.Device!);
tracker.PropertyChanged += (_, e) => RefreshDisplay(e.Current);
tracker.Deactivated += (_, t) => CloseHandle();
```

### Relationship to `DeviceWatcher.PropertyChanged`

Both events carry the same `DevicePropertyChangedEventArgs`. The difference is scope:

| Surface | Scope | When it fires |
|---|---|---|
| `DeviceWatcher.PropertyChanged` | All matching devices | Any matching device property changes |
| `DeviceTracker.PropertyChanged` | Resolved device only | Only when `tracker.Device` is the changed device |

Subscribing to `DeviceTracker.PropertyChanged` is safe when the watcher disposes:
the tracker survives watcher disposal, and the event simply stops firing until a new
watcher is attached.

---

## Alternatives Considered

### 1. `INotifyPropertyChanged` on `DeviceTracker`

Raises a string property name (`"Device"`) when the snapshot is replaced. Does not
propagate into `DeviceInfo` itself — WPF binding breaks at `Device.BatteryChargePercent`
because `DeviceInfo` has no `PropertyChanged` of its own. **Rejected** — removed in the
session preceding this ADR.

### 2. Single `TransitionChanged` event with a flags enum

```csharp
public event EventHandler<DeviceTrackerTransition>? TransitionChanged;
// consumer checks transition.Appeared, transition.Activated, etc.
```

Forces consumers to read a flags value and branch. Four named events are more
discoverable and produce simpler subscriptions. **Rejected.**

### 3. Expose `IObservable<DeviceTrackerTransition>` instead of events

Rx-friendly but adds an Rx dependency for consumers who want simple edge detection.
The existing `IObservable<DeviceTrackerState>` already serves Rx consumers.
**Rejected** for the primary surface; Rx consumers can derive transitions from
the existing observable using `Pairwise()` / `Buffer(2,1)`.

### 4. No `DeviceTracker.PropertyChanged` — rely on `DeviceWatcher.PropertyChanged`

Requires consumers to hold a reference to both the tracker and the watcher, filter
watcher events by `tracker.Device?.Id`, and tolerate the watcher being null after
disposal. **Rejected** — adds coupling and ceremony the tracker layer is designed
to eliminate.

---

## Consequences

### Positive

- Consumers can react to exact lifecycle edges without maintaining prior-state variables.
- `DeviceTracker.PropertyChanged` completes the symmetry with `DeviceWatcher.PropertyChanged`.
- `DeviceTrackerTransition.Before` makes the last-known device available on
  `Disappeared`/`Deactivated` without consumer-side caching.
- All notifications remain on the thread-pool thread that received the OS event —
  consistent with existing behaviour.
- `DeviceTrackerTransition` is a `readonly record struct` — stack-allocated, no heap
  pressure, structural equality for free.

### Negative / Constraints

- Five new public event fields increase the API surface of `DeviceTracker`.
- `NotifyChanges` gains five more conditional branches — complexity is justified
  by the reduction in consumer boilerplate.
- `PropertyChanged` event name shadows `System.ComponentModel.INotifyPropertyChanged.PropertyChanged`
  in intellisense for consumers who also import `System.ComponentModel`; this is
  intentional and consistent with `DeviceWatcher.PropertyChanged`.

### Test checklist

- `Appeared` fires on `OnDeviceAppeared` (absent → present); not on second device (latch).
- `Disappeared` fires on `OnDeviceDisappeared`; `Before.Device` is the last-known snapshot.
- `Activated` fires on `OnDeviceConnected` (inactive → active).
- `Deactivated` fires on `OnDeviceDisconnected` (active → inactive).
- USB simultaneous: both `Appeared` + `Activated` fire from single `OnDeviceConnected`.
- BT diverge: `Appeared` only from `OnDeviceAppeared(isActive: false)`; `Activated` only
  from subsequent `OnDeviceConnected`.
- No edge event fires when latch rejects a second device (state unchanged).
- `PropertyChanged` fires on `OnDevicePropertyChanged` for the resolved device.
- `PropertyChanged` does not fire when an unresolved device's properties change.
- `PropertyChanged` carries correct Previous/Current/ChangedProperties.
    if (before.IsPresent && !after.IsPresent)
        Disappeared?.Invoke(this, transition);

    if (!before.IsActive && after.IsActive)
        Activated?.Invoke(this, transition);

    if (before.IsActive && !after.IsActive)
        Deactivated?.Invoke(this, transition);
}
```

`DeviceTrackerTransition` is a `readonly record struct` — stack-allocated, no heap
pressure, structural equality for free.

---

## Concerns

### 1. Fourth notification surface

`DeviceTracker` would have four notification surfaces. The existing three already cover
all use cases; the new events are purely ergonomic. Each additional surface is API
surface that must be documented, tested, and maintained indefinitely.

**Mitigation:** The four events are the only surface that requires zero ceremony for
the most common cases (activate/deactivate). The existing surfaces remain for consumers
who need the full state or Rx composition. This is additive, not replacing anything.

### 2. New public type: `DeviceTrackerTransition`

Introducing `DeviceTrackerTransition` adds one new public type to the API surface.
It is minimal — two `DeviceTrackerState` fields, no methods beyond record equality —
but it must be documented and is a permanent commitment.

**Mitigation:** The type is a `readonly record struct`, so it costs nothing at runtime
(stack-allocated, no boxing in normal event handler usage). Its existence is well-justified:
it is the only way to deliver both snapshots atomically as a single event argument. The
alternative of two separate event args or overloaded constructors would be more confusing.

~~**Alternative:** Introduce a `DeviceTrackerTransition` record carrying both `Before`~~
~~and `After` snapshots as the argument for all four events.~~
**Resolved:** `DeviceTrackerTransition` is adopted as the event argument for all four
edge events. Using only `DeviceTrackerState` (after-only) was rejected because `Before`
is genuinely essential on `Disappeared` and `Deactivated` — it carries the last-known
device snapshot that `After.Device` no longer holds.

### 3. Naming collision with `DeviceWatcher`

`DeviceWatcher` already exposes `Appeared`, `Disappeared`, `Activated`, `Deactivated`
with the same names. The argument type differs (`DeviceChangeEventArgs` on the watcher,
`DeviceTrackerState` on the tracker), which may cause confusion.

**Mitigation:** The naming symmetry is intentional — the tracker events are the
resolved/filtered view of the same conceptual transitions. The different argument types
are expected: the watcher delivers the raw `DeviceInfo` that caused the transition; the
tracker delivers the resolved tracker state after it.

### 4. `IsAmbiguous` interaction

`DeviceTracker` has an ambiguity latch: when multiple devices match the top profile,
`IsAmbiguous` is set and `Device` is null. An `Activated` event should not fire while
the tracker is ambiguous, even if individually a device became active. The implementation
must guard against firing `Activated` when `after.IsAmbiguous == true`.

### 5. Multi-profile fallback transitions

With multiple profiles, a device on the fallback profile being active can transition
to Absent (falls back) and simultaneously the primary profile's device Activates. This
produces a `Deactivated` followed immediately by `Activated` in the same `NotifyChanges`
call. Consumers must handle rapid back-to-back edge events gracefully.

---

## Alternatives Considered

### A. `Transitioned` event carrying `DeviceTrackerTransition` — single event

```csharp
public event EventHandler<DeviceTrackerTransition>? Transitioned;
```

**Pro:** Single event, full context, minimal API surface.  
**Con:** Consumers must still diff `Before` and `After` to detect specific edges —
only marginally less ceremony than `StateChanged` with a local `_previous`. Does not
deliver the simple activation pattern this ADR is motivated by. The four named events
are far more discoverable: a consumer looking for "when does the device turn on?" finds
`Activated` immediately via IntelliSense.

**Rejected.** `DeviceTrackerTransition` is adopted as the *argument type*, not as a
replacement for the four named events.

### B. Do nothing — document the `PropertyChanged` pattern

Document that `PropertyChanged` on `"IsActive"` is the canonical edge detector.

**Pro:** Zero new API.  
**Con:** Every consumer writes the same boilerplate. Discoverability is poor —
`PropertyChanged` surfaces a string, not a type-safe event name. Torn reads remain
possible when consumers read multiple properties in the handler.

**Rejected.**

### C. Keep `StateChanged` but change its argument to `(Before, After)`

Change `StateChanged` to `EventHandler<DeviceTrackerTransition>`.

**Pro:** All edges derivable from one event with full context.  
**Con:** Breaking change. Existing consumers using `(_, state) => Use(state.Device)`
must be updated even though they don't need `Before`. The majority of `StateChanged`
consumers don't do edge detection.

**Rejected.**

### D. `IObservable<bool>` properties for `IsActive` / `IsPresent`

```csharp
public IObservable<bool> IsActiveObservable { get; }
```

**Pro:** Rx-composable; `DistinctUntilChanged().Where(x => x)` is a one-liner.  
**Con:** Hard Rx dependency on the core library, or requires a separate
`Periphery.Reactive` package. Already achievable via the existing
`IObservable<DeviceTrackerState>` surface with `.Select(s => s.IsActive).DistinctUntilChanged()`.

**Rejected.**

### E. After-only `DeviceTrackerState` as event argument (no `Before`)

Use `EventHandler<DeviceTrackerState>` for all four events, passing only the
post-transition snapshot.

**Pro:** No new public type; reuses `DeviceTrackerState`.  
**Con:** `After.Device` is null on `Disappeared` and downgraded on `Deactivated`.
The last-known device is lost unless the consumer cached it. `Before.Device` is the
natural and expected payload for those two events.

**Rejected** in favour of `DeviceTrackerTransition`.

---

## Decision

**Accepted.** Add four edge events to `DeviceTracker` — `Appeared`, `Disappeared`,
`Activated`, `Deactivated` — each carrying a `DeviceTrackerTransition` argument with
both `Before` and `After` `DeviceTrackerState` snapshots captured atomically.

The open question from the initial draft (whether to use `DeviceTrackerState` or a new
`DeviceTrackerTransition` type) is resolved in favour of `DeviceTrackerTransition`.
The `Before` snapshot is essential on `Disappeared` and `Deactivated` — it is the
only place the last-known device lives after the transition, without forcing consumers
to maintain their own cache.

---

## Consequences

- New public type: `DeviceTrackerTransition` (`readonly record struct` — no heap pressure).
- `DeviceTracker` gains four public events: `Appeared`, `Disappeared`, `Activated`, `Deactivated`.
- `NotifyChanges` gains one `DeviceTrackerTransition` construction and four edge-condition
  checks — all O(1), one stack allocation per transition.
- `DeviceTracker` XML doc summary updated to list all four notification surfaces.
- Test coverage required:
  - USB: `Appeared` and `Activated` fire simultaneously; `Before` is Absent, `After` is Active.
  - Bluetooth: `Appeared` fires first (`Before` Absent → `After` Present); `Activated` fires
    later (`Before` Present → `After` Active) without a redundant `Appeared`.
  - `Disappeared`: `Before.Device` is the last-known snapshot; `After.Device` is null.
  - `Deactivated`: `Before.IsActive` is true; `After.IsActive` is false.
  - `Unbind`: `Disappeared` and `Deactivated` fire for any device that was present/active
    at watcher disposal.
  - Multi-profile: back-to-back `Deactivated` + `Activated` in one `NotifyChanges` call.
- No breaking changes to existing surfaces (`StateChanged`, `INotifyPropertyChanged`,
  `IObservable<DeviceTrackerState>`).
