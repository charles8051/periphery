# Building the Treehopper EFM8UB1 firmware

Source copied from `Firmware-EFM8` in the upstream Treehopper SDK
(https://github.com/treehopper-electronics/treehopper-sdk). This builds the EFM8UB10F16G
USB firmware to an Intel HEX using the **Keil C51 toolchain embedded in
Simplicity Studio 5** - no Simplicity Studio / Eclipse GUI required.

## Build

```pwsh
pwsh -File build.ps1
```

Output lands in `build/` (gitignored):

- `build/Treehopper.hex` - the Intel HEX image (feed to `hex2boot` to make the
  `.tfi` bootload-record file - see `docs/explorations/treehopper-firmware-update.md`).
- `build/Treehopper.omf` - the linked OMF (debug/symbols).
- `build/link.lst` - the LX51 map + size summary.

Override the toolchain / SDK locations if your install differs:

```pwsh
pwsh -File build.ps1 -ToolchainRoot "C:\...\keil_8051\9.60" -SdkRoot "C:\...\sdks\8051\v4.3.1"
```

## IMPORTANT: run from a native console, NOT Git Bash

`C51.exe` (and the other Keil tools) **heap-corrupt and crash** (`exit
0xC0000374`, empty output) when invoked through a Git Bash / MSYS pseudo-console
pipe. Symptoms: most files "fail" silently with no object and no error text,
while a trivial file or two survive. Run `build.ps1` from **pwsh / cmd** (a real
Windows console) and it builds cleanly. This is an environment quirk, not a
source or toolchain-flag problem.

## Build recipe (mirrors Simplicity Studio's managed build)

Extracted from `.cproject` (Release config):

- Part: **EFM8UB10F16G** (EFM8UB1, 16 KB flash)
- Memory model: **SMALL** (C51 default); code size **ROM(LARGE)**
- Compiler: **C51** `OPTIMIZE(9,SIZE)`, `DEBUG OBJECTEXTEND` (the actual flags are
  in `build.ps1`'s `$CFLAGS`; `SIZE` is deliberate - level 9 is the maximum and
  `SPEED` costs ~40 bytes we do not have)
- Startup: **AX51** on `src/SILABS_STARTUP.A51`
- Linker: **LX51** (extended), Intel HEX via **OHX51**
- Toolchain: Keil 8051 **9.60** (project authored against 9.53 - forward
  compatible); 8051 SDK **v4.3.1** (project references 4.0.9 - newer, layout
  compatible)

The 66 LX51 link warnings (`L57` uncalled-function, `L15` USB-ISR
multiple-call, `L16` uncalled-segment) are benign and identical to what the
stock Simplicity build emits.

### Known-good result

Measured from the committed `dist/Treehopper.hex` immediately **before** the
watchdog change: **14822** bytes, contiguous from `0x0000`, top (exclusive)
**`0x39E6`**.

The app ceiling is **`0x3A00`**, not `~0x3DFF`. The part has 16 KB of code flash
(`0x0000-0x3FFF`) and the **USB** factory bootloader is ~1.5 KB occupying the
top-most pages (AN945 sec. 5.2 and Figure 5.2): `16384 - 1536 = 14848 = 0x3A00`.
The `0x3DFF` figure would be right for a one-page (~512 B) UART/SMBus bootloader,
which is not what a UB1 carries. That leaves **26 bytes** free before this change.

> [!WARNING]
> **`hex2boot -m ub1` currently assumes the wrong ceiling.** The same `0x3DFF`
> error is mirrored in `src/Periphery.Bootloader.Efm8.Usb/Efm8BootOptions.cs`
> (`Efm8FlashMap.Ub1`), where it drives real behaviour rather than documentation -
> tracked as [#100](https://github.com/charles8051/periphery/issues/100).
>
> Today's image tops at `0x398E`, comfortably under **both** ceilings, so a `.tfi`
> produced now is safe. But the guard is not doing its job: **an image that grows
> past `0x3A00` would be silently accepted** and produce a bootload record that
> writes into the factory bootloader's own flash region, bricking the board.
>
> Until `#100` lands: **check the top address against `0x3A00` before running
> `hex2boot -m ub1`** - do not rely on the tool to catch it. The build output above
> prints the figure you need.
>
> Note the `treehopper-flash` CLI consumes the `.hex` directly and does **not** go
> through `hex2boot`, so it is unaffected.

Enabling the watchdog made the image **smaller**, as expected: it removes the
WDT-disable routine, its `LCALL`, and the now-orphaned `?C?ULCMP` helper (which
only existed because the erratum delay loop used a `uint32_t` counter), and adds
only the main-loop feed plus the init enable/lockout.

Measured with the same toolchain, same flags, base commit vs. this change:

| | `code=` | HEX bytes | Top | Free to `0x3A00` |
|---|---|---|---|---|
| baseline (`3687507`) | 14948 | 14820 | `0x39E4` | 28 |
| with the watchdog | 14862 | 14734 | `0x398E` | **114** |
| | **-86** | **-86** | | **+86** |

### The `#170` stream-framing change

Measured the same way, from the actual HEX records rather than from `code=`. The
baseline row matches the committed `dist/Treehopper.hex` byte for byte.

| | HEX bytes | Top | Free to `0x3A00` |
|---|---|---|---|
| baseline (`= dist/Treehopper.hex`, v2.76) | 14752 | `0x39A0` | 96 |
| with the framing fix | 14774 | `0x39B6` | 74 |
| plus header validity + short-packet clear (v2.77, shipped) | 14825 | `0x39E9` | **23** |

> [!WARNING]
> **23 bytes is the whole remaining budget.** The next change to this firmware will
> very likely need to buy its own room first, the way this one did. Measure from the
> HEX records before assuming otherwise, and do not trust `code=`.

The `bcdDevice` bump to `0x0115` in the same change costs nothing — it is a constant in an
already-present descriptor.

The fix on its own does not fit in 96 bytes. Two size-neutral simplifications in
the same commit paid for it, rather than any part of the fix being traded away:
the enumeration blink in `USBD_DeviceStateChangeCb` became a loop over the six
`LED_SetVal`/delay pairs it used to spell out (identical sequence and timing),
and `configureDevice(uint8_t)` — which ignored its argument and called
`Treehopper_Init()` — was inlined at its one call site.

> [!NOTE]
> The blink counter lives in **XDATA**, deliberately. LX51 cannot overlay that
> function's locals and DATA is full to the byte: a plain `uint8_t n` there fails
> the link with `*** ERROR L107: ADDRESS SPACE OVERFLOW / SPACE: DATA`. If you add
> a local to anything reachable from the USB ISR, expect that error rather than a
> code-size one.

## ⚠️ `code=` is NOT a safe proxy for flash usage — measure the HEX file directly

**Corrected 2026-08-07, then corrected again the same day.** This doc
previously claimed "the linker's `code=` figure runs 128 bytes above the HEX
byte count" as if that offset were a fixed constant safe to subtract. **It
isn't**, for two separate reasons — one structural and stable, one that was a
real bug and is now fixed.

**Structural, and stays true regardless of build environment: `code=` counts
`SERIALNUMBER_SERIAL` / `SERIALNUMBER_NAME`**, two absolute segments LX51
places at `0xF800`/`0xF840` (64 bytes each) — nowhere near the real
`0x0000-0x3FFF` flash range and never emitted into the actual Intel HEX
output at those addresses.

**The other 15 bytes were not "LX51 constant-pool packing" — they were
`build.ps1` passing C51 an absolute path, now fixed.** `assert.h`'s
`SLAB_ASSERT(expr)` expands to `slab_Assert(__FILE__, __LINE__)`, and C51
pools the `__FILE__` string once per module (`?CO?EFM8_USBD` for
`efm8_usbd.c`, which has 6 reachable asserts). `build.ps1` was passing
`$c.FullName` — an absolute path — to `C51.exe`, so that pooled string's
length, and therefore `code=`/the HEX byte count, depended on **where the
repository happened to be checked out**: two independent rebuilds of the
exact same source (commit `82ecaf6`, byte-identical toolchain version
`C51 V9.60.0.0`) disagreed on `code=` by 15 bytes purely from their checkout
paths' character counts, with every real function in every module diffing
byte-for-byte identical between the two builds. On the worst measured
checkout this left only **2 bytes** of headroom to `0x3A00` — not
"comfortable," and on a ceiling that `#100`'s still-wrong `hex2boot -m ub1`
does not itself enforce, an image checked out one directory deeper than
usual could silently overflow it and write a bootload record into the
factory bootloader's own flash region. Silent brick, caused by a directory
name. **Fixed**: `build.ps1`'s `[CC]` step now passes the already-computed
relative path (`$rel`, previously only used for the console log line)
instead of `$c.FullName` — worth **94 bytes** on this build, and it makes
flash size a property of the source, not of the checkout location.

**The only reliable check is to compute the real top address from the actual
`build/Treehopper.hex` records** — the ceiling guard exists precisely because
`hex2boot -m ub1` (and its C# mirror, `Efm8FlashMap.Ub1`, [#100](https://github.com/charles8051/periphery/issues/100))
won't catch an over-ceiling image on its own, and `code=` will always carry
the two absolute `SERIALNUMBER_*` segments regardless of checkout path. This
version validates the whole record (checksum, structure, a terminating EOF)
rather than trusting well-formed input — a truncated or corrupted HEX file
must fail loudly here, not silently produce a ceiling this check then
vouches for:

```python
def top_addr(path):
    """Highest (address + 1) covered by a data record. Validates every
    record's checksum, structure, and per-type data length; requires exactly
    one terminating EOF record with nothing after it; and raises on anything
    malformed or truncated - a corrupted or incomplete HEX file must never
    silently produce a ceiling this check then treats as trustworthy."""
    base = 0
    maxaddr = 0
    saw_eof = False
    # Every fixed-length record type's required data byte count (0x00/data is
    # the only variable-length one). A record whose OWN declared byte_count
    # matches its OWN checksum can still be semantically malformed for its
    # type - e.g. a type-04 record declaring 1 data byte instead of 2 checksums
    # validly, but reading its 4-hex-char "address" would run past the data
    # into the checksum byte, silently corrupting `base` for every record after it.
    required_length = {0x01: 0, 0x02: 2, 0x03: 4, 0x04: 2, 0x05: 4}
    with open(path) as f:
        for lineno, raw in enumerate(f, 1):
            line = raw.strip()
            if not line:
                continue
            if saw_eof:
                raise ValueError(f"line {lineno}: data after the EOF record")
            if not line.startswith(':'):
                raise ValueError(f"line {lineno}: does not start with ':'")

            byte_count = int(line[1:3], 16)
            addr = int(line[3:7], 16)
            rec_type = int(line[7:9], 16)
            data_end = 9 + byte_count * 2
            if len(line) != data_end + 2:
                raise ValueError(f"line {lineno}: length does not match byte_count")

            payload = bytes.fromhex(line[1:data_end])
            checksum = int(line[data_end:data_end + 2], 16)
            if (sum(payload) + checksum) & 0xFF:
                raise ValueError(f"line {lineno}: checksum mismatch")

            if rec_type != 0x00 and rec_type not in required_length:
                raise ValueError(f"line {lineno}: unknown record type 0x{rec_type:02x}")
            if rec_type in required_length and byte_count != required_length[rec_type]:
                raise ValueError(
                    f"line {lineno}: type 0x{rec_type:02x} requires "
                    f"{required_length[rec_type]} data bytes, got {byte_count}"
                )

            if rec_type == 0x00:      # data
                maxaddr = max(maxaddr, base + addr + byte_count)
            elif rec_type == 0x01:    # end-of-file
                saw_eof = True
            elif rec_type == 0x02:    # extended segment address (20-bit: value << 4)
                base = int(line[9:13], 16) << 4
            elif rec_type == 0x03:    # start segment address - CPU register init only, ignored for the ceiling
                pass
            elif rec_type == 0x04:    # extended linear address (32-bit: value << 16)
                base = int(line[9:13], 16) << 16
            elif rec_type == 0x05:    # start linear address - CPU register init only, ignored for the ceiling
                pass

    if not saw_eof:
        raise ValueError("no EOF (type 01) record - the file may be truncated")
    return maxaddr
```

Compare the result against `0x3A00`, not `code=`. The committed
`dist/Treehopper.hex` (pre-watchdog) top-checks at exactly `0x39e6` / 14822
against this method, matching the number this doc already documented from a
different route — that cross-check is what surfaced the `code=` gap in the
first place, not a mistake in the method.

## Current headroom: small. Read this before adding anything.

Each row below adds one more feature on top of the last (EP0 rescue, then the
SOF watchdog gate) — a later row's `code=` growing relative to an earlier one
is these features costing space, not a contradiction of the "enabling the
watchdog made the image *smaller*" finding two sections up: that comparison
isolated the base watchdog-enable commit in a two-way A/B against its own
pre-watchdog baseline, on one checkout, so the checkout-path bug canceled out
of its delta even before today's fix. The rows here are a running total
across several separate commits, each adding its own code.

| | `code=` | HEX bytes | Top (from the HEX file, not `code=`) | Free to `0x3A00` |
|---|---|---|---|---|
| after the EP0 rescue (`#227`) — *historical, measured before the path fix below* | 14925 | 14797 | `0x39CD` | 51 |
| current `main`, absolute-path build — *superseded, kept as the record of why the fix mattered* | 14959-14976 | 14831-14846 | `0x39EF`-`0x39FE` (two checkouts, same source, different path lengths) | 2-17 |
| **current `main`, after the `build.ps1` relative-path fix (see above)** | **14880** | **14752** | **`0x39A0`** | **96** |

The middle row is what an absolute-path build produced depending on where the
repo happened to be checked out — as little as **2 bytes** of headroom on a
ceiling `#100`'s `hex2boot -m ub1` does not itself enforce. It is kept here only
as the reason the fix mattered, not as a number to build against. The bottom
row is the one that matters going forward: with C51 now given a relative
path, `code=`/HEX size no longer depend on checkout location at all, so this
is a single, reproducible figure rather than a range — **still re-run the
HEX-file check above locally after any change**, since 96 bytes is not a
large margin. Anything further needs a size lever first. Known ones,
cheapest first:

- The enumeration LED blink in `src/callback.c` - six 60000-iteration spins,
  ~50 bytes. It is purely cosmetic, and it is also the ~60 ms blocking spin
  *inside the USB ISR* that `src/main.c` cites as the reason the watchdog
  interval cannot be tightened (see [#234](https://github.com/charles8051/periphery/issues/234)).
  Removing it buys flash and unblocks that separately.
- `src/parallel.c` (`ParallelConfig` / `ParallelTransaction`, ~664 B) has no
  production consumer. Both opcodes are last in the `treehopper.h` enum, so
  dropping them renumbers nothing.

Note the two spaces are independent and **DATA overflowed first here**: adding a
two-byte counter to the 128-byte directly-addressable `DATA` space failed to link
(`LX51 ERROR L107, SPACE: DATA`) while code space was still fine. `data=187.1` of
a 128-byte space means the overlaid `?C?LIB_DATA` region is full. New firmware
state belongs in `SI_SEG_IDATA` or `SI_SEG_XDATA`, not plain `static`.
