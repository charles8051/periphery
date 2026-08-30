---
title: "ADR-0063: Bootloader entry — app-to-bootloader mode switching for device-specific flashers"
status: "Accepted"
date: "2026-06-18"
authors: "@charles8051"
tags: ["architecture", "decision", "firmware", "bootloader", "flashing", "mode-switch", "treehopper", "efm8", "dfu", "functional-core", "flashanything"]
supersedes: ""
superseded_by: ""
---

# ADR-0063: Bootloader entry — app-to-bootloader mode switching for device-specific flashers

## Status

> **Implemented** in all four slices of the "Implementation plan" below (on
> `feat/bootloader-entry-treehopper`): the `IBootloaderEntry` seam + shared
> orchestration, EFM8 as an `IFirmwareProgrammer`, FlashAnything mode-awareness, and
> the branded Treehopper Flasher composition — all behavior-preserving where promised,
> with the wait/correlation pure core and the app-mode flash unit-tested.
>
> Number `0063` is provisional until merge (the next free number after ADR-0062),
> per this repo's "assign the number at merge" convention.
>
> **Builds on — does not supersede — [ADR-0061](0061-firmware-flashing-platform.md)
> (the firmware-flashing platform), [ADR-0052](0052-periphery-treehopper-pure-core.md)
> (functional core / imperative shell), and [ADR-0024](0024-extension-package-pattern.md)
> (extension package pattern).** It adds one seam and one shared orchestration to the
> platform; it changes no existing flasher contract.

## Context

[ADR-0061](0061-firmware-flashing-platform.md) builds the "flash anything" platform around devices
that are **already in bootloader mode**: discovery matches a bootloader signature (e.g. STM32 DFU
`0483:DF11`), a `BootloaderRegistry` resolves an `IBootloaderProvider`, and an `IFirmwareProgrammer`
writes/verifies/leaves. The CLI and Avalonia GUI (`Periphery.FlashAnything`) are thin MVU front-ends
over a shared `FlashAnythingService`.

But a large class of devices spend their life running **application firmware** and must be told —
at the application protocol level — to enter their bootloader before any flasher can see them. The
device then **re-enumerates** as a (usually different) USB/serial identity, gets flashed, and resets
back into the new application. This is not a corner case; it is the *typical* pattern:

| Device | Application-level "enter bootloader" | Re-enumerates as |
|---|---|---|
| **Treehopper** | HID config command `0x0D` (`Command.EnterBootloader` / `TreehopperBoard.RebootIntoBootloaderAsync`) | EFM8 USB-HID bootloader `0x10C4:0xEAC9` |
| Arduino (AVR) | 1200 bps "touch" on the serial port | AVR/STK500 serial bootloader |
| ESP32/8266 | `RTS`/`DTR` auto-reset sequence | ESP ROM serial bootloader |
| STM32 app w/ USB DFU | a `DFU_DETACH` from the app's DFU interface | STM32 DFU `0483:DF11` |

Periphery already has a worked instance of this for Treehopper:
[`TreehopperFirmwareUpdate.ReflashAsync`](../../src/Periphery.Treehopper.Firmware/TreehopperFirmwareUpdate.cs)
hand-rolls the whole sequence —

1. `board.RebootIntoBootloaderAsync()` — enter the bootloader (drops the app handle),
2. `PollForDeviceAsync(0x10C4, 0xEAC9, …)` — wait for the EFM8 bootloader to enumerate,
3. `VerifyIsBootloader(info)` — a **safety gate** refusing to write to anything but the expected
   bootloader VID/PID,
4. `Efm8BootloaderUploader.UploadAsync(transport, image, …)` — flash,
5. `PollForDeviceAsync(app VID/PID, …)` — optionally wait for the application to return.

Steps 1-3 and 5 are **device-specific glue around a reusable flasher**, and *every* new app-mode
device (Arduino, ESP, …) would re-write the same shape: reboot, wait, gate, flash, wait. That is
the repetition this ADR exists to remove. The problem statement, in the requester's words: *reuse
the FlashAnything UI and core as a base for a device-specific flasher with the "reboot into
bootloader" command included, without repeating a bunch of code for every new application.*

## Decision

Model an app-mode flash as **two stages over a shared spine**, where only the first stage is
device-specific:

```
[application firmware] --device-specific--> [bootloader] --reusable--> flashed --> [new application]
                          IBootloaderEntry                IFirmwareProgrammer
   \________________________ shared orchestration (in Periphery.FlashAnything) ________________________/
        discover (app|boot) -> enter -> wait-for-expected-bootloader (+ safety gate) -> flash -> wait-for-app
```

### DEC-001 — Separate the device-specific "entry" from the reusable "flash"

A device-specific flasher is **not** a new flasher; it is a small *mode switch* composed in front of
an existing flasher. We do **not** extend `IBootloaderProvider` to also reboot application devices
(that would conflate "speak a bootloader protocol" with "wake a specific application into its
bootloader" and couple every entry to one flasher). Instead, entries and flashers are independent and
composed by the orchestrator. One flasher (`Efm8.Usb`, `Stm32.Usb`, …) then serves **every** device
that re-enumerates as that bootloader.

### DEC-002 — `IBootloaderEntry`, the one new seam (in `Periphery.Bootloader`)

```csharp
/// <summary>
/// Puts a device that is running its application firmware into its bootloader, so a flasher can
/// take over. The device-specific half of flashing an app-mode device; the reusable half is the
/// IBootloaderProvider for whatever bootloader it becomes. One per (application family, transport):
/// Treehopper HID reboot, Arduino 1200bps touch, ESP RTS/DTR, STM32 app DFU-detach, ...
/// </summary>
public interface IBootloaderEntry
{
    /// <summary>The application this enters the bootloader for (e.g. "Treehopper").</summary>
    string Name { get; }

    /// <summary>True if this device, in application mode, is one this entry can reboot.</summary>
    bool CanEnter(DeviceInfo applicationDevice);

    /// <summary>
    /// A filter matching the bootloader the device re-enumerates as, so the orchestrator can wait
    /// for + recognize it, and so it can refuse to write to anything else (the safety gate). E.g.
    /// Treehopper -> EFM8 HID 0x10C4:0xEAC9; STM32 app -> DFU 0x0483:0xDF11.
    /// </summary>
    DeviceFilter ExpectedBootloader { get; }

    /// <summary>
    /// Command the device into its bootloader. After this returns the device drops off the bus and
    /// reappears matching <see cref="ExpectedBootloader"/>; the orchestrator owns the wait + the
    /// correlation. Implementations open the application device with its own SDK, send the wake
    /// command, and dispose — they do not poll, gate, or flash.
    /// </summary>
    Task EnterAsync(DeviceInfo applicationDevice, CancellationToken ct);
}
```

For Treehopper, the whole device-specific addition is ~15 lines wrapping the command that already
exists:

```csharp
public sealed class TreehopperBootloaderEntry : IBootloaderEntry
{
    public string Name => "Treehopper";
    public bool CanEnter(DeviceInfo d) => d.VendorId == TreehopperBoard.Vid && d.ProductId == TreehopperBoard.Pid;
    public DeviceFilter ExpectedBootloader => new DeviceFilter().WithUsbId("10C4", "EAC9"); // EFM8 HID bootloader
    public async Task EnterAsync(DeviceInfo d, CancellationToken ct)
    {
        await using var board = await TreehopperBoard.OpenAsync(d, ct);
        await board.RebootIntoBootloaderAsync(ct);
    }
}
```

### DEC-003 — One shared orchestration (in `Periphery.FlashAnything`)

The body of `TreehopperFirmwareUpdate.ReflashAsync` is generalized once into the FlashAnything shell:
**enter → wait-for-`ExpectedBootloader` → safety-gate → flash → optionally wait-for-application.** It
is **watcher-driven**, not polling: it reuses the `DeviceWatcher` / `MultiDeviceTracker` the service
already runs for discovery (push, not the `PollForDeviceAsync` busy-loop). Per
[ADR-0052](0052-periphery-treehopper-pure-core.md), the **wait/correlation is a pure state machine**
advanced by device-appeared/disappeared events plus a shell-owned timeout clock; the shell owns the
watcher, the SDK calls in `EnterAsync`, and the flasher handle. No new device ever re-implements this.

### DEC-004 — Targets are mode-aware in the MVU core (UI reuse, not fork)

A discovered target gains a `DeviceMode { Application, Bootloader }`. `FlashTargetView` surfaces it
(and the entry's `Name`) so the existing CLI/GUI render *"Treehopper (application) — reboots to
flash"* with no structural change. Discovery widens to also match application-mode devices that an
`IBootloaderEntry` handles. Flashing an `Application` target runs the orchestration; the reducer
folds two new lifecycle states (`Entering`, `WaitingForBootloader`) ahead of the existing
`FlashStarted/Progressed/Finished`. **Autoflash composes for free**: arm a family, an application
device appears, it is woken and flashed.

### DEC-005 — Correlation + safety policy, solved once

Correlating the application device with the bootloader that reappears is the one genuinely hard part,
and it lives in the shared orchestration so no device re-solves it:

- **Serial present** (the bootloader exposes the same unique id, e.g. an STM32 app whose DFU keeps
  the 96-bit UID): correlate by serial (`BySerial`) — exact, parallel-safe.
- **Stable USB port** (no serial survives, but the physical port does — the EFM8 HID bootloader is the
  shared id `0x10C4:0xEAC9` for *every* EFM8 device, yet a board does **not** change USB port when it
  resets): correlate by **topology** (`ByLocationPath`) — match the bootloader whose
  `DeviceInfo.LocationPath` equals the application device's. **Windows-hardware-verified:** an EFM8 app
  and the bootloader it re-enumerates as report an **identical** USB-node `LocationPath`
  (`PCIROOT(20)#…#USB(6)#USB(3)` on both). **One shell subtlety proven on hardware:** the flasher opens
  the bootloader as its **HID function node** (`HID\VID_10C4&PID_EAC9\…`), whose own
  `DEVPKEY_Device_LocationPaths` is **empty** — so a naive read fell back to the instance id and
  correlation timed out on every board. The port lives on the HID node's **USB-node parent** and is
  identical across the reset, so `WindowsDeviceProvider` now walks `DEVPKEY_Device_Parent` up to the
  nearest ancestor with a port (`ResolveLocationPath`); the pure correlation core is unchanged. Linux
  (`syspath`) and macOS (`locationPath`) populate the same field, but port-invariance across the mode
  switch is **not yet hardware-verified there** (and whether their HID nodes carry the port or need the
  same parent-walk is likewise unverified) — the
  orchestrator therefore fails **loudly** (an explicit `BootloaderEntryException`) if a platform exposes
  no port, rather than silently mis-correlating. This correlation is exact and parallel-safe exactly
  like `BySerial`: each concurrent wait matches its own port, so it removes the *software* obstacle to
  **no-serial families flashing concurrently**, which is now the Treehopper/EFM8 default. Concurrent
  flashing is **hardware-verified safe** (see the note below); `#220`'s physical-bus hypothesis was
  disproven, so the correlation collapse was the whole cause.
- **No distinguisher at all** (neither serial nor a stable port survives): correlate by **debounce**
  (`FirstAppearance`) — "the bootloader matching `ExpectedBootloader` that appeared within the window
  after this `EnterAsync`," processed **one device at a time**. Because the re-enumerated bootloader
  is indistinguishable, two concurrent waits would both accept the first-appearing bootloader
  (collapsing onto one board — see the correlation-collapse note below), so this mode also **serializes**
  the family (the same shared-bootloader-id hazard the autoflash spec records under per-family concurrency). `FirstAppearance` is the safe fallback, no longer the EFM8 path.
- **Safety gate:** the orchestrator refuses to open/flash any device that does not match
  `ExpectedBootloader` — the generalization of Treehopper's per-device `VerifyIsBootloader`. The
  device-specific code never gets to flash the wrong thing.

> **Correlation-collapse note (the *software* root cause `ByLocationPath` fixes).** `FirstAppearance`
> means "any bootloader not in the pre-arm baseline." When two app-mode flashes run concurrently they
> share the service's one `MultiDeviceTracker` and both arm with an empty baseline, so the pure core's
> `Correlates()` accepts the **same first-appearing** EAC9 for *both* waits — both then flash that one
> physical board (interleaved HID writes → corruption) while the other board is never flashed. This is
> a genuine software defect, and topology correlation removes it at the root: each wait matches its own
> port, so under `ByLocationPath` two concurrent flashes address two different boards.
>
> **`#220`'s physical-collision hypothesis was disproven.** `#220` saw a stagger dose-response on real
> boards (one board fine; two overlapping fail; separating them in time fixes it) and attributed it to a
> **physical current-collision on the shared USB bus**. The investigation ruled that out:
> - **Two separate *processes*, each flashing one board concurrently → zero corruption.** That isolates
>   the fault to **in-process shared state**, not anything physical on the bus.
> - **Boards on separate USB controllers + a powered hub → still failed identically.** So it was never
>   shared-bus power/current.
>
> The sole cause was the software correlation collapse: two concurrent `FirstAppearance` waits sharing
> the service's one `MultiDeviceTracker` both grab the first-appearing bootloader → interleaved writes to
> one board. `ByLocationPath` fixes it at the root — each wait matches its own port. The
> **hardware verification** (2026-07-31, two Treehopper boards on distinct hub ports USB(2)/USB(3))
> confirms concurrency is safe:
> - Each EFM8 bootloader resolved to its **own** port via the parent USB node (`…#USB(6)#USB(2)` and
>   `…#USB(6)#USB(3)`), so `ByLocationPath` correlated each board to its own app device.
> - The two uploads ran with **genuinely overlapping windows** (flash1 33.055→33.778s, flash2
>   33.151→33.871s — simultaneous, versus `#220`'s serialize where flash2 began 255 ms *after* flash1
>   finished), each completed **120/120 records with zero corruption**.
>
> Accordingly:
> - `ByLocationPath` is the *correctness* fix (right board, every time). It is always on — it never
>   mis-correlates.
> - **Concurrent flashing is the default.** `TreehopperFlasher.CreateService` defaults
>   `allowConcurrentEfm8Flash: true` (full `maxFlashConcurrency` pool). Passing `false` is the
>   **opt-out** that forces one-board-at-a-time serialization (a conservative fallback / debugging aid).
> - **Cross-platform:** hardware verification was on Windows; Linux (`syspath`) / macOS (`locationPath`)
>   port-invariance is unverified. Concurrent-by-default is still acceptable there because `ByLocationPath`
>   is an **exact** match — a wrong or absent port never mis-flashes, it just fails to correlate and the
>   wait times out with a clean "did not re-enumerate" error. The failure mode is **fail-safe** (a visible
>   timeout), never corruption or cross-correlation.
> - `#220`'s serialization is retained unchanged as the `FirstAppearance` fallback for a family with
>   neither serial nor stable port.

### DEC-006 — A device-specific flasher is a thin composition

A "Treehopper Flasher" is the *same* `Periphery.FlashAnything` app with a curated registry
(`{ TreehopperBootloaderEntry, Efm8UsbBootloaderProvider }`) and branding. It reuses the MVU core,
the service + orchestration, the CLI/GUI, and the EFM8 flasher. A new device adds **one
`IBootloaderEntry`** and reuses (or, once, authors) the `IBootloaderProvider` for its bootloader.

> **Extended** (post-merge) with a **verb seam**, because "curated registry + branding" turned out to
> be one axis short. A device family has maintenance commands that are *not* flashes: `reboot`
> (2026-07-27, the `0x0C` firmware reset used as a per-board health probe) and `rename` (2026-07-27,
> which writes a board's device name). Both speak the Treehopper **application** protocol to a board that
> must stay in application mode, where the flasher's model — enter the bootloader, write an image,
> leave — does not apply, so neither can be a `FlashAnythingService` operation; and both are
> device-specific, so neither can live in the composition-agnostic CLI toolkit.
>
> `reboot` landed first by intercepting `args[0]` ahead of `Cli.RunAsync` in the front-end's
> `Program.cs`. That works for exactly one verb and costs the tool its own `--help` — the verb was
> undiscoverable from `treehopper-flash --help`. Generalizing it beat both repeating the interception
> per verb and forking the shared parser, which would have undone this decision outright.
>
> So `Periphery.FlashAnything.Cli` gained **`CliVerb`**: a front-end passes verbs to
> `Cli.RunAsync`, and the shared parser only *routes* — it matches the first token, hands over every
> argument after it verbatim, and splices the verb's usage into `--help`. The verb owns its parsing,
> its output, and its exit code (from the now-public `ExitCodes`, so a tool has one exit-code contract).
> Built-in verbs win a name collision. `reboot` moved onto the seam unchanged; `rename` splits per
> [ADR-0052](0052-periphery-treehopper-pure-core.md) with its pure core (`BoardRename` — parse,
> validate, select) and shell (`BoardRenamer` — open, write, reboot) in `Periphery.Treehopper.Flasher`
> beside the curated registry.

### DEC-007 — Placement (per the ADR-0061 taxonomy)

- `IBootloaderEntry` → `Periphery.Bootloader` (the flashing contract package, beside
  `IBootloaderProvider`).
- The orchestration + the `DeviceMode` MVU surface → `Periphery.FlashAnything`.
- `TreehopperBootloaderEntry` → a small device package (e.g. `Periphery.Bootloader.Treehopper`)
  depending on `Periphery.Treehopper`; or co-located with the Treehopper-flasher composition. It is
  an *entry*, not a `Bootloader.{family}.{transport}` *flasher*, so it does not take that name.

> **Realized** (post-merge, `refactor/treehopper-firmware-package`) as
> **`Periphery.Treehopper.Firmware`** rather than `Periphery.Bootloader.Treehopper`. The package holds
> the whole Treehopper firmware-reflash surface — `TreehopperBootloaderEntry` plus the
> `TreehopperFirmwareUpdate` convenience wrapper and its option/result types — so it sits in the
> `Periphery.Treehopper.*` product family rather than the bootloader taxonomy. The motivating win: the
> board API (`Periphery.Treehopper`) no longer drags `Periphery.Bootloader` / `.Efm8.Usb` / `.Hid` onto
> board-only consumers; only firmware-capable consumers
> (`Periphery.Treehopper.Control`, `Periphery.Treehopper.Flasher`) reference the new package.

## Consequences

**Positive**
- **One small class per new device.** UI, MVU core, orchestration, correlation, safety gate, and the
  flasher are all shared; only the wake command (and the rare new bootloader flasher) is written.
- The hard re-enumeration/correlation problem is solved once, watcher-driven and pure-core testable.
- Autoflash, parallel flashing, verify, progress, and the firmware-image layer all apply unchanged.
- `TreehopperFirmwareUpdate` stops being bespoke — it becomes either a thin convenience wrapper over
  the orchestration or is superseded by it.

**Negative / risks**
- **Prerequisite:** the EFM8 uploader must wear `IFirmwareProgrammer` (the
  `Periphery.Efm8Bootloader` → `Periphery.Bootloader.Efm8.Usb` restructure already on the backlog)
  before a Treehopper flasher can route through FlashAnything end-to-end. The foundational refactor
  (below) reuses the existing `Efm8BootloaderUploader` and does **not** block on this.
- Mode-aware discovery widens the watcher filter; the reducer gains states. Contained, but it is new
  surface on the hot discovery path.
- No-serial correlation is now **exact** for families with a stable USB port (`ByLocationPath` — the
  EFM8/Treehopper case; Windows-hardware-verified, Linux/macOS populate but unverified). This unlocks
  concurrent flashing, which is now the **default** for Treehopper — hardware-verified safe, with `#220`'s
  physical bus-collision hypothesis **disproven** (two-process and separate-controller evidence). On an
  unverified platform the exact match is **fail-safe**: a wrong/absent port times out rather than
  mis-flashing. `allowConcurrentEfm8Flash: false` is the opt-out that forces serialization; the heuristic
  debounce + serialize (`FirstAppearance`) also remains the mode for a family exposing neither serial nor
  stable port. Bounded by DEC-005.

**Format note.** EFM8 firmware is a **packaged blob** (hex2boot boot records — Kind 2 in the
firmware image-format taxonomy under `docs/feature-specs/firmware-flashing/image-formats/`), not a
memory image. The EFM8 `IFirmwareProgrammer` therefore accepts boot-record bytes, not coalesced
`FirmwareSegment`s; the contract already allows per-family accepted formats.

## Alternatives considered

1. **Extend `IBootloaderProvider` to also reboot application devices.** Rejected (DEC-001): conflates
   two responsibilities and couples each entry to one flasher, losing the "one flasher serves many
   devices" reuse.
2. **One monolithic `IDeviceFlasher` bundling entry + flash.** Rejected: the flasher is the *reusable*
   half; bundling it with the device-specific entry duplicates the flasher per device.
3. **Keep polling (`PollForDeviceAsync`).** Rejected for the shared path: the service already runs a
   watcher; pushing off it is cheaper and races less than a busy-loop. (Polling stays a legitimate
   fallback inside an `EnterAsync` that has no watcher.)
4. **Leave each device to hand-roll its flow (status quo).** Rejected: that is the repetition the
   requester named.

## Implementation plan (slices)

1. **Foundational refactor (the spawned task).** Add `IBootloaderEntry` to `Periphery.Bootloader`; a
   shared, watcher-driven `enter → wait → gate → flash → wait-for-app` orchestration (pure
   wait/correlation core + shell); `TreehopperBootloaderEntry`; and refactor
   `TreehopperFirmwareUpdate.ReflashAsync` to compose them, reusing the existing
   `Efm8BootloaderUploader`. **Behavior-preserving; tests stay green.** Does *not* touch the
   FlashAnything MVU or restructure EFM8.
2. **EFM8 → `IFirmwareProgrammer`** (`Periphery.Bootloader.Efm8.Usb`).
3. **FlashAnything mode-awareness** — `DeviceMode`, the entry registry, the orchestration in
   `FlashAnythingService`, the reducer `Entering`/`WaitingForBootloader` states (DEC-004).
4. **Treehopper-flasher composition app + UI** (DEC-006), and supersede the bespoke
   `TreehopperFirmwareUpdate` flow.

## Relationships

- **Builds on** [ADR-0061](0061-firmware-flashing-platform.md) (platform + contract),
  [ADR-0052](0052-periphery-treehopper-pure-core.md) (functional core / shell),
  [ADR-0024](0024-extension-package-pattern.md) (extension packages + star topology).
- **Cites** the autoflash spec (`docs/feature-specs/firmware-flashing/autoflash/spec.md`, the
  shared-bootloader-id hazard) and the image-format taxonomy
  (`docs/feature-specs/firmware-flashing/image-formats/`).
- **Reference precedent:** [`TreehopperFirmwareUpdate`](../../src/Periphery.Treehopper.Firmware/TreehopperFirmwareUpdate.cs)
  and [`TreehopperBoard.RebootIntoBootloaderAsync`](../../src/Periphery.Treehopper/TreehopperBoard.cs).
