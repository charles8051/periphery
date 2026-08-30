# Treehopper EFM8 SPI / USB lock-up — survey and fix plan

Status: exploration / pre-decision survey. Nothing implemented. Goal: characterize
the long-standing Treehopper lock-up (the board wedges and stays dark on USB until
it is physically re-plugged) and lay out candidate fixes, now that we can build
([`../../src/Periphery.Treehopper/firmware/Treehopper-EFM8`](../../src/Periphery.Treehopper/firmware/Treehopper-EFM8))
and flash (`Periphery.Efm8Bootloader` + `TreehopperFirmwareUpdate`) the firmware
end to end from our own stack.

All `path:line` citations are into
`src/Periphery.Treehopper/firmware/Treehopper-EFM8/` unless noted.

## Symptom (working hypothesis from the field)

A USB packet arriving while an SPI transaction is mid-flight wedges the EFM8: the
board stops responding on USB and only a power-cycle / re-plug recovers it. The
firmware has no watchdog, so a foreground hang is a hard brick rather than a
self-recovering reset.

## Execution model (what the firmware actually does)

Single-threaded superloop with **one hot interrupt (USB)**:

- `main()` runs `while(1) Treehopper_Task()` (`src/main.c:46`). `Treehopper_Task`
  polls the three endpoints and dispatches commands (`src/treehopper.c:85`).
- **USB is the only hot ISR**: `SI_INTERRUPT(usbIrqHandler, USB0_IRQn)`
  (`lib/efm8_usb/src/efm8_usbdint.c:62`). UART0, PCA0, SMB0, Timer3, Timer4 ISRs
  are also enabled (`src/InitDevice.c:747-791`).
- **SPI is fully polled in the foreground.** `ProcessPeripheralConfigPacket`
  (SPITransaction case, `src/treehopper.c:176`) -> `SPI_Transaction`
  (`src/spi.c:26`) -> `SPI0_pollTransfer` (`lib/efm8ub1/.../spi_0.c:164`). That
  routine calls `SPI0_disableInt()` and tight-loops on the FIFO flags. SPI's own
  interrupt is left disabled (`IE_ESPI0__DISABLED`, `src/InitDevice.c:789`).
- **No watchdog.** It is fed-then-disabled at boot
  (`WDT_0_enter_DefaultMode_from_RESET`, `src/InitDevice.c:61`). A foreground hang
  is therefore permanent until re-plug.
- **No interrupt nesting** by default (all priorities low / unset,
  `src/InitDevice.c:794-799`), so the USB ISR cannot itself be preempted.

The lock-up surface is therefore precise: **the foreground sits in a polled SPI
loop at `SFRPAGE=0x20`, and the USB ISR can fire on any byte boundary** — exactly
the "USB packet arrives mid-SPI-write" window.

## Candidate mechanisms (ranked)

### 1. `SFRPAGE` desync between the polled SPI loop and the USB ISR — most likely

`SPI0_pollTransfer` runs at page `0x20` and busy-waits on `SPI0CFG` /
`SPI0CN0_TXNF` with **no timeout** (`spi_0.c:183`, `spi_0.c:217-281`). It relies on
the EFM8's single-level automatic `SFRPAGE` save to survive a USB preemption. If
that save is ever defeated — a page left wrong on an ISR path, or a second enabled
interrupt (UART / SOF / PCA) landing in a bad window — the foreground resumes
reading the SPI flags from the wrong page, the flag never matches, and it spins
forever. This matches the symptom shape directly.

Confirmable by instrumenting `SPI0_pollTransfer` to trip a debug pin / capture
`SFRPAGE` if a busy-wait exceeds an iteration cap.

### 2. Genuine FIFO stall + a latent wrong-page register write

The polled loop assumes TXNF / RXE advance monotonically; a misconfiguration could
leave it waiting on a byte that never arrives. Related real bug found in passing:
`src/spi.c:28-29` writes `SPI0CKR` while `SFRPAGE=0x00`, but `SPI0CKR` lives on
page `0x20`. So the **host's per-transaction clock rate is written to the wrong
SFR and silently ignored** — the SPI runs at the boot default rate. Worth fixing
regardless; may also contribute to a stall on certain clock/CS modes.

### 3. Foreground-blocks-on-USB spins — amplifier, not the infinite hang

The burst path has bounded `while(timeout++ < 65000 && USBD_EpIsBusy(...))` spins
(`src/treehopper.c:191, 201, 219, 228`). Bounded, so not the permanent hang, but
the same "foreground blocks on USB" anti-pattern, and covered by a watchdog.

## Candidate fixes (prevention vs. recovery)

| Approach | Class | Verdict |
|---|---|---|
| **Watchdog** | Recovery | Do regardless. Converts *any* hang (known or not) into an auto-reset + re-enumerate instead of a brick. Cheap, low risk. Cost: a USB glitch + one lost transaction per trip; must size the WDT window against the longest legit foreground path (255-byte burst + the two 65000-spins) and feed mid-burst if needed. |
| **USB-mask the SPI critical section** | Prevention | Test first. Mask `EIE2 EUSB0` (or `IE_EA=0`) around the `SPI0_pollTransfer` FIFO loop. The host is synchronously blocked on the SPI result anyway, so delaying USB a few hundred us is free. If this makes the hang vanish, it confirms mechanism 1 and is likely the minimal real fix. |
| **Fix the `SPI0CKR` wrong-page write** | Correctness | Independent bug (mechanism 2); fold in. |
| **Interrupt-driven SPI** | Prevention | Plan B. `SPI0_transfer` / `SPI0_ISR` already exist in the driver, just uncompiled (`EFM8PDL_SPI0_USE_BUFFER`). Heaviest change; and if the root cause is `SFRPAGE` (mechanism 1), adding a second SFRPAGE-touching ISR could move or worsen it. Only if USB-masking can't hold throughput or the hang survives. |

## Plan

We can flash freely and recover any brick via the bootloader, so this is an
instrument-reproduce-fix loop, not guess-and-ship:

1. **Reproduce + instrument.** Build-flag'd debug path (the firmware already has
   `ENABLE_TIMING_DEBUGGING` -> debug GPIO P1_B2 / Pin 10, `inc/treehopper.h:24`,
   and a UART debug channel) that (a) bounds the `SPI0_pollTransfer` busy-waits
   with an iteration cap and trips the pin / captures `SFRPAGE` on timeout, and
   (b) a host-side stress test hammering SPI transactions while spamming other USB
   traffic until it wedges. Confirm which loop hangs and what `SFRPAGE` reads.
2. **Land the watchdog unconditionally** — re-enable WDT, feed once per superloop
   pass; verify the longest legit foreground path fits the WDT window (feed
   mid-burst if not); confirm an injected hang self-recovers.
3. **Apply the targeted fix** the repro points to — most likely USB-mask the SPI
   critical section + fix the `SPI0CKR` page. Re-run the stress test.
4. **Escalate to interrupt-driven SPI only if** masking can't hold throughput or
   the hang survives.
5. **Verify** — extended soak clean; watchdog still recovers an injected fault.

## Related

| Type | Link |
|------|------|
| Reflash pipeline (build -> flash) | [`treehopper-firmware-update.md`](treehopper-firmware-update.md) |
| Reflash feature | [`../feature-specs/treehopper/firmware-reflash/spec.md`](../feature-specs/treehopper/firmware-reflash/spec.md) |
| Functional-core / shell ADR | [`../adr/0052-periphery-treehopper-pure-core.md`](../adr/0052-periphery-treehopper-pure-core.md) |
| Firmware source | [`../../src/Periphery.Treehopper/firmware/Treehopper-EFM8`](../../src/Periphery.Treehopper/firmware/Treehopper-EFM8) |
