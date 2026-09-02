# ADR: ESP32 flashing (esptool ROM loader + USB DFU)

<!--
Append-only / superseded, never rewritten. Decisions are numbered Decision 1..N.
The "what" (living requirements, current API) is in the sibling spec.md; this file records
the "how / why" so a future contributor sees the tradeoffs, not just the result. If a
decision here grows to be cited by a second feature, graduate it into a numbered repo-level
ADR under docs/adr/.
-->

Context: [ADR-0061](../../../adr/0061-firmware-flashing-platform.md) put ESP32 on the roadmap in
one line — `Periphery.Bootloader.Esp32.Serial`, esptool ROM over `Periphery.Serial` (DEC-007) —
and named ESP32-S2/S3 USB DFU as the second DFU consumer that would trigger extracting
`Periphery.Bootloader.Dfu` (DEC-005). Both entries assumed the serial lane was the ESP32 lane.

`Periphery.Serial` does not exist. [ADR-0062](../../../adr/0062-periphery-serial-backend-provider.md)
is Proposed with no code, and it cited high ESP32 baud rates as one of its own drivers. Reading
that literally, ESP32 support is blocked behind an unbuilt transport package.

It is not. Every ESP32 part from the S2 onward has a USB peripheral on-die, and
[`Periphery.Usb`](../../../../src/Periphery.Usb/UsbDevice.cs) already exposes the control and
bulk transfers that peripheral needs. This ADR records the decisions that follow from that,
plus the licensing constraint that shapes how the protocol code may be written at all.

Nothing here is measured on hardware. Two facts *are* sourced from Espressif's own documentation
rather than assumed — the DFU-mode id range and the Windows driver requirement — and they made the
DFU path's reach smaller, not larger. See [What is not yet measured](#what-is-not-yet-measured).
See [`spec.md`](spec.md).

---

## Decision 1 - ESP32 is one protocol over three transports, and the modern parts are a USB family

**Decision.** Model ESP32 support as a single protocol — Espressif's ROM serial loader — reached
by three independent transports: a UART bridge (`Periphery.Serial`), USB-Serial-JTAG
(`Periphery.Usb` bulk), and USB-OTG DFU (`Periphery.Usb` control, a different protocol entirely).
Treat the USB paths as the primary lane for the modern parts. ADR-0061 DEC-007's single serial
row is **narrowed to the classic ESP32 and ESP8266**, not superseded.

**Why.** The transport, not the protocol, is what is scarce here. The esptool ROM protocol is
publicly specified and moderately sized; a from-scratch implementation is comparable in shape and
volume to `Periphery.Bootloader.Stm32.Usb` (804 lines of source, 338 of tests). The transport is
where the actual blocker lives, and the blocker applies to exactly one of the three paths. Filing
all of ESP32 behind ADR-0062 would defer a feature that two-thirds of its surface does not need.

**What this costs.** ADR-0061's roadmap table is now imprecise where it reads "ESP32 / Serial".
Left as-is rather than amended: the ADR corpus is append-only, and this file is the correction
a reader following the cross-reference will find.

---

## Decision 2 - `Periphery.Bootloader.Esp32` is a required family core

**Decision.** Create the family-level package. The SLIP codec, command union, response decoder,
chip table, and flash planner live there, transport-free, alongside an `IEsp32Transport` seam.
`.Usb` and `.Serial` are thin leaves.

**Why.** [ADR-0061](../../../adr/0061-firmware-flashing-platform.md) DEC-002 makes the family
level conditional: it exists **iff** the family has shared protocol code. ESP32 is squarely the
"one protocol, many transports" case the rule was written for — a UART bridge and USB-Serial-JTAG
carry byte-identical esptool frames and differ only in the substrate the bytes cross. That is
structurally the EFM8 situation (`Periphery.Bootloader.Efm8` + `.Usb` / `.Serial`), not the STM32
one (where USB and serial share no wire protocol and the family package is optional).

The practical payoff is that Decision 6's phasing works at all: the core can be written and fully
tested before any decision about transports is settled.

---

## Decision 3 - Both USB protocols live in one `Periphery.Bootloader.Esp32.Usb` leaf, disambiguated by PID

**Decision.** `Esp32UsbSerialJtagProvider` (esptool ROM over bulk endpoints) and
`Esp32UsbDfuProvider` (USB DFU 1.1 over control transfers) ship in the same `.Usb` package. The
`BootloaderRegistry` picks between them on PID. Resolves the spec's OQ-3.

**Why.** ADR-0061 DEC-002 says the leaf is named for "the user-facing transport, not the USB
*class*" — the precedent being `Efm8.Usb` (HID class) and `Stm32.Usb` (DFU class) sitting at the
same level under different names because they are different families. ESP32 breaks the tie
differently: one family, two USB protocols. Both are "USB" to the operator, both ride the same
`Periphery.Usb` spoke, so DEC-001's "one transport spoke per package" is satisfied by a single
package. Splitting into `.Usb` and `.UsbDfu` would put a protocol name back in the leaf, which is
the exact noise DEC-002 exists to prevent.

**What this costs.** The `.Usb` package holds two programmers with almost nothing in common — one
built on `Periphery.Bootloader.Dfu`, one on the ESP32 family core. That is a genuinely lumpy
package. Accepted because the alternative misnames the taxonomy, and because a consumer flashing
an S3 may legitimately need both without knowing which mode the board is in.

---

## Decision 4 - Flash stub-free, and the binding reason is the license, not the dependency count

**Decision.** Drive the ROM loader directly. Never upload a flasher stub.

**Why.** The first draft of this reasoning was dependency hygiene — a vendored binary blob is a
third-party runtime artifact in everything but the `PackageReference` sense, which is what
[ADR-0024](../../../adr/0024-extension-package-pattern.md) exists to prevent. That argument is
real but it is not the binding one. `esptool`'s `flasher_stub` binaries are **GPL-2.0**. Periphery
ships under PolyForm Small Business 1.0.0. Shipping those blobs in a Periphery NuGet package is a
license violation, not a taste question. Decision 5 generalises this.

`esptool` supports `--no-stub` on every ESP32-family chip for `write_flash`, so this is a
supported mode of the ROM loader rather than a workaround.

**What this costs.** Throughput, and whatever ROM-loader quirks the stub papers over. On the
USB-Serial-JTAG path the penalty is smaller than the UART case suggests — there is no 115200-baud
ceiling, so only the ROM's per-block overhead remains. Unmeasured; see below.

**The escape hatch, if it is ever needed,** is a permissively-licensed stub, built from
Apache-2.0 sources or written in-house. It is not `esptool`'s.

---

## Decision 5 - Implement from the published protocol spec; `esp-serial-flasher` is the only code reference; `esptool` is off-limits

**Decision.** Four rules, recorded in the spec as RULE-L1 through RULE-L4:

- Implement from [Espressif's per-chip serial-protocol documentation][serial-protocol], and
  cross-check against [`espressif/esp-serial-flasher`][esp-serial-flasher] (**Apache-2.0**) only.
- No GPL artifact ships in a Periphery package (this is Decision 4's general form).
- Capture test vectors from the wire, not from source. Running `esptool` against real hardware and
  recording SLIP frames produces facts about the protocol; GPL-2.0 governs the program, not the
  bytes it puts on a wire. Transcribing its `COMMAND_*` tables into C# does not have that defence.

- Apache-2.0 attribution is mandatory in the change that introduces copied or adapted source
  (RULE-L4, below), not a judgment made later.

Chip-table constants — magic register values, flash offsets, memory maps — come from the Technical
Reference Manuals, which is where they are documented as facts.

**Why.** Periphery is PolyForm Small Business 1.0.0, which is GPL-incompatible.
[`espressif/esptool`][esptool] is **GPL-2.0** (verified against its `LICENSE`), and it is the
reference every engineer reaches for first. Writing this rule down is the only thing that stops
someone opening it in good faith, six months from now, to check a constant.

The rule costs nothing real. Espressif publishes the protocol *as a specification* so third
parties can implement it, and ships an Apache-2.0 implementation of the same protocol. There is a
compliant source for every fact the implementation needs.

**What this costs.** Slower going in the places where `esptool`'s source would have answered a
question in thirty seconds — chip-specific quirks, retry heuristics, the undocumented edges. Some
of those will have to be recovered by experiment on hardware instead. That is the price of the
license, and it is worth naming honestly rather than discovering during review.

**Attribution is immediate, not deferred.** An earlier draft of this decision said a
`THIRD-PARTY-NOTICES` entry was needed only if the implementation "tracked `esp-serial-flasher`
substantially," decided once the core existed. That is wrong, and it is wrong in the direction
that ships unattributed code: Apache-2.0 §4's conditions attach on redistribution of the
licensed material, not on a later judgment about how much of it there was. A developer can
adapt one helper, ship it, and have the review conclude it was not substantial — and the
obligation was breached the moment the package was published.

RULE-L4 replaces it. The PR that introduces any copied or adapted source records the exact
upstream commit, preserves the Apache `LICENSE` and `NOTICE` material, and adds the
`THIRD-PARTY-NOTICES` entry **in the same change**. The only exemption is an implementation
written from the published spec alone, and it has to be claimed explicitly in the PR to count.
When in doubt, attribute — the cost is a paragraph.

---

## Decision 6 - Extract `Periphery.Bootloader.Dfu` now, and ship the DFU path before the protocol core

**Decision.** Phase 1 extracts the generic DFU 1.1 layer out of
[`Periphery.Bootloader.Stm32.Usb`](../../../../src/Periphery.Bootloader.Stm32.Usb/) and ships
`Esp32UsbDfuProvider` on top of it — before the esptool protocol core is written.

**Why.** [ADR-0061](../../../adr/0061-firmware-flashing-platform.md) DEC-005 deliberately kept the
generic DFU layer as an internal seam and named ESP32-S2/S3 USB DFU as the second consumer that
would earn the extraction. That consumer has arrived; the rule of two is satisfied on its own
terms, not by anticipation.

It also happens to be the shortest path to a first working ESP32 flash: no new transport, no new
protocol core, and the STM32 package's existing tests are the regression gate for the refactor.
Shipping something that flashes a real S3 early is worth more than shipping the larger, more
general piece first.

**What this costs.** Much less reach than "first working flash" suggests, and the extraction is
the only half that is actually safe to schedule. DFU covers S2/S3/P4 only, in a mode the board has
to be strapped into (GPIO0 low, pulse RESET); on Windows Espressif's own guide says the WinUSB
driver "has to be installed for the device to work properly"; `Periphery.Usb` has no macOS backend;
the payload container is unverified; and USB-OTG re-enumerates mid-sequence, so the programmer
cannot keep its pre-reset handle.

Two drafts of this ADR overstated that in the same direction. The first called path C "available
today"; the second narrowed it to Linux but still read as available, while the same table admitted
the payload container was unverified. Reaching a device is not flashing it. What phase 1 can
promise is the DFU extraction — pure refactor, gated by the STM32 package's existing tests — plus
an *attempt* at the DFU path that is itself gated on measuring the payload container and the
reset-and-reopen sequence first.

---

## Decision 7 - A `0x303A` id identifies a family and a mode, not a part, so the USB providers ship `Probe`

**Decision.** Every ESP32 provider declares `IdentificationMode.Probe`, USB included. `Passive`
is available to a provider only once its match is a verified, specific id set, and no ESP32 path
has one today. The chip resolved from the magic register after opening is a **gate**: where a
caller has named a target chip, a mismatch aborts before any write.

**Why — and what the first draft of this decision got wrong.** The draft said modern ESP32s are
`Passive` because "the VID/PID *is* the target, exactly as `0483:DF11` is an STM32 in DFU." That
overstates what the id carries. `303A:1001` is the **USB-Serial-JTAG peripheral**, shared across
C3, S3, C6, H2, and P4; Espressif's own udev rule for DFU mode is the range `303a:00??`. So the
id says "an Espressif chip in ROM download mode on this peripheral" — a family and a mode. It
does not say which part, which means it does not carry the flash geometry, the status-byte
length, the image offsets, or the security state.

Worse, the draft contradicted itself: Decision 8 and the spec's requirements both make a
magic-register read mandatory *before writing*, which is an admission that opening the device is
what establishes identity. A design cannot claim passive identification and simultaneously
require an active read to know what it is talking to.

The bridge contrast survives and is still worth stating — a CP210x id tells you nothing at all,
where `0x303A` tells you the family and the mode. That is a better starting point. It is not
part identity, and the gap between those two is exactly where an unattended flash goes wrong.

**What this costs.** Nothing that matters yet. Every flash in phases 1–3 is an explicit operator
action, and `Probe` is precisely the mode meaning "confirm identity before writing." Autoflash
eligibility becomes a separate decision, taken on measured ids rather than inferred from the
chips having native USB — recorded as OQ-9 in [`spec.md`](spec.md) and amended here when the
measurement exists.

---

## Decision 8 - Refuse encrypted and secure-boot targets; do not guard the bootloader region

**Decision.** `GET_SECURITY_INFO` is read during identification. If flash encryption or secure
boot is enabled, the programmer fails **before writing a byte** — and it fails identically when
the query is unsupported, times out, or does not decode. An undetermined security state is
refused exactly like an enabled one. There is no general `--force` that reaches this gate; an
override, if one is ever justified, is a distinct narrowly-named flag. Writing the second-stage
bootloader region gets no such guard. An unrecognised chip magic is a hard failure, not a
fallback to defaults.

**Why.** The two halves are asymmetric, and getting the asymmetry right is the whole point.

Writing plaintext to a device with flash encryption enabled produces an unbootable device that the
ROM loader cannot repair from outside — that is a real brick, and it is the ESP32 analogue of the
STM32 Read-Unprotect operation ADR-0061 singled out for a guard. A guard that reads "refuse when
the answer is yes" leaves the far more likely failure — an older ROM where the query is
unsupported, or a decode that does not match the table — running straight through it into a
plaintext write. Refusing the unknown is the only version of this gate that holds, and keeping
the override out of the general force flag is what stops the gate being routed around by an
operator working past an unrelated problem.

Writing a bad application image, or even a bad second-stage bootloader, is **recoverable**. The
ROM loader cannot be overwritten and is entered by strapping pin or by the USB peripheral, so the
device always comes back. Guarding it would add friction to the one operation that is genuinely
safe, and friction on a safe operation trains operators to reach for `--force` on the dangerous one.

Refusing an unknown chip follows from the chip table being load-bearing: it supplies the
status-byte length and the flash parameters. Guessing them means writing wrong bytes to a device
we cannot describe, which is the failure mode the whole platform's brick-guards exist to prevent.

---

## Decision 9 - Multi-offset image loading lands in `Periphery.Firmware`, not in the ESP32 flasher

**Decision.** Add a multi-file load entry point to `Periphery.Firmware` producing one
`FirmwareImage` from `(path, offset)` pairs, plus `FirmwareFormat.EspApplication` with the `0xE9`
content sniff. The CLI grows a repeatable `--file <path>@<offset>`. The ESP32 flasher consumes
addressed segments and knows nothing about files.

**Why.** An ESP32 flash is three artifacts at three fixed offsets (bootloader at `0x0` or `0x1000`,
partition table at `0x8000`, application at `0x10000`).
[`FirmwareImage`](../../../../src/Periphery.Firmware/FirmwareImage.cs) is already a sparse
`address -> bytes` model, so it represents this correctly today; the gap is only at the load
boundary, where [`FirmwarePayload.Load`](../../../../src/Periphery.Firmware/FirmwarePayload.cs)
takes one file and one base address. That is a general limitation of the image layer, not an ESP32
quirk — the [image-formats spec](../image-formats/spec.md) already anticipates the `0xE9` magic
with nothing consuming it. Solving it in the flasher would put file handling in a protocol package
and leave the next family to solve it again.

It also makes phase 0 independently shippable and independently useful, with no ESP32 code in it.

---

## What is not yet measured

[CONTRIBUTING](../../../../CONTRIBUTING.md) asks that claims be measured rather than assumed, and
that a hypothesis be labelled as one. Nothing in this ADR has touched hardware. The load-bearing
unknowns, in the order they should be resolved:

- **Windows driver binding on USB-Serial-JTAG.** Windows binds the peripheral to `usbser.sys`;
  `Periphery.Usb`'s Windows backend is WinUSB-direct and cannot claim a device another class
  driver owns. Linux is expected to work because
  [`LibUsbBackend`](../../../../src/Periphery.Usb/Linux/LibUsbBackend.cs) enables
  `libusb_set_auto_detach_kernel_driver`. Both halves are read off the source, not observed. If
  Windows needs Zadig-style rebinding, the USB-Serial-JTAG path is Linux-only in practice.
  **This single measurement decides the shape of the largest phase.**
- **The specific S2/S3 DFU-mode PIDs.** *Partly answered from documentation, and the answer is
  worse than assumed.* Espressif's DFU guide gives the udev rule as the range `303a:00??`, not a
  single id, and says outright that on Windows the WinUSB driver "has to be installed for the
  device to work properly" — their driver package or Zadig. So the DFU path inherits the driver
  problem above and there is no WCID auto-binding to hope for. What is still unmeasured is which
  PID each part actually presents, which is what a registry match needs and what Decision 7 holds
  `Probe` until.
- **Reset-into-download sequences** per USB path, including the USB-OTG re-enumeration mid-sequence
  that the shell has to survive.
- **Stub-free throughput** on USB-Serial-JTAG. Decision 4's cost is argued, not timed.
- **Whether the ROM loader's compressed commands are worth using** without a stub.

The recommended order is: measure the first item before committing to the phasing in
[`spec.md`](spec.md).

[serial-protocol]: https://docs.espressif.com/projects/esptool/en/latest/esp32s3/advanced-topics/serial-protocol.html
[esp-serial-flasher]: https://github.com/espressif/esp-serial-flasher
[esptool]: https://github.com/espressif/esptool
