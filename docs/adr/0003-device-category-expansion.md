---
title: "ADR-0003: Device Category Expansion"
status: "Accepted"
status_note: "Tier-2 categories partially superseded by ADR-0051"
date: "2025-07-15"
authors: "@charles8051 (proposal)"
tags: ["architecture", "decision"]
supersedes: ""
superseded_by: ""
depends_on: ["0002-device-tree-topology.md"]
---

# ADR-0003: Device Category Expansion

**Tracks:** New `DeviceCategory` values, validation of existing unmapped categories, category-specific `DeviceInfo` properties  
**Depends on:** ADR-0002 (UsbClassCode for MIDI disambiguation)

---

> **Partial supersession — ADR-0051 (2026-05-31).** The Tier-2 categories `Imaging`,
> `Biometric`, `Sensor`, `SmartCard`, and `Printer` are removed from the `DeviceCategory`
> enum and re-expressed as capability **tags** (`DeviceInfo.Tags`): off Windows they are
> capabilities riding on a generic bus (USB / HID / `iio`), not distinct subsystems, and as
> single-valued categories they produced broken Linux queries and per-platform special-
> casing. The Tier-1 additions in this ADR — `Battery` and `Camera` — and the Camera/Imaging
> split are unaffected. See ADR-0051.

## Context

The `DeviceCategory` enum currently has 15 values. All 14 non-`All` categories have Windows class GUID mappings in `WindowsCategoryMap`, but only 5 are marked "in progress" in the README (USB, Bluetooth, Network, Display/Monitor, HID). The remaining 9 exist in the enum and have GUID wiring but have not been validated end-to-end.

Meanwhile, several device types that users commonly ask about are not represented at all:

- **MIDI controllers** — USB/Bluetooth music devices. Currently discoverable as USB devices but not identifiable by category.
- **Battery / power supply** — laptops, UPS devices. The Windows GUID exists in `DeviceClassGuids.cs` (`Battery`) but no category maps to it.
- **Camera / webcam** — currently lumped under `Imaging` alongside scanners. Very different use cases.
- **Thunderbolt** — docks, eGPUs, displays. No dedicated GUID but identifiable through bus type and device tree.
- **Game controllers** — currently under `Hid`, but gamepads/joysticks are a distinct use case from keyboards and mice.

This ADR proposes a tiered plan: validate what exists, add high-value new categories, and defer or reject low-value ones.

---

## Decision Drivers

- **Real user demand** — prioritize categories that applications actually filter on.
- **Cross-platform feasibility** — every new category must be supportable on Windows, Linux, and macOS, even if providers ship incrementally.
- **Discovery boundary** — new categories must be enumerable through OS device APIs. Categories that require subsystem-specific APIs (MIDI ports, audio endpoints) are out of scope.
- **Incremental** — categories can ship one at a time. The enum is a non-breaking addition.

---

## Proposed Tiers

### Tier 1 — New categories (high value, clear cross-platform path)

#### 1a. `DeviceCategory.Midi`

MIDI controllers (keyboards, drum pads, DJ controllers, MIDI interfaces) are USB or Bluetooth devices with specific class codes.

| Platform | How to identify MIDI hardware | Notes |
|---|---|---|
| Windows | USB class `0x01` (Audio), subclass `0x03` (MIDIStreaming). Also `CompatibleID` matching. No dedicated setup class GUID — MIDI devices appear under `Media` or `USB`. | Requires ADR-0002's `UsbDeviceClass` property for clean filtering. Can also match by `CompatibleID` string in `Properties`. |
| Linux | sysfs `bDeviceClass=0x01`, `bDeviceSubclass=0x03`. Or `/sys/class/sound/midi*`. | |
| macOS | IOKit matching on USB class codes. | |

**Scope:** Physical device discovery only. "Is my MIDI keyboard plugged in?" — yes. "What MIDI ports does it expose?" — no, that's Core MIDI / ALSA sequencer territory.

**Window GUID mapping:** Map to `DeviceClassGuids.Media` filtered by USB class code, or add a synthetic identification based on `CompatibleID` parsing in `WindowsDeviceProvider.ToDeviceInfo()`.

**Depends on:** ADR-0002 (`UsbClassCode`) — shipped. MIDI devices are identified by `UsbClassCode.IsClassAndSubclass(0x01, 0x03)` (Audio class, MIDIStreaming subclass). The `Media` GUID resolves to `Audio` first, then a secondary check on `UsbClassCode` promotes to `Midi`.

```csharp
// With DeviceCategory.Midi:
var midiDevices = await Devices.Enumerate()
    .OfCategory(DeviceCategory.Midi)
    .Connected()
    .ToListAsync();

// Track a specific controller:
var launchpad = watcher.Track(t => t
    .OfCategory(DeviceCategory.Midi)
    .WithUsbId("1235", "0061"),
    name: "Launchpad");
```

#### 1b. `DeviceCategory.Battery`

Battery and power supply devices — laptop batteries, UPS units, USB power delivery status.

| Platform | Enumeration API | Data available |
|---|---|---|
| Windows | `Win32_Battery` (WMI), Battery class GUID `{72631e54-...}` already in `DeviceClassGuids.cs` | Charge %, estimated runtime, charging/discharging, chemistry, designed capacity |
| Linux | `/sys/class/power_supply/*` — each supply has `type`, `status`, `capacity`, `voltage_now` | Same plus fine-grained voltage/current |
| macOS | `IOPSCopyPowerSourcesInfo()` / IOKit `AppleSmartBattery` | Charge %, cycle count, health, temperature |

**Boundary question:** Is reading charge percentage "discovery" or "interaction"? The OS exposes battery state as enumerable system metadata (you don't open a handle or issue a command) — it's closer to reading `DisplayResolution` than sending a USB control transfer. **Recommendation:** Treat charge/status as discoverable metadata. The precedent is `IsConnected` — that's also a live state read from the OS, not a static descriptor.

**New `DeviceInfo` properties:**

```csharp
/// <summary>Battery charge level (0–100), if available.</summary>
public int? BatteryChargePercent { get; init; }

/// <summary>Battery status: Charging, Discharging, Full, etc.</summary>
public BatteryStatus? BatteryStatus { get; init; }

/// <summary>Whether external power (AC/USB-PD) is connected.</summary>
public bool? IsExternalPowerConnected { get; init; }
```

**Event model:** Periodic re-snapshotting for live-updating battery properties is deferred to ADR-0005 (`PropertyChanged` events). For now, consumers re-enumerate to get fresh snapshots.

#### 1c. `DeviceCategory.Camera`

Webcams and video capture devices — split from `Imaging`.

| Platform | Enumeration API |
|---|---|
| Windows | Camera class GUID `{ca3e7ab9-...}` already in `DeviceClassGuids.cs` |
| Linux | `/sys/class/video4linux/video*` + udev |
| macOS | IOKit matching for USB Video Class |

Currently `DeviceCategory.Imaging` maps to `[Image, Camera]` GUIDs. Splitting Camera out gives:
- `DeviceCategory.Camera` → `[DeviceClassGuids.Camera]`
- `DeviceCategory.Imaging` → `[DeviceClassGuids.Image]` (scanners only)

Users filtering for webcams today get scanners in the results. This split fixes that.

### Tier 2 — Existing categories (need validation)

These 9 categories exist in the enum and have Windows GUID mappings but are not validated in README:

| Category | GUIDs mapped | Validation status | Key question |
|---|---|---|---|
| `Storage` | DiskDrive, CdRom, FloppyDisk, TapeDrive | Untested | Does `DriveType` populate correctly? |
| `Audio` | Sound, Media | Untested | Media GUID overlaps with MIDI — needs disambiguation if `Midi` is added |
| `Imaging` | Image, Camera | Untested | Split Camera out? (see Tier 1c) |
| `Biometric` | Biometric | Untested | Uncommon hardware — hard to validate without devices |
| `Sensor` | Sensor | Untested | Windows Sensor API has its own model. Do PnP sensors map well? |
| `Ports` | Ports, MultiportSerial | Untested | `PortName` property (ADR-0002) is the key enrichment |
| `SmartCard` | SmartCardReader | Untested | Niche but straightforward |
| `Printer` | Printer, PnpPrinters, PrintQueue | Untested | PrintQueue devices are software — include or exclude? |
| `Display` | Display | Untested separately from Monitor | Display = GPU adapter, Monitor = screen. Distinction clear? |

**Action:** Systematic validation pass. For each category:
1. Run `Devices.Enumerate().OfCategory(cat).ToListAsync()` on Windows
2. Verify returned devices are correctly classified
3. Spot-check typed properties (`DriveType` for Storage, etc.)
4. Update README status from "Planned" to "In progress" / "Supported"

### Tier 3 — Candidates for future consideration

| Category | Assessment | Recommendation |
|---|---|---|
| **Thunderbolt** | No dedicated Windows GUID. Shows as PCI + USB. Identifiable via device tree (ADR-0002) + bus type. Linux has `thunderbolt` sysfs subsystem. | Defer — discoverable via tree topology once ADR-0002 ships. Add category if demand warrants. |
| **Game Controller** | Currently under `Hid`. Windows has no separate GUID — gamepads are HID devices. | Defer — distinguishable via HID usage page (game controller = usage page `0x05`). Needs USB class enrichment from ADR-0002. |
| **Modem** | GUID exists. Relevant for IoT/embedded cellular. | Defer — low demand for desktop apps. Add if IoT scenarios emerge. |
| **Firmware** | GUID exists. UEFI/BIOS update targets. | Defer — highly platform-specific, niche use case. |
| **Infrared** | GUID exists. IR receivers/blasters. | Defer — largely legacy. |
| **Processor** | GUID exists. CPU sockets/cores. | Reject — system info, not peripheral discovery. Use `Environment.ProcessorCount` or `System.Management` directly. |

### Tier 4 — Out of scope

| Category | Why |
|---|---|
| **MIDI ports** (In/Out endpoints) | Requires Core MIDI (macOS), ALSA sequencer (Linux), Windows Multimedia API. These are audio-subsystem constructs, not PnP devices. A different data model (direction, channel count, connections). Belongs in a dedicated MIDI library. |
| **Audio endpoints** (speakers, microphone jacks) | Windows Audio Endpoint API (`IMMDeviceEnumerator`), PulseAudio/PipeWire (Linux), Core Audio (macOS). Subsystem-specific, not PnP. |

**Note:** Virtual serial ports, virtual NICs, print queues, and other software devices are **included** by default. They have PnP device entries and are discoverable through standard enumeration. Filtering is a consumer concern via `BusType.Software` or `.PhysicalOnly()`.

---

## Cross-Cutting Concerns

### Virtual device filtering

Several categories include virtual/software devices (virtual NICs, print queues, software audio devices). Rather than filtering these per-category, a cross-cutting approach would be cleaner:

```csharp
// Option A: BusType-based (already available)
var physicalOnly = await Devices.Enumerate()
    .OfCategory(DeviceCategory.Network)
    .Where(d => d.BusType != BusType.Software)
    .ToListAsync();

// Option B: Dedicated filter (future)
var physicalOnly = await Devices.Enumerate()
    .OfCategory(DeviceCategory.Network)
    .PhysicalOnly()  // excludes BusType.Software
    .ToListAsync();
```

No new enum value needed — `BusType.Software` already exists. A convenience method (`.PhysicalOnly()`) could be added if the pattern is common.

### MIDI identification via UsbClassCode (ADR-0002)

MIDI devices are identified using the `UsbClassCode` property shipped in ADR-0002. On Windows, MIDI devices appear under the `Media` GUID (which maps to `Audio`). The category resolver performs a secondary check:

1. GUID resolution: `Media` → `Audio`
2. If `UsbClassCode.IsClassAndSubclass(0x01, 0x03)` → promote to `Midi`

This is the only category requiring post-GUID reclassification.

---

## Impact on Existing Types

| Type | Change |
|---|---|
| `DeviceCategory` | Add `Midi`, `Battery`, `Camera` (3 new values) |
| `WindowsCategoryMap` | Add Battery GUID mapping. Move Camera GUID from Imaging to Camera (**breaking:** `Imaging` no longer returns webcams). MIDI: secondary `UsbClassCode` check after `Media` GUID resolution. |
| `DeviceClassGuids` | No change — Battery and Camera GUIDs already exist |
| `DeviceInfo` | Add `BatteryChargePercent`, `BatteryStatus`, `IsExternalPowerConnected` |
| `BatteryStatus` | New enum: Unknown, Charging, Discharging, Full, NotCharging |
| `DeviceFilter` | Add `WithBatteryStatus()` convenience filter |
| README | Update category table with new entries and validation status |
| Tests | Category mapping tests, new `DeviceInfo` property defaults, new filter tests |

No breaking changes to existing API except `Imaging` category no longer including Camera GUID (pre-1.0, no stability guarantee).

---

## Decisions (resolved from open questions)

1. **Battery charge level is "discovery".** The OS exposes it as queryable metadata without device I/O, consistent with `IsConnected` and `DisplayResolution`. Properties: `BatteryChargePercent`, `BatteryStatus`, `IsExternalPowerConnected`. Periodic re-snapshot events deferred to ADR-0005.
2. **MIDI uses ADR-0002's `UsbClassCode`.** ADR-0002 shipped. MIDI identification uses `UsbClassCode.IsClassAndSubclass(0x01, 0x03)`. No `CompatibleID` fallback needed.
3. **Camera/Imaging split — break it.** Pre-1.0 library, no stability guarantee. Camera GUID moves from `Imaging` to new `Camera` category. Confirmed by validation: webcam uses Camera GUID, not Image GUID.
4. **Include all virtual devices.** Print queues, virtual serial ports, virtual NICs — all have PnP device entries and are returned by default. Virtual vs. physical is a filtering concern via `BusType.Software`. Virtual serial ports moved from out-of-scope to included.
5. **`SerialPortName` struct shipped in ADR-0002.** Resolved.
6. **Validation pass is independent.** Battery is fully independent (own GUID). Camera split confirmed by validation (webcam uses Camera GUID). MIDI confirmed (keyboard uses Media GUID). Other 7 categories can be validated in parallel.

---

## References

- `Periphery/DeviceCategory.cs` — Current enum (15 values)
- `Periphery/Windows/WindowsCategoryMap.cs` — GUID ↔ category mappings
- `Periphery/Windows/DeviceClassGuids.cs` — All known Windows class GUIDs (Battery, Camera already present)
- `docs/adr/0002-device-tree-topology.md` — USB class codes, `PortName` property
- `docs/ARCHITECTURE.md` — "Discovery only" principle, category mapping tables
- [USB Audio Class spec](https://www.usb.org/document-library/usb-audio-devices-rev-30-and-adopters-agreement) — MIDI subclass `0x03` definition
