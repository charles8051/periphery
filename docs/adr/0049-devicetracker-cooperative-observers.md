---
title: "ADR-0049: DeviceTracker — Cooperative Observers, Not Resource Claims"
status: "Rejected"
date: "2026-05-27"
authors: "@charles8051 (design)"
tags: ["architecture", "decision", "device-tracker", "device-watcher", "observability", "kiosk", "provisioning"]
supersedes: ""
superseded_by: ""
---

# ADR-0049: DeviceTracker — Cooperative Observers, Not Resource Claims

## Context

A deferred-work entry — *"Should `DeviceTracker` claim devices exclusively
across a watcher?"* — recorded a surprise discovered 2026-05-26 while
debugging a two-monitor kiosk binding:
both `MainScreen` and `SignageScreen` trackers, configured with the
broad filter `Category: Monitor`, latched the same physical monitor
instead of one each. This ADR resolves the question.

### The current model (cooperative)

Per ADR-0006 §9, each `DeviceTracker` maintains per-profile latches
keyed on `DeviceInfo.Id`. The latches are **per tracker, per profile**:

```
_presentLatch[profile]:   string?   // latched DeviceInfo.Id for present set
_connectedLatch[profile]: string?   // latched DeviceInfo.Id for connected set
```

`DeviceWatcher.FanOutActivated` delivers every matching event to every
matching tracker — independently. No watcher-level coordination tracks
"which tracker claimed which device":

```csharp
private void FanOutActivated(DeviceInfo device)
{
    foreach (var tracker in _trackers)
    {
        if (tracker.Matches(device))
            tracker.OnDeviceConnected(device);
    }
}
```

When two trackers' filters both match the same device, both call
`OnDeviceConnected`. Inside each tracker the per-profile latch
mechanism activates, claiming the device for that tracker's profile.
Neither tracker is aware of the other.

This is fine for the canonical pattern — each tracker's filter narrows
to exactly one device (VID/PID, container ID, instance ID prefix). It
bites when:

- Two roles share an unnarrowed filter (e.g. `Category: Monitor` for
  both `MainScreen` and `SignageScreen` on a host with two monitors).
- The operator expected "find me any two monitors" from declaring two
  trackers with the same broad filter.
- The downstream consumer needs **different** physical devices per
  role; "both latched the same one" produces an obvious functional
  regression but no log-level error.

### How the kiosk actually solved this

The kiosk consumer's current shape, verified against its `appsettings.json`
and its `Services/DeviceTracking/` sources:

1. **Bootstrap defaults** in `appsettings.json`. Each role narrows by
   monitor model — an `IdStartsWith` on one panel's PnP id for `MainScreen`,
   one on the other's for `SignageScreen`. The
   defaults are model-specific because the kiosk knows which monitor
   model goes in which role *at install time*.
2. **Runtime provisioning** via `IRoleProvisioner` +
   `RoleProvisioningCardViewModel`. A debug-dashboard card uses a
   one-shot `DeviceQuery` with a *broad* filter to enumerate
   candidates ("every Realtek 0BDA:3035 camera the kiosk's hardware
   might have"); operator clicks a candidate; the kiosk writes a
   discriminator (`ContainerId` for cameras) into a transient config
   layer; `DeviceTracker.Reconfigure` (ADR-0046) rebinds the role to
   the new narrow filter live.
3. **Persistent assignment**: `SaveAssignmentAsync` writes the
   discriminator into `appsettings.Local.json`; the transient layer is
   superseded.

The "broad filter, multiple devices" case is handled *at the
provisioning layer*, not inside DeviceTracker. The tracker itself only
ever has a narrow filter binding it to one specific device, by Id /
ContainerId / instance prefix / VID-PID. The query — not a tracker —
is what does the broad enumeration.

This is the load-bearing observation: the kiosk doesn't need
cross-tracker exclusivity, and didn't build a workaround for it. It
built a different architecture (broad query for candidates, narrow
tracker for binding, runtime reconfigure for swaps) that bypasses the
exclusivity question entirely.

### Why this ADR exists

Even with the kiosk's architecture in place, three problems remain:

1. **The cooperative contract isn't documented.** ADR-0006 talks about
   per-profile latching within one tracker; nothing explicitly says
   "trackers don't coordinate with each other." Future consumers who
   write two broad-filter trackers will rediscover the surprise.
2. **The DeviceTracker XML doc is ambiguous.** *"The latch releases
   automatically when the device disconnects, allowing the next
   matching device to claim the slot"* — "next" reads as "next
   matching device globally" when the actual semantic is "next
   matching device for this tracker's profile slot." Reads wrong.
3. **The failure mode is silent.** Two `OfCategory(Monitor)` trackers
   both quietly latch the same monitor; no log line, no warning. The
   first symptom is a downstream consumer noticing the wrong device
   reference.

---

## Decision

**DeviceTracker stays cooperative.** Cross-tracker exclusivity is *not*
implemented in core Periphery. The cooperative contract is documented
explicitly; broad-filter overlap is diagnosed at the watcher level via
a one-line log.

### 1. Pin the cooperative contract

Trackers are **observers**, not resource claims. A device matching
multiple trackers' filters is observed by all matching trackers
independently — there is no first-tracker-wins, no priority ordering
across trackers, no shared registry of "who owns what."

Disambiguation between roles that could share a filter is the
**consumer's** job, expressed via one of two patterns:

- **Narrow filter per role.** Each tracker's filter uniquely
  identifies one device (`WithUsbId(vid, pid)`, `WithSerialNumber`,
  `WithIdStartsWith`, `WithContainerId`). The canonical pattern for
  fixed-hardware deployments.
- **Broad query + narrow trackers + reconfigure.** Use a one-shot
  `DeviceQuery` (or `MultiDeviceTracker`, per ADR-0034) to enumerate
  candidates under a broad filter; have the consumer pick the
  per-role assignment; reconfigure each role's tracker to a narrow
  filter via `DeviceTracker.Reconfigure` (ADR-0046). The canonical
  pattern for runtime-provisioned deployments. The kiosk consumer's
  `IRoleProvisioner` is the reference implementation.

### 2. Update `DeviceTracker`'s XML doc

The current XML on `DeviceTracker` reads:

> The latch releases automatically when the device disconnects,
> allowing the next matching device to claim the slot.

Clarify that "next" is per-profile within this tracker, not
cross-tracker:

> Each profile's latch releases automatically when its claimed device
> disconnects, allowing the next matching device to claim that
> profile's slot. Latches are scoped to one tracker — two trackers
> with overlapping filters will both observe the same device. See
> ADR-0049 for the cross-tracker contract.

### 3. Diagnostic log on broad-filter overlap

`DeviceWatcher.FanOutActivated` (and the analogous `FanOutAppeared`)
gain an Information-level log entry when a device is being delivered
to a tracker AND another tracker already holds the device's Id in its
resolved state:

```csharp
private void FanOutActivated(DeviceInfo device)
{
    DeviceTracker? alreadyHolding = null;
    foreach (var tracker in _trackers)
    {
        if (!tracker.Matches(device)) continue;

        if (alreadyHolding is null && tracker.Device?.Id == device.Id)
            alreadyHolding = tracker;
        else if (alreadyHolding is not null && tracker.Device?.Id != device.Id)
            _logger.LogInformation(
                "Device {DeviceId} matches both tracker '{First}' and '{Second}' — " +
                "both will observe the same device. If you intend each tracker to " +
                "bind a different physical device, narrow the filters per role " +
                "(see ADR-0049).",
                device.Id, alreadyHolding.Name ?? "(unnamed)", tracker.Name ?? "(unnamed)");

        tracker.OnDeviceConnected(device);
    }
}
```

(Sketch — final shape decided at implementation time. Goal: one
Information-level line on the first overlap so operators notice during
development; not noisy on every event.)

The log is **informational, not an error**. Two broad trackers
observing the same device is a valid configuration — the kiosk's
provisioning model relies on it (the broad query is, in effect, a
transient broad tracker view of candidates). The log fires only when
two registered DeviceTracker instances each hold the same Id in their
resolved state, which is the actionable case.

### 4. Document the two patterns in `ARCHITECTURE.md`

Add a short subsection to `docs/ARCHITECTURE.md` §2.2 (Device Tracker)
describing "Narrow filter per role" vs "Broad candidates + narrow
trackers + reconfigure," with the kiosk's `IRoleProvisioner` named as
the reference pattern.

---

## Consequences

### Positive

- **POS-001**: `DeviceTracker`'s contract stays simple — "I observe
  state matching my filter." No claim semantics, no priority across
  trackers, no shared registry. Reasoning about a single tracker
  doesn't require thinking about every other tracker.
- **POS-002**: `Reconfigure` (ADR-0046) stays trivially safe. Changing
  one tracker's filter doesn't affect any other tracker's claims
  because there are no cross-tracker claims.
- **POS-003**: Multi-profile semantics (ADR-0006) stay clean.
  Cross-tracker exclusivity would have to choose between "exclude
  based on the resolved Device" and "exclude based on all latched
  devices across profiles" — both defensible, both confusing.
- **POS-004**: The kiosk's `IRoleProvisioner` pattern works as
  designed. Broad query + narrow tracker + reconfigure is the natural
  shape; cross-tracker exclusivity would compete with it.
- **POS-005**: The silent failure mode (two trackers, same device) is
  no longer silent — the Information log emission turns the surprise
  into a documented diagnostic.

### Negative

- **NEG-001**: Consumers who *want* "find me any N matching devices"
  semantics from N declaratively-named trackers still don't get it.
  The recommended path (one `MultiDeviceTracker` + consumer-side role
  assignment, or one-shot `DeviceQuery` + `Reconfigure`) requires
  consumer code, not just config. Acceptable cost given the
  alternatives' complexity.
- **NEG-002**: The diagnostic log is opportunistic, not exhaustive.
  Two trackers' filters could match disjoint devices on this boot but
  the same device on the next (hardware swap, port rearrangement) —
  the log fires only when the overlap actually happens, not
  predictively. Acceptable; an exhaustive "filter overlap" analysis
  would require trial-evaluating every filter against every device,
  which the watcher doesn't have a clean hook for.
- **NEG-003**: The cooperative model accepts that `tracker.Device`
  can be aliased across trackers. Consumers iterating multiple
  trackers' `Device` properties may see the same `DeviceInfo`
  reference twice. Idempotent consumers (display state, health
  probes) are unaffected; consumers that assume disjoint device
  references must do their own deduplication.

---

## Alternatives Considered

### A — Globally exclusive claim (registration-order priority)

First tracker (in registration order) to latch a device owns it;
subsequent matching trackers don't latch. Implemented as a watcher-
level claim registry (`Dictionary<string, DeviceTracker>` keyed by
device Id) consulted by `FanOutActivated` before delivery.

**Rejected.** Four problems:

1. **Registration-order coupling.** Tracker assignment depends on the
   order `AddTracker` was called. JSON config arrays preserve order
   (the kiosk's `appsettings.json` is a list — fine), but `Dictionary`-
   keyed enumerations are implementation-defined. The exclusivity
   semantics would silently flip between boots for consumers using
   key-ordered tracker registration.
2. **Hot-plug + Reconfigure interaction.** Tracker T1 holds Device X.
   T1.Reconfigure() to no longer match X. X should now be available
   for T2 to claim. The implementation requires the watcher to
   replay the cache through *all* trackers when a claim changes,
   which expands the surface of `Reconfigure` substantially. Doable;
   not warranted by use case demand.
3. **Multi-profile semantic ambiguity.** A tracker with multiple
   profiles can hold devices on lower-priority profiles even when its
   resolved `Device` is from the highest-priority one. Cross-tracker
   exclusivity has to choose: exclude based on the resolved Device
   (consistent with the public API) or all latched devices
   (consistent with the per-profile latch contract). Neither answer
   composes cleanly with ADR-0006's resolution model.
4. **`BroadFilter` provisioning conflict.** The kiosk consumer's
   `IRoleProvisioner.BroadFilter` deliberately wants to *see every
   candidate device*, including ones currently claimed by another
   role. Exclusivity at the tracker level would either bifurcate the
   listing API into "exclusivity-aware" and "raw" modes, or force the
   provisioning UI to use a non-tracker enumeration path (which it
   already does — but the bifurcation cost is real).

### B — Opt-in `exclusive: true` flag on tracker construction

`AddTracker(filter, exclusive: true)` for the rare role that wants
auto-disambiguation; default stays cooperative.

**Rejected.** Two semantic models in one library is worse than either
alone. Mixing exclusive and cooperative trackers in the same watcher
produces interactions (exclusive tracker yielding to cooperative, or
vice versa?) that no documentation can make intuitive. The flag is
essentially "I don't know which semantics is right, you pick" — which
admits the design hasn't converged.

### C — Cross-tracker exclusivity at the watcher level, transparent to trackers

The watcher decides which tracker gets each event without the tracker
itself knowing about exclusivity. Per-tracker code (latches, resolve)
stays unchanged; the watcher's `FanOut*` methods learn to skip
trackers that "already have something."

**Rejected.** Cleaner than A or B in some ways (trackers stay pure
observers), but the same hot-plug + Reconfigure + multi-profile
issues from A still apply at the watcher level. The watcher would
need to maintain its own per-device claim registry that mirrors the
trackers' per-profile latches — a parallel state machine that has to
stay coherent with the trackers' internal state. The coupling cost is
real and the benefit is narrow (one deferred-work use case).

### D — Replace the use case with `MultiDeviceTracker`

The kiosk drops two role-named trackers in favor of one
`MultiDeviceTracker(Category: Monitor)`; the kiosk app handles
role→device assignment in `DeviceAdded` handlers.

**Already happens, sort of.** The kiosk's runtime provisioning *is* a
broader query (a one-shot `DeviceQuery`, not a `MultiDeviceTracker`)
plus per-role narrow trackers plus `Reconfigure`. The current
architecture is a variant of option D, with the broad query feeding
candidates to the operator and the narrow trackers binding the chosen
assignments. The pure-D form (one MultiDeviceTracker, no role trackers)
would lose the declarative role-naming the kiosk relies on; the
hybrid form the kiosk actually uses is the right shape.

This ADR's documentation work (§4) recommends the hybrid pattern for
future consumers.

---

## Open Questions

- **OQ-001**: Should the diagnostic log fire on `Appeared` (every
  matching tracker, not just `Activated`)? Activated is the more
  consequential signal — "is this device active?" is what consumers
  drive most logic from. Appeared is noisier (Bluetooth-paired-but-
  not-in-range devices appear without activating). **Tentative
  answer:** log on Activated only; reconsider if consumers complain
  about missed overlaps.

- **OQ-002**: Should the log line include the tracker filters? Would
  help operators diagnose which configs collide, but DeviceFilter's
  introspection surface is internal (structured properties +
  lambda-only predicates). Exposing a "describe yourself" surface on
  DeviceFilter is its own design question. **Tentative answer:** log
  only the tracker names + device Id; consumers wanting filter
  details look at their own config.

- **OQ-003**: Re-visit this decision if a second consumer (beyond
  the kiosk consumer) lands with a "broad filter, N devices, no runtime
  provisioning UI" pattern. Frame-flow's future demos might surface
  one; if they do, the design space narrows considerably (probably
  toward option C, watcher-level transparent exclusivity, since it
  preserves the tracker contract). For now: no second consumer, no
  design pressure.

- **OQ-004**: The `RoleProvisioningCardViewModel` pattern is currently
  consumer-specific. Worth promoting to Periphery? **Tentative
  answer:** no — provisioning UI is consumer-domain. Periphery's job
  is providing the primitives (`DeviceQuery`, `Reconfigure`,
  `MultiDeviceTracker`); the role-naming, transient-vs-persistent
  config layering, and UI binding are kiosk-specific concerns that
  don't belong in a hardware-enumeration library. Document the
  pattern in `ARCHITECTURE.md`, don't import the code.

---

## References

- [ADR-0001](0001-device-tracking-handles.md) — Original tracking handles
- [ADR-0006](0006-device-profile-single-device-resolution.md) — Per-profile per-latch model that this ADR pins as the contract
- [ADR-0034](0034-device-group-tracker.md) — `MultiDeviceTracker` / `DeviceGroupTracker`
- [ADR-0046](0046-runtime-tracker-reconfigure.md) — `DeviceTracker.Reconfigure` — the runtime-rebinding mechanism the kiosk's provisioning leverages
- `src/Periphery/DeviceTracker.cs` — Per-profile latch implementation
- `src/Periphery/DeviceWatcher.cs` — Fan-out (cooperative by construction)
- The kiosk consumer's `Services/DeviceTracking/IRoleProvisioner.cs` — Reference pattern for runtime per-role provisioning
- The kiosk consumer's `ViewModels/DebugViewModels/Cards/RoleProvisioningCardViewModel.cs` — Operator-facing card that drives `Reconfigure`
