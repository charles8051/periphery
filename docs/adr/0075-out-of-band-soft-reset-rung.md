---
title: "ADR-0075: SoftProtocolOutOfBand — a second soft reset rung for a wedged foreground"
status: "Accepted"
status_note: "Shipped - `ResetStrategy.SoftProtocolOutOfBand`."
date: "2026-08-07"
authors: "@charles8051"
tags: ["architecture", "decision", "device-reset", "recovery", "treehopper", "usb"]
supersedes: ""
superseded_by: ""
depends_on: "ADR-0060 (device reset + recovery escalation — this adds a rung to its ladder), ADR-0052 (pure-core pattern), ADR-0073 (observations, not verdicts)"
---

# ADR-0075: `SoftProtocolOutOfBand` — a second soft reset rung for a wedged foreground

## Status

**Accepted.** Shipped as `ResetStrategy.SoftProtocolOutOfBand`, wired into
`WindowsDeviceReset` and `BootloaderEntryRecovery`.

## Context

ADR-0060 built a fault-aware recovery ladder whose gentlest rung is
`ResetKind.SoftProtocol` — a device-specific reset issued over the device's own
transport, supplied by a device extension rather than by core. For a Treehopper
board, `TreehopperDeviceReset` implements that rung by opening the board and
issuing `RebootAsync` (wire opcode `0x0C`).

**That rung cannot clear the fault the ladder was built for.**

`0x0C` travels over `EP_PeripheralConfig`, a bulk endpoint the firmware re-arms
**only from its foreground superloop**. The field failure mode (periphery `#226`,
14 boards across 7 production kiosks) is precisely a stopped foreground: the
board keeps enumerating and answering descriptors from its USB ISR while that
endpoint stops being drained, and every bulk write times out at 2000 ms. So the
reboot command is delivered to the very endpoint that is wedged, and can never
arrive. `RebootVerb` has said so in its own docstring since it was written; the
same sentence is why `#227` exists.

ADR-0060's Context describes this class exactly — "present, but recoverable only
by a power-cycle" — and its Decision 2 explicitly anticipates the fix: device
extensions *may add* a `SoftProtocol` strategy their firmware supports. What it
did not anticipate is a device needing **two** of them.

periphery `#227` added the mechanism: `TreehopperBoard.RescueResetAsync` sends a
vendor control request on **EP0**, which the EFM8 USB stack services entirely
from its ISR (`USBD_SetupCmdCb`). EP0 therefore stays reachable in exactly the
state that kills the bulk path — the board still enumerating *is* the evidence
that the ISR is alive, and the ISR is what answers. It is bench-verified on
hardware with a negative control (a board carrying the handler re-enumerates; a
board without it does not).

It is currently reachable only from `scratch/Ep0Rescue`. The recovery seam — the
thing that is supposed to choose a reset when a device faults — cannot use it.

## Decision

### DEC-001 — The out-of-band reset is its own `ResetKind`, not a variant of `SoftProtocol`

Add `ResetKind.SoftProtocolOutOfBand`, ordered between `SoftProtocol` and
`UsbPortCycle`:

```csharp
public enum ResetKind
{
    SoftProtocol,            // device-specific reset over the normal transport
    SoftProtocolOutOfBand,   // device-specific reset over a channel that survives a wedged one
    UsbPortCycle,
    PnpDisableEnable,
}
```

`ResetKind` is documented as ascending force, and the ordering holds on that
axis: `0x0C` is a *cooperative* reboot the application firmware participates in;
the EP0 rescue resets the MCU from the ISR whether or not the application is
alive. More force, same `ResetBlastRadius.Self`, still no disturbance to the bus
or to sibling devices — so it belongs above the cooperative reset and below
anything that touches the hub.

But force is not the interesting property. **Reachability is**, and that is the
argument for a distinct value rather than an implementation detail: the two
rungs differ in *what has to still be working for the reset to be delivered at
all*, and that is a property a recovery policy must be able to reason about.

### DEC-002 — Rejected: escalate internally inside the extension

The cheaper option is to keep one `SoftProtocol` value and have
`TreehopperDeviceReset` try `0x0C`, then fall back to EP0 when it faults.

Rejected, because it puts the decision in the wrong place. `IRecoveryPolicy` is
the seam ADR-0060 created *specifically* so that recovery decisions are made by
a policy that sees the fault, rather than buried in the mechanism. A policy that
wants "try the cheap reset; if the fault looks like a wedge, skip straight to
the one that survives a wedge, and only then disturb the hub" can only express
that if the two are distinct values it can name.

This is enforced, not merely conventional: `ResetEscalation.Decide` concedes
unless the requested strategy is one the device advertised in
`RecoveryContext.AvailableResets`. A rung the extension never advertises is a
rung no policy can ask for. Hiding the EP0 reset inside the `SoftProtocol`
implementation would make it permanently unaddressable by the seam that is
supposed to govern recovery.

### DEC-003 — Rejected: a reachability axis on `ResetStrategy`

The third option was to keep three kinds and add a descriptive field —
`SurvivesWedgedTransport`, alongside `Radius` and `ReEnumerates`.

Rejected as the weaker model. `Radius` and `ReEnumerates` describe *consequences*
of a reset that are orthogonal to the mechanism (any kind can be `Self` or
`SharedHub`). Reachability is not orthogonal — it *is* the mechanism. A boolean
that only ever varies for one kind is a kind wearing a disguise, and it would
leave `strategy.Kind` an incomplete description of what a strategy does, which
is the property every switch over `Kind` currently relies on.

### DEC-004 — The outcome is `Issued`, never a confirmation

`RescueResetAsync` deliberately reports nothing. A device that resets mid-transfer
and a device whose firmware never implemented the request **fault identically**
(WinUSB Win32 error 31 in both cases), so the transfer result carries no
information. The rung therefore returns `ResetOutcome.Issued` — "the reset was
issued as requested", which is exactly and only what is known.

The consequence must be stated plainly rather than discovered: **on firmware
predating the handler this rung reports `Issued` and does nothing.** The
recovery loop then waits for a re-open that never comes and escalates to the
next rung on its own budget, which is the correct behaviour and the reason the
ladder exists. What we must not do is invent a `Failed` we cannot substantiate;
per ADR-0073, a verdict Periphery cannot observe is one Periphery does not emit.

Confirming a rescue is possible, but not from the transfer: observe
re-enumeration (an arrival timestamp, or a watcher event). Polling for absence
does not work — the board returns under the same instance id in ~230 ms and a
sampling loop misses the gap. `BoardReboot` (periphery `#232`) already models
exactly that observation as a pure fold, and the `rescue` verb reuses it.

### DEC-005 — `ReEnumerates: true`

A rescue reset is a full MCU reset, so the board drops off the bus and returns —
the same transition `0x0C` produces, measured at ~230 ms absent (`#232`). Per the
correction in ADR-0060, this is declared per strategy and not inferred from the
kind.

## Consequences

### What we gain

- The recovery ladder gains a rung that can actually clear a wedged foreground,
  which is the failure mode ADR-0060 was written for and the one its gentlest
  rung could not touch.
- The capability leaves `scratch/` and becomes addressable by policy, by the
  `periphery devices reset` CLI, and by a `treehopper-flash rescue` verb.
- Recovery on a wedged board no longer requires elevation. The rungs that could
  previously clear it (`PnpDisableEnable`, and a port cycle on a hub that
  supports it) need an elevated host; opening a USB device and sending a control
  request does not. That matters for an unattended kiosk.

### What we accept

- **This is not a remedy for boards already wedged in the field.** The rung is a
  no-op on firmware that predates the handler, and getting the handler onto a
  board requires flashing it, which requires a board that works. It rescues
  boards that wedge *after* the fleet is on firmware carrying it. Nobody should
  plan a field procedure around it before then.
- ~~**It has never been exercised against a real wedge.**~~ **CLOSED by ADR-0076.**
  The staged test this bullet asked for — induce a hang, then rescue — was run on
  board `IMNUZ6YW` against a firmware that stops servicing `EP_PeripheralConfig`
  while still feeding the watchdog and still running the USB ISR. The gentle rung
  failed exactly as this ADR predicted (`SoftProtocol -> Failed`, because `0x0C`
  needs the endpoint that is wedged) and `SoftProtocolOutOfBand -> Issued` left
  the board reachable **1.1 s later**, with the shipped no-recovery tool failing
  in the same wedge state as a control. **This rung's central premise — that EP0
  stays reachable when the foreground is dead — is now measured, not inferred.**
  See ADR-0076's consequences for the full trace and for the earlier run that was
  discarded as confounded.
- **A wedge with interrupts masked is out of reach.** If the MCU hangs with
  `IE_EA = 0`, the USB ISR never runs and EP0 is as dead as the bulk path. In
  today's firmware the only such window is the flash write in `serialNumber.c`,
  which is narrow. Note that the interrupt-mask fix designed in
  `docs/investigations/2026-06-treehopper-spi-usb-lockup.md` is **not** in the
  shipped tree — `SPI0_pollTransfer` is still the unmodified SDK routine and the
  live mitigation remains the host-side 0.8–6 MHz clock ban. If that fix lands
  as designed it introduces exactly such a window; bounded, so small, but it is
  the one place this rung goes blind and it should be designed with knowingly.
- Once `#226`'s watchdog is deployed, a stopped superloop self-recovers in ~8 s,
  so this rung's practical scope narrows to a foreground that still *loops* (and
  so keeps feeding) but has stopped draining the config endpoint — plus recovery
  on demand instead of after an 8 s outage on a live payment kiosk. That is a
  smaller population than `#226`'s 14 boards, and the ladder should not be
  credited with saves the watchdog made.

### What we constrain

- Core never implements this kind. `WindowsDeviceReset` answers `NotSupported`
  for it, exactly as it does for `SoftProtocol` — an out-of-band protocol reset
  is device knowledge by definition.
- Like `SoftProtocol`, this rung must run where the board physically is, so a
  device-reset decorator carrying it composes on the **outside** of any
  remote/privileged reset adapter.

## Related ADRs

- **ADR-0060** — device reset capability and recovery escalation. This adds a
  rung to its ladder and does not alter its decisions.
- **ADR-0073** — observations, not verdicts. DEC-004 is that principle applied
  to a reset outcome.
- **ADR-0052** — pure core. The rescue verb's detection reuses `BoardReboot`'s
  pure fold rather than growing a second detector.
