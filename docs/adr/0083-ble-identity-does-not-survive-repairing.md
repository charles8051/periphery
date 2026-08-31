---
title: "ADR-0083: BLE device identity is address-derived and did not survive re-pairing on the device measured"
status: "Proposed"
status_note: "Measured once, on one BLE mouse, on one machine. The decision it argues for — document, do not synthesize — does not depend on the sample size. The generalisation to BR/EDR does, and is left open."
date: "2026-08-30"
authors: "@charles8051"
tags: ["architecture", "decision", "bluetooth", "ble", "identity", "device-info", "tracking", "reconnect", "windows"]
supersedes: ""
superseded_by: ""
depends_on: ["0001-device-tracking-handles.md", "0006-device-profile-single-device-resolution.md", "0030-application-level-reconnect.md", "0034-device-group-tracker.md", "0073-observations-not-verdicts.md", "0074-device-role-group-exclusive-role-assignment.md"]
---

# ADR-0083: BLE device identity is address-derived and did not survive re-pairing

## Status

Proposed. The evidence is a single unpair/re-pair of a single BLE mouse on one
Windows 11 machine, captured while investigating the Bluetooth activity-semantics
question recorded in `ARCHITECTURE.md` §10.6.2.

That sample supports the decision below, which is a decision *not* to build
something. It does not support generalising the finding — not to BR/EDR, and not
even to every LE peripheral, since an LE device on a public or static random
address may well keep its identity across a re-pair. This ADR does not
generalise it; see [Open questions](#open-questions).

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

Windows names the devnode after the address it has, so if the address changes,
the instance ID changes and everything derived from it moves too. That part is
mechanical and is not in question.

Why the address changed is **not** established by this measurement, and the
explanation must be scoped accordingly. LE permits several address types:

| Address type | Stable across re-bond? |
|---|---|
| Public | Yes — an assigned, fixed address |
| Static random | Yes, per power cycle, and typically for the device's lifetime |
| Resolvable private (RPA) | No — rotates; resolvable only with the bonding IRK |
| Non-resolvable private | No |

Only the RPA case requires IRK resolution, and only the private types are
expected to change. So "LE addresses are not durable identity" is true of *some*
LE devices, not all of them. What was observed here is one device whose address
changed across a re-pair, which is consistent with the RPA case and with a
device that issues a fresh address on re-bond, and does not distinguish them.

Where it *is* an RPA, no device-tree projection can see through the rotation,
because the IRK lives in the pairing store. That is the protocol working as
designed rather than a Windows defect. Where the peripheral uses a public or
static random address, the premise does not apply at all and the instance ID may
well be stable — untested here.

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

**Every row below is the observed result for one LE HID mouse whose address
changed across one re-pair.** It is not a general claim about LE, and certainly
not about Bluetooth. A peripheral on a public or static random address may keep
its instance ID and container across a re-pair; that case is untested.

| Member | Survives a link drop | Survives a re-pair (LE HID, n=1) |
|---|---|---|
| `VendorId` / `ProductId` | Yes | Yes |
| `Id` | Yes | No |
| `ContainerId`, `DeviceFilter.WithContainerId` | Yes | No |
| `SerialNumber` | n/a — always `null` on Bluetooth nodes | n/a |

A consumer should read this as "do not assume `Id` or `ContainerId` survives a
re-pair", which is safe on any transport, rather than as "they never survive",
which is asserted well beyond the evidence.

**Not yet done.** This belongs in the XML docs on `DeviceInfo.Id`,
`DeviceInfo.ContainerId`, and `DeviceFilter.WithContainerId`, because that is
where a consumer choosing a matcher actually reads. Those API docs are
unchanged as of this ADR; adding them is follow-up work, tracked by this
decision rather than delivered by it. `ARCHITECTURE.md` §10.6.2 carries the
measurement in the meantime.

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

### D5 — The reconciliation contract is stated, even though the policy is not

D1 declines to synthesize an identity. That is not a licence to leave the
lifecycle undescribed: a consumer cannot write correct code against "you decide"
without knowing what it will observe. Describing the sequence costs nothing and
synthesizes nothing.

What a consumer observes across a re-pair, in order:

1. `Disappeared` for every node of the old subtree, including the `…\DEV_…`
   node. The old `Id` and `ContainerId` never return.
2. `Appeared` for a new subtree carrying the same `VendorId` / `ProductId` and a
   different `Id` and `ContainerId`.

Nothing in that sequence marks step 2 as the same physical device as step 1.
A consumer holding per-device state must therefore decide three things, and
Periphery answers none of them:

- **Retirement.** When to drop the old record. Never dropping it means a
  duplicate per re-pair; dropping it on `Disappeared` cannot be distinguished
  from a device that is merely out of range.
- **Reconciliation.** Whether the new subtree is the same device. VID/PID plus
  name is the available evidence and is ambiguous between two units of one
  model — which is exactly why D1 refuses to make the call centrally.
- **Role release.** For ADR-0074 group members, whether the vacated role is
  freed for the new candidate (see D4).

The consumer that can answer these has deployment knowledge — how many of this
model exist, and which one is meant to be here — that Periphery does not.
Recording the questions is the contract; answering them is the consumer's.

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
- **NEG-002.** The whole D2 table rests on one re-pair of one LE HID mouse, and
  the table says so in its header rather than in a footnote a reader can skip.
  The safe reading — "do not assume `Id` or `ContainerId` survives" — holds
  regardless; the categorical reading does not.
- **NEG-003.** D2 adds a third vocabulary — durability scope — alongside
  `Category` and `Tags`. Three axes on identity is a lot to hold. Mitigated by
  keeping it in XML docs at the point of use rather than as a public type.
- **NEG-004.** D5 describes the reconciliation problem without solving it. A
  consumer that wanted a decision gets a well-specified question instead, and
  two consumers may answer it differently for the same hardware.

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
