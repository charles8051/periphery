---
title: "ADR-0021: MIDI Device Support — Enumeration Yes, I/O Deferred"
status: "Proposed"
status_note: "Not implemented - there is no `Periphery.Midi` package."
date: "2026-07-14"
authors: "@charles8051 (design)"
tags: ["architecture", "decision", "midi", "enumeration", "extension", "i/o", "boundary"]
supersedes: ""
superseded_by: ""
---

# ADR-0021: MIDI Device Support — Enumeration Yes, I/O Deferred

## Context

MIDI (Musical Instrument Digital Interface) devices — controllers, synthesizers, audio
interfaces, DAW control surfaces — are hardware peripherals that Periphery discovers
today under `DeviceCategory.Unknown` or `DeviceCategory.Audio` depending on how the OS
classifies them. A proposal was raised to add explicit MIDI enumeration support and
potentially MIDI I/O to the library.

### MIDI enumeration is straightforward

MIDI interfaces are enumerable through standard OS device tree APIs on all three platforms:

| Platform | Enumeration source |
|---|---|
| Windows | SetupAPI class GUID `{4d36e96c-e325-11ce-bfc1-08002be10318}` (Media) and `GUID_DEVINTERFACE_MIDI_INPUT` / `GUID_DEVINTERFACE_MIDI_OUTPUT` |
| Linux | ALSA `snd_seq` client enumeration; raw devices under `/dev/snd/midi*`; udev `SUBSYSTEM=="sound"` |
| macOS | CoreMIDI `MIDIGetNumberOfDevices` / `MIDIGetDevice`; IOKit `IOServiceMatching("AppleMIDIDriver")` |

Adding `DeviceCategory.Midi` to the core library and mapping the relevant class GUIDs
is the same pattern as any other device category — a few lines in `WindowsCategoryMap`,
`LinuxCategoryMap`, and `MacOSCategoryMap`. `DeviceInfo.Name`, `Manufacturer`, `VendorId`,
`ProductId`, and `IsConnected` all populate normally.

### MIDI I/O is a different problem

Where MIDI diverges sharply from HID (ADR-0020) and USB (ADR-0019) is in the I/O model.
The characteristics that make HID I/O simple — fixed-size reports, inbox OS driver,
report in / report out — do not apply to MIDI.

#### 1. Timestamping is first-class, not optional

A MIDI message without a precise timestamp is musically meaningless. MIDI note events,
control changes, and clock signals must be timestamped to sub-millisecond resolution to
maintain timing coherence across devices. This is not a nice-to-have — it is the primary
correctness requirement of any MIDI I/O library.

All three platform APIs treat timestamps as a first-class parameter:
- Windows `WinMM`: `midiInProc` callback receives `dwParam2` (timestamp in ms from
  `midiInStart`). Low-resolution and subject to multimedia timer jitter.
- Windows MIDI 2.0 (`Windows.Devices.Midi2`): UMP (Universal MIDI Packet) timestamps
  in 100-nanosecond units from a high-resolution clock.
- Linux `ALSA seq`: `snd_seq_real_time_t` (seconds + nanoseconds) on every event.
- macOS `CoreMIDI`: `MIDIPacket.timeStamp` as `MIDITimeStamp` (host time ticks via
  `mach_absolute_time`), requiring conversion via `AudioConvertHostTimeToNanos`.

Correct cross-platform timestamping requires platform-specific clock normalisation that
is substantially more complex than anything in Periphery's current codebase.

#### 2. Variable-length streaming protocol

HID reports are fixed-size. MIDI is a variable-length streaming protocol. The wire format
is a sequence of status bytes and data bytes: some messages are 1 byte, some 3 bytes,
SysEx messages are arbitrarily long (terminated by `0xF7`). A MIDI I/O layer must:
- Accumulate bytes until a complete message is formed (running status complicates this).
- Handle SysEx buffers that can be hundreds or thousands of bytes.
- Dispatch parsed `MidiMessage` values, not raw byte buffers.

This is a non-trivial parser with edge cases around running status, real-time messages
interspersed in SysEx, and MIDI 2.0 UMP framing.

#### 3. Platform API fragmentation

The three platform APIs are not analogous abstractions over the same underlying model —
they are genuinely different architectures:

| Aspect | Windows WinMM | Windows MIDI 2.0 | Linux ALSA | macOS CoreMIDI |
|---|---|---|---|---|
| Message model | Raw bytes via callback | UMP packets (MIDI 2.0) | `snd_seq_event_t` struct | `MIDIPacketList` |
| Timestamp resolution | Milliseconds | 100 ns | Nanoseconds | Host time ticks |
| Virtual ports | No | Yes | Yes (via `snd_seq`) | Yes (network-transparent) |
| Multi-client | Exclusive | Yes | Yes | Yes |
| Routing | No | Yes | Via `aconnect` | Built-in to CoreMIDI |

macOS CoreMIDI in particular has a fundamentally different model: it exposes MIDI as a
network-transparent routing graph. `MIDIEndpointRef`, `MIDIPortRef`, and
`MIDIClientRef` are reference-counted opaque handles in a system-wide MIDI router.
There is no analogous concept on Windows WinMM. A correct abstraction must either expose
the lowest common denominator (losing CoreMIDI's routing model entirely) or expose
platform-specific extensions, both of which are significant design decisions.

#### 4. MIDI 2.0 transition

The industry is mid-transition from MIDI 1.0 (31,250 baud serial) to MIDI 2.0 (UMP
framing, higher resolution, bidirectional negotiation). Windows 11 22H2+ ships a MIDI 2.0
class driver. Linux ALSA is adding UMP support. CoreMIDI supports MIDI 2.0 on macOS 13+.
A new MIDI I/O library has to decide whether to target MIDI 1.0, MIDI 2.0, or both. That
is a scope decision that belongs in a dedicated `Periphery.Midi` package with its own ADR.

### MIDI ports vs. physical devices

On all three platforms the OS enumerates MIDI **ports** (openable endpoints), not
physical devices, as the addressable unit:

| Platform | What is enumerated | Port / device relationship |
|---|---|---|
| Windows SetupAPI | `GUID_DEVINTERFACE_MIDI_INPUT` and `GUID_DEVINTERFACE_MIDI_OUTPUT` are **separate device interfaces** | A bidirectional device produces two `DeviceInfo` entries; a 4-port interface produces up to 8 |
| Linux ALSA seq | Clients with named ports (`snd_seq_client_info_t` + `snd_seq_port_info_t`) | Ports are the addressable unit; the client is the parent concept |
| macOS CoreMIDI | `MIDIEndpointRef` within a three-level graph: `MIDIDeviceRef` → `MIDIEntityRef` → `MIDIEndpointRef` | Endpoints (ports) are what `MIDIInputPortCreate` / `MIDISend` operate on |

This means `DeviceCategory.Midi` entries in the core enumeration represent **ports**,
not physical devices. A single USB MIDI controller typically produces two entries —
one input port and one output port. A multi-port MIDI interface produces one entry per
port per direction.

Virtual ports (ALSA seq virtual clients, CoreMIDI virtual endpoints, Windows MIDI 2.0
loop-back) have no physical parent device at all. They appear as `DeviceCategory.Midi`
entries with no `VendorId` / `ProductId`. `MidiPortDirection` (see Decision below) is
the primary discriminator for these entries.

### Extension properties and `Periphery.Midi` enrichment

C# 14 (shipped with .NET 10) introduces **extension members**, which allow extension
properties — not just extension methods — to be added to existing types via `extension`
blocks in a static class. These properties appear in IntelliSense and are callable with
normal property syntax, but are only available when the declaring namespace is imported
with `using`.

This enables `Periphery.Midi` to add MIDI-specific properties to `DeviceInfo` without
any changes to the core library, and without polluting `DeviceCategory` or `DeviceInfo`
with MIDI-internal concepts.

---

## Decision

### Core library — add `DeviceCategory.Midi`

Add `DeviceCategory.Midi` to the `DeviceCategory` enum and update all three platform
category maps. This follows the standard pattern for new device categories (ARCHITECTURE.md
section: "Adding a new device category"):

- `WindowsCategoryMap` — map SetupAPI class GUID `{4d36e96c-e325-11ce-bfc1-08002be10318}`
  and the MIDI interface GUIDs.
- `LinuxCategoryMap` — map `SUBSYSTEM=sound` + `ID_TYPE=audio` with MIDI-specific filtering
  on the `snd_rawmidi` and `snd_seq` device nodes.
- `MacOSCategoryMap` — map `IOClass = AppleMIDIDriver` and related service class names.

No I/O beyond what existing SetupAPI/udev/IOKit paths already provide. `DeviceInfo.Name`,
`Manufacturer`, `VendorId`, and `ProductId` populate from the standard provider path.

### `DeviceCategory.Midi` represents ports, not physical devices

The core provider maps MIDI port interfaces to `DeviceCategory.Midi`. Each openable
endpoint becomes one `DeviceInfo` entry. This is the correct granularity because
`MidiInputPort.OpenAsync(deviceInfo)` and `MidiOutputPort.OpenAsync(deviceInfo)` in
`Periphery.Midi` operate on individual port handles — not on parent device nodes.

Callers who need to group ports by physical device can use `DeviceInfo.ContainerId`
(Windows) or `DeviceInfo.ParentId` (all platforms) to correlate entries that share the
same physical hardware.

### `Periphery.Midi` enrichment — port direction via typed `DeviceInfo` property + C# 14 extension properties

The port direction (input vs. output) is critical for filtering. Following the precedent
established by `UsbSpeed?`, `UsbClassCode?`, and `PortName?` on `DeviceInfo`, port
direction is a **typed nullable property on the core `DeviceInfo` record** — not stored
in the `Properties` bag:

```csharp
// In Periphery core library — DeviceInfo.cs
/// <summary>
/// Direction of this MIDI port. Non-null only for <see cref="DeviceCategory.Midi"/> entries.
/// Null for all other device categories.
/// </summary>
public MidiPortDirection? MidiPortDirection { get; init; }

// Also in Periphery core — alongside UsbSpeed, DriveType, etc.
public enum MidiPortDirection { Input, Output, Bidirectional }
```

The platform providers populate this property directly during enumeration, inferring the
direction from the OS API: `GUID_DEVINTERFACE_MIDI_INPUT` vs `GUID_DEVINTERFACE_MIDI_OUTPUT`
(Windows), ALSA `snd_seq_port_info_t.capability` flags (Linux), and `MIDIEndpointRef` source
vs destination (macOS CoreMIDI).

`Periphery.Midi` then adds computed predicates as C# 14 extension properties, gated
entirely on `using Periphery.Midi;`. These are a **convenience layer only** — they derive
their value from the typed `MidiPortDirection?` property, not from any storage of their own:

```csharp
// In Periphery.Midi — computed predicates over the typed DeviceInfo property
public static class MidiDeviceInfoExtensions
{
    extension(DeviceInfo device)
    {
        /// <summary>True if this DeviceInfo represents an openable MIDI input port.</summary>
        public bool IsMidiInputPort
            => device.MidiPortDirection is MidiPortDirection.Input
                                       or MidiPortDirection.Bidirectional;

        /// <summary>True if this DeviceInfo represents an openable MIDI output port.</summary>
        public bool IsMidiOutputPort
            => device.MidiPortDirection is MidiPortDirection.Output
                                       or MidiPortDirection.Bidirectional;
    }
}
```

Call-site — `MidiPortDirection` is available from the core library directly; the computed
predicates require `using Periphery.Midi;`:

```csharp
// MidiPortDirection is a typed property on DeviceInfo — available without any using
var direction = device.MidiPortDirection; // MidiPortDirection? — null if not a MIDI device

// Computed predicates require: using Periphery.Midi;
var inputPorts = await Devices.Enumerate()
    .OfCategory(DeviceCategory.Midi)
    .Where(d => d.IsMidiInputPort)
    .ToListAsync();

// DeviceTracker profile using the extension predicate
var tracker = new DeviceTracker(
    "Arturia KeyLab",
    new DeviceProfile(f => f
        .OfCategory(DeviceCategory.Midi)
        .ByManufacturer("Arturia")
        .Where(d => d.IsMidiInputPort)));
```

Without `using Periphery.Midi;` the `IsMidiInputPort` and `IsMidiOutputPort` extension
properties are invisible and will not compile. `MidiPortDirection?` itself is always
available from the core library — callers who do not import `Periphery.Midi` can still
check `device.MidiPortDirection` directly.

### I/O — deferred to `Periphery.Midi` extension package

MIDI I/O belongs in a dedicated extension package following the pattern established in
ADR-0019 and ADR-0020. The design of that package is out of scope for this ADR but should
address:

- Clock normalisation strategy across platforms.
- MIDI 1.0 vs MIDI 2.0 / UMP scope decision.
- Virtual port and routing model.
- `MidiMessage` type hierarchy vs raw buffer API.
- Whether `IAsyncEnumerable<MidiMessage>` or an event-driven callback model is the right
  streaming surface.

The `Periphery.Midi` extension would follow the same two-layer shape as `Periphery.Hid`:
a `MidiDevice` I/O primitive opened from a `DeviceInfo`, and a `MidiDeviceProxy`
lifecycle manager composing around `DeviceTracker`.

---

## Consequences

### Positive

- **POS-001**: MIDI devices are correctly categorised in enumeration results instead of
  falling into `Unknown` or `Audio`. Consumers can filter with
  `.OfCategory(DeviceCategory.Midi)`.
- **POS-002**: The boundary between enumeration and I/O is maintained. Periphery stays
  discovery-only.
- **POS-003**: Deferring I/O allows the MIDI 2.0 transition to stabilise before committing
  to an API surface that may need to support both protocol versions.
- **POS-004**: Port-granularity enumeration aligns directly with the `MidiInputPort.OpenAsync` /
  `MidiOutputPort.OpenAsync` API in `Periphery.Midi` — each `DeviceInfo` entry is
  immediately openable with no further resolution step.
- **POS-005**: C# 14 extension properties allow `Periphery.Midi` to surface `IsMidiInputPort`
  and `IsMidiOutputPort` as computed predicates on `DeviceInfo` without adding MIDI-specific
  boolean flags to the core library. The underlying `MidiPortDirection?` typed property is
  available from the core library for callers who do not import `Periphery.Midi`.

### Negative

- **NEG-001**: `DeviceCategory.Midi` adds classification work across all three platform
  providers. Linux in particular requires careful filtering to distinguish MIDI-capable
  sound devices from pure audio devices.
- **NEG-002**: Consumers wanting MIDI I/O today cannot get it from this library. They must
  use `NAudio`, `RtMidi.Net`, `managed-midi`, or the platform APIs directly.
- **NEG-003**: Enumerating `DeviceCategory.Midi` returns port-level entries. A 4-port MIDI
  interface produces up to 8 entries. Callers who expect one entry per physical device
  must group by `ContainerId` or `ParentId` themselves.
- **NEG-004**: `MidiPortDirection?` is `null` for all non-MIDI `DeviceInfo` entries. Callers
  must check `device.Category == DeviceCategory.Midi` or `device.MidiPortDirection != null`
  before treating the value as meaningful. This is the standard nullable-property contract
  used by `UsbSpeed?`, `PortName?`, and other category-specific properties on `DeviceInfo`.
- **NEG-005**: The C# 14 `extension` block syntax requires the consuming project to target
  .NET 10 / C# 14. Projects on older TFMs must use the traditional `this`-parameter
  extension method syntax if `Periphery.Midi` needs to support them.

---

## Alternatives Considered

### A — Add MIDI I/O to the core library

Rejected. MIDI I/O violates the discovery-only contract. Even if scoped to a small
surface (`SendMessage`, `ReceiveMessage`), the timestamping requirement alone introduces
a high-resolution clock dependency and platform-specific normalisation code that has no
place in an enumeration library. The complexity is disproportionate to the problem Periphery
solves.

### B — Model MIDI as a subcategory of `DeviceCategory.Audio`

Considered. Many MIDI interfaces present as composite USB audio+MIDI devices and are
already enumerated under `Audio`. However, pure MIDI controllers (with no audio path) are
not audio devices and would be incorrectly classified. A dedicated `Midi` category is
more honest.

### C — Skip `DeviceCategory.Midi` and leave MIDI to `Periphery.Midi`

Reasonable, but the category classification is trivial and immediately useful for consumers
who want to list available MIDI ports without writing platform-specific code. The
enumeration benefit is clear and cheap; there is no reason to defer it to the extension
package.

### D — Split into `DeviceCategory.MidiDevice` and `DeviceCategory.MidiPort`

Considered as a way to preserve the device / port distinction in the category enum itself.
Rejected for three reasons: (1) the OS does not provide a clean, cross-platform
"physical device" enumeration for MIDI that is distinct from port enumeration — Windows
SetupAPI exposes port interfaces directly; ALSA seq exposes clients and ports; CoreMIDI
has a three-level graph — synthesising a `MidiDevice` category would require platform-
specific parent-node walking that is fragile and inconsistent; (2) virtual ports (ALSA
seq virtual clients, CoreMIDI virtual endpoints) have no physical device parent at all
and would be unclassifiable; (3) the existing `DeviceCategory` enum maps each value to a
single, unambiguous OS concept — `MidiDevice` and `MidiPort` would be a logical
distinction *within* the MIDI subsystem, not an OS-level one, which is inconsistent with
how every other category in the enum is defined.

### E — Store `MidiPortDirection` in the `DeviceInfo.Properties` bag

Considered as a way to keep the core library free of any MIDI-specific type while still
making direction available to `Periphery.Midi` extension properties. Rejected for three
reasons: (1) `DeviceInfo.Properties` is intentionally narrow — the XML documentation on
that property explicitly states that scalar, well-typed domain data should be promoted to
typed properties directly; (2) existing category-specific typed properties on `DeviceInfo`
(`UsbSpeed?`, `UsbClassCode?`, `PortName?`, `BatteryChargePercent?`) are direct precedent
for MIDI-specific nullable properties — `UsbSpeed` comes from an extension domain just as
`MidiPortDirection` does; (3) a Properties bag lookup is stringly-typed, loses static
analysis, and requires a runtime dictionary access every time the value is read, whereas
a typed `init` property is zero-cost after construction.

The accepted decision is to add `MidiPortDirection?` and `MidiPortDirection` (the enum)
to the core library, following the same pattern as `UsbSpeed?`/`UsbSpeed`. C# 14 extension
properties in `Periphery.Midi` then provide computed predicates (`IsMidiInputPort`,
`IsMidiOutputPort`) as a convenience layer over the typed property — not as a storage
mechanism.