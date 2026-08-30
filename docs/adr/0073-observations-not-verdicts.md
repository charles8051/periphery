---
title: "ADR-0073: Periphery reports observations, not verdicts — display virtuality is not observable, and unmeasured is its own state"
status: "Accepted"
date: "2026-07-26"
authors: "@charles8051"
tags: ["architecture", "decision", "monitor", "windows", "displayconfig", "edid", "virtual-display", "iddcx", "displaylink", "contract"]
supersedes: "ADR-0070 (the Context premise that EDID cannot help)"
superseded_by: ""
---

# ADR-0073: Periphery reports observations, not verdicts

## Status

Closes the question ADR-0070, `#200`, ADR-0071 and ADR-0072 each attacked from a
different angle and none of them settled: **how does a consumer know whether a
screen is real?** Supersedes ADR-0070's Context premise that EDID cannot help.
Affirms ADR-0072 Decision 4 and supplies the reasoning it lacked. Folds in
issue `#210`.

## Context

Four changes over two days tried to answer "is this screen virtual" from the
display topology, and each produced a mapping that measurement then falsified:

| Attempt | Claimed | Measured |
| --- | --- | --- |
| ADR-0070 D2 (original) | IddCx reports `INDIRECT_WIRED` → map it to `Virtual` | IddSampleDriver reports **`Hdmi`** |
| `#200` / ADR-0071 D1 | fold both indirect values into `Virtual` | never fires for any measured rig |
| ADR-0072 | `INDIRECT_WIRED` deserves its own `Indirect` member | correct, but **no observed device produces it** |
| `Virtual`'s own docs | "a Remote Desktop session display or a VM display" | RDP reports `Other`; QEMU reports `Other` |

That is not four independent mistakes. It is one mistake made four times, and it
is worth naming precisely so it is not made a fifth.

### The four evidence layers, and what each can actually answer

Everything reachable was measured on real machines (issue `#205`):

| Layer | Answers | Verdict for "is it real glass?" |
| --- | --- | --- |
| **Output technology** (`MonitorOutputTechnology`, `DisplayConnectionKind`) | how the pixels travel | ❌ **No correlation.** IddSample→`Hdmi`, RDP→`Other`, QEMU→`Other` |
| **Panel identity** (EDID vendor/product) | what the panel *claims to be* | ⚠️ **Fingerprint.** IddSample→`LNX0000`, distinct from every real panel measured |
| **Adapter identity** (parent devnode) | is an indirect display driver involved | ⚠️ **Honest "indirect", still not "virtual"** |
| **Deployment inventory** | which boxes run a synthetic driver | ✅ authoritative, and **external to Periphery** |

The adapter layer is the seductive one, so it was measured too, on the sandbox VM:

```
DISPLAY\LNX0000\…   parent ROOT\DISPLAY\0000        svc WUDFRd        "IddSampleDriver Device"
DISPLAY\QXL0001\…   parent PCI\VEN_1B36&DEV_0100…   svc QxlDod        "Red Hat QXL controller"
DISPLAY\DEFAULT_…   parent ROOT\BasicDisplay\0000   svc BasicDisplay  "Microsoft Basic Display Driver"
```

It looks decisive — `ROOT\` enumerator, `WUDFRd` service — until two things spoil
it. `ROOT\BasicDisplay` is *also* root-enumerated and drives **real** hardware, so
`ROOT\` alone means nothing. And DisplayLink ships as a UMDF/IddCx driver, so it
would present the same adapter shape as IddSampleDriver while driving actual
glass. (That second point is **not measured** — no dock was available. It is
stated as the reason not to trust this layer, not as a finding.)

### Why no layer can answer it

**Windows has no concept of "is there real glass at the end of this," and there
is no API for it, because the OS genuinely does not know.** An indirect display
driver is a driver that *presents* a monitor. Whether photons come out the far
end is outside the operating system's model entirely.

So layers 1–3 are all shared between the real and synthetic cases *by
construction*, not by accident or by an incomplete mapping. No amount of
refining the output-technology mapping — which is what all four prior attempts
were doing — could ever have worked. **The question was being asked of a layer
that does not hold the answer.**

## Decision

### Decision 1 — Periphery exposes evidence; the consumer forms the verdict

Periphery surfaces what it observed. It does not synthesize `IsVirtual`,
`IsReal`, `IsPhysical`, or any equivalent, on any plane, ever.

This is not modesty about a hard problem — it is that the verdict is **not a
function of anything Periphery can see**. Synthesizing one would convert a
fingerprint match or a guess into an authoritative-looking boolean, and a
consumer cannot tell the difference after the fact. A consumer that needs the
verdict combines the evidence below with **deployment knowledge it has and
Periphery does not**.

### Decision 2 — `MonitorLayoutEntry.PanelId` carries EDID identity

`MonitorPanelIdentity` (vendor + product code, with `PnpId` formatted to match
the device-instance-path segment) is populated from the `GET_TARGET_NAME` query
`CcdLayout` **already issues** — the same struct that yields `DeviceId`,
`FriendlyName` and `OutputTechnology`. It costs **no new interop call**; the
fields were being read past and discarded.

It is the cheapest signal that actually separates the fleet's synthetic rigs
(`LNX0000`) from every real panel measured (`ACR0507`, `SAM7089`, `BNQ7F31`,
`ACI24C4`). It is a **fingerprint**, not a fact: it works only because the
driver's author chose that EDID, a different virtual driver bakes a different
one, and nothing stops a real panel claiming anything. The XML docs say so where
a caller will read them.

**This supersedes ADR-0070's Context premise.** That ADR excluded EDID because
*"a dual-IddSample box's two virtual displays share one baked EDID, so the serial
is identical."* True — and answering a different question. EDID cannot tell
**rig A from rig B** (disambiguation). It can identify them **as a class**
(classification), which is the only thing an `IsVirtual` check ever needed. The
two questions were conflated, and the conflation cost four attempts.

### Decision 3 — Adapter identity is documented, not surfaced here

The parent-devnode evidence is real and worth knowing about, and it has one
property topology does not: **it is readable from session 0**, because the
device tree is global.

It is deliberately *not* added to `MonitorLayoutEntry`:

- `MonitorLayout` is documented as a **zero-handle** read (ADR-0059). Adding a
  parent-devnode property query per monitor changes its cost profile for a value
  most callers will not use.
- The **discovery plane already models the device tree** (ADR-0002), and
  `MonitorLayoutEntry.DeviceId` joins to `DeviceInfo.Id` by construction
  (ADR-0059 D2, issue `#190`). A consumer that wants the adapter walks it there.

Duplicating device-tree facts into the control plane would blur a boundary that
currently pays for itself. If a consumer demonstrates a real need, that is a
future ADR with a measured cost, not a speculative field.

### Decision 4 — `MonitorLayoutAvailability.NotMeasured` is the zero value (issue `#210`)

The same principle, applied to the absence of a read rather than the absence of
a panel. `MonitorLayoutAvailability` (ADR/issue `#207`) modelled the two
**outcomes** of a read and omitted **"no read happened"**, so every consumer with
a non-Windows fallback had to assert something it had not observed.

Both consumers hit it independently within hours, and both shipped a comment
saying the value they chose was wrong:

> *"periphery models the two OUTCOMES of a read, not the absence of one"*
> — the kiosk consumer `PeripheryKioskMonitorReader`
>
> *"NotVisibleFromThisSession is the least-wrong value, not the right one"*
> — the fleet consumer `PeripheryMonitorLayoutReader`

Two independent teams writing a paragraph to explain why a library value is
inaccurate is the library's defect. `NotMeasured` is added **at ordinal 0**, so a
default-constructed value asserts the least rather than — as before — making the
enum's strongest positive claim. This carries the posture ADR-0068 already set
for rotation: *unmeasured is its own state, never a negative result*.

`MonitorSessionVisibility.Classify` never returns it (it runs only after a read);
it belongs to callers that skipped the query, and a test pins both facts.

## Consequences

- **Non-breaking, deliberately.** `PanelId` is an `init`-only property rather
  than a positional record parameter, and the enum renumber is source-compatible
  (ordinals are opaque per ADR-0064; the JSON contract serializes by name). Both
  consumers had already absorbed two constructor breaks in one day; a third, for
  a field they never construct, would have been churn charged to them for our
  convenience. The asymmetry with the other fields is a trade, recorded so it
  reads as a choice rather than an oversight.
- **the downstream consumer is unblocked.** `ScreenInfo.IsVirtual` becomes a match of
  `PanelId.PnpId` against a maintained known-synthetic list (`LNX0000`), or a
  deployment assertion — explicitly a fingerprint, named as one.
- **`MonitorOutputTechnology` keeps `IndirectWired` / `IndirectVirtual`**, and
  `DisplayConnectionKind` keeps `Indirect` / `Virtual`, even though nothing
  measured produces them. They are the faithful mapping of two native values,
  and the decision to keep them distinct (ADR-0072) remains correct — a fold
  would misreport real dock-attached glass. They are simply not the answer to
  this question, and their docs now say so.
- **The decoder is pinned to hardware.** `EdidIdentity.Decode` is byte-order
  sensitive in a way that fails *plausibly* rather than obviously, so its tests
  assert four measured `(rawManufacturerId, rawProductCode) → devicePathSegment`
  pairs where the two sides come from different Windows APIs. The `LNX0000` case
  is labelled as derived-from-a-measured-output, not a measured input.
- **Still unmeasured:** whether anything reports `INDIRECT_WIRED`. A DisplayLink
  or USB-C dock driving real glass is the one plausible population, and none was
  available. Decision 1 makes this safe to leave open — no verdict is built on
  it.

## What this retires

The recurring instinct to add one more member, or refine one more mapping, so
that output technology finally answers virtuality. It cannot, for a structural
reason, and this ADR is the place to point at when it comes up again.
