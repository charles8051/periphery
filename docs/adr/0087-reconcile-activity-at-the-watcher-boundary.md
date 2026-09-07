---
title: "ADR-0087: Reconcile device activity at the watcher boundary"
status: "Accepted"
status_note: "Implemented on main in #193 (D1, D2 scoped to the start window, D3). Amended 2026-09-07: Option 2 adopted in the pure core alongside D1, plus a re-check of both axes before tracker fan-out and a gate on inactive Activated payloads in the watcher (#201, #202). The evidence-recency gap left open here is still tracked in #201. Earlier draft of this ADR located the fix in DeviceTrackerResolution and is superseded within this file."
date: "2026-09-06"
authors: "@charles8051 (reproduction, four parallel prototypes, adversarial review)"
tags: ["architecture", "decision", "device-watcher", "device-tracker", "state-model", "cross-platform", "adr-0004"]
supersedes: ""
superseded_by: ""
depends_on: ["0004-two-level-device-state-model.md", "0006-device-profile-single-device-resolution.md"]
---

# ADR-0087: Reconcile device activity at the watcher boundary

**Tracks:** `DeviceWatcher`, `DeviceTrackerResolution`
**Depends on:** ADR-0004 (two-level state model), ADR-0006 (per-profile latch + resolution)
**Issue:** #177

---

## Status

**Accepted.** Reproduced on `main`; four options implemented and measured in parallel worktrees.
Implemented on `main` in #193: D1 on both the live path and the walk, D2 scoped to the start
window, D3 on every lifecycle edge. The evidence-recency gap in **Rejected options** is open as
#201.

**Amended 2026-09-07.** An independent review of #193 measured a window this decision left open:
the public `Appeared` raise runs consumer code between the reconciliation at the top of the handler
and the tracker fan-out, and an edge for the same id that lands in that window makes the fan-out
payload stale. On Windows the two halves of one hot-plug arrive on separate threads, so the window
is a race per arrival; during startup the walk and the live stream overlap on every platform. Its
consequences were permanent: a demoted tracker stays demoted because `OnProviderActivated`'s dedup
guard blocks the recovery, and a removed device stays latched. Three changes close the permanent
outcomes without an ordering guarantee, each pinned by a probe that fails without it:

1. **Option 2 is adopted in the pure core alongside D1.** `ApplyAppeared` keeps the activity the
   connected latch holds, in both directions: a payload captured before the device started cannot
   demote it, and a payload captured before a property transition demoted it cannot promote it
   again. See the Option 2 entry below for what changed since it was set aside.
2. **The watcher re-checks both axes immediately before tracker fan-out**, on the live path and in
   the walk: a device whose `Disappeared` landed during the raise is not announced, and its id does
   not enter `_knownConnectedIds`; everything else is re-reconciled against the activity set as it
   stands at fan-out time.
3. **An `Activated` edge whose payload says `IsActive == false` is not recorded as an activation,
   and is treated as no edge at all.** The core's fail-safe (Option 1's conjunct) stays; the
   watcher no longer puts the id in `_knownConnectedIds`, so the genuine activation is not
   deduplicated away (#202), and does not write the replay cache, so a later `Reconfigure` does
   not replay an active device as `Present`. If the payload was wrong because a status read
   failed rather than because the device is stopped, the device stays `Present` until a later
   edge; telling those two apart is the provider's job, at the raise site.

The recency gap itself remains open as #201. These narrow its consequences; they do not close it.

An earlier draft of this ADR decided a change to `DeviceTrackerResolution` on a justification that
measurement falsified. That draft is replaced here rather than kept, because it was never merged.
The error is recorded in **Rejected options** so it is not repeated.

---

## Context

ADR-0004 gives two orthogonal axes: **present** (the OS knows the device) and **active** (usable
right now). ADR-0006 implemented them in `DeviceTrackerResolution` as two per-profile soft latches
over one shared snapshot map.

`Resolve()` gates `Active` on the connected latch **and** on `d.IsActive` read off that shared map.
`ApplyAppeared` writes the same map. So a presence edge carrying `IsActive == false` overwrites the
snapshot an activity assertion depends on, and the tracker drops `Active → Present` while the
connected latch is still held. `DeviceWatcher._knownConnectedIds` then blocks recovery.

Reproduced on `main`, cross-platform, no hardware —
`test/177-tracker-demotion-on-late-appeared` @ `be70306`:

```
LateAppearedWithStalePayload_DoesNotDemoteAnActiveTracker      FAIL  Expected Active, Actual Present
LateAppearedWithStalePayload_PublishesNoSpuriousDeactivation   FAIL  Collection: [Present]
AppearedThenActivated_ResolvesToActive                         pass
GenuineDeactivation_StillDemotesToPresent                      pass
```

`LinuxDeviceMonitorProvider.HandleAdd` raises `DeviceAppeared` unconditionally before raising
`DeviceActivated`; macOS mirrors it. Windows has been shielded only because its `DeviceAppeared`
never fires for a live arrival — which is the bug #177 diagnoses. So this ships today on two of three
platforms, and #186 would expose it on the third.

### What measurement changed about the problem statement

Four prototypes, each in its own worktree, each run against the full suite. Three findings reframed
the decision.

**1. The reproduction is two races wearing one name.** The failing tests model a pure-live
reordering. A second race — the startup walk versus the live stream — is only described in prose.
`DeviceWatcher.StartAsync` starts the monitor provider *before* `SnapshotCurrentDevicesAsync` walks
the tree, so the walk publishes edges built from a snapshot the live stream has already moved past.
A fix for the first leaves the second shipping.

**2. The defect has a mirror, and the mirror is worse.** Run the same window backwards: the walk
captures an active payload, the live stream raises `Disappeared` (a no-op — the tracker holds
nothing yet), then the walk publishes `Appeared` + `Activated`. The tracker latches **Active for a
device that is gone**, permanently, because `Disappeared` has already been consumed. Every
tracker-layer option is asymmetric — each protects the connected latch from presence writes and
none stops `ApplyConnected` latching after `ApplyDisappeared` found nothing. #177's direction makes
a working device look unusable; the mirror makes a removed device look usable, which is the class of
failure that blocked #186.

**3. The tracker is not the only consumer.** Options inside `DeviceTrackerResolution` fix trackers
and nothing else. `DeviceWatcher.Appeared` still delivers `IsActive == false` for a running device,
and that payload is what `TrackerDeviceWaitSource`, `TreehopperControlService.OnAppeared`, and any
consumer's `.Where(d => d.IsActive)` read.

### A third stale-snapshot instance, unrelated to the latches

`DeviceWatcher._deviceCache` is written only by the startup walk (`:961`) and by
`OnProviderPropertyChanged` (`:1192`), and cleared at `:921`. No arrival, activation, deactivation
or removal edge touches it. `Reconfigure` / `ReplaceProfiles` replay from it, so a reconfigure
**erases every device that arrived live** and **resurrects every device that was removed live**.
This is a second "no recovery" mechanism structurally identical to `_knownConnectedIds`, and no
tracker-layer option reaches it.

---

## Decision Drivers

| Concern | Requirement |
|---|---|
| ADR-0004 fidelity | The axes are orthogonal. One must not silently overwrite the other. |
| Cross-platform | The fix belongs where all three providers converge, not in a provider. |
| No deafness | A genuine deactivation must still demote. Correctness bought by ignoring real transitions is not correctness. |
| Both directions | A fix that closes the demotion and leaves its mirror open is half a fix. |
| All consumers | Trackers, group trackers, and the raw watcher events must all see a coherent payload. |
| Preserve ADR-0006 | The latch algebra is a small pure core. Prefer not to disturb it. |

---

## Decision

**Reconcile at the `DeviceWatcher` fan-out boundary. Leave `DeviceTrackerResolution` alone.**

Three parts, all in `DeviceWatcher`. The prototype measured **1250/1250 green, +49 lines, zero
lines changed in the pure core**; the shipped change (#193) is larger because D2 is scoped to the
start window and the reasoning is recorded inline:

### D1 — The watcher is the authority on activity

The watcher already owns `_knownConnectedIds` and already synthesises `Deactivated` from
`Disappeared`. A presence edge whose payload contradicts that authority is reconciled once, before
any consumer or tracker sees it:

```csharp
private DeviceInfo ReconcileActivity(DeviceInfo device)
{
    if (device.IsActive) return device;
    bool known;
    lock (_knownConnectedIds) known = _knownConnectedIds.Contains(device.Id);
    return known ? device with { IsActive = true } : device;
}
```

Applied in `OnProviderAppeared`. Fixes trackers, group trackers, the public `Appeared` event, and
every downstream consumer in one place.

### D2 — The startup walk reconciles rather than replays

Any id the live stream has already handled carries a strictly fresher verdict than the payload the
walk is holding. The walk skips those ids. One `HashSet<DeviceId>`, populated by the two presence
handlers (`Appeared` and `Disappeared`) and consulted in `SnapshotCurrentDevicesAsync`. Activity
edges do not populate it: they say nothing about whether the devnode is in the tree, and D1 already
reconciles the walk's activity payload.

This closes the walk-versus-live race, its mirror, **and** the duplicate startup `Appeared` — the
walk already guards `Activated` against `_knownConnectedIds` at `DeviceWatcher.cs:963-971` and does
not guard `Appeared` five lines above. The earlier draft of this ADR put that out of scope; it is
the same defect and comes free here.

### D3 — Every lifecycle edge maintains `_deviceCache`

`OnProviderAppeared`, `OnProviderActivated` and `OnProviderDeactivated` write it;
`OnProviderDisappeared` removes from it. Makes `Reconfigure` / `ReplaceProfiles` replay reflect
reality.

The deactivation write matters as much as the others, and an earlier draft of this ADR omitted it
by describing only "arrival and removal". Without it the cache keeps the last *active* snapshot
after a device goes inactive, and a later replay reasserts activity the watcher has already seen
retracted. On the cascade path - Windows raises `Deactivated` only as a cascade from
`Disappeared` - the entry is removed a moment later anyway, but on Linux and macOS a genuine soft
deactivation leaves the device present, and the cache must carry the inactive snapshot.

---

## Rejected options

### Option 1 — `Resolve()` drops `&& d.IsActive` and trusts the latch. **Rejected.**

The earlier draft of this ADR decided this, justified as removing "a redundant conjunct that carries
no information the latch does not already carry". **That justification is false**, and three
independent prototypes found it so.

`ApplyConnected` never reads `IsActive` — it claims the slot unconditionally. Two provider raise
sites are ungated: `WindowsDeviceMonitorProvider.HandleInstanceStarted` and
`LinuxDeviceMonitorProvider.HandleBind`, the latter reachable when a USB device is bound while
deauthorized. The conjunct is a fail-safe, not a redundancy, and there is a pinned test saying so:

```
DeviceTrackerResolutionTests.ApplyConnected_InactiveSnapshot_DoesNotResolveActive  FAIL
Expected: Absent   Actual: Active
```

The error was verifying that `ApplyConnected`'s callers are activity edges without checking whether
those edges gate on `IsActive`. Option 1 converts a fail-safe to fail-open, on the platform where
nothing clears the latch short of unplugging, and it leaves `state.Device.IsActive` contradicting
`state.ActivityStatus`.

### Option 2 — `ApplyAppeared` must not lower a latched activity. **Set aside at first; adopted 2026-09-07 alongside D1–D3.**

4 lines, all four acceptance tests pass, **zero regressions** across 1234 tests. It is the minimal
correct tracker-layer fix and was kept as the fallback if D1–D3 proved too broad.

Not chosen on its own because it fixes trackers only, closes one of the two races, does nothing
about the mirror or `_deviceCache`, and, as prototyped, dropped first-arrival monitor enrichment —
`TryBuildDeviceInfo` runs the same enrichers as `EnumerateAsync` minus
`WindowsDisplayConfigEnricher`, so the walk's payload is the only enriched copy for a monitor with
no prior cache entry.

Adopted in addition to D1–D3 once the fan-out window (see **Status**) showed that a boundary filter
alone leaves the core exposed to any payload that reaches it late. Each objection is answered by
the combination rather than by Option 2 alone: D1 fixes the public payload and every consumer, D2
and the fan-out re-check cover the mirror, D3 covers `_deviceCache`, and the adopted form stores
the new payload and replaces only its `IsActive` flag with the held snapshot's, in both directions,
so enrichment is kept and a stale active payload cannot undo a property transition either. The latch algebra of
ADR-0006 is unchanged: which id holds which slot is decided exactly as before; only the snapshot
stored for a held id is reconciled. Option 2 also reaches a case D1 does not: a device the
watcher-level filter rejects but a tracker holds, whose id the walk never adds to
`_knownConnectedIds`.

### Option 3 — split `_devicesByProfile` into per-axis snapshot maps. **Deferred, with a named blocker.**

Zero regressions, and the cleanest implementation of the three. It also closes a direction the
others leave open: a thin activity payload degrading the presence snapshot, live on Windows monitors
today.

The blocker is not size, and it is not the `Device` question — that has a clean answer (each
`Resolve()` branch carries its own axis's snapshot) which was implemented and cost nothing to
decide. The blocker is that **`DeviceTrackerState` has one `Device` slot**, so the second map is
write-only whenever both axes hold, which is the normal state of any USB device. It preserves a
payload it cannot surface, and discards activity-edge enrichment at disconnect — making ADR-0004's
motivating Bluetooth case slightly worse.

Revisit only alongside a `DeviceTrackerState` that can carry both payloads. At that point it stops
being purity and starts being capability.

---

## Consequences

### Good

- All four acceptance tests pass; both controls keep doing real work.
- Both races close, and so does the mirror.
- The duplicate startup `Appeared` closes as a side effect rather than as separate work.
- `Reconfigure` stops erasing live arrivals and resurrecting removed devices.
- ADR-0006's latch algebra is untouched, so this needs no ADR against the pure core.
- A provider may legitimately report "present but not started" — what `DEVICEINSTANCEENUMERATED`
  means — without corrupting downstream state. That unblocks #186's approach.
- Linux and macOS get a fix for a defect they carry today.

### Bad, or at least a cost

- **D1 stamps a payload no provider produced.** `Appeared`'s `DeviceInfo.IsActive` becomes a
  watcher-authored value. The field stays truthful as a provider observation everywhere else; only
  this one edge is reconciled.
- **D1 inherits `_knownConnectedIds`' imperfections.** `RollBackAttemptAsync` documents residue
  after a failed start, and a stale id there would assert `IsActive = true` falsely. Narrower than
  Option 1's exposure — D1 only upgrades on an unretracted `Activated`, and both `Deactivated` and
  `Disappeared` retract — but not zero.
- **D2 needs scoping.** The prototype never cleared its skip set. The shipped version opens the
  window before the monitor provider goes live and closes it in the start attempt's `finally`, so
  the set is bounded by the devices that change during one enumeration and a failed start does not
  leave it latched for the retry.
- **D2 can silently drop a device**, over a wider window than "filtered out". The skip set is
  populated at the top of each provider handler, before the watcher filter runs, so an id whose
  live edge the filter rejected is still skipped when the walk reaches it. If the walk's payload
  would have matched - reachable, because the walk runs the full enrichment pipeline and the
  notification path does not - the device is never announced at all.

  The same asymmetry costs enrichment even when both payloads match: for a Windows monitor the
  walk's is the only payload carrying DisplayConfig fields, so skipping it in favour of a live edge
  trades completeness for freshness. Both follow from treating "the live stream spoke" as
  sufficient without comparing what it said. Narrowing the skip to ids whose live edge was actually
  published, or comparing payloads rather than ids, would close it. Neither is done here.
- The two ungated provider raise sites (`HandleInstanceStarted`, `HandleBind`) remain ungated. This
  ADR does not depend on them being fixed, unlike Option 1, but they should be.

### Examined, and not a problem

**A device deactivated or removed mid-walk.** The skip set suppresses only the *walk's*
republication, never the live handler, which runs in full - so the removal is announced and the
cache pruned. A consumer can see a `Disappeared` for a device it never saw `Appeared`, because the
walk was suppressed before reaching it. That is the correct trade: the alternative is the walk
announcing the arrival of a device the live stream has already reported gone.

### Neutral

- No public API change.
- `DeviceInfoDiff` and `PropertyChanged` are unaffected; neither reads the latches.

---

## Validation

`test/177-tracker-demotion-on-late-appeared` is the acceptance criterion: all four green, with
`GenuineDeactivation_StillDemotesToPresent` still failing if the fix is stubbed out.

The four probes written against the prototypes must also pass — the mirror defect, both
`Reconfigure` directions, and the raw-event payload. All four fail on `main` and continue to fail
under Options 1, 2 and 3.

The existing `DeviceTrackerResolutionTests`, `DeviceTrackerTests`, `DeviceTrackerReconfigureTests`
and `MultiDeviceTrackerTests` must stay green — including
`ApplyConnected_InactiveSnapshot_DoesNotResolveActive`, which Option 1 broke.

---

## Open: `ApplyPropertyChanged` is a third path, and this boundary cannot close it

D1 reconciles the presence edge because a presence payload carries no activity information the
watcher does not already hold, so overriding its `IsActive` from `_knownConnectedIds` loses
nothing. That reasoning does not extend to `DevicePropertyChanged`.

`ApplyPropertyChanged` also writes the shared snapshot `Resolve()` reads `IsActive` from, so a
delayed property payload carrying `false` demotes a tracker whose connected latch is still held -
the #177 defect on a third path.

Reconciling it the same way was tried, and is wrong. A `PropertyChanged` carrying an `IsActive`
transition is a documented way to report an activity change - `DeviceWatcher` raises it that way,
and `PropertyChanged_IsActiveTransition_IncludedInChangedProperties` pins it - so upgrading the
payload against `_knownConnectedIds` masks a legitimate deactivation. The attempt failed that test,
which is the correct outcome.

Nothing distinguishes a stale property payload from a truthful one at this boundary: both arrive as
`IsActive` going true to false for a device the watcher believes active. Telling them apart needs
an evidence-recency notion, which is precisely what none of the options considered here provide.
Left open rather than closed badly; tracked as #201.

## Follow-on work, not decided here

- The two ungated activation raise sites.
- Monitor payloads built on the notification path lack DisplayConfig enrichment
  (`TryBuildDeviceInfo` versus `EnumerateAsync`); no option addresses it.
- Windows raises no `DeviceDeactivated` at all (ADR-0054), so a connected latch clears only on
  removal. Every option in this ADR inherits that.
