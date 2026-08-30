---
title: "ADR-0074: DeviceRoleGroup — Exclusive Role Assignment Over a Shared Candidate Pool"
status: "Proposed"
status_note: "Not implemented - there is no `DeviceRoleGroup` type."
date: "2026-08-03"
authors: "@charles8051"
tags: ["architecture", "decision", "tracking", "device-role", "multi-device", "cross-tracker", "camera"]
supersedes: ""
superseded_by: ""
depends_on: "ADR-0006 (single-device profile resolution), ADR-0034 (MultiDeviceTracker), ADR-0052 (pure-core pattern)"
---

# ADR-0074: DeviceRoleGroup — Exclusive Role Assignment Over a Shared Candidate Pool

## Status

This ADR intentionally covers only **in-process, cooperative role assignment**
across multiple `DeviceTracker`s. A related and harder problem — a genuinely
different OS process (or a stray prior instance) opening a handle to the same
physical device concurrently — is out of scope here by design; see
[Open Questions §1](#open-questions). That piece needs dedicated,
cross-platform investigation before it's designed, let alone decided.

---

## Context

the kiosk runs two camera roles, `FrontCamera` and `InternalCamera`,
each backed by its own `Periphery.DeviceTracker`. Both roles are the same
physical camera model (Realtek `0BDA:3035`), so their base config profiles are
byte-identical:

```json
"FrontCamera":    { "Profiles": { "primary": { "Category": "Camera", "VendorId": "0BDA", "ProductId": "3035" } } },
"InternalCamera": { "Profiles": { "primary": { "Category": "Camera", "VendorId": "0BDA", "ProductId": "3035" } } }
```

The kiosk consumer's `README.md` already documents this as "the
canonical example" of hardware with no model-stable discriminator, and flags
LED strips and multi-role HID as other candidates for the same exposure — this
is not a camera-specific problem.

**Why this produces a collision, not just a race.** `DeviceWatcher` fans out
every device event to every registered tracker whose filter matches
(`DeviceWatcher.cs` `FanOutAppeared`/`FanOutActivated`/etc. — unconditional
`foreach (var tracker in _trackers)`). Each `DeviceTracker`'s per-profile latch
(ADR-0006 §9) is scoped to *that tracker's own profiles* — it has no
visibility into sibling trackers. When two trackers share an identical filter
and two matching physical devices exist, **both trackers see both device
events**, and each independently latches to whichever device arrives first.
The result isn't "maybe they pick different devices, maybe not" — it's that N
trackers sharing one filter tend to *collapse onto one device*, since the
first arrival satisfies every tracker's latch simultaneously and every
subsequent arrival is rejected by all of them (already latched). In
The kiosk consumer concretely: `FrontCameraService` and `InternalCameraService` end
up opening two independent `CameraSession`s against the *same* physical
camera, and the second physical camera goes untracked.

**Today's workaround lives entirely in the consumer, and only partially
works.** The kiosk consumer's `CameraRoleProvisioner` / `TrackerRoleProvisionerBase` /
`OperatorProfileStack` / `ProfileDefinition` hand-roll a persisted,
priority-ordered profile stack per role, with a "steal the same pin from other
roles" exclusivity check (`OperatorProfileStack.RemoveFromOtherRoles`) that
only runs **at save time**, inside an operator-facing debug-dashboard flow. It
does nothing for the un-pinned base profile, and it does nothing until an
operator has explicitly run that flow on that specific physical kiosk (the
pinned discriminator is `DeviceInfo.Id`, which includes the USB-port instance
suffix, so it doesn't survive a re-plug either). A fresh kiosk, a dev machine,
or a station where the step was skipped has no protection at all.

This is a gap in the library, not a gap in the kiosk consumer's diligence: nothing in
Periphery lets a caller express "these N trackers compete for slots in one
candidate pool, and I want that arbitrated, observable, and — when it can't be
resolved automatically — pinnable by discriminator." That's the primitive this
ADR proposes.

---

## Decision Drivers

- **Reuse existing primitives.** Build on `MultiDeviceTracker` (ADR-0034,
  Accepted) — which already knows how to enumerate every device matching a
  broad filter and hand back a persistent child `DeviceTracker` per device —
  rather than modifying `DeviceWatcher`'s fan-out or `DeviceTracker`'s
  per-tracker latch. Both of those are the most heavily depended-on types in
  the library; zero changes to either is a hard constraint, not a preference.
- **No provider changes.** Same constraint every prior tracking ADR has held
  to (ADR-0006, ADR-0034).
- **Pure-core resolution.** The role-assignment algorithm must be the same
  shape as `DeviceTrackerResolution` (ADR-0052) and the per-profile latch
  (ADR-0006 §9): `(state, event) → state'`, swapped wholesale under a lock,
  exhaustively unit-testable against a synthetic device stream with no
  hardware and no clock.
- **Conflict is structured state, not silence.** Mirror `IsAmbiguous` /
  `AmbiguousDevices` (ADR-0006 §4) — a role that can't be resolved
  automatically must say so, observably, rather than the group silently
  collapsing two roles onto one device (today's actual failure) or dropping a
  candidate on the floor (today's actual side effect).
- **Persisted pinning is a first-class library concept, not an app-layer
  reinvention.** The kiosk consumer's `ProfileDefinition` / `OperatorProfileStack` is
  already, in substance, "a named discriminator with priority and cross-role
  exclusivity" — promote that shape into Periphery so every consumer with an
  identical-hardware disambiguation problem gets it for free, instead of each
  one hand-rolling its own stack.
- **Cross-process device ownership is explicitly out of scope.** Flagged under
  Open Questions, not designed here. It has real cross-platform variance
  (Windows Media Foundation's Frame Server can silently multiplex a UVC stream
  across processes depending on the driver, with no guaranteed
  `PREEMPTED`/`ACCESSDENIED` signal; Linux V4L2 enforces exclusive `open()` by
  default for most drivers; macOS AVFoundation has its own separate story) and
  deserves dedicated investigation on real hardware per platform before any
  mechanism is committed to.

---

## Decision

### 1. New type: `DeviceRoleGroup`

Wraps one `MultiDeviceTracker` — the candidate pool, "every device matching
this broad filter" — plus an ordered set of named `DeviceRole`s that compete
for slots in that pool.

```csharp
/// <remarks>
/// In-process only. Arbitrates roles across <see cref="DeviceTracker"/>s
/// created from ONE <see cref="MultiDeviceTracker"/> candidate pool inside
/// this process. It has no visibility into, and makes no claim about, a
/// different process opening a handle to the same physical device — see
/// ADR-0074 Open Question 1.
/// </remarks>
public sealed class DeviceRoleGroup : IDisposable
{
    // Borrows `candidates` — DeviceRoleGroup does not own or dispose it.
    // The caller controls the MultiDeviceTracker's lifetime, same as it
    // would if using it standalone.
    public DeviceRoleGroup(MultiDeviceTracker candidates, string? name = null);

    // Adding a role immediately re-resolves against the current pool state
    // (existing candidates aren't required to re-arrive). No RemoveRole in
    // this version — roles are expected to be configured once at startup,
    // matching every known consumer's actual usage (the kiosk consumer's Front/
    // Internal camera roles are fixed for the process lifetime).
    public DeviceRole AddRole(string name, int priority = 0);
    public IReadOnlyList<DeviceRole> Roles { get; }

    /// Pins <paramref name="roleName"/> to the device satisfying <paramref name="pin"/>.
    /// Always succeeds and stores the pin even if no candidate currently
    /// matches it — Device resolves to null until a match appears in the
    /// pool (see §2's "pinned-but-absent" case). Automatically clears the
    /// same pin from every other role in the group (§3).
    public void Pin(string roleName, DevicePin pin);
    public void Unpin(string roleName);

    /// Atomic snapshot of every role's resolved state.
    public event EventHandler<DeviceRoleGroupState>? StateChanged;

    /// Unsubscribes from the candidate pool. Does not dispose it.
    public void Dispose();
}

/// <summary>
/// Immutable per-resolution snapshot of one role, replaced wholesale on
/// every state transition — the public view over
/// <see cref="DeviceRoleGroupResolution"/>. Never mutated in place; to
/// change a role's pin, call <see cref="DeviceRoleGroup.Pin"/> on the
/// owning group, not a method on this snapshot.
/// </summary>
public sealed record DeviceRole
{
    public string Name { get; }
    public int Priority { get; }

    public DeviceInfo? Device { get; }                          // resolved, or null
    public bool IsPinned { get; }                                // has an explicit discriminator
    public bool IsAmbiguous { get; }                             // see §2
    public IReadOnlyList<DeviceInfo> UnclaimedCandidates { get; } // see §2
}
```

**Construction and validation.** `DeviceRoleGroup` resolves immediately on
construction against whatever the `MultiDeviceTracker` already has in its
pool at that moment — a consumer that constructs the group after devices have
already arrived sees `Roles` populated on the first read, not empty-until-the-
next-event. `StateChanged` fires once for that initial resolution (consistent
with `DeviceTracker.Subscribe`'s "deliver current state immediately" contract
— ADR-0006). Argument validation follows the rest of the library's existing
convention (`DeviceTracker`'s constructors, `ArgumentNullException.ThrowIfNull`
throughout `DeviceFilter`): null `candidates` in the constructor, null/empty
`name` in `AddRole`, and null `pin` in `Pin` all throw `ArgumentNullException`/
`ArgumentException` synchronously, before any state changes. `Pin`/`Unpin`
with a `roleName` that doesn't match any added role throws
`InvalidOperationException` (matching `DeviceWatcher.AddTracker`'s pattern of
failing loud on an unknown name rather than silently no-op'ing).

### 2. Resolution algorithm (pure core)

Generalizes ADR-0006 §9's per-profile latch from "scoped to one tracker's
profiles" to "scoped to one group's roles," keeping the identical soft-latch
shape. Pseudocode below writes `role.Device = ...` for readability; the real
implementation computes a fresh, fully-populated `DeviceRole` per role and
assigns the *result* — the immutable snapshots described in §1 are never
mutated in place, matching `DeviceTrackerResolution` (ADR-0052). "Priority
order" is a total order, not just a partial one: primarily by `Priority`
ascending, and roles sharing a `Priority` value break the tie by `AddRole`
call order (insertion order) — the same deterministic-by-registration-order
rule `DeviceWatcher._trackers` already relies on for its own fan-out. Two
processes given the same configuration therefore always resolve identically:

```
# Pass 1 — pinned roles. Two DIFFERENT pins can resolve to the same physical
# device (e.g. one role pinned by SerialNumber, another by ContainerId) —
# that is exactly the collision this ADR exists to prevent, so it is
# detected here rather than assumed away by "pins are exclusive."
resolved_by_device = {}   # DeviceInfo.Id -> role, for collision detection across DIFFERENT pins
for each pinned role in priority order:
    candidate = the one pool candidate satisfying the pin's predicate (if present)
    if candidate is null:
        role.Device = null              # pinned-but-absent — see §4 below
    else if candidate.Id already in resolved_by_device:
        role.Device = null              # collision: two different pins, one device
        role.IsAmbiguous = true
        resolved_by_device[candidate.Id].IsAmbiguous = true   # the earlier claimant too
    else:
        role.Device = candidate
        resolved_by_device[candidate.Id] = role

# Pass 2 — unpinned roles draw from whatever the pinned pass didn't claim.
for each unpinned role in priority order:
    candidate = first pool candidate not in resolved_by_device, in
                MultiDeviceTracker.DeviceAdded arrival order
    if found:
        role.Device = candidate   # soft latch by DeviceInfo.Id
        resolved_by_device[candidate.Id] = role
    else:
        role.Device = null        # role-starved: fewer candidates than unpinned roles

# Pass 3 — conflict signal for the unresolved leftovers.
unclaimed = pool candidates not in resolved_by_device
for each unpinned role with Device == null:
    role.IsAmbiguous = unclaimed.Count > 0   # true = oversubscribed pool, false = role-starved
    role.UnclaimedCandidates = unclaimed
```

**`IsAmbiguous` on an unresolved role means "at least one candidate exists that
nothing else claimed"** — an operator action (unplug the extra one, or pin it
elsewhere) can fix it. `IsAmbiguous == false` with `Device == null` means the
pool is simply short a device for that role — no candidate exists to point at.
This directly resolves the pinned-but-absent vs. role-starved distinction
raised in Open Question 4 below.

A latch releases (soft, per ADR-0006 §9) when its claimed device leaves the
pool (`MultiDeviceTracker`'s child tracker goes `Absent`), freeing the slot for
the next arrival. This whole pass is a pure value transform — `DeviceRoleGroup`
is the thin shell that re-runs it under one lock on every
`MultiDeviceTracker.DeviceAdded` / child `StateChanged` / `Pin`/`Unpin` call,
identical in shape to how `DeviceTracker` drives `DeviceTrackerResolution`
today (ADR-0052). The lock is private to `DeviceRoleGroup` (mirrors
`DeviceTracker`'s private `_lock`): `Pin`, `Unpin`, `AddRole`, and reads of
`Roles` / any `DeviceRole` property are safe from any thread; a `Roles` read
always returns a fully-resolved, self-consistent snapshot — never a partial
in-progress pass.

### 3. `DevicePin` — pinning promoted out of the kiosk consumer

A small, persistence-agnostic type: a named discriminator over the BCL-typed
fields `DeviceInfo` already exposes — no stringly-typed matching:

```csharp
public sealed record DevicePin
{
    public static DevicePin ById(DeviceId id) => ...;
    public static DevicePin ByContainer(Guid containerId) => ...;
    public static DevicePin BySerialNumber(string serialNumber) => ...;
    public static DevicePin Custom(Func<DeviceInfo, bool> predicate, string? label = null) => ...;
}
```

**`Custom` pins are session-only.** A delegate can't be serialized, and two
predicates that are semantically equivalent don't compare equal under the
record's value equality — so a consumer who persists a `Custom` pin and
reconstructs it on the next run gets a `DevicePin` that doesn't `==` the
original, which matters for the exclusivity dedup in the next paragraph. The
three factory methods (`ById`, `ByContainer`, `BySerialNumber`) round-trip
through config cleanly (this is what the kiosk consumer's `ProfileDefinition` already
persists today) and should cover every real deployment case; `Custom` exists
for one-off/programmatic use within a single process run, not for anything a
consumer expects to survive a restart.

No `Priority` field on `DevicePin` itself — a role holds exactly one active
pin at a time (`Pin`/`Unpin` replace it wholesale), so there is nothing to
order *within* a role. `DeviceRole.Priority` (§1) already orders roles
against each other for the unpinned pool in §2; a second, role-scoped
priority on the pin would have no defined effect and was cut rather than left
unspecified.

`DeviceRoleGroup` enforces cross-role pin exclusivity as a **library
invariant**, not a save-time convention the consumer has to remember to
implement: calling `Pin(roleName, ...)` clears the identical pin (by value
equality — `DevicePin` is a record) from every other role in the same group
automatically (mirrors the kiosk consumer's `OperatorProfileStack.RemoveFromOtherRoles`,
but guaranteed by the type instead of hand-rolled per consumer). Cross-role
exclusivity only dedupes *identical* pins; §2 Pass 1 separately handles two
*different* pins that happen to resolve to the same device.

Persistence (writing pins to `appsettings.json`, a local overlay, wherever) is
still the consumer's concern — `DeviceRoleGroup` takes and holds `DevicePin`
values in memory only, consistent with Periphery's existing
storage-agnostic stance. A consumer rehydrates pins from its own config store
at startup and calls `Pin(...)`, the same way it constructs `DeviceProfile`s
from config today.

### 4. The kiosk consumer's shape after adoption (illustrative, not part of this ADR)

`CameraRoleProvisioner` shrinks to: one `MultiDeviceTracker` over
`Category=Camera`, one `DeviceRoleGroup` with "Front"/"Internal" roles, and a
thin adapter between `IKioskOptions<DeviceTrackingOptions>` and
`Pin`/`Unpin`. `TrackerRoleProvisionerBase`'s hand-rolled exclusivity and
priority-stack logic (`OperatorProfileStack`) is deleted — it becomes a
Periphery guarantee instead of an app-layer one. The health graph gets a real,
always-on `IsAmbiguous` signal instead of depending on an operator having
remembered to provision the kiosk.

---

## Blast Radius

| Type | Change | Scope |
|---|---|---|
| `DeviceRoleGroup` | **New type.** `IDisposable` shell — borrows a `MultiDeviceTracker`, owns `Pin`/`Unpin`/`AddRole`, `Roles`, `StateChanged`. | `Periphery/DeviceRoleGroup.cs` (new) |
| `DeviceRole` | **New type.** Immutable per-resolution snapshot record (no mutating methods). | `Periphery/DeviceRole.cs` (new) |
| `DeviceRoleGroupResolution` | **New type.** Pure core — the algorithm in §2. | `Periphery/DeviceRoleGroupResolution.cs` (new) |
| `DevicePin` | **New type.** BCL-typed discriminator record (`DeviceId` / `Guid` / `string` / predicate factories). | `Periphery/DevicePin.cs` (new) |
| `MultiDeviceTracker` | **No changes.** Consumed as-is via `Trackers`/`DeviceAdded`. | — |
| `DeviceWatcher` | **No changes.** | — |
| `DeviceTracker` | **No changes.** | — |
| `DeviceFilter` / `DeviceProfile` | **No changes.** | — |
| Platform providers | **No changes.** | — |
| `Periphery.Tests` | New `DeviceRoleGroupTests.cs`: single-role resolution, priority ordering, pin exclusivity across roles, ambiguity entry/exit as candidates appear/disappear, latch release on disconnect, pinned-but-absent-device state. | `Periphery.Tests/Tracker/DeviceRoleGroupTests.cs` (new) |
| `docs/ARCHITECTURE.md` | Add `DeviceRoleGroup` section alongside `MultiDeviceTracker`. | Minor addition |
| the kiosk consumer (downstream, separate repo) | `CameraRoleProvisioner` / `TrackerRoleProvisionerBase` / `OperatorProfileStack` / `ProfileDefinition` become thin adapters over `DeviceRoleGroup` + `DevicePin`; most of their current logic is deleted. Tracked as follow-up work in that repo once this ADR is accepted, not part of this ADR's scope. | Cross-repo, informational only |

---

## Open Questions

1. **Cross-process device ownership.** Deliberately deferred (see Status).
   Needs investigation, per platform, before it's even designable:
   - Does relying on driver-surfaced errors suffice, or is an explicit lock
     required? Windows Media Foundation's `MfCameraBackend` already surfaces
     `MF_E_VIDEO_RECORDING_DEVICE_PREEMPTED` and `E_ACCESSDENIED`, but the
     Windows Camera Frame Server can multiplex a UVC stream across processes
     for many drivers with **no error at all** — "it didn't throw" is not
     evidence of exclusivity.
   - Linux V4L2 devices are exclusive-`open()` by default for most drivers
     (second open fails `EBUSY`) — closer to a real guarantee, but driver-
     dependent and not audited here.
   - macOS AVFoundation's arbitration model hasn't been investigated at all.
   - If a lock ends up warranted, the primitive differs completely per
     platform (named `Mutex` on Windows vs. `flock`/lockfile or a named
     semaphore on Linux/macOS) — there's no portable one-liner, which is
     exactly why this shouldn't be folded into the same decision as the
     in-process piece. Should become its own ADR once explored on real
     hardware on each platform.
2. **HRESULT triage independent of the above.** `MfCameraBackend`'s
   `E_ACCESSDENIED` handling is currently commented as "privacy settings may
   be blocking access" and doesn't distinguish that from "already claimed
   elsewhere"; `MF_E_VIDEO_RECORDING_DEVICE_PREEMPTED` is folded into the
   generic `CameraDeviceLostException` reconnect path. Splitting these into a
   distinct, actionable exception is cheap, low-risk, doesn't require
   resolving Open Question 1, and could land independently.
3. **Naming.** `DeviceRoleGroup` / `DeviceRole` / `DevicePin` are placeholders.
   ADR-0034 shipped as `MultiDeviceTracker` despite drafting as
   `DeviceGroupTracker` — expect similar drift here; not a blocking concern.
4. **Pinned-but-absent vs. no-pin-and-oversubscribed.** ~~Should `IsAmbiguous`
   distinguish these?~~ **Resolved in §2 Pass 3:** a pinned-but-absent role has
   `Device == null` and `IsAmbiguous == false` (nothing else is contending for
   its slot, it just isn't plugged in — "plug in the one you meant"); an
   oversubscribed unpinned role has `Device == null` and `IsAmbiguous == true`
   with `UnclaimedCandidates` non-empty ("unplug one, or pin the rest").
5. **Auto-steal vs. opt-in steal.** `Pin()` is sketched as unconditionally
   stealing the same pin from other roles (matching the kiosk consumer's existing
   UX). Is that the right default for a library API, or should stealing
   require `Pin(pin, steal: true)` so a consumer can't be surprised by a
   role losing its device as a side effect of configuring a different one?

---

## Consequences

### Positive

- Removes the collision at its source instead of leaving it to app-layer
  discipline — any consumer with N roles competing for identical hardware
  gets exclusivity and conflict visibility for free.
- Zero changes to `DeviceWatcher` or `DeviceTracker` — the two most
  heavily-depended-on types in the library are untouched; the new
  functionality is purely additive and composes over ADR-0034's already-
  accepted primitive.
- Matches the functional-core/imperative-shell convention already
  proven by `DeviceTrackerResolution` — one more pure, swap-under-lock
  resolution value, not a new concurrency primitive.

### Negative / Risks

- A second pure-core resolution type alongside `DeviceTrackerResolution`, with
  a similar soft-latch shape — worth checking during implementation whether
  the latch logic factors into a shared internal helper instead of being
  duplicated.
- Visibility is not prevention. A kiosk still ships with unpinned roles until
  something (an operator, an install script) provisions them; `IsAmbiguous`
  makes that state observable and health-graph-reportable, but a consumer that
  never checks it is no better off than today.
- Explicitly does not address cross-process contention (Open Question 1). A
  consumer reading "exclusive role assignment" could assume a stronger
  guarantee than this ADR actually provides — the docs for `DeviceRoleGroup`
  need to say so plainly.

---

## References

- `docs/adr/0006-device-profile-single-device-resolution.md` — per-profile
  latch and `IsAmbiguous` pattern this ADR generalizes across trackers.
- `docs/adr/0034-device-group-tracker.md` — `MultiDeviceTracker` (shipped
  name), the candidate-pool primitive this ADR builds on unmodified.
- `docs/adr/0052-periphery-treehopper-pure-core.md` — pure-core /
  swap-under-lock pattern this ADR's resolution type follows.
- `Periphery/DeviceTracker.cs` — existing per-tracker resolution this ADR does
  not modify.
- `Periphery/MultiDeviceTracker.cs` — existing candidate-pool tracker this ADR
  wraps.
- `Periphery/Windows/MfCameraBackend.cs` — source of the
  `E_ACCESSDENIED` / `MF_E_VIDEO_RECORDING_DEVICE_PREEMPTED` handling
  referenced in Open Question 2.
- The kiosk consumer's `README.md` — "Device tracking — profile
  authoring guidance," documents the Front/Internal camera case as the
  motivating example.
- The kiosk consumer's `Services/DeviceTracking/TrackerRoleProvisionerBase.cs`,
  `OperatorProfileStack.cs`, `ProfileDefinition.cs`,
  `Services/Camera/CameraRoleProvisioner.cs` — the app-layer workaround this
  ADR proposes to obsolete.
