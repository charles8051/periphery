---
title: "ADR-0034: DeviceGroupTracker — Dynamic Multi-Device Tracking"
status: "Accepted"
status_note: "Shipped as **`MultiDeviceTracker`**, not `DeviceGroupTracker` - the name in this ADR was never used in code. Added via `DeviceWatcher.AddMultiTracker`."
date: "2026-04-02"
authors: "@charles8051 (design)"
tags: ["architecture", "decision", "tracking", "multi-device", "device-group"]
supersedes: ""
superseded_by: ""
depends_on: "ADR-0006 (single-device resolution), ADR-0004 (two-level state model), ADR-0029 (edge events), ADR-0032 (session host)"
---

# ADR-0034: DeviceGroupTracker — Dynamic Multi-Device Tracking

---

## Context

`DeviceTracker` (ADR-0006) is designed around **single-device resolution**. Its
per-profile latch mechanism claims exactly one device ID per profile, and
`Resolve()` produces a single `Device` reference. Multi-profile support means
"priority-ordered fallback among candidates for the same slot," not "track N
devices simultaneously." This is the correct model for known, specific devices
— a barcode scanner, a particular mouse, a production controller.

A second class of use cases has emerged where the consumer does not know the
devices in advance and wants to track **all devices matching a filter**:

- Track all connected monitors and react when any is added or removed.
- Track all keyboards and create a `DeviceProxy` for each one.
- Track all serial ports of a certain VID/PID and spin up a session per device.
- Build a dashboard showing every Bluetooth device in range.

Today the only way to achieve this is to subscribe to `DeviceWatcher` global
events, manually maintain a dictionary of device IDs, and hand-roll lifecycle
management. This is error-prone and duplicates logic that `DeviceTracker`,
`DeviceProxy`, and `DeviceSessionHost` already encapsulate.

The library needs a first-class primitive that **dynamically creates and manages
a set of `DeviceTracker` instances** — one per matching device — so that the
entire existing handle/session/facade stack composes naturally over a
dynamically-sized device collection.

---

## Decision Drivers

- **Reuse existing primitives** — each device in the group should be a standard
  `DeviceTracker` so that `DeviceProxy` and `DeviceSessionHost`
  work without modification.
- **No changes to `DeviceTracker`** — the single-device latch model is correct
  for its purpose; group semantics belong in a separate type.
- **No provider changes** — OS providers and `IDeviceMonitorProvider` are
  untouched. Only `DeviceWatcher` fan-out is extended.
- **Persistent child trackers** — once a child tracker is created, it must
  survive disconnect/disappear cycles so that consumers holding a reference
  (e.g., via `DeviceProxy`) retain their reconnect path.
- **Consumer-controlled removal** — the group decides when to *start* tracking
  a device; the consumer decides when to *stop*.
- **Multiple independent groups on one watcher** — a single watcher should
  support "all Monitors" and "all Keyboards" simultaneously without
  interference.

---

## Decision

### 1. New Type: `DeviceGroupTracker`

A `DeviceGroupTracker` holds a `DeviceFilter` and dynamically creates child
`DeviceTracker` instances — one per unique `DeviceInfo.Id` — as matching
devices appear.

```csharp
public sealed class DeviceGroupTracker
{
    // Construction — same filter delegate pattern as DeviceTracker
    public DeviceGroupTracker(Action<DeviceFilter> configure, string? name = null);

    // Identity
    public string? Name { get; }

    // Observable collection of child trackers, keyed by DeviceInfo.Id
    public IReadOnlyDictionary<string, DeviceTracker> Trackers { get; }

    // Convenience projections
    public int Count { get; }
    public bool HasAny { get; }

    // Edge events
    public event EventHandler<DeviceTracker>? DeviceAdded;

    // IObservable — pushes state changes from any child tracker
    public IDisposable Subscribe(IObserver<DeviceTrackerState> observer);
}
```

### 2. Registration on `DeviceWatcher`

Registration follows the same pattern as `AddTracker`:

```csharp
// On DeviceWatcher
public DeviceGroupTracker AddGroupTracker(
    Action<DeviceFilter> configure, string? name = null);

public DeviceWatcher AddGroupTracker(DeviceGroupTracker groupTracker);
```

Both enforce the existing pre-Start invariant (`ThrowIfStarted`). The group
tracker is bound to the watcher and receives fan-out events alongside regular
trackers.

### 3. Child Tracker Lifecycle — Persistent Once Latched

Child trackers are **persistent**. Once a device is seen for the first time,
its child tracker remains in the group across disconnect and disappear cycles.
This preserves the reconnect contract that `DeviceProxy`,
and `DeviceSessionHost` depend on.

| Event | Group Behaviour |
|---|---|
| Device appears & matches filter | If no child tracker exists for this `DeviceInfo.Id`: create one, add to `Trackers`, fire `DeviceAdded`. Forward `OnDeviceAppeared` to child. |
| Device activates | Forward `OnDeviceConnected` to existing child. |
| Device deactivates | Forward `OnDeviceDisconnected` to existing child. Child transitions to Present or Absent — stays in group. |
| Device disappears | Forward `OnDeviceDisappeared` to existing child. Child transitions to Absent — **stays in group**. |
| Device reappears | Forward `OnDeviceAppeared` to existing child. Child transitions back to Present/Active. Handle reconnects naturally. |
| Group disposed (watcher disposal) | All children unbound, `Trackers` cleared. |

**Why persistent?** The entire `DeviceProxy` / `DeviceSessionHost` stack is
built around surviving disconnects. A consumer who receives a `DeviceTracker`
from `DeviceAdded` and builds a `DeviceProxy` on it expects that handle to
reconnect when the device returns. If the group removed the child on
`Disappeared`, the handle's reconnect path would be severed permanently —
breaking the library's core lifecycle contract.

**Monotonic growth.** Over a long-running application, the set of "ever-seen"
devices grows monotonically. This is acceptable in practice — the number of
distinct device IDs matching "all Keyboards" over an application's lifetime
is small.

### 4. Internal Fan-Out in `DeviceWatcher`

`DeviceWatcher` gains a `_groupTrackers` list alongside the existing
`_trackers` list. The four `FanOut*` methods are extended:

```csharp
private void FanOutAppeared(DeviceInfo device)
{
    foreach (var tracker in _trackers)
        if (tracker.Matches(device)) tracker.OnDeviceAppeared(device);

    foreach (var group in _groupTrackers)
        group.OnDeviceAppeared(device);
}
// Same pattern for FanOutActivated, FanOutDeactivated, FanOutDisappeared,
// FanOutPropertyChanged.
```

Inside `DeviceGroupTracker`, each `OnDevice*` method checks the group filter,
creates the child tracker if needed, and delegates to the child's existing
internal methods (`OnDeviceAppeared`, `OnDeviceConnected`, etc.).

### 5. Child Tracker Identity Filter

Each child tracker is constructed with a filter that combines the group's
filter with an exact `DeviceInfo.Id` match. This ensures the child's
`Matches()` method only accepts events for its specific device, and that the
child's latch mechanism works correctly (single-device resolution within the
child).

```csharp
// Inside DeviceGroupTracker, on first appearance of a device:
var child = new DeviceTracker(
    f => { groupFilter.CopyTo(f); f.WithId(device.Id); },
    name: device.Name ?? device.Id);
```

### 6. Usage Examples

**Basic — track all monitors:**

```csharp
await using var watcher = Devices.Watch();
var monitors = watcher.AddGroupTracker(
    f => f.OfCategory(DeviceCategory.Monitor),
    name: "AllMonitors");

monitors.DeviceAdded += (_, tracker) =>
    Console.WriteLine($"Monitor added: {tracker.Device?.Name}");

await watcher.StartAsync();

// Read current state at any time
foreach (var (id, tracker) in monitors.Trackers)
    Console.WriteLine($"  {tracker.Device?.Name}: {tracker.ActivityStatus}");
```

**With DeviceProxy — auto-create a handle per device:**

```csharp
await using var watcher = Devices.Watch();
var keyboards = watcher.AddGroupTracker(
    f => f.OfCategory(DeviceCategory.Keyboard), name: "Keyboards");

var handles = new ConcurrentDictionary<string, DeviceProxy>();

keyboards.DeviceAdded += (_, tracker) =>
{
    var handle = DeviceProxy.Create(tracker,
        onActivated: async (info, ct) =>
            Console.WriteLine($"Keyboard ready: {info.Name}"),
        onDeactivated: info =>
        {
            Console.WriteLine($"Keyboard disconnected: {info.Name}");
            return Task.CompletedTask;
        });
    handles[tracker.Device!.Id] = handle;
};

await watcher.StartAsync();
```

**With DeviceGroupSessionHost — session per device (convenience):**

```csharp
// Self-contained — owns its own watcher
await using var host = await DeviceGroupSessionHost<SerialSession>.StartAsync(
    configure: f => f.OfCategory(DeviceCategory.Ports).WithUsbId("1A86", "7523"),
    createSession: (info, ct) => Task.FromResult(
        new SerialSession(info.PortName!.Value.Value, 115200)),
    onSessionEnded: s => { s.Dispose(); return Task.CompletedTask; },
    name: "CH340 Ports");

host.SessionHostAdded += (_, sessionHost) =>
    Console.WriteLine($"New session: {sessionHost.DeviceInfo?.Name}");

foreach (var (id, sessionHost) in host.Hosts)
    if (sessionHost.TryGetCurrentSession(out var session))
        session.SendCommand("PING");
```

```csharp
// Shared — borrows an existing group tracker
await using var watcher = Devices.Watch();
var group = watcher.AddGroupTracker(
    f => f.OfCategory(DeviceCategory.Ports).WithUsbId("1A86", "7523"),
    name: "CH340 Ports");
await watcher.StartAsync();

await using var host = DeviceGroupSessionHost<SerialSession>.Create(
    group,
    createSession: (info, ct) => Task.FromResult(
        new SerialSession(info.PortName!.Value.Value, 115200)),
    onSessionEnded: s => { s.Dispose(); return Task.CompletedTask; });
```

**Mixed — known device + dynamic group on one watcher:**

```csharp
await using var watcher = Devices.Watch();

// Known device — static tracker
var scanner = watcher.AddTracker(
    f => f.WithUsbId("05E0", "1200"), name: "BarcodeScanner");

// Unknown devices — dynamic group
var keyboards = watcher.AddGroupTracker(
    f => f.OfCategory(DeviceCategory.Keyboard), name: "Keyboards");

scanner.Activated += (_, _) => Console.WriteLine("Scanner active");
keyboards.DeviceAdded += (_, t) => Console.WriteLine($"Keyboard: {t.Device?.Name}");

await watcher.StartAsync();
```

---

## Blast Radius

| Type | Change | Scope |
|---|---|---|
| `DeviceGroupTracker` | **New type.** `DeviceFilter` + child `DeviceTracker` `ConcurrentDictionary` + `DeviceAdded` event + `IObservable<DeviceTrackerState>` + internal `OnDevice*` routing methods. | `Periphery/DeviceGroupTracker.cs` (new file) |
| `DeviceGroupSessionHost<T>` | **New type.** Orchestrates one `DeviceSessionHost<T>` per child tracker. `StartAsync`/`Create` factories + `SessionHostAdded` event + `Hosts` dictionary. | `Periphery/DeviceGroupSessionHost.cs` (new file) |
| `DeviceWatcher` | Add `_groupTrackers` list. Add `AddGroupTracker` factory + registration overload (2 methods). Extend `FanOutAppeared`, `FanOutActivated`, `FanOutDeactivated`, `FanOutDisappeared`, `FanOutPropertyChanged` with group iteration. Extend `DisposeAsync` to unbind group trackers. Extend `SnapshotCurrentDevicesAsync` to fan out to groups. | `Periphery/DeviceWatcher.cs` — localised additions, no changes to existing tracker logic |
| `DeviceFilter` | Add internal `CopyTo(DeviceFilter target)` method so child trackers can clone the group's filter and layer `WithId` on top. | `Periphery/DeviceFilter.cs` — one internal method |
| `DeviceTracker` | **No changes.** Child trackers are standard instances. | — |
| `DeviceProxy` | **No changes.** Works with any `DeviceTracker`. | — |
| `DeviceProxy<TDevice>` | **No changes.** | — |
| `DeviceProxyBase<T,E>` | **No changes.** | — |
| `DeviceSessionHost<T>` | **No changes.** | — |
| `DeviceSessionHost<T>` | **No changes.** | — |
| `DeviceProfile` | **No changes.** | — |
| `DeviceInfo` | **No changes.** | — |
| `IDeviceProvider` | **No changes.** | — |
| `IDeviceMonitorProvider` | **No changes.** | — |
| Platform providers (Windows, Linux, macOS) | **No changes.** | — |
| `Periphery.Tests` | New test files: `DeviceGroupTrackerTests.cs` (34 tests: child creation, persistence across disappear/reappear, `DeviceAdded` events, fan-out from `DeviceWatcher`, mixed static+group tracking, filter matching, bind/unbind, Bluetooth scenarios, `IObservable`, property change forwarding) and `DeviceGroupSessionHostTests.cs` (8 tests: session host creation, dynamic device addition, session activation, disposal, events). | `Periphery.Tests/Tracker/DeviceGroupTrackerTests.cs`, `Periphery.Tests/Handle/DeviceGroupSessionHostTests.cs` (new files) |
| `docs/ARCHITECTURE.md` | Add section on `DeviceGroupTracker` alongside existing `DeviceTracker` documentation. | Minor addition |
| `README.md` | Add usage example in tracking section. | Minor addition |

---

## Open Questions

1. **`IObservable<T>` surface on the group.** `DeviceGroupTracker` implements
   `IObservable<DeviceTrackerState>`. Subscribers receive state changes from
   any child tracker — use `DeviceTrackerState.Device` to identify which
   device changed. Late subscribers receive the current state of all existing
   children on subscription. **Resolved: implemented.**

2. **`DeviceFilter.CopyTo` vs constructor cloning.** An internal `CopyTo`
   method replays structured properties and lambda predicates onto a target
   filter. This avoids exposing a public constructor and keeps the API surface
   unchanged. **Resolved: implemented via internal `CopyTo`.**

3. **Thread safety of `Trackers` dictionary.** `ConcurrentDictionary`
   internally, exposed as `IReadOnlyDictionary<string, DeviceTracker>`.
   **Resolved: option (a) implemented.**

4. **`DeviceGroupSessionHost<TSession>` convenience type.** Implemented as a
   thin orchestrator over `DeviceGroupTracker` + one
   `DeviceSessionHost<TSession>` per child. Provides `StartAsync` (self-
   contained) and `Create` (borrows existing group tracker) factories,
   matching the pattern of `DeviceSessionHost<T>`. Fires
   `SessionHostAdded` for each new per-device session host.
   **Resolved: implemented.**

5. **`RemoveTracker`.** Not part of the API. The group decides when to start
   tracking; child trackers are persistent and transition through Absent /
   Present / Active. No consumer-initiated removal.
   **Resolved: not implemented.**

6. **Multi-profile children.** Deferred — no current demand. Each child is
   created with a single auto-generated profile (`groupFilter + WithId`).

7. **Watcher-level filter interaction.** When a `DeviceGroupTracker` is
   registered, the watcher uses an unfiltered OS subscription (same as when
   regular trackers are registered). The group's filter is evaluated in-memory
   during fan-out. No additional provider-level changes needed.
   **Resolved: implemented.**

8. **Snapshot ordering guarantee.** During `SnapshotCurrentDevicesAsync`, the
   watcher enumerates all OS-known devices and fans out to groups. A child
   tracker created during the snapshot receives `OnDeviceAppeared` and
   `OnDeviceConnected` in the correct order. The child's internal `_lock`
   serialises concurrent updates. **Resolved: verified by tests.**

---

## Consequences

### Positive

- **Reuses the entire existing stack.** Each device in the group is a standard
  `DeviceTracker`. `DeviceProxy` and `DeviceSessionHost` — all
  work without modification.
- **Zero provider changes.** OS providers, `IDeviceProvider`, and
  `IDeviceMonitorProvider` are completely untouched.
- **Minimal watcher changes.** Fan-out extension is mechanical — iterate
  `_groupTrackers` alongside `_trackers` in existing methods.
- **Preserves reconnect contract.** Persistent child trackers mean handles
  survive disconnect/reconnect cycles, matching the library's core lifecycle
  guarantee.
- **Composable.** Static trackers and dynamic groups coexist on the same
  watcher without interference.

### Negative / Risks

- **Monotonic growth.** `Trackers` grows over time as new devices are seen.
  In practice the count is small (single digits to low tens).
- **Child tracker naming.** Auto-generated child names (`device.Name` or
  `device.Id`) may not be meaningful. Consumers can ignore the name or
  re-label via their own mapping.
- **`DeviceFilter.CopyTo` complexity.** Cloning lambda predicates is
  straightforward (copy the list), but the method must be kept in sync with
  any future structured properties added to `DeviceFilter`. Mitigation: a
  single internal method with a test that verifies round-trip fidelity.

---

## References

- `Periphery/DeviceTracker.cs` — Single-device tracker (unchanged)
- `Periphery/DeviceWatcher.cs` — Fan-out and registration (extended)
- `Periphery/DeviceFilter.cs` — Filter model (minor addition)
- `Periphery/DeviceProxy.cs` — Delegate-configured handle (unchanged)
- `Periphery/DeviceProxyBase.cs` — Handle base class (unchanged)
- `Periphery/DeviceSessionHost.cs` — Session host (unchanged)
- `docs/adr/0006-device-profile-single-device-resolution.md` — Single-device
  resolution model that this ADR complements
- `docs/adr/0004-two-level-device-state-model.md` — IsPresent/IsActive
  orthogonality preserved in child trackers
- `docs/adr/0029-devicetracker-edge-events.md` — Edge events forwarded to
  child trackers unchanged
- `docs/adr/0032-device-session-host.md` — Session host composes over child
  trackers without modification
