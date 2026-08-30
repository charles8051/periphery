# Feature Spec: Treehopper firmware reflash (EFM8 HID bootloader)

<!--
Authoritative, LIVING spec for the in-house Treehopper firmware-reflash path. Read
this before editing any code in the feature's scope; the "Affected Layers" table
names the projects to touch. The "how / why" decisions live in the append-only
sibling [`adr.md`](adr.md); this file is the "what" and is rewritten as the feature
evolves.
-->

## Status

**Implemented, and since renamed.** Two package renames happened after this spec was
written, so the names below are historical:

| This spec says | Today |
|---|---|
| `Periphery.Efm8Bootloader` | `Periphery.Bootloader.Efm8.Usb` — split under the `Periphery.Bootloader.{family}.{transport}` scheme of [ADR-0061](../../../adr/0061-firmware-flashing-platform.md), with the format-neutral `IntelHexImage` moved to `Periphery.Firmware` |
| `Periphery.Treehopper.Updater` | **retired.** Its pure logic moved into `Periphery.Treehopper.Control` (`FirmwareVersion`) and `Periphery.Treehopper.Control.Cli` (`FirmwareSource`, command surface) — see [`../control-app/spec.md`](../control-app/spec.md). The reflash wrapper itself lives in `Periphery.Treehopper.Firmware`. |

| Field        | Value                              |
|--------------|------------------------------------|
| Author       | Charles Lee                        |
| Created      | 2026-06-16                         |
| Last Updated | 2026-08-24 (rename reconciliation)  |
| Project      | Periphery.Treehopper.Firmware / Periphery.Bootloader.Efm8.Usb |
| Branch       | `feat/efm8-hid-bootloader-flasher` |

---

## Purpose

Reflash a Treehopper board's EFM8 firmware from our own stack, end to end, without
the upstream Treehopper SDK or SiLabs tooling on the host. This unblocks testing
patched firmware (e.g. the EFM8 SPI-FIFO lock-up fix) on a real board: build → boot
record → reboot into bootloader → replay over USB-HID → return to the app.

The host-side **upload loop** was the last missing piece — image build (Keil) and
boot-record conversion (`hex2boot`) already exist, and `RebootIntoBootloaderAsync`
already enters the bootloader. This feature adds the generic EFM8 HID uploader and a
Treehopper convenience wrapper over it.

---

## Affected Layers

| Project                    | Change Type                                                                 |
|----------------------------|-----------------------------------------------------------------------------|
| `Periphery.Efm8Bootloader` → **`Periphery.Bootloader.Efm8.Usb`** | **New project.** Generic, device-agnostic EFM8 HID bootloader uploader (pure protocol core + transport seam + HID adapter). |
| `Periphery.Treehopper` → **`Periphery.Treehopper.Firmware`** | New `TreehopperFirmwareUpdate` reflash wrapper + `TreehopperReflashOptions` / `TreehopperReflashResult`; project references `Periphery.Hid` + the EFM8 bootloader package. The wrapper now lives in its own `Periphery.Treehopper.Firmware` project rather than in `Periphery.Treehopper`. |
| `Periphery.Hid`            | **None** — existing `WriteReportAsync` / `ReadReportAsync` / `OpenAsync` cover the protocol. No new primitive, no new third-party dependency. |
| `Periphery` (core)         | **None** — discovery reuses `Devices.Enumerate().WithUsbId(...)`.            |
| `Periphery.Treehopper.Updater` → **retired** | **New project**, since removed. Slim, SSH-friendly CLI that scans for boards and runs a version-gated fleet auto-update over the wrapper. Its logic was folded into `Periphery.Treehopper.Control` / `.Cli`; the generic hands-free flashing story is now the separate `Periphery.FlashAnything` family. |
| `tests/Periphery.Efm8Bootloader.Tests` → **`tests/Periphery.Bootloader.Efm8.Usb.Tests`** | **New project.** Tests over the pure core, the uploader shell (fake transport), and a real `hex2boot`-produced `.efm8`. |
| `tests/Periphery.Treehopper.Updater.Tests` → **`tests/Periphery.Treehopper.Control{,.Cli}.Tests`** | **New project**, since removed. Tests over the CLI's pure logic (arg parser, version-gating planner, version parsing). |

---

## Requirements

- [x] Generic EFM8 HID uploader: parse a `.efm8`/`.tfi` boot-record stream, replay each
      `$`-framed record over an HID transport in ≤64-byte output reports, read the
      4-byte reply, classify it, and stop on the first non-`@`.
- [x] Device-agnostic (the EFM8 factory bootloader is generic across EFM8 parts) and
      transport-agnostic (binds to `IEfm8Transport`, not HID directly).
- [x] Built on `Periphery.Hid` primitives; no new third-party HID dependency.
- [x] Treehopper wrapper: one call from an open board (or `DeviceInfo`) + boot-record
      source → reboot, wait for `0x10C4:0xEAC9`, verify, replay, optionally wait for the
      app `0x10C4:0x8A7E` to return.
- [x] Functional-core / imperative-shell (ADR-0052): parse / chunk / classify are pure
      and total; HID IO, discovery, and re-enumeration waits live in the shell.
- [x] Safety: verify VID/PID before any write; replay-only (no synth/reorder/mutate);
      stop and report the failing record on any non-`@`; explicit destructive entry point.
- [x] Tests: well-formed multi-record success with exact chunking; malformed framing
      throws before any write; mid-stream CRC error stops with no further writes;
      >64-byte frame chunk/report-id correctness; one real `hex2boot` `.efm8` exercised
      end to end against the fake transport.
- [x] `dotnet build Periphery.slnx -c Release` green; `dotnet test` green.

---

## Architecture (functional core / imperative shell — ADR-0052)

### Pure core — `Efm8Protocol` (no IO, no clock, no `Task`)

[`src/Periphery.Efm8Bootloader/Efm8Protocol.cs`](../../../../src/Periphery.Bootloader.Efm8.Usb/Efm8Protocol.cs):

| Function | Contract |
|----------|----------|
| `ParseRecords(ReadOnlyMemory<byte>)` → `ImmutableArray<Efm8BootRecord>` | Splits the stream into `$`-framed records (zero-copy slices). **Total**: throws `Efm8BootFormatException` (before any IO) on bad start byte, zero-length record, declared-length overrun, or empty stream. |
| `ChunkFrame(frame, reportSize = 64)` → `IReadOnlyList<ReadOnlyMemory<byte>>` | The output-report chunks for one frame; final chunk is short. |
| `ClassifyReply(byte)` → `Efm8ReplyCode` | `0x40 '@'` = `Acknowledge`; `0x41/0x42/0x43` = `RangeError`/`CrcError`/`OtherError`; anything else (incl. timeout) = `Unknown`. |

### Imperative shell

| Type | Role |
|------|------|
| `IEfm8Transport` | The seam: `WriteOutputReportAsync(chunk)` + `Task<byte> ReadReplyAsync()`. Tests substitute `FakeEfm8Transport`. |
| `HidEfm8Transport` | Production transport over a `Periphery.Hid.HidDevice`. |
| `Efm8BootloaderUploader.UploadAsync(...)` | Drives the core: parse, then per record write chunks → read reply → stop on first non-`@`. Returns `Efm8UploadResult`. |
| `TreehopperFirmwareUpdate.ReflashAsync(...)` | Treehopper wrapper: reboot → poll for bootloader → verify → upload → poll for app. |

---

## Public API

### Generic uploader (`Periphery.Efm8Bootloader`)

```csharp
Task<Efm8UploadResult> Efm8BootloaderUploader.UploadAsync(
    IEfm8Transport transport,
    ReadOnlyMemory<byte> bootRecords,
    Efm8FlashConfirmation confirmation,            // required; must be ConfirmEraseAndReflash
    IProgress<Efm8UploadProgress>? progress = null,
    CancellationToken ct = default);
```

`Efm8UploadResult` carries `Success`, `RecordsSent`/`TotalRecords`/`TotalBytes`, and on
failure `FailedRecordIndex` + `FailedCommand` + `FailedReply` + `FailedReplyByte`, plus a
one-line `Describe()`.

### Treehopper wrapper (`Periphery.Treehopper`)

```csharp
Task<TreehopperReflashResult> TreehopperFirmwareUpdate.ReflashAsync(
    TreehopperBoard board,                          // or: DeviceInfo deviceInfo
    Stream bootRecords,
    Efm8FlashConfirmation confirmation,
    TreehopperReflashOptions? options = null,       // timeouts / poll interval / wait-for-app
    IProgress<Efm8UploadProgress>? progress = null,
    CancellationToken ct = default);
```

`ReflashAsync` **takes ownership of and disposes `board`** (the handle dies on bootloader
entry). `TreehopperReflashResult` is the `Efm8UploadResult` plus `ApplicationReturned`.
Constants: `BootloaderVid/BootloaderPid` (`0x10C4:0xEAC9`), `ApplicationVid/ApplicationPid`
(`0x10C4:0x8A7E`).

---

## Protocol (verified against the SiLabs references)

Frame: `0x24 ('$')`, 1 length byte (count of bytes after it = command + data), 1 command
byte, payload. Confirmed by `efm8load.py:140-145`, `hex2boot.py:72-107`, and the upstream
C# loader `FirmwareUpdateDevice.cs:67-74`.

- **Output report** (host→device): 64-byte payload (`SIZE_OUT`). Frames are written in
  successive ≤64-byte reports.
- **Input report** (device→host): 4-byte payload (`SIZE_IN`); reply status is byte 0.
- **ACK**: success is `'@'` (0x40). Valid replies `b'@ABC'` (0x40–0x43); anything else /
  timeout → `'?'`. Continue **only** on `'@'`.

### Report ID / chunking

The bootloader has a single unnamed report → report ID always `0`. The transport writes
each chunk as `new HidReport(0x00, chunk)`; the Periphery.Hid Windows backend
([`WindowsHidDevice.cs:160-178`](../../../../src/Periphery.Hid/Windows/WindowsHidDevice.cs))
builds a 65-byte buffer (`buffer[0]=0` + payload, zero-padded) — reproducing exactly
`efm8load.py:44-48`'s dummy report-ID prefix and `FirmwareUpdateDevice.cs:105-111`'s
`SizeOut+1` write. On read, the OS report-ID byte is stripped, so `Data.Span[0]` is the
reply byte (`efm8load.py:50-56`'s `in_report[0]`).

### Bootloader USB identity

`hidport.py:16-19` — `EFM8_LOADERS = [(0x10C4, 0xEAC9), (0x10C4, 0xEACA)]`. Treehopper's
bootloader is **`0x10C4:0xEAC9`** (app is `0x10C4:0x8A7E`).

---

## Safety rules (this flashes firmware — bricking is real)

1. **Verify before writing.** The wrapper re-checks the opened device VID/PID equals
   `0x10C4:0xEAC9`; a mismatch throws `Efm8BootloaderException` and writes nothing.
2. **Replay only.** Records are written verbatim, in order — never synthesised,
   reordered, or mutated. The bricking failsafes (reset vector written **last**,
   `hex2boot.py:160-166`; Lock `0x35` never emitted unless `-l`, `hex2boot.py:170-171`)
   live in `hex2boot`. **Input must come from `hex2boot`.**
3. **Stop on first error.** Any non-`@` aborts immediately; the result names the failing
   record (index + command + reply). No further writes.
4. **Explicit destructive entry point.** Every upload entry point requires a non-defaulted
   `Efm8FlashConfirmation.ConfirmEraseAndReflash`.
5. **Interrupted flash is recoverable** while records never target the reserved bootloader
   region (`-m ub1` keeps the app below it). The device leaves the bootloader only on the
   final RunApp (`0x36`), so a failed flash leaves it in the bootloader; re-run.

---

## End-to-end reflash steps

1. **[reuse: Keil / Simplicity Studio]** Build patched firmware → `Treehopper.hex`.
2. **[reuse: hex2boot]** `python hex2boot.py -o treehopper.tfi -m ub1 -b 0 Treehopper.hex`
   (needs `pip install intelhex`).
3. **[this feature]**

   ```csharp
   await using var board = await TreehopperBoard.OpenFirstAsync();
   using var image = File.OpenRead("treehopper.tfi");
   var progress = new Progress<Efm8UploadProgress>(p => Console.WriteLine($"{p.Percent:0}%"));
   var result = await TreehopperFirmwareUpdate.ReflashAsync(
       board, image, Efm8FlashConfirmation.ConfirmEraseAndReflash, progress: progress);
   // ReflashAsync disposes `board`; do not use it afterwards.
   Console.WriteLine(result.Upload.Describe());
   Console.WriteLine($"App returned: {result.ApplicationReturned}");
   ```

   For any EFM8 part / non-HID transport, call `Efm8BootloaderUploader.UploadAsync` with a
   custom `IEfm8Transport`.

---

## CLI updater (`treehopper-update`) — fleet auto-update

> **Superseded (2026-06).** The standalone `Periphery.Treehopper.Updater` tool described
> below has been **retired** and folded into the Treehopper control app — its fleet
> firmware logic now lives behind `treehopper firmware list|all|board`
> (see [`../control-app/spec.md`](../control-app/spec.md), ADR
> Decision 3). The safe-by-default dry run, version gating, embedded-image build
> mechanism, and exit codes are preserved verbatim. The section below is kept for the
> historical record of the original tool.

[`src/Periphery.Treehopper.Updater`](../../../../src/Periphery.Treehopper.Firmware) (retired) was a slim,
dependency-light CLI (no Spectre / framework — hand-rolled parser) packaged as a `dotnet tool`
and self-contained-publishable, so roam ships it to each kiosk and it runs over SSH. It is the
fleet front-end to `TreehopperFirmwareUpdate.ReflashAsync`.

**Verbs** (default `list`):

| Command | Behaviour |
|---------|-----------|
| `treehopper-update [list]` | Scan and report every board + the plan. **Read-only** (version read via `UsbDevice`, no `ConfigureDevice`). |
| `treehopper-update all [--yes]` | Auto-update: flash every board that needs it, **strictly sequentially**. |
| `treehopper-update board <serial> [--yes]` | Update one board by serial (or device id). |

**Safe-by-default for SSH.** Without `--yes`, every verb is a **dry run** — it prints the plan
and writes nothing. `--yes` performs the flash; there is no interactive prompt (none possible
over a non-TTY pipe). `--json` emits a machine-readable report for fleet automation. Exit codes:
`0` success/clean dry run, `1` a board failed, `2` usage/refused, `3` no image, `4` no board found.

**Version gating.** `list`/`all`/`board` compare each board's current `bcdDevice` version against
a target: `--target-version <code>` (decimal `274` or hex `0x0112`), else the version baked into
the embedded image at build time. Below target → flash; at/above → skip (no downgrade). `--force`
flashes regardless. `all` **refuses to flash** with `--yes` when no target is known and not
`--force` (no unguarded mass-flash); `board` flashes its explicit target.

**Embedded image (fleet artifact).** The binary carries the known-good image so it is itself the
deployable update; `--file <path>` overrides. No firmware blob is committed — a release build
supplies it:

```bash
dotnet publish src/Periphery.Treehopper.Updater -c Release -r <rid> --self-contained \
  -p:TreehopperFirmwareImage=<abs path to .tfi> -p:TreehopperFirmwareVersion=<bcdDevice code>
```

**Sequential-flash hazard (load-bearing).** Every Treehopper in the bootloader enumerates as the
same generic id `0x10C4:0xEAC9` — two in the bootloader at once are indistinguishable. The runner
reboots one board, flashes it, and waits (via `ReflashAsync`) for the app to return before the
next, so only one board is ever in the bootloader. A board left stuck in the bootloader from a
prior failed run is therefore ambiguous if another is also there; recover it in isolation.

```bash
treehopper-update                       # see fleet state on this host (read-only)
treehopper-update all                   # preview what an update would do
treehopper-update all --yes --json      # update every out-of-date board (fleet/SSH)
```

---

## Testing

[`tests/Periphery.Efm8Bootloader.Tests`](../../../../tests/Periphery.Bootloader.Efm8.Usb.Tests) — 25 tests, net8.0:

- **`Efm8ProtocolTests`** — parse (well-formed multi-record; bad start byte; declared-length
  overrun; truncated header; empty; zero-length), chunk (larger / smaller / exactly one
  report), `ClassifyReply` mapping.
- **`Efm8BootloaderUploaderTests`** — all-`@` success; exact chunk sequence for a >64-byte
  frame; malformed throws before any write; mid-stream `B` (CRC) stops and reports the
  record with no further writes; missing confirmation throws before any write; per-record
  progress (synchronous `IProgress` sink — no `Progress<T>` marshalling race).
- **`Efm8RealBootFileTests`** — a real `.efm8` (`Assets/synthetic.efm8`, produced by
  `hex2boot.py` from a synthetic Intel HEX; see [`Assets/README.md`](../../../../tests/Periphery.Bootloader.Efm8.Usb.Tests/Assets/README.md))
  parsed into its 6 records and replayed against the fake transport; the 133-byte
  erase-with-data frame exercises the chunk-boundary path end to end.

`tests/Periphery.Treehopper.Updater.Tests` — **removed** when the Updater was retired.
None of its three suites survive under their original names; the coverage moved with the
code into the control app:

- **`CliParserTests`** — verbs, selectors, flags/aliases, value-bearing options, and every
  malformed-input error path. Now
  [`tests/Periphery.Treehopper.Control.Cli.Tests/CliTests.cs`](../../../../tests/Periphery.Treehopper.Control.Cli.Tests/CliTests.cs).
- **`UpdatePlannerTests`** — version gating: below → update, at → skip, above → skip-newer,
  `--force` overrides all, null-target → ungated update.
- **`FirmwareVersionTests`** — target-version parse (decimal / hex / garbage) and display
  format. The last two are now
  [`tests/Periphery.Treehopper.Control.Tests/FirmwareHelpersTests.cs`](../../../../tests/Periphery.Treehopper.Control.Tests/FirmwareHelpersTests.cs)
  over `Periphery.Treehopper.Control.FirmwareVersion`.

The pure cores (protocol, planner, parser) are exercised hardware-free; the shells' real HID IO /
discovery / re-enumeration waits are integration concerns run against hardware by an operator
(not in CI).

---

## Open Questions

- [ ] Exact AN945 A/B/C reply-byte meanings are taken from the open re-implementations, not
      a clean read of the AN945 PDF. **Non-blocking** — the labels are diagnostic only; the
      uploader continues solely on `'@'`, so a wrong label cannot cause a wrong continue/stop.
- [ ] No on-hardware integration test of the two USB re-enumerations yet (app → bootloader →
      app); the timing tunables in `TreehopperReflashOptions` are first-pass defaults.

---

## Related

| Type | Link |
|------|------|
| Decisions (how / why) | [`adr.md`](adr.md) |
| Reverse-engineering survey | [`../../../explorations/treehopper-firmware-update.md`](../../../explorations/treehopper-firmware-update.md) |
| Functional-core / shell ADR | [`../../../adr/0052-periphery-treehopper-pure-core.md`](../../../adr/0052-periphery-treehopper-pure-core.md) |
| Bootloader entry | `Periphery.Treehopper.TreehopperBoard.RebootIntoBootloaderAsync` |
| SiLabs references (read-only) | `D:\_efm8\Tools\Source\{efm8load.py, hidport.py, hex2boot.py}` |
| Upstream C# loader | `treehopper-sdk\NET\API\Treehopper.Firmware\FirmwareUpdateDevice.cs` |
