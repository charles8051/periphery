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

- **Test 1 — EFM8UB10F16G page size at `0xF800`.** A datasheet confirmation, not a bench run.
  If it is 512 bytes rather than 64, the erase-overlap hypothesis returns as a primary cause
  and the 61-byte bound is wrong.
- **Test 3 — case flips over C2.** Read the serial page repeatedly at varying VDD and
  temperature. Stable over C2 while USB reads vary points at the serve path; drifting over C2
  confirms the supply-monitor defect.
- **Test 4 — read `0xF800`–`0xFBBF` over C2 on the two damaged boards before reflashing**,
  the lock byte in particular. Do this before anything else touches them: reflashing destroys
  the evidence, and the unbounded write means a locked part is a real possibility.

## Result, 2026-09-04

**Reproduced, and the fix stops it.** Windows 11, two boards on one host, no analyser and no
LED strip. The positive control passed on every run listed here.

### The threshold

Board `IMNUZ6YW`, firmware **v2.75** (`REV_0113`), 20 iterations per row:

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

**Why.** `RunWithVerificationAsync` derives its application filter from the device's USB
vendor/product id when the caller supplies none, and `FlashAnythingService` supplies none.
Every Treehopper is `VID_10C4&PID_8A7E`, so the filter matches both boards. Correlation is
`DeviceCorrelationMode.FirstAppearance`, which "ignore[s] candidates already present when the
wait arms, accept[s] the first one to appear afterwards" — and the flashed board is already
back (logged as *existing*) when that wait arms, while the bystander re-enumerates in the
bus churn a moment later and is taken as *new*. `WithApplicationFilter`'s own comment
predicts this and then argues it is fine because "the orchestrator's own FirstAppearance
correlation is what actually pins the physical device." It does not, when a second board of
the same model re-enumerates in the same window.

**The fix is named in the code already.** `DeviceCorrelationMode.ByLocationPath`'s
documentation says to prefer it "when the family exposes a stable USB port" and calls out
"Treehopper/EFM8" by name — a board does not change port when it resets, so the correlation
becomes exact and parallel-safe. `FlashAnythingService` constructs a bare
`new BootloaderEntryOptions()`, which defaults to `FirstAppearance`.

**Until it is fixed: flash Treehopper boards one at a time**, with every other board of the
same model unplugged. Both boards here survived (checked: both enumerate `OK` in application
mode with the versions they should have), because the verify round's `RunApp` puts the
bystander back each time — but a board that is mid-operation does not care that it came back.

Tracked separately; not fixed on this branch.

## What is still open

Test 2 is closed. Tests 1, 3 and 4 in ADR-0086 D5 are not, and they still gate regenerating
`dist/`. Test 1 in particular — the page size at `0xF800` — can still change D2's 61-byte
arithmetic.

## Recording the next result

Add it here: firmware image and how it was verified, the `--stall-ms` sweep, iterations,
desyncs, and the analyser trace if you took one. A "no desync" result on unfixed firmware is
only worth recording alongside the stall values you swept.
