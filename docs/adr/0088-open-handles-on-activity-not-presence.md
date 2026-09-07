---
title: "ADR-0088: Open handles on activity, never on presence"
status: "Accepted"
status_note: "Rule already implemented by DeviceProxy / DeviceSessionHost; written down because two consumers bypassed them and got it wrong independently. Both fixed on main (#194, #196, #197). The XML remarks on DeviceWatcher.Appeared / Activated that state the rule landed with this ADR (#195)."
date: "2026-09-06"
authors: "@charles8051"
tags: ["architecture", "decision", "device-proxy", "device-watcher", "state-model", "adr-0004"]
supersedes: ""
superseded_by: ""
depends_on: ["0004-two-level-device-state-model.md", "0054-windows-property-freshness-events-over-polling.md"]
---

# ADR-0088: Open handles on activity, never on presence

**Tracks:** `DeviceProxy`, `DeviceProxyBase`, `DeviceSessionHost`, `DeviceWatcher`, and every consumer that opens a device
**Depends on:** ADR-0004 (two-level state model), ADR-0054 (Windows raises no soft deactivation)

---

## Status

**Accepted.** The rule is already what `DeviceProxy`, `DeviceProxyBase` and `DeviceSessionHost`
implement. This ADR writes it down because two consumers hand-rolled their own subscriptions and
got it wrong independently, and one of them was wrong in shipped code.

---

## Context

ADR-0004 gives two orthogonal axes:

| | Means | Event |
|---|---|---|
| **Present** | the OS has an entry for this device | `Appeared` / `Disappeared` |
| **Active** | the driver is started and the device is usable | `Activated` / `Deactivated` |

Opening a handle requires a started driver. So the correct edge is not a matter of taste or of
which event happens to be convenient: presence alone does not establish that a driver has
started, so an open on the presence edge can fail.

The two axes are orthogonal, not exclusive. An active device is also present, and opening it is
correct - what makes `Appeared` the wrong trigger is that it also fires for devices which are
present and not yet active, and carries no way to tell the two apart.

### The library already answers this, and it is not discoverable enough

`DeviceProxy.OpenAsync` names its callbacks `onActivated` and `onDeactivated`. `DeviceProxyBase`
and `DeviceSessionHost` gate on `IsActive` or `ActivityStatus == Active` at nine sites between
them, and neither contains a presence gate anywhere.

That is the whole answer. But the rule lives only in parameter names and in the internals of two
types, so a consumer that subscribes to `DeviceWatcher` directly has nothing telling it which edge
to use — and `Appeared` is the one that reads like "the device showed up".

### Two consumers got it wrong

**`TrackerDeviceWaitSource`** (`Periphery.FlashAnything`) completed a bootloader wait on any
non-`Absent` tracker state, which includes `Present`. The caller opens a USB or serial handle the
moment the wait returns, so it could be handed a devnode whose driver had not started — the failure
`BootloaderEntryOrchestrator` already documents from issue #251, where a wasted attempt eats the
recovery budget on healthy hardware. Its post-flash return wait had the same shape, so a board that
enumerated but never started would be reported as having returned to application mode.

**`TreehopperControlService`** subscribed to `Appeared` only and called `UsbDevice.OpenAsync` from
the handler. The open threw and was swallowed, so a live-plugged board silently showed no firmware
version, with no second chance.

Both were invisible on Windows for the library's whole history, because Windows `DeviceAppeared`
never fired for a live arrival (issue #177). Neither was invisible on Linux or macOS, which raise
`DeviceAppeared` on arrival before raising `DeviceActivated`.

---

## Decision

### D1 — Open on `Activated`, close on `Deactivated`

Any code that acquires a handle, opens a port, starts a session, or otherwise performs I/O against
a device binds to the **activity** axis. `Appeared` is not an acceptable trigger for I/O, and
"non-`Absent`" is not an acceptable gate.

### D2 — `Appeared` is for inventory

Presence answers *does this device exist, do I care about it, should I list or track it*. That is a
real and useful signal, and a handler may legitimately do presence work on it. What it may not do
is open the device.

A handler that needs both splits: the presence work stays on `Appeared`, the I/O moves to
`Activated`.

### D3 — Prefer `DeviceProxy` / `DeviceSessionHost` over a hand-rolled subscription

Both defects came from consumers that subscribed to the watcher directly instead of using the
binding primitives. Those primitives already implement this ADR, and they also absorb the
asymmetry in **D4** that a hand-rolled consumer will not.

---

### D4 - the paired half does not hold on Windows

A consequence of D1 rather than a separate choice, but numbered because D3 turns on it. cfgmgr32 pushes no soft driver-stop signal, so
`DeviceDeactivated` is never raised there (ADR-0054) — a handle opened on activity is torn down
only by `Disappeared`, i.e. by physical removal. A device that stops without leaving the tree, such
as a Bluetooth peripheral going out of range, produces no close edge at all on Windows.

`DeviceProxyBase` absorbs this with its reopen and readiness loops. A hand-rolled consumer will
hold a handle across a soft stop it never hears about. This is the strongest argument for D3, and
it is why D3 is a decision rather than a style preference.

---

## Consequences

### Good

- One rule, stated where a reader can find it, for a mistake made twice independently.
- The rule is already implemented, so adopting it costs consumers a changed subscription rather
  than new machinery.
- It makes issue #177's fix safe to land: a provider that correctly reports "present but not
  started" stops being a hazard once no consumer treats presence as readiness.

### Bad

- D3 is a preference, not something the compiler enforces. A consumer can still subscribe to the
  watcher directly and ignore this.
- Splitting a handler that does presence work and I/O together (D2) is a real change to consumers
  that currently do both on one edge, not a rename.

### Neutral

- No public API change. No behaviour change in `DeviceProxy`, `DeviceProxyBase` or
  `DeviceSessionHost`, which already comply.

---

## Compliance

| Consumer | State |
|---|---|
| `DeviceProxy` / `DeviceProxyBase` / `DeviceSessionHost` | Compliant. Nine activity gates, no presence gate. |
| `TrackerDeviceWaitSource` | Fixed in #194 — gates on `Active`. |
| `TreehopperControlService` | Fixed in #196 and #197 — presence work stays on `Appeared`, the board open moved to `Activated` (#196), and the session is closed on `Deactivated` (#197), per D1 and D2. |
| `FlashAnythingService` | Fixed (#204) — `TargetDetected` is still emitted on `Present` (D2, inventory); autoflash fires on the `Active` tick, the arm-time sweep skips a present-but-not-started target, and a target that goes inactive before its queued flash is re-checked and skipped rather than opened. |

A consumer that opens a device from an `Appeared` handler is a defect under this ADR, whether or
not the platform it runs on currently delivers that event. Two instances were fixed as this ADR was
written, which is what it is for: the rule existed in `DeviceProxy` all along, and the consumers
that bypassed it did so independently. A third, in the same project as the first, was found by
review afterwards (#204); it had been invisible on Windows for the same reason, and #186 made it
reachable there.
