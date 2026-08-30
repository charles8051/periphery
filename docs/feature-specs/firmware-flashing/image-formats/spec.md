# Feature Spec: Firmware image formats (shared parsing, brick-sniffing, conversion)

<!--
Authoritative, LIVING spec for how FlashAnything / the bootloader packages identify,
validate, and parse firmware files. Read this before adding a new format or a new flasher.
The "what" lives here and is rewritten as the design evolves; the "how / why" decisions are
in the append-only sibling [`adr.md`](adr.md).
-->

## Status

**Draft** - design. Phase 0 is built (Intel HEX + raw binary + a per-format brick-guard
inside [`FirmwareImage.Load`](../../../../src/Periphery.Firmware/FirmwareImage.cs)); this spec
covers consolidating format handling into one shared layer across all families.

**ELF (Kind 1) landed 2026-06-18**, ahead of the Layer-1 registry: a pure
[`ElfImage`](../../../../src/Periphery.Firmware/ElfImage.cs) parser (`PT_LOAD` program headers
-> segments) + [`FirmwareImage.FromElf`](../../../../src/Periphery.Firmware/FirmwareImage.cs),
wired into `FirmwareImage.Load` (`.elf`/`.axf`/`.out`) with a `\x7FELF` brick-guard (and the
`.bin` guard now also rejects ELF magic). This is a Layer-2 parser (independent of the registry);
its brick-guard still lives in `Load` and folds into Layer 1 when that lands. See ADR Decision 6.

> Grounds: [ADR-0061](../../../adr/0061-firmware-flashing-platform.md) DEC-003 (`Periphery.Firmware`
> is the shared image model: HEX / bin / DfuSe) and DEC-007 (the roadmap adds `.zip` / GBL parsing
> "in `Periphery.Firmware`"). Directly addresses the brick-guard duplication noted while wiring `.hex`:
> EFM8's [`Efm8FirmwareImage`](../../../../src/Periphery.Bootloader.Efm8.Usb/Efm8FirmwareImage.cs) and
> Firmware's `FirmwareImage.Load` now implement the same declare+sniff+reject pattern twice.

| Field        | Value                                   |
|--------------|-----------------------------------------|
| Author       | Charles Lee                             |
| Created      | 2026-06-17                              |
| Last Updated | 2026-06-17                              |
| Project      | Periphery.Firmware (+ consumers)        |
| Branch       | `feat/firmware-flashing-platform`       |

---

## Purpose

Give every flasher **one place** to identify a firmware file, verify it is what its name
claims (the brick-guard), and - when the format is a memory image - parse it into the
addressed bytes a bootloader writes. Without this, each family re-implements format detection
and content-sniffing: there are already **two** parallel brick-guards (EFM8's `Efm8FirmwareImage`
and `FirmwareImage.Load`), and the roadmap adds STM32 (`.dfu`), ESP32, EFR32 (`.gbl`), and
Nordic (`.zip`) - i.e. N ad-hoc guards drifting apart.

The non-negotiable framing, inherited from the platform: **flashing the wrong bytes bricks the
device.** Format handling is a safety layer, not a convenience - a `.hex` streamed to flash as
raw text, or a `.gbl` fed to an STM32 DFU flasher, must be refused before any byte moves.

---

## The format landscape (the organizing insight: two kinds)

Firmware files split into two kinds, and **that split decides what can be shared.**

### Kind 1 - Memory images (decompose to `address -> bytes`)

What a "write bytes to flash" bootloader consumes. All of these collapse to the **same value**:
`FirmwareImage` (contiguous addressed `FirmwareSegment`s).

| Format | Ext | Content magic | Target(s) | Status |
|---|---|---|---|---|
| Intel HEX | `.hex` | leading `:` | STM32 (both), EFM8 source, generic | **have** (`IntelHexImage`) |
| Raw binary | `.bin` | *(none - base supplied at load)* | STM32, ESP32, all | **have** (`FromBytes`) |
| DfuSe | `.dfu` | `DfuSe` prefix / `UFD`+CRC-32 suffix | STM32 (ST's native) | planned (DEC-003) |
| Motorola S-record | `.srec` `.s19` `.mot` | leading `S` | some toolchains (NXP, parts of ARM) | likely |
| ELF | `.elf` `.axf` `.out` | `\x7FELF` | every GCC/Clang build's native output | **have** (`ElfImage`) |

A flasher that writes bytes to addresses (STM32 DFU, STM32 USART/AN3155, esptool raw write)
consumes Kind 1 **uniformly** - it never cares which on-disk format produced the segments.

### Kind 2 - Packaged / protocol-native blobs (consumed as-is or merely unwrapped)

These carry their own structure, signature, and/or CRC and are the bootloader's **native**
consumption format. The host does **not** re-address them.

| Format | Ext | Content magic | Target | Host's job |
|---|---|---|---|---|
| EFM8 boot-record stream | `.efm8` `.tfi` | leading `$` | EFM8 (AN945) | validate, replay as-is |
| Gecko GBL | `.gbl` | GBL header tag | EFR32 OTA | hand to bootloader as-is |
| Nordic DFU package | `.zip` | `PK\x03\x04` + `manifest.json` | nRF Secure DFU | unzip -> feed `.bin` + `.dat` init packet |
| ESP application image | `.bin` (+ offsets) | `0xE9` | ESP32 | flash chunk(s) at partition offsets |

These do **not** become `FirmwareImage` segments. You also do not *build* a signed `.zip` /
GBL on the host - there is no host-side conversion *into* them.

---

## What can be shared (by concern)

| Concern | Kind 1 (memory images) | Kind 2 (packaged blobs) |
|---|---|---|
| **Detect / brick-sniff** | shared registry | **same shared registry** (magic-byte ID works for blobs too) |
| **Parse -> addressed image** | one `FirmwareImage` + `From*` parsers | does not reduce to address->bytes |
| **Convert** | *into* `FirmwareImage` (shared); *out* to the wire is per-flasher | no host-side build; **unwrap only** |
| **Generic primitives** | - | ZIP extract, CRC-32, signature check can be shared utilities |

**Detection is universal; the image model is universal within Kind 1; the blobs share only
detection + utility primitives.**

---

## The two shared layers

### Layer 1 - Format-detection + brick-sniff registry (universal, serves every format)

A single registry where each format registers `(extensions, content-sniff predicate, kind)`.
One `Detect(content, fileName)` reconciles the declared extension against the sniffed content
and **throws on mismatch**. This is the layer that spans *all* families and **absorbs the two
existing brick-guards** (EFM8's and Firmware's both become consumers).

### Layer 2 - `FirmwareImage` addressed-segment model + parsers (Kind 1 only)

The existing `FirmwareImage` (segments) plus a `From*` parser per memory-image format
(`FromIntelHex` exists; add `FromSRecord`, `FromElf`, `FromDfuSe`). Grows as memory-image
formats appear. Packaged blobs are **not** forced into this model - they get a thin per-family
"validate + unwrap" seam and register with Layer 1 for detection only.

---

## Affected Layers

| Project | Change Type |
|---|---|
| `Periphery.Firmware` | **New:** `FirmwareFormat` enum, `FirmwareFormatInfo` descriptor, `FirmwareFormats` registry + `Detect`. **Refactor:** `FirmwareImage.Load` to consume `Detect`. **Add (scoped):** `FromSRecord` / `FromElf` / `FromDfuSe` parsers as those formats land — **`FromElf` / `ElfImage` done 2026-06-18** (Decision 6). |
| `Periphery.Bootloader.Efm8.Usb` (was `Periphery.Efm8Bootloader`) | **Refactor:** `Efm8FirmwareImage` consumes the shared registry; drop its private `FormatFromFileName` / `Sniff` (register EFM8's `.efm8`/`.tfi` + `$` magic with Layer 1 instead). |
| `Periphery.Bootloader` | **Small:** a provider/programmer declares its **accepted formats** (or accepted *kind*), so "this flasher can't take a `.gbl`" is a shared, uniform rejection rather than per-family code. |
| `Periphery.FlashAnything` (+ CLI / GUI) | **Small:** surface the shared "unsupported for this target" reason via the existing `FirmwareLoadFailed` / `FirmwareError` path. |
| `tests/Periphery.Firmware.Tests` | **New:** registry/`Detect` table (every format's extension + magic + mismatch), per-parser round-trips. |

---

## Requirements

- [ ] **One registry.** Every known format registers once with `(extensions[], sniff predicate, kind)`. Adding a format is one registry entry (+ a parser if it is Kind 1).
- [ ] **Universal `Detect`.** `Detect(content, fileName)` infers the format from the extension, confirms via content sniff, and throws `FirmwareFormatException` on mismatch - for **all** formats, both kinds.
- [ ] **Absorb the duplication.** `FirmwareImage.Load` and EFM8's `Efm8FirmwareImage` both consume the shared registry; neither keeps a private extension/sniff table.
- [ ] **Kind 1 parses to `FirmwareImage`.** Memory-image formats share `FirmwareImage` + `From*`; a byte-writing flasher consumes them without caring about the on-disk format.
- [ ] **Kind 2 stays family-owned.** Packaged blobs are validated/unwrapped by their family; they are **not** coerced into `FirmwareImage`.
- [ ] **Providers declare what they accept.** A flasher advertises its accepted formats/kind; feeding an unaccepted format is rejected with a clear, shared message before any device IO.
- [ ] **Pure + total** ([ADR-0052](../../../adr/0052-periphery-treehopper-pure-core.md)): detection and parsing are pure functions over in-memory bytes - the refusal happens while the board still runs its current firmware.
- [ ] **No third-party deps, AOT-clean** (ADR-0024 / ADR-0061): the registry and parsers are BCL-only. (A `.zip`/GBL parser must use BCL `System.IO.Compression`, not a third-party lib.)

---

## Public API (proposed)

```csharp
namespace Periphery.Firmware;

public enum FirmwareKind { MemoryImage, PackagedBlob }

public enum FirmwareFormat
{
    IntelHex, RawBinary, DfuSe, SRecord, Elf,          // Kind 1 (memory images)
    Efm8BootRecords, GeckoGbl, NordicZip, EspImage,    // Kind 2 (packaged blobs)
}

public sealed record FirmwareFormatInfo(
    FirmwareFormat Format,
    string[] Extensions,                       // ".hex", ".ihex"
    Func<ReadOnlyMemory<byte>, bool> Sniff,    // magic / leading-byte test
    FirmwareKind Kind);

public static class FirmwareFormats
{
    public static IReadOnlyList<FirmwareFormatInfo> Registry { get; } // every known format
    public static FirmwareFormat? FromExtension(string fileName);
    public static FirmwareFormat? Sniff(ReadOnlyMemory<byte> content);

    /// <summary>Extension declares, content confirms; throws FirmwareFormatException on mismatch.</summary>
    public static FirmwareFormat Detect(ReadOnlyMemory<byte> content, string fileName);
}

// FirmwareImage.Load becomes a thin consumer:
//   var fmt = FirmwareFormats.Detect(content, fileName);
//   return fmt switch {
//       _ when info(fmt).Kind != MemoryImage => throw "not a memory image for this flasher",
//       IntelHex  => FromIntelHex(...), RawBinary => FromBytes(...), DfuSe => FromDfuSe(...), ...
//   };
```

A provider advertises acceptance, e.g. `IReadOnlySet<FirmwareFormat> AcceptedFormats` (STM32 DFU:
`{IntelHex, RawBinary, DfuSe}`; EFM8: `{IntelHex, Efm8BootRecords}`), checked against `Detect`.

---

## Open Questions

- [x] **ELF scope.** *Resolved (ADR Decision 6, 2026-06-18.)* Parse only `PT_LOAD` program headers into segments using `p_paddr` (the LMA) and `p_filesz` bytes (the `.bss` tail is never flashed); ignore section headers / debug / symbols. Support both classes (32/64-bit) and both endiannesses; reject a 64-bit load address outside the 32-bit space. There is no `e_type` allow-list - an ELF with no loadable program data (a relocatable `.o`, debug-only file) naturally produces zero segments and is rejected. Mirrors `objcopy -O binary` / OpenOCD / pyOCD.
- [ ] **DfuSe multi-target / multi-element.** A `.dfu` can carry several targets/elements (alt settings, multiple regions). Which to select, and how to surface a multi-target file to the operator? Verify the CRC-32 suffix and the embedded VID/PID against the connected device?
- [ ] **ESP layout vs format.** An ESP image is often a *set* of `.bin` at partition offsets (a `flasher_args.json`), not one file. Is that a "format" Layer 1 detects, or a project-layout concern above it?
- [ ] **Where conversion *out* lives.** `FirmwareImage` -> EFM8 boot-record stream is a host-side serialization. Is that an EFM8-family concern (current `Efm8BootRecordGenerator`) or does any shared "image -> records" helper belong in Firmware? (Current answer: family-specific; it only *reads* the shared `IntelHexImage`.)
- [ ] **Provider acceptance shape.** A flat `AcceptedFormats` set, or a coarser `AcceptedKinds` plus per-format opt-outs? How does the registry expose a format's `Kind` to the bootloader layer without that layer referencing every parser?

---

## Related

| Type | Link |
|------|------|
| Decisions (how / why) | [`adr.md`](adr.md) |
| Firmware-flashing platform | [`../../../adr/0061-firmware-flashing-platform.md`](../../../adr/0061-firmware-flashing-platform.md) |
| Sibling feature (autoflash) | [`../autoflash/spec.md`](../autoflash/spec.md) |
| Functional-core / shell | [`../../../adr/0052-periphery-treehopper-pure-core.md`](../../../adr/0052-periphery-treehopper-pure-core.md) |
| Current shared image | [`FirmwareImage.cs`](../../../../src/Periphery.Firmware/FirmwareImage.cs) / [`IntelHexImage.cs`](../../../../src/Periphery.Firmware/IntelHexImage.cs) / [`ElfImage.cs`](../../../../src/Periphery.Firmware/ElfImage.cs) |
| The duplicated guard to absorb | [`Efm8FirmwareImage.cs`](../../../../src/Periphery.Bootloader.Efm8.Usb/Efm8FirmwareImage.cs) |
