# Feature Spec: Autoflash (hands-free flashing on plug-in)

<!--
Authoritative, LIVING spec for FlashAnything's autoflash mode. Read this before editing
any code in the feature's scope; the "Affected Layers" table names the projects to touch.
The "how / why" decisions live in the append-only sibling [`adr.md`](adr.md); this file is
the "what" and is rewritten as the feature evolves.
-->

## Status

**Implemented** — landed on `feat/autoflash` (the pure `AutoflashPolicy`, the MVU surface,
`IdentificationMode` on the contract, the autoflash shell in `FlashAnythingService`, and the
CLI `autoflash` + GUI Arm/Disarm front-ends), with the prerequisite refactor done first.

> **Prerequisite (done):** autoflash sits on the watcher-driven discovery refactor — the service
> now owns a `DeviceWatcher` filtered to flashable devices (push: Appeared → TargetDetected),
> replacing the pull-model `RefreshAsync`. (A per-family `MultiDeviceTracker` was not needed; the
> watcher's own filtered events suffice for a single flashable filter.) See
> [ADR-0061](../../../adr/0061-firmware-flashing-platform.md) and [`adr.md`](adr.md) Decision 1.

| Field        | Value                                   |
|--------------|-----------------------------------------|
| Author       | Charles Lee                             |
| Created      | 2026-06-16                              |
| Last Updated | 2026-06-18                              |
| Project      | Periphery.FlashAnything                 |
| Branch       | `feat/autoflash`                        |

---

## Purpose

Provision boards hands-free. Once the operator **arms** autoflash for a chosen firmware
image and target family, FlashAnything automatically flashes any matching device the
instant it is plugged in — plug in board after board on a bench or production line, each
gets the firmware with no further interaction. Autoflash is a thin, safety-gated *policy*
over the existing detect → flash pipeline, not a parallel system.

The non-negotiable framing: **auto-flashing is destructive and unattended**, so the entire
feature is built around *not* flashing the wrong thing. It is opt-in, restricted to
passively-identified devices, idempotent, and app-flash only.

---

## Dependencies / Prerequisites

| Depends on | Why |
|---|---|
| Watcher-driven discovery (one `DeviceWatcher` + per-family `MultiDeviceTracker`) | Autoflash is event-driven — it triggers on a *detection* event (`MultiDeviceTracker.DeviceAdded`). The current pull-model `RefreshAsync` has no "device just plugged in" signal. See [ADR-0061](../../../adr/0061-firmware-flashing-platform.md). |
| The bootloader contract (`IFirmwareProgrammer` / `IBootloaderProvider` / `BootloaderRegistry`) | Autoflash executes through the same per-target flash path as a manual flash. |
| At least one real flasher provider (e.g. `Periphery.Bootloader.Stm32.Usb`) | Until a provider exists, nothing is detected to autoflash. |
| Identification-mode signal on a provider (passive vs probe) | Autoflash must gate to **passively-identified** families only — see [Identification model](#identification-model). |

---

## Affected Layers

| Project | Change Type |
|---|---|
| `Periphery.FlashAnything` | **New:** pure `AutoflashPolicy` (decide flash/skip per detection); autoflash mode in `FlashAnythingService` (arm/disarm, subscribe to detections, drive the policy, dedupe); new `AppIntent.ArmAutoflash` / `DisarmAutoflash`; `AppState` armed-config + session tally; `AppReducer` additions. |
| `Periphery.Bootloader` | **Small:** `IBootloaderProvider` gains an `IdentificationMode` (Passive / Probe) so autoflash can include only passive families. |
| `Periphery.FlashAnything.Cli` | **New:** an `autoflash` mode — arm with an image + family, then run until Ctrl+C, printing per-device results. |
| `Periphery.FlashAnything.Gui` | **New:** an armed/disarmed control + indicator, the chosen image/family, and the live per-device result feed. |
| `tests/Periphery.FlashAnything.Tests` | **New:** `AutoflashPolicy` decision table + service-level autoflash via the fake device source + fake provider (arm → simulate plug-ins → assert flashes + dedupe + passive-only gating). |

This feature **does not** modify the Treehopper updater / control app.

---

## Requirements

- [ ] **Arm/disarm.** Autoflash is **disarmed by default**. Arming binds a loaded firmware
      image + a target family/provider (+ `FlashOptions`); disarming stops it. Both are
      explicit operator actions.
- [ ] **Trigger on detection.** When a device matching an armed family is detected
      (plugged in), automatically flash it through the existing per-target path
      (open → identify → flash → leave), reusing all of its safety gates.
- [ ] **Passive identification only.** Autoflash triggers **only** for families identified
      passively by USB VID/PID. Probe-identified (serial) targets are **never** auto-flashed
      (see [Identification model](#identification-model)).
      *In force. [`adr.md`](adr.md) Decisions 8-11 supersede this once implemented; until then
      this requirement governs — see [Presenting probe targets](#presenting-probe-targets-amendment-2026-09-03).*
- [ ] **Idempotent.** Each physical device is flashed **at most once per armed session**;
      post-flash re-enumeration (the board resetting back through the bootloader) must not
      re-trigger a flash.
- [x] **Bounded-parallel (per-family).** Distinct devices may flash concurrently, capped by
      `maxFlashConcurrency` (default 4 — a USB power/bandwidth bound, not CPU) **when the family's
      correlation has a per-board distinguisher that survives the mode switch** — a serial (`BySerial`)
      or a stable USB port (`ByLocationPath`). `ByLocationPath` makes no-serial correlation *exact*: an
      EFM8/Treehopper board does not change USB port when it reboots, so the re-enumerated bootloader is
      correlated to the exact board it came from by `LocationPath` (**Windows**-hardware-verified
      identical app↔bootloader port; Linux/macOS populate the field but are not yet hardware-verified).
      A family with **neither** serial nor stable port (`FirstAppearance` debounce) flashes **strictly
      one at a time**, enforced by a per-family serialization gate in `FlashAnythingService` — there the
      re-enumerated bootloader is indistinguishable and two concurrent waits would collapse onto the
      first-appearing one (see Safety rule 4). **EFM8:** correlating by port removes the *software*
      collapse, and concurrent EFM8 flashing is **hardware-verified on Windows** (two boards, overlapping
      upload windows, zero corruption) with `#220`'s physical bus-collision hypothesis **disproven** (see
      Safety rule 4). It is now the **default** (`allowConcurrentEfm8Flash: true`); `false` is the opt-out
      that forces serialization.
- [ ] **App-flash only.** Autoflash never performs the destructive ops (Read Unprotect /
      option-byte / RDP); those stay behind explicit manual confirmation.
- [ ] **Visible + reversible.** A clear armed indicator; immediate disarm; a confirm-before-arm
      summary naming the image + family that will be auto-flashed.
- [ ] **Observable.** Per-device autoflash outcomes + a running session tally
      (flashed / failed / skipped) fold into `AppState`; an audit list of what was flashed.
- [ ] **Pure policy.** The arm/skip/dedupe decision is a pure, total function, exhaustively
      unit-testable with no hardware (ADR-0052).

---

## Behaviour / lifecycle

```
ArmAutoflash(image, family, options)   operator opts in; FlashAnything records armed config
        │
        ▼
device plugged ──► MultiDeviceTracker.DeviceAdded ──► AppEvent.TargetDetected
        │
        ▼
AutoflashPolicy.Decide(armed, target, alreadyFlashed)
        │           ├─ Skip(not armed family)        ─┐
        │           ├─ Skip(not passively identified) ─┤── surfaced as a skip; no flash
        │           ├─ Skip(already flashed this id)  ─┘
        │           └─ Flash
        ▼
flash via the existing per-target path (bounded-parallel worker pool)  ──► FlashStarted/Progressed/Finished
        │
        ▼
record the device identity as flashed (debounce re-enumeration)
        ⋮  (repeats for each plugged device until DisarmAutoflash)
```

---

## Architecture (functional core / imperative shell — ADR-0052)

### Pure core — `AutoflashPolicy` (no IO, no clock, no `Task`)

```csharp
// PURE: given the armed config, a detected target, and what's already been flashed
// this session, decide what autoflash should do. Same inputs -> same decision.
public static AutoflashAction Decide(
    AutoflashConfig armed,
    FlashTargetView detected,
    IReadOnlySet<string> alreadyFlashed);

public abstract record AutoflashAction
{
    public sealed record Flash : AutoflashAction;
    public sealed record Skip(string Reason) : AutoflashAction;   // not-armed-family / not-passive / already-flashed / disarmed
}
```

### Imperative shell (in `FlashAnythingService`)

- Subscribes to the per-family `MultiDeviceTracker` detections (the discovery refactor).
- On each detection, calls `AutoflashPolicy.Decide`; on `Flash`, enqueues the device on the
  flash queue (drained by a bounded pool of workers — up to `maxFlashConcurrency` boards flash
  at once) and runs the existing per-target flash path; on `Skip`, records the reason (no flash).
- Owns the "already flashed this session" set and the post-flash debounce; owns arm/disarm.

---

## Public API (proposed)

```csharp
// Intents (front-end -> service)
public sealed record ArmAutoflash(string Path, string Family, FlashOptions Options) : AppIntent;
public sealed record DisarmAutoflash : AppIntent;

// Armed configuration (immutable)
public sealed record AutoflashConfig(string Family, FlashOptions Options /*, image handle */);
```

`AppState` gains the armed config (null when disarmed) + an autoflash session tally
(flashed / failed / skipped + an audit list). Per-device results reuse the existing
`FlashStarted` / `FlashProgressed` / `FlashFinished` events and `FlashStage`.

---

## Identification model (the load-bearing constraint)

Autoflash is only safe when the device's identity is known **without touching it**:

- **USB bootloaders = passive identification.** The VID/PID *is* the target (`0483:DF11` *is*
  an STM32 in DFU). Safe to detect and to act on unattended → **eligible for autoflash.**
- **Generic serial ports = the VID/PID identifies the *bridge* (FTDI / CP210x / CH340), not
  the target behind it.** Identifying the actual device requires an **active probe**
  (AN3155 `0x7F` autobaud, esptool `SYNC`, …). Auto-poking every COM port that appears with
  sync bytes risks disturbing unrelated hardware → **never auto-flashed.** Serial devices are
  flashed only by an explicit, manual operator action (with probe/confirm).

A provider declares its `IdentificationMode`; autoflash includes only `Passive` families.
See [ADR-0062](../../../adr/0062-periphery-serial-backend-provider.md) for the serial
lane and [`adr.md`](adr.md) Decision 4.

**This is shipped behaviour and it still governs.** Decision 4 is amended by Decisions 8-11, which
admit probe families on operator-bound bridges, but none of that is implemented. Until it is, a
probe-identified target is never auto-flashed, whatever the amendment below describes.

---

## Safety rules (this flashes firmware unattended — bricking is real)

1. **Opt-in, per-family, disarmed by default.** Nothing auto-flashes until the operator arms
   a specific image + family.
2. **Passive identification only.** Probe-identified (serial) targets are never auto-flashed.
   *In force until [`adr.md`](adr.md) Decisions 8-11 are implemented.*
3. **Idempotent + debounced.** A given physical device is flashed at most once per armed
   session; post-flash re-enumeration does not re-trigger.
4. **Bounded-parallel, per-family-safe.** Distinct devices may flash concurrently (capped by
   `maxFlashConcurrency`) **when the family's correlation uniquely identifies each board** — a serial
   (`BySerial`) or, for a no-serial family, a stable USB port (`ByLocationPath`). The shared-bootloader-id
   hazard (every EFM8 in the bootloader enumerates as `0x10C4:0xEAC9`) is fundamentally a **correlation**
   problem: with the `FirstAppearance` debounce, two concurrent app-mode flashes share the service's one
   `MultiDeviceTracker`, both arm with an empty baseline, and both correlate to the **same first-appearing**
   EAC9 — so both flash *one* physical board with interleaved HID writes (the deterministic corruption —
   a garbage `0x90` reply to a `0x33` write) while the second board is never flashed. **Topology
   correlation removes this at the root:** an EFM8 board does not change USB port when it reboots, so
   `ByLocationPath` correlates each re-enumerated bootloader to the exact board it came from, and the two
   flashes address two different boards. In the gate logic such families are treated as parallel-capable
   and are **not** gated; only a family with neither serial nor stable port (`FirstAppearance`) is held
   to **one board at a time** by the per-family serialization gate.

   `#220`'s serialization was a *safety* response to a **hardware** observation (the stagger dose-response),
   attributed to a physical current-collision on the shared USB bus. That hypothesis was **disproven**
   during investigation:
   - **Two separate processes, each flashing one board concurrently → zero corruption** — isolating the
     fault to **in-process shared state**, not the bus.
   - **Boards on separate USB controllers + a powered hub → still failed identically** — so it was never
     shared-bus power/current.

   The sole cause was the software correlation collapse (above), which `ByLocationPath` fixes at the root.
   Confirmed on hardware (2026-07-31, two Treehopper boards on distinct hub ports USB(2)/USB(3)):
   - Each EFM8 bootloader resolved to its **own** port via the parent USB node, so `ByLocationPath`
     correlated each board to its own app device.
   - The two uploads ran with **genuinely overlapping windows** (flash1 33.055→33.778s, flash2
     33.151→33.871s — simultaneous, versus `#220`'s serialize where flash2 began 255 ms *after* flash1
     finished), each **120/120 records, zero corruption**.

   So concurrent EFM8 flashing is **hardware-verified safe** on Windows, and the physical-collision
   concern is gone.
   - `ByLocationPath` is the **correctness** fix (always on — the right board every time).
   - **Concurrent flashing is the default.** `TreehopperFlasher.CreateService` defaults
     `allowConcurrentEfm8Flash: true` (full pool); `false` is the **opt-out** that forces serialization (a
     conservative fallback / debugging aid).
   - **Cross-platform:** hardware verification was on Windows; Linux (`syspath`) / macOS (`locationPath`)
     port-invariance is unverified. Default-on is still safe there because `ByLocationPath` is an **exact**
     match — a wrong/absent port never mis-flashes; it fails to correlate and the wait times out (a clean
     "did not re-enumerate" error). The failure mode is **fail-safe** (a visible timeout), never corruption
     or cross-correlation.
   - `#220`'s serialization is retained **unchanged** as the `FirstAppearance` posture for a family with
     neither serial nor stable port.
5. **App-flash only.** Read Unprotect / option-byte / RDP are never performed automatically.
6. **Visible + instantly reversible.** A prominent armed indicator; disarm takes effect
   immediately; arming shows a confirm summary of what will be flashed.
7. **Audited.** Every autoflash outcome is recorded (device identity + result) for the session.

---

## Front-ends

- **CLI** — `flashany autoflash --file <image> --family <name> [--yes]`: arm and run until
  Ctrl+C, printing each device's outcome and a running tally. Without `--yes`, a dry run
  (report what *would* be flashed on plug-in, flash nothing).
- **GUI** — an Arm/Disarm control bound to the loaded image + selected family, a prominent
  armed indicator, and the live per-device result feed (reusing the target rows).

> Probe-identified families present differently — one persistent row per bound bridge rather than
> one per device, and a tally of flashes rather than of boards. See
> [Presenting probe targets](#presenting-probe-targets-amendment-2026-09-03).

---

## Presenting probe targets (amendment, 2026-09-03)

*Decided, not yet implemented.* [`adr.md`](adr.md) Decisions 8-11 admit probe-identified targets
to autoflash and assume a presentation model without describing one. This section is that model.
It does not change anything about passive families.

### The row is the bridge, not the chip

A serial target has two levels of identity and only the outer one is real. The USB-serial bridge
is a genuine `DeviceInfo` from the watcher — VID/PID, `LocationPath`, often a `SerialNumber` —
and Decision 8 binds the arm to exactly that. The chip behind it is knowable only by probing, and
what a probe returns is a family: every STM32G431 answers Get ID with `0x0468`. Two boards in
sequence are indistinguishable.

So the chip cannot be a row. **It is an occupancy state of the bridge's row.**

| Row state | Means | Basis |
|---|---|---|
| `no response` | probe sent, nothing came back | **indeterminate** |
| `absent` | present-detect line deasserted | observed, `--repeat=cts` only |
| `occupied 0x468` | probe answered, chip id read | observed |
| `flashing 42%` | flash in progress | observed |
| `flashed` | written and verified | observed |
| `failed <reason>` | flash failed | observed |

**Silence is not absence, and the row must not claim it is.** A probe that gets nothing back is
consistent with an empty fixture, but equally with a board that is seated and unresponsive, a
non-STM32 device on that bridge, RX/TX swapped, a part held in reset, or one that has left the
bootloader for its application — the last of which is the *expected* end of every successful
flash. `no response` is therefore the honest state, and only a present-detect line can produce
`absent`.

The distinction is not cosmetic. Under `--repeat=silence`, Decision 10 releases the dedupe gate on
`no response`, so a row rendered as `empty` would tell an operator the fixture is ready for the
next board on evidence that does not support it. Probing continues in that state, which means
bytes keep going to whatever is actually attached — the accepted hazard of Decision 8, and one an
operator should be able to see they are still in.

The row persists for the armed session, because the fixture is still there whether or not a board
is in it. This is the visible difference from a passive family, where a row appears and disappears
with the device itself.

### The tally counts flashes, not boards

`AutoflashTally.Audit` builds its entries from `DeviceId` (`flashed {id}`). For a fixture that
yields `flashed COM7`, `flashed COM7`, `flashed COM7` — three identical lines that say nothing
about which board each was. Probe rows need a **per-row sequence number** instead: `COM7 #1`,
`COM7 #2`. A position in a sequence is what can honestly be produced; a name is not.

That forces a labelling rule, and it is deliberate rather than pedantic. **Under
`--repeat=silence` the summary says "3 flashes", never "3 boards."** Decision 10 is explicit that
departure-gating is a heuristic: a board that resets while seated and re-enters the bootloader is
counted again, and nothing available can distinguish that from a replacement. "Boards" may only be
claimed where a presence line backs it (`--repeat=cts`), which is the one mode where occupancy is
observed rather than inferred.

### Inferred occupancy must look inferred

`no response` and `absent` must not render alike, and neither may be styled as a settled fact the
way `occupied` is. Same discipline as marking probe targets as unconfirmed in `flashany list` —
still open below. The wording matters more than the styling: a state named for what was observed
(`no response`) cannot be misread, while one named for what was inferred (`empty`) invites exactly
that, which is how this table read before review caught it.

### Probe rows and passive rows are different claims

`COM7 · fixture · board #3` and `STM32 DFU 0483:DF11 · SN 207D34...` assert different things. The
first is a position in a sequence on a named fixture; the second is a device that identified
itself. Rendering them in one undifferentiated list is what would let an operator read a position
as an identity, which is the mistake this whole model exists to prevent.

### Front-end deltas

- **CLI** — while armed on a probe family, print a persistent per-fixture line that updates in
  place rather than a new line per detection, and a tally labelled per the rule above. The arm
  confirmation already has to enumerate the bound ports and state that probing sends bytes to
  whatever is attached (Decision 8).
- **GUI** — one row per bound bridge, showing occupancy and the running count for that fixture,
  visibly distinct from passive target rows.

---

## Open Questions

- [x] **Stable identity for dedupe + per-family concurrency.** *Per-family concurrency —
      **implemented**, then **unlocked for no-serial families via USB topology**.* Correlation now has
      three modes: `BySerial` (serial survives), `ByLocationPath` (no serial, but the physical USB port
      survives — the EFM8/Treehopper case, hardware-verified app↔bootloader port identity), and
      `FirstAppearance` (neither — debounce). `FlashAnythingService` serializes **only** `FirstAppearance`
      families (a per-family `SemaphoreSlim(1,1)` gate over the reboot → correlate → flash window);
      `BySerial` and `ByLocationPath` both carry a per-board distinguisher, so the gate treats them as
      parallel-capable under `maxFlashConcurrency`. For EFM8/Treehopper this makes concurrent flashing
      *correlation-safe* — each board is addressed by its own port — because a real root cause was the
      correlation collapse (both `FirstAppearance` waits accepting the first-appearing bootloader), not
      the OS id. Concurrent EFM8 flashing is now **hardware-verified on Windows** (two boards, overlapping
      upload windows, zero corruption), and `#220`'s physical bus-collision hypothesis was **disproven**
      (two-process and separate-controller evidence). It is the **default** for Treehopper
      (`allowConcurrentEfm8Flash: true`); `false` is the opt-out that forces serialization. On an
      unverified platform the exact-match correlation is fail-safe — a wrong/absent port times out rather
      than mis-flashing (see Safety rule 4).
      *Still open:* the **dedupe key** for no-serial *sequential re-arrival* (a flashed EFM8 that
      re-enters the bootloader on the same port) — that "flashed this id already" question is distinct
      from the concurrency gate and remains keyed on the device leaving before the next arrives. (The
      port is now a candidate stable key for it, too.)
- [ ] **Post-flash re-enumeration.** A flashed STM32 that leaves DFU disappears (won't
      re-trigger); but a board whose BOOT0 is still asserted re-enters DFU and reappears.
      Debounce window vs. serial-based dedupe — which per family?
- [x] **Probe-based autoflash (serial).** *Decided, not yet implemented.* Deferred entirely from
      v1; the per-port probe-and-confirm flow this bullet asked for is now [`adr.md`](adr.md)
      Decisions 8-11 (arm binds a bridge identity, a per-bridge probe loop that owns detection,
      departure-gated dedupe, open-per-probe-cycle). The
      safety rules below still describe shipped behaviour: until those land, probe targets are
      never auto-flashed.
- [ ] **Multi-arm.** v1 arms a single family/image at a time. Arming several families at once
      (a mixed bench) is a possible later extension.

---

## Related

| Type | Link |
|------|------|
| Decisions (how / why) | [`adr.md`](adr.md) |
| Firmware-flashing platform | [`../../../adr/0061-firmware-flashing-platform.md`](../../../adr/0061-firmware-flashing-platform.md) |
| Serial backend / probe lane | [`../../../adr/0062-periphery-serial-backend-provider.md`](../../../adr/0062-periphery-serial-backend-provider.md) |
| Functional-core / shell | [`../../../adr/0052-periphery-treehopper-pure-core.md`](../../../adr/0052-periphery-treehopper-pure-core.md) |
| Service it extends | [`FlashAnythingService.cs`](../../../../src/Periphery.FlashAnything/FlashAnythingService.cs) / [`AppReducer.cs`](../../../../src/Periphery.FlashAnything/AppReducer.cs) |
| Discovery primitive | [`DeviceWatcher.cs`](../../../../src/Periphery/DeviceWatcher.cs) / [`MultiDeviceTracker.cs`](../../../../src/Periphery/MultiDeviceTracker.cs) |
| Bootloader contract | [`IBootloaderProvider.cs`](../../../../src/Periphery.Bootloader/IBootloaderProvider.cs) |
