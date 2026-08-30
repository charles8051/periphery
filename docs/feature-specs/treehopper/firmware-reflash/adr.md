# ADR: Treehopper firmware reflash (EFM8 HID bootloader)

<!--
Append-only / superseded, never rewritten. Decisions are numbered Decision 1..N.
The "what" (living requirements, current API) is in the sibling spec.md; this file
records the "how / why" so a future contributor sees the tradeoffs, not just the
result. If a decision here grows to be cited by a second feature, graduate it into a
numbered repo-level ADR under docs/adr/.
-->

Context: building the host-side upload loop for the in-house Treehopper firmware
reflash path (see [`spec.md`](spec.md) and the survey
[`../../../explorations/treehopper-firmware-update.md`](../../../explorations/treehopper-firmware-update.md)).
Periphery has no external consumers, so the bias is "right design" over "compatible".

---

## Decision 1 — A dedicated `Periphery.Efm8Bootloader` project, not folded into `Periphery.Hid` or `Periphery.Treehopper`

**Decision.** The generic uploader ships as its own project referencing `Periphery` +
`Periphery.Hid`.

**Why.** The EFM8 factory bootloader (AN945) is generic across all EFM8 parts and is a
distinct protocol concern — not generic HID I/O (so it doesn't belong in `Periphery.Hid`)
and not Treehopper-specific (so it doesn't belong in `Periphery.Treehopper`). Keeping it
separate matches the repo's one-project-per-concern grain (`Periphery.Camera`,
`Periphery.Usb`, …) and lets any future EFM8 device reuse it without depending on
Treehopper. The Treehopper wrapper depends on this project, not vice versa.

---

## Decision 2 — Functional-core / imperative-shell split with an `IEfm8Transport` seam

**Decision.** Parsing, chunking, and reply classification are pure, total functions on
`Efm8Protocol`. All IO sits behind `IEfm8Transport` (`WriteOutputReportAsync` +
`ReadReplyAsync`); `HidEfm8Transport` is the production implementation.

**Why.** ADR-0052's grain. The protocol logic becomes exhaustively unit-testable with
hand-built byte arrays and a fake transport — no hardware, no clock. The seam also keeps
the uploader link-agnostic (the SiLabs bootloader also speaks UART/SMBus), so a future
non-HID transport is a new `IEfm8Transport`, not a rewrite.

---

## Decision 3 — Replay only: never synthesise, reorder, or mutate a record

> **Partially superseded by Decision 11.** The "input must come from `hex2boot`" hard rule
> is lifted: the records are now produced in-repo by `Efm8BootRecordGenerator`, whose output
> is pinned byte-for-byte to `hex2boot`. The *uploader* is still replay-only (it never decides
> what is safe to write) — that half stands.

**Decision.** The uploader writes the `hex2boot`-produced records verbatim, in order. It
holds no knowledge of the AN945 command set beyond framing.

**Why.** The device-bricking guarantees — reset vector written **last** as a failsafe, and
a Lock (`0x35`) record never emitted — live in `hex2boot` (`hex2boot.py:160-173`), upstream.
Re-deriving them on the host would duplicate brittle, safety-critical logic. Replaying bytes
keeps the single source of truth for "what is safe to write" in the tool that already owns
it. Documented hard rule: **input must come from `hex2boot`**.

---

## Decision 4 — Required, non-defaulted `Efm8FlashConfirmation` on every destructive entry point

**Decision.** `UploadAsync` and `ReflashAsync` take a required
`Efm8FlashConfirmation confirmation` and reject anything but `ConfirmEraseAndReflash`.

**Why.** This erases and rewrites firmware. An enum argument with no usable default forces
the caller to name the intent at the call site — it cannot be triggered by an accidental
call or a defaulted `bool`. Chosen over a `bool confirm = false` (too easy to pass `true`
reflexively) and over a separate "armed" object (more ceremony than the risk warrants).

---

## Decision 5 — Verify the opened device VID/PID before writing a byte (wrapper safety gate)

**Decision.** `TreehopperFirmwareUpdate` re-checks the opened device is `0x10C4:0xEAC9`
after opening, even though it discovered the device by that exact filter; a mismatch throws
and writes nothing.

**Why.** Defence in depth on a brick-capable path. Discovery and open are separated by a
re-enumeration window; the cheap re-check guarantees we never stream firmware records at a
device that is not the bootloader (e.g. a racing enumeration returning a stale/other handle).

---

## Decision 6 — Protocol non-`@` returns a result; malformed input / IO faults / safety throw

**Decision.** A clean protocol-level reject (any non-`@` reply) is reported via
`Efm8UploadResult` (`Success=false`, failing record named). Malformed framing, a refused
safety gate, and IO faults throw (`Efm8BootFormatException` / `Efm8BootloaderException`).

**Why.** A non-`@` reply is an expected, well-described outcome the caller routinely
inspects (which record, which error) — a return value, not an exception. Malformed input is
a programming/sourcing error caught before any write; a wrong device or a dropped link is
exceptional. Splitting the two keeps the happy path branch-light and the failure modes
unambiguous.

---

## Decision 7 — `ReflashAsync` takes ownership of and disposes the `TreehopperBoard`

**Decision.** The wrapper disposes the board it is handed, immediately after
`RebootIntoBootloaderAsync`.

**Why.** The board handle is dead the moment the device enters the bootloader (USB drops).
Leaving a dead handle open invites a stale handle interfering with the bootloader's clean
re-enumeration. Disposing it inside the call is surprising enough to document loudly, but is
the correct lifecycle — the alternative (caller must dispose a known-dead handle at exactly
the right moment) is more error-prone.

---

## Decision 8 — A/B/C reply labels are diagnostic only

**Decision.** `Efm8ReplyCode` labels `0x41/0x42/0x43` as `RangeError`/`CrcError`/`OtherError`
following the `efm8load.py` host convention, and treats every non-`@` byte identically
(stop + report).

**Why.** The exact AN945 meaning of the three error bytes differs between the primary PDF
and the open re-implementations and was not pinned from a clean read of AN945. Making the
continue/stop decision depend only on `'@'` means a mislabelled error code can never cause a
wrong flash decision — the labels improve a diagnostic message, nothing more.

---

## Decision 9 — A dedicated slim CLI (`Periphery.Treehopper.Updater`), no CLI framework

**Decision.** The fleet updater is its own minimal `dotnet tool` / self-contained app with a
hand-rolled argument parser, not a subcommand of the existing `periphery` Spectre.Console.Cli
tool.

**Why.** It is the unit roam ships to each kiosk and runs over SSH; a single-purpose binary with
minimal dependencies (Treehopper + Efm8Bootloader + Usb) is lighter to deploy and reason about
than dragging the whole device-inspection tool onto every station. The verb surface is tiny
(`list`/`all`/`board` + a handful of flags), so a ~150-line pure parser beats taking a framework
dependency — and keeps the parser unit-testable as a pure function. The pure pieces (parser,
version planner, version parsing) live behind `InternalsVisibleTo` and are exhaustively tested;
the orchestration shell is thin.

---

## Decision 10 — Safe-by-default: dry-run unless `--yes`, refuse ungated mass-flash, gate by version

**Decision.** Without `--yes` every verb is a dry run (scan + plan, no writes). `--yes` performs
the flash with no interactive prompt. `all --yes` is **refused** when no target version is known
and `--force` is not set. Auto-update is version-gated (skip at/above target) and strictly
sequential.

**Why.** The tool runs head-less over SSH where an interactive confirmation is impossible, so the
gate has to be a flag, and the *safe* thing must be the default — `treehopper-update all` previews,
`treehopper-update all --yes` acts. Refusing an ungated `--yes all` prevents a fat-fingered command
from reflashing an entire fleet of healthy boards. Sequential execution is not a preference but a
correctness requirement: all boards in the bootloader share id `0x10C4:0xEAC9` and are
indistinguishable, so only one may be in the bootloader at a time (Decision recorded in the spec's
sequential-flash hazard).

---

## Decision 11 — Bring HEX -> boot-record generation in-house (supersedes Decision 3)

**Decision.** A pure `Efm8BootRecordGenerator` (with `IntelHexImage` and `Efm8BootOptions`)
converts an Intel HEX image to the boot-record stream in C# — a faithful port of SiLabs'
`hex2boot.py`. The uploader's replay path (Decision 3) is unchanged; what changes is that the
records it replays can now be produced in-repo instead of by the external `hex2boot` tool.
`hex2boot` is no longer a required input to the build -> flash pipeline.

**Why.** Decision 3 deferred record synthesis to `hex2boot` to keep the brick-safety guarantees
in one place. In practice the generation is small and fully specified: an Intel HEX parse, a
handful of `$`-framed record encoders, and CRC-16/XMODEM, over a per-part flash map. Owning it
removes the only external-tool dependency in the pipeline (a Python script + the `intelhex`
package, sitting outside the .NET build), makes the conversion unit-testable and AOT-friendly,
and lets it ship in the same package as the uploader. Periphery's no-consumers stance makes
reversing Decision 3 free.

**How the safety is preserved — replicated exactly, not re-derived loosely.**
- The generator is a line-by-line port of the recovered, byte-verified `hex2boot.py`, and is
  **pinned to it by a golden-file test**: it must produce byte-for-byte identical output to real
  `hex2boot` for the same `.hex`. Validated against the synthetic fixture
  (`Assets/synthetic.hex` -> `synthetic.efm8`) and cross-checked against real `hex2boot.py` on
  the real ~15 KB Treehopper firmware (byte-identical).
- The **reset-vector-written-last** failsafe is reproduced: address 0 is blanked to 0xFF for the
  write+verify pass, and the real reset-vector byte is emitted as the final Write before RunApp,
  so an interrupted flash leaves the bootloader in control.
- A **Lock (0x35) record is never emitted** (`Efm8BootOptions.Lock` defaults to null), and the
  part region map keeps writes within the app region — the reserved bootloader region at the top
  of flash is never targeted.

**Consequence.** Decision 3's hard rule ("input must come from `hex2boot`") is lifted. The
uploader still only replays records — it never decides what is safe to write; the records now
come from `Efm8BootRecordGenerator`, whose output is contractually identical to `hex2boot`'s. If
the golden test ever diverges, the generator is wrong, not `hex2boot`.

---

## Decision 12 — The updater takes a firmware FILE; infer the format from the extension, then verify it against the content (brick-guard)

**Decision.** The updater accepts a firmware *file* and infers its format from the extension —
`.hex` (Intel HEX) vs `.tfi`/`.efm8` (a boot-record stream) — then **verifies the inferred format
against the file content** before any device IO. A `.hex` is converted to boot records in-process
(Decision 11); a boot-record file is replayed as-is. A file whose content does not match its
extension, an unrecognized extension, or a malformed file is **refused** (`Efm8BootFormatException`)
on the file bytes, while the board is still safely running its current firmware. There is **no
`--hex` flag**: the extension is the declared intent, the content is the check.

**Why.** Decision 11 let the updater consume a raw `.hex` directly (no manual hex2boot step). But
"two accepted formats" introduces a brick risk: streaming an Intel HEX file (ASCII text) at the
bootloader as if it were boot records would write HEX characters as flash commands. A `--hex` flag
is both poor ergonomics and unsafe (a wrong flag bricks). Inferring from the extension and then
verifying the content — a `:` first byte for HEX, `$` for boot records, then a full parse — means
the file itself must agree with what the user said it is; a mismatch cannot be flashed.

**Layering.**
- **`Efm8FirmwareImage.ToBootRecords(content, fileName, options)`** — the pure, total brick-guard:
  extension → format, sniff content, refuse on mismatch, convert (.hex) / validate (records). One
  implementation, called at every file boundary.
- **Entry points.** `TreehopperFirmwareUpdate.ReflashFromFileAsync(path)`; the CLI `--file`
  (refuses up front in `FirmwareSource`, so a fleet `--all` never starts on a bad file); the GUI
  picker (refuses via the service's failure event). No flag anywhere.
- **Defense in depth.** `TreehopperFirmwareUpdate.ReflashAsync(Stream)` now parses the records
  **before** rebooting the board, so even a direct caller that hands it non-records (e.g. raw HEX
  bytes) is rejected while the application is still running — the board is never even rebooted.

**Recoverability.** Every refusal happens before the board enters the bootloader, so the worst
case is "nothing happened, fix the file." The pre-reboot parse is the backstop for the low-level
records path.

---

## Decision 13 — Auto-update is a policy layer above the board package; it is never built into `Periphery.Treehopper`

**Decision.** The "decide whether a board is behind and update it" feature does **not** live in
`Periphery.Treehopper` (the board package). The three concerns stay split across three layers:

- **Read the current firmware version** — `Periphery.Treehopper`. The board already exposes
  `TreehopperBoard.Version` / `VersionString` from the USB device-release descriptor (`bcdDevice`),
  read for free at open time. The board's job is to *report the signal*, not to act on it.
- **Perform the reflash** — `Periphery.Treehopper.Firmware` (`TreehopperFirmwareUpdate.ReflashAsync`,
  `TreehopperBootloaderEntry`), the package that owns the HID + bootloader dependency and the
  reboot → bootloader → upload → verify → app orchestration.
- **Decide *when* to flash** (version-gate, fleet policy, sequencing, scheduling) — the consuming
  app or `Periphery.Treehopper.Updater`. This is pure policy + IO orchestration.

A board object that auto-updates *itself* is therefore not offered.

**Why.**
- **It reverses [ADR-0063](../../../adr/0063-bootloader-entry-mode-switch.md) DEC-007.** That refactor
  deliberately extracted the whole firmware-reflash surface out of `Periphery.Treehopper` into
  `Periphery.Treehopper.Firmware` precisely so the board API stops dragging `Periphery.Bootloader` /
  `.Efm8.Usb` / `.Hid` onto board-only consumers (e.g. the kiosk consumer, which only wants the
  LED/SPI handle). Folding auto-update back in re-imposes that dependency tax for no new benefit.
- **It fuses concerns the prime directive keeps apart.** `ReflashAsync` *takes ownership of and
  destroys the board handle* (Decision 7) — the handle dies the instant the device enters the
  bootloader. A `board.UpdateSelfAsync()` would have to dispose the very object it was called on,
  then wait for a *different* re-enumerated handle (new `bcdDevice`). That is a destroy-and-rebuild
  lifecycle that belongs in the imperative shell a layer up, not inside the board's own value/handle
  surface. State (board handle), IO (the flash), and lifecycle/policy (when to flash) are three
  concerns; the board package owns only the first.

**Ergonomic affordance, placed correctly.** If a "is this board behind, and update it" convenience is
wanted, it goes in `Periphery.Treehopper.Updater` (or the consumer's coordinator): read
`board.Version`, compare against the baked target, and call `Periphery.Treehopper.Firmware`. The board
package contributes only the version it already exposes.

**Consumer coordination (the kiosk case).** A board-only consumer that wants to host an update releases
the handle first (stop the render tick, dispose the renderer/proxy so the board closes cleanly), then a
firmware-capable component borrows the released `DeviceInfo` and reflashes, then the consumer rebuilds
over the re-enumerated board. The board package stays board-only on both sides of that handoff — it is
never both the thing being parked and the thing doing the flashing. See the consumer's parking-seam
documentation for the release/borrow/resume sequence, which is tracked in that
consumer's own repository until the feature-spec lands.
