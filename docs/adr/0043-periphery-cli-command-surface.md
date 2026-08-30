---
title: "ADR-0043: Periphery.Cli command surface roadmap"
status: "Accepted"
status_note: "Partly shipped, partly diverged. Phase 1 (`devices watch` with filters) landed. Phase 3 landed for `hid`, not for camera or serial. Phases 2 (`wait` / `stream` / `on`) and 4 (`diff` / `fleet`) were not built. `battery`, `monitor`, `reset`, and `dashboard` commands shipped without appearing in this roadmap."
date: "2026-05-18"
authors: "@charles8051 (decision)"
tags: ["architecture", "decision", "cli", "tooling", "ux"]
supersedes: ""
superseded_by: ""
depends_on: "ADR-0029 (devicetracker edge events), ADR-0034 (multi-device tracker)"
---

# ADR-0043: Periphery.Cli command surface roadmap

---

## Context

`Periphery.Cli` ships today (`v1.0.0-alpha.13`) with three commands:

- `devices list [--category X] [-v|--verbose] [--json]`
- `devices watch [--category X]`
- `devices dashboard`

These cover snapshot inspection and basic live event streaming. PowerShell's
`Get-PnpDevice` family handles snapshots adequately on Windows, but the
following capabilities are either absent, awkward, or Windows-only in the
broader ecosystem:

- **Cross-platform consistent schema** for device inventory.
- **Real-time event streaming** for connect/disconnect AND property changes
  (battery level, link state, IP address, etc.).
- **Device-aware automation primitives** — "block until a device matching
  this filter appears," "run command X when device Y connects."
- **HID, serial, and camera I/O** — PowerShell has no native HID story;
  serial is bare `System.IO.Ports.SerialPort` with no reconnect; camera
  requires shelling out to ffmpeg.
- **Multi-host device fleet inventory** with normalized output.

`Periphery` already provides the substrate for all of these:
`DeviceWatcher`, `DeviceTracker`, `MultiDeviceTracker` (ADR-0034),
`DeviceProxy`, `DeviceSessionHost`, `DeviceInfoDiff`, and the
`Periphery.Hid` / `Periphery.Camera` extension packages. The question is
which slices of that capability to expose as command-line UX, in what
order, and under what shape.

---

## Decision

A phased roadmap, prioritised by "what's uniquely hard with existing tools"
× "small enough to ship incrementally without restructuring the CLI."

### Phase 1 — Unified `watch` (build now)

Generalise the existing `devices watch` command from a category-only event
logger to a fully-filtered, optionally property-aware event stream.

```
periphery devices watch                                         # all categories, connect/disconnect only (today)
periphery devices watch --category Usb                          # category filter (today)
periphery devices watch --vid 046D --pid C52B                   # USB ID filter (new)
periphery devices watch --name "MX Master"                      # name substring (new)
periphery devices watch --manufacturer Logitech                 # manufacturer substring (new)
periphery devices watch --serial 1234ABCD                       # exact serial (new)
periphery devices watch --bus Bluetooth                         # bus type filter (new)
periphery devices watch --properties BatteryChargePercent       # opt into property events (new)
periphery devices watch --category Battery --properties BatteryChargePercent,BatteryStatus
                                                                # battery telemetry stream (new)
```

Implementation shape:

- New `Settings` flags: `--vid`, `--pid`, `--name`, `--manufacturer`,
  `--serial`, `--bus`, `--properties`.
- Build a `DeviceFilter` from the flags via `f.OfCategory(...)`,
  `f.WithUsbId(vid, pid)`, `f.WithName(...)`, etc.
- If any filter flag is set, register a `DeviceTracker` on the watcher; else
  fan-out events for everything (current behavior).
- If `--properties` is set, subscribe to `watcher.PropertyChanged` and
  filter events to those where
  `e.ChangedProperties.Intersect(settings.Properties).Any()`.
- Print one line per event. Connect/disconnect lines keep the
  `▲`/`▼` arrows; property-change lines use a new glyph (`⚙` or `•`)
  with `prop: old → new` formatting.
- `--pid` without `--vid` is a usage error.
- Unknown property names produce no output (silent — the
  `Intersect` simply never matches). A future flag could enable validation.

**Why same command, not separate `devices battery`**: it's the same mental
model ("log events matching a filter"), the filter syntax composes
(`--category Battery --properties BatteryChargePercent` is one expression),
and adding a separate command per property domain would fragment discovery.
Property events are opt-in via `--properties`, so default `watch` behavior
is unchanged and no extra noise is introduced.

### Phase 2 — automation primitives (next)

Three small commands that genuinely have no clean equivalent elsewhere:

| Command | Description | Built on |
|---|---|---|
| `devices wait <filter> [--timeout 30s]` | Block until a matching device connects. Exit 0 on match, nonzero on timeout. Boot scripts, USB-keyed services, CI fixtures. | `MultiDeviceTracker.DeviceAdded` + `TaskCompletionSource` |
| `devices stream [--filter] [--json]` | Newline-delimited JSON event stream, pipeable to `jq`/log shippers. | Same subscription as `watch`, JSON output via `DeviceInfoJsonContext` |
| `devices on <filter> -- <cmd …>` | Run a shell command each time a matching device connects (also `--on-disconnect`). Cross-platform udev-rules style. | `MultiDeviceTracker` + `Process.Start` |

These share the filter expression with `watch` (Phase 1), so the filter
parsing should be extracted into a reusable `DeviceFilterBinder` helper.

### Phase 3 — Periphery.Hid / Periphery.Camera / serial commands

| Command | Description |
|---|---|
| `hid scan` | List all HID devices with usage page / usage codes. |
| `hid read <vid:pid>` | Dump HID input reports as hex (or `--json`). |
| `serial <port> [--baud N]` | Interactive serial terminal that survives unplug via `DeviceSessionHost`. |
| `camera list` / `camera info <id>` | Camera enumeration with format/resolution detail. |
| `camera snapshot <id> --output frame.png` | Single-frame capture. |
| `camera capture <id> --frames N --output dir/` | Multi-frame capture. |

These pull in `Periphery.Hid` and `Periphery.Camera` as dependencies. They
broaden the CLI's install footprint, so consider whether to ship as
subcommands of the main `Periphery.Cli` package or as separate optional
packages (`Periphery.Cli.Hid`, `Periphery.Cli.Camera`) that register
subcommands via assembly scanning.

### Phase 4 — fleet / change-detection

| Command | Description |
|---|---|
| `devices diff <baseline.json>` | Compare current enumeration against a saved snapshot. Print added/removed/property-changed. | Uses `DeviceInfoDiff`. |
| `devices fleet --hosts h1,h2,h3 [--json]` | SSH/WinRM-fan-out inventory aggregation with normalised schema. | Cross-platform consistency is the unlock. Optional — large blast radius. |

---

## Decision Drivers

- **Build on existing primitives.** Every Phase 1/2/3 command should be a
  thin shell over `DeviceWatcher` / `DeviceTracker` / `MultiDeviceTracker` /
  `DeviceProxy` / `DeviceSessionHost`. The CLI is UX over an existing
  library, not a parallel implementation.
- **Filter expression consistency.** `watch`, `wait`, `stream`, `on`, `diff`
  all benefit from the same filter flag set. Extract once, reuse.
- **Opt-in noise.** Property-change events fire frequently (battery,
  link, IP). Default `watch` stays clean; `--properties` is the opt-in.
- **AOT later, not now.** Spectre.Console.Cli uses reflection; full AOT
  would require migrating to System.CommandLine or hand-rolled parsing.
  Defer until startup latency is a real complaint.
- **No mandatory hard deps in the core CLI.** Camera/HID/serial subcommands
  shouldn't force a 200MB Avalonia + OpenCV install on someone who just
  wants `devices list`. Consider plugin-style packaging in Phase 3.

---

## Blast Radius

| Type | Change | Scope |
|---|---|---|
| `Periphery.Cli/Commands/WatchCommand.cs` | Add 6 new filter flags + 1 properties flag. Subscribe to `watcher.PropertyChanged`. Format property-change lines. | Phase 1 — single file edit, ~60 added lines |
| `Periphery.Cli/Commands/DeviceFilterBinder.cs` (new) | Extract filter-flag → `DeviceFilter` mapping for reuse by `wait`, `stream`, `on`, `diff`. | Phase 2 prerequisite |
| `Periphery.Cli/Commands/WaitCommand.cs` (new) | Block-until-match logic. | Phase 2 |
| `Periphery.Cli/Commands/StreamCommand.cs` (new) | NDJSON event output. | Phase 2 |
| `Periphery.Cli/Commands/OnCommand.cs` (new) | Process invocation on event. | Phase 2 |
| `Periphery.Cli/Periphery.Cli.csproj` | Add `ProjectReference` to `Periphery.Hid`, `Periphery.Camera` (Phase 3 only). | Phase 3 |
| `Periphery.Cli/Program.cs` | Register new commands in the `devices` branch + new `hid`, `serial`, `camera` branches. | Phase 1-3 incremental |
| `MultiDeviceTracker` / `DeviceWatcher` / `DeviceFilter` | **No changes.** All Phase 1-4 commands compose over the existing API. | — |

---

## Open Questions

1. **`--properties` value validation.** Today the design says unknown
   property names silently produce no output. Should we validate against
   `typeof(DeviceInfo).GetProperties()` and warn on first use? Reflection
   cost is one-time at startup. **Recommendation: defer; the silent
   no-match is acceptable for v1.**

2. **Property-change formatting.** Each `PropertyChanged` event may carry
   multiple changed properties. Options:
   - One line per intersected property (verbose but greppable)
   - One line per event with comma-joined props (compact)

   **Recommendation: one line per event, comma-joined, e.g.
   `12:34:56 ⚙ Logitech MX  BatteryChargePercent: 85→84, BatteryStatus: Discharging`**.

3. **Filter expression future syntax.** Phase 2's `wait` and `on` commands
   take a filter expression as a positional arg. Should they accept the
   same `--vid X --name Y` flag style, or a single positional string like
   `"category=Usb,vid=046D,pid=C52B"`? Latter is more shell-friendly for
   long expressions; former is more discoverable. **Recommendation: flags
   for v1 to match `watch`; positional shorthand later if demand emerges.**

4. **JSON output for property events.** Phase 2's `stream` command produces
   NDJSON. Property-change events need a schema — wrap the
   `DevicePropertyChangedEventArgs` in something like:
   ```json
   {"at": "2026-05-18T16:30:00Z", "kind": "property",
    "device": {...DeviceInfo...},
    "changes": {"BatteryChargePercent": {"before": 85, "after": 84}}}
   ```
   **Recommendation: defer concrete schema to Phase 2 implementation.**

5. **AOT for `wait`.** Boot-script use cases benefit from sub-100ms
   startup. `wait` is small enough to maybe ship a separate AOT binary
   later. **Recommendation: skip for now; revisit if startup latency
   becomes a real complaint.**

6. **Multi-instance subcommand plugins.** Phase 3 raises the question of
   whether `hid` / `camera` should be optional packages or in-tree. Avoids
   forcing 200MB+ of camera deps on `devices list` users. **Recommendation:
   in-tree for Phase 3 v1; reconsider if the install footprint becomes a
   complaint.**

---

## Consequences

### Positive

- **Filter consistency** across the CLI — learn the flags once, use them
  with `watch`, `wait`, `stream`, `on`, `diff`.
- **Property-change events surface** a Periphery capability (`PropertyChanged`
  events) that currently has zero CLI presence. Battery monitoring,
  network-link status flips, IP address changes — all become one-liners.
- **No library changes required.** Every command in Phase 1-4 is pure CLI
  surface over the existing API. ADR-0029 / ADR-0034 already laid the
  substrate.
- **Each phase is independently shippable.** No flag day required;
  Phase 1 can land and be useful before Phase 2 starts.

### Negative / Risks

- **Property-change event volume.** Battery levels can update every minute;
  IP address changes can flap during network reconfiguration. The
  `--properties` opt-in mitigates default noise, but consumers using
  `--properties` need to be ready for it.
- **Subcommand dependency creep** in Phase 3. Pulling in
  `Periphery.Camera` (plus its transitive inference and OpenCV deps) onto every
  CLI install is the wrong trade-off if most users never touch camera
  commands. Phase 3 needs a packaging decision — likely separate
  `Periphery.Cli.Hid` / `Periphery.Cli.Camera` packages with
  assembly-scanning subcommand registration.
- **Filter expression flag explosion.** Each new `DeviceFilter.With*`
  method potentially wants a CLI flag. The current count (6 from `Watch`
  Phase 1 + future additions like `--driver`, `--mac`, `--ip`) is
  manageable; if it grows beyond ~12 we should revisit the
  positional-expression alternative from Open Question 3.

---

## References

- `src/Periphery.Cli/Commands/WatchCommand.cs` — Phase 1 target
- `src/Periphery/DeviceFilter.cs` — filter method surface
- `src/Periphery/DeviceWatcher.cs` — event surface (`PropertyChanged` is
  the new subscription)
- `src/Periphery/DevicePropertyChangedEventArgs.cs` — property change
  event payload
- `src/Periphery/MultiDeviceTracker.cs` — Phase 2 `wait`/`on` substrate
- `docs/adr/0029-devicetracker-edge-events.md` — connect/disconnect events
- `docs/adr/0034-device-group-tracker.md` — note: refers to the type by
  its original `DeviceGroupTracker` name; renamed to `MultiDeviceTracker`
  in commit `beab9b7` (post-ADR)
