---
title: "ADR-0086: The peripheral-config stream resynchronises by draining to a packet boundary"
status: "Accepted"
status_note: "Source change landed; the image is NOT released - dist/ is unchanged pending the bench tests in D5."
date: "2026-09-04"
authors: "@charles8051"
tags: ["architecture", "decision", "firmware", "treehopper", "usb", "flash", "data-loss"]
supersedes: ""
superseded_by: ""
depends_on: "ADR-0075 (out-of-band soft reset - same endpoint, same wedged-foreground family), ADR-0052 (pure-core pattern - the host half of D2 lives in the codec), ADR-0061 (flash-anything: dist/ is a released artefact)"
---

# ADR-0086: The peripheral-config stream resynchronises by draining to a packet boundary

## Status

**Accepted.** The source change has landed in
`src/Periphery.Treehopper/firmware/Treehopper-EFM8` and in the host codec.
`dist/Treehopper.hex` and `dist/treehopper.tfi` are **deliberately unchanged** —
see D5.

## Context

Issue #170. On shred-vault station `SV3-01-ENMOVS6`, two of three Treehopper
boards on one hub permanently lost both their `iProduct` and `iSerialNumber`
descriptors on 2026-09-01, and all three entered their bootloaders within eleven
seconds of each other. The corruption re-reads from EEPROM on every enumeration.
It survived four host cold reboots and a `0x0C` firmware reboot, returning
byte-identical garbage each time. `verify` against `dist/Treehopper.hex` reports
MATCH on both boards: the application image is intact, only the config page is
damaged.

The damaged `BusReportedDeviceDesc` decodes to a period-4 repeat of
`0B 09 06 FF`. That is an APA102 pixel group — `Apa102Encoder` emits
`header, B, G, R` per pixel, and at brightness 31 the header is `0xFF`, so a dim
blue-white pixel is `FF 0B 09 06` — **entered one byte late, at the Blue
channel**. The Blue byte `0x0B` landed at `Treehopper_PeripheralConfig[0]` and
was executed as `FirmwareUpdateName`; the Green byte `0x09` became the length.
The board wrote its own pixel data over its name page.

Two facts turn that from a curiosity into a hazard class:

1. **Every low colour-channel value is a live opcode.** `0x01` ConfigureDevice,
   `0x0A` FirmwareUpdateSerial, `0x0B` FirmwareUpdateName, `0x0C` Reboot,
   `0x0D` EnterBootloader, `0x0E` LedConfig. **Dim frames are more dangerous
   than bright ones**: a bright frame sits at `0xFF`, which is not an opcode; a
   dim one sweeps the whole command range. Which boards failed matches which
   animation they ran — the two that failed are the two whose animation sweeps a
   channel slowly through the low opcode range.
2. **`ProcessPeripheralConfigPacket` has no framing.** No length prefix, no
   sequence number, no magic, no opcode validation.
   `switch (Treehopper_PeripheralConfig[0])` executes whatever byte is there.

`Apa102Strip.FlushAsync` chunks at 252 bytes, so the firmware packet is
7 + 252 = 259 bytes — USB packets of 64/64/64/64/3. **Every animation tick takes
the multi-packet path, all day.** On that path the firmware armed a continuation
read at `&Treehopper_PeripheralConfig[64]`, spun on a fixed budget, and then ran
`USBD_AbortTransfer` *unconditionally*. On the healthy path that abort is a
no-op. On a timeout it discards up to two whole in-flight packets and clears
`outPacketPending` while the host is still sending the rest of the command — and
the re-arm at the bottom of the same function went straight back to offset 0. The
next surviving packet, pure pixel data, landed where an opcode was expected.

There is a second path to the same state with no timeout involved:
`USBD_DeviceStateChangeCb` re-arms the endpoint at offset 0 from the USB ISR on
every transition to CONFIGURED, which can happen while the foreground owns the
continuation read.

Three further defects sit in the same write path and are addressed here because
each one converts a transient desync into permanent damage: `writeUsbString`
never bounded its length, the record's validity marker was written *first*, and
the supply monitor was never enabled as a reset source before a flash write.

## Decision

### D1. Resynchronise by draining to a packet boundary, not by framing the protocol

**The stream keeps its current shape.** No magic byte, no sequence number, no
length prefix, and no confirmation token on the destructive opcodes.

Adding framing would have been a stronger guarantee and was rejected on
compatibility: `0x0C` and `0x0D` are how *every* Treehopper host — including the
upstream SDK and third-party flashing tools — reboots a board and puts it in its
bootloader. A magic byte on those opcodes silently breaks all of them, and the
device offers no way to negotiate a protocol version.

Instead the firmware now treats loss of framing as a state it can be *in* and
recover *from*:

- The unconditional `USBD_AbortTransfer` is gone. The abort now runs **only when
  the read actually failed** — which is the only case where it does anything —
  and latches `Treehopper_PeripheralConfigDesync`.
- The transaction is skipped on that path. Its payload is incomplete by
  definition, and running an SPI or I²C transaction over half a buffer was
  itself a defect.
- While desynchronised, `ProcessPeripheralConfigPacket` **discards** packets
  instead of executing them, until a **short packet** arrives. A short packet is
  the host's own end-of-transfer marker and therefore the next boundary the
  device can trust. `USBD_XferCompleteCb` records it; the offset-0 re-arms now
  pass `callback: true` purely to make that signal exist.

**Known limit, accepted.** If the abandoned command happens to be an exact
multiple of 64 bytes, no short packet ends it and the drain eats the first packet
of the *next* command, resynchronising on that command's short tail. One dropped
command is a far better failure than executing pixel data as `EnterBootloader`.
The 259-byte APA102 case that caused #170 is not a multiple of 64.

### D2. The identity length is a UTF-8 byte count, bounded at 61 on both ends

`writeUsbString` took its length from `Treehopper_PeripheralConfig[1]` and never
checked it. `SER_ADDR 0xF800` and `NAME_ADDR 0xF840` are one 64-byte page apart,
and the record carries a three-byte header, so the payload cannot exceed **61**
bytes. A longer name write ran past `0xF87F` into the unerased reserved region
that holds bootloader data and the lock byte — where a zero can permanently lock
the part. A longer serial write spilled into the name page without erasing it and
AND-corrupted it. In the desync scenario `[1]` was frequently the APA102 header
`0xFF`, asking for a 255-byte write.

The firmware now **rejects** — not truncates — a length over 61. A silently
shortened identity is still corruption.

The host had the mirror-image bug: `IdentityBytes` wrote `text.Length`, a UTF-16
**char** count, as the length of a UTF-8 **byte** payload, and capped neither.
`TreehopperWire.IdentityMaxBytes` now states the bound once;
`IdentityBytes` sends the byte count and throws past it, and
`UpdateNameAsync` / `UpdateSerialAsync` validate in bytes rather than in
characters. Thirty-one two-byte characters is 31 characters and 62 bytes — under
the old bound, over the real one.

### D3. The validity marker is written last

Byte `[0]` is the only thing `SerialNumber_Init` tests, so it is the record's
validity flag — and it was written **first**. Any interruption after byte 0 and
before the payload left a record that looked valid forever: self-repair was dead
and the damage survived every reboot, which is exactly the durability observed in
the field. It is now written after the payload, so an interrupted write leaves
`[0] == 0xFF` and the next boot regenerates the string.

### D4. The supply monitor is enabled and selected before any flash write

`VDM0CN` was never written and the supply monitor was never selected in `RSTSRC`
at init. The reference manual requires both before any flash write or erase;
without them a supply dip leaves cells *partially programmed* rather than
resetting the part. `flash_armVddMonitor()` does it once per config-page update,
which is the only place this firmware writes flash.

This is the leading candidate for a second, lower-severity symptom seen on all
three boards including the healthy one, and predating the total failure: serial
strings whose letter **case** changed between reads (`VOQXRNTN` → `vOQxrntn`).
Every flip sets bit 5, i.e. drifts toward the erased state — what a weakly-erased
cell does and what a write-without-erase cannot do. This station's hub runs on
mains passthrough and loses power on any mains dip. **Candidate, not conclusion**
— D5 says what would settle it.

### D5. The source change ships; the image does not

`dist/Treehopper.hex` and `dist/treehopper.tfi` are **not** regenerated by this
change. They are what `treehopper-flash` writes to real boards, and #170's own
"needs a bench test" list is not closed:

1. **Confirm the EFM8UB10F16G page size at `0xF800`.** If it is 512 bytes rather
   than 64, the erase-overlap hypothesis returns as a primary cause and D2's
   arithmetic changes.
2. **Reproduce the desync directly** — APA102 flush loop at 252-byte chunks, an
   analyser on EP2 OUT, stall the host to force the abort path, and watch a pixel
   byte land at `buf[0]`. This is also the test for D1. **Harness written:**
   `scratch/Apa102Desync`, procedure in
   `docs/investigations/2026-09-treehopper-peripheral-config-desync.md`. It aims a
   deliberately inert canary (`0x01 ConfigureDevice`) at `buf[0]` rather than a
   destructive opcode, detects execution from the EP1 IN pin-report stream rather
   than from the analyser, and refuses to run at all unless a positive control
   fires first. Not yet run against hardware.
3. **Case flips over C2** at varying VDD and temperature. Stable over C2 while
   USB reads vary points at the serve path; drifting over C2 confirms D4.
4. **Read `0xF800-0xFBBF` over C2 on the two damaged boards before reflashing**,
   in particular the lock byte, given D2's unbounded write.

Regenerating the `.tfi` also needs `hex2boot`, which is not in the repo
(`docs/explorations/treehopper-firmware-update.md`). Shipping a `.hex` without
its matching `.tfi` would leave `dist/` internally inconsistent.

### D6. Flash headroom was bought, not borrowed

The EFM8UB1 app region ends at **`0x3A00`**, and the baseline image already sat
at `0x39A0` — 96 bytes free. The changes above cost more than that, so two
size-neutral simplifications were made in the same commit rather than trading
away any part of the fix:

- The enumeration blink in `USBD_DeviceStateChangeCb` was six spelled-out
  `LED_SetVal`/delay pairs; it is now the same six iterations in a loop.
  Identical sequence and timing. Its counter lives in **XDATA** because LX51
  cannot overlay that function's locals and DATA is full to the byte — a plain
  `uint8_t` there fails the link with `L107 ADDRESS SPACE OVERFLOW`.
- `configureDevice(uint8_t)` ignored its argument and called `Treehopper_Init()`.
  Inlined at its single call site.

Measured with the same toolchain and flags (C51 V9.60, `OPTIMIZE(9,SIZE)`),
computed from the actual HEX records per `BUILD.md`:

| | HEX bytes | Top | Free to `0x3A00` |
|---|---|---|---|
| baseline (matches `dist/Treehopper.hex`) | 14752 | `0x39A0` | 96 |
| with this change | 14774 | `0x39B6` | **74** |

## Consequences

**A desynchronised stream now costs a dropped command instead of a destroyed
board.** That is the whole point, and it is a strict improvement on both known
desync paths.

**Two commands can be dropped where one was executed wrongly.** The drain
deliberately discards, and on an exact-multiple-of-64 command it discards into
the next one. Hosts already treat a peripheral-config write as fire-and-forget
with no acknowledgement, so a dropped command surfaces as a missed animation
frame or a timed-out transaction, not as an error the host can distinguish. A
protocol with acknowledgements would do better; see D1 for why this one does not
have them.

**A name or serial over 61 UTF-8 bytes is now an `ArgumentException` rather than
a corrupted board.** `BoardRename` already restricted names to printable ASCII
and to 60 characters, so no working caller changes. Its ASCII restriction stays:
D2 fixes the length byte, but the EFM8 stack still widens each stored byte back
to a UTF-16 code unit on read, so a multi-byte character still reads back
mangled.

**Enabling the supply monitor as a reset source changes reset behaviour.** A
board that previously browned out through a mains dip mid-write will now reset
instead. That is the intended behaviour and the point of D4, but it is a
behaviour change on hardware that has never had it.

**Field boards are not fixed by this commit.** Until D5's bench tests pass and a
release regenerates `dist/`, the workaround stands: `treehopper-flash rename`
rewrites the config page and repairs the descriptor. Confirmed on one of the two
damaged boards on 2026-09-04 — `BusReportedDeviceDesc` went from garbage to
`DepositChamber`. It does **not** restore the serial, and on Windows the cached
`DEVPKEY_Device_FriendlyName` still needs a devnode rebuild before the host
reports the new name.
