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
dotnet run --project scratch/Apa102Desync -- --iterations 200 --stall-ms 50
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

`--stall-ms 50` is a starting point, not a measurement. The spin is
`while(timeout++ < 65000 && USBD_EpIsBusy(...))`, so the budget is a function of the core
clock and of how long each `USBD_EpIsBusy` call takes — order tens of milliseconds. **A run
that reports no desync on unfixed firmware has not disproved anything until you have swept
`--stall-ms` upward** (try 100, 250, 500). The harness says so in its own output.

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

1. **EFM8UB10F16G page size at `0xF800`.** A datasheet confirmation, not a bench run. If it
   is 512 bytes rather than 64, the erase-overlap hypothesis returns as a primary cause and
   the 61-byte bound is wrong.
3. **Case flips over C2.** Read the serial page repeatedly at varying VDD and temperature.
   Stable over C2 while USB reads vary points at the serve path; drifting over C2 confirms
   the supply-monitor defect.
4. **Read `0xF800`–`0xFBBF` over C2 on the two damaged boards before reflashing**, the lock
   byte in particular. Do this before anything else touches them — reflashing destroys the
   evidence, and the unbounded write means the lock byte is a real possibility.

## Recording the result

Add the run to this file: firmware image, `--stall-ms` sweep, iterations, desyncs, and the
analyser trace if you took one. A "no desync" result on unfixed firmware is only worth
recording alongside the stall values you swept.
