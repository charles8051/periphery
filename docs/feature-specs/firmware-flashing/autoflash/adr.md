# ADR: Autoflash (hands-free flashing on plug-in)

<!--
Append-only / superseded, never rewritten. Decisions are numbered Decision 1..N.
The "what" (living requirements, current API) is in the sibling spec.md; this file
records the "how / why" so a future contributor sees the tradeoffs, not just the result.
If a decision here grows to be cited by a second feature, graduate it into a numbered
repo-level ADR under docs/adr/.
-->

Context: FlashAnything (the "flash anything" app, [ADR-0061](../../../adr/0061-firmware-flashing-platform.md))
can already flash a target and fleet-flash all detected targets. Autoflash adds an
*unattended* mode: arm once, then flash matching devices automatically as they are plugged
in. Because it acts without a human in the loop on a destructive operation, every decision
below is dominated by "don't flash the wrong thing." Periphery has no external consumers, so
the bias is "right design" over "compatible." See [`spec.md`](spec.md).

---

## Decision 1 — Autoflash rides the watcher-driven discovery refactor; it is not its own discovery path

**Decision.** Autoflash is built on the planned discovery refactor (one `DeviceWatcher` +
one `MultiDeviceTracker` per uniquely-identifiable family) and triggers on its detection
events. It does **not** introduce a second enumeration/watch mechanism, and the refactor is
a prerequisite, not part of this feature.

**Why.** Autoflash is fundamentally "do X when a device is plugged in" — it needs a *push*
"device appeared" signal. The current pull-model `RefreshAsync` has none. The watcher already
provides exactly this (`MultiDeviceTracker.DeviceAdded` + the state stream) and is the
intended primitive (one watcher fans every event out to every tracker). Bolting a separate
poll loop onto autoflash would duplicate discovery and split the safety gates across two
paths. Sequencing the refactor first keeps autoflash a thin policy.

---

## Decision 2 — A pure `AutoflashPolicy.Decide(...)` core; the shell only subscribes and executes

**Decision.** The arm / skip / dedupe decision is a pure, total function
`AutoflashPolicy.Decide(armedConfig, detectedTarget, alreadyFlashed) -> AutoflashAction`
(`Flash` | `Skip(reason)`). The service shell subscribes to detections, calls `Decide`, and
executes flashes; it holds no decision logic.

**Why.** ADR-0052's grain. The interesting, safety-critical logic *is* the decision (is this
the armed family? is it passively identified? have we already flashed it?). Making it a pure
function means the entire policy — including every skip reason — is a decision table
exhaustively testable with hand-built inputs, no hardware and no clock. The shell is then a
thin subscribe-and-run loop with nothing to unit-test but wiring.

---

## Decision 3 — Explicit arm, per-family, disarmed by default

**Decision.** Autoflash does nothing until the operator explicitly arms it for a specific
firmware image + target family (+ flash options). Disarmed is the default and only resting
state; disarm is immediate.

**Why.** Auto-flashing on plug-in is powerful and surprising — a background mode that
silently rewrote firmware on any matching device would be a foot-gun. Requiring an explicit,
named arm (which image, which family) makes the dangerous behaviour a deliberate act with a
visible indicator, mirroring the required-confirmation gate on the manual destructive path
(the Treehopper reflash `Efm8FlashConfirmation` pattern). Per-family (not "anything
flashable") so arming for STM32 can't accidentally flash an EFM8 that happens to appear.

---

## Decision 4 — Autoflash triggers only for **passively-identified** families; probe-identified (serial) targets are never auto-flashed

**Decision.** A provider declares an `IdentificationMode` (`Passive` for USB VID/PID,
`Probe` for serial). Autoflash includes only `Passive` families. Serial / probe-identified
devices are flashed exclusively by an explicit manual action.

**Why.** This is the load-bearing safety decision. Passive identification means the device's
identity is known *without touching it* — a `0483:DF11` USB device *is* an STM32 in DFU, so
acting on it unattended is safe. A serial port's VID/PID identifies only the USB-serial
**bridge** (FTDI / CP210x / CH340), not whatever is wired behind it; establishing the real
target requires actively probing (sending AN3155 `0x7F` / esptool `SYNC` / …). Auto-probing
every COM port that appears would poke arbitrary, possibly-unrelated hardware with protocol
bytes the instant it is plugged — unacceptable for an unattended mode. So the passive/active
split from [ADR-0062](../../../adr/0062-periphery-serial-backend-provider.md) becomes
a hard autoflash boundary: passive may auto, probe may not.

---

## Decision 5 — Idempotent, debounced, and strictly sequential

**Decision.** Each physical device is flashed at most once per armed session; post-flash
re-enumeration does not re-trigger; devices are flashed one at a time.

**Why.** A flashed board typically resets and re-enumerates — often back through the
bootloader — which would re-fire a detection and, naively, re-flash it in a loop. Tracking
"already flashed this session" and debouncing the re-appearance breaks the loop. Sequential
execution serves two ends: it bounds the blast radius (a misconfigured arm flashes one device
before the operator can disarm, not a whole bench at once), and it is *required* for
shared-bootloader-id families — every EFM8 in the bootloader enumerates as `0x10C4:0xEAC9`,
so two in the bootloader at once are indistinguishable (the Treehopper sequential-flash
hazard). The dedupe key per family is an open question (serial where available; sequential +
left-the-bootloader where not) — see `spec.md`.

---

## Decision 6 — App-flash only: autoflash never performs the destructive ops

**Decision.** Autoflash performs only an application-image flash. Read Unprotect, option-byte
writes, and RDP changes are never triggered automatically — they remain behind explicit
manual confirmation.

**Why.** Those operations are irreversible or near-irreversible (RDP Level 2 is permanent;
Read Unprotect mass-erases). They must never happen without a human explicitly asking for
*that* operation on *that* device. Restricting autoflash to app-flash keeps the unattended
path within the recoverable envelope (a re-runnable flash), consistent with ADR-0061's
guard on the dangerous STM32 DFU ops.

---

## Decision 7 — Reuse the MVU event/state model; no parallel autoflash state machine

**Decision.** Autoflash results flow through the existing `AppEvent` /  `AppReducer` /
`AppState` model — per-device outcomes reuse `FlashStarted` / `FlashProgressed` /
`FlashFinished` and `FlashStage`; arming adds an immutable armed-config + a session tally to
`AppState`. There is no separate autoflash state object.

**Why.** Both front-ends already render `AppState`; routing autoflash through the same reducer
means the GUI and CLI show armed status and live per-device results for free, with one
rendering model and one place state is computed. A parallel state machine would duplicate the
target lifecycle and force each front-end to merge two sources. Arm/disarm are intents
(front-end → service) like every other user action; the resulting state changes are events
folded by the pure reducer.
