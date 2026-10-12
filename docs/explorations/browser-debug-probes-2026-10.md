# Debug probes from a browser

**Date:** 2026-10-11
**Status:** Exploration. Nothing here is a decision. It asks whether a Periphery page should drive
an SWD debug probe through the [ADR-0092](../adr/0092-browser-devices-come-from-grants.md)
packages. The firmware-flashing platform has kept SWD out of scope so far
([nordic-dfu spec](../feature-specs/firmware-flashing/nordic-dfu/spec.md#two-facts-that-shape-everything-below)).
The findings would feed an ADR if the work is taken up.
**Method:** Windows driver bindings were read from PnP device properties and driver INF files on a
Windows 11 machine. Two J-Links were attached: the on-board J-Link of an nRF52833 DK and a
standalone J-Link. ST's ST-Link driver package was installed. No ST-Link was attached. Interface
layouts and command sets were read from probe-rs at `5214b96b9a`. Chromium behaviour was read from
Chromium source on `main` as of the date above. Licenses were read from each project's license file
or README. No browser was run for this document.
**Scope:** ST-Link, J-Link and CMSIS-DAP probes, reached from a Chromium page over WebUSB or WebHID,
to program flash over SWD. Out of scope: non-Chromium browsers, JTAG, interactive debugging, RTT,
and the desktop. A desktop app can run probe-rs as a separate process.
**Decided in:** [ADR-0094](../adr/0094-st-link-is-the-first-debug-probe-flasher.md) (Proposed), for
the ST-Link first slice.

## Evidence labels

| Label | Meaning |
|---|---|
| Measured | Observed for this document. |
| Source | Read in the source code or driver package of the named component. |
| Documented | Stated in the vendor's own documentation. |
| Inferred | Follows from the above. Not observed. |

---

## Why the browser changes the question

On a desktop, Periphery has no reason to drive probes itself. probe-rs (MIT / Apache-2.0) supports
ST-Link, J-Link, CMSIS-DAP and several other probes. An app can run `probe-rs download` as a
separate process without taking on any of its code.

A page cannot start a process. A page that programs a chip over SWD needs code in the page that
speaks the probe's protocol.

The transport underneath already works. On 2026-10-11, in Chrome 155 on Windows, the ADR-0092
packages opened a granted Treehopper and a granted STM32 DFU bootloader bound to WinUSB through an ST
INF (ADR-0092, Verification).

---

## Findings, ranked

Ranked by how much each one decides whether to start and where.

| # | Finding | Evidence | Consequence |
|---|---|---|---|
| 1 | probe-rs does not run in a page. A code search of `probe-rs/probe-rs` for `wasm32` finds only `deny.toml`. Its USB library, nusb, has a `webusb` platform module on `main` (`57ff5ae6cd`). | Source | A page needs its own probe code, or a WASM build of probe-rs that does not exist yet. See [probe-rs compiled to WASM](#probe-rs-compiled-to-wasm). |
| 2 | The probe protocol is the smallest layer of SWD flashing. probe-rs keeps the rest in `architecture/arm` (`dp`, `ap`, `core`, `sequences`), `flashing`, and 255 target-family files under `targets/`. Each target file carries its flash algorithms as compiled Thumb code. | Source | Cost grows with the chip families supported, not with the probes. See [The layers under a flash write](#the-layers-under-a-flash-write). |
| 3 | ST-Link's debug function is interface 0. probe-rs claims interface 0 for every ST-Link PID. Endpoints are OUT `0x02`, IN `0x81`, SWO `0x83` on V2 (`3748`), and OUT `0x01`, IN `0x81`, SWO `0x82` on V2-1 and V3. ST's `stlink_dbg_winusb.inf` 2.2.0.0 binds WinUSB to `0483:3748` as a whole device and to `MI_00` of `374A`, `374B`, `374E`, `374F`, `3752`, `3753`, `3754` and `3757`. | Source | `Periphery.Usb.Browser` claims interface 0 at open and no other (ADR-0092). An ST-Link fits it as built. Not measured, because no ST-Link was attached. |
| 4 | ST-Link firmware performs target memory access itself. Its command set includes `JTAG_READMEM_32BIT` (`0x07`) and `JTAG_WRITEMEM_32BIT` (`0x08`). DP and AP register commands arrive in firmware V2J24. | Source | The host needs no ADIv5 layer to program an STM32. Writes to the flash controller's registers are enough. |
| 5 | On Windows, Chromium opens each function of a composite device through that function's own device path. It refuses to claim an interface whose function driver is not WinUSB. | Source (`services/device/usb/usb_device_handle_win.cc`) | A probe is reachable when its debug function binds WinUSB. The drivers on its other functions do not matter. |
| 6 | The nRF52833 DK's on-board J-Link (`1366:1025`) is composite. Interface 0 is CDC on SEGGER's `JLinkCDC` service. Interface 2 is class `FF/FF/FF` bulk on SEGGER's `jlinkwinusb.inf`, which includes `winusb.inf`. Interface 3 is mass storage. | Measured | Chromium can claim interface 2 (Inferred from 5). `Periphery.Usb.Browser` cannot, because it claims interface 0 only. |
| 7 | The standalone J-Link (`1366:0101`) binds SEGGER's kernel driver: `jlink.inf` 2.70.08.0, service `jlink`, `jlinkx64.sys`. That INF lists a probe USB-configuration bit for "BULK via WinUSB driver. Needs to be enabled in J-Link config area". | Measured, Source | Chromium cannot open this probe as configured. The INF says WinUSB mode must be enabled in the probe's config area. How that setting is changed was not checked (open question 8). |
| 8 | Arm publishes CMSIS-DAP under Apache-2.0. Arm says v2 adapters need no driver on Windows 8 and later or on macOS, and need a udev rule on Linux. Arm calls v1 deprecated. | Documented | A v2 probe is reachable over WebUSB with no install. A v1 probe is HID, needs WebHID, and ADR-0092 left WebHID out of scope. |
| 9 | probe-rs recognises a CMSIS-DAP v2 interface by an interface string containing "CMSIS-DAP" and class `FF` subclass `00`. It scans every interface, so the number varies by probe. | Source | The browser backend needs to claim an interface other than 0 and read interface strings. |
| 10 | No probe vendor appears on Chromium's static WebUSB blocklist. Checked: `0483` (ST), `1366` (SEGGER), `0D28` (Arm), `2E8A` (Raspberry Pi), `1FC9` (NXP), `03EB` (Microchip), `C251` (Keil). | Source (`chrome/browser/usb/usb_blocklist.cc`) | Nothing is blocked today. Entries can also arrive by server-side config (ADR-0092). |
| 11 | Two web projects already flash through a probe. webstlink (MIT, a port of pystlink) drives ST-Link V2 and V2-1 over WebUSB. Its flash writes were tested on STM32F103 only, and it was last pushed in 2018. The micro:bit Python editor flashes over WebUSB with DAP.js (MIT), a CMSIS-DAP library. | Documented | Probe flashing works in Chromium. Both projects are usable references under [Licensing](#licensing). |

---

## The layers under a flash write

Programming flash over SWD takes five layers. Only the first two depend on the probe.

| Layer | What it does | Source of truth | Who implements it |
|---|---|---|---|
| Transport | Bulk transfers on the probe's debug interface | WebUSB | `Periphery.Usb.Browser`, extended past interface 0 for CMSIS-DAP and J-Link |
| Probe protocol | Encodes commands, decodes responses | CMSIS-DAP specification. ST-Link and J-Link are reverse-engineered. | One codec per probe |
| Debug port and access port | SWD line reset, debug power-up, MEM-AP setup, 32-bit memory access | Arm ADIv5 specification | The host for CMSIS-DAP and J-Link. The probe firmware for ST-Link (finding 4). |
| Core control | Halt, reset and resume through `DHCSR`, `DEMCR` and `AIRCR` | Arm v7-M and v8-M Architecture Reference Manuals | Shared across probes |
| Flash driver | Unlock, erase, program, verify | The chip's reference manual, or a CMSIS-Pack flash algorithm | One per chip family |

Each layer splits into a pure core and a shell, as the rest of Periphery does
([ADR-0052](../adr/0052-periphery-treehopper-pure-core.md)). Command codecs, debug-port state and a
flash plan are values. The shell owns transfers and waits.

A flash driver takes one of two forms.

- **Host register writes.** The host unlocks the controller, sets the program bit, writes words and
  polls a busy flag. One small driver per family, written from the reference manual. Every poll is a
  USB round trip, so it is slow (Inferred).
- **A flash algorithm.** Position-independent code is loaded into target RAM and called through the
  core registers. probe-rs and CMSIS-Packs work this way. The algorithm's license comes from where it
  came from. See open question 4.

---

## The probes compared

| | CMSIS-DAP v2 | ST-Link | J-Link |
|---|---|---|---|
| Protocol | Published, Apache-2.0 | Reverse-engineered | Reverse-engineered |
| Browser API | WebUSB | WebUSB | WebUSB |
| Windows driver | None needed on Windows 8 and later (Documented) | ST's WinUSB INF on `MI_00` (Source) | On-board: WinUSB on interface 2 (Measured). Standalone: SEGGER's kernel driver (Measured). |
| Fits `Periphery.Usb.Browser` as built | No. The interface number varies. | Yes. Interface 0. | No. Interface 2 on the measured DK. |
| Host implements ADIv5 | Yes | No | Yes (probe-rs `jlink/bits.rs` drives SWD at bit level) |
| References Periphery may use | Arm specification, DAP.js, probe-rs, pyOCD | probe-rs, stlink-org, pyOCD, webstlink | probe-rs |

---

## Licensing

Nothing proposed here relies on a reverse-engineering exception. The planned inputs are a published
specification, permissively licensed code used under its license, and USB traffic observed between a
probe and software that drives it.

These points are established, because they follow from license text:

- Copying or adapting code is governed by that code's license. The table below lists what each
  license allows Periphery to do.
- Periphery's PolyForm Small Business license is GPL-incompatible. GPL code cannot be copied or
  adapted into it.

These points are assumptions, and need legal review before any of this ships:

- That observing a probe's USB traffic and implementing the observed protocol is lawful. This
  document treats protocol messages as unprotected facts. The statutory provisions on studying and
  decompiling software for interoperability are narrow and conditional (US: *Sega v. Accolade* and
  17 U.S.C. §1201(f). EU: Directive 2009/24/EC, Articles 5(3) and 6). This document does not claim
  they cover any particular step.
- That no license term binds the people doing the work. In the US, a license term the user accepted
  can restrict acts the law would otherwise permit. Rule 3 exists for this reason, and the ST licenses
  it names were not read.

This section is the project's reading, not legal advice. It follows the rules the ESP32 flasher
adopted ([esp32 adr.md](../feature-specs/firmware-flashing/esp32/adr.md), Decision 5).

| Source | License | Use in Periphery |
|---|---|---|
| CMSIS-DAP specification | Apache-2.0 | Implement from it |
| probe-rs | MIT / Apache-2.0 | Reference and adapt, with attribution |
| pyOCD | Apache-2.0 | Reference and adapt, with attribution |
| stlink-org/stlink | BSD-3-Clause | Reference and adapt, with attribution |
| webstlink | MIT | Reference and adapt, with attribution |
| DAP.js | MIT | Reference and adapt, with attribution |
| OpenOCD | GPL-2.0-or-later | Off-limits. Periphery's PolyForm Small Business license is GPL-incompatible. |
| libjaylink | LGPL-2.1-or-later | Off-limits for copied or adapted code |

1. Implement from the published specification where one exists, and from the permissive sources
   above otherwise. Do not open OpenOCD or libjaylink to check a constant.
2. USB captures of any tool driving a probe may become test vectors, under the first assumption above.
3. Do not decompile vendor software. SEGGER's software license forbids decompiling, disassembling or
   reverse engineering its software (§3(a) in a mirrored copy, which may be out of date). ST's
   licenses for STM32CubeProgrammer and the ST-Link firmware updater were not read for this document.
4. Never ship probe firmware.
5. A probe's own license binds its owner, whatever software drives it. SEGGER limits J-Link EDU and
   EDU Mini to non-commercial use. SEGGER's terms for its on-board firmware on ST and NXP boards limit
   it to that vendor's chips. Periphery cannot enforce these terms and should not imply it lifts them.

---

## probe-rs compiled to WASM

Not explored. nusb's `webusb` module means the USB layer of probe-rs could run in a browser. The
page would host two WASM runtimes, .NET and Rust, joined through JavaScript. In exchange it would
reuse probe-rs's target files (finding 2) instead of rebuilding them.

probe-rs's probe interfaces are synchronous. Its ST-Link transport calls `read_bulk` and
`write_bulk` with a timeout. WebUSB is promise-only. Bridging the two on one browser thread is the
first thing a spike would check (Inferred).

---

## A first slice, if the work is taken up

The first consumer should pick the first probe. Two consumers are visible:

- **Recovering an nRF whose bootloader is gone.** The nordic-dfu spec says such a device "leaves
  this platform's reach entirely". This needs a probe that reaches an nRF: the DK's on-board J-Link,
  or a CMSIS-DAP probe.
- **Programming an STM32 board without setting BOOT0.** Every Nucleo and Discovery board carries an
  ST-Link.

With no consumer named, ST-Link with one STM32 family is the smallest end-to-end proof:

- It needs no backend change, because the debug interface is 0 (finding 3).
- It needs no ADIv5 layer, because the probe firmware accesses memory (finding 4).
- It needs one flash driver, written as host register writes from one reference manual.

CMSIS-DAP v2 is the general second step. Its protocol is published, it needs no driver, and it covers
probes from many vendors. It needs `Periphery.Usb.Browser` to claim an arbitrary interface and read
interface strings. It also needs the ADIv5 layer.

J-Link comes last. Of the two measured here, only the on-board J-Link is reachable without
reconfiguring the probe, and only after the same interface change.

Any of these needs an ADR that revises the SWD exclusion in the nordic-dfu spec, at least for the
browser.

---

## Open questions

| # | Question | Resolves with |
|---|---|---|
| 1 | Does a real ST-Link match finding 3, with `MI_00` bound to WinUSB and the endpoints listed? | Attach one and read its PnP properties and descriptors. |
| 2 | Does Chromium open the DK's on-board J-Link and claim interface 2? | A page calling `claimInterface(2)` in a full Chrome. |
| 3 | Which CMSIS-DAP v2 probes are on the bench, and at which interface number? | An inventory. |
| 4 | What license covers the flash algorithms embedded in probe-rs's target files, and the CMSIS-Packs they came from? | probe-rs's target-generation provenance and the packs' license files. |
| 5 | What do ST's licenses for STM32CubeProgrammer and the ST-Link firmware updater say about reverse engineering? | Read them. |
| 6 | Once a .NET probe core exists, it also runs on the desktop through `Periphery.Usb`. Does the desktop use it, or keep probe-rs? | The ADR. |
| 7 | Can probe-rs's synchronous probe interfaces run over promise-only WebUSB? | A spike. |
| 8 | How is a standalone J-Link's WinUSB mode enabled, and which tools can change it? | SEGGER's documentation, then a standalone J-Link switched and re-read. |

---

## Sources

- probe-rs at `5214b96b9a`: `probe-rs/src/probe/stlink/usb_interface.rs`, `stlink/constants.rs`,
  `cmsisdap/tools.rs`, `jlink/mod.rs`, `jlink/bits.rs`, `probe-rs/targets/`.
  <https://github.com/probe-rs/probe-rs>
- nusb at `57ff5ae6cd`: `src/platform/webusb`. <https://github.com/kevinmehall/nusb>
- Chromium: `services/device/usb/usb_device_handle_win.cc`, `chrome/browser/usb/usb_blocklist.cc`.
- ST `stlink_dbg_winusb.inf` 2.2.0.0, and SEGGER `jlink.inf` 2.70.08.0 and `jlinkwinusb.inf`
  3.00.00.000, read from the Windows driver store.
- CMSIS-DAP: <https://arm-software.github.io/CMSIS-DAP/latest/> and
  <https://arm-software.github.io/CMSIS-DAP/latest/dap_drv_install.html>
- webstlink: <https://github.com/devanlai/webstlink>
- DAP.js: <https://github.com/ARMmbed/dapjs>
- micro:bit Python editor, `docs/tech-overview.md`:
  <https://github.com/microbit-foundation/python-editor-v3>
- SEGGER J-Link EDU terms: <https://www.segger.com/j-link-arm-for-non-commercial-use.html>
- SEGGER software license, mirrored copy:
  <https://cos.googlesource.com/third_party/overlays/portage-stable/+/refs/heads/main-R97/licenses/SEGGER>
- SEGGER on-board firmware for ST-LINK: <https://segger.com/jlink-st-link.html>
- SEGGER on-board firmware for LPC-Link2: <https://www.segger.com/jlink-lpcxpresso-ob.html>
- OpenOCD `COPYING`: <https://github.com/openocd-org/openocd>
- libjaylink `README.md`: <https://gitlab.zapb.de/libjaylink/libjaylink>
