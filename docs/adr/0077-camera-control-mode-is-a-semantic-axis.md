---
title: "ADR-0077: Camera control mode is a semantic axis, not a Media Foundation flag — and setting a control means the same thing on both platforms"
status: "Accepted"
date: "2026-08-14"
authors: "@charles8051"
tags: ["architecture", "decision", "camera", "controls", "windows", "mediafoundation", "linux", "v4l2", "contract", "platform-neutrality"]
supersedes: ""
superseded_by: ""
---

# ADR-0077: Camera control mode is a semantic axis, and setting a control means the same thing on both platforms

## Status

Applies ADR-0064's platform-neutral value contract to `Periphery.Camera`'s
control surface. Carries the posture ADR-0073 D4 set for topology
(`NotMeasured`) and ADR-0068 set for rotation: **unmeasured is its own state,
never a negative result.**

## Context

`Periphery.Camera` could set a control and reset one, but not **read** one.
`CameraControlInfo` describes a control's fixed shape — range, step, default,
writability — and there was no way to ask what a control is *currently doing*.

That gap has a consequence beyond the missing feature: **it makes
`SetControlAsync` irreversible.** A caller can move exposure and cannot record
where it was, so "put it back the way I found it" is not expressible and the
best available is "return it to automatic" — which is a different thing, and
wrong on any device somebody had configured deliberately.

Adding the read forced a question the library had not had to answer: *what is
the neutral meaning of "this control is on automatic"?* The two backends
disagree profoundly, and neither shape is a candidate for the contract.

### What actually differs

| | Media Foundation | V4L2 |
| --- | --- | --- |
| Where mode lives | A **flag on the control itself** — `IAMVideoProcAmp::Get` returns `VideoProcAmp_Flags_Auto` / `_Manual` alongside the value | A **separate companion control** (`V4L2_CID_EXPOSURE_AUTO`, `V4L2_CID_AUTO_WHITE_BALANCE`, …) |
| Sense | Two flag bits | **Not consistent between controls.** `AUTO_WHITE_BALANCE`, `AUTOGAIN` and `FOCUS_AUTO` are booleans where 1 = automatic. `EXPOSURE_AUTO` is an **enumeration running the other way**: `AUTO` = **0**, `MANUAL` = 1 |
| Extra states | None | `EXPOSURE_AUTO` also has `SHUTTER_PRIORITY` (2) and `APERTURE_PRIORITY` (3) |
| What `Set` does | Writes with `MF_CAMERA_FLAGS_MANUAL` — the write **takes the control away from the device** | Writes the value only. The auto loop still owns the control |

The last row is the one that mattered most, and it was not discovered by
design — it was found by adding the read and noticing that a `Set` followed by a
`Get` on Linux returns `Automatic` and a value the caller did not write.

## Decision

### D1. `CameraControlMode` is a semantic axis, and each backend maps its own signal

`CameraControlMode` is defined by **meaning** — is the device driving this
control, or is it held where someone put it — explicitly *not* as the Media
Foundation flag pair it superficially resembles. Each backend maps from its own
encoding, and the mapping lives with the backend
(`V4l2FormatMap.InterpretAutoValue`), not in the shared type.

This is ADR-0064's stance applied one subsystem over: state the neutral contract
while the shapes still differ, so a third backend drops in behind a type that
already means the right thing rather than discovering that a "neutral" type
encoded one platform's model.

**Named risk, stated rather than hidden:** the two-state-plus-unknown model
happens to coincide with Media Foundation's, so the claim of neutrality is
weaker here than in ADR-0064, where the Windows encoding was visibly rejected.
AVFoundation's locked / auto-continuous / auto-one-shot triad is the likely next
test of it. If a backend arrives with a state that is genuinely neither, the
contract gains a **member** — see D3 — and does not overload an existing one.

### D2. `Unknown` is a named gap, never a synonym for `Manual`

Reported when the device gives a readable value but nothing that says how it is
being driven — a Media Foundation driver returning neither flag, or a V4L2
companion control that will not answer. It is the enum's zero value.

The distinction is load-bearing in the direction that is easy to get wrong: a
caller that reads `Unknown` as `Manual` **believes a value is pinned when it may
be drifting**, which is precisely the silent-wrong-belief shape ADR-0073 exists
to prevent. The safer-sounding default is the dangerous one.

A control with **no auto companion at all** reads as `Manual`, not `Unknown` —
that is a real determination (the device has no automatic behaviour for it), and
conflating "there is nothing to ask" with "I asked and got no answer" would
throw away the difference D2 exists to preserve.

### D3. Partial-automatic states are a future member, not an overload — but the exposure priority modes are not one of them

V4L2's `SHUTTER_PRIORITY` and `APERTURE_PRIORITY` look like states the two-value
model cannot express, and the first instinct was to call them a gap and map them
to `Unknown`.

That is wrong, and the reason is worth recording because it is not obvious.
`CameraControlKind.Exposure` maps to `V4L2_CID_EXPOSURE_ABSOLUTE` — the exposure
**time**, not the exposure *system*. V4L2 defines shutter priority as *manual
exposure time, automatic iris* and aperture priority as *automatic exposure
time, manual iris*. So for the quantity this control actually names:

- `SHUTTER_PRIORITY` → the time is held → **`Manual`**
- `APERTURE_PRIORITY` → the time is driven → **`Automatic`**

Both are exact, not approximations, and mapping them to `Unknown` would discard
information the device supplied. An unrecognised value maps to `Unknown`.

Should a control ever arrive whose mode is genuinely partial *for the quantity
being read*, ADR-0064 D2's rule governs: a new member or companion axis, never
an overload of `Manual` or `Automatic`.

### D4. Setting a control means "take it off automatic" on every backend

`SetControlAsync` now has one meaning: after it returns successfully, the device
is not driving that control. Media Foundation already did this via
`MF_CAMERA_FLAGS_MANUAL`; V4L2 now writes the companion's manual sentinel before
writing the value.

This is a **behaviour change on Linux**, and it is a fix rather than a feature.
The previous behaviour was that a write to a control the auto loop owned either
failed with `EBUSY` or was overwritten on the next frame — and the backend's own
error text already told the caller to *"disable auto first"*, an instruction the
public API gave them no way to follow. A contract that means "pin this" on one
platform and "make a suggestion" on the other is not a contract.

Correspondingly `ResetControlAsync` hands the control back to the device on both
backends, which is what Media Foundation's reset-with-`_AUTO` already meant.

`V4l2FormatMap.MapModeToAutoValue` is the inverse of `InterpretAutoValue`, and a
test pins the round trip: if the two drift apart, a Linux write silently stops
pinning anything and the failure is invisible until someone measures a drifting
value on hardware.

**The companion write is enforced, not best-effort** (`EnforceCompanionMode`).
The first draft wrote it best-effort, reasoning that a refused mode switch would
surface as `EBUSY` on the value write immediately after. That reasoning is wrong
and review caught it: V4L2 drivers are under no obligation to refuse the value
write, and a driver that accepts it and lets the auto loop overwrite the value on
the next frame produces a *successful* `SetControlAsync` over a control the device
is still driving — the precise failure D4 exists to eliminate, reintroduced by the
implementation of D4.

**And the same distinction has to hold one layer down** (`ControlPresence`). The
first attempt at the enforcement guarded it with a boolean "does the companion
exist?" that was really "did the query succeed?" — so a transient `VIDIOC_QUERYCTRL`
failure skipped the enforcement altogether and the hole reopened directly above
where it had just been closed. `QueryControl` now separates `Absent` (the device
answered `EINVAL`: it has no such control) from `Unreadable` (the device did not
answer), and an operation that is *promising* a mode refuses to proceed over a
companion it could not ask about. This is D2's rule applied to presence rather
than mode, and it is worth noting that the second reviewer found it precisely
because the first fix made the guard load-bearing.

A refused write is still not automatically a failure, so the enforcement reads the
companion back before throwing: read-only companions, and companions already at
the requested mode, refuse the write with the contract satisfied. If the read-back
also fails, the operation throws rather than assuming — the same reasoning as D2,
that the unconfirmable case must not be resolved in favour of the comfortable
answer. `ResetControlAsync` enforces only its *destination* (`Automatic`); the
manual pass it makes on the way is genuinely best-effort, since a companion stuck
at automatic should not fail a reset that wants automatic.

### D5. A reading is not a description — `CameraControlState` is separate from `CameraControlInfo`

`CameraControlInfo` is a control's fixed shape, stable for as long as the device
is the device. `CameraControlState` is a **reading**: true when taken and
potentially false immediately after, because the entire point of these controls
is that the device keeps moving them.

Folding the reading into the descriptor would have made `GetControlsAsync` — a
capability query — perform a round of per-control IO and hand back values that
go stale in the caller's hand. Keeping them apart lets a consumer ask each
question at the cost it deserves.

### D6. The testing seam models the failure modes, not an idealised camera

Per ADR-0065, the fake's job is to reproduce *"the failure modes real drivers
exhibit"*. `InMemoryCameraBackend` gains `SetControlState` and
`RefuseControlRead`, because without them:

- `Unknown` was **unreachable through the fake** — the member whose whole
  documentation is a warning about mishandling it could not be produced by any
  consumer test, so restore logic that mishandles it ships green;
- a driver that answers enumeration but declines a read — the case the Windows
  unsupported-property branch exists for — had no hook at all.

A fake that only models the cooperative device is the failure mode ADR-0065 was
written to prevent.

## Consequences

- `SetControlAsync` / `ResetControlAsync` / `GetControlAsync` are a coherent
  trio on both platforms: a caller can record where a control was, move it, and
  put it back. That was the motivating use case and it did not previously work
  on Linux at all.
- **Linux behaviour changes.** A `SetControlAsync` that previously no-opped
  against an auto loop now takes effect. Any consumer relying on the old
  behaviour was relying on the write being ignored.
- The neutrality claim in D1 is weaker than ADR-0064's and says so. The next
  backend is the test; the contract has a documented extension path rather than
  a pretence that the question is settled.
- `Unknown` at ordinal 0 means a default-constructed `CameraControlMode` asserts
  the least, matching what ADR-0073 D4 did for `MonitorLayoutAvailability`.

## Follow-up (deliberately deferred)

- **No batch read.** A consumer snapshotting N controls pays N round trips
  where `GetControlsAsync` does one enumeration pass. Worth a
  `GetControlStatesAsync` if a consumer feels it; none does yet.
- **fd-reuse hazard on Linux.** `GetControlAsync` checks `ThrowIfNotOpen()` and
  then `ioctl`s on `_fd` from a `Task.Run`, so a concurrent `DisposeAsync` can
  close and the fd be recycled between the check and the call. **Pre-existing
  and shared with `Set`/`Reset`** — not introduced here — but the `Task.Run`
  widens the window. A `SafeHandle` or a reader gate around fd use is the fix,
  and it belongs to all three methods at once.
- **None of the V4L2 failure paths above are covered by an automated test.**
  `EnforceCompanionMode`, the read-back, and the operational-failure branch in
  `GetControlAsync` all live in the backend, below the `ICameraBackend` seam the
  fake substitutes at — so `InMemoryCameraBackend` cannot reach them and the CI
  `device-tests` job is skipped for want of a camera. Testing them would mean an
  indirection over `ioctl` itself, which is a larger change than this one.
  Tracked with the rest of the unverified Linux surface in `#255`.
- **`V4L2_CTRL_FLAG_INACTIVE`** is not checked. `G_CTRL` still returns a valid
  reading for an inactive control and reporting it alongside `Automatic` is the
  right answer, so this is noted rather than open.
