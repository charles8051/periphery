---
title: "ADR-0066: Monitor DisplayConfig freshness via a WM_DISPLAYCHANGE refresh hook"
status: "Accepted"
status_note: "Shipped - `WindowsDisplayChangeSink`, `WindowMessageInterop`, `MonitorAnnouncementLedger`."
date: "2026-07-23"
authors: "@charles8051 (analysis + adversarial review)"
tags: ["architecture", "decision", "monitor", "windows", "displayconfig", "device-monitor-provider", "enrichment", "wm-displaychange"]
supersedes: ""
superseded_by: ""
---

# ADR-0066: Monitor DisplayConfig freshness via a WM_DISPLAYCHANGE refresh hook

## Status

Amends **ADR-0054 Decision 2** for the monitor DisplayConfig case (resolves its
explicitly-deferred "wire display notifications inside Periphery" alternative);
stays within **ADR-0054 Decision 3**.

## Context

`DeviceInfo`'s monitor identity fields — `MonitorName`, `DisplayResolution`,
`DisplayBounds`, and the connector kind — are not device-node properties. They
come from a whole-topology `QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS)` batch
(`WindowsDisplayConfigEnricher`), keyed by PnP instance id, that the Windows
provider runs **only during full enumeration** (`WindowsDeviceProvider.EnumerateAsync`).

The **hotplug arrival** path (`WindowsDeviceMonitorProvider` → `TryBuildDeviceInfo`)
never runs that tier. So (issue `#149`):

- A **freshly hotplugged** monitor surfaces with `MonitorName`/`DisplayResolution`/
  `DisplayBounds` all `null` and never recovers — there was no Windows refresh path
  that re-stamps DisplayConfig fields after enumeration.
- A **re-appearing** monitor's bare arrival payload *overwrote* a good
  enumeration-time snapshot in the tracker (`ApplyAppeared` → `SetItem`).
- **Mode / rotation / resolution changes** on an already-attached panel produced
  **no event at all**.

ADR-0054 removed the whole-tree property scan and (Decision 2) placed display
freshness on the consumer — *"Display resolution/config → `WM_DISPLAYCHANGE` (a
windowed/UI consumer already receives this; e.g. Avalonia)."* That assumption
does not hold for the consumers that actually join on these fields: the
The fleet consumer / the kiosk consumer window manager resolves a role→`Screen` binding by
`MonitorName`, and it consumes Periphery's enriched `DeviceInfo`, not a raw
Win32 message it pumps itself. The enrichment has to be fresh **at Periphery's
layer**. ADR-0054 Decision 3 anticipated exactly this and pre-authorised it:
`DevicePropertyChanged` stays dormant on Windows *"until/unless a specific OS
notification is wired for a specific property (a targeted, event-driven add,
never a return to tree polling)."*

## Decision

Wire that one targeted signal, in the Windows monitor provider, for the monitor
DisplayConfig tier only.

### Decision 1 — A `WM_DISPLAYCHANGE` sink drives a monitor DisplayConfig refresh

`WindowsDeviceMonitorProvider` owns a `WindowsDisplayChangeSink`: a hidden
**top-level** window (message-only windows are excluded from the
`WM_DISPLAYCHANGE` broadcast) pumped on a dedicated background thread. On a
display change, the provider re-runs `WindowsDisplayConfigEnricher` over its
cached `Monitor`-category snapshots and raises `DevicePropertyChanged` with the
`(previous → enriched)` delta. `DeviceTracker.OnDevicePropertyChanged` already
re-stamps via `ApplyPropertyChanged`. This is not polling and not a tree scan;
it fires only on the OS's own topology-settled edge, so
`QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS)` reliably reflects the change. It also
closes the previously-eventless mode/rotation/resolution-change case.

### Decision 2 — Arrival also triggers the refresh; enrichment merges forward on re-appearance

A genuinely-new panel has no cached snapshot to enrich, and the arrival/topology
ordering is not guaranteed, so a monitor devnode arrival **also** requests a
refresh (coalesced onto the same sink queue) — covering both orderings without a
retry heuristic. On arrival/re-appearance the provider **merges the monitor tier
forward** from its cache onto the (never-DisplayConfig-enriched) arrival payload
before raising `DeviceAppeared`/`DeviceActivated`, so a re-appearance is never a
bare clobber. The merge covers *every* monitor-tier field on `DeviceInfo`, not
only those the DisplayConfig enricher populates today, so a future HDR/DPI
enricher cannot silently reopen this bug for its field. This merge lives in the
**Windows provider shell** (`WindowsMonitorEnrichment.MergeArrival`), not the
platform-neutral tracker core — the core stays category-blind; "which fields the
Windows arrival path fails to enrich" is shell knowledge.

### Decision 2a — Monitor appearance and refresh events are ordered by a ledger, not a lock

The two signals run on two threads (cfgmgr32 callback vs. sink pump), so they
must be ordered. Without it there is a real hole: an arrival publishes a bare
monitor to the cache, and before it raises `DeviceAppeared` a concurrent refresh
can enrich that entry, write it back, and raise `DevicePropertyChanged` — which
the tracker **drops**, because the device is not resolved yet. The arrival's
follow-up refresh then diffs the already-enriched cache to nothing and never
re-emits, leaving the monitor bare indefinitely.

The precondition to enforce is therefore *"a refresh delta is only written back
and raised for a monitor whose appearance has already been raised"*. That is
recorded as **data** — `MonitorAnnouncementLedger`, guarded by the provider's
existing cache lock — rather than enforced by holding a lock across the raising:

- A publish (cache write + `DeviceAppeared`/`DeviceActivated`) registers itself
  with the ledger for its whole duration, and every publish requests a refresh
  once its events are out.
- The refresh **skips** a monitor that is unannounced or mid-publish entirely —
  neither raising *nor writing back*. Skipping the write-back is the load-bearing
  half: enriching the cache without emitting is exactly how the enrichment used
  to get lost. A skipped monitor is re-driven by the in-flight publish's own
  trailing refresh request, so nothing is dropped and nothing spins.
- The in-flight count is a depth, not a flag: one plug can publish the same
  monitor twice concurrently (the interface-arrival and instance-started
  notifications both fire, on different callback threads).
- Cache-seeded monitors (`StartAsync`) are marked announced up front: consumers
  learn about those from the watcher's startup snapshot, which runs the
  enrichment pipeline, not from a provider event.

**Every event is raised with no provider lock held.** The first implementation
used a `_monitorEventGate` held across the raising on both sides, which put an
internal lock around synchronous consumer callbacks (`DeviceWatcher` →
`DeviceTracker` → `StateChanged`/observers all run on the raising thread). A
consumer that applies a display layout from a monitor handler — the station agent's
actual flow — makes Windows broadcast `WM_DISPLAYCHANGE` by `SendMessage` to
this sink's own window, which only the pump thread services; with the pump thread
blocked on that gate the broadcast stalled until Windows declared the window hung
(issue `#153`). Mutual exclusion was standing in for an ordering constraint, and it
was the wrong instrument.

Dropping the gate also stops serializing monitor arrivals against each other —
a side effect of the first implementation, never a goal. That restores the
behaviour of the non-monitor arrival path in the same provider, and of the Linux
and macOS providers, all of which raise straight from the notification thread.

The sink is also started *before* the cfgmgr32 registrations, so an arrival can
never find the sink absent and drop its refresh request.

### Decision 3 — The heavy work runs off the broadcast and callback threads

`WM_DISPLAYCHANGE` is delivered by `SendMessage`; doing a `QueryDisplayConfig`
plus synchronous consumer fan-out inline in the `WndProc` would block the OS
broadcast to every window. The `WndProc` therefore only posts a private
`WM_APP_REFRESH` and returns; the pump loop coalesces bursts and runs the
enrich + raise. The DisplayConfig enricher build/enrich (which reads the EDID
registry) runs **outside** the provider cache lock, so cfgmgr32 notification
callbacks are never stalled by it. Disposal joins the pump deterministically and
never disposes the readiness primitive under a live thread (a background-thread
`ObjectDisposedException` would be process-fatal).

## Consequences

### Positive

- Hotplugged and re-plugged monitors get their DisplayConfig fields, and keep
  them across mode/rotation/resolution changes — the eventless case ADR-0054
  named as a known Windows gap, closed for this tier.
- **No contract change.** `DevicePropertyChanged` is already declared on
  `IDeviceMonitorProvider` and raised by the Linux/macOS providers; Windows now
  honours it for the monitor tier. No consumer, fake, or non-Windows provider
  changes. The kiosk consumer's battery tracker cannot receive monitor deltas (the
  tracker self-gates on the resolved id), so ADR-0054's null-clobber hazard does
  not reopen.
- The novel logic (`MergeArrival`, `ComputeDeltas`) is pure and unit-tested with
  no display hardware; the ordering precondition (`MonitorAnnouncementLedger`) is
  plain state with no OS calls and is unit-tested too. Only the window/pump shell
  is untestable.
- No provider lock is held while a consumer's event handler runs, so a handler
  cannot stall the sink's message pump or the cfgmgr32 callback thread beyond its
  own duration.

### Negative / trade-offs

- The provider now owns a background message-pump thread (not polling, but not
  "nothing running"). Documented in the class summary.
- A second DisplayConfig `QueryDisplayConfig` surface now exists in core
  `Periphery` (the enricher) alongside `Periphery.Monitor`'s CCD reader
  (ADR-0059). Accepted: the enricher is part of the enumeration pipeline and
  `Periphery.Monitor` is an optional extension; centralising is a separate
  question.
- In session 0 (a service with no interactive desktop) the hidden window does not
  receive the console session's broadcast, so the refresh is inert there; the
  provider degrades to its pre-`#149` behaviour rather than failing.

## Alternatives considered

- **Enrich synchronously on arrival (in `TryBuildDeviceInfo`).** Rejected as the
  primary fix: at `DEVICEINTERFACEARRIVAL` the panel may not yet be an active CCD
  path, so `QDC_ONLY_ACTIVE_PATHS` can return null (timing-fragile), and a full
  `QueryDisplayConfig` on the `[UnmanagedCallersOnly]` cfgmgr32 callback thread is
  the wrong place for heavy work. The arrival-triggered *coalesced* refresh
  (Decision 2) keeps the benefit without either hazard.
- **Merge-don't-clobber in the tracker core only.** Rejected as a standalone
  fix: it cannot enrich a genuinely-new panel (no prior snapshot), is undone by
  the `Activated` full-replace, and puts monitor/DisplayConfig field knowledge in
  the platform-neutral core. Kept only as the shell-side `MergeArrival` guardrail.
- **Leave it to the consumer (status quo, ADR-0054 D2).** Rejected: the consumers
  that need these fields join on Periphery's enriched `DeviceInfo`, not on a Win32
  message they pump; the freshness must live at Periphery's layer.
