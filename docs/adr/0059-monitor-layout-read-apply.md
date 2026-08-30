---
title: "ADR-0059: Monitor layout read/apply — CCD topology surfaces for Periphery.Monitor"
status: "Accepted"
date: "2026-06-12"
authors: "@charles8051; requirements from the fleet station agent (the anchor consumer)"
tags: ["architecture", "decision"]
supersedes: ""
superseded_by: ""
---

# ADR-0059: Monitor layout read/apply — CCD topology surfaces for Periphery.Monitor

**Tracks:** `MonitorLayout`, `MonitorLayoutEntry`, `MonitorConfiguration`, `MonitorLayoutApplier`, `LayoutDiff`, `DisplayPosition`; CLI `monitor layout` / `set-primary`
**Supersedes (in part):** ADR-0058 D8 (GDI as the only apply mechanism) and NEG-005 (topology out of scope)

> **Extended by ADR-0064** (2026-07-11): the value contract below is made platform-neutral by definition — `IsPrimary` is an explicit modeled flag (not "== origin"), `MonitorOrientation` is a semantic value (not the Windows DMDO ordinal), and `Position` / primary-anchoring are backend capabilities a platform may not support. D5's "OS-portable shape" bullet is superseded there.

> **Number provisional.** Assigned at merge per this repo's convention.

## Context

The fleet station agent — a privileged convergence agent keeping kiosks in a desired OS posture — asked for Periphery.Monitor as its display-configuration mechanism layer, with requirements ADR-0058's v1 deliberately excluded: per-monitor **position** and **IsPrimary** (read *and* apply), **current-vs-preferred mode distinctly**, a transactional **validate → apply → persist** path, **idempotent convergence** ("already satisfied" as the common case), and — as a hard constraint — **read and apply as separate surfaces** so a sandboxed process can link/call read while only the privileged agent reaches apply.

These wants are exactly the "revisit when limits bite" triggers D8 and NEG-005 named. Position and primary are *inherently cross-monitor* (CCD defines primary as the monitor at virtual-desktop origin; setting it means translating every monitor), so the per-monitor GDI dance (CDS_NORESET batching) is the clumsy shape and Windows CCD (`QueryDisplayConfig`/`SetDisplayConfig`) is the native one. Per the repo stance, a better answer supersedes the old decision rather than bending to it.

## Decision

### D1. Two new top-level surfaces, split along the consumer's trust boundary

- **Read:** `MonitorLayout.ReadAsync(ct)` — a zero-handle, whole-topology snapshot. Pure data records; no mutators anywhere on the read model.
- **Apply:** `MonitorLayoutApplier.ApplyAsync(desired, options, ct)` — a separate static entry type, so "who may call apply" is visible at the call site and the two surfaces are independently referenceable.

`MonitorDevice` (ADR-0058) keeps its per-monitor handle surface — the VCP plane is untouched, and the handle's CDS-based mode/orientation setters remain as the validated single-monitor convenience (CLI, scripts). The layout surfaces are the posture/topology mechanism. Two apply mechanisms coexisting is an accepted drift risk, traded for not destabilizing the just-validated v1; consolidation onto CCD is future work if the seam ever leaks.

### D2. Read model

```csharp
sealed record MonitorLayout(ImmutableArray<MonitorLayoutEntry> Monitors)   // empty when no active paths
sealed record MonitorLayoutEntry(
    string DeviceId,                 // PnP instance id — joins DeviceInfo.Id and the v1 resolver
    string? FriendlyName,
    bool IsPrimary,                  // explicit field (CCD: source position == origin)
    DisplayMode CurrentMode,         // the LIVE mode (source size + target refresh)
    DisplayMode? PreferredMode,      // DISPLAYCONFIG_TARGET_PREFERRED_MODE — distinct from current
    MonitorOrientation Orientation,  // CCD target rotation
    DisplayPosition Position,        // virtual-desktop origin of this monitor
    ImmutableArray<DisplayMode> SupportedModes) // for pre-apply validation
```

Current and preferred are separate fields by design — core's `DeviceInfo.DisplayResolution` carries the preferred/native mode and `DisplayBounds` the current one, an overlap that has already confused consumers; the layout model names both honestly. Color depth is not modeled (modern Windows is uniformly 32bpp).

### D3. Apply model: desired state, validate-first, persist, idempotent

```csharp
sealed record MonitorConfiguration(      // desired state for one monitor; null = leave that axis alone
    string DeviceId,
    DisplayMode? Mode = null,
    MonitorOrientation? Orientation = null,
    DisplayPosition? Position = null,
    bool? IsPrimary = null)

MonitorLayoutApplyResult ApplyAsync(IReadOnlyList<MonitorConfiguration> desired, …)
    // result: Outcome ∈ { AlreadySatisfied, Applied } + the post-apply MonitorLayout
```

- **Idempotence is a pure function:** `LayoutDiff.IsSatisfiedBy(current, desired)` compares before any OS call; the convergence-loop common case never touches CCD. Unit-tested exhaustively.
- **Validate → apply → persist:** the mutated path/mode arrays go through `SetDisplayConfig(SDC_VALIDATE)` first — an unsupported request fails loudly with the CCD return code instead of blanking the panel — then `SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_SAVE_TO_DATABASE` so the result survives logon cycles.
- **Primary is a translation:** CCD defines primary as origin, so `IsPrimary = true` translates every source position such that the chosen monitor lands at (0,0). Setting primary and explicit positions for the same transaction are reconciled in the diff layer (explicit positions win; primary translation applies to unpinned monitors).
- **Rotation is a separate axis; the source frame is native (corrected — `#137` / ADR-0064):** the CCD source mode is the panel's *native, unrotated* frame and stays native across a rotation — a native 1920x1080 surface rotated 90° displays as a 1080x1920 portrait desktop, so the OS derives the on-desktop footprint from the source mode plus the rotation. The apply path therefore sets only `TargetInfo.Rotation` and never transposes the source dimensions. The original decision here swapped the source width/height on a landscape/portrait crossing, believing the source was desktop-space; `#137` verified (on real portrait hardware) that both the read and the store keep the source native, so that swap was removed on both the read (`CurrentMode`) and apply (`CcdLayoutApplier`) sides. The desktop footprint a consumer wants is the derived `MonitorLayoutEntry.DesktopSize`.
- **Failures throw typed exceptions** (family norm): unknown `DeviceId` → `MonitorDeviceNotFoundException`; validation rejection → `MonitorLayoutRejectedException` carrying the `SetDisplayConfig` return code; the success/no-op distinction lives in the result. Nothing is silent.

### D4. No-display and LTSC zero-paths degrade explicitly, not by crashing

`ReadAsync` with zero active paths returns an **empty layout** (the "no display attached / non-interactive session" answer the station agent treats as a safe no-op). `ApplyAsync` in the same state throws `MonitorDeviceNotFoundException` with a message naming the two causes: genuinely headless, or a **session without display paths** — the Win10 IoT/LTSC zero-paths behaviour (ADR-0044) and the session-locality constraint (ADR-0058 OQ-004) are the same failure shape on the apply side, and there is no apply-side EDID fallback possible (CCD cannot set what it cannot see). Integration note for the station agent, validated on the dual-display bench VM: **apply must run in the interactive console session** (the fleet's Interactive-task pattern); a service-session station agent can read enumeration facts but will see an empty layout.

### D5. Pushbacks / reconciliations to the consumer note

- **"Don't hang mutators off the read model"** — agreed and done (D1). Note the *handle* model (`MonitorDevice`) retains its setters: it is an open-device I/O surface (its VCP plane is inherently mutating), not the posture read model. The station agent's sandboxed reader should consume `MonitorLayout`/enumeration only and simply not reference the applier.
- **Separate linkability** — both surfaces ship in `Periphery.Monitor` (one package; splitting assemblies for a two-type boundary is ceremony the trust model doesn't need — the boundary is *which entry point a process calls*, enforceable by review/DI). If a consumer later needs a load-time guarantee, a `Periphery.Monitor.Apply` split is mechanical.
- **Identity across hotplug** — v1 identity is the PnP instance id (stable across reboots on the same port, already the join key to `DeviceInfo`). EDID-serial-based matching (stable across ports, but real fleets ship all-zero serials — kiosk experience) is deferred until drift is observed; the station agent's per-station config can pin instance ids today.
- **OS-portable shape** — the records are platform-neutral on their face (`DisplayMode`, `MonitorOrientation`, `DisplayPosition`); `ReadAsync`/`ApplyAsync` throw `PlatformNotSupportedException` off-Windows today, slotting into ADR-0058 D9's session-owner analysis when a Linux consumer pins a session model. Not contorted for it.
- **Win10 LTSC** — read-side zero-paths keeps the core EDID-fallback enrichment (names still resolve); layout read returns empty rather than wrong; apply fails typed (D4). That is the honest ceiling: no display-config API exists to apply through when the OS reports no paths.

## Consequences

- The station agent gets drift-detection facts (current vs preferred, rotation, position, primary, available modes) and a transactional, idempotent, persisted apply with explicit outcomes — on the CCD surfaces it asked for.
- ADR-0058's D8/NEG-005 are narrowed: GDI remains the per-monitor handle mechanism; topology belongs to the layout surfaces.
- New interop is self-contained in `Periphery.Monitor.Windows` (full CCD mode structs + `SetDisplayConfig`); core's internal read-only `DisplayConfigInterop` stays untouched (no new core coupling beyond the existing `InternalsVisibleTo`).
- Validation: layout read + primary-swap + position-move + mode-change round-trips on the bench VM's dual IddSample displays, via the established Interactive-task bench.
