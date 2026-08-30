---
title: "ADR-0061: Firmware-flashing platform — 'flash anything' over Periphery transports, starting with STM32 USB DFU"
status: "Accepted"
status_note: "Partly shipped. `Periphery.Firmware`, `Periphery.Bootloader`, `Periphery.Bootloader.Stm32.Usb` (DFU/AN3156), `Periphery.Bootloader.Efm8.Usb`, and the `Periphery.FlashAnything` tool family all landed. Still open: `Periphery.Serial` ([ADR-0062](0062-periphery-serial-backend-provider.md)), `Periphery.Bootloader.Stm32.Serial` (AN3155), and the ESP32 / EFR32 / Nordic families."
date: "2026-06-16"
authors: "@charles8051"
tags: ["architecture", "decision", "firmware", "bootloader", "flashing", "dfu", "stm32", "efm8", "usb", "serial", "esp32", "functional-core", "extension-package"]
supersedes: ""
superseded_by: ""
---

# ADR-0061: Firmware-flashing platform — "flash anything" over Periphery transports, starting with STM32 USB DFU

## Status

> Number `0061` is provisional until merge (the next free number after ADR-0060),
> per this repo's "assign the number at merge" convention.
>
> **Builds on — does not supersede — [ADR-0024](0024-extension-package-pattern.md)
> (extension package pattern + star topology) and [ADR-0052](0052-periphery-treehopper-pure-core.md)
> (functional core / imperative shell).** Adds a *family* of packages (firmware
> bootloader clients) composing those decisions, plus two scoped dependency-rule
> refinements (DEC-001, DEC-006). The serial transport these build on is decided
> in **[ADR-0062](0062-periphery-serial-backend-provider.md)** (which supersedes
> ADR-0028).

## Context

Periphery already ships one firmware flasher: `Periphery.Efm8Bootloader` (the
SiLabs EFM8 HID bootloader uploader behind Treehopper's self-reflash, plus the
in-house Intel-HEX → boot-record generator that replaced SiLabs' `hex2boot`).
It is a clean [ADR-0052](0052-periphery-treehopper-pure-core.md) split — a pure
protocol core ([`Efm8Protocol`](../../src/Periphery.Bootloader.Efm8.Usb/Efm8Protocol.cs)),
a pure Intel-HEX parser ([`IntelHexImage`](../../src/Periphery.Firmware/IntelHexImage.cs)),
a thin transport seam ([`IEfm8Transport`](../../src/Periphery.Bootloader.Efm8.Usb/IEfm8Transport.cs)),
and an imperative uploader ([`Efm8BootloaderUploader`](../../src/Periphery.Bootloader.Efm8.Usb/Efm8BootloaderUploader.cs)) —
riding on `Periphery.Hid`.

This ADR recognises that flasher as the **first instance of a platform** and
commits to the north star:

> **Periphery is uniquely positioned to publish a "flash anything" tool.** It
> already owns cross-platform, zero-dependency, AOT-clean device *discovery*
> (`DeviceInfo`) and the transport spokes a flasher needs — USB control/bulk
> (`Periphery.Usb`), HID (`Periphery.Hid`), and serial (`Periphery.Serial`,
> [ADR-0062](0062-periphery-serial-backend-provider.md)). A vendor firmware
> flasher is "discover the target → speak its bootloader protocol over the right
> transport → write/verify/leave." Periphery has every layer but the protocol
> cores.

The immediate driver is **STM32 USB DFU** (ST application note **AN3156**,
Rev 18). The roadmap is **STM32 USART** (AN3155), then **EFM8** UART/SMBus
siblings, **ESP32**, and Silicon Labs **EFR32** BLE.

### The trap that shaped the naming: "DFU" is a protocol, not an umbrella

DFU = the USB **Device Firmware Upgrade** class (AN3156). It is *not* a generic
word for "firmware update," even though vendors colloquially abuse it that way
(Nordic/SiLabs "OTA DFU"). Of every flasher on the roadmap, only the
**USB** ones are actually DFU:

| Flasher | Actual protocol | DFU? |
|---|---|---|
| STM32 / USB | USB DFU (AN3156) | ✅ |
| STM32 / Serial | AN3155 USART | ❌ |
| EFM8 / USB | AN945 HID bootloader | ❌ |
| EFM8 / Serial | AN945 UART | ❌ |
| ESP32 / Serial | esptool ROM | ❌ |
| EFR32 / BLE | Gecko OTA | ❌ |

Naming the serial packages `*.Dfu.*.Serial` would re-create the **exact
AN3155-vs-AN3156 confusion** this platform exists to eliminate. The umbrella is
therefore **`Periphery.Bootloader`** (every target has a resident bootloader);
"Dfu" is reserved for the one place it is literally true — the generic DFU 1.1
client (DEC-005).

### What AN3156 actually specifies (grounds DEC-005)

AN3156 is **protocol only** (VID/PID and memory-layout descriptor strings are
AN2606 / DfuSe territory). Seven DFU 1.1 class requests, all with `wIndex` = the
DFU interface number:

| Request | `bRequest` | `bmRequestType` | `wValue` | Data stage |
|---|---|---|---|---|
| DFU_DETACH | 0x00 | 0x21 (OUT) | wTimeout | — |
| DFU_DNLOAD | 0x01 | 0x21 (OUT) | wBlockNum | firmware **or** command |
| DFU_UPLOAD | 0x02 | 0xA1 (IN) | wBlockNum | firmware **or** cmd list |
| DFU_GETSTATUS | 0x03 | 0xA1 (IN) | 0 | 6-byte status |
| DFU_CLRSTATUS | 0x04 | 0x21 (OUT) | 0 | — |
| DFU_GETSTATE | 0x05 | 0xA1 (IN) | 0 | 1-byte state |
| DFU_ABORT | 0x06 | 0x21 (OUT) | 0 | — |

The five real operations are multiplexed onto DNLOAD/UPLOAD by `wValue`:

- **Write memory** — DNLOAD, `wValue > 1`. `Address = ((wBlockNum − 2) × wTransferSize) + AddressPointer`.
- **Read memory** — UPLOAD, `wValue > 1`. Same address formula.
- **Set Address Pointer** — DNLOAD `wValue = 0`, payload `[0x21, addr LSB..MSB]` (5 bytes).
- **Erase** — DNLOAD `wValue = 0`, payload `[0x41, addr LSB..MSB]` (page) or `[0x41]` (mass erase).
- **Read Unprotect** — DNLOAD `wValue = 0`, payload `[0x92]`.
- **Get** (command discovery) — UPLOAD `wValue = 0` → supported command bytes.
- **Leave DFU** — DNLOAD with `wLength = 0`, then GETSTATUS → manifest → jump to `[AddressPointer + 4]`.

The behavioural fact that dictates the shell: **a DNLOAD command does nothing
until DFU_GETSTATUS.** Per command:

```
DNLOAD(cmd or data)  →  GETSTATUS → device returns dfuDNBUSY + bwPollTimeout
                        wait bwPollTimeout ms
                        GETSTATUS → dfuDNLOAD-IDLE (ok) | dfuERROR (errTARGET/errVENDOR)
```

`bwPollTimeout` (3 of the 6 status bytes: `bStatus`, `bwPollTimeout[3]`,
`bState`, `iString`) is the device dictating the wait. That wait lives in the
imperative shell, never the pure core ([ADR-0052](0052-periphery-treehopper-pure-core.md) DEC-004).

Other AN3156 facts the design honours:
- **Command set is not fixed (V3.0+)** — must issue **Get** and parse it (STM32H5 has no Read Unprotect).
- **Block/transfer size** 2–2048 bytes; real `wTransferSize` is in the DFU functional descriptor. `UsbConfigurationDescriptor` does not surface it today, so the DFU package self-serves via `GET_DESCRIPTOR(CONFIGURATION)`, with a constant fallback.
- **DFU_DETACH "is not meaningful"** for the system bootloader — Periphery cannot put a running STM32 into DFU; scope is "flash a device already in DFU mode" (BOOT0 / option bytes per AN2606).
- **Unbrickable bootloader, two dangerous ops** — ROM bootloader can't be bricked by app-flash writes, but **Read Unprotect** (mass-erase + RDP regression) and **option-byte writes** (RDP Level 2 = permanent) need an explicit guard.

### Constraints inherited from the ecosystem

- **[ADR-0024](0024-extension-package-pattern.md):** extension packages depend on `Periphery` core, never each other (**star topology**), with a "sub-protocol foundation" escape hatch; **no third-party runtime deps**; AOT-clean; domain exception derived from `DeviceEnumerationException`.
- **[ADR-0062](0062-periphery-serial-backend-provider.md):** `Periphery.Serial` is a **backend-provider** model — `Periphery.Serial` (abstraction) + `Periphery.Serial.Bcl` / `Periphery.Serial.RJCP` backends. Supersedes ADR-0028's single-native-impl plan.

## Decision

Establish a **firmware-bootloader platform**: a shared `Periphery.Firmware`
image foundation, a `Periphery.Bootloader` contract/family root, and one
bootloader-client package per `(family, transport)`, each an
[ADR-0052](0052-periphery-treehopper-pure-core.md) functional-core / imperative-shell
on a Periphery transport spoke. First deliverable: `Periphery.Bootloader.Stm32.Usb`
(AN3156, on `Periphery.Usb`).

```
Periphery (core, discovery — DeviceInfo)
  ├─ transports (Layer 1/2)
  │    Periphery.Usb   Periphery.Hid   Periphery.Serial (+ .Bcl / .RJCP, ADR-0062)
  │
  ├─ Periphery.Firmware                  image model: HEX / bin / DfuSe (pure, shared)
  │
  └─ Periphery.Bootloader                flashing contract + family root
       ├─ Periphery.Bootloader.Dfu       generic USB DFU 1.1 client (graduates per DEC-005)
       ├─ Periphery.Bootloader.Stm32     [optional] shared STM32 chip/memory maps
       │    ├─ Periphery.Bootloader.Stm32.Usb      AN3156 → Periphery.Usb (+ .Dfu)   [phase 1]
       │    └─ Periphery.Bootloader.Stm32.Serial   AN3155 → Periphery.Serial          [phase 3]
       ├─ Periphery.Bootloader.Efm8      shared AN945 core (protocol/records/uploader/seam)
       │    ├─ Periphery.Bootloader.Efm8.Usb       HID → Periphery.Hid   [the EFM8 rename, phase 0]
       │    └─ Periphery.Bootloader.Efm8.Serial    AN945 UART → Periphery.Serial      [roadmap]
       ├─ Periphery.Bootloader.Esp32.Serial        esptool → Periphery.Serial          [roadmap]
       └─ Periphery.Bootloader.Efr32.Ble           Gecko OTA → BLE                     [roadmap]
```

### DEC-001 — `Periphery.Bootloader.*` is a second package tier composing a transport spoke + the firmware foundation

[ADR-0024](0024-extension-package-pattern.md)'s star topology forbids
spoke-to-spoke deps "unless explicit … a true sub-protocol foundation." A
bootloader client *is* that case (a protocol over a transport), and
`Periphery.Efm8Bootloader → Periphery.Hid` already relies on the hatch. Make the
tier explicit:

> A **bootloader package** (`Periphery.Bootloader.{family}[.{transport}]`) may
> depend on one transport spoke (`Periphery.Usb` / `Hid` / `Serial`), the
> `Periphery.Firmware` foundation, the `Periphery.Bootloader` root, and its own
> `.{family}` core. It must not depend on another family or a second transport.
> It inherits ADR-0024's other constraints — AOT, the
> `DeviceEnumerationException`-derived exception type, and **no third-party**
> runtime deps (with the narrow first-party carve-out of DEC-006).

### DEC-002 — Taxonomy: `Periphery.Bootloader.{family}.{transport}`, and the family level holds shared protocol code

The naming is uniform `{family}.{transport}`. The `.{family}` level is where a
family's shared code lives **iff it has any** — which cleanly separates two cases:

- **Different protocol per transport → no required family core; transports are independent.** STM32: `Stm32.Usb` (DFU) and `Stm32.Serial` (AN3155) share no wire protocol. `Periphery.Bootloader.Stm32` is *optional* — created only if shared chip-ID / memory-map data materialises.
- **One protocol, many transports → a required family core.** EFM8: the AN945 boot-record protocol, generator, uploader, and `IEfm8Transport` seam are identical across HID / UART / SMBus; only framing differs. `Periphery.Bootloader.Efm8` holds that core; `.Efm8.Usb` / `.Efm8.Serial` are thin transports. (Structurally identical to `Periphery.Serial` + `.Bcl`/`.RJCP`.)

| Package | Role | Transport spoke | Status |
|---|---|---|---|
| `Periphery.Firmware` | image model (HEX/bin/DfuSe) | — | **new, phase 0** |
| `Periphery.Bootloader` | flashing contract + family root | — | new (contract graduates phase 2) |
| `Periphery.Bootloader.Dfu` | generic USB DFU 1.1 client | — | graduates per DEC-005 |
| `Periphery.Bootloader.Stm32.Usb` | AN3156 | `Periphery.Usb` | **new, phase 1** |
| `Periphery.Bootloader.Stm32.Serial` | AN3155 | `Periphery.Serial` | phase 3 |
| `Periphery.Bootloader.Efm8` | shared AN945 core | — | **phase 0 (from `Periphery.Efm8Bootloader`)** |
| `Periphery.Bootloader.Efm8.Usb` | AN945 over HID | `Periphery.Hid` | **phase 0 (the rename)** |
| `Periphery.Bootloader.Efm8.Serial` | AN945 over UART | `Periphery.Serial` | roadmap |
| `Periphery.Bootloader.Esp32.Serial` | esptool ROM | `Periphery.Serial` | roadmap |
| `Periphery.Bootloader.Efr32.Ble` | Gecko OTA | BLE/GATT | roadmap |

Transport leaf is the user-facing transport (`.Usb` / `.Serial` / `.Ble`), not
the USB *class*: `Efm8.Usb` is HID-class and `Stm32.Usb` is DFU-class under the
hood (different spokes: `Periphery.Hid` vs `Periphery.Usb`), but both are "USB"
to the operator.

### DEC-003 — `Periphery.Firmware` (images) and `Periphery.Bootloader` (contract) are separate foundations

- **`Periphery.Firmware`** — pure image model: move `IntelHexImage` +
  `IntelHexFormatException` out of the EFM8 package; add `FirmwareImage`
  (sparse address→bytes), `RawBinaryImage` (flat + base), `DfuSeFile` (DfuSe
  `.dfu` prefix/target headers + CRC-32 suffix). BCL-only, AOT-clean. Two
  consumers already (EFM8 + STM32 DFU) justify it now.
- **`Periphery.Bootloader`** — the flashing contract: `IFirmwareProgrammer`
  (`IdentifyAsync` / `FlashAsync` / `LeaveAsync`), `FlashOptions`,
  `FlashProgress`, `FlashResult`, `DeviceIdentity`, and a `BootloaderRegistry`
  ("which client handles this discovered device"). **Graduates at phase 2**, once
  `Efm8.Usb` + `Stm32.Usb` (two clients) prove the shape — not invented up front.

Protocol-specific **plans and steps stay client-private**; only image, options,
progress/result, identity, and (later) the contract are shared.

### DEC-004 — Every bootloader client is pure core + transport seam + imperative shell ([ADR-0052](0052-periphery-treehopper-pure-core.md))

- **Pure core:** protocol value types (closed unions) + total `Encode` / `Decode` / `Plan`. No `Task`, `CancellationToken`, transport handle, or clock. Byte-exact unit-testable, no hardware.
- **Transport seam:** a small interface at the protocol-request grain (`IEfm8Transport`, `IStm32DfuTransport`), so the shell is testable against a fake.
- **Imperative shell:** owns the transport handle, the GETSTATUS poll loop, the `bwPollTimeout`-paced `Task.Delay`, cancellation, reconnect-after-reset, `IProgress<FlashProgress>`.

### DEC-005 — `Periphery.Bootloader.Stm32.Usb` (AN3156), with the generic DFU layer kept extractable

DFU requests run over `UsbDevice.ControlTransferAsync` (`RequestType` 0x21/0xA1,
`Index` = interface number); the shell implements the GETSTATUS-triggered model
verbatim. **Internally**, keep the **generic DFU 1.1 layer** (DNLOAD / UPLOAD /
GETSTATUS / CLRSTATUS / state enum / 6-byte status decode) separate from the
**ST command layer** (Set Address Pointer / Erase / Read Unprotect /
memory-layout / address formula). The generic layer **graduates into
`Periphery.Bootloader.Dfu`** when a second DFU consumer appears — **ESP32-S2/S3
ROM USB DFU** is exactly that (DEC-007). One package now; "Dfu" earns its name
when there are two consumers.

### DEC-006 — STM32 USART (AN3155) reuses `call-and-response` (core + protocol) over a Periphery-owned byte-source seam

`call-and-response` is a **first-party** byte-stream library, and
its `Protocol.Stm32Bootloader` is a maintained AN3155 client.
`Periphery.Bootloader.Stm32.Serial` **reuses** it:

- Depend on **`CallAndResponse`** (core: `Transceiver` / `IDuplexPipe` / `FrameDetectionResult`) **+ `CallAndResponse.Protocol.Stm32Bootloader`** (Ping / Get / GetID / Read / Write / Extended-Erase / Go).
- **Do not** reference `CallAndResponse.Transport.Serial` — the only RJCP-pulling package. Implement a thin `SerialDuplexPipe : IDuplexPipe` over `Periphery.Serial`'s pipe surface (~10 lines). `IDuplexPipe` *is* the byte-source seam.

**Dependency-rule refinement (narrows ADR-0024 to its intent).** The "no
external runtime deps" rule keeps out **third-party supply-chain surface** (why
RJCP is rejected) and preserves AOT-cleanliness — it doesn't forbid reusing our
own libraries. A bootloader package may depend on a **first-party,
self-maintained** library when it (a) adds **no third-party transitive
deps** — `CallAndResponse` core pulls only `Microsoft.Extensions.Logging.Abstractions`,
`System.IO.Pipelines`, `System.Diagnostics.DiagnosticSource`, all Microsoft
BCL-adjacent and already used across `Periphery.Usb`/`Periphery.Serial`; (b) is
**AOT-clean** under the `PublishAot` gate; (c) is consumed **transport-free**.
`CallAndResponse` (core + protocol) meets all three; `Transport.Serial` is excluded.

> **First-party prep (in the `call-and-response` repo):** `CallAndResponse` core
> + the STM32 protocol target `net8.0` only with no `<IsAotCompatible>`.
> Before Periphery depends on them: multi-target `net10.0` + add/validate the AOT
> flag. Expected trivial (Pipelines + byte manipulation); fix there if not.

> **`call-and-response`'s wider fit:** its `FrameDetectionResult` / `Transceiver`
> engine is the natural substrate for the SLIP-framed serial protocols on the
> roadmap (ESP32 esptool, Nordic DFU), and `Transport.BleNordicUart` is the legit
> Nordic BLE-DFU transport (outside the zero-dep core). It was only wrong for
> **USB DFU** (control transfers, not a byte stream; DEC-005).

### DEC-007 — Roadmap families validate the platform

| Target | Protocol | Transport | Reuses | Notes |
|---|---|---|---|---|
| ESP32 / S2 / S3 / C3 | esptool ROM (SLIP) | `Periphery.Serial`; `Periphery.Usb` on S2/S3 | SLIP via the serial pipe; **`Periphery.Bootloader.Dfu` for S2/S3 USB DFU** (DEC-005 payoff) | MD5 verify built in; high/custom baud needs ADR-0062's custom-rate support |
| Silicon Labs EFR32 | Gecko OTA ("OTA DFU") | BLE/GATT (future) | `.zip`/GBL image parsing in `Periphery.Firmware` | SiLabs OTA GATT service; needs a BLE transport |
| Nordic nRF5x | nRF Secure DFU | serial / USB-CDC; BLE | `.zip` package parsing; `call-and-response` `BleNordicUart` for BLE | — |
| EFM8 (BB/SB) | AN945 over UART / SMBus | `Periphery.Serial` / I2C | **the `Periphery.Bootloader.Efm8` core** | proves the family-core split (DEC-002) |

## Implementation plan

Independently shippable phases (no time estimates, per this repo's convention).

### Phase 0 — Foundations + EFM8 restructure

- **`Periphery.Firmware`** (new, `net8.0;net10.0`, BCL-only, AOT-gated): move `IntelHexImage` + `IntelHexFormatException` from the EFM8 package; add `FirmwareImage`, `RawBinaryImage`, `DfuSeFile`. Tests in `tests/Periphery.Firmware.Tests/`.
- **EFM8 rename/restructure** (no external consumers — free):
  - `Periphery.Efm8Bootloader` → **`Periphery.Bootloader.Efm8`** (transport-free core: `Efm8Protocol`, boot-records, generator, options, uploader, `IEfm8Transport`, result/progress/exception). Consumes `Periphery.Firmware`.
  - **`Periphery.Bootloader.Efm8.Usb`** (new): `HidEfm8Transport` + discovery glue → `Periphery.Bootloader.Efm8` + `Periphery.Hid`. (Treehopper's reflash updates to this.)

### Phase 1 — `Periphery.Bootloader.Stm32.Usb` MVP (mass-erase app flashing)

- New project → `Periphery.Usb` + `Periphery.Firmware`. AOT-gated.
- **Pure core:** `DfuState` (appIDLE..dfuERROR, 0–10); `DfuStatusCode` (OK=0x00 … errSTALLEDPKT=0x0F); `DfuStatus` (`readonly record struct` + `Decode(6 bytes)`); `Stm32DfuCommand` closed union (`Get | SetAddress(uint) | ErasePage(uint) | MassErase | ReadUnprotect`, each `Encode()`); `Stm32DfuPlan.Plan(FirmwareImage, DfuCapabilities, FlashOptions) → ImmutableArray<DfuStep>` (phase 1: MassErase + per-segment SetAddress + wTransferSize chunks + Leave). Generic-DFU vs ST-command layers kept as an internal seam (DEC-005).
- **Seam** `IStm32DfuTransport` (Download / Upload / GetStatus / ClearStatus / GetState / Abort); **`UsbStm32DfuTransport`** over `ControlTransferAsync`; **`DfuFunctionalDescriptor`** self-served probe for `wTransferSize` (constant fallback).
- **Shell** `Stm32DfuProgrammer`: `OpenAsync(DeviceInfo, iface=0)` → probe + ensure dfuIDLE (CLRSTATUS-recover loop); `FlashAsync(...)` runs the plan with the GETSTATUS / `Delay(PollTimeout)` / confirm loop; `LeaveAsync(jump)`. Exception `Stm32DfuException : DeviceEnumerationException`.
- **CLI** ([ADR-0043](0043-periphery-cli-command-surface.md)): `periphery bootloader list` / `periphery bootloader flash --file fw.hex [--base 0x08000000] [--vid 0483 --pid df11] [--leave] [--json]`; EFM8-style exit codes (0/1/2/3/4).
- **Tests** (`FakeStm32DfuTransport`): decode, encode golden bytes (AN3156 §5.2/5.3), planner, and a scripted dfuDNBUSY→idle sequence incl. errTARGET + CLRSTATUS recovery — zero hardware.

### Phase 2 — Contract graduation + STM32 DFU completeness

- Graduate `IFirmwareProgrammer` + `FlashOptions/Progress/Result` + `DeviceIdentity` + `BootloaderRegistry` into **`Periphery.Bootloader`**; retrofit `Efm8.Usb` + `Stm32.Usb` (two clients = rule of two).
- DfuSe **memory-layout string** parse (`GET_DESCRIPTOR(STRING)`) → per-page erase (`--page-erase`); upload/verify; guarded Read-Unprotect / option-byte (`--allow-rdp`, RDP-2 is permanent); reconnect-after-reset via `UsbDeviceProxy` / `DeviceSessionHost`.

### Phase 3 — `Periphery.Bootloader.Stm32.Serial` (AN3155), gated on `Periphery.Serial`

- **`call-and-response` prep** (its repo): multi-target `net10.0` + `<IsAotCompatible>` on core + `Protocol.Stm32Bootloader` (DEC-006).
- **`Periphery.Serial`** backend-provider per [ADR-0062](0062-periphery-serial-backend-provider.md) (abstraction + at least `.Bcl` or `.RJCP`).
- **`Periphery.Bootloader.Stm32.Serial`** → `Periphery.Serial` + `Periphery.Firmware` + PackageReference `CallAndResponse` (+ protocol): `SerialDuplexPipe : IDuplexPipe`, `Stm32SerialProgrammer` shell (115200 8E1, `new Transceiver(pipe)` → `Stm32BootloaderClient`, Ping → GetID → Extended-Erase → Write-Memory ≤256 B → Go), mapped to the contract.

### Phase 4 — Unified tool

- **`periphery flash`** auto-detect: pick the client from the discovered `DeviceInfo` (DFU `0483:DF11`, serial-bootloader signature, EFM8 HID id) via `BootloaderRegistry`. Optional standalone "flash anything" `dotnet tool`.

### Phase 5+ — Roadmap (DEC-007)

- `Periphery.Bootloader.Esp32.Serial` (esptool); extract **`Periphery.Bootloader.Dfu`** for ESP32-S2/S3 USB DFU.
- `Periphery.Bootloader.Efm8.Serial` (AN945 UART — exercises the family core).
- `Periphery.Bootloader.Efr32.Ble` (Gecko OTA) / Nordic nRF Secure DFU.

## Consequences

### What we gain

- A **coherent, uniformly-named platform** (`Periphery.Bootloader.{family}.{transport}`) where the family level absorbs shared protocol code — STM32's independent protocols and EFM8's shared protocol both fit the same scheme.
- **Honest naming**: "Dfu" appears only where it's true (`Periphery.Bootloader.Dfu`), not as a misleading umbrella.
- Each protocol core is **byte-exact unit-testable with no hardware**.
- **No new third-party supply-chain surface**: the only added runtime dep is first-party `call-and-response` (Serial client only); serial third-party choice (RJCP) is opt-in per ADR-0062.
- A **graduation path** (generic DFU → `Periphery.Bootloader.Dfu`; contract → `Periphery.Bootloader`) that defers abstraction until proven.

### What we accept

- **EFM8 rename churn** — `Periphery.Efm8Bootloader` splits into `Periphery.Bootloader.Efm8` (+ `.Usb`); Treehopper's reflash updates. No external consumers.
- **Two scoped dependency-rule refinements** (DEC-001 flasher tier; DEC-006 first-party `call-and-response`) — both narrow, both anchored in existing precedent.
- **Deeper namespaces** (`Periphery.Bootloader.Stm32.Usb`, 4 segments) — precedented by `Periphery.Treehopper.Control.Cli`.
- **STM32 USART gated on `Periphery.Serial`** (ADR-0062) shipping first.

### What we constrain

- Bootloader packages obey **DEC-001** (one transport + firmware foundation + family core; no cross-family, no second transport).
- Protocol cores obey **ADR-0052** purity; poll/timing live in the shell.
- Plans/steps stay client-private until the contract graduates (DEC-003).

## Alternatives considered

- **`Periphery.Dfu.{family}.{transport}` (the floated scheme).** Rejected — "DFU" is the USB DFU class, false for 5 of 6 roadmap targets; re-creates the AN3155-vs-AN3156 confusion the platform exists to kill. "Dfu" is kept for the generic DFU client only.
- **`Periphery.Firmware.{family}.{transport}` (single cohesive namespace).** Viable, but muddies "what's in `Periphery.Firmware`" (images vs. a flasher). Chose the crisp split: `Firmware` = images, `Bootloader` = protocol clients.
- **Use `call-and-response` for USB DFU.** Rejected — DFU is control transfers, not a byte stream; `IDuplexPipe` framing doesn't apply (DEC-005).
- **Reimplement AN3155 natively (no dep).** Rejected (DEC-006) — duplicates a maintained first-party impl for no supply-chain benefit; the RJCP concern is confined to the transport we don't use. Fallback if AOT prep fails.
- **Per-vendor packages (`Periphery.Stm32` with both transports).** Rejected — forces disjoint USB+serial stacks on every consumer.
- **Generic `Periphery.Bootloader.Dfu` from day one.** Deferred (DEC-005) — internal seam until ESP32-S2/S3 is the second consumer.

## Affected files (planned)

- `src/Periphery.Firmware/` — **new**: `FirmwareImage.cs`, `IntelHexImage.cs` (moved), `IntelHexFormatException.cs` (moved), `RawBinaryImage.cs`, `DfuSeFile.cs`.
- `src/Periphery.Bootloader/` — *(phase 2)* `IFirmwareProgrammer.cs`, `FlashOptions.cs`, `FlashProgress.cs`, `FlashResult.cs`, `DeviceIdentity.cs`, `BootloaderRegistry.cs`.
- `src/Periphery.Bootloader.Efm8/` — **from** `Periphery.Efm8Bootloader` (drop the moved HEX files + the HID transport).
- `src/Periphery.Bootloader.Efm8.Usb/` — **new**: `HidEfm8Transport.cs` + glue → `Periphery.Hid`.
- `src/Periphery.Bootloader.Stm32.Usb/` — **new**: `DfuState`, `DfuStatusCode`, `DfuStatus`, `Stm32DfuCommand`, `Stm32DfuPlan`, `DfuCapabilities`, `IStm32DfuTransport`, `UsbStm32DfuTransport`, `DfuFunctionalDescriptor`, `Stm32DfuProgrammer`, `Stm32DfuException`. *(phase 2)* `DfuMemoryLayout`.
- `src/Periphery.Cli/Commands/` — `BootloaderListCommand.cs`, `BootloaderFlashCommand.cs`.
- `tests/Periphery.Firmware.Tests/`, `tests/Periphery.Bootloader.Stm32.Usb.Tests/` (incl. `FakeStm32DfuTransport`).
- *(phase 3)* `src/Periphery.Bootloader.Stm32.Serial/` (`SerialDuplexPipe`, `Stm32SerialProgrammer`; PackageReference `CallAndResponse` + `Protocol.Stm32Bootloader`); `Periphery.Serial.*` per ADR-0062; AOT/multi-TFM prep in the `call-and-response` repo.
- Treehopper reflash call sites → `Periphery.Bootloader.Efm8.Usb`.

## Testing

- **Pure (per-PR, no hardware):** status/state decode; command encode vs AN3156 golden bytes; `Plan(...)` assertions; HEX/DfuSe/bin round-trips; AN3155 ACK/checksum (phase 3).
- **Shell-with-fake (per-PR, no hardware):** `FakeStm32DfuTransport` scripts GETSTATUS sequences — happy path, dfuDNBUSY→idle, errTARGET/errVENDOR, CLRSTATUS recovery.
- **Hardware (manual / rig):** real STM32 in DFU (`0483:DF11`) — confirm the pure core's bytes are firmware-accurate; end-to-end flash + leave + run.
- **AOT gate:** `PublishAot=true` for `Periphery.Firmware`, `Periphery.Bootloader.*`.

## Related ADRs

- [ADR-0024 — Extension package pattern](0024-extension-package-pattern.md) — star topology + no-third-party-deps + AOT; **refined** by DEC-001 (bootloader tier) and DEC-006 (first-party carve-out).
- [ADR-0052 — Periphery.Treehopper pure core](0052-periphery-treehopper-pure-core.md) — the functional-core/imperative-shell split mandated for every client (DEC-004).
- [ADR-0038 — Periphery.Usb](0038-periphery-usb.md) — `ControlTransferAsync`, the DFU substrate (DEC-005).
- [ADR-0062 — Periphery.Serial backend-provider](0062-periphery-serial-backend-provider.md) — the serial transport STM32 USART / ESP32 build on (supersedes ADR-0028).
- [ADR-0043 — Periphery.Cli command surface](0043-periphery-cli-command-surface.md) — the `periphery bootloader` / `periphery flash` conventions.
- [ADR-0060 — Device reset and recovery escalation](0060-device-reset-and-recovery-escalation.md) — bootloader entry / reconnect-after-reset (phase 2).
- `Periphery.Efm8Bootloader` → `Periphery.Bootloader.Efm8` — the precedent flasher this generalises (phase 0 rename).
