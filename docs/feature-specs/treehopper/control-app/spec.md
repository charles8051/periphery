# Feature Spec: Treehopper control app (pure core + CLI + Avalonia)

<!--
Authoritative, LIVING spec. Read before editing code in scope. The "Affected Layers"
table names the projects to touch; the "how / why" decisions live in the append-only
sibling adr.md.
-->

## Status

**Implemented** — all four phases done: pure core, app service, CLI, and Avalonia GUI. CLI validated on hardware; both CLI and GUI publish as Native AOT (zero ILC warnings).

| Field        | Value                                   |
|--------------|-----------------------------------------|
| Author       | Charles Lee                             |
| Created      | 2026-06-16                              |
| Last Updated | 2026-06-16                              |
| Project      | Periphery.Treehopper.Control (+ .Cli, .Gui) |

---

## Purpose

One Treehopper control application with a **single pure logic core** driving **two
interchangeable front-ends** — a CLI (headless / SSH / fleet) and a slim Avalonia GUI
(desktop). It discovers boards and shows their live state, toggles GPIO, updates
firmware, and scans the I2C bus. The point of the architecture is that every behaviour
is decided once, in a pure core, and merely *rendered* differently by each front-end.

This **subsumes the standalone `treehopper-update` tool**: its fleet firmware logic
moves into this app's CLI, and the standalone project is retired (see ADR Decision 3).

---

## v1 feature set (locked)

- **Discover + live hotplug.** List connected boards; the list self-updates as boards
  appear/disappear (`DeviceWatcher`). Each board shows serial, name, firmware version,
  and connection state (app / bootloader / gone).
- **Live board state.** A live 20-pin view per board, driven by the board's
  `BoardReport` stream (per-pin mode + digital level; the report also carries ADC, so
  analog is available to surface later at no extra cost).
- **GPIO control.** Set a pin's mode (output / digital-input) and drive / toggle output
  pins. Inputs are read live via the state stream.
- **Firmware.** Per-board version + an "update available" indicator (the existing
  version gate), run an update with live progress, **and** the fleet "scan + flash all
  out-of-date boards" mode migrated from `treehopper-update`.
- **I2C bus scan.** A read-only scan: ping every address (`I2cLease.PingAsync`) and list
  responders. No general bus transactions in v1.

### Explicitly deferred (future, behind the same core)

- Analog (ADC) read-outs in the UI — data is already in `BoardReport`; just not surfaced.
- LED toggle; identity (rename / set serial); plain reboot — all cheap to add later.
- Full peripheral-bus transactions (I2C read/write, SPI, UART, 1-Wire); PWM (hard/soft);
  pin presets/profiles; telemetry/logging/export; scripting.

---

## Affected Layers

| Project | Change |
|---------|--------|
| `Periphery.Treehopper.Control` | **New library.** Pure MVU core (`AppState` / `AppEvent` / `AppIntent` / `Reduce`) + the app service (imperative shell that owns hardware). Absorbs the migrated firmware logic. |
| `Periphery.Treehopper.Control.Cli` | **New `dotnet tool`** (`treehopper`). Thin front-end: argv→intents, render `AppState`. Subsumes `treehopper-update`'s verbs under `firmware`. |
| `Periphery.Treehopper.Control.Gui` | **New Avalonia app.** Thin front-end: bind `AppState`→view-models, gestures→intents. CommunityToolkit.Mvvm (ADR Decision 4). |
| `Periphery.Treehopper.Updater` | **Retired.** Pure logic (`FirmwareVersion`, `UpdatePlanner`, `FirmwareSource` embed mechanism, `ExitCodes`) migrates into `Control`; the project + its tests are removed. |
| `Periphery.Treehopper` / `Periphery.Efm8Bootloader` / `Periphery.Usb` / `Periphery` | **None** — consumed as-is. Firmware reuses `TreehopperFirmwareUpdate` + `Efm8Bootloader`. |
| `tests/Periphery.Treehopper.Control.Tests` | **New.** Exhaustive over the pure core (reducers) + the migrated firmware logic. |

---

## Requirements

- [ ] One pure core, no IO/Task/Avalonia/Console, that both front-ends render from.
- [ ] Live board list with hotplug; per-board serial / name / version / connection state.
- [ ] Live 20-pin state per selected board from the `BoardReport` stream.
- [ ] Set pin mode + drive/toggle outputs.
- [ ] Firmware: per-board status + update-with-progress + migrated fleet scan-and-flash.
- [ ] I2C bus scan (read-only).
- [ ] CLI and Avalonia front-ends, both thin, both on the shared core/service.
- [ ] `treehopper-update` retired; its behaviour reachable via `treehopper firmware …`.
- [ ] `dotnet build Periphery.slnx -c Release` green; `dotnet test` green.

---

## Architecture — MVU core, shared service, two thin shells (ADR-0052)

```
            ┌───────────────────────────── Periphery.Treehopper.Control ──────────────────────────────┐
 CLI  ─┐    │  PURE CORE (no IO)                         IMPERATIVE SHELL (owns hardware)              │
       ├──▶ │  AppState  ◀── Reduce(state, AppEvent) ◀──  TreehopperControlService                     │
 GUI  ─┘    │  AppIntent ──────────────────────────────▶  • DeviceWatcher (hotplug → events)           │
       ◀────┤  (render AppState)                          • open TreehopperBoard sessions + Reports    │
            │                                             • TreehopperFirmwareUpdate (reflash+progress) │
            │                                             • I2cLease.PingAsync (scan)                   │
            └─────────────────────────────────────────────────────────────────────────────────────────┘
```

- **Pure core.** Immutable `AppState`; an `AppEvent` union (things that happened); a
  pure total `Reduce(AppState, AppEvent) → AppState`. Intents (`AppIntent`) are values
  describing what the user wants. Exhaustively unit-testable — same grain as the EFM8
  protocol core and the kiosk consumer's LED engine.
- **App service** (shell, same library). Owns the `DeviceWatcher`, the dictionary of
  open `TreehopperBoard` sessions and their `Reports` subscriptions, the reflash flow,
  and I2C scans. Converts hardware callbacks → `AppEvent`s (folded into state) and
  executes `AppIntent`s. Exposes the current `AppState` + a change signal
  (`IObservable<AppState>` / event).
- **Front-ends.** CLI maps argv→intents and renders `AppState` (one-shot or a live view);
  Avalonia projects `AppState`→bindable view-models and turns gestures→intents.

### Core model sketch (refined at implementation)

```
AppState   { ImmutableArray<BoardView> Boards; string? SelectedBoardId }
BoardView  { string Id; string? Serial; string? Name; int? Version;
             BoardConnection Connection;            // App | Bootloader | Gone
             ImmutableArray<PinView> Pins;           // 20
             FirmwareView Firmware;                  // status + optional progress
             ImmutableArray<byte>? I2cResponders }
PinView    { int Number; PinMode Mode; bool High; int Adc }
AppEvent   = BoardAppeared | BoardActivated | BoardRemoved | BoardOpened(identity)
           | ReportReceived(id, BoardReport) | PinModeChanged | OutputDriven
           | FirmwareStatusChanged | FirmwareProgress(id, pct) | FirmwareFinished(id, result)
           | I2cScanFinished(id, addresses) | OperationFailed(id, message)
AppIntent  = SelectBoard | SetPinMode(id,pin,mode) | DriveOutput(id,pin,high)
           | ToggleOutput(id,pin) | ScanI2c(id) | UpdateFirmware(id, source) | RefreshAll
```

---

## CLI surface (`treehopper`) — unifies treehopper-update

| Command | Behaviour |
|---------|-----------|
| `treehopper list` / `watch` | Snapshot / live board list + state (read-only). |
| `treehopper pin <board> <n> <high\|low\|input>` | Set mode / drive an output. |
| `treehopper i2c scan <board>` | Read-only I2C bus scan. |
| `treehopper firmware list` | Per-board version + update-available (was `treehopper-update list`). |
| `treehopper firmware all [--yes]` | Fleet scan-and-flash (was `treehopper-update all`). |
| `treehopper firmware board <serial> [--yes]` | Single-board flash (was `treehopper-update board`). |

Carries over the safe-by-default contract: no `--yes` = dry run; `--force`,
`--target-version`, `--file`, `--json`, the embedded-image build mechanism, and the exit
codes all migrate verbatim. The sequential-flash hazard and version gating are unchanged.

---

## GUI sketch (Avalonia, slim)

Master/detail: a left board list (live, hotplug) → a detail pane with the 20-pin grid
(click to toggle outputs, dropdown for mode), a firmware card (version, update button +
progress), and an I2C-scan button showing responders. Binds a `MainViewModel` projected
from `AppState`; every gesture dispatches an `AppIntent`. No hardware logic in the view.

---

## Testing

- **Pure core (exhaustive, hardware-free):** `Reduce` for every `AppEvent` — board
  appear/activate/remove ordering, report folding into pin views, firmware
  status/progress transitions, I2C-scan results, selection. Plus the migrated firmware
  logic (version parse, planner gating).
- **Service / front-ends:** integration against real hardware by an operator (hotplug,
  flashing, I2C scan), not in CI — same posture as the firmware-reflash feature.

---

## Implementation phases

1. **Core + tests** — ✅ done. `AppState` / `AppEvent` / `AppIntent` / `Reduce` + firmware
   helpers; 36 reducer tests.
2. **App service** — ✅ done. `TreehopperControlService`: `DeviceWatcher` hotplug, single
   on-demand board session + report folding, reflash, I2C scan; live-streaming decoupled
   from selection so `list` stays read-only.
3. **CLI** — ✅ done. `treehopper` (`list` / `watch` / `pin` / `i2c` / `firmware all|board`);
   `treehopper-update` retired, its logic folded in; validated on hardware (list, gating,
   I2C scan, GPIO drive, single-board flash). 29 parser tests.
4. **Avalonia GUI** — ✅ done. `Periphery.Treehopper.Control.Gui` (Avalonia 11.2 +
   CommunityToolkit.Mvvm + compiled bindings): master/detail, live board list, per-board
   pin grid (toggle / input), I2C scan, firmware flash via file picker. An `AppState`→VM
   reconciler updates in place (pins never rebuilt). Native AOT publish verified (17 MB
   native exe, zero ILC warnings); launches and runs.

> **Output-level authority (learned on hardware, phase 3).** The firmware emits reports on
> *input* changes, not host-driven *output* changes, so a driven output's level is
> host-authoritative: the `OutputDriven` event records it immediately and `ReportReceived`
> does not clobber output-mode pins.

---

## Open questions / risks

- [ ] **Board-open contention.** Opening a `TreehopperBoard` sends `ConfigureDevice`
      (resets transient config) and holds the handle. The service must own a single open
      session per board and share it across operations, and avoid opening a board the
      operator's other software is using. (List/version reads stay read-only via `UsbDevice`.)
- [ ] **Sequential firmware flashing** remains mandatory (all bootloaders share id
      `0x10C4:0xEAC9`) — carried over from the reflash feature.
- [ ] Avalonia stack is CommunityToolkit.Mvvm (Decision 4); revisit only if the streaming
      state proves awkward to bind.

---

## Related

| Type | Link |
|------|------|
| Decisions (how / why) | [`adr.md`](adr.md) |
| Firmware-reflash feature (substrate) | [`../firmware-reflash/spec.md`](../firmware-reflash/spec.md) |
| Functional-core / shell ADR | [`../../../adr/0052-periphery-treehopper-pure-core.md`](../../../adr/0052-periphery-treehopper-pure-core.md) |
| Avalonia precedent | `src/Periphery.Camera.Avalonia` |
