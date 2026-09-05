---
title: "ADR-0086: The peripheral-config stream resynchronises by draining to a packet boundary"
status: "Accepted"
status_note: "Shipped. All four D5 tests closed; dist/ regenerated to v2.77 (14827 bytes, top 0x39EB) with a matching .tfi."
date: "2026-09-04"
authors: "@charles8051"
tags: ["architecture", "decision", "firmware", "treehopper", "usb", "flash", "data-loss"]
supersedes: ""
superseded_by: ""
depends_on: "ADR-0075 (out-of-band soft reset - same endpoint, same wedged-foreground family), ADR-0052 (pure-core pattern - the host half of D2 lives in the codec), ADR-0061 (flash-anything: dist/ is a released artefact)"
---

# ADR-0086: The peripheral-config stream resynchronises by draining to a packet boundary

## Status

**Accepted and shipped.** The source change has landed in
`src/Periphery.Treehopper/firmware/Treehopper-EFM8` and in the host codec, and
D5's gate has cleared: all four bench tests are closed, so `dist/Treehopper.hex`
and `dist/treehopper.tfi` are regenerated at **v2.77** (14827 bytes, top
`0x39EB`, 21 under the `0x3A00` ceiling).


## Context

Issue #170. On one production station, two of three Treehopper
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
checked it. `SER_ADDR 0xF800` and `NAME_ADDR 0xF840` are one 64-byte scratchpad
page apart (D5 test 1), and the record carries a three-byte header, so the payload
cannot exceed **61** bytes. A longer **serial** write runs from `0xF803` through
`0xF901`, straight across the unerased name page at `0xF840`-`0xF87F` and
AND-corrupting it, then on into scratchpad pages the firmware does not own; a
longer **name** write does the same to everything above `0xF87F`. In the desync
scenario `[1]` was frequently the APA102 header `0xFF`, asking for a 255-byte
write.

**Corrected 2026-09-05.** This previously repeated the issue's claim that such a
write reaches "the lock byte - where a zero can permanently lock the part." It
cannot. `BL_LOCK_ADDRESS` is `0xFBFF` and `len` is a single byte, so the furthest
any write reaches is `0xF941`. The bound is still right and still necessary; the
consequence was overstated.

The firmware now **rejects** — not truncates — a length over 61. A silently
shortened identity is still corruption.

The host had the mirror-image bug: `IdentityBytes` wrote `text.Length`, a UTF-16
**char** count, as the length of a UTF-8 **byte** payload, and capped neither.
`TreehopperWire.IdentityMaxBytes` now states the bound once;
`IdentityBytes` sends the byte count and throws past it, and
`UpdateNameAsync` / `UpdateSerialAsync` validate in bytes rather than in
characters. Thirty-one two-byte characters is 31 characters and 62 bytes — under
the old bound, over the real one.

### D3. The validity marker is written last, and validity is the whole header

Byte `[0]` is the record's validity flag, and it was written **first**. Any
interruption after byte 0 and before the payload left a record that looked valid
forever: self-repair was dead and the damage survived every reboot, which is
exactly the durability observed in the field. It is now written after the
payload.

**Corrected 2026-09-05.** This decision previously claimed that "an interrupted
write leaves `[0] == 0xFF` and the next boot regenerates the string." That is
true of an interruption during **programming** and false of one during the
**erase**: the erase precedes everything, and a brownout part-way through it can
leave `[0]` reading something other than `0xFF` over a payload that is already
gone. Marker-last is not transactional across an erase, and no ordering of
single-byte writes can be. D4's supply monitor narrows that window; it cannot
close it, because no erase is atomic.

So validity stops being one byte. `SerialNumber_Init` now checks the whole
three-byte header - the packed-encoding marker at `[0]`, an even length at `[1]`
between 4 and `(61+1)*2`, and the descriptor type at `[2]` - and regenerates
unless all three hold. A partially-erased page fails at least one with high
probability.

**Deliberately conservative**, because the failure mode of being too strict is
worse than the one it fixes: a healthy record wrongly rejected means a board
silently changing its identity. Every field checked is fixed or tightly bounded
by construction, so nothing this firmware could have written can fail the test.
Confirmed on hardware - a bench board flashed with this change kept its serial
`cDYhINBh` across the update.

**And it is not hypothetical.** D5 test 4 found two boards at
those two boards in precisely the falsely-valid state right now: marker present,
record unserveable, no self-repair across four days and many reboots. Under the
old one-byte test they stay broken forever. Under this one they regenerate on the
first boot after the update - which means the fix reaches boards already damaged,
not only boards not yet damaged.

### D4. The supply monitor is enabled and selected before any flash write

`VDM0CN` was never written and the supply monitor was never selected in `RSTSRC`
at init. The reference manual requires both before any flash write or erase;
without them a supply dip leaves cells *partially programmed* rather than
resetting the part. `flash_armVddMonitor()` does it once per config-page update,
which is the only place this firmware writes flash.

**Corrected 2026-09-05 - this decision loses its supporting symptom, and keeps
its justification.** It previously read as the leading candidate for a second,
lower-severity symptom seen on all three boards including the healthy one:
serial strings whose letter case changed between reads - four of the eight
characters flipped, every flip setting bit 5, i.e. drifting toward the erased state.

D5 test 3 dissolved that. The case difference is a host-side presentation
artefact: the stored serial genuinely is mixed case (`generateRandomString`
draws from `0-9`, `A-Z` and `a-z`), C2 reads it byte-identically five times
running, and one `--verbose` log shows the same board reported as `cDYhINBh`,
`CDYHINBH` and - for its neighbour - `imnuz6yw` and `IMNUZ6YW`, simultaneously,
because Windows normalises instance ids differently per API. Every example in
the issue is the uppercase of its own "after" reading, digits untouched. And
"every flip sets bit 5" is tautological: bit 5 **is** the ASCII case bit, so any
upper/lower pair differs in exactly that bit whatever caused it.

**The decision stands unchanged.** The reference manual requires the supply
monitor enabled and selected before any flash write or erase, and that is
sufficient reason on its own. Nothing here varied VDD or temperature, so a
marginal-cell effect at the extremes is unevidenced rather than excluded - but
this is no longer the explanation for anything observed, and must stop being
presented as one.

### D5. The bench gate, and what it cost to clear

`dist/` was held back until four things were true, because those files are what
`treehopper-flash` writes to real boards. All four are now closed, three of them
without the hardware the test list assumed.

1. **Page size at `0xF800` - 64 bytes.** From the AN945 bootloader's device
   header for `EFM8UB10F16G_QFN28`: `BL_FLASH0_PSIZE 512` for code flash,
   `BL_FLASH1_PSIZE 64` for the scratchpad. `SER_ADDR` and `NAME_ADDR` are one
   scratchpad page apart, so the erase-overlap hypothesis stays ruled out and
   D2's bound stands. Confirmed on silicon too: two `rename` cycles rewriting
   `0xF840` left `0xF800` byte-identical over C2.
   **The trap:** the SDK's generic `FlashPrimitives` examples say 512, which
   describes the code region only - and 512 inverts the conclusion, because
   `0xF800` is 512-aligned. Only the per-part header separates the regions.
2. **Reproduce the desync - reproduced, and the fix stops it.** One board, one
   variable, image verified both ways: `dist` desynced 30/30 at a 250 ms stall;
   the fixed build 0/30 at 250 ms and 0/100 at 600 ms. Spin budget measured
   210-220 ms, knee sharp between 200 and 235.
3. **Case flips - a host artefact, not the flash.** Five byte-identical C2 reads
   while one host log reports the same serials in three cases at once. The stored
   form is the mixed-case one. Caught in the act in the field logs too:
   the serial read one way at `03:02:25.072` and with four characters case-flipped at
   `03:02:37.793`, same board, across
   one re-enumeration. D4 keeps its justification and loses its symptom.
4. **Read the damaged boards - answered from artifacts already off the station.**
   the station's uploaded diagnostic snapshots carry the garbage name byte-for-byte
   (`06 FF 0B 09 06 FF 0B 09 06`, both boards) and both waves of bootloader
   entries. They also **move the timeline**: at `03:02:21`, four seconds before
   the first bootloader entry, both boards already carry the garbage name and
   already enumerate by port path. The descriptor damage is a separate, earlier
   event. And they serve **no** serial rather than a garbage one - which is field
   evidence for D3, since `SerialNumber_Init` regenerates whenever byte `[0]` is
   `0xFF` and these have not self-healed in four days. `[0]` is present while the
   record is unserveable: valid forever to the firmware, invalid to the stack.

The one reading not taken is the raw `iSerialNumber` descriptor bytes from those
two boards, which needs code running on a production station. It would sharpen
D3's field evidence and change no decision, so the gate does not wait for it.
`scratch/TreehopperIdentityProbe` is built and validated against C2 if it is ever
wanted.

**The `.tfi` is no longer a blocker either.** `hex2boot` is still not in the repo,
but it no longer needs to be: `Efm8BootRecordGenerator` with
`Efm8BootOptions.Ub1` is an in-repo replacement, driven by `scratch/Hex2Tfi`.
Positive control - regenerating from the *old* committed `.hex` reproduces the
*old* committed `.tfi` byte for byte - so the generator is doing what hex2boot
did. The new pair is 14827 HEX bytes / 15433 boot-record bytes in 120 records,
and the bench board that passed test 2 verifies MATCH against the shipped
`dist/Treehopper.hex`.

**Ceiling checked by hand, as `BUILD.md` requires.** Top is `0x39EB`, under
`0x3A00`. `hex2boot -m ub1` and its C# mirror still assume `0x3DFF` (#100), so
this check is not delegated.

### D5b. `bcdDevice` bumps to v2.77 with the source change, not with the release

`descriptors.c` goes from `0x0114` to `0x0115`. A 2.76 board executes pixel data as commands
on the abort path and a 2.77 board does not, and that distinction is the only thing this word
is for.

**It bumps now, while `dist/` stays put, and the two are not in tension.** The comment this
change replaces records the last time the other order was chosen: the watchdog work (#226,
#227, #233) landed on top of the v2.75 release without a bump, so a 2.75 board could be the
released image or any of them. The identical problem recurred while closing D5 test 2 on
2026-09-04 — two boards on one bus reporting the same version, one of which turned out not
to match `dist/Treehopper.hex`, and separating them took rebooting a board into its
bootloader to run a flash verify. An unreleased image sitting on a bench board is exactly the
case that needs a distinct version, not an exemption from one.

With the bump, `REV_0113` / `REV_0114` / `REV_0115` read straight off the bus as v2.75 /
v2.76 / v2.77, and everything at or below `0114` is vulnerable.

Nothing else needed a version change: package versions are MinVer/tag-driven, and
`TreehopperControlOptions.FirmwareTargetVersion` is supplied by the caller rather than
defaulted in source.

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
| with the framing fix | 14774 | `0x39B6` | 74 |
| as shipped, after the review | 14827 | `0x39EB` | **21** |

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
