---
title: "ADR-0072: Indirect is its own connection kind — an indirect display is not knowably virtual"
status: "Accepted"
status_note: "premise falsified by measurement; decision stands on corrected grounds"
date: "2026-07-25"
authors: "@charles8051"
tags: ["architecture", "decision", "monitor", "windows", "displayconfig", "deviceinfo", "virtual-display", "iddcx", "displaylink"]
supersedes: "ADR-0071 (Decision 1 only)"
superseded_by: ""
---

# ADR-0072: `Indirect` is its own connection kind — an indirect display is not knowably virtual

## Status

> ### 📐 MEASURED 2026-07-25 — the motivating premise is FALSE; the decision survives on other grounds
>
> This ADR named one open item as *"the only thing that can promote it past
> Proposed"*: measuring whether an IddCx display actually reports
> `INDIRECT_WIRED`. **That measurement has been run, and it does not** (issue
> `#205`).
>
> On a **Win10 LTSC 2019 sandbox VM** running **ge9's `IddSampleDriver`** with two
> always-on virtual monitors, `periphery monitor layout --json` in the console
> session reports both as **`Hdmi`** — and the discovery plane reports
> **`Wired` / `Hdmi`**. Neither `INDIRECT_WIRED` (16) nor `INDIRECT_VIRTUAL` (17)
> appears anywhere. The driver presents a baked EDID (`DISPLAY\LNX0000\…`, monitor
> name `Linux FHD`) and Windows classifies the target as a cabled HDMI panel.
>
> **Consequently `DisplayConnectionKind.Indirect` never fires for the fleet's
> rigs**, and Decision 2's promise that a consumer can build `IsVirtual` on top of
> these values is **withdrawn** — see Decision 4, added below.
>
> **The decision itself is nonetheless correct, and the measurement strengthens
> it.** The rejected alternative — collapsing `INDIRECT_WIRED` into `Virtual` —
> would have produced false positives on real DisplayLink / dock-attached panels
> **and still missed every fleet rig**, since those report `Hdmi`. Pure downside.
> The conditional this ADR stated explicitly ("*if Windows emits something else …
> the change is no worse than before but does not achieve its stated intent*") is
> precisely what occurred; writing that condition down is what makes the outcome
> legible instead of a silent wrong belief.
>
> Accepted rather than Rejected because **what** it decided still holds; only
> **why** changed. `Indirect` remains the honest classification for anything that
> genuinely reports `INDIRECT_WIRED` — a population that is now itself unverified
> and may be limited to DisplayLink / USB-C docks (no dock was available to test).

**Supersedes ADR-0071 Decision 1** ("an indirect display is `Virtual` on both
planes"). ADR-0071 **Decision 2** — enumerate an open native enumeration in full
and default to the target type's unknown sentinel — is **affirmed and extended**,
not superseded; this ADR is an application of it. Aligns the discovery plane with
**ADR-0070 D2** (the control plane's `MonitorOutputTechnology`).

## Context

Three changes landed within roughly forty minutes, in separate sessions, on the
same question — *what does `DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INDIRECT_WIRED` (16)
mean?* — and they did not agree:

| # | Plane | Decided | `INDIRECT_WIRED` → |
| --- | --- | --- | --- |
| `#200` | discovery (`DisplayConnectionKind`) | merged first | `Virtual` (folded with `INDIRECT_VIRTUAL`) |
| `#197` | control (`MonitorOutputTechnology`) | reversed mid-review | `IndirectWired` (kept distinct) |
| `#201` | ADR-0071 | recorded the fold as cross-plane parity | `Virtual` |

The citation chain inverted: `#200`'s CHANGELOG justified its fold as *"matching the
control plane's mapping (ADR-0070), which already folds both"*, and ADR-0071 D1
rested on the same parity claim — but ADR-0070 D2 had already been rewritten to
reject the fold. ADR-0071 anticipated exactly this, and its own contingency clause
is what triggers this ADR:

> Decision 1's *substance* stands on its own (the discovery plane should not call
> a software-presented display a cable), but its claim of *parity with the control
> plane* does not survive the control plane changing its answer.

That is what happened. The substance survives; the parity claim does not.

### The defect in the fold

`INDIRECT_WIRED` is not an IddCx marker. It is the **general indirect-display
path**, and **DisplayLink adapters and USB-C / Thunderbolt docks drive genuinely
physical monitors through it**, alongside purely synthetic rigs. Windows does not
distinguish the two at this layer.

> **⚠ Amended post-measurement — this paragraph originally said "alongside purely
> synthetic rigs *such as IddSampleDriver*", i.e. it asserted that
> `IddSampleDriver` reports `INDIRECT_WIRED`. That was documentation-sourced and
> is measured FALSE: `IddSampleDriver` reports `Hdmi` (Decision 4). The example
> is removed. The paragraph's actual claim — that `INDIRECT_WIRED` cannot be
> read as "synthetic", because real dock-attached panels use the same path —
> is unaffected, and it is still the reason the fold was rejected.**

So both available answers were assertions the code cannot support:

- `Wired` (pre-`#200`) asserts a cable — wrong for a synthetic IddCx rig. This was
  the reported defect.
- `Virtual` (`#200`) asserts *no physical panel* — wrong for a real monitor on a
  dock, and wrong on precisely the question a "virtual?" attribute exists to
  answer.

Trading one false positive for another is not a fix. The enum had no member for
"presented by a software display driver; panel-attachment unknown," so every
available mapping had to lie.

### This is ADR-0071 D2 applied one level up

ADR-0071 D2 already established the principle: *a value with no faithful member
stays at the sentinel rather than being folded into a neighbour* — and it applied
that reasoning to `UDI_EXTERNAL`, which remains `DisplayConnector.Unknown`
because no member models UDI. `INDIRECT_WIRED` is the identical situation with
one difference: the concept is common enough, and load-bearing enough for the
fleet, to deserve a member rather than the sentinel.

## Decision

### Decision 1 — `DisplayConnectionKind.Indirect` is added, and `INDIRECT_WIRED` maps to it

`INDIRECT_WIRED` → `DisplayConnectionKind.Indirect`; `INDIRECT_VIRTUAL` →
`DisplayConnectionKind.Virtual`. The two stop collapsing.

`Indirect` means: *presented by an indirect display driver rather than a direct
GPU output, and whether a physical panel is attached is not knowable at this
layer.* It asserts only what Windows actually reports.

`Virtual`'s documentation is tightened to say what it now exclusively means — a
software display with **no physical panel** — so the member stops quietly
covering the dock case.

> **Amended post-measurement (issue `#205`).** This decision originally cited "RDP,
> VM" as the examples of `Virtual`. Both were documentation-sourced and are
> **measured wrong**: a live Remote Desktop session display reports `Unknown`,
> and a QEMU VM display reports `Unknown`. The member is retained as the faithful
> mapping of `INDIRECT_VIRTUAL`, but **no measured source produces it** — see
> Decision 4.

### Decision 2 — Periphery does not synthesize an `IsVirtual` verdict on either plane

Whether a given indirect display is synthetic is **deployment knowledge**, not a
display-topology fact: it depends on which machines run a synthetic driver, which
only the operator knows. A consumer needing that bit combines `Indirect` with its
own inventory. Manufacturing it in Periphery would launder a guess into an
authoritative-looking value, on both planes.

### Decision 3 — The planes stay parallel in *shape*, not identical in type

Discovery reports `DisplayConnectionKind.Indirect` / `.Virtual`; control reports
`MonitorOutputTechnology.IndirectWired` / `.IndirectVirtual`. Both keep the
distinction; neither reuses the other's type (ADR-0070 D4, ADR-0068 D4 — core
must not depend on the optional monitor extension). A consumer holding both maps
member-for-member.

### Decision 4 — (added 2026-07-25, post-measurement) Output technology is NOT a virtuality signal, and no consumer may treat it as one

Measurement (issue `#205`) shows a software-presented display can report a
perfectly ordinary physical connector. The mapping from *output technology* to
*"is there a real panel"* is therefore *not a function* — the same value appears
on both sides, in both directions.

**Four virtualization mechanisms measured. None produces an indirect value:**

| Mechanism | Where | `MonitorOutputTechnology` | `DisplayConnectionKind` |
| --- | --- | --- | --- |
| IddCx — ge9 `IddSampleDriver` | sandbox VM, console session | `Hdmi` | `Wired` |
| Emulated GPU — QEMU/virtio | Windows 11, console session | `Other` | `Unknown` |
| **Remote Desktop session display** | Windows 11, RDP session | `Other` | `Unknown` |
| VNC (screen-scrape of console) | sandbox VM | *adds no display* | — |

So `IndirectWired`, `IndirectVirtual`, `Indirect` and `Virtual` are, as of this
writing, **members that nothing observed has ever produced**. They remain the
faithful mapping of the two native values and are kept — but a consumer must not
wait for them to fire. The still-untested candidate is a DisplayLink / USB-C dock
driving real glass, which is the one population plausibly reporting
`INDIRECT_WIRED`.

So the contract is stated negatively and permanently:

- **`MonitorOutputTechnology` and `DisplayConnectionKind` answer "how is this
  attached", never "is this real".** No member of either type may be documented,
  named, or consumed as a virtuality flag.
- **`Indirect` / `IndirectWired` are necessary-but-not-sufficient at best**, and
  as measured, not even necessary: a virtual display can be `Hdmi`.
- A consumer needing `IsVirtual` must derive it from a **different signal** — the
  panel identity (EDID vendor/product; `LNX0000` / `Linux FHD` for
  `IddSampleDriver`), the driver or devnode behind the target, or its own
  deployment inventory.

This closes the inference the whole ADR-0070 → `#200` → ADR-0071 → ADR-0072
sequence was reaching for. The honest finding is that **the question was being
asked of the wrong field**, and no amount of refining the output-technology
mapping would have answered it.

## Consequences

- **`DisplayConnectionKind` gains a public member.** Cheap under the repo's
  pre-1.0 stance, and **measured free rather than assumed** — ADR-0071 asserted a
  four-repo survey; this re-ran it across every sibling repository, all file types:

  ```
  # from the parent directory holding every sibling clone
  $ rg -l --no-ignore \
      -g '!**/bin/**' -g '!**/obj/**' -g '!**/.git/**' -g '!**/*.dll' \
      'DisplayConnectionKind' .
  → 9 hits, ALL under ./periphery/ (5 src, 4 tests). Zero in the other 19 repos.
  ```

  The concrete hazard of adding an enum member is a C# 8+ **switch expression
  with no discard arm**, which throws `SwitchExpressionException` at runtime when
  an unseen member arrives. A second grep for `switch`/`=>` over
  `DisplayConnectionKind` found **no such switch anywhere**, including inside
  periphery — the enum is only ever assigned, compared, and diffed. So the member
  is additive with no reachable break.
- A dock- or DisplayLink-attached monitor is no longer reported as `Virtual`, and
  a synthetic IddCx rig is no longer reported as `Wired`. Neither is reported as
  something the code cannot know.
- ADR-0071 D2's rule is untouched and still governs the default arm: the full
  enumeration and `_ => Unknown` remain, and this ADR adds a faithful member
  rather than widening a neighbour, which is that rule's stated preference.
- Tests pin the pair apart on both planes (`WindowsDisplayConnectionKindTests`,
  `CcdOutputTechnologyTests`), because refolding them reads as a harmless
  simplification.
- **The `IddCx → INDIRECT_WIRED` premise remains unmeasured.** It is
  documentation-sourced across all four changes; a live four-panel read on the
  development box returned only `DISPLAYPORT_EXTERNAL` (10) ×2, `DVI` (4) and
  `HDMI` (5), with no indirect display attached, so the path was never exercised.
  This decision is **conditionally** robust to that gap, and the condition is
  worth stating precisely rather than glossing:

  - *If Windows emits `INDIRECT_WIRED`* (the documented behaviour), `Indirect` is
    correct for an indirect display **whether or not** a panel is attached —
    which is the improvement, since a fold would have had to guess.
  - *If Windows instead emits `INDIRECT_VIRTUAL` for IddCx rigs* — i.e. the
    premise is wrong — this mapping reaches `Virtual`, reintroducing the very
    "no physical panel" assertion this ADR exists to remove. In that scenario the
    change is **no worse than** the code it replaces (both answer `Virtual`), but
    it does not achieve its stated intent either.

  So the decision does not *depend* on the premise to be an improvement, but it
  does depend on it to be a *fix*. Nothing CI can run closes this: the unit tests
  prove switch-arm wiring, not that Windows emits the value. Measuring on an
  `IddSampleDriver` rig — and confirming DisplayLink's reported value on a dock —
  remains open, and is the only thing that can promote this ADR past Proposed.
- **Process note.** Three sessions raced on one question and cross-cited each
  other into an inconsistency that CI could not catch, because each change was
  internally consistent and the disagreement lived across plane boundaries.
  ADR-0071's explicit contingency clause is what made the collision recoverable
  rather than silent; that habit — stating which other decision your ADR's
  reasoning depends on, and what happens if it moves — is worth keeping.
