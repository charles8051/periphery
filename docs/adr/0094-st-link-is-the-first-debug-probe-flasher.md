---
title: "ADR-0094: The first debug-probe flasher drives an ST-Link over SWD and programs one STM32G4 part"
status: "Proposed"
status_note: "Drafted 2026-10-11 from the exploration browser-debug-probes-2026-10.md. Nothing is implemented. The ST-Link facts are Source (probe-rs, ST's driver INF); no ST-Link has been attached for this work. RM0440 was not re-read for this draft, so D4's registers and D5's sequence are to be checked against it. The package stays unpublished until the legal review in D8 is recorded."
date: "2026-10-11"
authors: "@charles8051"
tags: ["architecture", "decision", "firmware-flashing", "debug-probe", "st-link", "swd", "stm32", "browser", "webusb", "usb", "pure-core", "licensing"]
supersedes: ""
superseded_by: ""
depends_on: ["0024-extension-package-pattern.md", "0038-periphery-usb.md", "0052-periphery-treehopper-pure-core.md", "0061-firmware-flashing-platform.md", "0089-tests-do-not-depend-on-elapsed-time.md", "0092-browser-devices-come-from-grants.md"]
---

# ADR-0094: The first debug-probe flasher drives an ST-Link over SWD and programs one STM32G4 part

> Number `0094` is provisional until merge (the next free number after ADR-0093), per this repo's
> "assign the number at merge" convention.

**Tracks:** one new package, `Periphery.DebugProbe.StLink`. Revises the scope statement in
nordic-dfu spec fact 1. Answers the exploration's open question 6 for this slice.

---

## Status

Proposed 2026-10-11. Nothing is implemented. Stays Proposed until the gates under
[Verification](#verification) pass.

---

## Context

### What is already known

[`browser-debug-probes-2026-10.md`](../explorations/browser-debug-probes-2026-10.md) surveyed
ST-Link, J-Link and CMSIS-DAP as targets for a Periphery page. Its evidence labels (Measured,
Source, Documented, Inferred) are used unchanged here. It established:

- A page cannot run probe-rs, which has no browser build. A desktop app can run it as a separate
  process. (Source.)
- The probe protocol is the smallest layer of SWD flashing. The rest is debug-port logic, Cortex-M
  core control, and a flash driver per chip family. (Source.)
- ST-Link's debug function is interface 0 on every PID ST's driver lists, and ST's Windows driver
  binds WinUSB to it. `Periphery.Usb.Browser` claims interface 0 and no other
  ([ADR-0092](0092-browser-devices-come-from-grants.md)). (Source.)
- ST-Link firmware performs 32-bit target memory reads and writes itself. A host can program an
  STM32 without implementing ADIv5. (Source.)
- CMSIS-DAP v2 and the on-board J-Link need the browser backend to claim an interface other than 0.
  (Source, Measured.)

It ranked ST-Link with one STM32 family as the smallest end-to-end proof, and CMSIS-DAP v2 as the
general second step.

### The scope this revises

The firmware-flashing platform ([ADR-0061](0061-firmware-flashing-platform.md)) flashes through
bootloaders. The nordic-dfu spec states the exclusion outright: a blank nRF is programmed over SWD
with a debug probe, "which this platform does not do and does not intend to". ADR-0061 itself names
no probe.

### The consumer

No page or app in this repo programs a chip through a probe yet. Two cases want it:

- Programming an STM32 board from a page with no install and no BOOT0 jumper. Every Nucleo and
  Discovery board carries an ST-Link.
- Recovering a part whose bootloader is gone, which no bootloader flasher can reach. The nordic-dfu
  spec names this for nRF. This slice does not serve it. It builds the layers a later probe slice
  would reuse.

### What the transport already does

On 2026-10-11, in Chrome 155 on Windows, the ADR-0092 packages opened a granted Treehopper and a
granted STM32 DFU bootloader bound to WinUSB through an ST INF (ADR-0092, Verification). The same
`UsbDevice` path serves the desktop through `WinUsbBackend` and libusb
([ADR-0038](0038-periphery-usb.md)).

### What the flashing contract offers

`Periphery.Bootloader` defines `IFirmwareProgrammer` (identify, flash, leave) and
`IBootloaderProvider` (recognise a `DeviceInfo`, open a programmer). A provider declares an
`IdentificationMode`. `Passive` means the USB id is the target, and such a target is eligible for
unattended autoflash. `Probe` means the USB id names only an intermediary, and the target is never
auto-flashed.

---

## Decision

### D1 — Periphery programs flash over SWD through a debug probe, and does nothing else over SWD

The platform gains a second way to flash: through a debug probe, over SWD. The scope is flash
programming: identify the target, erase, write, verify, reset and run.

Out of scope: interactive debugging, breakpoints, RTT, semihosting, a GDB server, and a target
database. probe-rs covers all of these, and on a desktop it remains the recommended tool for them.

This revises nordic-dfu spec fact 1. On acceptance, that sentence changes to say SWD is reached
through the debug-probe tier, not through Nordic DFU.

### D2 — A new package tier, `Periphery.DebugProbe.*`, starting with `Periphery.DebugProbe.StLink`

The package depends on one transport spoke (`Periphery.Usb`), on `Periphery.Firmware` for images,
and on `Periphery.Bootloader` for the flashing contract. That is the shape
[ADR-0061](0061-firmware-flashing-platform.md) DEC-001 allows a flasher, applied to a new tier.

Inside the package, three layers sit behind an internal memory-access seam:

| Layer | Namespace | Knows about the probe? |
|---|---|---|
| ST-Link codec and transport | `Periphery.DebugProbe.StLink` | Yes |
| Cortex-M core control | `Periphery.DebugProbe.CortexM` | No |
| STM32G4 flash driver | `Periphery.DebugProbe.Targets.Stm32G4` | No |

The seam is a 32-bit memory read and write, plus halt and reset expressed as memory writes. When a
second probe arrives (CMSIS-DAP, per the exploration), the seam, core control and flash drivers move
to a `Periphery.DebugProbe` package that both probes depend on. This follows the extract-on-second-
consumer rule ADR-0061 DEC-005 applied to the generic DFU layer.

### D3 — The ST-Link is opened through `UsbDevice` on interface 0, so one path serves a page and a desktop

`StLinkProgrammer.OpenAsync(DeviceInfo)` opens the probe with `UsbDevice.OpenAsync`, as
`Stm32DfuProgrammer` does. In a page, ADR-0092 D6 routes that call to `Periphery.Usb.Browser`. On a
desktop, it reaches `WinUsbBackend` or libusb. No change to either USB backend is needed.

The package accepts the PIDs in ST's driver INF and takes endpoints from the PID, as probe-rs does:

| Hardware | PIDs | OUT | IN | SWO |
|---|---|---|---|---|
| ST-Link/V2 | `3748` | `0x02` | `0x81` | `0x83` (unused) |
| ST-Link/V2-1 | `374A`, `374B`, `3752` | `0x01` | `0x81` | `0x82` (unused) |
| STLINK-V3 | `374E`, `374F`, `3753`, `3754`, `3757` | `0x01` | `0x81` | `0x82` (unused) |

Recovery never resets the USB device. probe-rs resets the probe when its first command fails with a
USB error. In a page, `reset()` is unimplemented on Windows (ADR-0092 D9). A failed open is
reported, and the caller reopens.

### D4 — The probe is a `Probe`-identified target and is never auto-flashed

`StLinkProvider.Identification` is `IdentificationMode.Probe`. The probe's VID and PID say nothing
about the chip behind it, the same way a CP210x says nothing about the chip on its UART. A probe
appears in FlashAnything and can be flashed only by an explicit action.

The target's identity is read at `IdentifyAsync`, from the STM32 debug MCU device-ID register and
the flash-size register (RM0440). It maps onto `DeviceIdentity` as follows:

| Field | Value |
|---|---|
| `Family` | `"STM32G4"` |
| `Chip` | The part resolved from the device ID and flash size |
| `BootloaderVersion` | The probe's firmware version, for example `"V3J7"` |
| `TransferSize` | The write chunk the plan uses |
| `Regions` | Main flash only: `Readable`, `Writable`, `Erasable`, with its page size |

`BootloaderVersion` carrying a probe's firmware version is a misfit. See
[Consequences](#negative).

### D5 — Slice 1 programs the STM32G431 by register writes, rewrites whole pages, and refuses everything else

The first part is the STM32G431, the part the browser/WASM exploration synced over its USART
bootloader. The flash driver writes the flash controller's registers through the memory seam,
following RM0440's standard programming sequence: unlock, clear errors, set the program bit, write a
doubleword, wait for not-busy, check the result, and lock when done.

The driver rewrites whole pages, and only the pages the image touches. It reads each such page
first, merges the image's bytes into that copy, erases the page, and programs the merged copy.
Bytes the image does not cover keep their values. Every write is page-aligned, so every doubleword
is aligned, and no segment needs padding.

It does not use a CMSIS-Pack flash algorithm. The license of those algorithms is unknown
(exploration open question 4), and register writes need no code loaded into the target.

The plan refuses, before any write:

- A device ID other than the STM32G431's.
- A read-protection level other than 0. Lowering read protection mass-erases the part, and level 2
  is permanent.
- Any segment outside main flash, including option bytes, OTP and system memory.

The programmer never writes option bytes. It halts the core through reset-and-halt before erasing,
freezes the watchdogs through the debug MCU while halted, verifies each rewritten page by reading it
back against the merged copy, and leaves with a system reset that runs the application.

### D6 — Every layer is a pure core with a thin shell ([ADR-0052](0052-periphery-treehopper-pure-core.md))

| Pure core | Shell |
|---|---|
| `StLinkCommand`: a closed union, each case encoding its 16-byte command block and expected response length | `StLinkTransport`: bulk writes and reads on the PID's endpoints |
| `StLinkStatus` and `StLinkVersion`: decoders for status bytes and `GET_VERSION` | `StLinkProgrammer`: owns the `UsbDevice`, the open sequence, and the poll loop |
| `CortexMControl`: halt, reset-and-halt and run, as sequences of register writes and the reads that confirm them | The memory seam's ST-Link implementation |
| `Stm32G4FlashPlan`: from an image and the geometry, the pages to read; from their current contents, the merged pages and the `FlashStep`s that erase, program and verify them. It carries every refusal in D5. | Reading the pages, `FlashStep` execution, and the poll cadence on an injected `TimeProvider` |

Poll cadence is a pure state advanced by a shell-owned clock, and tests drive it with a manual
clock ([ADR-0089](0089-tests-do-not-depend-on-elapsed-time.md)).

The shell is tested end to end against a pure model of the STM32G4 flash controller. The model
accepts the same register writes as the part, rejects writes the part rejects, and reports busy for
a fixed count of polls. Tests of each refusal are proved by mutation: remove the check, and the test
goes red.

### D7 — The package refuses probe firmware below probe-rs's floor, and never updates it

The open sequence reads the probe's current mode, returns it to idle, then sends `GET_VERSION`, as
probe-rs does. The package refuses an ST-Link/V2 or V2-1 with JTAG firmware below 26, and a V3
below 3. These are probe-rs's minimums.
They are a starting floor, to be raised if a measured command fails on firmware above them.

The error tells the user to update the probe with ST's own tool. Periphery never ships, downloads or
applies probe firmware.

### D8 — Licensing follows the exploration's rules, and the package is not published before a legal review

The package follows the exploration's [Licensing](../explorations/browser-debug-probes-2026-10.md#licensing)
section in full. In short:

- Code may be adapted from probe-rs, stlink-org, pyOCD and webstlink, under their licenses. The
  commit that introduces adapted code also carries everything that source's license requires: its
  copyright notice, its license text, and, for Apache-2.0, any NOTICE content and a statement of
  the changes made.
- OpenOCD and libjaylink are not opened, not even to check a constant.
- No vendor software is decompiled.

The exploration lists two legal assumptions: that implementing an observed protocol is lawful, and
that no license term binds the people doing the work. The ST-Link protocol is known only by
observation, including where the permissive sources learned it, so every line of the ST-Link codec
rests on the first assumption. Until the review is recorded in this ADR:

- **Work that does not encode the ST-Link protocol proceeds.** The memory seam, Cortex-M control,
  the STM32G4 flash plan, the flash-controller model, and the provider wiring against a fake memory
  seam rest on Arm's and ST's published manuals and Periphery's own code.
- **The ST-Link codec and transport wait.** No ST-Link command is encoded, and no probe is driven,
  until the review is recorded.
- **No one captures vendor software.** ST's licenses have not been read, and SEGGER's restricts its
  software.
- **Nothing is published.** The package is `IsPackable=false`.

---

## Consequences

### Positive

- A page can program an STM32 with no install beyond ST's driver on Windows and a udev rule on Linux.
- One code path serves a page and a desktop, because both open the probe through `UsbDevice`.
- No change to `Periphery.Usb.Browser` or to any desktop backend.
- Cortex-M control and the STM32G4 flash driver are probe-independent from the start, so CMSIS-DAP
  reuses them.
- The flash plan's refusals are pure and testable without hardware.

### Negative

- Periphery now overlaps probe-rs on the desktop for STM32G431 flash programming. D1 bounds the
  overlap to flash programming.
- One part only. Every further STM32 family needs its own geometry, refusal set and hardware gate.
- The ST-Link half of the slice cannot start until a legal review is recorded. Only the
  probe-independent layers can be built before it.
- Host register writes cost a USB round trip per poll, and each touched page is read before it is
  rewritten. Throughput is unmeasured.
- A failure between a page's erase and its reprogram loses that page's bytes outside the image as
  well. The merged copy exists only in the host. A retry in the same session can restore it. If the
  browser tab or app closes first, those bytes are gone.
- The flashing contract's names come from bootloaders. `IFirmwareProgrammer` is documented as a
  bootloader session, and `DeviceIdentity.BootloaderVersion` carries a probe's firmware version here.
  Both are left as they are until the contract graduates (ADR-0061 phase 2).
- A target in a low-power mode, or with SWD pins repurposed by its firmware, needs connect-under-
  reset. Slice 1 does not do it, and reports the failure.

---

## Alternatives considered

**Run probe-rs as a separate process.** Rejected for a page, which cannot start a process. On a
desktop this remains the recommendation for everything outside D1.

**Compile probe-rs to WASM.** Not chosen. It would put a second WASM runtime beside .NET, and
probe-rs's probe interfaces are synchronous while WebUSB is promise-only. Unexplored rather than
refuted.

**CMSIS-DAP v2 first.** Deferred to the second slice. Its protocol is published and it needs no
driver, but it needs the browser backend to claim an arbitrary interface and read interface strings,
and it needs an ADIv5 layer in the host. ST-Link proves the other layers first.

**J-Link first.** Rejected for now. Of the two J-Links measured, only the on-board one is reachable
from a page, it needs the same backend change as CMSIS-DAP, and SEGGER's terms restrict how its
probes may be used.

**CMSIS-Pack flash algorithms.** Not chosen while their license is unknown (exploration open
question 4). Revisit if throughput from D5 is unacceptable.

**Name the package `Periphery.Bootloader.Stm32.StLink`.** Rejected. ADR-0061 DEC-002's taxonomy
fits on paper, but an ST-Link is not a bootloader and is not STM32-specific. ADR-0061 rejected
"Dfu" as an umbrella for the same kind of misnaming.

**Mass erase before writing.** Rejected. It destroys all of flash, and SWD has no need for the
shortcut the DFU flasher's phase 1 took.

**Erase touched pages without merging.** Rejected. It destroys the bytes an image does not cover in
the pages it touches. A caller flashing a partial image does not expect that.

---

## Verification

| Gate | Passes when | Decisions |
|---|---|---|
| Pure core | Codec, version decode, core control and flash plan tests pass with no hardware. A partial-page image leaves the page's other bytes unchanged. Every refusal test and the merge test are proved by mutation. | D5, D6 |
| Shell against the model | The flash shell erases, programs and verifies an image through a fake memory seam backed by the flash-controller model, with a manual clock. | D6 |
| Legal review | The review of the exploration's two assumptions is recorded here. Every gate below waits for it. | D8 |
| Probe opens on Windows, desktop | Through `Periphery.Usb`, an ST-Link answers `GET_VERSION`, reports target voltage, and reads the STM32G431's device ID. | D3, D4, D7 |
| Probe opens on Windows, page | In Chrome on Windows, the same from a granted ST-Link. This also measures exploration finding 3. | D3 |
| Flash from a page | In Chrome on Windows, an STM32G431 image is written, read back identical, and runs after the reset. | D5 |
| Flash from a desktop | The same through FlashAnything's CLI. | D2, D4, D5 |
| Refusals on hardware | A part with read protection at level 1 is refused before any write. | D5 |

Hardware needed: an ST-Link/V2-1 or STLINK-V3 wired to an STM32G431, for example a NUCLEO-G431RB,
whose on-board probe is an STLINK-V3E. None was attached for the exploration.

---

## Open questions

| # | Question | Resolves with |
|---|---|---|
| 1 | Is the bench's ST-Link board an STM32G431? If not, D5's part changes to the one it carries. | An inventory. |
| 2 | How many doublewords can one `WRITEMEM_32BIT` carry to flash on the STM32G431 before the busy poll, and what throughput results? | A measurement on the hardware gate. |
| 3 | Should FlashAnything's GUI list a probe before its target is identified, or open it first? | The FlashAnything spec, at implementation. |
| 4 | Does Linux need anything beyond the udev rule ADR-0092 already names? | The Linux run of the page gate. |

---

## Related

- [`browser-debug-probes-2026-10.md`](../explorations/browser-debug-probes-2026-10.md): the
  exploration this ADR decides.
- [ADR-0061](0061-firmware-flashing-platform.md): the flashing platform, package tier rules and
  contract.
- [ADR-0092](0092-browser-devices-come-from-grants.md): the browser USB backend this rides on.
- [ADR-0052](0052-periphery-treehopper-pure-core.md): the pure-core pattern.
- [ADR-0089](0089-tests-do-not-depend-on-elapsed-time.md): tests and clocks.
- [esp32 adr.md](../feature-specs/firmware-flashing/esp32/adr.md), Decision 5: the licensing rules
  this follows.
- [nordic-dfu spec](../feature-specs/firmware-flashing/nordic-dfu/spec.md#two-facts-that-shape-everything-below):
  the scope statement D1 revises.
- RM0440, the STM32G4 reference manual: flash controller, debug MCU and device-ID registers.
