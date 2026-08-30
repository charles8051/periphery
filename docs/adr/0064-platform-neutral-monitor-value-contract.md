---
title: "ADR-0064: Platform-neutral monitor value contract — decoupling the read/apply seam from Windows CCD"
status: "Accepted"
date: "2026-07-11"
authors: "@charles8051"
tags: ["architecture", "decision"]
supersedes: ""
superseded_by: ""
---

# ADR-0064: Platform-neutral monitor value contract — decoupling the read/apply seam from Windows CCD

**Tracks:** `MonitorOrientation`, `MonitorLayoutEntry.IsPrimary`, `DisplayPosition`, `MonitorConfiguration` (Position / IsPrimary axes); new `Periphery.Monitor.Windows.CcdOrientation`
**Extends / partially supersedes:** ADR-0059 (monitor layout read/apply — the value contract it defined) and ADR-0058 D7 (the `MonitorOrientation` DMDO framing)

> **Number provisional.** Assigned at merge per this repo's convention; renumber if `0064` is taken by a parallel branch.

## Context

ADR-0059 gave `Periphery.Monitor` a read model (`MonitorLayout` / `MonitorLayoutEntry`) and a separate apply surface (`MonitorLayoutApplier` / `MonitorConfiguration`), split along the station agent's read/apply trust boundary. It claimed the records are "platform-neutral on their face" and throw `PlatformNotSupportedException` off-Windows "slotting into ADR-0058 D9's session-owner analysis when a Linux consumer pins a session model" (ADR-0059 D5).

The seam *shape* is neutral, but the **values crossing it bake in Windows CCD semantics** that have no X11/Wayland analog. The `PlatformNotSupportedException` guard is honest about the *backend* being Windows-only; it hides that the shared **types** — which the fleet consumer and the kiosk consumer compile against — encode Windows assumptions a Linux backend could not honor without reworking the contract. Per the repo stance (no consumers committed to stability; prefer the right design), the fix is to state the neutral contract now, while the only backend is Windows, so a future backend drops in behind types that already mean the right thing — rather than discovering at that point that the "neutral" records need breaking changes.

This ADR does **not** build a Linux backend (none exists to validate against). It fixes the *contract definition* and makes the low-risk code changes that follow, leaving anything that would collide with in-flight sibling work (`#137` frame normalization, `#138` primary detection) as tracked follow-up.

### The Windows-specific contract points (what actually leaks)

| Contract point | Where | Windows meaning today | X11 (RandR) | Wayland |
| --- | --- | --- | --- | --- |
| `IsPrimary` | `MonitorLayoutEntry.IsPrimary`; populated at `Windows/CcdLayout.cs:77` as `position is { X:0, Y:0 }` | Primary is *defined as* the source at the virtual-desktop origin | An explicit **`primary` output flag** (`XRRSetOutputPrimary`), unrelated to coordinates | Often no primary concept at all; compositor policy |
| `MonitorOrientation` ordinals | `MonitorOrientation.cs`; consumed via casts at `CcdLayout.cs:66`, `GdiDisplayModeBackend.cs`, `CcdLayoutApplier.cs` | Enum value == DEVMODE `DMDO_*` (0–3); CCD rotation is that + 1 | A rotation **bitmask** (`RR_Rotate_0/90/180/270`), combinable with reflection flags — not an ordinal | A `wl_output` **transform** enum folding rotation + flip together |
| `Position` / primary-anchoring | `DisplayPosition`; `LayoutDiff.ResolvePositions` (`MonitorLayoutApplier.cs`) translates the whole desktop so primary lands at (0,0) | One signed global desktop plane; clients set source positions; primary == origin | CRTC coordinates exist, but primary is a *flag*, not (0,0); a client can position CRTCs | **No global desktop origin**; clients **cannot** set output position |

## Decision

### D1. `IsPrimary` is an explicit modeled flag, not the predicate "position == origin"

The contract defines `IsPrimary` as a first-class boolean a backend *asserts*, decoupled from any coordinate. The Windows backend happens to derive it from the CCD origin invariant, but the **type does not bind primary to (0,0)**. A backend maps it from its own signal (X11's RandR primary flag), and a backend with no primary concept may report every entry `false`. This is documented on the field; the detection logic that produces the Windows value is out of scope here (owned by `#138`).

### D2. `MonitorOrientation` is a semantic value, not the Windows ordinal

`MonitorOrientation` is redefined by **semantic rotation** (0° / 90° / 180° / 270°), explicitly *not* as the DEVMODE `DMDO_*` ordinal it coincidentally matched. Its numeric values are a stable, opaque serialization contract; no code is permitted to treat the ordinal as an OS rotation value. Every Windows translation is centralized in one place — `Periphery.Monitor.Windows.CcdOrientation` — which maps to/from both Windows encodings (DEVMODE ordinal, CCD rotation = ordinal + 1). A future backend writes its own mapping from the semantic value to an X11 RandR bitmask or a Wayland transform, touching only its own translator.

**Reflection is a named gap, not a silent reinterpretation.** X11 and Wayland can express reflected-only geometries (`RR_Reflect_*`, the `*_FLIPPED_*` transforms) that the four rotation states cannot. The contract models the four rotations today; surfacing reflection is an explicit contract *extension* (a new member/companion axis), never an overload of the existing values.

### D3. `Position` and primary-anchoring are backend capabilities, not universal facts

`DisplayPosition` and the apply-side `Position` / `IsPrimary` axes model **capabilities a platform may not support**, documented as such rather than presented as always-available. The Windows backend exposes a single global virtual-desktop plane and realizes `IsPrimary = true` by translating every source to the origin (`LayoutDiff.ResolvePositions`). Neither is portable: Wayland clients cannot set output position and have no readable global origin; X11 sets primary via a flag with no translation. The contract's expectation is that a non-Windows applier **rejects or ignores an unsupported axis explicitly** rather than emulating it, and that consumers treat absolute cross-monitor geometry as meaningful only when the active backend documents a global desktop space.

### D4. Scope: contract + translator now; no speculative backend

The concrete changes are confined to the value contract and the Windows translator. The `MonitorLayout` / `MonitorLayoutApplier` seam, the CCD interop, and the `PlatformNotSupportedException` guards are unchanged — this ADR does not add a platform, it makes the existing types honest about which of their guarantees are Windows-CCD-backed capabilities.

## Concrete changes in this change set

- **New `Windows/CcdOrientation.cs`** — the single explicit `MonitorOrientation` ↔ Windows-encoding translator (DEVMODE ordinal and CCD rotation). Unit-tested round-trips + out-of-range fallback.
- **`MonitorOrientation.cs`** — redocumented as a semantic, platform-neutral contract; per-backend mapping and the reflection gap spelled out. Numeric values retained (stable serialization); the doc forbids relying on the DMDO coincidence.
- **`CcdLayoutApplier.cs` (apply, CCD) and `GdiDisplayModeBackend.cs` (read + apply, DEVMODE)** — the direct `(uint)orientation` / `(MonitorOrientation)…` casts now route through `CcdOrientation`, so those sites no longer depend on the enum ordinal.
- **`MonitorLayoutEntry` (`IsPrimary`, `Orientation`, `Position`), `DisplayPosition`, `MonitorConfiguration`** — XML-doc stating each is Windows-CCD-backed today and what a non-Windows backend must provide.
- **`MonitorInterop.cs`** — corrected the stale "CCD rotation = MonitorOrientation + 1" comment (that arithmetic now lives only in `CcdOrientation`).

## Follow-up (deliberately deferred)

- **The read-side CCD orientation cast at `CcdLayout.cs:66`** (`(MonitorOrientation)(rotation - 1)`) is the one remaining direct ordinal cast; it is **not** converted here because those lines share the region `#137` is normalizing (`CurrentMode` frame). Routing it through `CcdOrientation.FromCcdRotation` is a mechanical one-line follow-up once `#137` lands. Until then the enum's numeric values must stay DMDO-aligned; after it, they are free to be renumbered (e.g. to literal degrees) touching only `CcdOrientation`.
- **A reflection member / axis** (D2) waits for a backend that needs it.
- **Backend capability advertisement** — a future non-Windows applier will want to *declare* which axes it supports (rather than throwing per-axis); the shape of that surface is left to the first real second backend, consistent with ADR-0058 D9 deferring the session model.

## Consequences

- The types the fleet consumer / the kiosk consumer compile against now *mean* the neutral thing: primary is a flag, orientation is a rotation value, position/anchoring are optional capabilities. A future X11/Wayland backend slots behind them without a breaking contract rework.
- One translator (`CcdOrientation`) is the sole home of Windows rotation encoding, matching the "one tested home" grain `OrientationMath` already set for the width/height swap.
- The change is contract-and-translator only; the CCD read/apply behavior is byte-for-byte unchanged (the enum values are unaltered and the translator reproduces the former arithmetic), so all existing tests hold and no consumer behavior shifts.
- ADR-0059's D5 "OS-portable shape" bullet is superseded: the records are now neutral *by contract and documentation*, not merely "on their face."
