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

---

# Amendment (2026-09-02): probe-identified autoflash, scoped to a named port

Decisions 8–10 amend Decisions 4, 1, and 5 respectively. Nothing above is rewritten.

**What prompted it.** `Periphery.Bootloader.Stm32.Serial` merged ([#153](https://github.com/charles8051/periphery/pull/153),
`6fd0e9d`), so the STM32 system UART bootloader is now a flashable family in FlashAnything.
It is `IdentificationMode.Probe`, which Decision 4 excludes from autoflash entirely. The
operator use case that exposed the gap is a **test fixture**: a USB-serial bridge wired to
pogo pins, boards dropped in and taken out by hand, the port never disappearing. Under
Decisions 1, 4 and 5 that bench cannot be automated at all.

Decision 4's reasoning is not overturned. Its hazard is *blanket* auto-probing — poking every
COM port that appears. `spec.md` already named the way out, and this amendment is that flow:

> Revisit only with a safe, explicit, per-port probe-and-confirm flow — never blanket auto.

---

## Decision 8 — Probe families may autoflash, but only on ports the operator named (amends Decision 4)

**Decision.** `AutoflashConfig` gains a set of `SerialPortName`. A `Probe` target is eligible
only when its port is in that set; the set is empty for passive families and required for
probe families (an arm that omits it is refused at arm time, not silently disarmed).
`AutoflashPolicy.Decide`'s rule 2 becomes a scope check rather than a ban:

```csharp
if (detected.Identification != IdentificationMode.Passive
    && (detected.PortName is not { } port || !armed.Ports.Contains(port)))
    return new AutoflashAction.Skip("probe-identified and not on an armed port");
```

`FlashTargetView` gains `PortName` so the policy has the input. Passive families are
unaffected: with an empty set the expression short-circuits on the first clause and every
existing decision-table case keeps its meaning.

**Why.** Decision 4 rests on identity being knowable without touching the device. For serial
that is false — a bridge's VID/PID names the bridge — and the amendment does not pretend
otherwise. What it changes is *where consent comes from*. For DFU, `0483:DF11` is the
operator's consent, supplied by the device. For serial there is no such signal, so the
operator supplies it directly by naming the port at arm time. `--port COM7` is a statement
about the bench: *this port is wired to a target I intend to flash*. That is narrower than
what the manual path already permits, since the manual path lets an operator flash any port
on the machine.

The set, not a single port, because a multi-fixture bench is the case that motivated this and
one arm per port would multiply the armed sessions rather than the targets.

**Consequence, accepted.** A port in the set that is *not* an STM32 receives `0x7F` at 8E1,
once per probe cycle, until disarm. If something else is wired there — a GPS module, a motor
controller — it receives a stray byte on that cadence. Scoping to a named port reduces this
hazard to one the operator chose. It does not remove it, and it is the price of the feature.

---

## Decision 9 — A per-port probe loop, because hotplug does not fire for a swapped board (amends Decision 1)

**Decision.** While armed on a probe family, the service runs one probe loop per armed port:
open, AN3155 sync, read the chip id, close on failure. A transition absent → present emits
`TargetDetected`; N consecutive silences emit `TargetRemoved`. The probe deadline is its own
short timeout, separate from `Stm32SerialOptions.CommandTimeout`.

**Why.** Decision 1 forbids a second discovery path, and this does not add one. The
`DeviceWatcher` still discovers *ports*; the probe loop resolves what is *behind* a port the
watcher already found. It is an identification stage on an existing target, not a parallel
enumeration — it never learns about a port the watcher did not report, and it starts only for
ports in the armed set.

It is needed because there are two arrival shapes and only one produces a device event:

| Shape | Port | Detection |
|---|---|---|
| Bridge plugged together with the board (Nucleo, CH340 dongle) | appears / disappears | watcher fires, then probe |
| Board swapped in a fixture, bridge on the fixture | persists | **no event** — poll only |

The second is the fixture case, and no amount of watcher plumbing sees it. A board dropped
onto pogo pins changes nothing the OS reports.

The separate timeout matters more than it looks: `CommandTimeout` defaults to 5 s, so an
empty fixture polled on that deadline spends every cycle waiting, and disarm latency inherits
it. A few hundred milliseconds is the right order for "is anyone there."

The probe is cheap to build — `Stm32SerialProgrammer.OpenAsync` followed by `IdentifyAsync`
already *is* it. `SyncAsync` treats the NACK an already-synced part returns to a second `0x7F`
(AN3155 §3.1) as success, which is what makes the probe safe to repeat against a part that
stays put between cycles.

---

## Decision 10 — Probe autoflash dedupes on departure, not on device id (amends Decision 5)

**Decision.** For probe families the already-flashed key is the **port**, and it is cleared
when the part leaves: after a successful flash the port is not re-eligible until N consecutive
probes fail. Decision 5's per-device idempotence is unchanged for passive families.

**Why.** Decision 5 keys on `DeviceId`, which for a serial target is the port — the probe
yields only a chip id and a protocol version, and every STM32F407 on earth reads back `0x413`.
Keyed on the port alone, an armed session flashes exactly one board and then skips forever,
which defeats the fixture case completely. This is the "sequential + left-the-bootloader" key
Decision 5 already lists as open for shared-bootloader-id families, applied to a family where
it is the only key available.

Departure-gating also matches how the bench is actually operated: drop a board in, it flashes,
lift it out, the next one flashes. The gate is the operator's hand, and the loop observes it.

**The upgrade, when a chip database exists.** STM32 parts carry a 96-bit unique id in system
memory, readable with AN3155 Read Memory (`0x11`) — at `0x1FFF7A10` on F4, `0x1FFFF7E8` on F1,
elsewhere on other families. Reading it would give probe autoflash a per-device key exactly as
strong as the USB one, and would retire departure-gating. It needs a per-family address table,
which does not exist yet, and it is unavailable under read protection. Deferred, not rejected.

---

## Not decided here

- **Which front-ends expose the port set.** The CLI shape is assumed to be
  `--port COM7 --port COM9`; the GUI equivalent is untouched.
- **Whether the armed session holds the port open between cycles.** Open-per-cycle is simpler
  and lets other tools use the port while armed; a long-lived hold removes the reset race
  between the deciding probe and the flash that follows it. See
  [ADR-0062](../../../adr/0062-periphery-serial-backend-provider.md) §9.
- **ESP32.** Its modern parts enumerate as a USB family and are passive; the fixture question
  reaches it only on the legacy bridged path.
- **Nothing here is verified against hardware.** The AN3155 flasher itself has never flashed a
  real STM32 (`#153`), and a probe loop's timing against a real part — sync latency, how long a
  removed board takes to go quiet — is unmeasured.
