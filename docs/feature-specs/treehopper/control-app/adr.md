# ADR: Treehopper control app (pure core + CLI + Avalonia)

<!--
Append-only / superseded, never rewritten. Decisions numbered Decision 1..N.
The "what" (living requirements, current shape) is in spec.md.
-->

Context: one Treehopper control app whose logic is shared verbatim by a CLI and a slim
Avalonia GUI, covering discovery + live state, GPIO, firmware, and an I2C scan. Scope
was locked with the operator (live pin-state stream; I2C scan only; unify the existing
`treehopper-update` tool).

---

## Decision 1 — MVU / reducer pure core (`AppState` + `AppEvent` + pure `Reduce`)

**Decision.** Model the whole app as an immutable `AppState`, an `AppEvent` union of
things that happened, and a pure total `Reduce(AppState, AppEvent) → AppState`. User
actions are `AppIntent` values. Both front-ends render `AppState` and emit intents.

**Why.** "One pure logic core, two front-ends" *is* MVU. A reducer makes the entire
behaviour exhaustively unit-testable with no hardware and no UI, makes the two front-ends
provably consistent (they cannot diverge — they render the same state), and matches the
repo's functional-core / imperative-shell grain (ADR-0052), already proven by the EFM8
protocol core and the kiosk consumer's LED engine. The alternative — shared service methods that
each UI calls and then locally tracks state — re-introduces exactly the drift this
project exists to avoid.

---

## Decision 2 — A single app service owns all hardware; front-ends are thin

**Decision.** One `TreehopperControlService` (the imperative shell, in the core library)
owns the `DeviceWatcher`, the open `TreehopperBoard` sessions and their report
subscriptions, the reflash flow, and I2C scans. It turns hardware callbacks into
`AppEvent`s and executes `AppIntent`s. The CLI and GUI contain no hardware logic.

**Why.** Hardware ownership is the messy, stateful, racy part; concentrating it in one
shell keeps the two front-ends trivial and keeps board handles single-owned (a board is
opened once and shared across operations, not re-opened per command — important because
opening sends `ConfigureDevice`). Front-ends that each opened boards would fight over
handles and duplicate lifecycle logic.

---

## Decision 3 — Unify `treehopper-update` into this app; retire the standalone tool

**Decision.** The fleet firmware logic from `Periphery.Treehopper.Updater` moves into
this app: the pure pieces (`FirmwareVersion`, `UpdatePlanner`, the `FirmwareSource`
embed mechanism, `ExitCodes`) migrate into `Control`, and the verbs reappear under
`treehopper firmware …`. The standalone project and its tests are removed.

**Why.** The operator chose one app over two overlapping CLIs. Firmware update is one of
this app's four core features, so it belongs in the same core/service rather than a
parallel tool with its own discovery and version-gating. The safe-by-default contract,
version gating, embedded-image build mechanism, and exit codes are preserved verbatim —
this is a re-home, not a behaviour change. `treehopper-update` was committed but has no
external consumers (per the repo's no-consumers stance), so retiring it is free.

---

## Decision 4 — Avalonia GUI uses CommunityToolkit.Mvvm, not ReactiveUI

**Decision.** The GUI binds a `MainViewModel` projected from `AppState` using
CommunityToolkit.Mvvm (`[ObservableProperty]` / `[RelayCommand]`).

**Why.** "Slim" was an explicit goal. CommunityToolkit.Mvvm is a light source-generator
MVVM layer with no large reactive runtime; the view-model subscribes to the service's
state-change signal and re-projects. ReactiveUI would suit the streaming state but adds
weight and a second paradigm. The view-model is a thin adapter over the pure core, so the
MVVM flavour is a local, reversible choice, not load-bearing.

---

## Decision 5 — Lean v1: defer analog/LED/identity and full buses (I2C scan excepted)

**Decision.** v1 ships discovery + live pin-state + GPIO + firmware + a read-only I2C
scan. Analog read-outs, LED, identity (rename/serial), reboot, full bus transactions,
and PWM are deferred behind the same core.

**Why.** The operator scoped v1 deliberately tight. The deferred items are cheap to add
later precisely *because* of Decisions 1–2 — each is a new `AppIntent` + a service call +
a render, with no architectural change. Notably analog is already in `BoardReport`
(`PinSnapshot.Adc`), so surfacing it later is render-only. Shipping the spine first and
proving the two-front-end architecture is worth more than a wide but shallow v1.

---

## Decision 6 — I2C "scan" is read-only ping-sweep; no transaction API in v1

**Decision.** The I2C feature is a bus scan only: `I2cLease.PingAsync` against each
address, returning responders. No read/write transaction surface yet.

**Why.** A scan is a single, high-value, low-risk operation ("what's on the bus") that
needs no device-specific protocol knowledge, and `PingAsync` already exists. A general
transaction API invites per-device protocol scope that belongs in the deferred bus work.

---

## Decision 7 — Native AOT is a target for both CLI and GUI; this fixes the MVVM choice

**Decision.** Both front-ends are built to publish as Native AOT. The CLI's `--json` uses a
source-generated `JsonSerializerContext` (no reflection serialization); the projects we own
set `<IsAotCompatible>true</IsAotCompatible>` (trim/AOT analyzers). The GUI (Decision 4) uses
**CommunityToolkit.Mvvm** — and the GUI will use Avalonia **compiled bindings**
(`x:DataType` + `AvaloniaUseCompiledBindingsByDefault`) rather than reflection bindings.

**Why.** CommunityToolkit.Mvvm is source-generator based (no runtime reflection) and is
AOT-compatible; **ReactiveUI is not reliably AOT-clean** (expression-tree `WhenAnyValue`,
reflection bindings, the Splat locator), so the AOT requirement settles Decision 4 in
CommunityToolkit's favour beyond just "slim". The real AOT gate for an Avalonia app is
reflection XAML bindings, addressed by compiled bindings, not the MVVM framework.

**Validated.** A Native AOT publish of the CLI produced a **4.3 MB self-contained native exe
with zero ILC trim/AOT warnings across the whole dependency chain** (Periphery, Usb,
Treehopper, Hid, Efm8Bootloader, Control) and ran correctly on hardware (`list`, `--json`).
The publish recipe (one-off env notes): the repo sets `GeneratePackageOnBuild=true`, which
native compilation rejects, and the native link needs the VS toolchain on PATH:

```bash
export PATH="/c/Program Files (x86)/Microsoft Visual Studio/Installer:$PATH"   # vswhere
dotnet publish src/Periphery.Treehopper.Control.Cli -c Release -r win-x64 \
  -p:PublishAot=true -p:GeneratePackageOnBuild=false \
  -p:TreehopperFirmwareImage=<abs .tfi> -p:TreehopperFirmwareVersion=<code>   # optional fleet embed
```

`PackAsTool` (IL dotnet-tool) and `PublishAot` (native exe) remain two separate distribution
modes of the same project; `PublishAot` stays a publish-time flag, never set in the csproj.
