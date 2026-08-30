---
title: "ADR-0076: The updater sits behind the recovery seam — resetting a device that will not enter its bootloader"
status: "Accepted"
status_note: "Shipped - `Periphery.Bootloader.BootloaderEntryRecovery`."
date: "2026-08-07"
authors: "@charles8051"
tags: ["architecture", "decision", "bootloader", "firmware-update", "device-reset", "recovery"]
supersedes: ""
superseded_by: ""
depends_on: "ADR-0060 (the recovery seam this reuses), ADR-0075 (the out-of-band rung that makes it worth having), ADR-0063 (the bootloader-entry orchestration this extends), ADR-0052 (pure-core pattern), ADR-0073 (observations, not verdicts)"
---

# ADR-0076: The updater sits behind the recovery seam

## Status

**Accepted.** Shipped as `Periphery.Bootloader.BootloaderEntryRecovery`.

## Context

ADR-0063 gave every app-mode device one shared spine for firmware updates:
`BootloaderEntryOrchestrator` drives **enter → wait → gate → flash**, where
"enter" is a small device-specific `IBootloaderEntry` that puts the device into
its bootloader.

For a Treehopper that entry is:

```csharp
await using var board = await TreehopperBoard.OpenAsync(applicationDevice, ct);
await board.RebootIntoBootloaderAsync(ct);        // wire opcode 0x0D
```

Both halves travel over `EP_PeripheralConfig` — `OpenAsync` because it
reconciles a `ConfigureDevice` write before returning (the same discovery that
drove ADR-0075's static rescue), and `0x0D` because that is the wire path.

**So the mode switch is delivered over exactly the endpoint that is broken when
a board is wedged.** A board whose foreground has stopped never drains that
endpoint. `OpenAsync` times out and throws, and the update ends there.

The consequence is circular, and it is the same circularity ADR-0075 named for
the rescue itself: **the updater's answer to a broken board is "flash it", and
flashing it requires a board that works.**

Meanwhile ADR-0060 had already built the machinery for "this device will not do
what it is told": a policy that chooses `retry | reset | give-up`, a ladder of
reset strategies the device advertises, a pure admissibility check, and a safety
gate. ADR-0075 added the rung that reaches a wedged board — `SoftProtocolOutOfBand`,
the EP0 vendor rescue, serviced by the board's USB ISR rather than its dead
foreground.

Every piece needed already existed. Nothing was wired to the updater, because
the seam had exactly one consumer: `DeviceProxyBase`, for long-lived sessions.

## Decision

### DEC-001: The orchestrator drives the existing recovery seam — it does not grow its own retry logic

`BootloaderEntryOrchestrator` gains a loop around the enter-and-wait step. On
failure it builds a `RecoveryContext`, asks an `IRecoveryPolicy`, validates the
answer with `ResetEscalation.Decide`, consults `IResetSafetyGate`, executes the
rung through `IDeviceReset`, waits for the device to return, and retries.

Those are ADR-0060's types, used unchanged. The alternative — a private
retry/backoff inside the orchestrator — was rejected because it would be a
second, divergent answer to a question the codebase has already answered once,
and because a consumer who has written a policy for their proxy should be able to
hand the same object to their updater.

**A second consumer is the point, not a complication.** "The device will not do
what it is told" is one problem; it should have one seam.

### DEC-002: A new `RecoveryTrigger`, so a policy can tell this fault apart

`RecoveryTrigger.BootloaderEntryFailure` joins `OpenFailure` and
`EnumeratedFault`.

It matters because **a plain retry is close to worthless here** and a policy
should be able to know that. For an open-failure, waiting is often right — the
device may be settling. For a failed mode switch, the dominant cause is a wedged
data path, and re-sending the same command down the same wedged endpoint fails
identically however long you wait. A policy that cannot distinguish the two will
spend its whole attempt budget on retries that could not have worked.

### DEC-003: `EscalatingResetRecoveryPolicy` — a policy that escalates rather than waits

`ExponentialBackoffRecoveryPolicy`, the existing default, **never returns
`Reset`**. No amount of tuning gets escalation out of it, so bootloader-entry
recovery needed a policy that walks the ladder: *n* sanity retries (default 1,
ADR-0060 Decision 3's "rule out a blip"), then one advertised rung per attempt
gentlest-first, then give up.

It lives in core next to the backoff policy rather than in `Periphery.Bootloader`,
because "escalate through the ladder" is not specific to firmware updates; a
proxy facing a recognized wedge wants the same curve.

Pure and total per ADR-0052: an out-of-range attempt or an empty ladder yields
`GiveUp`, never an exception — it is called from a loop the shell owns, where a
throw would surface as a device fault rather than a recovery decision.

### DEC-004: Off by default at the seam, on by default for Treehopper

`BootloaderEntryOptions.Recovery` defaults to `null`, and with it null the
orchestrator's behaviour is unchanged in every path — including that an
`EnterAsync` exception propagates unwrapped rather than being retried or
re-typed. A reset is a device-disrupting side effect and the shared spine should
not start producing them for callers who never asked.

`TreehopperFlasher.CreateService` then opts **in** (`recoverWedgedBoards: true`),
because for this device the failure is known, terminal without recovery, and
exactly what ADR-0075's rung was built for. Callers can opt out.

The composition does **not** override a `Recovery` the caller already supplied.
This is deliberately different from `Correlation`, which that composition *does*
own: a wrong correlation mis-flashes the wrong board, so it is a safety property;
recovery is a policy choice about disturbing hardware, and a caller who expressed
one meant it.

### DEC-005: A refusing safety gate aborts the update — it does not defer

`DeviceProxyBase` responds to `CanResetAsync == false` by backing off and
re-deciding, which is right for a long-lived session that will get another
chance.

An update is a **bounded operation an operator started**. Waiting out a refusal
would either hang the run or, worse, fire the reset the instant the gate blinked
open — mid-sale on a kiosk board is precisely the case the gate exists to
prevent. So a refusal fails the update with a reason that names the gate and
tells the operator to re-run when the device is idle.

### DEC-006: Re-acquire the device snapshot after a re-enumerating reset — but only against an invariant identity

`DeviceInfo` is a snapshot, and a reset that re-enumerates invalidates it — on
Windows the instance id can even change **case** across re-enumeration
(periphery `#231`). Recovery therefore **resets, settles, then looks** for the
device again, and retries the entry against the **refreshed** snapshot.

The filter is derived from the device's own USB id rather than taken from
`BootloaderEntryOptions.ApplicationFilter`, which is optional and exists for a
different purpose (the post-flash liveness check) — recovery must not depend on
the caller having configured something unrelated.

**But VID/PID is not an identity**, and this decision is about which physical
board the retry — and therefore the flash — is aimed at. `FlashAnythingService`
flashes concurrently by default, so a sibling board of the same model
re-enumerating inside our window is ordinary rather than exotic, and adopting it
would point the flash at the wrong hardware — exactly the correlation collapse
`#220` already cost this codebase once.

**Identity lives in the filter, and the identity is the physical USB port AND the
serial** — a conjunction, never a choice between them. The wait is armed against
a filter admitting only this board, so nothing else is ever surfaced to adopt.

Each half covers the other's hole, and review found both holes one at a time:

| | covers | fails alone |
|---|---|---|
| **Port** | a same-serial board elsewhere on the bus | identifies a *slot, not its occupant* — a board swapped into that port during the window satisfies it |
| **Serial** | exactly that replacement | not unique — many families ship one hardcoded across every unit |

The intermediate revisions each took one half and tried to shore it up. Serial
alone was guarded by a pre-reset uniqueness check, which **cannot hold**: it
proves uniqueness only among devices present *before* the reset, and once our
board is off the bus a same-serial sibling becomes the only match. Uniqueness
across a window in which the reference device is absent is not something a
snapshot can establish. Port alone was then adopted for being unique by
construction — and is, of slots. Conjoining them costs nothing and is strictly
stronger than either.

**What remains indistinguishable**, stated rather than hidden: an identical board
carrying an identical serial, physically swapped onto the same port inside the
window. No invariant available at this layer separates that from the original —
and the same is true of the `ByLocationPath` correlation ADR-0063 already ships
for the bootloader itself, so this is a property of the codebase's correlation
model rather than something recovery introduces.

**Both are required, or there is no identity.** A device exposing only one of
them does not get the weaker half as a fallback. That shape — take one invariant,
shore it up, ship it — was two successive revisions and two successive review
findings, because neither half alone proves *sameness*; each proves something
adjacent to it ("a board with this serial exists", "a compatible board is in this
slot"). Where identity is unavailable recovery simply does not refresh: it still
resets, settles, and retries — which is what actually recovers the board, as the
hardware runs show — and holding the stale snapshot is safe, because a stale id
fails to open rather than resolving to a *different* board. **Losing the refresh
is an optimisation; adopting the wrong board flashes it.**

The sequence is **reset → settle → look**, not a correlation on a transition.
Two earlier revisions keyed on the transition and both failed, which is worth
recording because both looked right:

- `DeviceWaitState`'s `BySerial` / `ByLocationPath` *correlate immediately* on an
  already-present match — and our board **is** present when the wait arms, since
  it has not been reset yet. So they returned the pre-reset snapshot without ever
  waiting, silently defeating the refresh. Fakes with no devices present hid it.
- `FirstAppearance` + `debouncePreExisting` needs the removal event to clear the
  baseline, but the source filters removals through the same `DeviceFilter` and a
  removal's `DeviceInfo` does not carry `LocationPath` — so `Disappeared` never
  fired for an identity filter and the board's return was debounced away. **That
  one cost a 117 s hardware failure on a path the shipped code did in 39 s**, and
  no unit test caught it.

Settle-then-look depends on neither. With an identity-pinned filter, any match is
our board whether it is the post-reset instance or (if the settle was short) the
pre-reset one, and holding either is safe.

Where neither a port nor a serial is available, recovery **does not wait and does
not adopt**: it resets, settles, and retries against the snapshot it already
holds. Keeping the stale snapshot is safe in a way that adopting an uncorrelated
appearance is not — a stale id fails to open, it never resolves to a *different*
board.

An earlier revision of this change did have the `FirstAppearance` fallback, and
review caught it. It is called out here because the mistake is an easy one: the
fallback looks like robustness (always find *something*) when it is in fact the
removal of the safety property.

### DEC-007: A host-side fault must never disrupt hardware, so only a tagged entry failure is recoverable

Recovery acts on `IBootloaderEntry.EnterAsync` failing, and on that alone. The
entry call is wrapped so its exception carries a private marker; the recovery
catch matches only that marker.

The alternative — catching everything thrown inside the enter-and-wait step — is
what the first revision did, and it swept in failures that say nothing about the
device: a disposed `IProgress` consumer throwing from `Report`, a watcher that
could not be constructed. Answering a UI bug by **resetting a payment-kiosk
board** is not a recovery, and the blast radius is not proportional to the fault.

### DEC-008: A safety gate is composed, never replaced

Where configuration supplies more than one `IResetSafetyGate` — a
`BootloaderEntryRecovery` carrying its own, plus one passed to the composition —
they are combined with `ResetSafetyGate.All`, so **both** must permit the reset.

A gate is a veto. Letting one shadow the other means a caller who explicitly
passed a refusal gets resets anyway while believing they are protected, which is
strictly worse than having no gate at all: it is a silent downgrade of a safety
property rather than a visible absence of one.

### DEC-009: `ResetOutcome.Issued` is not treated as proof, and its absence is not fatal

Per ADR-0073 and ADR-0075, the out-of-band rung **cannot be confirmed from the
transfer** — a device resetting and one that never implemented the request fault
identically. So recovery never reads `Issued` as success, and never treats
`Failed` / `NotSupported` as terminal. A rung that did nothing is a spent
attempt; the policy escalates on the next pass. The only thing that counts as
success is the device actually entering its bootloader.

## Consequences

**A wedged board becomes flashable again**, which is the whole point: the ladder
resets it out-of-band, it reboots into a healthy application, and the ordinary
`0x0D` entry then works.

**Every app-mode device family gets this**, not just Treehopper — the loop is in
the shared spine, so an STM32 entry gains the same behaviour the moment its
composition opts in and its `IDeviceReset` advertises rungs.

**An update can now take substantially longer before failing.** Worst case is
roughly `(sanityRetries + rungs + 1)` entry attempts, each bounded by
`BootloaderTimeout`, plus a return wait per rung. Front-ends should render the
new `BootloaderEntryPhase.Recovering`, which exists so escalation does not look
like a hang.

**An update can now reset hardware that was merely slow.** The sanity retry and
the gentlest-first ordering bound the harm, and the safety gate is the real
control — but a caller who cannot tolerate a reset should pass
`recoverWedgedBoards: false` rather than rely on the ladder being gentle.

**Verified against a real wedge on hardware.** This is the staged test ADR-0075
and `#226` both name as outstanding, and it now exists.

A throwaway firmware reproduces the endpoint-wedge signature exactly: after ~10 s
of healthy operation the foreground stops calling `Treehopper_Task` — so
`EP_PeripheralConfig` is never re-armed — while it *keeps feeding the watchdog*
and leaves the USB ISR running. The board stays enumerated and serves EP0. By
construction neither `#226` (still feeding) nor `#233` (SOF still flowing) can see
it; this is precisely the row the EP0 rescue owns. An escape counter reboots the
board after ~10 minutes so a failed test cannot strand hardware.

On board `IMNUZ6YW`, wedged, escape ~10 minutes away:

| | result |
|---|---|
| shipped `main` flasher (rung exists, updater cannot reach it) | **FAILED at 16.0 s**, twice, in the same wedge state |
| orchestrator with recovery | **reached the bootloader in 19.3 s** |
| full `treehopper-flash` CLI with recovery | **flashed, exit 0, 39.4 s** |
| full CLI, after the DEC-006/007/008 review fixes | **flashed, exit 0, 40.8 s** |
| full CLI, port-only identity | **flashed, exit 0, 40.3 s** |
| full CLI, port AND serial | **flashed, exit 0, 40.4 s** |
| full CLI, final (both required, else no identity) | **flashed, exit 0, 40.2 s** |

Re-run after every revision of DEC-006, and that mattered: the first attempt at
the identity fix **regressed the hardware path to a 117 s failure** while the
whole unit suite stayed green. The rung trace with the final code is unchanged:

```
[  18.8s] SoftProtocol          -> Failed
[  24.6s] SoftProtocolOutOfBand -> Issued
[  29.3s] REACHED BOOTLOADER
```

The recovery trace is the attributable part — the rungs were recorded as they
executed on hardware:

```
[  14.8s] SoftProtocol          -> Failed     <- 0x0C needs the wedged endpoint
[  18.2s] SoftProtocolOutOfBand -> Issued     <- EP0, serviced by the USB ISR
[  19.3s] REACHED BOOTLOADER
```

Two things this establishes beyond the fakes. **The premise holds**: EP0 really
does stay reachable when the foreground is dead — 1.1 s after the rescue the
board was healthy enough to accept `0x0D`. And **the ordering is not cosmetic**:
the gentle rung was tried first and genuinely could not work, which is the
concrete failure ADR-0075 predicted from where `0x0C` travels.

An earlier run of this test was **discarded as confounded** and is recorded here
so the result is not over-read: with the escape counter at ~18 s it fired during
the run, so the board recovered on its own while `SoftProtocol` reported
`Failed` — a success that recovery had not caused. Widening the escape to ~10
minutes removed the confound. A wedge-recovery test whose induced fault
self-heals on the same timescale as the recovery proves nothing.

**Still not verified:** a wedge arising naturally in the field rather than
induced, and the `UsbPortCycle` / `PnpDisableEnable` rungs, which this fault
never needed.

## Alternatives considered

**Retry inside `TreehopperBootloaderEntry`.** Rejected: it buries a recovery
policy inside a device shim where no consumer can see, replace, or gate it, and
every future device family would need its own copy.

**Have the updater open a `DeviceProxy` and lean on its recovery.** Rejected:
the proxy is built around a long-lived session with reopen semantics, and an
update is a one-shot operation that ends by deliberately making the device
disappear. Borrowing the seam's *values* is the reuse that fits; borrowing its
*lifecycle* is not.

**Always reset before entering, unconditionally.** Rejected: it disturbs every
healthy board in a fleet update to help the rare wedged one, and it would make
the common path slower and more dangerous to serve the uncommon one.
