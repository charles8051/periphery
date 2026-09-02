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

# Amendment (2026-09-02): probe-identified autoflash, scoped to an operator-bound port

Decisions 8–10 amend Decisions 4, 1, and 5 respectively. Decision 11 amends nothing; it is a
new decision this amendment introduces. Nothing above is rewritten.

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

## Decision 8 — Probe families may autoflash, but only on a bridge the operator bound (amends Decision 4)

**Decision.** `AutoflashConfig` gains a set of **bridge identities**, not COM names. An
identity is the USB-serial bridge's `VendorId` + `ProductId` + `LocationPath`, plus
`SerialNumber` when the bridge exposes one — the `ByLocationPath` correlation safety rule 4
already uses. The operator names `COM7` at arm time; the arm resolves it to the identity of
the bridge currently behind it and binds *that*. A `Probe` target is eligible only when the
bridge it sits behind matches a bound identity; the set is empty for passive families and
required for probe families (an arm that omits it is refused at arm time, not silently
disarmed). `AutoflashPolicy.Decide`'s rule 2 becomes a scope check rather than a ban:

```csharp
if (detected.Identification != IdentificationMode.Passive
    && (detected.Bridge is not { } bridge || !armed.Bridges.Contains(bridge)))
    return new AutoflashAction.Skip("probe-identified and not on a bound bridge");
```

`FlashTargetView` gains the bridge identity so the policy has the input. Passive families are
unaffected: with an empty set the expression short-circuits on the first clause and every
existing decision-table case keeps its meaning.

**A disconnect breaks the bind.** If the bound bridge stops being present, the loop stops and
stays stopped. It does not resume when a bridge matching the identity comes back; the operator
re-arms. This is what makes the identity sufficient rather than merely better than a string.

Without it the identity is still guessable hardware. `LocationPath` names the USB socket, not
the physical bridge, and `VendorId` + `ProductId` names the model — so an identical bridge in
the same socket produces the same identity and would inherit the authorization. That is not a
corner case: CH340s commonly expose no `SerialNumber` at all, so on the most ordinary bridge
on the bench the composite is all there is. Refusing to arm on a serial-less bridge would rule
out that hardware entirely, which is too much. Breaking the bind on disconnect closes the same
hole from the other side, because swapping a bridge requires unplugging one — and it costs the
operator a re-arm only in the case where something was physically changed.

**Why an identity and not the COM name.** A COM name is not an identity, and treating it as
one silently transfers the operator's consent to hardware they never saw. Windows recycles
COM numbers: unplug the bound bridge, plug in a GPS receiver, and the OS can hand it `COM7`.
A loop authorized against the *string* keeps probing, and now sends `0x7F` to the GPS. The
operator consented to a bench, not to a number, and the number is the part that moves. Binding
the identity makes the failure loud instead: the bound bridge is gone, so the loop stops. If
the name resolves to an identity that is not the bound one, the loop refuses and says so
rather than probing.

**Why.** Decision 4 rests on identity being knowable without touching the device. For serial
that is false — a bridge's VID/PID names the bridge — and the amendment does not pretend
otherwise. What it changes is *where consent comes from*. For DFU, `0483:DF11` is the
operator's consent, supplied by the device. For serial there is no such signal, so the
operator supplies it directly by pointing at a bridge at arm time. `--port COM7` is a statement
about the bench: *this fixture is wired to a target I intend to flash*. That is narrower than
what the manual path already permits, since the manual path lets an operator flash any port
on the machine.

The set, not a single bridge, because a multi-fixture bench is the case that motivated this and
one arm per fixture would multiply the armed sessions rather than the targets.

**Consequence, accepted — and it must be shown, not just recorded here.** A bound bridge with
something other than an STM32 behind it receives `0x7F` at 8E1, once per probe cycle, until
disarm. If a GPS module or a motor controller is wired there, it gets a stray byte on that
cadence. Binding to a bridge the operator chose reduces this hazard; it does not remove it.

Decision 3 makes the dangerous behaviour deliberate and visible, and that obligation extends
here: **the arm confirmation must enumerate every bound port with the bridge behind it, and
state that probing sends bytes to whatever is attached.** Naming only the image and family —
what the confirmation says today — lets an operator accept this consequence without being
told it exists. Disarm stops every loop.

---

## Decision 9 — A per-port probe loop, because hotplug does not fire for a swapped board (amends Decision 1)

**Decision.** While armed on a probe family, the service runs one probe loop per bound bridge:
open, AN3155 sync, read the chip id, close on failure. A transition absent → present emits
`TargetDetected`; N consecutive silences emit `TargetRemoved`. The probe deadline is its own
short timeout, separate from `Stm32SerialOptions.CommandTimeout`.

**The loop owns detection for probe families.** A watcher appearance for a bound bridge
*registers and starts the loop, and emits nothing*. Only the loop may emit `TargetDetected`
for a probe target.

This ownership rule is not optional tidiness. `OnTrackerState` already emits `TargetDetected`
for any device the registry matches, and `Stm32SerialBootloaderProvider.CanHandle` claims
every device carrying a `PortName` — so a bridge and board plugged in together would produce
a watcher detection *and* a probe detection for the same physical target. `MaybeAutoflash`
fires on first detection, so the two paths race: a double flash, a flash dispatched before the
probe has established there is an STM32 there at all, or two opens of one COM port. Routing
every probe-family detection through the loop leaves one lifecycle per target and one place
that decides a target exists.

**Probing suspends while a flash runs on that bridge.** One state machine per bound bridge,
holding the port handle, in exactly one of: probing, flashing, or stopped. A flash takes
ownership from the probe cycle that dispatched it (Decision 11 hands over the open handle) and
the cadence does not tick again on that bridge until the flash finishes.

Without that, Decision 9's cadence and Decision 11's spanning handle fight each other: the
next cycle either tries to open a port the flash still owns, or reuses the handle and injects
`0x7F` into the middle of a Write Memory sequence. A flash long enough to outlast N cycles
would also read as N silences and emit `TargetRemoved` for the target being flashed at that
moment. One state machine per bridge is also simply less machinery than a cadence plus a lock,
which is the reason to prefer it over bolting exclusion onto two independent loops.

**Why.** Decision 1 forbids a second discovery path, and this does not add one. The
`DeviceWatcher` still discovers *ports*; the probe loop resolves what is *behind* a port the
watcher already found. It is an identification stage on an existing target, not a parallel
enumeration — it never learns about a port the watcher did not report, and it starts only for
bridges in the armed set.

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

**Where this is a heuristic, stated plainly.** Silence is not proof a board left, and the
flash *itself manufactures the silence*: `LeaveAfterFlash` issues Go, the part jumps to the
application, and it stops answering AN3155 while sitting right where it was. That is the
expected end of every successful flash, so the gate opens on every success by design. A board
that then re-enters the bootloader — BOOT0 still strapped and something resets it — reads as
an arrival and is flashed again. Nothing available today distinguishes that from a
replacement board.

Three things narrow it, and none closes it:

1. **Require `LeaveAfterFlash` for probe autoflash.** A flashed part that stays in the
   bootloader keeps answering, never goes silent, and never releases its own gate — but it
   also never lets the next board in. Leaving is what makes silence mean *finished*.
2. **Distinguish the sync answer.** AN3155 §3.1 ACKs the first `0x7F` since reset and NACKs a
   later one, so a NACK means *this part has not reset since we last spoke to it* and an ACK
   means it has. That separates a part sitting untouched from one that arrived or restarted.
   It does not separate an arrival from a re-strapped reset. Note that `SyncAsync` currently
   collapses both answers into success, deliberately — a probe wanting this signal has to
   stop doing that.
3. **A strapped BOOT0 that also spontaneously resets is a bench misconfiguration**, and this
   design assumes the fixture does not have one. That is a precondition, not a guarantee.

**A fixture can supply a real presence signal, and that is the only thing that closes this.**
Silence is inference. A hardware present-detect line is observation: wire the fixture's
board-seated sense to the bridge's CTS, DSR, or DCD input, and the host reads the transition
directly — both `RJCP.SerialPortStream` and the BCL `SerialPort` expose those inputs, so
nothing new is needed below `ISerialPort` to read one.

So `--repeat` takes a presence source. `--repeat=cts` (or `dsr`, `dcd`) gates on the line and
gives the departure/arrival transition this decision otherwise only infers; a board that never
lifts off the pins never releases the gate, whatever the protocol does. `--repeat=silence` is
the fallback for a fixture with nothing wired, and it carries the residual above.

A seated line is not an identification. It says something is in the fixture, not that the
something is an STM32 — a GPS wired to a bound bridge can hold CTS asserted all day and still
get `0x7F` once per cycle. Nothing about presence detect narrows *who gets probed*; that is
Decision 8's job, done by the bound bridge and stated in the arm confirmation. Presence detect
answers only *has this board left*, which is the question the gate asks.

The line is not required, because requiring it would rule out every fixture already built
without one. It is the supported way to get replacement-safety before the UID lands, and a
fixture being designed now should wire it.

**So the loop is bounded by default.** One flash per bound bridge per armed session, which is
Decision 5's original guarantee unchanged. Re-arming a bridge after its board departs is
opt-in — `--repeat` — and that flag is the operator saying *this is a fixture and I intend to
flash a succession of boards through it*. Without it a board that re-enters the bootloader
cannot be flashed a second time, whatever the gate thinks it saw.

That does not make departure-gating correct. It bounds what being wrong costs: one unintended
re-flash of a board that was just flashed with the same image, on a bench the operator
explicitly put into repeat mode, recorded in the session audit like every other outcome.

Replacement-safety proper needs a present-detect line, the UID below, or an operator confirm
per board. Under `--repeat=silence` specifically, departure-gating is a heuristic that fits an
attended-adjacent fixture, and it should not be described as more.

**The upgrade, when a chip database exists.** STM32 parts carry a 96-bit unique id in system
memory, readable with AN3155 Read Memory (`0x11`) — at `0x1FFF7A10` on F4, `0x1FFFF7E8` on F1,
elsewhere on other families. Reading it would give probe autoflash a per-device key exactly as
strong as the USB one, and would retire departure-gating. It needs a per-family address table,
which does not exist yet, and it is unavailable under read protection. Deferred, not rejected.

---

## Decision 11 — Open the port per probe cycle, and do not close it between the probe and the flash it triggers

**Decision.** A probe cycle opens the port, syncs, identifies, and closes. The port is free
between cycles. When a cycle decides to flash, it **keeps that same open handle** and flashes on
it rather than closing and reopening.

**Why.** The choice was framed as open-per-cycle against holding the port for the whole armed
session, and the only real argument for holding was the race: the part can change between the
probe that decided to flash and the open that flashes it. But that is not an argument for
holding *across* cycles. It is an argument for not closing *within* one.
`Stm32SerialProgrammer` is already both the prober and the flasher — probing is
`OpenAsync` + `IdentifyAsync`, flashing is `FlashAsync` on the same instance — so a cycle that
decides to flash simply declines to dispose. The race disappears and the port still goes free
the moment the cycle ends.

What remained for holding was reopen cost, which at a ~1 Hz cadence is noise, and reopen
*flakiness*, which is unmeasured (ADR-0062 §8) and which open-per-cycle is the design that
surfaces rather than hides.

Against holding, and decisive: an armed session that locks a port for hours locks out the
operator's terminal and every other tool on that bench, with no way to share and no obvious
way to walk it back once consumers depend on it. A held handle is also what leaks when the
process dies badly — a stuck COM handle on Windows can need a replug to clear. And the change
is one-way: open-per-cycle can become holding later, behind the same policy. Holding cannot
cheaply become open-per-cycle.

**A caveat that will age.** The race is nearly content-free today, because the probe learns
nothing device-specific — every STM32 that ACKs looks alike, and re-probing a swapped board
reaches the same decision. It becomes a real race when Decision 10's UID upgrade lands and the
probe starts learning an identity the flash then assumes. Not closing within the cycle covers
that case too, which is why this is the shape to build now rather than the one to revisit then.

---

## Not decided here

- **The arm affordance in each front-end.** Decision 8 fixes what the confirmation must say —
  every bound port, the bridge behind it, and that probing sends bytes to whatever is attached.
  How each front-end presents that is open; the CLI shape is assumed to be
  `--port COM7 --port COM9`, and the GUI equivalent is untouched.
- **The three constants.** Probe cadence, probe timeout, and how many consecutive silences mean
  a board has left. Decisions 9 and 10 fix the shape and give orders of magnitude (~1 Hz, a few
  hundred milliseconds); the values want a real fixture, not an argument.
- **ESP32.** Its modern parts enumerate as a USB family and are passive; the fixture question
  reaches it only on the legacy bridged path.
- **Nothing here is verified against hardware.** The AN3155 flasher itself has never flashed a
  real STM32 (`#153`), and a probe loop's timing against a real part — sync latency, how long a
  removed board takes to go quiet — is unmeasured.
