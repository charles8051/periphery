---
title: "ADR-0070: MonitorLayoutEntry.OutputTechnology is a platform-neutral semantic value, and INDIRECT_WIRED is Virtual"
status: "Accepted"
status_note: "premise measured false; see ADR-0072 Decision 4"
date: "2026-07-25"
authors: "@charles8051"
tags: ["architecture", "decision", "monitor", "windows", "displayconfig", "monitorlayout", "output-technology", "virtual-display", "iddcx"]
supersedes: ""
superseded_by: ""
---

# ADR-0070: `MonitorLayoutEntry.OutputTechnology` is a platform-neutral semantic value, and `INDIRECT_WIRED` is `Virtual`

## Status

> **Promoted 2026-07-25.** The measurement this ADR left open (does an IddCx
> display report `INDIRECT_WIRED`?) has been run and the premise is **false** —
> see the amendment under Decision 2 and ADR-0072 Decision 4. The decision it
> records (keep the two indirect technologies distinct) is unaffected and, if
> anything, better supported: the rejected fold would have been wrong in both
> directions. Accepted rather than left Proposed because the gating condition
> was met — the answer simply came back negative.

Applies the platform-neutral monitor value contract (**ADR-0064**) to a new
axis, exactly as **ADR-0068** did for rotation. Extends the `MonitorLayout`
read model (**ADR-0059**). Consumed by the fleet consumer's *screens-as-first-class-station-state*
epic (Slice 4b) — the re-derived Periphery ask in that ADR's 2026-07-26 amendment.

## Context

`MonitorLayout` / `MonitorLayoutEntry` (ADR-0059) is the **control-plane** read
model: a zero-handle topology snapshot behind the read/apply trust split. It is
the surface the fleet consumer's station agent reads to project screen state, because the fleet consumer's
screens ADR (Decision 5) bars the station agent from Periphery's
`DeviceWatcher` / enriched-`DeviceInfo` / `WM_DISPLAYCHANGE`-sink surface — that
surface is the kiosk consumer's.

The fleet consumer wants one bit per screen: **is this an indirect / virtual display** (a
Windows IddCx display — the fleet runs dual-`IddSampleDriver` rigs — rather than
a physical panel on a real port). Periphery cannot honestly supply that bit
(Decision 2), but it can supply the fact the bit must be derived from, and
nothing the fleet consumer already has supplies even that:

- `ScreenKey` / `DeviceId` carries **port identity**, not output kind — it cannot
  say whether the port is a real connector or a software one.
- An **EDID serial** cannot disambiguate the fleet's rigs: a dual-`IddSample`
  box's two virtual displays share one baked EDID, so the serial is identical.

The value that *does* answer it is already in hand and already discarded. The
CCD reader issues one `DisplayConfigGetDeviceInfo(GET_TARGET_NAME)` per path to
resolve the instance id and friendly name; the `DisplayConfigTargetDeviceName`
it fills also carries `OutputTechnology` (a `DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY`
value), which `CcdLayout` read past and dropped. Surfacing it costs **no new
interop call** — only a read of a field already populated, plus a mapping.

The **discovery plane** already computes this from the same Win32 value:
`DeviceInfo.DisplayPhysicalConnector` (`DisplayConnector`) and
`DisplayConnectionKind` are mapped in `WindowsDisplayConfigEnricher`. That is the
type-level parallel this ADR sits beside — but it is the very surface the fleet consumer is
barred from, and (see Decision 2) its `INDIRECT_WIRED` handling would miss the
fleet's rigs anyway.

## Decision

### Decision 1 — `OutputTechnology` is a platform-neutral semantic value, not a raw Win32 `uint`

`MonitorLayoutEntry` gains `OutputTechnology` (`MonitorOutputTechnology`), a
semantic enum defined by **kind** (`Internal`, `Vga`, `Dvi`, `Hdmi`,
`DisplayPortExternal`, `DisplayPortEmbedded`, `IndirectWired`,
`IndirectVirtual`, and an `Other` fallback) — modeled *exactly* like
`MonitorOrientation` (ADR-0064): its numeric
values are a stable, opaque serialization contract, **never** the platform's
native encoding. No raw `DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY` `uint` reaches
the public surface.

The single Windows translation lives in one place — the new
`Periphery.Monitor.Windows.CcdOutputTechnology.FromCcd(uint)` — mirroring
`CcdOrientation`. It is **total**: any value the contract does not model
(S-Video, composite/component, LVDS, SDI, Miracast, the raw `OTHER` /
`_FORCE_UINT32` sentinels) maps to `Other`. A non-Windows backend writes its own
mapping (a Linux DRM connector type, an X11/RandR output) without touching the
contract enum.

### Decision 2 — `INDIRECT_WIRED` and `INDIRECT_VIRTUAL` stay **distinct** members; Periphery does not synthesize an `IsVirtual` verdict

The consumer ask (the fleet consumer's re-derivation) was to map
`DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INDIRECT_WIRED` (16) to a single `Virtual`
member, on the premise that IddCx rigs report it. **That mapping is rejected**,
and the pair is surfaced as two members — `IndirectWired` and `IndirectVirtual`.

The premise is half right and the conclusion does not follow. `INDIRECT_WIRED`
is the indirect-display path in general: **DisplayLink adapters and USB-C /
Thunderbolt docks drive genuinely physical panels through it**, alongside
synthetic IddCx rigs. Windows does not distinguish the two at this layer.
Collapsing them therefore produces a **false positive on real glass — on exactly
the question the attribute exists to answer.** A contract that already separates
`DisplayPortExternal` from `DisplayPortEmbedded` is operating at finer
granularity than the collapse it was being asked to make.

So Periphery reports the platform fact and leaves the collapse to the consumer,
which is the only layer where the answer is knowable: the fleet consumer knows which of
*its* rigs run a synthetic driver; Periphery cannot know that from the CCD read.
`IsVirtual` is a deployment-informed judgement, not a display-topology fact, and
manufacturing it here would launder a guess into an authoritative-looking value.

**This obligates a correction upstream.** The fleet consumer's screens ADR amendment
specifies the collapse; that instruction should be amended, and its Slice 4b
projector must decide `IsVirtual` from `IndirectWired`/`IndirectVirtual` plus its
own deployment knowledge rather than reading a single Periphery member.

**Empirically unresolved, stated as such.** The claim "IddCx targets report
`INDIRECT_WIRED`" is documentation-sourced on both sides of this exchange and
was **not** measured here: the development box has four real panels
(2×`DISPLAYPORT_EXTERNAL`, `DVI`, `HDMI` — see Consequences) and no IddCx
display attached, so the indirect path could not be exercised. Keeping the
members distinct is also what makes that gap *safe*: if the assumption is wrong,
a distinct member misreports nothing, whereas the collapse would have baked the
unverified claim into a boolean. Measuring it on an `IddSampleDriver` rig, and
confirming DisplayLink's value on a dock, remain open.

> **⚠ MEASURED 2026-07-25 — the assumption was WRONG** (issue `#205`, ADR-0072
> Decision 4). An `IddSampleDriver` rig reports **`Hdmi`**, not `INDIRECT_WIRED`;
> the discovery plane reports `Wired` / `Hdmi` for the same targets. So
> `MonitorOutputTechnology.IndirectWired` does **not** identify the fleet's
> virtual displays, and **no member of this enum is a virtuality signal** — a
> software-presented panel can report an ordinary physical connector.
>
> The paragraph above is left as written because its *reasoning* is what held:
> keeping the members distinct is precisely what made the wrong assumption
> harmless. A distinct member misreported nothing when the premise collapsed;
> the rejected fold would have written the false claim into a boolean **and**
> still missed every rig. Consumers deriving "is this screen virtual" must use
> panel identity (EDID) or deployment knowledge instead — see ADR-0072 D4.

The discovery plane diverges and is left alone:
`WindowsDisplayConfigEnricher.MapConnectionKind` maps only `INDIRECT_VIRTUAL` to
`DisplayConnectionKind.Virtual`. That surface is out of scope here (the kiosk consumer's,
and the fleet consumer is barred from it); tracked separately.

### Decision 3 — It is a read-only, descriptive attribute — not an apply axis, not an actuation predicate

Unlike `Orientation`, `OutputTechnology` has **no apply-side counterpart**:
Windows exposes no way to *set* a monitor's output technology, so
`CcdOutputTechnology` has only `FromCcd`, no `ToCcd`, and `MonitorConfiguration`
gains nothing. It describes; it does not actuate.

It is also **not** an exclusion predicate. Per the fleet consumer's screens ADR
(Decision 10), `IsVirtual` is an *informational* screen attribute; the `Arrange`
exclusion decision stays `ScreenKey`-based and workload-side (the fleet consumer's slice 6).
This work only makes the value **available** — it drives no decision in
Periphery.

### Decision 4 — `MonitorOutputTechnology` and core's `DisplayConnector` / `DisplayConnectionKind` stay separate types

Core `Periphery` already models connector kind for the discovery plane
(`DisplayConnector`, `DisplayConnectionKind`). It is **not** reused here, for the
same reason ADR-0068 D4 kept `MonitorOrientation` and `DeviceInfo.DisplayOrientation`
separate: the dependency runs `Periphery.Monitor` → `Periphery`, and the control
plane must not be forced to speak the discovery plane's types (nor may core take
a dependency on the optional monitor extension). The two planes describe the same
physical fact from independent reads, each with its own backend translator, and
the fleet consumer can only see the control-plane one anyway. `Virtual` as a
first-class member (rather than reusing the discovery plane's four-way
`DisplayConnectionKind`) keeps the control-plane enum focused on the connector
kinds a layout consumer reasons about, with virtuality as one of them.

## Scope (per the fleet consumer re-derivation)

In: the CCD `MonitorLayout` read path only — the enum, the `CcdOutputTechnology`
translator, the additive `MonitorLayoutEntry.OutputTechnology` field, and its
population in `CcdLayout.Read()`.

Out: `ConnectorInstance`, source GDI name, and EDID serial are **not** added.
The re-derivation defers them — `ScreenKey` already carries port identity,
`ConnectorInstance` is `0` unless an adapter has multiple same-type targets, and
the fleet's dual-`IddSample` rigs share one baked EDID so a serial cannot
disambiguate them. The discovery-plane / `DeviceWatcher` surface is untouched.

## Consequences

- `MonitorLayoutEntry` gains one field. The record's positional constructor
  changes — a breaking change, which the repo's pre-1.0 no-consumers stance
  permits; the one production construction site (`CcdLayout.Read`)
  and the test factories are updated in this change. `LayoutDiff` /
  `MonitorLayoutApplier` compare individual fields, never whole-record equality,
  so apply behaviour is byte-for-byte unchanged.
- The mapping is exhaustively unit-tested with no display attached
  (`CcdOutputTechnologyTests`), including an explicit guard that the two indirect
  members do **not** collapse (the regression Decision 2 exists to prevent) and
  the total-fallback behaviour — the same pure-value-transform pattern as
  `CcdOrientationTests`.
- **The constant values were verified against the SDK, then measured**, after a
  reviewer asserted they were "off by 2". They are not: they match
  `Include/10.0.26100.0/um/wingdi.h` lines 2807-2828 verbatim (note the enum
  skips `7`, so it cannot be checked by counting members), they match the values
  core's shipped `DisplayConfigInterop.OUTPUT_TECH_*` already uses, and a live
  four-panel read on the development box returned `DISPLAYPORT_EXTERNAL` (10) ×2,
  `DVI` (4) and `HDMI` (5), which this mapper classified correctly as
  `DisplayPortExternal` / `Dvi` / `Hdmi`. The provenance is now cited in
  `MonitorInterop.cs` so the next reader can check it without guessing.
- **Linux / macOS report nothing until their backends read output technology.**
  There is no `MonitorLayout` backend off Windows yet (`ReadAsync` throws
  `PlatformNotSupportedException`), so this is an unimplemented backend, not a
  Windows-only concept — DRM connector types and RandR outputs both expose the
  equivalent. This matches the incremental-backend posture ADR-0064 set.
- The discovery plane's `INDIRECT_WIRED` → `Wired` gap (Decision 2) is left in
  place as a known, separately-owned divergence; if the kiosk consumer's surface later
  needs the fleet's rigs classified as virtual, that is its own change.
- The fleet consumer's slice 4b consumes this by pinning `Periphery*` to the next release —
  but **not** by reading a single `Virtual` member, which no longer exists. Its
  projector derives `IsVirtual` from `IndirectWired` / `IndirectVirtual` plus its
  own deployment knowledge, and the fleet consumer's screens ADR amendment needs the
  corresponding correction (Decision 2).
