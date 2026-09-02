# Feature Spec: ESP32 flashing (esptool ROM loader + USB DFU)

<!--
Authoritative, LIVING spec for ESP32-family flashing in the Periphery.Bootloader platform.
Read this before editing any code in the feature's scope; the "Affected Layers" table names
the projects to touch. The "how / why" decisions live in the append-only sibling [`adr.md`](adr.md);
this file is the "what" and is rewritten as the feature evolves.
-->

## Status

**Proposed** — no code. [ADR-0061](../../../adr/0061-firmware-flashing-platform.md) reserves
`Periphery.Bootloader.Esp32.Serial` on the roadmap (DEC-007) and names ESP32-S2/S3 USB DFU as
the second DFU consumer that triggers extracting `Periphery.Bootloader.Dfu` (DEC-005). This
spec is the feasibility answer to "can we do the modern chips now," and the answer is yes:
the modern parts have native USB, which decouples them from the unbuilt `Periphery.Serial`.

| Field        | Value                                      |
|--------------|--------------------------------------------|
| Author       | Charles Lee                                |
| Created      | 2026-09-02                                 |
| Last Updated | 2026-09-02                                 |
| Project      | `Periphery.Bootloader.Esp32`               |
| Branch       | `claude/esp32-flasher-support-9accc1`      |

---

## Purpose

Flash ESP32-family targets — ESP32-S2, S3, C3, C6, H2, P4 — through the existing FlashAnything
pipeline, with the same contract, safety gates, and front-ends as the STM32 and EFM8 flashers.

The scope word is **modern**. The classic ESP32 and ESP8266 reach the host only through a
UART bridge, so they wait on `Periphery.Serial`. Every chip from the S2 onward has a USB
peripheral on-die, and that peripheral is reachable through `Periphery.Usb` today.

---

## The organizing insight: one protocol, three transports

ESP32 support is not one flasher. The **protocol** is one thing — Espressif's ROM serial
loader, the wire protocol `esptool` speaks, publicly specified per-chip in
[Espressif's serial-protocol documentation][serial-protocol]. The **transport** is three
things, and they have completely different availability in this codebase.

The last column is deliberately **transport reachability** — whether Periphery can open and talk
to the device at all — and not "you can flash this today." **No path has a verified end-to-end
flash.** Reaching a device is necessary and nowhere near sufficient: path C still has an unverified
payload container (OQ-10) and path B and C both have unverified reset-and-reopen behaviour (OQ-6).

| Path | Chips | Protocol | Periphery transport | Identification | Transport reachable |
|---|---|---|---|---|---|
| **A — UART bridge** | all, incl. classic ESP32 / ESP8266 | esptool ROM, SLIP over UART | `Periphery.Serial` | `Probe` | **No.** No `Periphery.Serial` at all ([ADR-0062](../../../adr/0062-periphery-serial-backend-provider.md) is Proposed, no code) |
| **B — USB-Serial-JTAG** | C3, S3, C6, H2, P4 | esptool ROM, SLIP over CDC bulk endpoints | `Periphery.Usb` bulk | family + mode (OQ-9) | **Expected on Linux**, unmeasured. Windows is OQ-1; no macOS backend (OQ-5) |
| **C — USB-OTG DFU** | S2, S3, P4 | USB DFU 1.1; payload container **unverified** (OQ-10) | `Periphery.Usb` control | family + mode (OQ-9) | **Expected on Linux**, unmeasured. Windows needs an operator-installed WinUSB driver ([Espressif's DFU guide][esp-dfu]); no macOS backend (OQ-5) |

Three consequences fall out of that table.

1. **Paths B and C need no new dependency and no new transport package** — which is a statement
   about what is *missing*, not about what is *proven*. `Periphery.Usb`
   already exposes `ControlTransferAsync`, `BulkReadAsync`, and `BulkWriteAsync`
   ([`UsbDevice.cs`](../../../../src/Periphery.Usb/UsbDevice.cs)), so nothing has to be built
   before trying. Whether it works is [what has not been measured](#open-questions).
2. **Paths B and C identify a family and a mode without touching the device** — Espressif VID
   `0x303A` in ROM download mode, not a CP210x that says nothing about what is behind it. That
   is a materially better starting point than the bridge case the
   [autoflash spec](../autoflash/spec.md) excludes, but it is **not** part identity: `303A:1001`
   is shared across C3/S3/C6/H2/P4. See [Identification model](#identification-model) for what
   that does and does not license. ADR-0061's roadmap table filed ESP32 under the serial lane;
   for the modern parts that is no longer the right lane.
3. **Path C is the `Periphery.Bootloader.Dfu` extraction DEC-005 was waiting for**, and the
   shortest route to *attempting* a flash, because the generic DFU layer already exists inside
   [`Periphery.Bootloader.Stm32.Usb`](../../../../src/Periphery.Bootloader.Stm32.Usb/). Shortest
   is not short: DFU mode is entered by strapping GPIO0 and holding RESET, Windows needs a driver
   the operator installs, the payload container is unverified (OQ-10), and USB-OTG re-enumerates
   mid-sequence so the programmer cannot keep its pre-reset handle (OQ-6). Phase 1 is a beachhead
   whose landing is not yet confirmed.

---

## Licensing (read before writing a line of protocol code)

Periphery is licensed **PolyForm Small Business 1.0.0**. That is incompatible with GPL, and
the ESP32 tooling ecosystem is split across three licenses. Getting this wrong is not a style
problem; it is a relicensing problem discovered after the fact.

| Source | License | Use here |
|---|---|---|
| [Espressif serial-protocol docs][serial-protocol] | documentation | **Primary specification.** Implement from this. |
| Chip Technical Reference Manuals / datasheets | documentation | **Primary source for constants** — magic register values, flash offsets, memory maps. |
| [`espressif/esp-serial-flasher`][esp-serial-flasher] | **Apache-2.0** | **Permitted reference implementation**, under RULE-L4. Apache-2.0 permits redistribution in a PolyForm-licensed work; its §4 notice conditions are **not** optional. |
| [`espressif/esptool`][esptool] | **GPL-2.0** | **Do not read as a source for code.** Do not copy, port, transliterate, or derive constants from its Python source. |
| `esptool`'s `flasher_stub` binaries | **GPL-2.0** | **Never vendored.** See [Stub policy](#stub-policy). |
| Packets captured from a running `esptool` | not covered by GPL | **Permitted as test vectors.** GPL-2.0 governs the program, not the bytes it puts on a wire. |
| USB DFU 1.1 class spec (USB-IF) | open specification | Already implemented in-house for STM32; path C reuses it. `dfu-util` is GPL and is not consulted. |

Three rules follow.

- **RULE-L1 — implement from the published protocol spec, and cross-check against
  `esp-serial-flasher` only.** `esptool` is the ubiquitous reference and it is the one you
  must not open. The Apache-2.0 alternative is maintained by the same vendor and covers the
  same protocol, so there is no practical loss.
- **RULE-L2 — no GPL artifact ships in a Periphery package.** This makes the
  [Stub policy](#stub-policy) a licensing requirement rather than a preference.
- **RULE-L3 — capture test vectors from the wire, not from source.** Running `esptool`
  against real hardware and recording the SLIP frames produces facts about the protocol.
  Transcribing its `COMMAND_*` tables into C# does not.
- **RULE-L4 — Apache-2.0 attribution is mandatory the moment any source is copied or adapted,
  not a judgment call made later.** If a single helper, structure, or modified file comes from
  `esp-serial-flasher`, the PR that introduces it records the exact upstream commit, preserves
  the Apache `LICENSE` and `NOTICE` material, and adds the `THIRD-PARTY-NOTICES` entry in the
  same change. Apache-2.0 §4's conditions attach on redistribution; there is no
  "substantial enough" threshold to reason about. The **only** exemption is an implementation
  written from the published spec alone, and that exemption has to be stated in the PR to
  count. When in doubt, attribute.

Nothing about the *protocol itself* is encumbered. Espressif publishes it as a specification
precisely so third parties can implement it, and ships an Apache-2.0 implementation of it.

[serial-protocol]: https://docs.espressif.com/projects/esptool/en/latest/esp32s3/advanced-topics/serial-protocol.html
[esp-serial-flasher]: https://github.com/espressif/esp-serial-flasher
[esptool]: https://github.com/espressif/esptool
[esp-dfu]: https://docs.espressif.com/projects/esp-idf/en/stable/esp32s2/api-guides/dfu.html

---

## Dependencies / Prerequisites

| Depends on | Why | State |
|---|---|---|
| The bootloader contract (`IFirmwareProgrammer` / `IBootloaderProvider` / `BootloaderRegistry`) | ESP32 flashers plug into the same dispatcher as STM32 and EFM8. | **Have** |
| `Periphery.Usb` control + bulk transfers | The transport for paths B and C. | **Have** |
| `Periphery.Firmware` `FirmwareImage` addressed segments | An ESP flash is `offset -> bytes`; the sparse segment model already fits. | **Have** |
| Generic DFU 1.1 layer extracted to `Periphery.Bootloader.Dfu` | Path C reuses it rather than forking DNLOAD/GETSTATUS/state handling. | **New** (phase 1) |
| Multi-offset image loading | An ESP flash is three artifacts at three fixed offsets, not one file at one base. | **New** (phase 0) |
| `Periphery.Serial` ([ADR-0062](../../../adr/0062-periphery-serial-backend-provider.md)) | Path A only. | **Missing** — blocks path A entirely |
| A macOS `Periphery.Usb` backend ([ADR-0038](../../../adr/0038-periphery-usb.md)) | Paths B and C on macOS. | **Missing** — `Periphery.Usb` ships Windows (WinUSB) and Linux (libusb) only |

---

## Affected Layers

| Project | Change Type |
|---|---|
| `Periphery.Firmware` | **Small:** `FirmwareFormat.EspApplication` (`.bin` with the `0xE9` image magic) + its content sniff; a multi-offset load entry point (`FirmwarePayload.Load` currently takes one file and one base address). |
| `Periphery.Bootloader.Dfu` | **New:** the generic DFU 1.1 client extracted from `Periphery.Bootloader.Stm32.Usb` — `DfuState`, `DfuStatusCode`, `DfuStatus`, `DfuRequest`, `DfuFunctionalDescriptor`, the transport seam, and the GETSTATUS poll loop. ST's command layer stays behind in the STM32 package. |
| `Periphery.Bootloader.Stm32.Usb` | **Refactor, no behaviour change:** consume `Periphery.Bootloader.Dfu` instead of its private copy. Its existing tests are the regression gate. |
| `Periphery.Bootloader.Esp32` | **New:** the pure esptool-ROM protocol core — SLIP codec, command union, response decode, chip table, flash planner — plus the `IEsp32Transport` seam. Transport-free (DEC-002 family core). |
| `Periphery.Bootloader.Esp32.Usb` | **New:** both USB providers. `UsbSerialJtagTransport` (bulk, path B) and an `Esp32DfuProgrammer` over `Periphery.Bootloader.Dfu` (path C), disambiguated by PID ([`adr.md`](adr.md) Decision 3). Both ship `Probe` until OQ-1/OQ-2 are measured. |
| `Periphery.Bootloader.Esp32.Serial` | **Deferred:** a `SerialEsp32Transport` over `Periphery.Serial`. Nothing to build until ADR-0062 ships. |
| `Periphery.FlashAnything.Cli` / `.Gui` | **Small:** register the new providers (alongside [`Program.cs:25`](../../../../src/Periphery.FlashAnything.Cli/Program.cs)); a repeatable `--file <path>@<offset>` argument. |
| `tests/Periphery.Bootloader.Esp32.Tests` | **New:** SLIP round-trips, command golden bytes, response decode across status-byte lengths, planner assertions, and a `FakeEsp32Transport` scripting sync/retry/error sequences. Zero hardware. |
| `tests/Periphery.Bootloader.Dfu.Tests` | **New:** the extracted generic layer's tests, moved out of the STM32 test project. |
| `THIRD-PARTY-NOTICES` | **New or amended:** Apache-2.0 attribution if any `esp-serial-flasher` derivation is judged substantial enough to warrant it (RULE-L1). |

---

## Requirements

- [ ] **Flash an ESP32-S3 over USB-Serial-JTAG** end to end: detect, sync, identify chip,
      write, MD5-verify, reset into the application.
- [ ] **Flash an ESP32-S2 or S3 in USB-OTG DFU mode** through the extracted generic DFU client.
- [ ] **Run stub-free.** The ROM loader alone, no downloaded stub. See [Stub policy](#stub-policy).
- [ ] **Identify the chip before writing.** Magic-register read (and `GET_SECURITY_INFO` where
      supported) resolves the part; the resolved chip supplies the status-byte length, the flash
      parameters, and the bootloader offset. A chip the table does not know is refused, not guessed.
- [ ] **Multi-offset images.** Accept the three-artifact layout (second-stage bootloader,
      partition table at `0x8000`, application at `0x10000`) as well as a single merged binary.
- [ ] **Verify by MD5.** `SPI_FLASH_MD5` implements `FlashOptions.Verify` natively — no read-back.
- [ ] **Refuse an encrypted or secure-boot target, and refuse equally when the security state
      cannot be determined.** See [Safety rules](#safety-rules).
- [ ] **Report progress** through the existing `FlashProgress` / `FlashPhase` contract.
- [ ] **Explain a driver-binding failure.** On Windows, "this device is bound to `usbser.sys`
      and cannot be claimed" must be the message, not an opaque open error.
- [ ] **Ship the USB providers as `Probe`.** No `Passive` declaration until the ids are
      measured; see [Identification model](#identification-model).
- [ ] **Attribute Apache-2.0 material in the change that introduces it** (RULE-L4), not in a
      later review pass.
- [ ] **No new runtime dependency, and no GPL artifact.** No third-party package, no vendored
      binary blob ([ADR-0024](../../../adr/0024-extension-package-pattern.md), RULE-L2).
- [ ] **AOT-clean** under `PublishAot`, like every other `Periphery.Bootloader.*` package.

---

## Package layout

Per [ADR-0061](../../../adr/0061-firmware-flashing-platform.md) DEC-002: a family core exists
**iff** the family has shared protocol code. ESP32 is the "one protocol, many transports" case
— paths A and B speak byte-identical esptool ROM commands and differ only in framing substrate
— so `Periphery.Bootloader.Esp32` is a **required** family core, structurally the same as
`Periphery.Bootloader.Efm8`.

```
Periphery.Bootloader
  ├─ Periphery.Bootloader.Dfu              generic DFU 1.1          [extracted, phase 1]
  ├─ Periphery.Bootloader.Stm32.Usb        ST commands on .Dfu      [refactored, phase 1]
  └─ Periphery.Bootloader.Esp32            esptool ROM core + seam  [phase 2]
       ├─ Periphery.Bootloader.Esp32.Usb     USB-Serial-JTAG (bulk) + USB-OTG DFU   [phase 2/1]
       └─ Periphery.Bootloader.Esp32.Serial  UART bridge            [phase 4, blocked on ADR-0062]
```

Each leaf obeys DEC-001: one transport spoke (`Periphery.Usb` **or** `Periphery.Serial`), the
`Periphery.Firmware` foundation, the `Periphery.Bootloader` root, and its own family core.

---

## Architecture (functional core / imperative shell — [ADR-0052](../../../adr/0052-periphery-treehopper-pure-core.md))

Mirrors [`Periphery.Bootloader.Stm32.Usb`](../../../../src/Periphery.Bootloader.Stm32.Usb/)
exactly: 804 lines of source and 338 of tests there is a fair estimate of the shape and size here.

### Pure core — `Periphery.Bootloader.Esp32` (no IO, no clock, no `Task`)

- **`SlipCodec`** — `Encode` wraps a payload in `0xC0` delimiters, replacing `0xC0` with
  `0xDB 0xDC` and `0xDB` with `0xDB 0xDD`; `Decode` is an incremental frame accumulator, a
  total state machine over fed bytes. Byte-exact testable, no transport.
- **`Esp32Command`** — a closed union, each case with an `Encode()` producing the request
  packet (byte 0 direction `0x00`, byte 1 opcode, bytes 2–3 `u16` size, bytes 4–7 checksum,
  bytes 8+ payload): `Sync` (`0x08`), `ReadReg` (`0x0A`), `WriteReg` (`0x09`), `SpiAttach`,
  `SpiSetParams`, `FlashBegin` (`0x02`), `FlashData` (`0x03`), `FlashEnd` (`0x04`),
  `FlashDeflBegin` (`0x10`), `FlashDeflData` (`0x11`), `FlashDeflEnd` (`0x12`),
  `ChangeBaudRate`, `SpiFlashMd5`, `GetSecurityInfo`.
- **`Esp32Response`** — `Decode(frame, statusByteLength)`. Response layout is byte 0 direction
  `0x01`, byte 1 the echoed command, bytes 2–3 size, bytes 4–7 value, bytes 8+ payload ending
  in status. The trailing status field is **not a constant**: the stub loader uses two bytes,
  while the ESP32-S3 ROM uses four of which only the first two carry status. It is therefore a
  property of the resolved chip, threaded in by the caller.
- **`Esp32Chip`** — the chip table: magic-register value, human name, status-byte length,
  flash-block size, the second-stage bootloader offset (`0x0` on C3/S3/C6/H2, `0x1000` on the
  classic part and S2), and the reset style the transport must apply. Every constant sourced
  per RULE-L1.
- **`Esp32FlashPlan.Plan(FirmwareImage, Esp32Chip, FlashOptions) -> ImmutableArray<Esp32Step>`**
  — the ordered step list, exactly as `Stm32DfuPlan` does.
- **`Esp32Step`** — closed union: `Sync`, `Attach`, `SetFlashParams`, `BeginRegion(offset, size)`,
  `WriteBlock(sequence, data)`, `EndRegion`, `VerifyMd5(offset, size, expected)`, `Reset`.

Compression, if enabled, is raw deflate via `System.IO.Compression.ZLibStream` — BCL, AOT-clean,
no dependency. It is a pure transform and belongs in the planner, not the shell.

### Transport seam — `IEsp32Transport`

At the protocol-request grain, so the shell is testable against a fake:

- `WriteFrameAsync(ReadOnlyMemory<byte>, CancellationToken)` / `ReadFrameAsync(CancellationToken)`
- `EnterDownloadModeAsync(CancellationToken)` — the reset dance, which differs per path and is
  therefore the transport's business, not the core's
- `SetBaudRateAsync(int, CancellationToken)` — a no-op on the USB paths, where baud is meaningless
- `FlushAsync(CancellationToken)`

### Imperative shell — `Esp32Programmer : IFirmwareProgrammer`

Owns the handle, the clock, and every retry: enter download mode, the `SYNC` retry loop
(the ROM loader needs several attempts and tolerates junk in the buffer), chip identification,
executing the plan, MD5 verification, reset out. Reports `FlashProgress`. Throws
`Esp32BootloaderException : DeviceEnumerationException`, per ADR-0024.

---

## Public API (proposed)

```csharp
namespace Periphery.Bootloader.Esp32;

// Pure core
internal static class SlipCodec
{
    public static byte[] Encode(ReadOnlySpan<byte> payload);
    public static SlipDecoder.Result Feed(ref SlipDecoder state, ReadOnlySpan<byte> input);
}

public sealed record Esp32Chip(
    uint MagicValue,
    string Name,
    int StatusByteLength,
    int FlashBlockSize,
    uint BootloaderOffset,
    Esp32ResetStyle ResetStyle);

internal abstract record Esp32Command
{
    public abstract byte[] Encode();
    public sealed record Sync : Esp32Command;
    public sealed record ReadReg(uint Address) : Esp32Command;
    public sealed record FlashBegin(uint EraseSize, uint BlockCount, uint BlockSize, uint Offset) : Esp32Command;
    public sealed record FlashData(ReadOnlyMemory<byte> Data, uint Sequence) : Esp32Command;
    public sealed record SpiFlashMd5(uint Offset, uint Size) : Esp32Command;
    // ... SpiAttach, SpiSetParams, FlashEnd, FlashDefl*, ChangeBaudRate, GetSecurityInfo
}

// Shell
public sealed class Esp32Programmer : IFirmwareProgrammer
{
    public static Task<Esp32Programmer> OpenAsync(IEsp32Transport transport, DeviceInfo device, CancellationToken ct = default);
    public ImmutableArray<FirmwareFormat> AcceptedFormats { get; } // RawBinary, EspApplication, Elf
}

// Providers, both in Periphery.Bootloader.Esp32.Usb.
// Probe, not Passive: 303A:1001 is shared across C3/S3/C6/H2/P4 and the DFU ids are a
// range (303a:00??), so neither match is part identity yet. See Identification model.
public sealed class Esp32UsbSerialJtagProvider : IBootloaderProvider  // 303A:1001 - family + mode
{
    public IdentificationMode Identification => IdentificationMode.Probe;
}

public sealed class Esp32UsbDfuProvider : IBootloaderProvider          // 303A:00?? - unverified, OQ-2
{
    public IdentificationMode Identification => IdentificationMode.Probe;
}
```

---

## Stub policy

`esptool` uploads a compiled stub loader into RAM before flashing. **This spec does not.**

**Why not — licensing first.** The stubs in `esptool`'s `flasher_stub` are GPL-2.0. Vendoring
them into a PolyForm-licensed NuGet package is not a dependency-hygiene question, it is a
license violation (RULE-L2). Building equivalents from source at our build time would drag an
Xtensa and RISC-V toolchain into CI to solve a problem we do not have to have.

**What it costs.** Throughput, and some ROM-loader quirks the stub papers over. On path B the
cost is smaller than it looks: USB-Serial-JTAG is not rate-limited by a 115200-baud UART, so
the ROM loader's per-block overhead is the only penalty. `esptool` supports `--no-stub` on
every ESP32-family chip for `write_flash`, so this is a supported mode, not a hack.

**Revisit when** measured flash times on real hardware are unacceptable — and note that the
only clean escape is an Apache-2.0 or permissively-licensed stub, not `esptool`'s.

---

## Image model: the multi-offset gap

An ESP32 flash is three artifacts at three fixed offsets:

| Artifact | Offset | Notes |
|---|---|---|
| Second-stage bootloader | `0x0` (C3/S3/C6/H2) or `0x1000` (classic, S2) | chip-dependent — from the `Esp32Chip` table |
| Partition table | `0x8000` | |
| Application | `0x10000` | `0xE9` image magic |

[`FirmwareImage`](../../../../src/Periphery.Firmware/FirmwareImage.cs) is a sparse
`address -> bytes` model, so it already represents this. The gap is at the **load** boundary:
[`FirmwarePayload.Load`](../../../../src/Periphery.Firmware/FirmwarePayload.cs) takes one file
and one base address.

Two things close it:

1. **A multi-file load entry point** producing one `FirmwareImage` from `(path, offset)` pairs,
   and a CLI surface for it — a repeatable `--file <path>@<offset>`. A single merged `.bin` at
   one offset keeps working unchanged.
2. **`FirmwareFormat.EspApplication`** with the `0xE9` content sniff, so the
   [image-formats](../image-formats/spec.md) brick-guard has something to check an ESP
   application against. That spec already anticipates the magic byte; nothing consumes it yet.

---

## Identification model

**A `0x303A` PID identifies a family and a mode, not a part.** `303A:1001` is the USB-Serial-JTAG
peripheral and is shared across C3, S3, C6, H2, and P4; Espressif's own udev rule for DFU mode is
the range `303a:00??`. What the VID/PID tells you without touching the device is "an Espressif
chip sitting in ROM download mode on this USB peripheral." It does not tell you the flash
geometry, the status-byte length, the image offsets, or the security state — which is exactly why
[Requirements](#requirements) makes a magic-register read mandatory before any write.

That is a real distinction from the bridge case, and a smaller one than the first draft of this
spec claimed. Three consequences:

- **The USB providers ship as `IdentificationMode.Probe`, not `Passive`, until the identities are
  measured.** A provider may declare `Passive` only once its match is a verified, specific id set,
  which OQ-1 and OQ-2 have not yet established for either path. `Probe` costs nothing today: every
  flash in phases 1–3 is an explicit operator action anyway, and `Probe` is precisely the mode
  that means "identity needs confirming before writing."
- **Autoflash eligibility is a separate, later decision** with its own evidence, not a corollary
  of the chips having native USB. Revisit only when the ids are measured, amending
  [`adr.md`](adr.md) Decision 7 with what was observed.
- **The chip resolved from the magic register is a gate, not metadata.** Where a caller has named
  a target chip, a mismatch aborts before any write. That is what stops a family-level match from
  becoming a family-level flash.

The classic ESP32 behind a CP210x/CH340/FTDI bridge is `Probe` for the original reason: the
VID/PID names the bridge and says nothing about what is behind it. The modern parts reach the
same mode by a shorter route, not by a different rule.

---

## Safety rules

Flashing bricks things. The ESP32 case has one genuinely dangerous mode and one reassuring one.

1. **Refuse an encrypted or secure-boot target, and fail closed when the answer is unavailable.**
   `GET_SECURITY_INFO` reports flash encryption and secure-boot state. Writing plaintext to a
   device with flash encryption enabled produces an unbootable device that the ROM loader
   cannot repair from the outside. The programmer **fails before writing a byte** when either
   is set — **and equally when the query is unsupported, times out, or does not decode.** "We
   could not determine the security state" is refused exactly like "the security state is on";
   an unknown is never read as a no. There is deliberately **no general `--force`** reaching
   this gate: any override is a distinct, narrowly-named flag added only when a real use case
   demands it. This is the ESP32 analogue of the STM32 Read-Unprotect guard.
2. **Download mode is in ROM, so an app-flash mistake is recoverable.** The ROM loader cannot
   be overwritten and is entered by strapping pin or by the USB peripheral, so a bad application
   image — or a bad second-stage bootloader — is re-flashable. Writing the bootloader region
   therefore does **not** need the guard that a fusible operation would.
3. **Refuse an unknown chip.** If the magic register does not resolve to a table entry, stop.
   Guessing the status-byte length or the flash parameters means writing wrong bytes to a
   device we cannot describe.
4. **Verify by MD5 by default**, honouring `FlashOptions.Verify`.
5. **Refuse a format not in `AcceptedFormats`** before any device IO, per the existing gate.

---

## Testing

| Layer | What | Hardware |
|---|---|---|
| Pure | SLIP encode/decode round-trips, including escaped `0xC0`/`0xDB` payloads and split feeds | none |
| Pure | Command golden bytes, compared against frames captured from the wire (RULE-L3) | none |
| Pure | Response decode across each status-byte length in the chip table | none |
| Pure | `Esp32FlashPlan` assertions: block sequencing, region boundaries, erase sizing, MD5 steps | none |
| Shell + fake | `FakeEsp32Transport` scripts: sync-after-N-retries, an error status mid-write, a truncated frame, a chip that fails to identify | none |
| Hardware | ESP32-C3 and S3 over USB-Serial-JTAG; S2 or S3 in DFU. Flash, verify, boot | rig |
| AOT | `PublishAot` gate on every new package | none |

The golden-byte comparison is what makes the pure core trustworthy. Capture the frames once
from real hardware, commit them as test vectors — never transcribe them from `esptool` source.

---

## Implementation plan

Independently shippable phases, no time estimates.

- **Phase 0 — image model.** `FirmwareFormat.EspApplication` + `0xE9` sniff; multi-offset load;
  the `--file <path>@<offset>` CLI surface. Useful on its own, no ESP32 code required.
- **Phase 1 — DFU extraction, then an attempt at the DFU path.** Extract
  `Periphery.Bootloader.Dfu` from the STM32 package (its existing tests are the regression gate) —
  that half is pure refactor and carries no hardware risk. Then `Esp32UsbDfuProvider` for S2/S3,
  which is **gated on OQ-10 and OQ-6**: settle the payload container and the reset-and-reopen
  sequence before writing the programmer, or it will be built against a guess. It pays off
  DEC-005 as designed and needs no new transport or protocol core.
- **Phase 2 — the protocol core.** `Periphery.Bootloader.Esp32`: SLIP, commands, responses,
  chip table, planner, seam. Entirely pure, entirely testable, valuable regardless of which
  transport lands. This is the bulk of the work.
- **Phase 3 — USB-Serial-JTAG.** `Esp32UsbSerialJtagProvider` over `Periphery.Usb` bulk. Linux
  first; Windows gated on OQ-1.
- **Phase 4 — serial.** `Periphery.Bootloader.Esp32.Serial` once `Periphery.Serial` exists.
  Covers the classic parts. Blocked, and last.

Phases 0 through 3 need no new dependency, no `Periphery.Serial`, and no vendored blob.

---

## Open Questions

- **OQ-1 — Windows driver binding (the go/no-go for path B).** Windows binds USB-Serial-JTAG
  to `usbser.sys`. [`Periphery.Usb`'s Windows backend is WinUSB-direct](../../../../src/Periphery.Usb/Windows/WinUsbBackend.cs)
  and cannot claim a device owned by another class driver. Linux is fine —
  [`LibUsbBackend`](../../../../src/Periphery.Usb/Linux/LibUsbBackend.cs) enables
  `libusb_set_auto_detach_kernel_driver`. If Windows needs Zadig-style rebinding, path B is
  Linux-only in practice, because that is not something a fleet operator can be asked to do.
  **This is one afternoon on the bench with a C3 and an S3, and it decides the shape of phase 3.**
- **OQ-2 — the exact DFU-mode ids.** *Half answered.* Espressif's
  [DFU guide][esp-dfu] gives the udev rule as the **range** `303a:00??`, not a single PID, and
  states plainly that on Windows "the WinUSB driver is the recommended driver which has to be
  installed for the device to work properly" (their driver package or Zadig). So path C **does**
  carry OQ-1's driver problem, and there is no WCID auto-binding to hope for. What remains
  unmeasured is the specific PID each part presents, which is what a `BootloaderRegistry` match
  needs and what [Identification model](#identification-model) holds `Probe` until.
- **OQ-3 — ~~two USB leaves, one transport name~~.** *Resolved* — [`adr.md`](adr.md) Decision 3.
  Both USB protocols ship in one `Periphery.Bootloader.Esp32.Usb`, disambiguated by PID.
- **OQ-4 — driver-binding metadata.** [ADR-0038](../../../adr/0038-periphery-usb.md) deferred a
  "is this device bound to a claimable driver" enricher. Whatever OQ-1 concludes, this feature
  is the case that makes it worth having: the failure must be explained, not merely returned.
- **OQ-5 — macOS.** `Periphery.Usb` has no macOS backend. Paths B and C are Windows and Linux
  only until that lands. Does that gate shipping?
- **OQ-6 — reset-into-download over USB.** The DTR/RTS dance differs between the UART bridge,
  USB-Serial-JTAG, and USB-OTG. On the USB paths these are CDC `SET_CONTROL_LINE_STATE` control
  requests, reachable through `ControlTransferAsync` — but the exact per-chip sequences need
  verification on hardware, and USB-OTG re-enumerates mid-sequence, which the shell has to
  survive. [ADR-0063](../../../adr/0063-bootloader-entry-mode-switch.md)'s `DeviceWaitState`
  correlation is the existing machinery for that.
- **OQ-7 — compression.** Is `FLASH_DEFL_*` worth it stub-free, given USB-Serial-JTAG's
  throughput? Measure before implementing. The planner can add it later without a shape change.
- **OQ-8 — ~~attribution scope~~.** *Resolved* — RULE-L4 and [`adr.md`](adr.md) Decision 5.
  Attribution is mandatory in the change that introduces copied or adapted source, not a later
  judgment about how substantial it was.
- **OQ-9 — does family-level identity ever justify autoflash?** Deliberately left open. The
  honest answer needs the measured ids from OQ-1 and OQ-2 plus a decision about whether
  "an Espressif chip in download mode, chip confirmed by magic register after opening" clears
  a bar written for "the VID/PID *is* the target." Until then the providers are `Probe` and the
  question does not block anything.
- **OQ-10 — the ESP32 DFU payload container.** The first draft asserted "DfuSe-format file."
  That was unverified and probably wrong: DfuSe is ST's proprietary extension, not part of USB
  DFU 1.1, and Espressif's DFU guide does not name it. `idf.py dfu` produces a `dfu.bin` whose
  container is not documented on that page. Determine — from Espressif's tooling or from a
  capture — whether `Esp32DfuProgrammer` downloads raw segments or a wrapped container, before
  phase 1 writes a byte. Getting this wrong makes the device reject the transfer.

---

## Related

- [`adr.md`](adr.md) — the sibling decision record: why the modern parts are a USB family, the
  licensing rules, the stub policy, and what has not yet been measured.
- [ADR-0061 — Firmware-flashing platform](../../../adr/0061-firmware-flashing-platform.md) — the
  platform, the taxonomy (DEC-002), the flasher dependency tier (DEC-001), the DFU-extraction
  trigger (DEC-005), and the roadmap entry this spec fills in (DEC-007).
- [ADR-0062 — Periphery.Serial backend-provider](../../../adr/0062-periphery-serial-backend-provider.md) —
  blocks path A; cited high ESP32 baud rates as a driver, which the USB paths make moot.
- [ADR-0038 — Periphery.Usb](../../../adr/0038-periphery-usb.md) — the transport for paths B and
  C; the source of OQ-1, OQ-4, and OQ-5.
- [ADR-0063 — Bootloader entry / mode switch](../../../adr/0063-bootloader-entry-mode-switch.md) —
  the re-enumeration correlation OQ-6 needs.
- [ADR-0024 — Extension package pattern](../../../adr/0024-extension-package-pattern.md) — the
  no-third-party-runtime-deps constraint that [Licensing](#licensing-read-before-writing-a-line-of-protocol-code)
  and the [Stub policy](#stub-policy) extend.
- [ADR-0052 — Functional core / imperative shell](../../../adr/0052-periphery-treehopper-pure-core.md) —
  the mandated split.
- [Feature spec: Autoflash](../autoflash/spec.md) — the identification gate modern ESP32s pass.
- [Feature spec: Firmware image formats](../image-formats/spec.md) — where `EspApplication` and
  the `0xE9` sniff land.
