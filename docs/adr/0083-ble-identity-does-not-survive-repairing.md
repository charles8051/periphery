---
title: "ADR-0083: BLE device identity is address-derived and does not survive re-pairing"
status: "Proposed"
status_note: "Measured once, on one BLE mouse, on one machine. The decision it argues for — document, do not synthesize — does not depend on the sample size. The generalisation to BR/EDR does, and is left open."
date: "2026-08-30"
authors: "@charles8051"
tags: ["architecture", "decision", "bluetooth", "ble", "identity", "device-info", "tracking", "reconnect", "windows"]
supersedes: ""
superseded_by: ""
depends_on: ["0001-device-tracking-handles.md", "0006-device-profile-single-device-resolution.md", "0030-application-level-reconnect.md", "0034-device-group-tracker.md", "0073-observations-not-verdicts.md", "0074-device-role-group-exclusive-role-assignment.md"]
---

# ADR-0083: BLE device identity is address-derived and does not survive re-pairing

## Status

Proposed. The evidence is a single unpair/re-pair of a single BLE mouse on one
Windows 11 machine, captured while investigating the Bluetooth activity-semantics
question recorded in `ARCHITECTURE.md` §10.6.2.

That sample supports the decision below, which is a decision *not* to build
something. It does not support generalising the finding to BR/EDR, and this ADR
does not do so — see [Open questions](#open-questions).

---

## Context

### What was measured

A paired BLE mouse was removed in Windows Settings and re-paired, with a
`CM_Register_Notification` listener and a 1 s device-tree poller running on one
timeline. Unpair removed the whole subtree with full `DEVICEINSTANCEREMOVED` /
`DEVICEINTERFACEREMOVAL` edges; re-pair created it again with
`DEVICEINSTANCEENUMERATED` / `DEVICEINTERFACEARRIVAL` / `DEVICEINSTANCESTARTED`.

The subtree that came back was not the one that left:

| Field | Survives re-pairing? | Detail |
|---|---|---|
| `VID&…_PID&…_REV&…` embedded in the instance ID | **Yes** | byte-identical before and after |
| `DeviceInfo.Id` | **No** | every instance ID in the subtree re-issued |
| `DeviceInfo.ContainerId` | **No** | `{ece5e2dd-…}` → `{180fb3c1-…}` |
| `DeviceInfo.SerialNumber` | n/a | never populated for Bluetooth nodes |

The device address changed. That is established rather than inferred: the capture
tool masks addresses against a set it learns at startup, and the re-pair events
rendered as the tool's unknown-value placeholder, which only occurs for a value
absent from that set. The pre-unpair address had been learned at startup.

`SerialNumber` is a non-issue for a reason recorded here so nobody
"fixes" it later. `WindowsDeviceProvider.ParseSerialNumber` returns `null` when
the last instance-ID segment contains `&`, and every Bluetooth instance ID's last
segment does (`a&ede6a8a&0&…`, `b&2798c6b0&1&0015`, `c&10ee39f5&0&0000`). The
field is not unstable here; it is absent.

### Why this happens

Bluetooth LE separates the address a peripheral advertises from any durable
identity. A device may advertise a resolvable private address that rotates, and
the identity behind it is recoverable only with the bonding IRK. Windows names
the devnode after the address it has, so a new address means a new instance ID,
and everything derived from it moves too.

This is the protocol working as designed, not a Windows defect and not something
a device-tree projection can see through.

### An observation, offered as a hypothesis

Every `ContainerId` seen this session is a **version 5** UUID. The third group
starts with `5` in each: `8399-501f`, `e78d-50c0`, `b0da-537b`. Version 5 is a
name-based (SHA-1) UUID.

If the container is hashed over something including the device address, that
explains why it moves with the address, and it predicts that BR/EDR containers
are stable because BR/EDR addresses are public and fixed. **This is a hypothesis
from three samples of a version nibble.** It is recorded because it is cheap to
test and would settle a question this ADR leaves open, not because it is known.

---

## Decision

### D1 — Periphery does not synthesize a durable BLE identity

No `StableId`, no `IdentityKey`, no VID/PID-plus-name composite presented as an
identity. Periphery surfaces the instance ID, the container, and the hardware IDs
it observed, and the consumer decides what to key on.

The reasoning is ADR-0073's, and it applies more sharply here than it did to
display virtuality. Resolving an address rotation requires the bonding IRK, which
lives in the pairing store and is not reachable from the device-tree projection.
Any identity Periphery composed would be a fingerprint — VID/PID plus a friendly
name — presented as a fact, and a consumer could not tell the difference after
the fact. Two mice of the same model would collide; a renamed device would
split.

The consumer that needs durable identity across re-pairing has something
Periphery does not: knowledge of which devices are supposed to exist.

### D2 — Matcher durability is documented on each matcher

Each identity-bearing member states how far its durability reaches:

| Member | Survives a link drop | Survives a re-pair |
|---|---|---|
| `VendorId` / `ProductId` | Yes | Yes — LE, measured once (see NEG-002) |
| `Id` | Yes | **No** |
| `ContainerId`, `DeviceFilter.WithContainerId` | Yes | **No** |
| `SerialNumber` | n/a — always `null` on Bluetooth nodes | n/a |

This goes in the XML docs on `DeviceInfo.Id`, `DeviceInfo.ContainerId`, and
`DeviceFilter.WithContainerId`, because that is where a consumer choosing a
matcher actually reads. `ARCHITECTURE.md` §10.6.2 carries the measurement.

### D3 — The reconnect consequence is named, not fixed

ADR-0030 / ADR-0055 reconnect re-resolves through the tracker rather than holding
a devnode handle, so a changed `Id` is survivable **provided the profile does not
key on `Id` or `ContainerId`**. A profile that does will not reconnect after a
re-pair; it will present as a device that disappeared permanently, while a
new device it does not match sits in the tree.

No behaviour change is proposed. The failure is a consequence of the profile's
choice of matcher, and D2 is what puts that choice in front of the author.

### D4 — `DeviceGroupTracker` inherits the question

ADR-0074's exclusive role assignment operates over a shared candidate pool. A
re-paired device enters that pool as a new candidate while the old identity is
gone. Whether the vacated role is released, and whether the new candidate is
eligible for it, is a question ADR-0074 should answer explicitly rather than
inherit by accident. Flagged there; not decided here.

---

## Consequences

### Positive

- No invented identity that a consumer cannot audit. The failure mode is a
  device that stops matching, which is visible, rather than a device that matches
  the wrong thing, which is not.
- The matcher-durability table is a small, honest artefact that answers the
  question a profile author is actually asking.
- Costs nothing at runtime. D1 through D4 are documentation and one flag on
  another ADR.

### Negative

- **NEG-001.** A consumer that wants "the same mouse, across a re-pair" gets no
  help from Periphery and must supply its own knowledge. That is the intended
  outcome, and it is still a gap from that consumer's point of view.
- **NEG-002.** The durability table asserts `VendorId` / `ProductId` are
  pairing-durable on the strength of one re-pair of one device. It is the
  weakest row and is marked as such in the table.
- **NEG-003.** D2 adds a third vocabulary — durability scope — alongside
  `Category` and `Tags`. Three axes on identity is a lot to hold. Mitigated by
  keeping it in XML docs at the point of use rather than as a public type.

---

## Open questions

1. **Rotation or re-bond?** Is the new address a rotating RPA, or a fresh
   identity address issued on re-bond? One sample cannot distinguish them.
   Repeated re-pairs of the same device, plus a device known to advertise a
   public address, would.
2. **BR/EDR is untested.** BR/EDR addresses are public and fixed, so re-pairing
   plausibly preserves the instance ID and container. If it does, this ADR
   narrows to LE only, which is a materially smaller claim. This is the single
   cheapest measurement that would improve the ADR, and it was not taken because
   re-pairing the available BR/EDR keyboard was more disruptive than re-pairing
   the mouse.
3. **The v5-container hypothesis.** If `ContainerId` is a name-based UUID over
   the address, durability becomes predictable per transport rather than a
   per-device unknown. Testable against question 2's data at no extra cost.
4. **Non-HID profiles.** Everything here was measured on HID peripherals. A
   headset or serial-over-Bluetooth device may present a different subtree shape
   on re-pair.
