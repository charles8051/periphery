# Treehopper firmware-update (reflash) pipeline — survey

Status: exploration / survey only. Nothing implemented. Goal: a reproducible
how-to for building a patched EFM8 Treehopper firmware and flashing it from our
own stack (Periphery.Hid), so we can test a fix for the EFM8 SPI-FIFO lock-up.

This is a reverse-engineering survey of a local clone of `treehopper-sdk`
(read-only) plus the SiLabs AN945 toolchain. All `path:line` citations below
are into `treehopper-sdk` unless marked otherwise.

## TL;DR pipeline

1. Build firmware in Keil C51 (Simplicity Studio project) -> `Treehopper.hex`
   (Intel HEX).
2. `hex2boot.exe -o treehopper.tfi -m ub1 -b 0 Treehopper.hex` -> `.tfi`
   (a renamed SiLabs `.efm8` bootload-record file: a sequence of `$`-framed
   bootloader command records).
3. Host calls Treehopper API `RebootIntoBootloader()` -> sends peripheral-config
   packet `[0x0D]` (`EnterBootloader`).
4. Firmware writes signature byte `0xA5` to internal address `0x00`, then forces
   a reset (`RSTSRC`); the running app drops off USB.
5. The on-chip SiLabs factory bootloader (AN945) sees the signature and
   enumerates as an HID device at VID/PID `0x10C4:0xEAC9` (app is
   `0x10C4:0x8A7E`).
6. Host opens the HID bootloader, walks the `.tfi` file record-by-record, and
   sends each `$`-framed record over 64-byte HID output reports.
7. After each record the bootloader returns a 1-byte status in a HID input
   report; `0x40` (`@`) = ACK / success, anything else = error.
8. The record stream itself carries Setup -> Erase -> Write -> Verify(CRC16) and
   finally Run-application; hex2boot emits those records, so the host just
   replays bytes and checks ACKs.
9. On the final Run-app record the bootloader resets into the freshly written
   app; the device re-enumerates as `0x10C4:0x8A7E`.
10. We reuse hex2boot to make the `.tfi`; we write new code only for the
    record-replay-over-HID loop (port the ~130-line
    `FirmwareUpdateDevice.Load` onto Periphery.Hid).

## 1. Bootloader entry

### API surface (host)

- .NET: `TreehopperUsb.RebootIntoBootloader()` —
  `NET/API/Treehopper/TreehopperUsb.cs:397`. Body:
  `SendPeripheralConfigPacketAsync(new[] { (byte)DeviceCommands.EnterBootloader }); Disconnect();`
- Python: `TreehopperUsb.reboot_into_bootloader()` —
  `Python/treehopper/api/treehopper_usb.py:344`, sends
  `[DeviceCommands.EnterBootloader]` then disconnects.
- Java: `DeviceCommands.EnterBootloader` exists
  (`Java/.../io/treehopper/DeviceCommands.java`).
- Our port: `Periphery.Treehopper.TreehopperBoard.RebootIntoBootloaderAsync(...)`
  / `Wire.Command.EnterBootloader` (confirmed in
  `repos/periphery/src/Periphery.Treehopper/.../Periphery.Treehopper.xml:938,1430`)
  already matches upstream.

### Wire opcode

`EnterBootloader` is the `DeviceCommands` enum value. Counting the enum in
`NET/API/Treehopper/DeviceCommands.cs:6` (Reserved=0 ... Reboot=12,
EnterBootloader=13): **opcode = 13 = 0x0D**. The firmware enum agrees exactly —
`Firmware-EFM8/inc/treehopper.h:34` `GlobalCommands` lists the same 17 members in
the same order (EnterBootloader is the 14th = 13).

The command is delivered as the first byte of a peripheral-config packet on the
`EP_PeripheralConfig` (EP2 OUT) endpoint
(`Firmware-EFM8/inc/treehopper.h:127`). Python confirms it is just a one-byte
payload `[0x0D]` (`treehopper_usb.py:354`).

### What the firmware does

`Firmware-EFM8/src/treehopper.c:261-266`:

```c
case EnterBootloader:
    USBD_Stop();
    *((uint8_t SI_SEG_DATA *) 0x00) = 0xA5;   // bootloader-enable signature
    SFRPAGE = 0x00;
    RSTSRC = RSTSRC_SWRSF__SET | RSTSRC_PORSF__SET;  // force reset
    break;
```

So the app: (1) stops USB, (2) writes signature byte **`0xA5` to internal DATA
address `0x00`** (`SI_SEG_DATA` = 8051 internal RAM, not XRAM/flash; this RAM byte
survives a soft reset and is the flag the factory bootloader checks at boot),
(3) triggers a software + power-on reset. Contrast with the plain `Reboot`
case (`treehopper.c:255`) which does the same reset **without** writing the
signature, so it reboots straight back into the app. This confirms the
mechanism: signature-byte-in-retained-RAM + reset, exactly the AN945
"application-requested bootloader entry" pattern (the AN945 bootloader, on
reset, checks a fixed RAM location for a magic value and stays in the
bootloader if present).

## 2. Bootloader identity

- App firmware USB id: **`0x10C4:0x8A7E`**
  (`Firmware-EFM8/inc/descriptors.h:28,32`; host side
  `Python/treehopper/api/settings.py:6-7`, `find_boards.py:15`).
- Bootloader USB id: **`0x10C4:0xEAC9`**
  (`NET/API/Treehopper.Firmware/FirmwareConnectionService.cs:31-36`:
  `BootloaderPid = 0xeac9`, `BootloaderVid = 0x10c4`). Matches our port's
  documented DFU id.
- Class: the host opens it via HidSharp `DeviceList.Local.GetHidDevices(...)`
  (`FirmwareConnectionService.cs:40`) and talks to it with HID output/input
  reports (`FirmwareUpdateDevice.cs:66,109,114`). So it enumerates as a
  **generic HID device, not CDC/DFU-class**. This is the **SiLabs EFM8 factory
  USB HID bootloader (AN945)**, the one baked into EFM8UB1 ROM/top-of-flash.
- MCU: **EFM8UB10F16G** (EFM8 Universal Bee, 16 KB flash), per
  `Firmware-EFM8/Treehopper.hwconf` (`partId ... efm8ub10f16g-b-qfn28`). VID
  0x10C4 is Silicon Labs (`Firmware-EFM8/lib/efm8_usb/inc/efm8_usb.h:608`).
- No bootloader image is shipped in the repo (no `.efm8`/`.hex` bootloader
  blob found) because it is the factory bootloader already resident on the
  chip; only the **app** `.tfi` is checked in
  (`NET/API/Treehopper.Firmware/treehopper.tfi`, ~12.5 KB).

## 3. The HID bootloader protocol (AN945)

### Frame format

`$` (0x24) start byte, then 1 length byte (count of bytes that follow the
length byte, i.e. command + data), then the command byte, then the payload:

```
0x24  LEN  CMD  DATA...
```

Confirmed two ways: the upstream host loader parses exactly this
(`FirmwareUpdateDevice.cs:67-74`: reads 2-byte header `[ '$', len ]`, then
`len` more bytes, asserts `header[0]=='$'` and `header[1]==frame.Length-2`),
and the AN945 doc / community implementations state "frames start with `$`,
1 byte length, 1 byte command, x bytes data"
(cjacker/hex2boot, cjacker/efm8load).

### Command set (AN945)

Command bytes (from the AN945 protocol, as implemented in BarnabyShearer/efm8
and cjacker/hex2boot):

| Cmd | Byte | Payload | Purpose |
|-----|------|---------|---------|
| Identify | 0x30 | `BL_id` (1 byte) | Check the bootloader matches the target derivative; bootloader compares the id and NAKs a mismatch (the chief brick-guard). |
| Setup    | 0x31 | keys + bank (e.g. `0xA5 0xF1 bank`) | Unlock flash for erase/write; sends the flash keys + bank select. |
| Erase    | 0x32 | `addrHi addrLo` [+ data] | Erase the flash page at addr (and optionally write trailing data). |
| Write    | 0x33 | `addrHi addrLo` + data | Write data bytes to flash starting at addr. |
| Verify   | 0x34 | `startHi startLo endHi endLo crcHi crcLo` | Bootloader computes CRC16 over [start,end] and compares to supplied CRC. |
| Lock     | 0x35 | `sig lock` | Write the signature byte and/or flash lock byte. |
| RunApp   | 0x36 | `optionHi optionLo` | Reset and run the application (or stay in bootloader). |

CRC for Verify is **CRC-16/CCITT-XMODEM** (poly 0x1021, init 0x0000) — the
algorithm cjacker/efm8load and BarnabyShearer/efm8 use for the bootloader's
verify command.

### Report sizes / direction (mapped to Periphery.Hid)

From the upstream HID loader (`FirmwareUpdateDevice.cs:17-18,103-116`):

- **Output reports**, 64-byte payload (`SizeOut = 64`). A long frame is split
  into successive 64-byte chunks, each written with a leading report-ID byte
  `0x00` (HidSharp prepends report id; buffer is `SizeOut + 1 = 65` bytes,
  `buffer[0]=0`). Direction: host -> device.
- **Input reports**, 4-byte payload (`SizeIn = 4`): after sending a whole
  frame the host reads one input report of `SizeIn + 1 = 5` bytes and
  discards byte 0 (report id), taking the next 4. Direction: device -> host.
  The status/ACK is the first of those bytes.
- **ACK semantics** (`FirmwareUpdateDevice.cs:78`): success is the byte `'@'`
  (0x40). Community loaders accept `@ABC` (0x40-0x43) as the success/range of
  status codes; non-`@` is an error (e.g. RANGE, BADID, CRC fail).
- These are **interrupt output/input reports, not feature reports**, and the
  **report ID is 0** (the leading 0 byte HidSharp prepends).

Map to Periphery.Hid (`repos/periphery/src/Periphery.Hid/HidDevice.cs`):

- chunk write -> `WriteReportAsync(new HidReport(0x00, chunkOf64))` (line 97).
  Periphery's `HidReport(reportId, data)` carries the report id explicitly
  (`HidReport.cs:20`), so build it with id `0` and the 64-byte chunk; the
  backend handles the on-wire framing.
- status read -> `ReadReportAsync()` -> inspect `report.Data` first byte for
  `0x40` (line 83). `MaxInputReportLength`/`MaxOutputReportLength` (lines
  62,65) let us assert the 64/4 sizes before starting.
- **Not** `WriteFeatureReportAsync` / `ReadFeatureReportAsync` — the protocol
  uses interrupt (not feature) reports.

## 4. Image conversion — the core unknown (resolved)

### What the build emits

Keil C51 (the EFM8 8051 toolchain, project at `Firmware-EFM8/Treehopper.hwconf`,
a Simplicity Studio hwconf for `EFM8UB10F16G`) emits **Intel HEX**. The build
script names it `..\Keil 8051 v9.53 - Release\Treehopper.hex`
(`Firmware-EFM8/tools/treehopperBuild.cmd:1`), i.e. the Keil "v9.53 - Release"
output directory next to the project, file `Treehopper.hex`.

### The SiLabs conversion tool

**`hex2boot.exe`** — `Firmware-EFM8/tools/treehopperBuild.cmd:1`:

```
hex2boot.exe -o "..\..\NET\API\Treehopper.Firmware\treehopper.tfi" -m ub1 -b 0 "..\Keil 8051 v9.53 - Release\Treehopper.hex"
```

`hex2boot` is the converter from the SiLabs **AN945SW** ("EFM8 Factory
Bootloader") package: it turns an Intel HEX into a **bootload-record file**
(SiLabs extension `.efm8`; Treehopper just renames the output `.tfi`). The
record file is a pure-binary concatenation of the exact `$`-framed bootloader
command records (Setup, Erase, Write, Verify, RunApp) that the device consumes
unchanged — "the same binary record format is used whether saving to a file or
sending over the bootloader transport." So the host loader does **no** HEX
parsing; it streams the pre-baked records.

Treehopper output extension is **`.tfi`** (and the loader also accepts `.dhi`)
— `FirmwareUpdateDevice.cs:47`. The embedded default firmware resource is
`Treehopper.Firmware.treehopper.tfi` (`FirmwareUpdateDevice.cs:99`).

#### hex2boot invocation / flags

Official tool ships in AN945SW (SiLabs); a faithful open re-implementation is
github.com/cjacker/hex2boot (Python). CLI (both agree):

- `-o OUT` — boot-record output file (required). Treehopper uses `.tfi`.
- `-m {bb2,bb50,bb51,bb52,sb2,ub1}` — part/special-map family. Treehopper uses
  **`ub1`** (EFM8UB1).
- `-b {0,1}` — flash bank (default 0). Treehopper uses **0**.
- `-e {0,1,2}` — erase mode (0=none, 1=separate erase records, 2=erase-with-data).
- `-s ADDR` / `-t ADDR` — start / top address bounds.
- `-w` — remain in bootloader after flashing (omit -> emit RunApp at the end).
- `-i [ID ...]` — identity value(s) for the Identify record.
- `-l LOCK` — lock byte value.

For `ub1`, cjacker/hex2boot's special map is
`'ub1': [ [0x0000, 0x3DFF, 512], [0xF800, 0xFBBF, 64] ]` — i.e. the app/flash
region is `0x0000-0x3DFF` (512-byte pages) and a second region
`0xF800-0xFBBF` (64-byte) for the lock/config area. To reproduce Treehopper's
image exactly: `hex2boot -o treehopper.tfi -m ub1 -b 0 Treehopper.hex` (the
identical command in `treehopperBuild.cmd`).

### Record format produced

A `.tfi`/`.efm8` is a back-to-back sequence of `$`-framed records:

```
$ LEN CMD payload   $ LEN CMD payload   ... (repeat)
```

Typical hex2boot ordering: an Identify record (0x30), a Setup record (0x31),
then for each flash page an Erase (0x32) and one or more Write (0x33) records,
a Verify (0x34) record carrying the CRC16, optionally a Lock (0x35), and a
final RunApp (0x36) unless `-w` was given. The host loop (section 6) just reads
`$`, len, payload and replays each frame.

## 5. Parameter / bricking hazards

- **`-m ub1` is mandatory and must match the silicon.** The map sets page
  size and the address bounds; a wrong family writes pages at the wrong
  granularity. The bootloader's Identify check (cmd 0x30) is the guard — a
  mismatched `-i`/family makes the bootloader NAK rather than write garbage,
  but only if the records carry the right Identify; do not strip it.
- **Do not overwrite the bootloader region.** The factory bootloader lives at
  the top of flash (the AN945 reserved area; cjacker's ub1 map carries it as
  the `0xF800-0xFBBF` region). If a Write/Erase record targets the bootloader
  pages the device is bricked (no USB bootloader left, recoverable only via
  C2/JTAG debug pins). Keep the app linked below the reserved top region (the
  stock Keil project already does; do not change the link map / code size
  ceiling when patching).
- **Lock byte (`-l` / Lock cmd 0x35).** Writing a flash lock byte can disable
  further bootloader writes and/or debug access. Treehopper's command does
  **not** pass `-l`, so leave it off — adding it can permanently lock the part.
- **Signature byte.** Two distinct "signatures" — do not conflate:
  - the **RAM entry flag** `0xA5 @ 0x00` written by the app firmware
    (`treehopper.c:263`) to *request* the bootloader; volatile, not in the
    image.
  - any **flash signature/lock** the bootloader itself checks at boot to decide
    app-vs-bootloader on a cold (non-app-requested) reset. AN945 keeps this in
    the reserved region; hex2boot manages it. Do not hand-edit it.
- **CRC / Verify.** If a patched build changes bytes, hex2boot recomputes the
  Verify CRC16; never reuse a stale `.tfi`. A CRC mismatch at flash time
  surfaces as a non-`@` ACK and a half-written app — re-flash, do not power off
  mid-stream.
- **Interrupted flash is recoverable**, **interrupted bootloader-region write
  is not.** As long as we never target the reserved region, a failed flash
  leaves the device in the bootloader (it only RunApps on the final 0x36), so
  we can simply re-run the loader.

## 6. End-to-end pipeline (ordered, with reuse vs build-it markers)

1. **[reuse: Keil/Simplicity Studio]** Build patched firmware ->
   `Treehopper.hex` (Intel HEX). Keep the link map / code-size ceiling
   unchanged so nothing spills into the reserved bootloader region.
2. **[reuse: hex2boot]**
   `hex2boot.exe -o treehopper.tfi -m ub1 -b 0 Treehopper.hex` -> `.tfi`
   bootload-record file. (Get hex2boot from SiLabs AN945SW, or use
   cjacker/hex2boot.)
3. **[reuse: Periphery.Treehopper]** With the board enumerated as the app
   (`0x10C4:0x8A7E`), call `RebootIntoBootloaderAsync()` (sends `[0x0D]`).
   The handle dies; the device drops off USB.
4. **[reuse: OS/USB]** Device re-enumerates as the HID bootloader
   `0x10C4:0xEAC9`. Poll for it (HidSharp `DeviceList` upstream; our equivalent
   is enumerating HID devices by that VID/PID via Periphery.Hid's backend).
5. **[BUILD: ~130 LOC over Periphery.Hid]** Open the bootloader HID device and
   replay the `.tfi`:
   - read 2-byte header `[ '$'(0x24), len ]`; assert start byte;
   - read `len` more bytes -> full frame;
   - send the frame in 64-byte output-report chunks
     (`WriteReportAsync(new HidReport(0, chunk))`), report id 0;
   - read one input report (`ReadReportAsync`), assert first data byte is
     `0x40` (`@`);
   - repeat until EOF. The final RunApp (0x36) record resets into the new app.
   This is a direct port of `FirmwareUpdateDevice.Load`
   (`NET/API/Treehopper.Firmware/FirmwareUpdateDevice.cs:63-117`) onto
   Periphery.Hid — no HEX parsing, no CRC math on our side (hex2boot baked it
   into the Verify record).
6. **[reuse: OS/USB]** Device re-enumerates as the app `0x10C4:0x8A7E`;
   re-open with Periphery.Treehopper and confirm the patched behavior.

What we must build: only the record-replay-over-HID loop (step 5) plus the
bootloader-device discovery glue (step 4). Everything else is an existing tool
(hex2boot) or an existing API (RebootIntoBootloaderAsync, Periphery.Hid
read/write report).

## 7. Open questions / unverified

- **Exact AN945 command-byte table not read from the primary PDF.** The
  0x30-0x36 mapping, the Setup key bytes, and the `@ABC` status codes come from
  open re-implementations (cjacker/hex2boot, cjacker/efm8load,
  BarnabyShearer/efm8) and the upstream host loader's `$`/len/`@` handling,
  not from a clean read of the AN945 PDF (the PDF is image/stream-encoded; a
  page-image read needs `pdftoppm`, which is unavailable in this sandbox).
  Verify the exact bytes against AN945 §"Bootloader Protocol" /
  "Command Reference" before relying on them in code.
- **Setup record payload exact bytes** (flash keys + bank) for ub1 not
  confirmed byte-for-byte; we replay them from the `.tfi` so we never need to
  construct one, but confirm if we ever generate records ourselves.
- **Where AN945's cold-boot app-vs-bootloader decision lives in flash** (the
  reserved-region signature) is not pinned to an address from a primary source;
  hex2boot manages it, so leave it alone. Confirm the exact reserved top-of-
  flash bound for EFM8UB10F16G (16 KB part) against the datasheet before
  trusting cjacker's `0x3DFF`/`0xF800` map for THIS derivative — the 16 KB part
  may differ from the generic ub1 map.
- **`SizeIn = 4` input report** (upstream reads 4 status bytes): only the first
  is the ACK in observed use; the meaning of bytes 1-3 is unconfirmed.
  Probably padding to the report's wMaxPacketSize; verify against AN945.
- **hex2boot.exe binary is not in the repo** — only the generated `treehopper.tfi`
  is checked in. We must obtain hex2boot from SiLabs AN945SW (Simplicity Studio
  install or the AN945 software zip) or use the open Python port. Confirm the
  open port produces a byte-identical `.tfi` from the same `.hex` before
  trusting it for a real flash.
- **bcdDevice = 0x0112** (`descriptors.c:39`) is the app's device version; not
  load-bearing for flashing but useful to confirm a successful update.

## Source map

treehopper-sdk:
- `NET/API/Treehopper/TreehopperUsb.cs:397` — `RebootIntoBootloader()`
- `NET/API/Treehopper/DeviceCommands.cs:6` — opcode enum (EnterBootloader=13)
- `Firmware-EFM8/inc/treehopper.h:34` — firmware `GlobalCommands` enum
- `Firmware-EFM8/src/treehopper.c:261-266` — EnterBootloader handler (0xA5@0x00 + reset)
- `Firmware-EFM8/src/treehopper.c:255-259` — plain Reboot (reset, no signature)
- `Firmware-EFM8/inc/descriptors.h:28,32` — app VID/PID 0x10C4:0x8A7E
- `Firmware-EFM8/Treehopper.hwconf` — MCU EFM8UB10F16G-B-QFN28
- `Firmware-EFM8/tools/treehopperBuild.cmd:1` — hex2boot invocation
- `Firmware-EFM8/tools/treehopperLoad.cmd:1` — efm8load invocation
- `NET/API/Treehopper.Firmware/FirmwareConnectionService.cs:31-40` — bootloader VID/PID 0x10C4:0xEAC9, HID enumeration
- `NET/API/Treehopper.Firmware/FirmwareUpdateDevice.cs:17-18,63-117` — HID loader (frame parse, 64/4 report sizes, `$`/`@`)
- `Python/treehopper/api/treehopper_usb.py:344-355` — reboot_into_bootloader
- `Python/treehopper/api/settings.py:6-7`, `find_boards.py:15` — app VID/PID

periphery (where we build):
- `repos/periphery/src/Periphery.Hid/HidDevice.cs:62,65,83,97,124,142` — Read/WriteReportAsync, report length props
- `repos/periphery/src/Periphery.Hid/HidReport.cs:20` — HidReport(reportId, data)
- `repos/periphery/src/Periphery.Treehopper/.../Periphery.Treehopper.xml:938,1430` — RebootIntoBootloaderAsync / Command.EnterBootloader

SiLabs / external:
- AN945: EFM8 Factory Bootloader User's Guide — https://www.silabs.com/documents/public/application-notes/an945-efm8-factory-bootloader-user-guide.pdf
- hex2boot (open re-impl + flag reference) — https://github.com/cjacker/hex2boot
- efm8load (host loader, `$`/`@`, SIZE_OUT=64) — https://github.com/cjacker/efm8load
- efm8 (Python AN945 HID loader, command bytes 0x30-0x36, CRC16-XMODEM) — https://github.com/BarnabyShearer/efm8 / https://efm8.readthedocs.io/
