---
title: "ADR-0068: Display rotation is a first-class DeviceInfo property, and DisplayBounds is the rotated footprint"
status: "Accepted"
status_note: "Shipped - `DeviceInfo.DisplayOrientation` and rotation-applied `DisplayBounds`."
date: "2026-07-24"
authors: "@charles8051"
tags: ["architecture", "decision", "monitor", "windows", "displayconfig", "deviceinfo", "rotation", "orientation"]
supersedes: ""
superseded_by: ""
---

# ADR-0068: Display rotation is a first-class `DeviceInfo` property, and `DisplayBounds` is the rotated footprint

## Status

Extends the TFM-free DisplayConfig enrichment tier (**ADR-0018**, **ADR-0044**),
kept fresh by **ADR-0066**. Relates to **ADR-0064** (the `Periphery.Monitor`
platform-neutral value contract) without superseding it.

## Context

`DeviceInfo.DisplayBounds` was assembled directly from the CCD
`DISPLAYCONFIG_SOURCE_MODE` of the monitor's active path:

```csharp
bounds = new Rectangle(SourcePositionX, SourcePositionY, SourceWidth, SourceHeight);
```

Those four fields are **not in the same frame of reference** (issue `#163`):

- `position` is the origin Windows laid the monitor out at on the virtual
  desktop, computed from the panel's **rotated** footprint.
- `width`/`height` describe the **source surface**, which rotation does not
  transpose.

Combining them verbatim produces a rectangle whose origin and size disagree.
Observed on Windows 10 LTSC, two monitors on an IddSampleDriver virtual adapter,
rotating the non-primary 1920×1080 panel to portrait:

```
Periphery : Bounds = 1920x1080 @ (-1080,0)
Avalonia  :          1080x1920 @ (-1080,0)
```

A 1920-wide rectangle at x=-1080 runs to +840 and overlaps the neighbour at
x=0 — it cannot describe any real desktop layout.

The second, worse consequence is a **missing event**. `DeviceInfoDiff.Compute`
is the whole of the change signal: `DevicePropertyChanged` fires only when some
typed property moves. Rotating the **primary** panel at (0,0) cannot move the
origin, and the pre-fix `DisplayBounds` carried the unrotated size, so the
snapshot was byte-identical before and after (`720x1280 @ (0,0)` → `720x1280 @
(0,0)`). No property changed, so **no event was raised at all** — and because
`DeviceInfo` exposed no rotation, a consumer had no other way to learn the panel
re-oriented. It is not a formatting bug; it is an unobservable state change.

`DISPLAYCONFIG_PATH_TARGET_INFO.rotation` was already being read in the same
loop and discarded, so the information needed to fix both was in hand.

Two consumers depend on this directly: the kiosk window manager
re-places windows from `DisplayBounds` on display changes (the non-primary case
worked only incidentally — its origin moved, and the match ignores size), and
`ScreenRoleProvisioner` uses `DisplayBounds.Size` to tell a portrait panel from a
landscape one when two monitors share an EDID model, where a rotated panel
reported the wrong orientation outright.

## Decision

### Decision 1 — `DisplayBounds` is the rotated on-desktop footprint

`DisplayBounds` reports one frame of reference: the CCD source **position** (which
already arrives rotated) with the source surface size **transposed for a
portrait-class rotation**. A portrait-rotated 1920×1080 panel now reports
`1080x1920`, matching `GetMonitorInfo` and what a windowing toolkit reports for
the same screen.

Only the size crosses frames — the position is never transposed. Consumers that
want the unrotated source surface transpose back using `DisplayOrientation`.

### Decision 2 — Rotation is a first-class property, and it is diffed

`DeviceInfo.DisplayOrientation` (`DisplayOrientation?`) is populated from the
active path's rotation, and `DeviceInfoDiff` checks it. A rotation therefore
always moves a diffed property and always raises `DevicePropertyChanged`, even
where both the origin and the footprint are unchanged (an immovable primary at
(0,0) with a square footprint is the degenerate case the fixed geometry alone
would still miss).

Decision 1 alone would fix the reported rectangle and leave a pure rotation
undetectable in that corner; Decision 2 alone would make rotation observable but
leave `DisplayBounds` geometrically impossible. Both are taken.

`null` means unmeasured (non-Windows, or no DisplayConfig path resolved to the
device) — never "unrotated". `Landscape` is the measured no-rotation value.

### Decision 3 — The reconciliation lives in a pure core

`Periphery.Windows.DisplayGeometry` holds both value transforms —
`FromCcdRotation` (total; anything outside CCD's 1..4 reads as `Landscape`) and
`DesktopBounds` — with no IO, no OS call, and no mutable state. The imperative
shell (`WindowsDisplayConfigEnricher`) owns the `QueryDisplayConfig` batch and
calls in. This is the design preference applied to the smallest possible unit:
the arithmetic that was wrong is now exhaustively unit-testable with no display
attached, including the exact reported repro.

### Decision 4 — `DisplayOrientation` and `MonitorOrientation` stay separate types

`Periphery.Monitor.MonitorOrientation` (ADR-0064) already models the same four
states for the **control** plane. It is not reused here, and the duplication is
deliberate: `DeviceInfo` lives in core `Periphery`, and core must not take a
dependency on the optional monitor-control extension to describe a device it can
already enumerate. The dependency runs `Periphery.Monitor` → `Periphery`, so the
shared type would have to move down into core — a rename across a published
control contract with live downstream consumers, for no gain to either plane.

The two are kept exactly parallel instead — same four members, same ordinals,
same "opaque serialization contract, not a platform ordinal" rule — so a consumer
holding both maps member-for-member, and each plane keeps its own backend
translation (`DisplayGeometry.FromCcdRotation` for discovery,
`CcdOrientation` for control).

## Consequences

- **`DisplayBounds` changes value for rotated panels.** That is the fix, not a
  regression: consumers reading `.Size` to infer orientation (the
  `ScreenRoleProvisioner` case) become correct without changing, and consumers
  matching on the origin are unaffected.
- A rotation now produces a `DevicePropertyChanged` carrying
  `DisplayOrientation` (and `DisplayBounds` whenever the footprint moved), on the
  ADR-0066 `WM_DISPLAYCHANGE` refresh path.
- `WindowsMonitorEnrichment.MergeArrival` carries the new field forward like every
  other monitor-tier field; the reflection drift guard in
  `WindowsMonitorEnrichmentTests` pins that automatically.
- Flip/reflection geometries (X11 `RR_Reflect_*`, the Wayland `*_FLIPPED_*`
  transforms) remain unmodelled, matching ADR-0064. A backend that needs them is
  a contract extension, not a reinterpretation of these four members.
- **Linux and macOS report `null` until their providers read rotation.** That is
  an unimplemented backend, not a Windows-only concept — DRM/KMS or RandR on
  Linux and `CGDisplayRotation` on macOS both expose it, and `DisplayBounds`
  there will need the same reconciliation once they do. This matches the
  incremental-provider posture the whole DisplayConfig enrichment tier already
  has (`DisplayResolution`, `DisplayBounds`, `MonitorName`, connector kind are
  all Windows-only today), and keeps the abstraction honest per the repo's
  cross-platform principle: `null` is "unmeasured", never "unrotated".
- A parity guard (`OrientationContractParityTests`, in `Periphery.Monitor.Tests`
  — the only suite that sees both assemblies) pins Decision 4 at build time:
  member names, ordinal equality across the two enums, and the literal ordinals
  themselves, so neither renumbering one nor renumbering both can pass silently.
