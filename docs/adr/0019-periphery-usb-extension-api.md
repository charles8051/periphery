---
title: "ADR-0019: Periphery.Usb Extension Library — API Shape and Core Extensibility Contract"
status: "Accepted"
status_note: "Shipped - `src/Periphery.Usb`."
date: "2026-07-14"
authors: "@charles8051 (design)"
tags: ["architecture", "decision", "usb", "extension", "api-design", "periphery-usb", "i/o"]
supersedes: ""
superseded_by: ""
---

# ADR-0019: Periphery.Usb Extension Library — API Shape and Core Extensibility Contract

## Context

### The discovery / I/O boundary

`Periphery` is a discovery-only library. Its public contract is explicit: it enumerates
hardware devices and returns immutable `DeviceInfo` snapshots. It never opens device
handles, sends commands, or reads data streams. This constraint is load-bearing — it is
what keeps the library safe to call on any thread, in any context, with zero side-effects.

A natural follow-on use case is performing actual USB I/O with devices that Periphery
has already discovered. The existing enumeration infrastructure (`DeviceInfo`, `DeviceTracker`,
`DeviceWatcher`, `DeviceFilter`, `DeviceProfile`) is directly reusable; a companion package
`Periphery.Usb` need not reimplement discovery at all. The design question is how to bridge
enumeration metadata into an I/O context cleanly, and what changes (if any) the core library
needs to support that bridge.

### What USB I/O requires

At the hardware/OS level, a USB I/O session requires:

1. **A device handle** — a platform-specific kernel object (WinUSB `HANDLE` on Windows,
   `libusb_device_handle*` on Linux, `IOUSBInterfaceInterface` vtable on macOS).
2. **Descriptor reading** — an initial `GET_DESCRIPTOR(DEVICE)` control transfer to read
   `bcdUSB`, `bDeviceClass`, `bMaxPacketSize0`, string indices, number of configurations,
   etc. This is genuine I/O; it cannot be performed during enumeration.
3. **Interface claiming** — required by Linux `usbfs` before any transfer can be submitted.
4. **Transfer submission** — control (IN/OUT), bulk (IN/OUT), interrupt (IN), isochronous.

Some USB metadata **is** available without I/O via OS enumeration APIs:
- Windows registry (`HKLM\SYSTEM\CCS\Enum\USB\...`) exposes `bcdUSB`, device/class strings.
- Linux `sysfs` (`/sys/bus/usb/devices/...`) exposes most descriptor fields as text files.
- macOS IOKit service properties expose the same set.

This metadata is legitimately part of the enumeration layer and belongs in `DeviceInfo`.
The descriptor tree (configurations, interfaces, endpoints) requires an open handle and
cannot be enumerated without I/O.

---

## Decision

### Two-layer design for `Periphery.Usb`

This design maps directly to the canonical **Layer 1 / Layer 2** extension package pattern
established in ADR-0024. `UsbDevice` is the Layer 1 I/O primitive; `UsbDeviceProxy` is
the Layer 2 lifecycle manager.

#### Layer 1 — `UsbDevice` (the I/O primitive)

`UsbDevice` is the explicit, named crossing of the discovery / I/O boundary. It is created
only by `UsbDevice.OpenAsync(DeviceInfo)` — a static factory that uses `DeviceInfo.Id` to
locate the platform handle. It reads the device descriptor on open and exposes the full
transfer surface:

```csharp
public sealed class UsbDevice : IAsyncDisposable
{
    // Discovery context
    public DeviceInfo DeviceInfo { get; }

    // Read during OpenAsync via GET_DESCRIPTOR(DEVICE)
    public UsbDeviceDescriptor Descriptor { get; }
    public IReadOnlyList<UsbConfigurationDescriptor> Configurations { get; }

    // Transfer surface
    public Task<int> ControlTransferAsync(UsbSetupPacket setup,
        Memory<byte>? data = null, CancellationToken ct = default);
    public Task<int> BulkReadAsync(byte endpointAddress,
        Memory<byte> buffer, CancellationToken ct = default);
    public Task<int> BulkWriteAsync(byte endpointAddress,
        ReadOnlyMemory<byte> data, CancellationToken ct = default);
    public Task<int> InterruptReadAsync(byte endpointAddress,
        Memory<byte> buffer, CancellationToken ct = default);

    // Interface claiming (required on Linux before transfers)
    public Task ClaimInterfaceAsync(int number, CancellationToken ct = default);

    // Factory — bridge from enumeration to I/O
    public static Task<UsbDevice> OpenAsync(DeviceInfo device,
        CancellationToken ct = default);
}
```

#### Layer 2 — `UsbDeviceProxy` (the lifecycle manager)

`UsbDeviceProxy` is the `DeviceTracker` equivalent for I/O. It composes around a
`DeviceTracker` (via the existing `StateChanged` event) and manages the `UsbDevice`
open/close lifecycle automatically: opening a handle when the tracker transitions to
connected, disposing it on disconnect. This gives the same profile-ordered resolution and
watcher-restart resilience as `DeviceTracker` without any new infrastructure:

```csharp
public sealed class UsbDeviceProxy : INotifyPropertyChanged, IAsyncDisposable
{
    public UsbDeviceProxy(DeviceTracker tracker);

    // Mirror of inner tracker state
    public bool IsConnected { get; }
    public DeviceInfo? DeviceInfo { get; }

    // The additional layer — live I/O handle when connected, null otherwise
    public UsbDevice? Device { get; private set; }

    // Fine-grained lifecycle events (in addition to PropertyChanged)
    public event EventHandler<UsbDevice>? DeviceOpened;
    public event EventHandler? DeviceClosed;
    public event PropertyChangedEventHandler? PropertyChanged;
}
```

Internally `UsbDeviceProxy` subscribes to `tracker.StateChanged`. On connect it awaits
`UsbDevice.OpenAsync`; on disconnect it awaits `existing.DisposeAsync()`. The consumer
never manages the open/close cycle.

#### Descriptor types

```csharp
public sealed record UsbDeviceDescriptor
{
    public ushort UsbVersion { get; init; }          // bcdUSB
    public byte DeviceClass { get; init; }           // bDeviceClass
    public byte DeviceSubClass { get; init; }        // bDeviceSubClass
    public byte DeviceProtocol { get; init; }        // bDeviceProtocol
    public byte MaxPacketSize0 { get; init; }        // bMaxPacketSize0
    public string? ManufacturerString { get; init; } // iManufacturer resolved
    public string? ProductString { get; init; }      // iProduct resolved
    public string? SerialNumberString { get; init; } // iSerialNumber resolved
    public byte NumConfigurations { get; init; }     // bNumConfigurations
}

public sealed record UsbConfigurationDescriptor
{
    public byte ConfigurationValue { get; init; }
    public string? ConfigurationString { get; init; }
    public byte Attributes { get; init; }
    public byte MaxPowerMilliamps { get; init; }
    public ImmutableArray<UsbInterfaceDescriptor> Interfaces { get; init; }
}

public sealed record UsbInterfaceDescriptor
{
    public byte InterfaceNumber { get; init; }
    public byte AlternateSetting { get; init; }
    public byte InterfaceClass { get; init; }
    public byte InterfaceSubClass { get; init; }
    public byte InterfaceProtocol { get; init; }
    public string? InterfaceString { get; init; }
    public ImmutableArray<UsbEndpointDescriptor> Endpoints { get; init; }
}

public sealed record UsbEndpointDescriptor
{
    public byte EndpointAddress { get; init; }  // direction + number
    public UsbTransferType TransferType { get; init; }
    public ushort MaxPacketSize { get; init; }
    public byte Interval { get; init; }
}
```

### Call-site shape

```csharp
// One-shot: open a device and read from a bulk endpoint
var device = await Devices.Enumerate()
    .OfCategory(DeviceCategory.Usb)
    .WithUsbId("046D", "C52B")
    .FirstOrDefaultAsync();

await using var usb = await UsbDevice.OpenAsync(device);
Console.WriteLine(usb.Descriptor.ProductString);

var buf = new byte[64];
int read = await usb.BulkReadAsync(0x81, buf);

// Lifecycle-managed: auto-opens on connect, auto-closes on disconnect
var tracker = new DeviceTracker("MX Master 3",
    new DeviceProfile(f => f.WithUsbId("046D", "C52B")));

await using var handle = new UsbDeviceProxy(tracker);

handle.DeviceOpened += (_, dev) =>
    Console.WriteLine($"Opened: {dev.Descriptor.ProductString}");

handle.DeviceClosed += (_, _) =>
    Console.WriteLine("Device disconnected.");

await using var watcher = Devices.Watch().AddTrackers(tracker);
await watcher.StartAsync();
```

---

## Required changes to the core library

### 1. `IDeviceEnricher` interface

Allows `Periphery.Usb` (and future extension packages) to populate `DeviceInfo` fields
from OS-cached metadata during enumeration, without the core library having any knowledge
of USB protocols. Registered fluently on `DeviceQuery` and `DeviceWatcher`. The full
contract is specified in ADR-0024 §3c; the boundary decision is in ADR-0026.

Implementations must not open device handles or perform device I/O. OS metadata
(registry keys, sysfs attributes, IOKit property bags) only.

```csharp
// In Periphery core
public interface IDeviceEnricher
{
    /// <summary>Whether this enricher applies to <paramref name="device"/>.</summary>
    bool CanEnrich(DeviceInfo device);

    /// <summary>
    /// Returns an enriched copy of <paramref name="device"/> with additional fields
    /// populated from OS enumeration metadata (registry, sysfs, IOKit, WMI).
    /// Must not open device handles or perform device I/O.
    /// The returned <see cref="DeviceInfo"/> is always a zero-I/O snapshot.
    /// </summary>
    Task<DeviceInfo> EnrichAsync(DeviceInfo device, CancellationToken ct);
}

// Fluent registration
Devices.Enumerate()
    .OfCategory(DeviceCategory.Usb)
    .WithEnricher(new UsbOsMetadataEnricher())
    .ToListAsync();
```

#### Handle-gated USB data — `UsbDevice.ReadDescriptorsAsync` (ADR-0026 Option D)

Some USB data (full string descriptors, configuration details on platforms where
sysfs/IOKit do not cache them) requires a `GET_DESCRIPTOR` control transfer to read.
This data must not be fetched by an enricher. Instead, `UsbDevice` exposes a static
snapshot helper that makes the I/O cost explicit at the call site:

```csharp
// In Periphery.Usb — explicit, not hidden in enumeration
public sealed class UsbDevice : IAsyncDisposable
{
    // ... normal transfer surface ...

    /// <summary>
    /// Opens a transient handle to <paramref name="device"/>, reads the full
    /// descriptor tree via GET_DESCRIPTOR control transfers, closes the handle,
    /// and returns the result. Use this when descriptor data is needed before
    /// opening a persistent session. If a <see cref="UsbDevice"/> is already
    /// open, read <see cref="Descriptor"/> and <see cref="Configurations"/> directly.
    /// </summary>
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

Call-site contrast:

```csharp
// ✅ Explicit — caller knows this costs I/O for one device
var snapshot = await UsbDevice.ReadDescriptorsAsync(device, ct);
Console.WriteLine(snapshot.Descriptor.SerialNumberString);

// ✅ Already open — no extra round-trip
await using var usb = await UsbDevice.OpenAsync(device);
Console.WriteLine(usb.Descriptor.SerialNumberString);
```
```

### 2. Promoted typed USB fields on `DeviceInfo`

`VendorId`, `ProductId`, and `UsbClassCode` are already present. The following additional
fields are OS-enumerable on all three platforms without I/O and belong as first-class
typed `init` properties on `DeviceInfo` rather than stranded in `Properties`. This
follows the ADR-0024 Layer 3 promotion rule: scalar, enumeration-time values become
typed properties; the full descriptor tree (configurations, interfaces, endpoints)
requires an open handle and lives on `UsbDevice` (Layer 1), not on `DeviceInfo`.

- `UsbVersion` (`ushort?`) — `bcdUSB` from registry/sysfs/IOKit
- `UsbDeviceProtocol` (`byte?`) — `bDeviceProtocol`
- `UsbMaxPacketSize0` (`byte?`) — `bMaxPacketSize0` for endpoint 0

### 3. `DeviceTracker` composition API

`DeviceTracker` is correctly sealed (inheritance is wrong here). `UsbDeviceProxy` can
already compose around `StateChanged`, but a `TrackAs<T>` factory method on `DeviceWatcher`
would make the pattern ergonomic and consistent with the existing `AddTracker` API:

```csharp
// Proposed
var handle = Devices.Watch()
    .OfCategory(DeviceCategory.Usb)
    .WithUsbId("046D", "C52B")
    .TrackAs(tracker => new UsbDeviceProxy(tracker));
```

---

## Relationship to ADR-0024 and ADR-0025

**ADR-0024** (Extension Package Pattern) formalised the two-layer architecture introduced
here as the canonical three-layer model (Layer 1 I/O primitive, Layer 2 lifecycle
manager, Layer 3 enrichment). `Periphery.Usb` was the motivating example. Key points
of alignment:

- `UsbDevice` is the canonical **Layer 1** shape: `static OpenAsync(DeviceInfo)`,
  `IAsyncDisposable`, no inheritance.
- `UsbDeviceProxy` is the canonical **Layer 2** shape: composes `DeviceTracker` via
  `StateChanged`, `INotifyPropertyChanged`, `IAsyncDisposable`.
- The three promoted scalar fields (`UsbVersion`, `UsbDeviceProtocol`,
  `UsbMaxPacketSize0`) follow the ADR-0024 **Layer 3 promotion rule**: scalar,
  enumeration-time values → typed `init` property on `DeviceInfo`; descriptor tree
  (requires open handle) → property on `UsbDevice` (Layer 1).
- `IDeviceEnricher` is the ADR-0024 §3c extension hook. Implementations are OS-metadata
  only — no handle opens, no device I/O (ADR-0026).
- `UsbDevice.ReadDescriptorsAsync` is the ADR-0026 **Option D** static snapshot helper
  for handle-gated descriptor data.

**ADR-0025** (Extensible `DeviceCategory`) provides the mechanism by which
`Periphery.Usb` can register fine-grained USB sub-categories (e.g.
`DeviceCategory.UsbHub`, `DeviceCategory.UsbComposite`) without modifying the core
library. Extension packages use `[ModuleInitializer]` to call
`DeviceCategoryRegistry.Register*` and `RegisterDisplayName` at startup. Platform map
default arms consult the registry before throwing, so new categories work end-to-end
without core changes.

**ADR-0026** (Enricher I/O Boundary) is the decision that removed sub-kind B enrichers
from this ADR and established the static snapshot helper convention. The full rationale,
the gray-zone analysis across device domains, and the alternatives considered are
documented there.

---

## Consequences

### Positive

- **POS-001**: `Periphery.Usb` can be built entirely as a consumer of the public `Periphery`
  API. The core library needs only the three changes above; it does not need to know
  about USB transfers or descriptors.
- **POS-002**: The discovery / I/O boundary is a named, explicit crossing (`UsbDevice.OpenAsync`).
  Consumers who enumerate only never pay any I/O cost.
- **POS-003**: `UsbDeviceProxy` reuses `DeviceTracker`'s profile system, priority ordering,
  ambiguity latch, and watcher-restart resilience without reimplementing them.
- **POS-004**: `IDeviceEnricher` is a general extension hook, not USB-specific. Future packages
  (`Periphery.Bluetooth`, `Periphery.Hid`) can register their own enrichers under the same
  interface.
- **POS-005**: OS-enumerable USB metadata (bcdUSB, class strings, max packet size) can be
  populated during enumeration and is available on `DeviceInfo` without opening a handle —
  useful for display and filtering without committing to I/O.

### Negative

- **NEG-001**: `IDeviceEnricher` adds async overhead to enumeration when registered, even for
  devices the enricher skips. The `CanEnrich` fast-path check mitigates this.
- **NEG-002**: `UsbDevice.OpenAsync` requires exclusive access semantics on some platforms
  (WinUSB requires the kernel driver to be WinUSB/libwdi; Linux `usbfs` requires
  `CAP_NET_ADMIN` or a udev rule). These are environmental prerequisites not expressible
  in the API shape.
- **NEG-003**: `UsbDeviceProxy.DeviceOpened` fires on a background thread (the watcher
  event thread). Consumers must marshal to UI thread if needed — consistent with
  `DeviceTracker.StateChanged` but worth documenting prominently.

---

## Alternatives Considered

### A — Inheritance from `DeviceTracker`

Rejected. `DeviceTracker` is sealed by design. Even if
unsealed, inheritance creates brittle coupling to the tracker's internal state machine and
makes it impossible to compose multiple trackers. The `StateChanged` event already provides
everything `UsbDeviceProxy` needs.

### B — USB descriptors in `DeviceInfo`

Rejected for the full descriptor tree (configurations, interfaces, endpoints). These require
an open handle and cannot be populated during enumeration without violating the
discovery-only contract. The three scalar fields promoted in the decision
(`UsbVersion`, `UsbDeviceProtocol`, `UsbMaxPacketSize0`) are genuinely OS-enumerable and
are the correct subset to promote.

### C — Static `UsbDevice.FromDeviceInfo(DeviceInfo)` returning a handle without reading descriptors

Considered as a lighter-weight factory that defers descriptor reading to a separate
`ReadDescriptorAsync()` call. Rejected in favour of reading on open: the descriptor is
almost always needed immediately, the control transfer is fast (<1 ms), and splitting it
introduces a "partially-initialized handle" state that callers must guard against.

### D — `Periphery.Usb` as a separate solution / repo

Reasonable, but premature. While `Periphery.Usb` will have platform-native I/O backends
and its own test surface, keeping it in the same solution during initial development avoids
cross-repo coordination friction. The `IDeviceEnricher` boundary is clean enough that
splitting later is low-cost.

---

## Open Questions

- **OQ-001**: Should `UsbDevice` support isochronous transfers in v1? Isochronous is
  significantly more complex (requires pre-allocated URBs on Linux, streaming pipes on macOS)
  and the use cases are narrow (audio, video capture). Likely deferred to v2.
- **OQ-002**: Should `UsbDeviceProxy` auto-retry `OpenAsync` on transient failures
  (e.g., the device is briefly inaccessible during driver initialization)? Or leave
  retry policy to the consumer?
- **OQ-003**: What is the `Periphery.Usb` TFM target? `net8.0` minimum to match the core
  library, with per-platform backend implementations.
- **OQ-004**: Should `UsbDevice` expose a C#14 extension property block (via the ADR-0024
  Layer 3 pattern) to surface computed predicates such as `IsHighSpeed`, `IsHub`, and
  `SupportsUsbPower`? These are derivable from `UsbVersion` and `UsbClassCode` without
  opening a handle and fit naturally into the fluent filter API.