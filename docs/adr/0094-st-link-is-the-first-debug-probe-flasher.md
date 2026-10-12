---
title: "ADR-0094: The first debug-probe flasher drives an ST-Link over SWD and programs STM32 flash with the vendor's pack algorithm"
status: "Proposed"
status_note: "Drafted 2026-10-11 from the exploration browser-debug-probes-2026-10.md. Revised the same day: D5 runs the vendor's CMSIS-Pack flash algorithm instead of host register writes, and D9 sets where algorithms come from and under what license. Nothing is implemented. The ST-Link facts are Source (probe-rs, ST's driver INF); no ST-Link has been attached for this work. RM0440 was not re-read for this draft, so D4's registers are to be checked against it. The package stays unpublished until the legal review in D8 is recorded."
date: "2026-10-11"
authors: "@charles8051"
tags: ["architecture", "decision", "firmware-flashing", "debug-probe", "st-link", "swd", "stm32", "cmsis-pack", "browser", "webusb", "usb", "pure-core", "licensing"]
supersedes: ""
superseded_by: ""
depends_on: ["0024-extension-package-pattern.md", "0038-periphery-usb.md", "0052-periphery-treehopper-pure-core.md", "0061-firmware-flashing-platform.md", "0089-tests-do-not-depend-on-elapsed-time.md", "0092-browser-devices-come-from-grants.md"]
---

# ADR-0094: The first debug-probe flasher drives an ST-Link over SWD and programs STM32 flash with the vendor's pack algorithm

**Tracks:** one new package, `Periphery.DebugProbe.StLink`. Revises the scope statement in
nordic-dfu spec fact 1. Answers the exploration's open question 4 for the STM32G4 pack, and its
open question 6 for this slice.

---

## Status

Proposed 2026-10-11. Revised the same day: D5 now runs the vendor's CMSIS-Pack flash algorithm
instead of writing flash-controller registers from the host, and D9 records where algorithms come
from. Nothing is implemented. Stays Proposed until the gates under [Verification](#verification)
pass.

---

## Context

### What is already known

[`browser-debug-probes-2026-10.md`](../explorations/browser-debug-probes-2026-10.md) surveyed
ST-Link, J-Link and CMSIS-DAP as targets for a Periphery page. Its evidence labels (Measured,
Source, Documented, Inferred) are used unchanged here. It established:

- A page cannot run probe-rs, which has no browser build. A desktop app can run it as a separate
  process. (Source.)
- The probe protocol is the smallest layer of SWD flashing. The rest is debug-port logic, Cortex-M
  core control, and flash programming per chip family. (Source.)
- ST-Link's debug function is interface 0 on every PID ST's driver lists, and ST's Windows driver
  binds WinUSB to it. `Periphery.Usb.Browser` claims interface 0 and no other
  ([ADR-0092](0092-browser-devices-come-from-grants.md)). (Source.)
- ST-Link firmware performs 32-bit target memory reads and writes itself. A host can program an
  STM32 without implementing ADIv5. (Source.)
- CMSIS-DAP v2 and the on-board J-Link need the browser backend to claim an interface other than 0.
  (Source, Measured.)

It ranked ST-Link with one STM32 family as the smallest end-to-end proof, and CMSIS-DAP v2 as the
general second step.

### How probe-rs programs flash

probe-rs has no per-family STM32 flash code. It loads a CMSIS-Pack flash algorithm into target RAM
and calls its functions through the core registers (`probe-rs/src/flashing/flasher.rs`). Its target
files embed those algorithms. The STM32G4 file records `generated_from_pack: true` and
`pack_file_release: 1.6.0`. (Source.)

### What the packs are licensed under

GitHub reports Apache-2.0 for every STM32 device pack under the `Open-CMSIS-Pack` organisation. The
STM32G4 pack's `LICENSES` file splits it: Apache-2.0 for "CMSIS add-ons" and BSD-3-Clause for ST's
HAL. The source of the STM32G4 flash
algorithm, `CMSIS/Flash/STM32G4xx_Flash/FlashPrg.c`, also carries Arm's zlib grant in the file
itself. So does the STM32G0 algorithm's. The H7 algorithm's source carries only a Keil copyright
line ending "All rights reserved", with no grant of its own. (Source, checked 2026-10-11.)

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
database beyond the parts this tier has passed on hardware. probe-rs covers all of these, and on a
desktop it remains the recommended tool for them.

This revises nordic-dfu spec fact 1. On acceptance, that sentence changes to say SWD is reached
through the debug-probe tier, not through Nordic DFU.

### D2 — A new package tier, `Periphery.DebugProbe.*`, starting with `Periphery.DebugProbe.StLink`

The package depends on one transport spoke (`Periphery.Usb`), on `Periphery.Firmware` for images,
and on `Periphery.Bootloader` for the flashing contract. That is the shape
[ADR-0061](0061-firmware-flashing-platform.md) DEC-001 allows a flasher, applied to a new tier.

Inside the package, four layers sit behind an internal memory-access seam:

| Layer | Namespace | Knows about the probe? |
|---|---|---|
| ST-Link codec and transport | `Periphery.DebugProbe.StLink` | Yes |
| Cortex-M core control | `Periphery.DebugProbe.CortexM` | No |
| Flash plan and algorithm runner | `Periphery.DebugProbe.Flash` | No |
| Target records: device ID, flash algorithm, geometry, RAM window | `Periphery.DebugProbe.Targets` | No |

The seam is a 32-bit memory read and write. Halt, reset and core-register access are memory
operations on the Cortex-M debug registers. When a second probe arrives (CMSIS-DAP, per the
exploration), every layer except the ST-Link codec moves to a `Periphery.DebugProbe` package that
both probes depend on. This follows the extract-on-second-consumer rule ADR-0061 DEC-005 applied to
the generic DFU layer.

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
| `TransferSize` | The flash algorithm's programming page size |
| `Regions` | Main flash only: `Readable`, `Writable`, `Erasable`, with the algorithm's sector size |

`BootloaderVersion` carrying a probe's firmware version is a misfit. See
[Consequences](#negative).

### D5 — Slice 1 flashes the STM32G431 with the vendor's pack algorithm, rewrites whole sectors, and refuses everything else

The first part is the STM32G431, the part the browser/WASM exploration synced over its USART
bootloader.

Flash is programmed by running the part's CMSIS-Pack flash algorithm in target RAM. For the
STM32G431 that is `STM32G43x-4x_128.FLM` from `Open-CMSIS-Pack/STM32G4xx_DFP` v2.2.0. The pack
assigns it to every STM32G431 variant, covering 128 KB at `0x08000000` with a 32 KB RAM window at
`0x20000000`. Its FlashDevice record gives a 2 KB sector, a 1 KB programming page, and 400 ms
timeouts for both program and erase. (Source.)

A call runs the way probe-rs runs one (Source, `flasher.rs`). The runner loads the algorithm at the
start of the RAM window behind a header of Thumb breakpoint instructions. For each call it writes
the arguments to R0 to R3, the function's address to PC, and the header's address plus one to LR,
so the function returns into a breakpoint. An `Init` call also sets R9 to the algorithm's static
base and SP to a stack above it. The runner resumes the core, waits for it to halt within the
record's timeout, and reads R0, where 0 is success. Core registers are written through the debug
core-register selector and data registers (DCRSR and DCRDR), so the runner needs nothing beyond the
memory seam.

A flash runs in two algorithm sessions and a read-back: `Init` for erase, `EraseSector` per touched
sector, `UnInit`; then `Init` for program, `ProgramPage` per page, `UnInit`; then verification by
reading memory.

The RAM window holds four regions, laid out in this order from its start, with no overlap:

| Region | Size |
|---|---|
| Breakpoint header, then the algorithm's code and data | From the record |
| Stack | A fixed size the record carries |
| Page buffer | The record's programming page size, 1 KB for the STM32G431 |
| Unused | The rest of the window |

The layout is computed in the pure core from the record. A record whose regions do not fit the
window is refused before the core is halted. Before each `ProgramPage` call, the runner writes the
merged page into the page buffer and passes the page's flash address, its size and the buffer's
address as the call's three arguments. The buffer always holds exactly one programming page.

The runner rewrites whole sectors, and only the sectors the image touches. It reads each such
sector first, merges the image's bytes into that copy, erases the sector, and programs the merged
copy page by page. Bytes the image does not cover keep their values.

A further part needs a target record (D9) and its own hardware gate. It needs no new code. A device
ID joins the allowlist only after its hardware gate passes.

The plan refuses, before any write:

- A device ID not on the allowlist. In slice 1, that is every ID but the STM32G431's.
- A read-protection level other than 0. Lowering read protection mass-erases the part, and level 2
  is permanent.
- Any segment outside the flash range. The range is the record's range cut to the flash size the
  part reports.

The programmer never writes option bytes. It halts the core through reset-and-halt before loading
the algorithm, freezes the watchdogs through the debug MCU while halted, verifies each rewritten
sector by reading it back against the merged copy, and leaves with a system reset that runs the
application. The algorithm overwrites the RAM window, and the final reset discards it.

### D6 — Every layer is a pure core with a thin shell ([ADR-0052](0052-periphery-treehopper-pure-core.md))

| Pure core | Shell |
|---|---|
| `StLinkCommand`: a closed union, each case encoding its 16-byte command block and expected response length | `StLinkTransport`: bulk writes and reads on the PID's endpoints |
| `StLinkStatus` and `StLinkVersion`: decoders for status bytes and `GET_VERSION` | `StLinkProgrammer`: owns the `UsbDevice` and the open sequence |
| `CortexMControl`: halt, reset-and-halt, run, and core-register access, as sequences of memory operations and the reads that confirm them | The memory seam's ST-Link implementation |
| `AlgorithmCall`: a function and its arguments, encoded as the register values of one call | Loading the algorithm and running each call |
| `FlashPlan`: from a target record, the RAM layout; from an image and the record, the sectors to read; from their contents, the merged sectors and the `AlgorithmCall`s that erase and program them. It carries every refusal in D5. | Reading sectors, the halt poll on an injected `TimeProvider`, and the read-back |

Poll cadence is a pure state advanced by a shell-owned clock, and tests drive it with a manual
clock ([ADR-0089](0089-tests-do-not-depend-on-elapsed-time.md)).

The shell is tested end to end against a pure model of a core running a flash algorithm. The model
recognises each call by its PC, applies the call to a model of flash, sets R0, and halts after a
fixed count of polls. Its flash model erases a sector to `0xFF` and fails a program into bytes that
are not erased. Tests of each refusal are proved by mutation: remove the check, and the test goes
red.

### D7 — The package refuses probe firmware below probe-rs's floor, and never updates it

The open sequence reads the probe's current mode, returns it to idle, then sends `GET_VERSION`, as
probe-rs does. The package refuses an ST-Link/V2 or V2-1 with JTAG firmware below 26, and a V3
below 3. These are probe-rs's minimums. They are a starting floor, to be raised if a measured
command fails on firmware above them.

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
rests on the first assumption. The restrictions below end when the review's outcome is recorded in
this ADR's Status section. A review that finds a problem supersedes this ADR instead.

- **Work that does not encode the ST-Link protocol proceeds.** The memory seam, Cortex-M control,
  the flash plan, the algorithm runner, the generator and its records (D9), the core model, and the
  provider wiring against a fake memory seam rest on Arm's published manuals, the packs' licenses,
  and Periphery's own code.
- **The ST-Link codec and transport wait.** No ST-Link command is encoded, and no probe is driven,
  until the review is recorded.
- **No one captures vendor software.** ST's licenses have not been read, and SEGGER's restricts its
  software.
- **No package is built for release.** `Periphery.DebugProbe.StLink` is `IsPackable=false`, so no
  NuGet package carries it. This covers the package only. The probe-independent layers above may be
  merged with their tests like any other code.

### D9 — Flash algorithms come from Open-CMSIS-Pack as generated records, never from probe-rs's target files

A generator tool in this repo reads an FLM file, which is an ELF image, together with its pack's
`.pdsc` entry. It writes one checked-in record per algorithm: the code bytes, each function's
offset, the static base, the FlashDevice fields, the RAM window, and the provenance (pack name, pack
version, file path and SHA-256 of the FLM). Runtime code reads records and never parses a pack.
probe-rs's `target-gen` (MIT / Apache-2.0) is the reference for reading FLM files.

Records come from the `Open-CMSIS-Pack` device packs. They do not come from probe-rs's target files,
which were built from older pack releases. The STM32G4 file's release, 1.6.0, predates the
`Open-CMSIS-Pack/STM32G4xx_DFP` tags, which start at v2.0.0, and its license was not checked.

An algorithm is admitted only when its own source file carries a permissive grant, as the STM32G4
and STM32G0 algorithms do, or when the pack's license is confirmed to cover that file. The H7
algorithm's source has no grant of its own, so it waits for that confirmation. Each record carries
its source's notices: the source file's grant, and the pack's Apache-2.0 license text with any
NOTICE content.

Only main-flash algorithms are generated. The pack's option-byte algorithms, such as
`STM32G4xx_SB_OPT.FLM`, are never generated, and the runner refuses a record whose range leaves main
flash.

---

## Consequences

### Positive

- A page can program an STM32 with no install beyond ST's driver on Windows and a udev rule on Linux.
- One code path serves a page and a desktop, because both open the probe through `UsbDevice`.
- No change to `Periphery.Usb.Browser` or to any desktop backend.
- Adding a part is a target record and a hardware gate. No Periphery code carries a flash
  controller's register map, because the vendor's algorithm does.
- Each call halts once per page or sector, not once per flash write (Inferred).
- Every layer except the codec is probe-independent from the start, so CMSIS-DAP reuses them.
- The flash plan's refusals are pure and testable without hardware.

### Negative

- Periphery now overlaps probe-rs on the desktop for STM32 flash programming. D1 bounds the overlap
  to flash programming on parts that passed on hardware.
- Periphery executes vendor code on the target. A fault inside the algorithm surfaces only as a
  timeout or a nonzero R0, and its behaviour is checked only by read-back.
- The ST-Link half of the slice cannot start until a legal review is recorded. Only the
  probe-independent layers can be built before it.
- Each touched sector is read before it is rewritten, and each call costs register writes and a halt
  poll. Throughput is unmeasured.
- A failure between a sector's erase and its reprogram loses that sector's bytes outside the image
  as well. The merged copy exists only in the host. A retry in the same session can restore it. If
  the browser tab or app closes first, those bytes are gone.
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

**Host register writes, one driver per controller design.** The first draft of this ADR chose it.
Rejected: each controller design needs its own driver and register map, and a wrong register map on
an untested part can start an option-byte write. The vendor's algorithm already encodes the
controller. It stays the fallback for a family whose algorithm license does not clear.

**Copy probe-rs's target files.** Rejected. Their algorithms were built from older pack releases
whose licenses were not checked, and regenerating from the current pack costs one tool.

**Name the package `Periphery.Bootloader.Stm32.StLink`.** Rejected. ADR-0061 DEC-002's taxonomy
fits on paper, but an ST-Link is not a bootloader and is not STM32-specific. ADR-0061 rejected
"Dfu" as an umbrella for the same kind of misnaming.

**Mass erase before writing.** Rejected. It destroys all of flash, and SWD has no need for the
shortcut the DFU flasher's phase 1 took.

**Erase touched sectors without merging.** Rejected. It destroys the bytes an image does not cover
in the sectors it touches. A caller flashing a partial image does not expect that.

---

## Verification

| Gate | Passes when | Decisions |
|---|---|---|
| Pure core | Core control, algorithm-call encoding, RAM layout and flash plan tests pass with no hardware. A partial-sector image leaves the sector's other bytes unchanged. A record too large for its window is refused. Every refusal test and the merge test are proved by mutation. | D5, D6 |
| Generator | The generator reproduces the checked-in STM32G431 record from the pack's FLM file, and the record's SHA-256 matches that file. | D9 |
| Shell against the model | The runner erases, programs and verifies an image through a fake memory seam backed by the algorithm-running core model, with a manual clock. | D5, D6 |
| Legal review | The review of the exploration's two assumptions is recorded here. Every gate below waits for it. | D8 |
| ST-Link codec | Command encoding, status and version decoding tests pass with no hardware. | D6, D7 |
| Probe opens on Windows, desktop | Through `Periphery.Usb`, an ST-Link answers `GET_VERSION`, reports target voltage, and reads the STM32G431's device ID. | D3, D4, D7 |
| Probe opens on Windows, page | In Chrome on Windows, the same from a granted ST-Link. This also measures exploration finding 3. | D3 |
| Flash from a page | In Chrome on Windows, an STM32G431 image is written by the pack algorithm, read back identical, and runs after the reset. | D5 |
| Flash from a desktop | The same through FlashAnything's CLI. | D2, D4, D5 |
| Refusals on hardware | A part with read protection at level 1 is refused before any write. | D5 |

Hardware needed: an ST-Link/V2-1 or STLINK-V3 wired to an STM32G431, for example a NUCLEO-G431RB,
whose on-board probe is an STLINK-V3E. None was attached for the exploration.

---

## Open questions

| # | Question | Resolves with |
|---|---|---|
| 1 | Is the bench's ST-Link board an STM32G431? If not, the first record is for the part it carries. | An inventory. |
| 2 | How long does one `ProgramPage` call take on the STM32G431 through an ST-Link, and what throughput results? | A measurement on the hardware gate. |
| 3 | Should FlashAnything's GUI list a probe before its target is identified, or open it first? | The FlashAnything spec, at implementation. |
| 4 | Does Linux need anything beyond the udev rule ADR-0092 already names? | The Linux run of the page gate. |
| 5 | What clock argument should `Init` receive for the STM32G4 algorithm? | Its `FlashPrg.c`, and probe-rs's choice. |
| 6 | Does each pack's Apache-2.0 grant for "CMSIS add-ons" cover algorithm sources with no grant of their own, such as H7's? | The pack maintainers, or the legal review. |
| 7 | The pack lists `STM32G431CBTxZ` with 112 KB of main flash under the 128 KB algorithm. Does its flash-size register report 112 KB, or must the record narrow the range? | Read the register on that variant, or ST's datasheet. |

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
- `Open-CMSIS-Pack/STM32G4xx_DFP` v2.2.0: the STM32G431 algorithm, its source and the pack's
  licenses. <https://github.com/Open-CMSIS-Pack/STM32G4xx_DFP>
- probe-rs `probe-rs/src/flashing/` and `target-gen/`: the reference for running and reading flash
  algorithms. <https://github.com/probe-rs/probe-rs>
- RM0440, the STM32G4 reference manual: debug MCU, device-ID, flash-size and option registers.
