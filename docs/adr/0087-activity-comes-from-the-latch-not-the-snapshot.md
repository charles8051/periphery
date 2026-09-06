---
title: "ADR-0087: Activity comes from the latch, not from the device snapshot"
status: "Proposed"
status_note: "Reproduction on main: `test/177-tracker-demotion-on-late-appeared` @ be70306. Not implemented."
date: "2026-09-06"
authors: "@charles8051 (reproduction + analysis)"
tags: ["architecture", "decision", "device-tracker", "state-model", "cross-platform", "adr-0004"]
supersedes: ""
superseded_by: ""
depends_on: ["0004-two-level-device-state-model.md", "0006-device-profile-single-device-resolution.md"]
---

# ADR-0087: Activity comes from the latch, not from the device snapshot

**Tracks:** `DeviceTrackerResolution`, `DeviceTrackerState`
**Depends on:** ADR-0004 (two-level state model), ADR-0006 (per-profile latch + resolution)
**Issue:** #177

---

## Status

**Proposed.** The defect is reproduced on `main` by four tests on
`test/177-tracker-demotion-on-late-appeared` (`be70306`), two of which fail. No
implementation yet — this ADR is the decision that gates it.

---

## Context

ADR-0004 introduced two orthogonal axes: **present** (the OS knows the device) and
**active** (it is usable right now). ADR-0006 implemented them in
`DeviceTrackerResolution` as two per-profile soft latches over one shared snapshot
map:

```csharp
private readonly ImmutableDictionary<DeviceProfile, ImmutableDictionary<DeviceId, DeviceInfo>> _devicesByProfile;
private readonly ImmutableDictionary<DeviceProfile, DeviceId?> _presentLatch;
private readonly ImmutableDictionary<DeviceProfile, DeviceId?> _connectedLatch;
```

The latches are orthogonal. The snapshot map is shared. That is the whole problem.

### The defect

`Resolve()` gates `Active` on the connected latch **and** on a field of the shared
snapshot:

```csharp
if (_connectedLatch[profile] is { } connId &&
    _devicesByProfile[profile].TryGetValue(connId, out var d) &&
    d.IsActive)
```

`ApplyAppeared` writes that same snapshot through `SetDevice`. So a **presence** edge
carrying `IsActive == false` overwrites the snapshot an **activity** assertion depends
on, and the tracker drops `Active → Present` while the connected latch is still held.
The consumer also observes a deactivation transition for a device that never stopped.

There is no recovery. `DeviceWatcher.OnProviderActivated` returns early once
`_knownConnectedIds` holds the id, and on Windows `Deactivated` never fires to clear
it. The tracker sits at `Present` until physical removal.

### It ships today, on two of three platforms

Reproduced with the fake providers, so it exercises the platform-agnostic path:

```
LateAppearedWithStalePayload_DoesNotDemoteAnActiveTracker      FAIL  Expected Active, Actual Present
LateAppearedWithStalePayload_PublishesNoSpuriousDeactivation   FAIL  Collection: [Present]
AppearedThenActivated_ResolvesToActive                         pass
GenuineDeactivation_StillDemotesToPresent                      pass
```

`LinuxDeviceMonitorProvider.HandleAdd` raises `DeviceAppeared` unconditionally and only
then raises `DeviceActivated` if `device.IsActive`; macOS mirrors it. Windows has been
shielded for the library's entire history **only because its `DeviceAppeared` never
fires for a live arrival** — the bug #177 diagnoses. Fixing the Windows provider in
isolation (#186) would expose this on a third platform, which is why that PR is on
hold.

The interleaving needs no concurrent OS callbacks. `DeviceWatcher.StartAsync` starts
the monitor provider *before* `SnapshotCurrentDevicesAsync` walks the tree, so the walk
can build a snapshot for a device that has not yet started, the provider can raise
`Activated` for it mid-walk, and the walk then raises `Appeared` with the stale payload.

### Why the coupling is redundant as well as harmful

The connected latch is already the complete record of activity. `ApplyConnected` sets
it, and only the activity edge calls it. `ApplyDisconnected` clears it, and only the
activity edge calls that. `ApplyReplay` sets it only `if (device.IsActive)`. No path
sets the connected latch for an inactive device.

So `&& d.IsActive` in `Resolve()` carries no information the latch does not already
carry. It is a redundant conjunct that creates a dependency on a mutable field written
by an unrelated axis.

---

## Decision Drivers

| Concern | Requirement |
|---|---|
| ADR-0004 fidelity | The two axes are orthogonal. One must not be able to silently overwrite the other. |
| Cross-platform | The fix must hold for all three providers. It belongs in the pure core, not in a provider. |
| No deafness | A genuine deactivation must still demote. Correctness bought by ignoring real transitions is not correctness. |
| Consumer coherence | Within one `DeviceTrackerState`, `ActivityStatus` and `Device` must not contradict each other. |
| Unblock #177 | A provider that legitimately reports "present but not started" must be representable. |

---

## Options considered

### Option 1 — `Resolve()` trusts the connected latch

Drop `&& d.IsActive` from the `Active` branch.

- **Fixes** the demotion, because presence-edge writes no longer feed the activity verdict.
- **Removes** a redundant coupling rather than adding machinery.
- **Leaves** `state.Device.IsActive == false` while `state.ActivityStatus == Active`. A
  consumer reading the payload sees it contradict the verdict. That is a new, visible
  inconsistency, so this is **not sufficient alone**.

### Option 2 — `ApplyAppeared` does not demote an activity-latched snapshot

When the connected latch holds this id and the incoming presence payload is less active,
keep the existing snapshot.

- **Fixes** the demotion, and keeps `Device` consistent with `ActivityStatus`.
- **Costs** a snapshot refresh: a presence edge carrying newer enrichment for an
  already-active device is dropped. Acceptable, because `ApplyPropertyChanged` is the
  designated path for property refresh and leaves both latches untouched by design.
- **Leaves** the latch/snapshot coupling in place. Any future path that writes a stale
  snapshot re-arms the same trap.

### Option 3 — separate snapshots per axis

`_devicesByProfile` splits into a present-snapshot map and an active-snapshot map.

- **Most faithful** to ADR-0004: the axes stop sharing storage entirely.
- **Forces an unanswered question**: which snapshot does `DeviceTrackerState.Device`
  return when the two disagree, and what does `DeviceInfoDiff` diff against?
- **Largest change** to a type ADR-0006 deliberately made a small pure core, for a
  failure mode Options 1+2 already close.

---

## Decision

**Adopt Options 1 and 2 together.** Reject Option 3 for now.

1. `Resolve()` gates `Active` on the connected latch alone. Activity is what the latch
   says, not what a snapshot field says.
2. `ApplyAppeared` does not overwrite the stored snapshot when the connected latch holds
   that id and the incoming payload has `IsActive == false`. Presence edges carry
   presence; they do not get to lower activity.

Option 2 alone would make the failing tests pass. Option 1 alone would too, but leaves
the payload contradicting the verdict. Taken together they fix the behaviour *and*
remove the coupling that made it fragile, which is the difference between a patch and a
fix for something this load-bearing.

Option 3 stays on the table if a future case shows the axes genuinely need distinct
payloads. Nothing observed so far requires it.

### Not in scope

- **The duplicate `Appeared` at watcher startup.** `SnapshotCurrentDevicesAsync` guards
  `Activated` against ids the provider already announced and does not guard `Appeared`.
  Separate defect, separate fix, tracked in #177.
- **The Windows provider change.** #186 stays on hold. This ADR removes the reason its
  approach was unsafe, but the Windows-specific findings (monitor payload enrichment,
  cache seeding before registration, the unmeasured action-7 `ClassGuid`) stand on their
  own.
- **`DeviceInfo.IsActive` itself.** It remains a truthful property of a snapshot. This
  ADR only stops the *tracker* deriving its verdict from it.

---

## Consequences

### Good

- The two failing tests go green; both controls stay green, so a genuine deactivation
  still demotes.
- A provider may now legitimately report "present but not started" — exactly what
  `DEVICEINSTANCEENUMERATED` means — without corrupting tracker state. That unblocks the
  #177 approach.
- Provider-level `Appeared`-before-`Activated` ordering stops being load-bearing. The
  claim withdrawn from #186 no longer needs to be true.
- Linux and macOS get a fix for a defect they carry today.

### Bad, or at least a cost

- A presence edge carrying fresher enrichment for an already-active device no longer
  refreshes the snapshot. Property refresh must come through `ApplyPropertyChanged`. On
  Windows that path only fires for monitors, so a thin-then-enriched sequence for a
  non-monitor is not repaired — pre-existing, unchanged, and worth its own issue.
- `Resolve()` becomes less defensive. If a future provider sets the connected latch
  without a matching clear, nothing downstream second-guesses it. That is the intended
  trade: one authority instead of two that can disagree.

### Neutral

- No public API change. `DeviceTrackerState`, `DeviceActivityStatus`, and the tracker
  event surface are untouched.
- `DeviceInfoDiff` and `PropertyChanged` are unaffected — neither reads the latches.

---

## Validation

`test/177-tracker-demotion-on-late-appeared` is the acceptance criterion. All four tests
must pass, and the two controls must still be doing real work — a fix that passes by
making the tracker ignore deactivation fails `GenuineDeactivation_StillDemotesToPresent`
by construction.

Beyond that, the existing `DeviceTrackerTests`, `DeviceTrackerResolutionTests`,
`DeviceTrackerReconfigureTests`, and `MultiDeviceTrackerTests` must stay green;
`ApplyReplay` shares the latch rules and is the most likely place for a regression.
