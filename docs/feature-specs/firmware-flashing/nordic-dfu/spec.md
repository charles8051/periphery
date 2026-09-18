# Feature Spec: Nordic nRF5x flashing (nRF Secure DFU over serial and BLE)

<!--
Authoritative, LIVING spec for Nordic nRF5x flashing in the Periphery.Bootloader platform.
Read this before editing any code in the feature's scope; the "Affected Layers" table names
the projects to touch. The "how / why" decisions belong in an append-only sibling `adr.md`
when one is written; this file is the "what" and is rewritten as the feature evolves.
-->

## Status

**Proposed** — no code. [ADR-0061](../../../adr/0061-firmware-flashing-platform.md) DEC-007
reserves the Nordic family on the roadmap and names its transports as "serial / USB-CDC; BLE".
This spec fills that row in and splits it: the serial half is buildable on shipped packages,
and the BLE half is blocked behind [ADR-0085](../../../adr/0085-the-32feet-binding-is-two-integration-packages.md),
which is itself Proposed with no code.

| Field        | Value                                            |
|--------------|--------------------------------------------------|
| Author       | Charles Lee                                      |
| Created      | 2026-09-18                                       |
| Last Updated | 2026-09-18                                       |
| Project      | `Periphery.Bootloader.Nordic`                    |
| Branch       | `claude/nordic-bootloader-protocols-9c2756`      |

---

## Purpose

Flash Nordic nRF51 / nRF52 / nRF53 targets through the existing FlashAnything pipeline, with
the same contract, safety gates, and front-ends as the STM32 and EFM8 flashers.

---

## Two facts that shape everything below

**1. Nordic parts have no ROM bootloader.** There is no equivalent of the STM32 system
bootloader at a fixed address that ADR-0061 called unbrickable. A blank nRF is programmed over
SWD with a debug probe, which this platform does not do and does not intend to. Every protocol
in this spec assumes a bootloader that someone already put in flash. A device that loses its
bootloader leaves this platform's reach entirely, which is why [Safety rules](#safety-rules)
treats a bootloader-update package differently from an application package.

**2. The op-code layer is shared across serial and BLE.** That is the "one protocol, many
transports" case in [ADR-0061](../../../adr/0061-firmware-flashing-platform.md) DEC-002, so
`Periphery.Bootloader.Nordic` is a **required** family core, structurally the same as
`Periphery.Bootloader.Efm8`. It is also the constraint that decides the seam: a core that has
to run over both a byte stream and two GATT characteristics cannot be written against either.

---

## Scope: nRF5 SDK Secure DFU, not MCUboot

Nordic currently ships two bootloaders, and the newer one is the default for new designs.

| | nRF5 SDK Secure DFU | MCUboot + SMP |
|---|---|---|
| Shipped in | nRF5 SDK (maintenance) | nRF Connect SDK / Zephyr |
| Wire protocol | nRF DFU op codes | mcumgr SMP, CBOR bodies, image group 1 |
| Serial framing | SLIP (RFC 1055) | `06 09` marker, base64 of length + packet + CRC16-XMODEM, newline-terminated |
| BLE | GATT service `0xFE59` | SMP GATT service `8D53DC1D-1DB7-4CD3-868B-8A527460AA84` |
| Image | signed `.zip` package | MCUboot-signed `.bin`, header + TLV trailer |

**This spec covers the first column only.** The two share no framing, no header, no command
encoding, and no image container. Filing MCUboot as a third Nordic transport would put two
unrelated protocols under one family core, which is exactly the AN3155-vs-AN3156 confusion
ADR-0061's naming section exists to prevent. MCUboot is its own family
(`Periphery.Bootloader.McuBoot`) whose transports happen to include Nordic parts. See OQ-8.

Choosing the maintenance-mode protocol first is deliberate: it is what fielded devices run,
and it is the one whose serial transport is reachable on shipped packages today.

---

## Transports, and which are reachable

The last column is **transport reachability** — whether Periphery can open and talk to the
device at all — not "you can flash this today". **No path has a verified end-to-end flash.**

| Path | Devices | Framing | Periphery transport | Identification | Transport reachable |
|---|---|---|---|---|---|
| **A — UART** | any nRF running the SDK serial bootloader | SLIP over a UART bridge | `Periphery.Serial` | `Probe` | **Yes.** `Periphery.Serial` ships ([`BclSerialDuplexPipe`](../../../../src/Periphery.Serial/BclSerialDuplexPipe.cs)) |
| **B — USB CDC ACM** | nRF52840 Dongle and any nRF52840 running the USB serial bootloader | SLIP over a CDC ACM port | `Periphery.Serial` (the CDC device enumerates as a port) | `Passive` **once the ids are measured** — see OQ-2 | **Yes**, unmeasured |
| **C — BLE GATT** | any nRF running the BLE bootloader | GATT, no SLIP | none | n/a | **No.** Periphery has no GATT client |

Paths A and B are the same protocol over the same framing reaching the same `IDuplexPipe`.
They differ only in identification, which is why they are one package and not two.

---

## Dependencies / Prerequisites

| Depends on | Why | State |
|---|---|---|
| The bootloader contract (`IFirmwareProgrammer` / `IBootloaderProvider` / `BootloaderRegistry`) | Nordic flashers plug into the same dispatcher as STM32 and EFM8. | **Have** |
| `Periphery.Serial` | The transport for paths A and B. | **Have** — landed in #168; [ADR-0062](../../../adr/0062-periphery-serial-backend-provider.md)'s amendment records it |
| `CallAndResponse` core, for `SlipCodec` | SLIP encode and decode, shipped under that repo's ADR-0020 as `IFrameCodec`. First-party, permitted by ADR-0061 DEC-006. | **Have** — already referenced by `Periphery.Bootloader.Stm32.Serial` |
| `Periphery.Firmware` payload loading | The `.zip` package is a new format; the `.bin` inside it is an existing one. | **Have**, needs one new format |
| `.zip` package parsing | The init packet and the image both come out of the package. | **New** (phase 0) |
| A GATT client | Path C, and nothing else. | **Missing** — [ADR-0085](../../../adr/0085-the-32feet-binding-is-two-integration-packages.md) is Proposed with no code |
| A way to rebind an LE peripheral across a reset | Path C. See OQ-5. | **Missing** — ADR-0085 D7 §3 defers `BleDeviceProxy` on [ADR-0083](../../../adr/0083-ble-identity-does-not-survive-repairing.md) |

---

## Affected Layers

| Project | Change Type |
|---|---|
| `Periphery.Firmware` | **Small:** `FirmwareFormat.NordicZip` and its content sniff (`PK\x03\x04` plus a `manifest.json` member), classified `FirmwareKind.PackagedBlob` beside `Efm8BootRecords` in [`FirmwareFormat.cs`](../../../../src/Periphery.Firmware/FirmwareFormat.cs). The unwrap that produces the `.dat` and `.bin` pair. Already anticipated by the [image-formats spec](../image-formats/spec.md). |
| `Periphery.Bootloader.Nordic` | **New:** the pure nRF DFU protocol core — op-code union with `Encode()`, response decode, result codes, the object model, and the transfer planner — plus the `INordicDfuTransport` seam. Transport-free (DEC-002 family core). |
| `Periphery.Bootloader.Nordic.Serial` | **New:** `SlipNordicDfuTransport` over `Periphery.Serial` and `CallAndResponse`'s `SlipCodec`, plus the provider and the port-probe handshake. Covers paths A and B. |
| `Periphery.Bootloader.Nordic.Ble.*` | **Deferred:** `GattNordicDfuTransport` and the buttonless `IBootloaderEntry`. Nothing to build until ADR-0085 ships a GATT client. Its name is OQ-3 and its TFM set is OQ-4. |
| `Periphery.FlashAnything.Cli` / `.Gui` | **Small:** register the new provider alongside the three in [`Program.cs:26`](../../../../src/Periphery.FlashAnything.Cli/Program.cs). Ordering matters: like `Stm32SerialBootloaderProvider`, a serial provider must register after every VID/PID-matched one. |
| `tests/Periphery.Bootloader.Nordic.Tests` | **New:** command golden bytes, response decode, planner tables, and a `FakeNordicDfuTransport` scripting the CRC-mismatch and resume paths. Zero hardware. |

---

## Requirements

- [ ] **Flash an nRF52840 Dongle over USB CDC ACM** end to end: open, ping, negotiate MTU,
      transfer the init packet, transfer the image, reset into the application.
- [ ] **Flash an nRF52 over a UART bridge** with the same core and a different provider.
- [ ] **Accept a Nordic `.zip` package and nothing else** for this family. A bare `.bin` has no
      init packet, so a secure bootloader will reject it after the transfer. Refuse it at load
      time with that reason, rather than sending bytes that cannot succeed.
- [ ] **Verify by CRC-32 during transfer**, which the protocol does natively, satisfying
      `FlashOptions.Verify` with no read-back. `IBootloaderEntry.CanVerify` stays `false`: the
      in-session check is the protocol's own and needs no second bootloader round trip.
- [ ] **Resume a partial transfer.** `Select` returns `(max_size, offset, crc32)` for a reason;
      a reconnect mid-image validates the prefix and continues from `offset`.
- [ ] **Refuse a bootloader or SoftDevice package unless explicitly allowed.** See
      [Safety rules](#safety-rules).
- [ ] **Report progress** through the existing `FlashProgress` / `FlashPhase` contract.
- [ ] **Ship the serial provider as `Probe`, and the CDC provider as `Probe` until OQ-2 is
      measured.** No `Passive` declaration on unmeasured ids.
- [ ] **No new third-party runtime dependency on the serial path.** `CallAndResponse` is
      first-party and carries no third-party transitive deps (DEC-006).
- [ ] **AOT-clean** under `PublishAot`, like every other `Periphery.Bootloader.*` package.

---

## Package layout

```
Periphery.Bootloader
  └─ Periphery.Bootloader.Nordic            nRF DFU core + INordicDfuTransport   [phase 1]
       ├─ Periphery.Bootloader.Nordic.Serial   SLIP over UART and USB CDC        [phase 2]
       └─ Periphery.Bootloader.Nordic.Ble.*    GATT over 0xFE59                  [deferred]
```

The serial leaf obeys DEC-001: one transport spoke (`Periphery.Serial`), the
`Periphery.Firmware` foundation, the `Periphery.Bootloader` root, its own family core, and
the first-party `CallAndResponse` carve-out DEC-006 grants.

The BLE leaf does not, and that is OQ-3. It would carry a third-party runtime dependency on
`InTheHand.BluetoothLE`, which is the row of ADR-0024's dependency table that ADR-0085 D1 used
to refuse the name `Periphery.Bluetooth`. A leaf named `Periphery.Bootloader.Nordic.Ble` is the
same shape of name. Either it takes an `.InTheHand` suffix, naming the library it binds, or
DEC-006 widens to admit third-party transports. The suffix is cheaper and matches
`Periphery.Serial.Rjcp`.

---

## Architecture (functional core / imperative shell — [ADR-0052](../../../adr/0052-periphery-treehopper-pure-core.md))

### Pure core (`Periphery.Bootloader.Nordic`)

- `NordicDfuOpCode`, `NordicDfuResult`, `NordicDfuObjectType` — closed enums over the byte values.
- `NordicDfuCommand` — a closed union (`Create | SetPrn | CrcGet | Execute | Select | MtuGet |
  Write | Ping | Abort`), each with `Encode()`.
- `NordicDfuResponse.Decode(ReadOnlySpan<byte>)` — parses `60 <request op> <result>` plus the
  per-op payload. Total over every byte sequence: an unrecognised frame is a decode failure
  value, not an exception thrown from a pure function.
- `NordicDfuPlan.Plan(FirmwarePayload, NordicDfuCapabilities, FlashOptions) →
  ImmutableArray<NordicDfuStep>` — chunks the init packet and the image against the device's
  reported `max_size` and `MaxDataChunk`, emitting the Create / Write / CrcGet / Execute cycles.
  Same shape as [`Stm32DfuPlan`](../../../../src/Periphery.Bootloader.Stm32.Usb/Stm32DfuPlan.cs).
- CRC-32 accumulation, which the planner needs to state the expected value at each checkpoint.

No clock, no cancellation token, no IO. The device-dictated waits and the retry decisions live
in the shell, as ADR-0052 DEC-004 requires.

### The seam

```csharp
public interface INordicDfuTransport
{
    /// Control point: write one command, await its response frame.
    Task<ReadOnlyMemory<byte>> SendCommandAsync(ReadOnlyMemory<byte> command, CancellationToken ct);

    /// Bulk firmware bytes. BLE writes the Packet characteristic; serial emits op code 0x08.
    Task SendDataAsync(ReadOnlyMemory<byte> data, CancellationToken ct);

    /// Largest payload per data write. BLE: ATT_MTU - 3. Serial: (mtu - 1) / 2 - 1.
    int MaxDataChunk { get; }
}
```

Three members, the same granularity as
[`IEfm8Transport`](../../../../src/Periphery.Bootloader.Efm8.Usb/IEfm8Transport.cs) and
[`IStm32DfuTransport`](../../../../src/Periphery.Bootloader.Stm32.Usb/IStm32DfuTransport.cs).
SLIP is hidden on one side, two GATT characteristics on the other, and nothing about either
appears in the core.

**This is not the GATT abstraction ADR-0085 D7 declined.** D7 refused `IGattClient` and
Periphery-shaped service, characteristic, and descriptor types, on the grounds that
`InTheHand.Bluetooth` is already task-based and there is no second backend to swap in behind a
wrapper. `INordicDfuTransport` is DFU-shaped, not GATT-shaped: the BLE implementation calls
`GetPrimaryService`, `GetCharacteristic`, and `StartNotificationsAsync` on the 32feet types
directly, and no translation layer stands between them and the core.

It does, however, update one row of D7's table. That table records "Periphery abstraction to
adapt to: **none**" for `Periphery.Ble.InTheHand`, which is why D7 sizes its contents at two
extension methods. This feature supplies the first one. The adapter still belongs in a
bootloader leaf rather than in the BLE integration package, because a BLE integration package
depending on a flasher inverts the direction every other integration runs
(`Periphery.Camera.OpenCvSharp` depends on `Periphery.Camera`, not the reverse).

### Shell

`NordicDfuProgrammer : IFirmwareProgrammer` — owns the transport, executes the plan, compares
each returned CRC against the planned value, retries or fails a mismatched chunk, and maps the
result codes onto `FlashResult`. `NordicDfuException : DeviceEnumerationException`.

---

## The wire protocol

One op-code set across both transports. **The byte values below are from the nRF5 SDK and
`nrfutil` as recalled, not read off a header in this repository** — OQ-1 pins them before any
of them is written into code.

| Op | Code | Params | Response payload |
|---|---|---|---|
| Create | `0x01` | type (u8), size (u32 LE) | — |
| Set PRN | `0x02` | n (u16 LE), `0` disables | — |
| CRC Get | `0x03` | — | offset (u32), crc32 (u32) |
| Execute | `0x04` | — | — |
| Select | `0x06` | type (u8) | max_size, offset, crc32 (u32 each) |
| MTU Get | `0x07` | serial only | mtu (u16) |
| Write | `0x08` | data, serial only | — |
| Ping | `0x09` | id (u8), serial only | id (u8) |
| Abort | `0x0C` | — | — |
| Response | `0x60` | — | `60 <request op> <result>` then payload |

Result codes: `0x01` success, `0x02` op not supported, `0x03` invalid parameter, `0x04`
insufficient resources, `0x05` invalid object, `0x07` unsupported type, `0x08` not permitted,
`0x0A` failed, `0x0B` extended error with a trailing detail byte.

### The object model

The image transfers as two **objects**. Type `0x01` is the command object: the `.dat` init
packet, a protobuf carrying firmware and hardware versions, sizes, and an ECDSA P-256
signature. Type `0x02` is the data object: the `.bin`. Each is written in chunks no larger
than the `max_size` the device reports from `Select`, typically 4096 on BLE and a multiple of
the flash page.

```
Select(1) -> max_size, offset, crc
Create(1, len(dat));  write dat;  CRC Get -> compare;  Execute
Select(2) -> max_size, offset, crc
for each max_size chunk of bin:
    Create(2, len(chunk));  write chunk;  CRC Get -> compare;  Execute
```

The CRC-32 is **cumulative over everything received**, not per chunk. That is what makes
`Select` a resume point and what the planner must model, because the expected value at each
checkpoint depends on every byte before it.

### Path A and B framing

SLIP (RFC 1055) over the port, each packet terminated by `0xC0`.
`CallAndResponse`'s `SlipCodec` already implements both directions with the same constants
(`End 0xC0`, `Esc 0xDB`, `EscEnd 0xDC`, `EscEsc 0xDD`). Its `MaxFrameLength` defaults to 1006
and must instead be set from the device-reported MTU — see OQ-9.

Because escaping can double a payload, the usable chunk per `Write` is `(mtu - 1) / 2 - 1`.
`nrfutil` computes exactly that, and a host that assumes the MTU is the payload size will
overrun the device's receive buffer on an image whose bytes happen to need escaping.

The session opens with `Ping` carrying a rolling id to sync the stream, then `MTU Get`, then
`Set PRN`, then the flow above. Those three op codes exist only because a byte stream has no
message boundaries; path C has none of them.

### Path C framing

Service `0xFE59`, two characteristics:

| Characteristic | UUID | Properties | Carries |
|---|---|---|---|
| DFU Control Point | `8EC90001-F315-4F60-9FB8-838830DAEA50` | Write, Notify | commands and responses |
| DFU Packet | `8EC90002-F315-4F60-9FB8-838830DAEA50` | Write Without Response | firmware bytes only |

The client enables notifications on the Control Point CCCD, writes each command there, and
reads the reply as a notification on the same characteristic. Bulk data never touches it;
firmware streams to Packet as Write Without Response in `ATT_MTU - 3` chunks. GATT supplies a
boundary and a length per write, so there is no SLIP and no framing layer at all.

---

## Bootloader entry

| Path | How the device gets into the bootloader | Entry implementation |
|---|---|---|
| A / B | A physical button held through reset, or the application's own trigger. | None. The device is already in the bootloader when Periphery sees it, the same premise as `Stm32UsbBootloaderProvider`. |
| C | Buttonless DFU: characteristic `8EC90003-…` unbonded or `8EC90004-…` bonded, write `0x01`, the device notifies, disconnects, and reboots. | An `IBootloaderEntry` ([ADR-0063](../../../adr/0063-bootloader-entry-mode-switch.md)) whose `ExpectedBootloader` filter cannot be a stable address — see OQ-5. |

---

## Identification model

`IdentificationMode` is the autoflash safety gate (see the [autoflash spec](../autoflash/spec.md)).

- **Path A is `Probe`, permanently.** The VID/PID names an FTDI or CP210x bridge and says
  nothing about what is behind it. Same reasoning as `Stm32SerialBootloaderProvider`.
- **Path B could be `Passive`.** A dongle in bootloader mode enumerates at Nordic's own VID
  with a bootloader-specific PID, so the id **is** the target in the sense DEC-007's gate
  requires. It ships `Probe` until those ids are measured rather than recalled (OQ-2), matching
  how the [ESP32 spec](../esp32/spec.md) handles the same question.
- **Path C is out of scope for autoflash** regardless. An LE address is not a durable identity
  (ADR-0083), so there is nothing to gate on.

---

## Safety rules

**SR-1 — A bootloader or SoftDevice package is not an application package.** A failed
application update leaves the bootloader running and the device still reachable in DFU mode,
which is the property that makes path A and B recoverable. A failed *bootloader* update does
not: the thing being overwritten is the thing doing the overwriting, and recovery needs SWD
and a debug probe. The package's `manifest.json` names which images it carries. A package
containing a bootloader or SoftDevice image is refused unless an explicit opt-in flag is
passed, in the shape of `Stm32.Usb`'s `--allow-rdp` guard.

**SR-2 — Refuse a bare `.bin` for this family.** Without an init packet a secure bootloader
rejects the transfer after it completes. Failing at load time with that reason is better than
spending the transfer to learn it.

**SR-3 — Refuse a package whose manifest does not parse, or whose declared sizes do not match
the members.** A mismatch means the host and the device will disagree about the cumulative CRC,
and the failure surfaces as an opaque mid-transfer mismatch.

**SR-4 — Do not implement the signature.** The bootloader validates it. The host's job is to
transmit the init packet faithfully, not to judge it.

---

## Licensing

Periphery is licensed **PolyForm Small Business 1.0.0**. Write the protocol from Nordic's
published DFU documentation rather than by adapting nRF5 SDK or `nrfutil` source. If any source
is adapted, attribute it in the change that introduces it rather than in a later review pass,
matching the [ESP32 spec](../esp32/spec.md)'s RULE-L4. The licenses on the SDK and on `nrfutil`
have **not** been checked against PolyForm for this spec; do that before copying a line. The
protocol documentation itself carries no such constraint.

---

## Testing

Per the workspace testing preference, everything below is pure logic over values with no
wall-clock, no IO, and no hardware.

- Command encode golden bytes, per op code, against the values OQ-1 pins.
- Response decode across every result code, including the extended-error trailing byte and a
  truncated frame.
- Planner tables: chunk boundaries at `max_size`, a final short chunk, an image that is an exact
  multiple, and the cumulative CRC at each checkpoint.
- Resume: a `Select` reporting a non-zero offset with a matching prefix CRC continues, and one
  with a mismatched CRC restarts the object.
- `FakeNordicDfuTransport` scripting a CRC mismatch, an `insufficient resources` result, and a
  `not permitted` result.
- SLIP round-trips are `CallAndResponse`'s tests, not this repository's. What belongs here is
  the `MaxFrameLength`-from-MTU wiring and the `(mtu - 1) / 2 - 1` chunk calculation.

---

## Implementation plan

Independently shippable phases (no time estimates, per this repo's convention).

**Phase 0 — the package format.** `FirmwareFormat.NordicZip`, its content sniff, and the unwrap
to a `.dat` / `.bin` pair. Testable on a package file alone, with no protocol and no device.

**Phase 1 — the family core.** Op codes, encode, decode, the planner, `INordicDfuTransport`.
Entirely pure, entirely tested, no transport.

**Phase 2 — the serial leaf.** `SlipNordicDfuTransport`, the `Ping` / `MTU Get` / `Set PRN`
handshake, `NordicDfuProgrammer`, the provider, CLI registration. First end-to-end flash, on a
dongle over USB CDC.

**Phase 3 — measurement.** The dongle's bootloader ids (OQ-2), and whether path B earns
`Passive`.

**Deferred — the BLE leaf.** Blocked on ADR-0085 shipping a GATT client, and on OQ-5.

---

## Open Questions

- **OQ-1 — the op-code and result-code values are unpinned.** Everything in
  [The wire protocol](#the-wire-protocol) is recalled rather than read off a header. Pin the
  table against Nordic's published DFU protocol documentation before phase 1 writes a byte.
  Getting a single value wrong produces a device that answers `op code not supported` and no
  obvious reason why.
- **OQ-2 — the dongle's bootloader VID/PID is unmeasured.** Path B's identification mode, and
  therefore its autoflash eligibility, depends on it. Ships `Probe` until an nRF52840 Dongle is
  enumerated in bootloader mode and the ids are recorded.
- **OQ-3 — the BLE leaf's name.** `Periphery.Bootloader.Nordic.Ble` would carry a third-party
  runtime dependency under a `Periphery.*` name, which is the row ADR-0085 D1 used to refuse
  `Periphery.Bluetooth`, and which ADR-0061 DEC-006 narrowed to first-party only. Either the
  leaf takes an `.InTheHand` suffix or DEC-006 widens. This is a decision for an `adr.md`, not
  for whoever happens to create the project.
- **OQ-4 — TFM contamination from the BLE leaf.** ADR-0085 D3 pins `Periphery.Ble.InTheHand` at
  `net10.0;net10.0-windows;net10.0-windows10.0.19041` with no `net8.0`. Any bootloader package
  above it inherits that set and becomes the only member of the family that cannot ship
  Periphery's standard TFMs, with a Linux asset that resolves to BlueZ. Decide whether that is
  acceptable before building, not after.
- **OQ-5 — rebinding an LE peripheral across two resets.** A BLE DFU run crosses the buttonless
  reboot into the bootloader and the reset back into the application. In the unbonded case the
  bootloader advertises at the device address plus one, so the host must rescan and rebind.
  ADR-0085 D7 §3 defers `BleDeviceProxy` on ADR-0083, so there is no proxy, no activation
  window, and no `IRecoveryPolicy` for this — and [ADR-0088](../../../adr/0088-open-handles-on-activity-not-presence.md)
  D3 says a hand-rolled reconnect is the case consumers get wrong. **This, not GATT, is what
  actually blocks path C.**
- **OQ-6 — does `Periphery.Serial` open a CDC ACM port cleanly on all three platforms?** Path B
  assumes the dongle presents as an ordinary serial port. Unmeasured on Windows and macOS.
- **OQ-7 — packet receipt notifications.** `Set PRN(0)` disables them and leaves the per-chunk
  CRC as the only checkpoint, which is simplest. A non-zero PRN gives finer-grained progress and
  earlier failure detection at the cost of round trips. Measure before choosing; the planner can
  gain it later without a shape change.
- **OQ-8 — MCUboot as its own family.** [Scope](#scope-nrf5-sdk-secure-dfu-not-mcuboot) argues
  it is `Periphery.Bootloader.McuBoot` rather than a Nordic transport. Confirm that in an ADR
  before someone files SMP as `Periphery.Bootloader.Nordic.Smp` and puts two unrelated wire
  protocols behind one family core.
- **OQ-9 — `SlipCodec.MaxFrameLength`.** It defaults to 1006, and the Nordic serial MTU is
  device-reported. The transport must construct the codec from the negotiated MTU, which means
  the codec cannot be created until after `MTU Get` completes. Check that against
  `CallAndResponse`'s framing lifecycle; if a codec must exist before the first exchange, the
  handshake needs a second codec instance or a mutable bound.

---

## Related

- [ADR-0061 — Firmware-flashing platform](../../../adr/0061-firmware-flashing-platform.md) — the
  platform, the taxonomy (DEC-002), the flasher dependency tier (DEC-001), the first-party
  `CallAndResponse` carve-out (DEC-006), and the roadmap entry this spec fills in (DEC-007).
- [ADR-0085 — The 32feet binding is two integration packages](../../../adr/0085-the-32feet-binding-is-two-integration-packages.md) —
  D7 declines a GATT abstraction and defers `BleDeviceProxy`; the source of OQ-3, OQ-4, and OQ-5.
- [ADR-0083 — BLE identity does not survive re-pairing](../../../adr/0083-ble-identity-does-not-survive-repairing.md) —
  why OQ-5 is a measurement and not a design argument.
- [ADR-0062 — Periphery.Serial backend-provider](../../../adr/0062-periphery-serial-backend-provider.md) —
  the transport for paths A and B, and the backend-choice measurement behind it.
- [ADR-0063 — Bootloader entry / mode switch](../../../adr/0063-bootloader-entry-mode-switch.md) —
  the shape the buttonless BLE entry takes.
- [ADR-0052 — Functional core / imperative shell](../../../adr/0052-periphery-treehopper-pure-core.md) —
  the split [Architecture](#architecture-functional-core--imperative-shell--adr-0052) follows.
- [ADR-0024 — Extension package pattern](../../../adr/0024-extension-package-pattern.md) — the
  no-third-party-runtime-deps row that OQ-3 turns on.
- [Image formats spec](../image-formats/spec.md) — already lists the Nordic `.zip` as a Kind-2
  packaged blob; phase 0 implements that row.
- [ESP32 spec](../esp32/spec.md) — the sibling family, and the precedent for holding
  `IdentificationMode` at `Probe` until ids are measured.
- [Autoflash spec](../autoflash/spec.md) — the gate `IdentificationMode` feeds.
