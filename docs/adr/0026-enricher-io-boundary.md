---
title: "ADR-0026: IDeviceEnricher Must Not Open Device Handles — Static Snapshot Helper Convention"
status: "Accepted"
status_note: "Shipped - `IDeviceEnricher`, `EnricherScope`, `EnrichmentPipeline`."
date: "2026-07-14"
authors: "@charles8051 (design)"
tags: ["architecture", "decision", "enricher", "api-design", "i/o-boundary", "extension"]
supersedes: ""
superseded_by: ""
---

# ADR-0026: IDeviceEnricher Must Not Open Device Handles — Static Snapshot Helper Convention

## Context

ADR-0024 §3c introduced a two-sub-kind model for `IDeviceEnricher`:

- **Sub-kind A (OS-metadata):** reads OS APIs (registry, sysfs, IOKit) without opening a
  device handle.
- **Sub-kind B (I/O-gated):** opens a transient device handle inside `EnrichAsync`, reads
  data unavailable from OS metadata, then closes the handle before returning.

Sub-kind B was introduced to handle a real gap: some data that is logically "about the
device" — and that a consumer might want before committing to a full I/O session — is not
surfaced by the OS at enumeration time. The canonical example is the USB serial number
string descriptor, which on some platforms requires a `GET_DESCRIPTOR` control transfer
to read.

After reviewing this model and identifying analogous cases across multiple device domains
(Bluetooth battery level, serial port baud rate capabilities, audio device sample rate
lists, camera resolution lists, gamepad actuator capabilities), we identified that
sub-kind B creates four problems that are not adequately mitigated by documentation alone:

### Problem 1 — `DeviceInfo` is no longer a zero-I/O snapshot

The load-bearing invariant of `DeviceInfo` is that it is a pure product of OS enumeration:
no handles were opened to produce it. Sub-kind B breaks this invariant silently. A
consumer who calls `Devices.Enumerate().WithEnricher(new UsbStringDescriptorEnricher())`
has no syntactic indication that they have signed up for N handle open/read/close cycles,
one per USB device enumerated. The I/O cost is invisible at the call site.

### Problem 2 — `null` becomes ambiguous

When a typed `DeviceInfo` property is `null`, it should mean exactly one thing: "the OS
did not surface this at enumeration time." With sub-kind B enrichers populating the same
property, `null` could instead mean "the enricher ran but the handle open failed" or
"the enricher ran but the read timed out" — two failure modes that are indistinguishable
from the consumer's perspective without inspecting enricher error logs.

### Problem 3 — Error handling has no clean answer

If a sub-kind B enricher's transient `OpenAsync` throws (device in use, access denied,
driver not loaded), the options are all bad:

- Abort enumeration of that device → silent data loss.
- Abort the entire enumeration → disproportionate; one unreachable device kills the list.
- Swallow and return `null` → the consumer cannot distinguish "not available" from
  "enricher failed."
- Re-throw → the consumer must wrap `Enumerate()` in try/catch for exceptions that are
  really just "serial number not readable right now."

### Problem 4 — Sub-kind B is just `OpenAsync` with extra steps

If the consumer needs data that requires a handle, they should open a handle explicitly.
The enricher abstraction adds a layer of indirection — hiding I/O inside what looks like
metadata enrichment — without adding capability. The data is still only available after
a handle is open; the enricher just hides that fact.

---

### The gray zone

The domains where sub-kind B enrichers were most tempting all share the same
characteristic: data that is *conceptually* static device metadata but where the *API
path* on at least one platform gates it behind a handle open. A survey of device domains
reveals this pattern consistently:

| Domain | Data | OS-enumerable on? | Handle-gated on? |
|---|---|---|---|
| USB | Serial number string descriptor | Linux (`sysfs`), macOS (IOKit) for most devices | Windows (WinUSB requires handle on some drivers) |
| Bluetooth | Battery level (BLE Battery Service 0x180F) | Windows (HID battery report cache, some devices) | All platforms for GATT-only devices |
| Serial / COM | Supported baud rate range | Windows (`IOCTL_SERIAL_GET_PROPERTIES`, no handle on some drivers) | Linux (port must be open) |
| Audio | Supported sample rates | macOS (Core Audio, no session needed) | Windows (WASAPI requires `IAudioClient` activation) |
| Camera | Supported resolutions / frame rates | macOS (`AVCaptureDeviceFormat`, no session) | Windows (MF requires source activation), Linux (V4L2 fd required) |
| Gamepad | Actuator / force-feedback capabilities | Never — XInput and DirectInput require device open | All platforms |

The platform inconsistency is itself an argument against using `DeviceInfo` for this data
at all. A typed property that is `null` on Windows but populated on Linux — not because
the device lacks the capability, but because the current platform's OS API gates it
differently — produces a confusing and platform-dependent consumer experience.

---

## Decision

**Option A + Option D.**

### Option A — `IDeviceEnricher` is OS-metadata only, unconditionally

Sub-kind B is removed. `IDeviceEnricher` implementations must never open device handles
or perform device I/O. The interface contract is restored to its original strict form:

```csharp
public interface IDeviceEnricher
{
    /// <summary>Whether this enricher applies to <paramref name="device"/>.</summary>
    bool CanEnrich(DeviceInfo device);

    /// <summary>
    /// Returns an enriched copy of <paramref name="device"/> with additional fields
    /// populated from OS enumeration metadata (registry keys, sysfs attributes,
    /// IOKit property bags, WMI property bags). Must not open device handles or
    /// perform device I/O. The returned <see cref="DeviceInfo"/> is always a
    /// zero-I/O snapshot.
    /// </summary>
    Task<DeviceInfo> EnrichAsync(DeviceInfo device, CancellationToken ct);
}
```

The XML doc on `EnrichAsync` is the enforcement point: it is a hard API contract, not a
guideline. Extension packages that implement `IDeviceEnricher` and open handles are
in violation of this contract regardless of whether they close the handle before returning.

#### What about the gray zone?

For data that is OS-enumerable on some platforms but handle-gated on others: populate it
where the OS surfaces it, leave it `null` where it doesn't. `null` retains its single
meaning: "the OS did not provide this at enumeration time on the current platform."
Document the platform caveat on the `DeviceInfo` property in XML doc. This is the same
honest approach taken for `DeviceInfo.BatteryChargePercent` — available where the OS
caches it; `null` otherwise.

### Option D — Static snapshot helper on the Layer 1 port

For cases where a consumer legitimately wants handle-gated data before committing to a
full I/O session, the Layer 1 port exposes a **static snapshot helper**:

```csharp
// In Periphery.{Domain} (extension package)
public sealed class {Domain}Port : IAsyncDisposable
{
    // ... normal I/O surface ...

    /// <summary>
    /// Opens a transient handle to <paramref name="device"/>, reads
    /// [domain-specific snapshot data], closes the handle, and returns
    /// the result. The handle does not outlive this call.
    /// </summary>
    /// <remarks>
    /// This method performs device I/O. It is appropriate when [the data]
    /// is needed before opening a full session. For consumers who will open
    /// a port anyway, read [the property] from the open port instead.
    /// </remarks>
    public static Task<{Domain}Snapshot> ReadSnapshotAsync(
        DeviceInfo device,
        CancellationToken ct = default);
}
```

**Key properties of this shape:**

1. **The I/O cost is explicit at the call site.** It's a method on `{Domain}Port`,
   not hidden inside an enricher registered on `Enumerate()`. The consumer knows they
   are paying I/O cost for one device, not N devices silently.

2. **Error handling is natural.** If the open fails, `ReadSnapshotAsync` throws. The
   consumer handles it at the explicit call site, not buried in enumeration error
   handling.

3. **It doesn't populate `DeviceInfo`.** The snapshot is returned as a domain type
   (`{Domain}Snapshot`), not as a modified `DeviceInfo` copy. `DeviceInfo` remains
   a pure OS enumeration artifact.

4. **It's discoverable.** A consumer who looks at `UsbPort` to understand USB I/O
   naturally finds `ReadSnapshotAsync`. It does not require knowing that the data is
   available via an enricher registered on the query.

#### USB concrete example

```csharp
// In Periphery.Usb
public sealed class UsbPort : IAsyncDisposable
{
    // ... transfers, interface claiming, etc. ...

    /// <summary>
    /// Opens a transient handle to <paramref name="device"/>, issues
    /// GET_DESCRIPTOR(DEVICE) and GET_DESCRIPTOR(CONFIGURATION) control transfers,
    /// and returns the full descriptor tree. The handle is closed before this method
    /// returns.
    /// </summary>
    /// <remarks>
    /// Use this when you need the descriptor tree before opening a persistent session.
    /// If you already have an open <see cref="UsbPort"/>, read
    /// <see cref="Descriptor"/> and <see cref="Configurations"/> directly.
    /// </remarks>
    public static async Task<UsbDescriptorSnapshot> ReadDescriptorsAsync(
        DeviceInfo device,
        CancellationToken ct = default)
    {
        await using var port = await OpenAsync(device, ct: ct);
        return new UsbDescriptorSnapshot
        {
            Descriptor     = port.Descriptor,
            Configurations = port.Configurations,
        };
    }
}

public sealed record UsbDescriptorSnapshot
{
    public UsbDeviceDescriptor Descriptor { get; init; } = null!;
    public IReadOnlyList<UsbConfigurationDescriptor> Configurations { get; init; } = [];
}
```

The caller is explicit:

```csharp
// Enumerate first — zero I/O cost
var devices = await Devices.Enumerate()
    .OfCategory(DeviceCategory.Usb)
    .WithUsbId("046D", "C52B")
    .ToListAsync();

// Explicit snapshot read for the one device we care about — caller knows this costs I/O
var snapshot = await UsbPort.ReadDescriptorsAsync(devices[0], ct);
Console.WriteLine(snapshot.Descriptor.SerialNumberString);

// If we're going to open it anyway, just read from the port
await using var port = await UsbPort.OpenAsync(devices[0]);
Console.WriteLine(port.Descriptor.SerialNumberString); // same data, no extra round-trip
```

#### Naming convention

Static snapshot helpers follow the naming pattern `ReadXxxAsync` where `Xxx` is the
domain concept being read. They always accept `DeviceInfo` and `CancellationToken` as
parameters. They always return a snapshot record or value, never `void`.

| Extension package | Helper | Returns |
|---|---|---|
| `Periphery.Usb` | `UsbPort.ReadDescriptorsAsync(DeviceInfo)` | `UsbDescriptorSnapshot` |
| `Periphery.Bluetooth` | `BluetoothPort.ReadAttributesAsync(DeviceInfo)` | `BluetoothDeviceAttributes` |
| `Periphery.Serial` | `SerialPort.ReadCapabilitiesAsync(DeviceInfo)` | `SerialPortCapabilities` |
| `Periphery.Hid` | `HidBattery.ReadSnapshotAsync(DeviceInfo)` | `HidBatterySnapshot?` |
| `Periphery.Audio` (hypothetical) | `AudioPort.ReadFormatsAsync(DeviceInfo)` | `AudioDeviceFormats` |

Not every extension package needs a static helper — only those where there is a genuine
use case for reading handle-gated data before opening a persistent session. If the
consumer will always open the port anyway, the data should simply be a property on the
open port, with no static helper at all.

#### Three-valued contract

A snapshot helper has three legitimate exit shapes. Callers should always handle two
failure modes plus the happy path:

| Outcome | Shape | When |
|---|---|---|
| **Success** | Return the snapshot record (non-null) | Handle opened, codec/parser succeeded |
| **Not applicable** | Return `null` (helper's return type is nullable) | The device doesn't expose this capability — no codec registered for the (VID, PID), no driver loaded, descriptor missing. **Not an error** — the caller passed a device that simply can't answer this question. |
| **Transport / protocol failure** | Throw the domain exception (e.g. `HidException`, `UsbException`) | Handle open failed, timeout, malformed response. The caller's input was reasonable but the device didn't cooperate. |

The `null` return is what distinguishes "this question doesn't apply" from "I tried and
failed." A `try/catch` around every snapshot call is the wrong shape when callers want
to ask "is this a UPS?" — they want `null` to mean no, and exception to mean something
broke. Helpers must commit to the same boundary: never throw to mean "not applicable,"
never return `null` to mean "I gave up."

`HidBattery.ReadSnapshotAsync` (ADR-0048) is the first concrete helper following this
contract: returns `null` when `HidQuirks` has no codec for the device's (VID, PID),
throws `HidException` when the codec was registered but the handle open or read failed.

---

## Alternatives Considered

### B — Two-phase `DeviceInfo` (`EnumeratedDeviceInfo` vs `InspectedDeviceInfo`)

Rejected. Adds type system complexity that propagates through all filter, watcher, and
query APIs. The caller benefit is marginal: the type distinction communicates the same
thing that the Option D call site already communicates explicitly. See the alternatives
analysis in the session that produced this ADR.

### C — Separate `IDeviceInspector` interface with explicit consumer call

Considered as a middle ground: keep `IDeviceEnricher` OS-metadata only (Option A), but
add `IDeviceInspector` to the core library as a named interface for handle-gated
enrichment. Rejected because it adds a second enricher-like interface to the core library
for a use case that is better served by a static method on the domain port (Option D).
`IDeviceInspector` would have exactly the same API as `IDeviceEnricher` but with looser
semantics — a distinction that is hard to enforce and easy to misuse. Option D is
simpler and puts the method on the type where a consumer naturally looks for it.

### E — Lazy `DeviceInfo.RequestAsync<TData>()` pull mechanism

Rejected. Makes `DeviceInfo` aware of a plugin mechanism — a core library concern
bleeding in the wrong direction. Equivalent to a service locator. Hard to trace in a
debugger. See the alternatives analysis in the session that produced this ADR.

### Sub-kind B (the rejected approach)

The sub-kind B I/O-gated enricher model introduced in ADR-0024 §3c is superseded by
this ADR. The four problems documented in the Context section — invisible I/O cost,
ambiguous `null`, insoluble error handling, and redundancy with explicit `OpenAsync` —
collectively make it the wrong default. The static helper (Option D) addresses all four:
cost is explicit, errors are caught at the call site, `DeviceInfo` is never modified by
I/O, and the method lives on the type the consumer already knows about.

---

## Consequences

### Positive

- **POS-001**: `DeviceInfo` is unconditionally a zero-I/O snapshot. The invariant has no
  exceptions, no sub-kinds, no footnotes. A consumer can always assume that obtaining a
  `DeviceInfo` never opened a device handle.
- **POS-002**: `null` on a typed `DeviceInfo` property has exactly one meaning: the OS did
  not surface this at enumeration time. There is no second meaning for enricher failure
  or platform unavailability.
- **POS-003**: Error handling for handle-gated reads is natural: `ReadSnapshotAsync` throws,
  the consumer handles it at the explicit call site. No silent swallowing, no
  per-device abort policy buried in enricher infrastructure.
- **POS-004**: The I/O cost of reading handle-gated data is visible at the call site.
  `UsbPort.ReadDescriptorsAsync(device)` is obviously a method call on a port type that
  performs I/O. `Devices.Enumerate().WithEnricher(new UsbStringDescriptorEnricher())` is
  not obviously I/O at the call site.
- **POS-005**: The `IDeviceEnricher` interface contract is simple and verifiable: no handles,
  no I/O, OS metadata only. Code review of an enricher implementation is straightforward.
- **POS-006**: The static snapshot helper pattern is discoverable. Consumers who look at
  `UsbPort` for USB capabilities naturally find `ReadDescriptorsAsync`. There is no
  hidden enricher registration path to discover.

### Negative

- **NEG-001**: Consumers who want handle-gated data during a bulk enumeration loop must now
  call `ReadSnapshotAsync` explicitly per device, rather than registering an enricher
  once. For large device lists this is more verbose. The tradeoff is that the cost is
  now visible — which is the point.
- **NEG-002**: Some data in the gray zone (e.g. USB serial number on Windows with certain
  drivers) is simply not available at enumeration time on some platforms. The
  corresponding `DeviceInfo` property is `null` on those platforms and populated on
  others. This platform disparity existed before sub-kind B; sub-kind B merely hid it.
  With Option A, the disparity is visible; the property XML doc must document the
  platform caveat clearly.
- **NEG-003**: Extension packages that had planned to use sub-kind B enrichers must
  migrate to the static helper pattern. For `Periphery.Usb` this is a design-time
  decision (not yet implemented); no migration cost in practice.

---

## Open Questions

- **OQ-001**: Should the static snapshot helper return a `DeviceInfo` with additional
  fields populated, or a separate domain snapshot record? This ADR recommends a separate
  record (`UsbDescriptorSnapshot`) to avoid the appearance that `DeviceInfo` was produced
  by I/O. If future use cases require the enriched `DeviceInfo` to flow back into the
  filter/watcher APIs, revisit this decision.

- **OQ-002**: Should `ReadSnapshotAsync` be on the port type itself, or on a companion
  static class (e.g. `UsbPortSnapshot.ReadAsync`)? The port type is more discoverable;
  a companion class is cleaner if the port type already has a large surface. Default to
  the port type unless the surface becomes unwieldy.

- **OQ-003**: The §Naming convention table above shows only the success shape of an
  Option D helper. In practice, helpers have **three** exit shapes — success (return
  the snapshot), not-applicable (return `null`; e.g. no codec registered, no driver
  loaded, device doesn't expose this capability), and transport/protocol failure
  (throw). `HidBattery.ReadSnapshotAsync` (ADR-0048) is the first concrete helper in
  the tree and uses all three: throws `HidException` on transport failure, returns
  `null` when `HidQuirks` has no codec for the device's (VID, PID), returns the
  snapshot otherwise. Callers must handle both `null` and the exception.
  **Tentative answer:** spell the three-valued contract into the naming convention so
  future helpers (`UsbPort.ReadDescriptorsAsync`, `BluetoothPort.ReadAttributesAsync`,
  ...) follow the same pattern rather than each re-deriving where the "not applicable"
  boundary lies.

- **OQ-004**: ~~This ADR describes `IDeviceEnricher` as an interface contract, but the
  type does not yet exist in the tree.~~ **Resolved (2026-05-27).** `IDeviceEnricher`
  now exists in core Periphery (`src/Periphery/IDeviceEnricher.cs`), matching the
  ADR-0024 §3c signature: `bool CanEnrich(DeviceInfo)` discriminator plus async
  `Task<DeviceInfo> EnrichAsync(DeviceInfo, CancellationToken)`. Registration goes
  through the process-wide `DeviceEnrichers` registry (mirrors `HidQuirks`'s
  Register/Unregister/Snapshot shape) and extension packages auto-register via
  `[ModuleInitializer]` — `Periphery.Hid` registers `HidBatteryEnricher.Instance` on
  assembly load. `HidBatteryEnricher` is now a sealed class implementing
  `IDeviceEnricher`; the old static `Enrich(DeviceInfo)` method is gone (no consumers
  outside this repository's scope). The Windows provider runs the registry
  via `WindowsEnrichmentPipeline.RunRegisteredAsync` (async iterator path) and
  `RunRegisteredSync` (callback / cache paths); per-enricher exceptions are caught
  and logged so a misbehaving extension can't nuke an enumeration. **Deferred**:
  the Linux and macOS providers don't yet invoke the pipeline — follow-up work
  when those providers land their own enrichers. **Also deferred**: converting
  `WindowsBatteryEnricher` itself to `IDeviceEnricher` would unify the
  enricher-as-call-site pattern across core and extensions; today the Windows
  inline enrichers still own their per-enumeration optimisation (single
  `GetSystemPowerStatus`, single `DisplayConfigEnricher.Build`). Not load-bearing
  until a similar optimisation is needed in an extension-package enricher.

---

## Impact on Prior ADRs

| ADR | Change required |
|---|---|
| ADR-0024 §3c | Sub-kind B documentation removed; `IDeviceEnricher` restored to OS-metadata only; Option D static helper convention added to checklist |
| ADR-0019 §Required changes 1 | Sub-kind B removed from `IDeviceEnricher` description; `UsbPort.ReadDescriptorsAsync` added as the Option D example |
| ADR-0020 | `HidDeviceEnricher` is sub-kind A only (reads HID caps from OS without handle) — no change required; already compliant |
| ADR-0022 | No enricher used — unaffected |
| ADR-0023 | No enricher used — unaffected |
