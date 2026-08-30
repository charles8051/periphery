---
title: "ADR-0071: Indirect displays are Virtual on both planes, and a native-enum translation defaults to its unknown sentinel"
status: "Accepted"
status_note: "for Decisions 2-4 (Decision 1 superseded by ADR-0072; the title states it and is left as the historical record)"
date: "2026-07-25"
authors: "@charles8051"
tags: ["architecture", "decision", "monitor", "windows", "displayconfig", "deviceinfo", "virtual-display", "iddcx", "cross-platform"]
supersedes: ""
superseded_by: "ADR-0072 (Decision 1 only)"
---

# ADR-0071: Indirect displays are `Virtual` on both planes, and a native-enum translation defaults to its unknown sentinel

## Status

> ### ⚠️ Read this first — the title is no longer accurate
>
> **Decision 1 ("an indirect display is `Virtual` on both planes") is superseded
> by [ADR-0072](0072-indirect-is-its-own-connection-kind.md).** `INDIRECT_WIRED`
> now maps to `DisplayConnectionKind.Indirect`, not `Virtual`, because DisplayLink
> adapters and USB-C docks drive **real** panels through that same technology.
> The document's title and Decision 1 heading are left unedited as the historical
> record of what was decided on 2026-07-25; they do **not** describe current
> behaviour.
>
> **Decision 2 — enumerate an open native enumeration in full and default to the
> unknown sentinel — is NOT superseded.** It stands, still governs both planes,
> and ADR-0072 is an application of it rather than a departure from it.
> Decisions 3 and 4 also stand.

Resolves the divergence **ADR-0070** Decision 2 deliberately deferred, and
generalises the mapping rule that both planes now follow. Affirms — does not
supersede — ADR-0070 Decision 4 (the two planes keep separate types). Extends the
Win32 DisplayConfig enrichment tier settled by **ADR-0018**, kept fresh by
**ADR-0066**, and shares the "`null` is unmeasured" posture of **ADR-0068**.

> **Sequencing note.** ADR-0070 lives on PR `#197`, which is still open at the time
> of writing; the code and CHANGELOG merged in `#200` already cite it. Until `#197`
> merges, those citations — and this ADR's references — point at a document not
> yet on `main`.
>
> **Acceptance is contingent on ADR-0070 D2 and D4 surviving review in their
> current form.** This ADR quotes D2 (both indirect technologies map to `Virtual`)
> and restates D4 (the planes keep separate types). If `#197` is rejected, or those
> two decisions are renumbered or reworded before it merges, this ADR's citations
> must be re-checked before it moves from Proposed to Accepted — Decision 1's
> *substance* stands on its own (the discovery plane should not call a
> software-presented display a cable), but its claim of *parity with the control
> plane* does not survive the control plane changing its answer.
>
> **Outcome (2026-07-25, same day): the contingency fired, exactly as written.**
> `#197` merged — but D2 was **reworded during its review** to keep `IndirectWired`
> and `IndirectVirtual` distinct, because `INDIRECT_WIRED` is also how DisplayLink
> adapters and USB-C docks drive real panels. So the re-check this clause demanded
> was performed, and it went the way the clause anticipated: the substance held,
> the parity claim did not. ADR-0072 supersedes Decision 1 accordingly. **D4
> survived unchanged**, so Decision 4 below needs no revision. This note is left
> in place because it is the mechanism that made the collision recoverable
> instead of silent.
>
> **Status resolved 2026-07-26.** This ADR sat at `Proposed` after the contingency
> above had already fired and been answered, which left a reader unable to tell
> whether the re-check was still outstanding. It is not: Decision 1 is superseded
> by ADR-0072, and Decisions 2, 3 and 4 stand, so the frontmatter now says exactly
> that. The title still asserts Decision 1's falsified claim and is **deliberately
> unedited** — see the note above. For why virtuality is not observable at all,
> which settles the underlying question this ADR was reaching for, see
> [ADR-0073](0073-observations-not-verdicts.md).

## Context

### The reported defect

`WindowsDisplayConfigEnricher.MapConnectionKind` translates the CCD
`DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY` of a monitor's active path into
`DeviceInfo.DisplayConnectionKind`. It mapped exactly one indirect technology:

```csharp
OUTPUT_TECH_INDIRECT_VIRTUAL => DisplayConnectionKind.Virtual,
OUTPUT_TECH_OTHER            => DisplayConnectionKind.Unknown,
_                            => DisplayConnectionKind.Wired,   // ← the defect
```

A Windows IddCx indirect display — the fleet's `IddSampleDriver` rigs — reports
`INDIRECT_WIRED` (16), **not** `INDIRECT_VIRTUAL` (17). Sixteen was not even a
named constant, so every IddCx display fell through the default and was reported
as a physical cable.

### The defect is the default arm, not the missing constant

Adding `INDIRECT_WIRED` fixes the observed symptom and leaves the mechanism
intact. `_ => Wired` is not a default; it is an **assertion about hardware the
code failed to recognise**. Its failure mode is the worst kind: no exception, no
log, a plausible answer a consumer cannot distinguish from a measured one.

`DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY` is an **open** enumeration — Microsoft
added `MIRACAST` (15) in Windows 8.1, `INDIRECT_WIRED` / `INDIRECT_VIRTUAL`
(16/17) for IddCx, and `DISPLAYPORT_USB_TUNNEL` (18) later still. A default that
asserts "cabled" guarantees the same bug for whatever Microsoft adds next.

The type already carried the honest answer: `DisplayConnectionKind.Unknown`
existed and was reachable only from the explicit `OTHER` arm.

### Why flipping the default alone would have been wrong

Several values the mapper never named are *genuinely* cabled: `SVIDEO` (1),
`COMPOSITE_VIDEO` (2), `COMPONENT_VIDEO` (3), `D_JPN` (8), `SDI` (9),
`UDI_EXTERNAL` (12), `SDTVDONGLE` (14), `DISPLAYPORT_USB_TUNNEL` (18). A bare
`_ => Unknown` would have demoted all of them from a correct `Wired` to
`Unknown` — trading a false positive for a false negative. **Enumerating the
native enum in full is what earns the honest default.**

### What the investigation found beyond the reported bug

Three facts, each verified against source rather than assumed:

1. **There is exactly one producer.** `WindowsDisplayConfigEnricher.Enrich` is the
   sole writer of `DeviceInfo.DisplayPhysicalConnector` /
   `DeviceInfo.DisplayConnectionKind`, from `MapConnector` / `MapConnectionKind`.
   `WindowsMonitorEnrichment.MergeArrival` only carries a prior value forward and
   measures nothing. There was never a competing mapping to disagree with.

2. **The public enum docs name a source that no longer exists.**
   `DisplayConnectionKind`, `DisplayConnector`, and `DisplayUsageKind` each claim
   to be "Sourced from `Windows.Devices.Display.DisplayMonitor.*`". That WinRT
   enricher was deleted in commit `47c7eb3` under **ADR-0018**, which replaced it
   with Win32 DisplayConfig and removed the Windows TFMs outright. A WinRT
   projection could not compile in this repo today.

3. **The tier is Windows-only by implementation.** Neither `LinuxDeviceProvider`'s
   `ToDeviceInfo` nor `MacOSDeviceProvider`'s `DeviceInfo` construction sets *any*
   display metadata — not just these two fields. Both are `null` off Windows for
   want of a producer, not because the concept is absent. The enum *shape*
   (`Internal`/`Wired`/`Wireless`/`Virtual`) is genuinely portable — DRM exposes
   the connector type, including a `Virtual` connector, in the sysfs node name
   `Periphery.Monitor` already walks — but nothing maps it.

## Decision

### Decision 1 — An indirect display is `Virtual` on both planes

> **⚠️ SUPERSEDED by ADR-0072.** This decision's *substance* held — the discovery
> plane must not call a software-presented display a cable — but its **parity
> claim did not**, exactly as the Status section's contingency clause warned.
> ADR-0070 D2 was rewritten during review to keep `IndirectWired` and
> `IndirectVirtual` **distinct**, because `INDIRECT_WIRED` is also how DisplayLink
> adapters and USB-C docks drive *real* panels — so folding reports real glass as
> virtual. `INDIRECT_WIRED` now maps to the new `DisplayConnectionKind.Indirect`.
> **Decision 2 below is unaffected and still governs.**

Both `INDIRECT_WIRED` and `INDIRECT_VIRTUAL` map to `DisplayConnectionKind.Virtual`
on the discovery plane, matching `CcdOutputTechnology.FromCcd`'s
`MonitorOutputTechnology.Virtual` on the control plane (ADR-0070 D2). Either
indirect kind is a software-presented display, not a panel on a real port.

ADR-0070 D2 recorded this divergence as out of scope because the discovery plane
is the kiosk consumer's surface and the fleet consumer is barred from it. That reasoning governed
*who may consume* the surface; it was never a claim that `Wired` was correct.
**Ownership is not a licence to leave a surface wrong**, and the investigation
confirmed the change costs nothing: none of the four downstream repositories
surveyed — the kiosk consumer's library and its kiosk application, the fleet
consumer, and frame-flow — reads `DisplayConnectionKind` at all. The two planes now answer the same question the same way.

### Decision 2 — A translation from an open native enumeration enumerates every known value and defaults to its unknown sentinel

For any mapping from a platform's native enumeration into a Periphery semantic
value that is **reported** to consumers:

- every value the native enumeration defines is named explicitly, and
- the default arm yields the target type's **unknown sentinel**
  (`DisplayConnectionKind.Unknown`, `DisplayConnector.Unknown`,
  `MonitorOutputTechnology.Other`), never a substantive value.

The default arm's meaning becomes exactly *"the platform reported something this
build has never heard of"* — which is a fact, where "cabled" was a guess. A value
with no faithful member stays at the sentinel rather than being folded into a
neighbour: `UDI_EXTERNAL` remains `DisplayConnector.Unknown` because no
`DisplayConnector` member models UDI, and inventing one would reintroduce exactly
the dishonesty this decision removes.

**This rule is conditional, and the condition is load-bearing.** It applies when
the native enumeration is *open* and the mapped value is *reported*. It does not
apply to `DisplayGeometry.FromCcdRotation`, whose `_ => Landscape` default is
correct and stays: CCD rotation is a **closed** four-value set, `DisplayOrientation`
has **no** unknown member by design, and the value is consumed as *arithmetic*
(`IsPortrait` decides whether `DesktopBounds` transposes the source surface) rather
than reported as a fact. There is no "don't know" branch for a rectangle to take.
A substantive default is defensible precisely when those conditions hold — but it
must be a recorded choice, as it is there, not an accident, as it was here.

### Decision 3 — The discovery-plane display tier is Windows-only, and says so

The XML docs on `DisplayConnectionKind`, `DisplayConnector`, and
`DisplayUsageKind` are corrected to name Win32 DisplayConfig — the actual source
per ADR-0018 — instead of the deleted WinRT API, and to state that non-Windows
providers leave them `null`.

`null` continues to mean **unmeasured**, never "not virtual" and never "no such
display" — the same posture ADR-0068 fixed for orientation. A consumer that needs
"is this screen virtual" must treat `null` as *unknown*, not as a negative.

`DisplayUsageKind` is documented as having no producer on any platform, per
ADR-0018 NEG-002. It is retained, not deleted: the enum is a modelled part of the
monitor contract whose Win32 equivalent does not exist, and removing it would
discard a contract slot a future backend can fill.

### Decision 4 — The two planes keep separate types

Unchanged from ADR-0070 D4, restated because Decision 1 might be read as
converging them. It does not. `Periphery.Monitor` depends on `Periphery`, never
the reverse, so the control plane must not be forced to speak the discovery
plane's types. `MonitorOutputTechnology` and `DisplayConnectionKind` remain
distinct types; they are two independent reads of the same physical fact, each
with its own backend translator.

> **Amended per ADR-0072.** This decision stands, but its original wording said
> the two types "happen to agree on virtuality." They no longer agree on *that*
> — the control plane has `IndirectWired`/`IndirectVirtual`, the discovery plane
> `Indirect`/`Virtual`. What they agree on is the **shape**: both keep the two
> indirect technologies apart, so a consumer holding both maps member-for-member.
> D4's actual claim — separate types, dependency direction preserved — is
> untouched.

## Consequences

- An IddCx display's `DeviceInfo.DisplayConnectionKind` changes from `Wired` to
  `Virtual`. No consumer reads the field, so nothing observes the change today —
  which is precisely why it was the right moment to make it.
  *(**Superseded by ADR-0072:** the landing value is `Indirect`, not `Virtual`.
  The "no consumer reads the field" premise was re-verified there across every
  consuming repository and all file types, not the four named here, and it holds.)*
- An output technology this build does not name now reports `Unknown` instead of
  `Wired`. For an up-to-date build this arm is unreachable; it becomes reachable
  only when Windows grows a new technology, which is when `Unknown` is the honest
  answer.
- `DisplayConnector.AnalogTv` becomes producible. It was a public enum member no
  input could reach — the whole analogue-television family fell through to
  `Unknown` — so this closes a dead branch of the public contract rather than
  adding a new one.
- **Not verified on hardware.** The mapping is pure and exhaustively unit-tested,
  and the constant values are checked against the `wingdi.h` reference, but no
  IddCx rig has confirmed the end-to-end result. The claim rests on documentation
  and tests, not observation.
- **ADR-0047 (`0047-device-tags-vs-multi-category.md`) is now inaccurate
  off-Windows.** Where it rejects a `Wired`/`Wireless` device tag, it argues that
  for displays "`DisplayConnectionKind` covers it… No enricher work needed". On
  Linux and macOS it covers nothing, because nothing populates it there. Recorded
  here rather than silently edited; correcting ADR-0047 is its own change.
- The Linux gap is now specified rather than merely absent. `Periphery.Monitor`'s
  `I2cDdcMonitorBackend` already resolves DRM connector syspaths of the form
  `…/drm/card1/card1-HDMI-A-1` and discards the connector-type token; parsing it
  would populate both fields, including `Virtual-1` → `Virtual`. This ADR does
  not implement that — it records that the enum shape is portable and the data is
  in reach, so a future backend is filling a gap, not reinterpreting a contract.

## Alternatives considered

- **Add `INDIRECT_WIRED` and stop.** Rejected: it fixes the symptom and preserves
  the mechanism. The next technology Microsoft adds reproduces the bug exactly.
- **Flip the default to `Unknown` without enumerating the enum.** Rejected: it
  demotes genuinely-cabled outputs (`SDI`, `SVIDEO`, `D_JPN`, `SDTVDONGLE`) to
  `Unknown`, trading a false positive for a false negative and losing real
  information.
- **Leave the discovery plane alone, per ADR-0070's scoping.** Rejected. The
  scoping was about consumption boundaries, not correctness, and the surface had
  no consumers to protect. Deferring would have left two planes giving different
  answers to one question with no date for reconciling them.
- **Merge `DisplayConnectionKind` and `MonitorOutputTechnology`.** Rejected:
  violates the dependency direction (ADR-0070 D4). Agreement on one member is not
  a reason to fuse two contracts.
- **Delete `DisplayUsageKind` as dead surface.** Rejected: it is a modelled
  contract slot with no Win32 source, not a mistake. Documenting it as unpopulated
  costs nothing; deleting a public enum to re-add it later costs a breaking change
  in both directions.
