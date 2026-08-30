# Investigation: Treehopper EFM8 SPI/USB "lock-up" — timing race, not silicon

Status: **mechanism identified + fix validated on hardware** (bounded production
form of the fix still pending). Date: 2026-06-17.

> **Update (2026-08-07, periphery `#226`): the hard-wedge escalation path below is gone.**
> This investigation was written against firmware that entered USB power-save on suspend
> and disabled the watchdog at boot. `#226` changed both. `SLAB_USB_PWRSAVE_MODE` is now
> `USB_PWRSAVE_MODE_OFF` (see `Treehopper-EFM8/inc/config/usbconfig.h`), so the board no
> longer suspends and cannot reach the stuck-in-USB-suspend state the findings below
> describe; and the watchdog is enabled with a superloop feed, so a foreground hang now
> self-recovers in ~8 s (bench-measured, 3/3) instead of needing a C2 reset or a physical
> replug. **The recoverable in-band stall, the ~4–5 MHz resonance band, and the [#93](https://github.com/charles8051/periphery/issues/93)
> fix are all unaffected** — only the escalation to a hard wedge, and its recovery
> procedure, are obsolete. Findings 1 and the "Escalation + recovery" bullet are kept as
> written because they were accurate for the firmware measured here.

## TL;DR

The long-standing Treehopper "the board wedges under load" fault is **not a
silicon defect** (it is **not in the EFM8UB1 errata**). It is a **firmware
timing race** in the polled SPI transfer routine: a USB interrupt firing while
the SPI FIFO is mid-transfer lets the RX FIFO overflow, dropping received bytes,
which desynchronises the poll loop's byte counters so it spins forever.

The race is confined to a **narrow SPI-clock band (~4–5 MHz)** and scales with
**transfer length**. The original Treehopper SDK saw the symptom, assumed
silicon, and worked around it by **banning the entire 0.8–6 MHz clock band**
(host-side, `SpiClockByte`) — far broader than the real danger zone. That
mitigation is correct and still in force; this investigation explains *why* it
works and opens the door to a targeted firmware fix.

## How we got a reliable repro (two stacked guards had to come down)

The clock could not be driven into the danger band because of **two** independent
guards:

1. **Firmware ([#93](https://github.com/charles8051/periphery/issues/93))** — `SPI_Transaction` wrote `SPI0CKR` on the wrong SFR page
   (0x00 instead of 0x20), so the host's per-transfer clock value was silently
   ignored and SPI always ran at the ~6.25 MHz boot default. Fixed: write
   `SPI0CKR` on page 0x20, then restore page 0x00.
2. **Host (`SpiClockByte`, ADR-0039)** — deliberately rounds any request in
   `0.8 < f < 6.0` MHz up to 6 MHz. This is the actual ported mitigation. Bypassed
   for the experiment via an **opt-in env var** `TREEHOPPER_SPI_DANGER_BAND=1`
   (production guard intact by default).

With both down, a requested 4 MHz produced a measured **~3.91 MHz** SCK on the
Saleae, and the fault reproduced immediately.

## Tooling built for this investigation

- **Saleae Logic Pro 8 over MCP** (`logic2` server): capture SCK/MOSI/CS, decode
  SPI, measure the real clock. Confirmed [#93](https://github.com/charles8051/periphery/issues/93) on the wire (1 MHz requested →
  6.25 MHz actual) and the true 4 MHz after the fixes.
- **C2 on-chip debug via J-Link** (Silicon Labs J-Link OB; *not* the GDB server,
  which is JTAG/ARM-only). Head-less command scripts (`scratch/jlink/`): halt,
  read PC + SFRPAGE + memory, decode PC → function via the Keil map
  (`scratch/jlink/jdump.py`). A C2 **reset** (`r`) reliably recovers a wedged
  board — this is what made the automated sweep possible.
- **Stress harnesses**: `Periphery.Examples.TreehopperSpiStress` (SPI flood + USB
  noise + report drain, wedge-detect via per-transfer timeout); `scratch/SpiEmit`
  (deterministic emitter); `scratch/UartEmit` (UART analogue).

## Findings

### 1. Reproduction is deterministic in-band

At true ~4 MHz with concurrent USB load, SPI transactions fail within **1–4
transfers** (often transfer #0). Recoverable: the board returns to the
application afterward; repeated rapid triggering can **escalate** it into a
stuck-in-USB-suspend hard wedge, from which a **C2 reset** recovers it.

### 2. It is a narrow resonance band, not "everything below 6 MHz"

Sweep at 200-byte bursts (USB noise on):

| SPI clock | result |
|---|---|
| 1 MHz | clean |
| 2 MHz | clean |
| 4 MHz | **wedged** |
| 5 MHz | **wedged** (transfer #1) |
| 5.9 MHz | clean |
| 6.25 MHz | clean (5 prior multi-minute soaks) |

Clean *below* (1–2 MHz) **and** *above* (5.9–6.25 MHz) the failing band. A
monotonic "the FIFO can't keep up at low clocks" defect cannot look like this — a
**resonance** can.

### 3. It scales with transfer length

Sweep at 4 MHz:

| burst | result |
|---|---|
| 8 B | clean (12 444 transfers) |
| 32 B | wedged (after ~298) |
| 64 B | wedged (after ~693) |
| 128 B | wedged (after ~1105) |
| 200 B | wedged (transfer #0) |

Small transfers are safe; failure probability rises sharply with bytes clocked.
Consistent with a per-byte timing coincidence, not a static hardware limit. (The
64-byte USB-packet / 57-payload multi-packet boundary is likely also in play.)

### 4. The Heisenbug (the decisive evidence)

Instrumenting `SPI0_pollTransfer` with a per-iteration counter (to snapshot the
FIFO SFRs on a stall) **eliminated the wedge**: the instrumented build ran **2219
transfers clean** at 4 MHz / 200 B, and the snapshot never even latched — the loop
stopped stalling. The snapshot could **not** be captured, because *observing it
removes the fault*.

A/B control, same source minus the instrumentation, same setup:

| build | 4 MHz / 200 B |
|---|---|
| instrumented (per-iteration counter in poll loop) | **2219 transfers, clean** |
| reverted (instrumentation stripped) | **wedged on transfer #0** |

Flipped deterministically, both directions. The entire delta is **~3 instructions
per loop iteration**. **A silicon FIFO defect cannot be cured by adding
instructions to a host CPU loop.** This is conclusive.

## Mechanism

The polled transfer is a producer/consumer over the EFM8's shallow (4-deep) SPI
FIFO:

```c
while (xferCount) {
    if (SPI0CN0_TXNF && txCount)        { SPI0DAT = *pTx++;  --txCount;  }  // feed TX
    if (!(SPI0CFG & RXE) && xferCount)  { *pRx++ = SPI0DAT;  --xferCount; }  // drain RX
}
```

The interrupt mechanism restores **CPU** state (ACC/PSW/DPTR/`SFRPAGE` via the
auto-page stack) correctly — so this is *not* corrupted register state. But the
**SPI peripheral is a real-time actor that keeps clocking during the ISR**:

1. A USB ISR fires mid-loop (SOF every 1 ms + EP traffic; guaranteed inside a
   ~400 µs transfer).
2. While the foreground is in the ISR, queued TX-FIFO bytes keep clocking and the
   received bytes pile into the 4-deep **RX FIFO**.
3. If the RX FIFO already held bytes when the ISR hit, the in-flight bytes
   **overflow it → RX bytes are silently dropped** (`RXOVRN`).
4. `txCount` reaches 0 (all bytes clocked, partly during the ISR) but `xferCount`
   stays > 0 (some RX bytes lost). The loop now spins forever: nothing left to
   send, nothing left to receive, but `xferCount != 0`. **Hard hang.**

State save/restore is **necessary but not sufficient**: it handles preemption of
*computation*, not preemption of a *real-time data stream through a shallow FIFO*.
That is why it masquerades as a hardware ("silicon") fault.

This explains the evidence: length-dependence (more bytes → more ISR windows →
more overflow chances), USB-dependence (no traffic → no ISRs → no overflow), and
the Heisenbug (loop timing changes how full the FIFO runs when an ISR lands).

**Open question:** the *exact* band shape (~4–5 MHz failing, clean again at
6.25 MHz, non-monotonic) depends on FIFO-occupancy dynamics (T_byte vs loop period
vs ISR duration) that we have not derived from first principles. Not required to
conclude "timing race, not silicon," but unexplained.

## Fix — VALIDATED on hardware

Mask interrupts (`IE_EA = 0` / `IE_EA = 1`) around the FIFO-service critical
section in `SPI0_pollTransfer`. The existing `SPI0_disableInt()` masks only the
**SPI** interrupt — the USB interrupt, the actual culprit, is left enabled; that
is the hole.

Result, 4 MHz / 200 B danger band:

| build | result |
|---|---|
| control (no mask, `OPTIMIZE SIZE`) | **wedged in ≤9 transfers** |
| masked (`IE_EA` around the loop) | **12 125 transfers in 40 s, clean** |

And **data is correct** — Saleae decode of a known pattern at 4 MHz =
**2376/2376 bytes byte-perfect**, no corruption or drops. So the mask both fixes
the hang and confirms the mechanism (preventing ISR preemption prevents the
overflow). The `OPTIMIZE SIZE`-without-mask control still wedged, ruling out a
timing-detune artefact.

**Still required before shipping — the bounded form.** The current fix masks the
**whole** transfer. That is fine for short transfers (~400 µs at 4 MHz / 200 B)
but a multi-millisecond one (255 B at the low clock end) would mask long enough to
trip USB suspend (3 ms idle) or a host transaction timeout. The production version
must **bound the masked window**: chunk the transfer and reopen the interrupt
window between chunks (≤ a few hundred µs each), or mask only while bytes are
actually in flight. Also note the **flash headroom** constraint ([#100](https://github.com/charles8051/periphery/issues/100)): the masked
image needs `OPTIMIZE(SIZE)` to fit under the 0x3A00 bootloader wall.

This supersedes — and is far narrower than — the host's blunt 0.8–6 MHz clock ban:
with the firmware made preemption-safe, the danger band can in principle be
reopened.

## Fix options compared

All three make the FIFO service robust to USB preemption; they differ in USB
latency, complexity, and flash cost.

| Option | How | USB latency | Complexity | Flash | Status |
|---|---|---|---|---|---|
| **A. Whole-transfer mask** | `IE_EA=0/=1` around the poll loop | whole transfer (≤ ms at low clock — *too long*) | trivial | +~6 B | validated, but not shippable as-is |
| **B. Bounded/chunked mask** | mask `SPI_MASK_CHUNK` bytes, reopen the window, repeat | ≤ one chunk (~64 µs at 4 MHz) | low | +~50 B | **VALIDATED + shipped** (the chosen fix) |
| **C. Interrupt-driven SPI** | enable `SPI0_transfer`/`SPI0_ISR`; SPI ISR drains the FIFO | only brief SPI-ISR preemptions | high | **+~400–600 B** (re-adds the LX51-stripped path) | exploration only |

### B (bounded mask) — implemented and validated

`SPI0_pollTransfer` now services the transfer in `SPI_MASK_CHUNK` (=32)-byte
windows with `IE_EA=0`, reopening interrupts between chunks. **Design crux:** cap
TX feeding at the chunk size (`chunkTx`) so the SPI is *idle* at each unmask
boundary — nothing is in flight, so no drain is needed and the reopened window is
safe.

A wrong first cut is instructive: it ran the existing pipelined loop (TX free to
race ahead) and then a `while(SPIBSY)` "drain to idle" before unmasking. That drain
clocked the in-flight TX bytes out **without reading their RX**, overflowing the RX
FIFO *during the drain itself* — reproducing the very byte-loss/desync it was meant
to prevent, and hanging on transfer #0. The lesson: a drain must keep reading RX,
or (simpler) never let TX lead the chunk.

**Adaptive chunk (final form).** The chunk is sized from the live clock divider so
the masked window stays bounded across the whole SPI range — no minimum-frequency
floor needed (slow SPI is a real need: long/breadboard wiring, slow or isolated
slaves, bring-up). To avoid a 16-bit divide (which pulled ~140 B of C51 runtime and
blew the 0x39FF ceiling), it uses tiered comparisons that halve the chunk as the
divider doubles, keeping `chunk·(SPI0CKR+1) ≤ 768` (~256 µs at ~48 MHz sysclk):

| `SPI0CKR` | SPI clock | chunk |
|---|---|---|
| ≤23 | ≥1 MHz | 32 |
| 24–47 | 0.5–1 MHz | 16 |
| 48–95 | 0.25–0.5 MHz | 8 |
| 96–191 | ~125–250 kHz | 4 |
| 192–255 | ~94–125 kHz | 2 |

**Full-range verification** (200 B bursts, USB load, C2-reset per cell) — every
clock CLEAN:

| MHz | 0.094 | 0.5 | 1 | 2 | 4 | 5 | 6 | 8 | 12 |
|---|---|---|---|---|---|---|---|---|---|
| result | ✓ | ✓ | ✓ | ✓ | ✓ (was wedge) | ✓ (was wedge) | ✓ | ✓ | ✓ |

The danger band (4–5 MHz) is fixed, and the slow clocks (0.094/0.5 MHz — where a
fixed 32-byte chunk would mask ~2.7 ms and trip USB suspend) are clean, confirming
the adaptive window. Data integrity across chunk boundaries verified at 0.5 MHz
(chunk=16): 189/190 200-byte bursts byte-perfect on the Saleae (the one outlier was
capture truncation at stop). Fits flash (top 0x39E6, 25 B headroom).

**Consequence:** the firmware is now preemption-safe across the entire 94 kHz–12 MHz
range, so the host's 0.8–6 MHz `SpiClockByte` clamp can be retired (left in place as
a belt-and-suspenders default; the env-var bypass remains for testing).

### Why interrupt-driven SPI (C) is not a drop-in

The interrupt-driven path already exists in the driver (`EFM8PDL_SPI0_USE_BUFFER=1`)
but is link-stripped because nothing calls it. Wiring it in needs: a foreground
restructure of `SPI_Transaction` (kick off `SPI0_transfer`, wait on a
completion flag), definitions for the (currently undefined) `SPI0_transferCompleteCb`
/ `SPI0_transferErrorCb`, FIFO request thresholds, and `IE_ESPI0 = 1`.

**The load-bearing requirement:** today *every* interrupt is at default priority
(level 0 — `IP`/`EIP1`/`EIP2` are all unset in `InitDevice`). At equal priority a
running USB ISR cannot be preempted, so an SPI FIFO-request raised mid-USB-ISR
still waits → the RX FIFO can still overflow. So enabling interrupt-driven SPI
**only fixes the bug if the SPI0 interrupt is elevated above USB0** (`EIP`/`IP`),
so SPI servicing preempts the USB ISR. Get that wrong and it does nothing.

Trade-off: (C) gives the best USB latency (USB is only nudged by short SPI ISRs,
not blocked for the whole transfer), but it is the most invasive, requires the
priority elevation to work at all, and **costs ~400–600 B of flash we do not have**
(119 B headroom; the 0x39FF ceiling is a hard bootloader limit, [#100](https://github.com/charles8051/periphery/issues/100)). It would
have to be paid for by gating out an optional feature (`PARALLEL` ~664 B, one-wire
~400 B, or `SOFTPWM` ~293 B — a product decision).

### Flash-efficiency sweep (for the record)

- LX51 already performs dead-code elimination — uncalled functions (including the
  entire unused interrupt-driven SPI path) are already stripped. No free reclaim.
- Optimization is already `OPTIMIZE(9,SIZE)`; SPEED costs +40 B; level 9 is the max.
- The only lever for material headroom is **conditionally compiling out optional
  features** (`PARALLEL`/one-wire/`SOFTPWM`, ~1.3 KB combined) — functional change,
  not a free optimization.
- The 0x39FF app-flash ceiling is a real bootloader reservation ([#100](https://github.com/charles8051/periphery/issues/100)), not a map
  bug we can reclaim past.

**Recommendation:** ship **B (bounded mask)** — it is the minimal, low-risk change
that fixes the race with acceptable USB latency and negligible flash. Keep **C**
on the table only if a future need for the danger band plus the best-possible USB
latency justifies the flash spend and the priority-tuning work.

## Side findings

- **[#100](https://github.com/charles8051/periphery/issues/100) — `Efm8BootOptions.Ub1` flash-map ceiling is wrong.** The map claims the
  app region is `0x0000–0x3DFF`, but this part's bootloader reserves
  `0x3A00–0x3FFF` (top 1.5 KB). An image reaching ≥ 0x3A00 fails to flash with a
  bootloader `RangeError` (0x41) on the erase (observed: 0x39AC fits, 0x3A28 fails
  at record 117). Masked until now because Treehopper firmware always fit under
  0x3A00.
- **Escalation + recovery.** Rapid repeated triggering pushes the board from a
  transient, recoverable stall into a stuck-in-USB-suspend hard wedge; a C2 reset
  recovers it deterministically.
- **[#93](https://github.com/charles8051/periphery/issues/93) is a real fix on its own** — before it, *every* per-transfer clock
  request was ignored; after it, clocks above 6 MHz (previously silently capped)
  actually work, while the host guard still blocks the danger band.

## What stays / what's debug-only

- **Keep:** [#93](https://github.com/charles8051/periphery/issues/93) `SPI0CKR`-page fix (`spi.c`). It is correct independently.
- **Debug-only:** the `TREEHOPPER_SPI_DANGER_BAND` env-var bypass in
  `TreehopperWire.SpiClockByte` (off by default; opt-in for repro). The host
  0.8–6 MHz clamp remains the shipped mitigation.
- The poll-loop instrumentation has been reverted to vendor-original.

## References

- Code: `firmware/Treehopper-EFM8/lib/efm8ub1/peripheralDrivers/src/spi_0.c`
  (`SPI0_pollTransfer`); `src/spi.c` (`SPI_Transaction`, [#93](https://github.com/charles8051/periphery/issues/93));
  `src/Periphery.Treehopper/Wire/TreehopperWire.cs` (`SpiClockByte`).
- Tooling: `scratch/jlink/` (C2 dump/decode/reset), `scratch/SpiEmit`,
  `scratch/UartEmit`, `examples/Periphery.Examples.TreehopperSpiStress`.
- Captures/logs: `spi-*.sal`, `thopper-*-stress.log` (kept locally, not committed).
- Prior survey: [`../explorations/treehopper-spi-usb-lockup.md`](../explorations/treehopper-spi-usb-lockup.md).
