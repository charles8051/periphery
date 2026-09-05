# Reproducing the EP_PeripheralConfig desync on a bench board

Bench procedure for [#170](https://github.com/charles8051/periphery/issues/170) test 2, the
one ADR-0086 D5 blocks the firmware release on. The harness is
[`scratch/Apa102Desync`](../../scratch/Apa102Desync/Program.cs).

The claim under test: **on firmware without the #170 fix, a host stall during an APA102
flush makes the board execute a pixel byte as a command.** Everything else in the issue
follows from that one fact, and none of it has been seen happen — it was reconstructed from
two damaged boards after the fact.

## What you need

- A Treehopper board you are willing to reset repeatedly. **No LED strip is required.** The
  board clocks the frame out of its SPI pins whether or not anything is listening; the
  hazard is in what the *firmware* does with the bytes, not in what the strip shows.
- A USB analyser on **EP2 OUT** for the packet-level proof. The harness's own result does
  not need it — see "What the harness decides on its own".
- For the "before" run: a board running firmware **without** the fix, i.e. the committed
  `dist/Treehopper.hex`. For the "after" run: a board flashed from a `build/Treehopper.hex`
  produced by `src/Periphery.Treehopper/firmware/Treehopper-EFM8/build.ps1` on this branch.

## Run it

```bash
dotnet run --project scratch/Apa102Desync -- --list
```

```bash
dotnet run --project scratch/Apa102Desync -- --iterations 200 --stall-ms 250
```

Exit code 1 means the desync reproduced. 0 means it did not. 3 means the positive control
failed and nothing was run.

## What it sends

A 63-pixel frame, which encodes to 260 bytes, which `Apa102Strip.FlushAsync`'s 252-byte
chunking splits into a **259-byte SPITransaction command** — USB packets of 64/64/64/64/3.
That is the shape every animation tick has, all day, on a kiosk.

The harness writes packet 0, **waits `--stall-ms`**, then writes the rest. That stall is the
whole experiment: it runs out the firmware's fixed spin budget on the continuation read
armed at `&Treehopper_PeripheralConfig[64]`, which is what puts it on the
`USBD_AbortTransfer` path.

The spin is `while(timeout++ < 65000 && USBD_EpIsBusy(...))`, so the budget is a function of
the core clock and of how long each `USBD_EpIsBusy` call takes. **Measured on 2026-09-04 it
is 210–220 ms** — an order of magnitude longer than a back-of-the-envelope guess, and the
reason the harness's default `--stall-ms 50` finds nothing. Start at **250**.

**A run that reports no desync on unfixed firmware has not disproved anything until you have
swept `--stall-ms` upward.** The harness says so in its own output, and the sweep below is
why: everything at or under 200 ms is silent, and everything at or over 235 ms fires on
every iteration.

## Why the canary is safe

Every pixel is `Rgb(R:0x00, G:0x00, B:0x01)`, so the repeating wire group is
`FF 01 00 00`. A packet boundary at command offset `64k` is stream offset `64k - 7`, and
`(64k - 7 - 4) mod 4 == 1` for every `k` — **the Blue channel, every time**. That is the
phase the field evidence showed, and it is arithmetic, not luck: `64 mod 4 == 0`.

So the byte that lands at `Treehopper_PeripheralConfig[0]` is `0x01 ConfigureDevice`, which
calls `Treehopper_Init()` and touches no flash. Every other phase of that group is inert:
`0xFF` is not an opcode and neither is `0x00`.

`--canary` exists for aiming at another benign opcode. The harness **refuses** `0x0A`,
`0x0B`, `0x0C` and `0x0D` — serial write, name write, reboot, bootloader entry. Those are
the damage the issue is about; the serial is not recoverable from the host, and a `0x01`
that fires proves the identical mechanism.

## What the harness decides on its own

Pin 0 is set to a digital input, so the board's EP1 IN pin-report stream stops reporting it
as reserved. `Treehopper_Init()` puts every pin back, and the firmware reports a reserved pin
as `0xFF/0xFF` (`treehopper.c` `SendPinStatus`, the `default:` arm). **A report where all 20
pins read `0xFFFF` is a `ConfigureDevice` the host never sent.** That is the detector, and it
is host-observable — no analyser, no operator watching an LED.

**It runs a positive control first.** It sends one honest `ConfigureDevice` and requires the
detector to fire; if it does not, the harness exits 3 and runs nothing. Without that step a
clean run cannot be told apart from a broken detector, which is the failure mode where a
bench test passes having tested nothing.

At the end it re-enumerates and compares the name and serial against what it recorded at
startup, so the run also says whether it left the config page alone.

## Reading it against the analyser

On EP2 OUT you are looking for one thing: **a 64-byte packet whose first byte is `0x01` and
which the host did not send as a command.** The harness prints the repeating group and the
packet split so the trace lines up against what the host believed it sent. Expect, on
unfixed firmware:

1. Packet 0 of the command — starts `07 FF 00 03 30 01 FC` (the SPITransaction header).
2. A gap of `--stall-ms`.
3. Packets 1..3 and the 3-byte tail. One of these is discarded by the abort; the next one
   the firmware accepts lands at offset 0.

On fixed firmware the same trace appears and nothing executes, because the drain discards
until the 3-byte short packet.

## The other three bench tests

Not covered by this harness; ADR-0086 D5 lists them and they gate the release just as hard.

- **Test 1 — EFM8UB10F16G page size at `0xF800`. CLOSED 2026-09-05, from documentation.**
  **64 bytes.** The serial and name pages do not overlap, and D2's 61-byte bound stands. See
  "Test 1: the page size" below — including the way this check goes wrong.
- **Test 3 - case flips over C2. CLOSED 2026-09-05, and the premise dissolves.** The case
  difference is a host-side presentation artefact, not marginal cells. See "Test 3: the case
  flips" below. D4 stays in on the reference manual's authority, but it no longer explains a
  symptom.
- **Test 4 — read `0xF800`–`0xFBBF` over C2 on the two damaged boards before reflashing**,
  the lock byte in particular. Do this before anything else touches them: reflashing destroys
  the evidence, and the unbounded write means a locked part is a real possibility.

## Result, 2026-09-04

**Reproduced, and the fix stops it.** Windows 11, two boards on one host, no analyser and no
LED strip. The positive control passed on every run listed here.

### The threshold

Board `IMNUZ6YW`, firmware **v2.75** (`REV_0113`). The sample size differs by row - the
sweep was narrowed as the knee showed itself - so it is given per row rather than stated
once:

| `--stall-ms` | desyncs |
|---|---|
| 50 | 0 / 25 |
| 150 | 0 / 20 |
| 200 | 0 / 20 |
| 220 | **17 / 20** |
| 235 | 20 / 20 |
| 250 | 50 / 50 |
| 300 | 20 / 20 |
| 600 | 20 / 20 |

The knee is at 220 ms and it is sharp: nothing under 200, everything over 235. That brackets
the firmware's spin budget at **210–220 ms**.

### The controlled comparison

`IMNUZ6YW` turned out **not** to match `dist/Treehopper.hex`, so a two-board comparison
against it confounds the fix with whatever else differs. (It is on v2.75 — the release
*before* the watchdog change — which is why it does not match a 2.76 `dist/`. Working that
out took rebooting it into its bootloader for a flash verify, because at the time every
board on the bus reported the same version. See "identify the board first" below.)

The decisive run is therefore one board, one variable: board `CDYHINBH`, flashed back and
forth, with the image **verified by `treehopper-flash verify` in both directions**.

| `CDYHINBH` running | verified | `--stall-ms` | desyncs |
|---|---|---|---|
| `dist/Treehopper.hex` (unfixed) | MATCH | 250 | **30 / 30** |
| `build/Treehopper.hex` (fixed) | MATCH | 250 | 0 / 30 |
| `build/Treehopper.hex` (fixed) | MATCH | 600 | 0 / 30 |
| `build/Treehopper.hex` (fixed) | MATCH | 600 | 0 / 100 |

Same board, same host, same traffic, same stall. The only thing that changed is the
firmware image, and the desync goes from every single iteration to none.

Descriptors were unchanged after every run, on both boards. That is the canary doing its
job — `0x01 ConfigureDevice` fired 30 times per run and touched no flash.

### Identify the board first — it is one command

`bcdDevice` is now bumped **with the source change**, not with the release, so an unreleased
image on a bench board is still self-identifying. Read it off the bus without opening,
rebooting or flashing anything:

```pwsh
Get-PnpDevice -PresentOnly | Where-Object { $_.InstanceId -match 'VID_10C4&PID_8A7E' } | ForEach-Object { $rev = (Get-PnpDeviceProperty -InstanceId $_.InstanceId -KeyName 'DEVPKEY_Device_HardwareIds').Data | Where-Object { $_ -match 'REV_' }; "$($_.InstanceId.Split('\')[-1])  $rev" }
```

```
IMNUZ6YW  USB\VID_10C4&PID_8A7E&REV_0113     <- v2.75, pre-watchdog release
CDYHINBH  USB\VID_10C4&PID_8A7E&REV_0115     <- v2.77, the #170 fix
```

`REV_0114` is v2.76, i.e. `dist/Treehopper.hex`: the watchdog work, and **still vulnerable to
#170**. Anything at or below `0114` desyncs.

### Three things worth knowing for the next run

**The harness's descriptor check had a false-alarm bug, now fixed.** It matched the
re-enumerated board by serial *or* name. A stock board is called `Treehopper`, so with two
of them connected the name fallback matched the *other* board and reported
`serial 'CDYHINBH' -> 'IMNUZ6YW'` — damage that had not happened. It now matches on serial,
falls back to the name only when exactly one connected board carries it, and otherwise says
it cannot tell rather than guessing.

**`treehopper-flash flash` verified the WRONG BOARD, and rebooted it.** Reported `FAILED` on
every application-mode flash in this session while a `verify` immediately afterwards returned
MATCH against the image just written. That looked like a false negative on the confirmation
step. It is not — the confirmation is telling the truth, about a board nobody asked it to
touch. See "The wrong-board flash defect" below. Not a #170 problem, and worse than it first
looked.

**The version word was nearly missed again.** The whole reason this session had to reboot a
board into its bootloader to find out what it was running is that the #170 fix originally
landed without bumping `bcdDevice` — the same omission the 275 -> 276 comment in
`descriptors.c` was written to warn about. Bump it in the same commit as any firmware
behaviour change, whether or not `dist/` is being regenerated. An unreleased image on a
bench board is precisely the case that needs it.

## The wrong-board flash defect

Found while explaining the `FAILED` above, with `treehopper-flash flash --verbose`. **This is
a defect in shipped code, not in the bench setup, and it is not a #170 problem.**

`FlashAnythingService.RebootAndFlashApplicationAsync` flashes, then confirms the write in a
separate, later bootloader session (`BootloaderEntryOrchestrator.RunWithVerificationAsync`).
Between those two steps it waits for the application to come back and correlates *which*
device came back. With two Treehopper boards connected it correlated the wrong one:

```
19:57:52.025  Flash USB\...\CDYHINBH: identified EFM8; transfer size 64 bytes
19:57:53.138  detected EXISTING Application target ...\cDYhINBh          <- the board we flashed
19:57:53.403  detected NEW Application target ...\imnuz6yw               <- correlated as "it came back"
19:57:54.582  target ...\imnuz6yw removed (no longer present)            <- verify round REBOOTS IT
              EFM8 upload #8: record 1/2 (command 0x34) -> reply OtherError (0x43)
              EFM8 upload #9: record 0/1 (command 0x36) -> Acknowledge   <- RunApp, puts it back
```

Command `0x34` is the bootloader's Verify; `0x43` is a content mismatch. Correct answer,
wrong board — `IMNUZ6YW` holds v2.75, not the image just written to `CDYHINBH`. The whole
cycle then repeats for all three attempts, so **one flash reboots a bystander board into its
bootloader three times.**

Two consequences, and the second is much the worse:

1. The `FAILED` is not a false negative. The verify genuinely failed, on a board that was
   never going to match.
2. **Flashing one board takes an uninvolved board of the same model off the bus**, repeatedly.
   On a kiosk hub that is every other Treehopper on it.

**Why.** Two things combine.

`RunWithVerificationAsync` derives its application filter from the device's USB vendor/product
id when the caller supplies none, and `FlashAnythingService` supplies none. Every Treehopper is
`VID_10C4&PID_8A7E`, so the filter matches both boards.

And the application-return wait adopts an arbitrary already-present match **immediately**. It is
armed with `debouncePreExisting: false` (`BootloaderEntryOrchestrator.cs:321`, "liveness check,
not a re-enumeration correlation"), so in `DeviceWaitState.Arm()` the `ignored` set is empty,
`Correlates` for `FirstAppearance` reduces to `!_ignored.Contains(id)` - unconditionally true -
and `Arm()` returns the first entry of an `ImmutableDictionary` of present boards.

> **This replaces an earlier, wrong paragraph.** It said the two boards raced to re-enumerate
> inside the mode-switch window. There is no race. The debounce that `FirstAppearance`'s own
> documentation describes is switched off on this particular wait, so the wrong board is adopted
> synchronously at arm, before anything re-enumerates - which is exactly why the bystander
> enters its bootloader 20 ms after the write while the flashed board does not return for
> another 350 ms. It reproduces every time; "a race" reads as flaky and would have sent the fix
> hunting a timing window that is not there.

`WithApplicationFilter`'s own comment anticipates the ambiguity and argues it is fine because
"the orchestrator's own FirstAppearance correlation is what actually pins the physical device."
The correlation pins nothing here, because the debounce it relies on is disabled.

**The retry can also write to the adopted board.** On a mismatch with a confirmed return,
`RunWithVerificationAsync` sets `device = confirmedForRetry` and the next iteration *flashes*
it, from the same arbitrary-first-present wait. It did not happen here (`IMNUZ6YW` still read
v2.75 after three retries that each wrote 15380 bytes), but the path allows it - a write to a
bystander, not merely a reboot.

**The fix.** `DeviceCorrelationMode.ByLocationPath` names Treehopper/EFM8 in its own
documentation, but a port identifies a slot rather than its occupant, and switching the mode
would leave the flash round's own wait - the one feeding the retry above - still undebounced.
The stronger fix is one level up in `WithApplicationFilter`: derive an identity, the
port-AND-serial conjunction `IdentityFilterFor` already applies on the recovery path in the same
file, keeping VID/PID only as the fallback for a device exposing no identity.

**Until it is fixed: flash Treehopper boards one at a time**, with every other board of the same
model unplugged. Both boards here survived - both enumerate `OK` in application mode on the
versions they should have - because the verify round's `RunApp` puts the bystander back each
time. A board that is mid-operation does not care that it came back, and the retry hazard above
is a write.

Since the `bcdDevice` bump, `REV_0113` / `REV_0114` / `REV_0115` on the bus read as v2.75 /
v2.76 / v2.77, so which image a board is running can be checked without a verify round-trip -
useful precisely because the round-trip is the thing that misbehaves.

Tracked as [#173](https://github.com/charles8051/periphery/issues/173), which carries the full
`--verbose` trace, the corrected mechanism, and a second trace showing every ordering reversed
once the right board is checked. Fixed by
[#175](https://github.com/charles8051/periphery/pull/175), which pins the post-flash
application wait to an identity rather than a model - a separate PR against `main`, because it
is in `Periphery.Bootloader` and touches nothing #170 owns.

## Test 1: the page size at `0xF800` — 64 bytes, closed

**Answered from Silicon Labs' own sources, no hardware needed.** The EFM8UB10F16G has **two
flash regions with different page sizes**, and only one of them is the scratchpad the config
records live in.

`examples/EFM8UB1_SLSTK2000A/Bootloader/USB/inc/efm8_device.h` in the 8051 SDK — the AN945
factory USB bootloader, built for `EFM8UB10F16G_QFN28`, the Treehopper part:

```c
#define EFM8UB1_DEVICE   EFM8UB10F16G_QFN28
...
#define BL_FLASH0_LIMIT  DEVICE_FLASH_SIZE   // 0x4000
#define BL_FLASH0_PSIZE  512                 // code flash 0x0000-0x3FFF
#define BL_FLASH1_START  0xF800
#define BL_FLASH1_LIMIT  0xFC00
#define BL_FLASH1_PSIZE  64                  // scratchpad 0xF800-0xFBFF  <-- ours
```

| Region | Range | Page size |
|---|---|---|
| Flash0, code | `0x0000`-`0x3FFF` | 512 |
| Flash1, scratchpad | `0xF800`-`0xFBFF` | **64** |

`SER_ADDR 0xF800` and `NAME_ADDR 0xF840` are both in Flash1, `0x40` apart — **exactly one
page**. They are separate erase pages, `flash_erasePage` on one does not touch the other, and
the erase-overlap hypothesis stays ruled out. The 61-byte bound in ADR-0086 D2 stands, and so
does hex2boot's `[0xF800, 0xFBBF, 64]`, which matches `BL_FLASH1_PSIZE` exactly rather than
being the unrelated record granularity it could have been mistaken for.

### How this check goes wrong

Grepping the SDK for a page size finds **512** first, in
`EFM8UB1_FlashPrimitives.h` (`#define FLASH_PAGESIZE 512`) and in every `EFM8*_FlashPrimitives`
example. Those describe **Flash0 only** — the code region — and say nothing about the
scratchpad. Take that number and the arithmetic inverts: `0xF800` is 512-aligned, so a
512-byte page would span `0xF800`-`0xF9FF` and swallow both records, making every name write
erase the serial and every serial write erase the name. That is a dramatic, plausible, and
completely wrong conclusion, and it was two minutes from being written down here.

**The device header for the specific part is the source that settles it**, because it is the
only one that distinguishes the two regions.

### A correction this turned up

The issue text, carried into ADR-0086 D2 and the changelog, said an unbounded write "runs past
`0xF87F` into the unerased reserved region, which holds bootloader data and the lock byte — a
zero written there can permanently lock the part." **The lock byte is out of reach**, and the
same header shows why: `BL_LOCK_ADDRESS = BL_FLASH1_LIMIT - 1 = 0xFBFF`, while `len` is a
single byte, so a name write reaches at most `0xF840 + 3 + 254 = 0xF941` and a serial write at
most `0xF901`. Neither can get near `0xFBFF`.

What an unbounded write *can* do is real and is what the bound is for: a **serial** write with
`len > 61` runs from `0xF803` through `0xF901`, straight across the unerased name page at
`0xF840`-`0xF87F`, AND-corrupting it — precisely the "spills into the name page without erasing
it" case. Beyond that it corrupts scratchpad pages the firmware does not own.

## Test 3: the case flips are a host artefact - closed

**Equipment:** Silicon Labs J-Link (DBG1015A, S/N 440305956) on C2, target `CDYHINBH`.
`VTref = 3.301 V`. Reads via:

```bash
JLink.exe -usb 440305956 -device EFM8UB10F16G -if C2 -speed 100 -autoconnect 1 \
          -CommanderScript <script with: mem 0xF800 0xC0>
```

C2 needs `-if C2` on the **command line**; `si 3` selects FINE, and a `connect` inside a
CommanderScript stops at an interactive interface prompt the script cannot answer.

### Ground truth

```
0000F800 = 01 12 03 63 44 59 68 49  4E 42 68 FF FF FF FF FF  ...cDYhINBh.....
0000F840 = 01 16 03 54 72 65 65 68  6F 70 70 65 72 FF FF FF  ...Treehopper...
```

Both records decode exactly: marker `0x01`, length `(len+1)*2`, descriptor `0x03`, payload.
`0x12` -> 8 chars `cDYhINBh`; `0x16` -> 10 chars `Treehopper`. Everything above `0xF87F` is
erased. **Five consecutive reads were byte-identical** at nominal VDD.

### The stored serial is genuinely mixed case

`cDYhINBh`, not `CDYHINBH`. That is not corruption - `getRandomPrintableCharacter` in
`serialNumber.c` deliberately draws from `0-9`, `A-Z` **and** `a-z`, so a mixed-case serial is
what the firmware is supposed to produce.

### The same board reports three different cases, simultaneously

One `treehopper-flash --verbose` log, one host, one session, both boards:

```
     18  cDYhINBh     <- matches the C2 bytes exactly
     10  CDYHINBH
     16  imnuz6yw
      8  IMNUZ6YW
```

Windows normalises device instance ids differently depending on the API: the notification path
passes the device's own string through, the SetupAPI/PnP enumeration path uppercases it.
`Get-PnpDevice` reports `CDYHINBH` at the same moment C2 says the flash holds `cDYhINBh`.

### Which accounts for every example in the issue

| reported as "before" | reported as "after" | uppercase of "after"? |
|---|---|---|
| `VOQXRNTN` | `vOQxrntn` | yes |
| `0PM1YKJO` | `0PM1YKjO` | yes |
| `KISSUEDM` | `kIssUEDM` | yes |

Every character that differs is a lowercase letter in the mixed-case reading; every digit is
untouched. That is what case normalisation does, and it is not what a drifting flash cell does
- a cell has no notion of "letter".

**And "every flip sets bit 5" is tautological, not evidence.** Bit 5 (`0x20`) *is* the ASCII
case bit: `V`(0x56) -> `v`(0x76) sets it by definition. Any uppercase/lowercase pair differs in
exactly that bit, whatever produced the difference. The issue read it as drift toward the
erased `0xFF`; it is simply what upper- and lower-case letters are.

### What this does and does not settle

**Settles:** the reported symptom needs no flash explanation, and the strings are stable over
C2 at nominal VDD.

**Does not settle:** nothing here varied VDD or temperature, so a marginal-cell effect at the
extremes is unevidenced rather than excluded. **D4 stays in regardless** - the reference manual
requires the supply monitor enabled and selected before any flash write, and that is reason
enough on its own. What changes is that D4 must stop being presented as the explanation for the
case flips, because there is no longer anything for it to explain.

## Test 1 again, this time on silicon

While the probe was attached: two `treehopper-flash rename` cycles on `CDYHINBH`, each of which
erases and rewrites the name page at `0xF840`, with a C2 read of `0xF800`-`0xF84F` after each.

```
after rename to "DesyncBench":
0000F800 = 01 12 03 63 44 59 68 49  4E 42 68 FF ...   <- serial UNCHANGED
0000F840 = 01 18 03 44 65 73 79 6E  63 42 65 6E 63 68 ...DesyncBench

after rename back to "Treehopper":
0000F800 = 01 12 03 63 44 59 68 49  4E 42 68 FF ...   <- serial UNCHANGED
0000F840 = 01 16 03 54 72 65 65 68  6F 70 70 65 72 FF ...Treehopper
```

The serial page is byte-identical across both erase-and-write cycles 64 bytes above it. **The
scratchpad page really is 64 bytes on this silicon**, not just in the device header, and
`flash_erasePage(NAME_ADDR)` does not reach `SER_ADDR`.

Two further things fall out of the same reads:

- **D3 (marker written last) is confirmed on hardware.** Byte `[0]` is `0x01` and each record is
  complete and correctly framed after the write.
- **The C2 read is live, not cached.** It changed exactly where a rename should change it and
  nowhere else, which is the positive control for every other read in this section.

## Test 4, mostly answered from artifacts already off the station

**No deployment, no C2, no site visit.** Station `cindy` / `SV3-01-ENMOVS6` (meshware
`4QV9B54J`) uploads `shredvault.diagnostic_snapshot` artifacts - gzipped NDJSON - and two of
them bracket the incident. `meshware --env production artifacts get` is read-only.

The incident is 2026-09-01 22:02 local = **2026-09-02 03:02 UTC**.

### The garbage name, from the station's own logs

```
03:02:21.928  USB\VID_10C4&PID_8A7E&5D32C7&0&1   name='ÿ	ÿ	'
03:02:21.932  USB\VID_10C4&PID_8A7E&5D32C7&0&2   name='ÿ	ÿ	'
```

`06 FF 0B 09 06 FF 0B 09 06` - the nine bytes the issue derived, byte-identical, on both
boards. Confirmed rather than reconstructed.

### The bootloader entries, confirmed

```
03:02:25.072  VOQXRNTN disappears            -> PID_EAC9&5d32c7&0&3 at 03:02:25.303
03:02:35.856  6&5D32C7&0&1 disappears        -> PID_EAC9&5d32c7&0&1 at 03:02:36.076
03:02:35.916  6&5D32C7&0&2 disappears        -> PID_EAC9&5d32c7&0&2 at 03:02:36.190
03:02:37.5-6  all three bootloaders vanish; the boards come back
```

All three boards, two waves 11 s apart, exactly as the issue reports. Nothing in the log
requests a bootloader entry.

### What the artifacts change

**The identity loss PREDATES the bootloader event.** At `03:02:21` - four seconds before the
first board enters its bootloader, during the app's own startup device snapshot - both damaged
boards are *already* carrying the garbage name and are *already* enumerating by port path
(`6&5D32C7&0&1`) rather than by serial. So these are two separate events and the descriptor
damage came first. The issue treats the 22:02 bootloader arrivals as the visible edge of the
same incident; they are the second act, and the snapshot does not reach back to the first.

**The damaged boards serve no serial at all - not a garbage one.** A port-path instance id is
what Windows falls back to when a device has no `iSerialNumber`. That matters, because a
*garbage* serial is what the issue's "second such event with a Blue channel of `0x0A`"
hypothesis predicts, and it is not what is there.

**And that is field evidence for D3.** `SerialNumber_Init` regenerates whenever
`serialNumber_serial[0] == 0xFF`, so a blank page would have self-healed into a fresh random
serial on the very next boot. It has not, across four days and many reboots. So byte `[0]` is
present while the record is unserveable - a record that looks valid forever to the firmware and
invalid to the USB stack, which is exactly the marker-written-first failure D3 fixes.

**The rename workaround took, and did not restore the serial.** The 2026-09-05 snapshot shows
`name='DepositChamber'` and `name='Vending'` on the same two port paths - still no serial.

### The case flip, caught in the act

```
03:02:25.072  USB\VID_10C4&PID_8A7E\VOQXRNTN     <- before the reboot
03:02:37.793  USB\VID_10C4&PID_8A7EOQxrntn     <- 12 s later, same board
```

The issue's own first example, with timestamps, in the instance id itself, across one
re-enumeration. Twelve seconds is not cell drift, and the later reading is the mixed-case form
that C2 shows is what is actually stored. Independent confirmation of test 3 on the affected
station.

### What is genuinely left

One reading: **the raw `iSerialNumber` descriptor bytes from the two damaged boards** - what
`bLength` comes back, and whether the request fails outright. That is the difference between
"the length byte is garbage" and "the payload is garbage", and it is the last thing the logs
cannot say. `scratch/TreehopperIdentityProbe` is built and validated against C2 for exactly
this, and it needs to run on the station.

It sharpens D3's field evidence. It does not change any decision already made.

## What is still open

**Nothing that gates the release.** All four ADR-0086 D5 tests are closed and `dist/` is
regenerated at v2.77 (14827 HEX bytes, top `0x39EB`; `.tfi` 15433 bytes, 120 records). The
bench board that reproduced the desync verifies MATCH against the shipped `dist/Treehopper.hex`.

One reading was deliberately not taken: the raw `iSerialNumber` descriptor bytes from the two
damaged boards. It needs code running on a production station, and the control plane has no
run-a-binary path - deployment there means publishing an OTA workload to a live shredder. It
would sharpen D3's field evidence and change no decision, so it was skipped rather than
scheduled. `scratch/TreehopperIdentityProbe` is built and validated against C2 if it is ever
wanted; run it against both boards and record what `bLength` comes back for string index 3.

## Recording the next result

Add it here: firmware image and how it was verified, the `--stall-ms` sweep, iterations,
desyncs, and the analyser trace if you took one. A "no desync" result on unfixed firmware is
only worth recording alongside the stall values you swept.
