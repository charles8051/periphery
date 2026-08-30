# ADR: Firmware image formats (shared parsing, brick-sniffing, conversion)

<!--
Append-only / superseded, never rewritten. Decisions are numbered Decision 1..N.
The "what" (living requirements, current API) is in the sibling spec.md; this file records
the "how / why" so a future contributor sees the tradeoffs, not just the result. If a
decision here grows to be cited by a second feature, graduate it into a numbered repo-level
ADR under docs/adr/.
-->

Context: the firmware-flashing platform ([ADR-0061](../../../adr/0061-firmware-flashing-platform.md))
spans many target families, each handed firmware in some on-disk format. DEC-003 already names
`Periphery.Firmware` as the shared image model (HEX / bin / DfuSe) and DEC-007 puts `.zip` / GBL
parsing there too. Wiring `.hex` (phase 0) surfaced the concrete problem: EFM8's
[`Efm8FirmwareImage`](../../../../src/Periphery.Bootloader.Efm8.Usb/Efm8FirmwareImage.cs) and the new
`FirmwareImage.Load` now implement the same "declare format from extension, sniff the content,
refuse a mismatch" brick-guard **twice**, and every roadmap family (STM32 `.dfu`, ESP32, EFR32
`.gbl`, Nordic `.zip`) would add another. This ADR decides how much of format handling is shared,
and how. Periphery has no external consumers, so the bias is "right design" over "compatible."
See [`spec.md`](spec.md).

---

## Decision 1 - Firmware formats are organized by two kinds: memory images vs packaged blobs

**Decision.** Treat every firmware format as exactly one of: a **memory image** (decomposes to
`address -> bytes`: Intel HEX, raw binary, DfuSe, S-record, ELF) or a **packaged blob**
(a protocol-native container consumed as-is or merely unwrapped: EFM8 boot-record stream, Gecko
GBL, Nordic `.zip`, ESP image). The kind is a first-class attribute of each format.

**Why.** This split is the single fact that determines what can be shared. Memory images all
collapse to one value (`FirmwareImage` segments) and so share parsing, conversion-in, and the
write path. Packaged blobs do not reduce to address->bytes - they carry their own
structure/signature/CRC and are the bootloader's native input - so forcing them into a common
image model would be wrong. Naming the kinds up front stops the recurring temptation to "just
add another `From*`" for a `.gbl` or `.zip` that is not a memory image at all.

---

## Decision 2 - A universal format-detection / brick-sniff registry is the one layer shared across all formats

**Decision.** `Periphery.Firmware` owns a single registry: each format registers
`(extensions, content-sniff predicate, kind)`, and one `Detect(content, fileName)` reconciles the
declared extension against the sniffed content, throwing on mismatch. **Both** existing
brick-guards (`FirmwareImage.Load` and EFM8's `Efm8FirmwareImage`) become consumers of it; neither
keeps a private extension/sniff table. Every future family registers its formats here.

**Why.** Detection is about *identifying* a file, which is independent of what you do next - so it
works for both kinds (a `.gbl` or `.zip` has magic bytes just like a `.hex` does). It is therefore
the one layer that genuinely serves all families, and it is exactly the duplication phase 0
created. Centralizing it means the brick-guard - the safety-critical "refuse a `.hex` mislabelled
as `.bin`, or a `.gbl` handed to an STM32 flasher" - has one implementation, one test surface, and
one place to add a format. Leaving it per-family guarantees the guards drift (different magic
checks, different exceptions, different gaps) on the most dangerous code in the platform.

---

## Decision 3 - `FirmwareImage` (addressed segments) is the shared target for memory images only

**Decision.** The shared parsed representation - `FirmwareImage` (contiguous `FirmwareSegment`s) +
its `From*` parsers - covers **Kind 1 only**. Packaged blobs are **not** parsed into it; they get a
thin per-family "validate + unwrap" seam (e.g. unzip a Nordic package to its `.bin` + `.dat`,
verify a GBL's tags/CRC) and register with the Decision-2 registry for detection.

**Why.** A byte-writing bootloader (STM32 DFU/USART, esptool raw) wants addressed bytes and does not
care whether they came from HEX, S-record, ELF, DfuSe, or a raw `.bin` at a base - so one model and
one `From*` per format is a real, clean share. But a Nordic `.zip` is a signed multi-image package
and a GBL is a tagged, possibly-encrypted container; flattening them to segments would discard the
exact metadata (init packet, signature, CRC) the target's protocol requires. The honest boundary is:
share the segment model where the bytes really are just addressed bytes; keep the containers with
the family that speaks their protocol.

---

## Decision 4 - A flasher declares which formats it accepts; cross-format rejection is shared

**Decision.** A bootloader provider/programmer advertises its accepted formats (or accepted kind),
and feeding it an unaccepted format is rejected uniformly via `Detect` + the acceptance check,
surfaced through the existing `FirmwareLoadFailed` / `FirmwareError` path - not by per-family ad-hoc
code.

**Why.** "Which formats can this target take" is a property of the target, and the answer differs
(STM32 DFU: HEX / bin / DfuSe; EFM8: HEX / boot-records; Nordic: `.zip` only). Encoding it as data
on the provider lets one shared check produce a clear, consistent "this flasher does not accept
`.gbl`" message before any device IO, instead of each family re-deriving the rejection. It also lets
FlashAnything filter file pickers per selected target from one source of truth.

---

## Decision 5 - Conversion is shared *into* `FirmwareImage`; conversion *out* stays flasher-private; blobs are unwrap-only

**Decision.** Parsing a Kind-1 format into `FirmwareImage` is shared (Decision 3). Turning a
`FirmwareImage` into wire bytes (DFU download blocks, AN3155 write commands, esptool writes) stays
**private to each flasher**. Packaged blobs have **no** host-side build step - the host only
validates/unwraps them. EFM8's host-side `FirmwareImage`-equivalent -> boot-record-stream
serialization stays an EFM8-family concern; it only *reads* the shared `IntelHexImage`.

**Why.** The "out" direction *is* the protocol - it is the part that is genuinely different per
family and already lives correctly in each client (DEC-003: plans/steps stay client-private). There
is no host-side construction of a signed `.zip` or a GBL (you are handed those by the vendor build),
so there is nothing to share there but generic primitives (ZIP read, CRC-32). Keeping EFM8's
record generation in the family - while it consumes the shared parser - is the same boundary applied
consistently: shared *identify + parse-to-image*, family-specific *speak-the-protocol*.

---

## Decision 6 - ELF is parsed from its `PT_LOAD` program headers (LMA + file bytes), like a HEX

**Decision (2026-06-18).** ELF (`.elf` / `.axf` / `.out`) is a Kind-1 memory image (Decision 1),
parsed by a pure `ElfImage` reader into `FirmwareImage` segments via `FirmwareImage.FromElf`. The
parser:

- emits **only `PT_LOAD` program-header segments**; non-load program headers and all section-header
  metadata (symbols, debug info, `.comment`, …) are ignored - they never reach flash;
- uses **`p_paddr` (the load/physical address, the LMA)** as the segment address, *not* `p_vaddr` -
  for initialized `.data` the two differ (bytes stored in flash at the LMA, copied to RAM at runtime),
  and the flashed image must use where the bytes physically live;
- takes **only `p_filesz` bytes**; a `p_memsz > p_filesz` tail is zero-initialized `.bss` that is not
  stored on disk, and a `p_filesz == 0` segment (pure `.bss`) is skipped;
- supports **both classes (32/64-bit) and both endiannesses**, reading each header's fields at the
  class-specific offsets; a 64-bit `p_paddr` outside the 32-bit space Periphery flashes is **refused**,
  not truncated (`FirmwareSegment.Address` is `uint`);
- imposes **no `e_type` allow-list**: an ELF with no loadable program data (a relocatable `.o`, a
  debug-only file) naturally yields zero segments and is rejected with a clear message.

It is wired into `FirmwareImage.Load` for `.elf`/`.axf`/`.out` behind a `\x7FELF` magic brick-guard
(extension says ELF, content must be ELF), and the `.bin` brick-guard now also refuses ELF magic
(an ELF renamed `.bin` would flash its headers verbatim and brick the device). Malformed-but-ELF
content throws a dedicated `ElfFormatException` (mirroring `IntelHexFormatException`); a wrong-format
file throws `FirmwareFormatException` (the shared brick-guard exception).

**Why.** This is exactly what `objcopy -O binary`, OpenOCD (`flash write_image`), and pyOCD do, and
it is the only interpretation that flashes the right bytes to the right addresses. `p_paddr` over
`p_vaddr` is the load-vs-run distinction that bricks an STM32 if gotten wrong (`.data` would land in
the RAM address range, not flash). Dropping the `.bss` tail avoids writing megabytes of zeroes that
were never in the file. Supporting both classes/endiannesses is cheap (fixed offsets) and makes the
parser correct for AArch64/RISC-V64 and big-endian targets without a second code path. Refusing an
out-of-range 64-bit address rather than silently truncating keeps the safety-first framing: a
firmware tool must never quietly write to the wrong place.

**Scope note.** `ElfImage` is a **Layer-2 parser** (Decision 3) and is independent of the Layer-1
`FirmwareFormats` registry (Decision 2), which is not built yet. ELF's extension/content brick-guard
therefore currently lives in `FirmwareImage.Load` alongside HEX/bin; when Layer 1 lands, ELF registers
its `(extensions, \x7FELF sniff, MemoryImage)` there like every other format and the `Load` switch
collapses into `Detect`. Nothing about this decision changes then - only where detection is wired.
