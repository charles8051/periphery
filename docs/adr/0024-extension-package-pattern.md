---
title: "ADR-0024: Extension Package Pattern — Generalised Contract for Periphery I/O Libraries"
status: "Accepted"
status_note: "Shipped - the pattern every extension package follows (`Periphery.Hid`, `Periphery.Usb`, `Periphery.Camera`, `Periphery.Monitor`)."
date: "2026-07-14"
amended: "2026-08-25"
authors: "@charles8051 (design)"
tags: ["architecture", "decision", "extension", "api-design", "pattern", "i/o", "lifecycle"]
supersedes: ""
superseded_by: ""
---

# ADR-0024: Extension Package Pattern — Generalised Contract for Periphery I/O Libraries

> **Amendment (2026-08-25).** The "Package naming and dependency rules" table
> gains a `Periphery.{Domain}.{Library}` row for integration packages, which
> the original three rows had no place for. Two corrections in the same table:
> the `Periphery` row said "BCL only", and the core references
> `Microsoft.Extensions.Logging.Abstractions` on the terms in
> [`docs/patterns/logging-and-diagnostics.md`](../patterns/logging-and-diagnostics.md);
> build-only references marked `PrivateAssets="All"` are now stated not to
> count against the table. Where an
> integration package lives is routed by
> [`docs/patterns/integration-package-placement.md`](../patterns/integration-package-placement.md).

## Context

`Periphery` is a discovery-only library. Its core contract — enumerate hardware, return
immutable `DeviceInfo` snapshots, never open handles — is load-bearing. It keeps the
library safe to call on any thread, in any context, with zero side-effects.

But discovery is rarely the end-goal. Consumers discover devices in order to *use* them:
read reports, send MIDI messages, transfer USB data, capture keystrokes. A recurring need
has emerged across multiple extension packages (`Periphery.Usb`, `Periphery.Hid`,
`Periphery.Midi`, `Periphery.Input`) for a consistent set of answers to the same
architectural questions:

1. How does I/O bridge from a `DeviceInfo` snapshot to an open platform handle?
2. How does lifecycle management work when a device disconnects and reconnects?
3. How does an extension package add domain-specific properties and filters to `DeviceInfo`
   without modifying the core library?
4. What are the NativeAOT and GC constraints on I/O callback paths?
5. What is the standard shape for async streaming APIs?

ADR-0019 (`Periphery.Usb`) established a two-layer pattern and a set of required core
changes. ADR-0020 (`Periphery.Hid`) validated it. ADR-0022 (`Periphery.Input`) and
ADR-0023 (`Periphery.Midi`) each independently arrived at the same structure, adding
refinements for real-time callback constraints and enrichment via C# 14 extension
properties. This ADR extracts and formalises the pattern so that future extension packages
have an explicit contract to follow rather than reverse-engineering precedent.

---

## The Pattern

An extension package in the Periphery ecosystem is a NuGet package that:

- Depends on `Periphery` (the core library) — never the reverse.
- Bridges the `DeviceInfo` enumeration layer to platform-native I/O.
- Follows the three-layer structure below.
- Meets the AoT, GC, and API shape constraints defined here.

### Layer 1 — The I/O primitive

The I/O primitive is the explicit, named crossing of the discovery / I/O boundary. It is
always created via a static `OpenAsync(DeviceInfo, ...)` factory. It owns exactly one
platform handle for the lifetime of the object.

**Canonical shape:**

```csharp
public sealed class {Domain}Port : IAsyncDisposable   // or {Domain}Device, {Domain}Session
{
    // The DeviceInfo this handle was opened from — never null after construction.
    public DeviceInfo DeviceInfo { get; }

    // Domain-specific I/O surface (transfers, streams, messages, etc.)
    // ...

    // The only constructor of a handle is this static factory.
    // Throws {Domain}Exception (or DeviceProviderException) if the port cannot be opened.
    public static Task<{Domain}Port> OpenAsync(
        DeviceInfo device,
        {Domain}PortOptions? options = null,
        CancellationToken ct = default);
}
```

**Rules:**
- `sealed` — not inheritable. Composability is via the lifecycle manager, not subclassing.
- `IAsyncDisposable` — always. Platform handles are always released asynchronously.
- Static `OpenAsync` is the **only** public construction path. No public constructors.
- `DeviceInfo` is set at construction and never changes. It is the link back to
  enumeration metadata and is always accessible even after `DisposeAsync`.
- Throws a domain-specific exception type (e.g. `MidiPortException`, derived from
  `DeviceEnumerationException`) if the device cannot be opened, with a meaningful
  message that includes the device name and platform error.

### Layer 2 — The lifecycle manager

The lifecycle manager composes around a `DeviceTracker` via its `StateChanged` event.
It manages the open/close cycle automatically: `OpenAsync` on connect, `DisposeAsync`
on disconnect. This gives consumers reconnect resilience, profile-ordered resolution,
and ambiguity handling without any new infrastructure.

**Canonical shape:**

```csharp
public sealed class {Domain}DeviceProxy : INotifyPropertyChanged, IAsyncDisposable
{
    // Accepts a pre-configured DeviceTracker — does not create one internally.
    public {Domain}DeviceProxy(DeviceTracker tracker,
        {Domain}PortOptions? options = null);

    // Mirrors the tracker's connected state.
    public bool IsConnected { get; }
    public DeviceInfo? DeviceInfo { get; }

    // The live I/O primitive. Non-null only while the device is connected.
    public {Domain}Port? Port { get; }

    // Fine-grained lifecycle events.
    public event EventHandler<{Domain}Port>? PortOpened;
    public event EventHandler? PortClosed;

    public event PropertyChangedEventHandler? PropertyChanged;
}
```

**Rules:**
- Accepts an *externally constructed* `DeviceTracker` — it does not build the tracker
  internally. This allows consumers to share a tracker across multiple handles or use
  `DeviceWatcher.AddTrackers` for declarative registration.
- `Port` transitions: `null` → non-null on connect (after `OpenAsync` succeeds);
  non-null → `null` on disconnect (after `DisposeAsync` completes).
- `PortOpened` and `PortClosed` fire on the watcher's event thread. Document this
  prominently — consumers must marshal to UI thread if needed.
- `DisposeAsync` disposes the inner tracker subscription and any open `Port`.

### Layer 3 — Enrichment (optional)

Some extension packages expose domain-specific data about devices. The right storage
mechanism depends on the nature of that data.

#### The promotion rule

`DeviceInfo` already carries category-specific typed properties as a matter of course:
`UsbSpeed?`, `UsbClassCode?`, `PortName?`, `BatteryChargePercent?`, `DisplayResolution?`
are all null for devices where they do not apply. The existing code and its comments are
explicit: *"Most platform-specific concepts that are scalar and universally meaningful on
their platform are promoted to typed properties on DeviceInfo directly."*

The decision rule for new domain data is:

| Data characteristic | Storage |
|---|---|
| Scalar, well-typed, available at enumeration time without opening a handle | **Typed `init` property on `DeviceInfo`** in the core library |
| Array-typed, diagnostic/raw, or platform-specific debug data | `DeviceInfo.Properties` bag |
| Requires opening a device handle or performing device I/O to read | **Property on the Layer 1 I/O primitive** (`{Domain}Port`) — never on `DeviceInfo` |

A typed property on `DeviceInfo` requires a core library PR, but that cost is the right
one to pay. It produces compile-time type safety, value equality in record `with`
expressions, JSON serialisation, and IntelliSense completeness — none of which the
`Properties` bag provides. The `Properties` bag is a last resort for data that genuinely
has no typed home, not a convenience escape hatch to avoid a core change.

**`DeviceInfo` is always a zero-I/O snapshot.** It is constructed from OS enumeration
APIs that never open device handles. Data that requires opening a handle — USB full
descriptor trees, HID report descriptors, MIDI device capability queries — cannot be
populated during enumeration without a handle-per-device penalty across the entire device
list. That data belongs on the open `{Domain}Port` primitive, where a handle already
exists and the cost is paid explicitly by the consumer who opened it.

#### 3a — Typed property on `DeviceInfo` (preferred for scalar domain data)

Add a nullable typed property to `DeviceInfo` in the core library. The property is `null`
for all devices where it does not apply, exactly as `UsbSpeed` is null for non-USB devices:

```csharp
// In Periphery core — DeviceInfo.cs
/// <summary>
/// Direction of this MIDI port.
/// Non-null only for <see cref="DeviceCategory.Midi"/> entries.
/// </summary>
public MidiPortDirection? MidiPortDirection { get; init; }
```

The corresponding enum or type lives in the core library alongside the property. The
core library's category map provider populates it during enumeration from OS-native
data (e.g. `GUID_DEVINTERFACE_MIDI_INPUT` vs `GUID_DEVINTERFACE_MIDI_OUTPUT` on Windows).
No enricher pass and no runtime dictionary lookup are required.

#### 3b — C# 14 extension properties (computed convenience layer)

C# 14's `extension` block syntax allows an extension package to add **real property
syntax** to `DeviceInfo` for computed, derived, or convenience values that build on
the first-class typed properties. These appear in IntelliSense only when the extension
package namespace is imported with `using`.

Extension properties read directly from typed `DeviceInfo` fields — they are **not** a
storage mechanism and must not read from `DeviceInfo.Properties`:

```csharp
// In Periphery.{Domain} — convenience computed properties, not storage
public static class {Domain}DeviceInfoExtensions
{
    extension(DeviceInfo device)
    {
        /// <summary>True if this entry represents an openable MIDI input port.</summary>
        public bool IsMidiInputPort
            => device.MidiPortDirection is MidiPortDirection.Input
                                        or MidiPortDirection.Bidirectional;

        /// <summary>True if this entry represents an openable MIDI output port.</summary>
        public bool IsMidiOutputPort
            => device.MidiPortDirection is MidiPortDirection.Output
                                        or MidiPortDirection.Bidirectional;
    }
}
```

This separation is deliberate: the core library owns the data; the extension package
owns the derived convenience view. A consumer who only uses the core library can still
read `device.MidiPortDirection` directly. The extension properties add ergonomic
shorthand without duplicating storage.

**Rules:**
- Extension properties must be pure computed reads over typed `DeviceInfo` properties —
  no side-effects, no I/O, no `Properties` bag access.
- Use extension properties for predicates and derived values (`IsMidiInputPort`,
  `IsVirtualPort`, `IsHighSpeedUsb`) — not for storing data the core library should own.
- Extension properties are the preferred mechanism in C# 14+ / .NET 10+ projects. For
  multi-TFM packages that must support older targets, fall back to `this`-parameter
  extension methods with the same constraints.

#### 3c — `IDeviceEnricher` (for data requiring a separate OS metadata call during enumeration)

Defined in the core library (ADR-0019). An enricher runs as a post-pass after the main
provider loop and returns an enriched `DeviceInfo` copy via a `with` expression. The
interface is always async — `Task<DeviceInfo> EnrichAsync(DeviceInfo, CancellationToken)`
— because some OS metadata reads (WMI queries, slow sysfs paths) are blocking and must
not stall the thread pool.

**`IDeviceEnricher` implementations must never open device handles or perform device I/O.**
This is a hard contract, not a guideline. An enricher that opens a handle violates the
zero-I/O snapshot invariant of `DeviceInfo` (see ADR-0026 for the full rationale and
the four problems this causes). Enrichers read only from OS metadata sources:
registry keys, sysfs attributes, IOKit property bags, WMI property bags.

```csharp
// Correct — OS-metadata enricher; no handle opened
public sealed class HidDeviceEnricher : IDeviceEnricher
{
    public bool CanEnrich(DeviceInfo device)
        => device.Category == DeviceCategory.Hid;

    public Task<DeviceInfo> EnrichAsync(DeviceInfo device, CancellationToken ct)
    {
        // Reads from OS HID property bag — no handle, no I/O
        var usagePage = ReadHidUsagePageFromOs(device.Id);
        return Task.FromResult(device with { HidUsagePage = usagePage });
    }
}
```

Enrichers populate typed `DeviceInfo` properties via `with` expressions — never the
`Properties` bag:

```csharp
// ✅ Correct — enricher sets a typed property
return device with { HidUsagePage = 0x0001 };

// ❌ Wrong — enricher stashes data in the Properties bag
return device with
{
    Properties = device.Properties.Add("Hid.UsagePage", 0x0001)
};
```

#### Handle-gated data: static snapshot helper on the Layer 1 port

Some data that is conceptually "about the device" (USB string descriptors, audio device
format lists, serial port baud rate capabilities) is not available from the OS at
enumeration time on all platforms — it requires opening a device handle to read.

This data **must not** be populated by an enricher. Instead, the Layer 1 port exposes a
**static snapshot helper** (ADR-0026 §Option D): a static method that opens a transient
handle, reads the data, closes the handle, and returns a snapshot record. The I/O cost
is explicit at the call site; `DeviceInfo` is never modified by I/O.

```csharp
// In Periphery.{Domain} — explicit, discoverable, honest about I/O cost
public sealed class {Domain}Port : IAsyncDisposable
{
    // ... normal I/O surface ...

    /// <summary>
    /// Opens a transient handle to read [snapshot data] and returns it.
    /// The handle is closed before this method returns.
    /// </summary>
    public static async Task<{Domain}Snapshot> ReadSnapshotAsync(
        DeviceInfo device, CancellationToken ct = default)
    {
        await using var port = await OpenAsync(device, ct: ct);
        return port.BuildSnapshot();
    }
}
```

The caller is unambiguous:

```csharp
// Enumerate — zero I/O cost
var devices = await Devices.Enumerate().OfCategory(DeviceCategory.Usb).ToListAsync();

// Explicit snapshot read for one device — caller knows this costs I/O
var snapshot = await UsbPort.ReadDescriptorsAsync(devices[0], ct);
```

See ADR-0026 for the full rationale, naming convention, and domain-specific examples.

---

## Structural diagram

```
Periphery (core)
  DeviceInfo ─────────────────────────────── immutable snapshot, always available
    ├─ {Domain}TypedProperty?              ← scalar domain data lives here, not in Properties
    └─ Properties                          ← array-typed or purely diagnostic data only
  DeviceTracker ──────────────────────────── lifecycle state machine
  DeviceWatcher ──────────────────────────── real-time connect/disconnect events
  IDeviceEnricher ────────────────────────── optional enumeration-time enrichment hook

Periphery.{Domain} (extension package)
  ┌─ Layer 3 (enrichment) ──────────────────────────────────────────────────────┐
  │  {Domain}DeviceEnricher  implements IDeviceEnricher                         │
  │    CanEnrich(DeviceInfo) → fast-path guard                                  │
  │    EnrichAsync(DeviceInfo, ct) → returns device with { TypedProp = value }  │
  │    ← OS metadata ONLY (registry/sysfs/IOKit/WMI) — NEVER opens handles     │
  │                                                                             │
  │  {Domain}DeviceInfoExtensions  C#14 extension block on DeviceInfo           │
  │    extension(DeviceInfo d) { public bool IsFoo => d.TypedProp == X; }       │
  │    (computed convenience layer — reads typed properties, not Properties bag)│
  │                                                                             │
  │  {Domain}Port.ReadSnapshotAsync(DeviceInfo)  ← static snapshot helper      │
  │    opens transient handle, reads handle-gated data, closes, returns record  │
  │    (ADR-0026 Option D — explicit I/O cost, not hidden in enumeration)       │
  └─────────────────────────────────────────────────────────────────────────────┘
           │ typed DeviceInfo flows down
           ▼
  ┌─ Layer 1 (I/O primitive) ───────────────────────────────────────────────────┐
  │  {Domain}Port : IAsyncDisposable                                            │
  │    static OpenAsync(DeviceInfo) → opens platform handle                     │
  │    {domain-specific I/O surface}                                            │
  └─────────────────────────────────────────────────────────────────────────────┘
           │ Port instance composed into
           ▼
  ┌─ Layer 2 (lifecycle manager) ───────────────────────────────────────────────┐
  │  {Domain}DeviceProxy : INotifyPropertyChanged, IAsyncDisposable            │
  │    ctor(DeviceTracker tracker)                                               │
  │    Port { get; }  ← null when disconnected                                  │
  │    PortOpened / PortClosed events                                            │
  └─────────────────────────────────────────────────────────────────────────────┘
```

---

## AoT and GC Constraints

These constraints apply to every extension package in the ecosystem. They are non-optional.

### NativeAOT

All P/Invoke must use `[LibraryImport]` (source-generated). `[DllImport]` with non-blittable
parameters is forbidden — it fails silently under NativeAOT publication.

Native callbacks (OS-fired, e.g. MIDI input, HID report callbacks, CGEventTap) must use
`[UnmanagedCallersOnly]` static methods. Delegate-based callbacks (`Marshal.GetFunctionPointerForDelegate`)
are forbidden — the JIT-generated stub does not exist under NativeAOT.

Context passed to native callbacks must cross the GC boundary via a `GCHandle`-pinned
pointer, not a managed object reference:

```csharp
// ✅ REQUIRED — GCHandle.Alloc pins the context; nint crosses the GC boundary safely
_contextHandle = GCHandle.Alloc(context, GCHandleType.Pinned);
nint contextPtr = GCHandle.ToIntPtr(_contextHandle);
// pass contextPtr to the native API

// ❌ FORBIDDEN — 'this' pointer is a managed reference; invalid in [UnmanagedCallersOnly]
[UnmanagedCallersOnly]
private static void Callback(nint context) {
    var self = (MyClass)GCHandle.FromIntPtr(context).Target!; // ← managed cast, valid
}
```

Every extension package CI pipeline must include a `PublishAot=true` publish step as a
gate. A library that compiles under JIT but fails under AoT publication is a regression.

### GC — the two-zone rule for real-time callbacks

**Any callback that is timing-critical must not touch managed memory in the hot path.**

The .NET GC can suspend all managed threads at any point for a collection. On a callback
thread that captures timestamps or fires time-sensitive output, a GC pause corrupts the
result. This is not mitigated by NativeAOT — AoT eliminates JIT warmup but does not
eliminate GC stop-the-world pauses.

The solution is the **two-zone architecture**:

```
GC-FREE ZONE (native OS callback thread)
  [UnmanagedCallersOnly] callback
    → capture Stopwatch.GetTimestamp()          ← timestamp before any managed code
    → pack into blittable struct               ← no heap allocation
    → write to GCHandle-pinned ring buffer     ← no GC barrier
    (never touches Channel<T> or any managed object)

MANAGED ZONE (dedicated drain thread)
  SpinWait → read from ring buffer
    → reconstruct domain object (MidiMessage, HidReport, etc.)
    → write to Channel<T>
    → consumer reads via IAsyncEnumerable<T>
```

**Apply this pattern when:**
- The callback fires at ≥ 1 kHz, OR
- The callback captures a timestamp that must be accurate to < 5 ms, OR
- The callback is on an OS real-time or multimedia thread.

**You may skip this pattern when:**
- The callback is infrequent (e.g. connect/disconnect events).
- Timestamp accuracy is not a correctness requirement.

The ring buffer entry must be a **blittable struct** (`[StructLayout(LayoutKind.Sequential)]`,
no reference-type fields). The ring buffer array must be pinned with
`GCHandle.Alloc(array, GCHandleType.Pinned)` before the first callback fires and freed
in `DisposeAsync`.

---

## Async API Shape

All public entry points in an extension package must follow these conventions:

### Streaming input

Expose an `IAsyncEnumerable<T>` property backed by a `Channel<T>`. Do not expose
`Channel<T>` directly. The channel is bounded with `DropOldest` overflow handling to
prevent unbounded growth under consumer back-pressure:

```csharp
// ✅ Correct
public IAsyncEnumerable<{DomainEvent}> Events { get; }

// ❌ Wrong — exposes Channel internals
public Channel<{DomainEvent}> EventChannel { get; }
```

`IAsyncEnumerable<T>` is populated by the managed drain thread (Zone 2 above), never
directly from the native callback.

### One-shot I/O

Expose `Task`-returning methods with a `CancellationToken` parameter. Name by the action,
not the mechanism:

```csharp
public Task SendAsync(T message, CancellationToken ct = default);
public Task<T> ReadAsync(CancellationToken ct = default);
```

### Scheduled output

When the platform supports native scheduled dispatch (ALSA seq, CoreMIDI, Windows MIDI 2.0),
translate the `TimeSpan`-relative timestamp to the platform domain and pass it to the OS.
When it does not (WinMM), use a GC-free software scheduler thread with
`CREATE_WAITABLE_TIMER_HIGH_RESOLUTION` (Win10 2004+) at `THREAD_PRIORITY_TIME_CRITICAL`.

Scheduled output overloads accept a `TimeSpan scheduledAt` parameter using the same
`Stopwatch`-relative epoch as input timestamps, so recorded events can be replayed
without conversion:

```csharp
public Task SendAsync(T message, TimeSpan scheduledAt, CancellationToken ct = default);
public Task SendSequenceAsync(
    IEnumerable<(TimeSpan ScheduledAt, T Message)> sequence,
    CancellationToken ct = default);
```

### Timestamp normalisation

All timestamps exposed to consumers are `TimeSpan` offsets from the moment the port was
opened, using `Stopwatch.GetTimestamp()` as the normalisation reference. Absolute
`DateTimeOffset` is not provided — correlating monotonic timestamps with wall clock time
introduces drift and is not necessary for sequencing, recording, or performance use cases.

---

## Exception Hierarchy

Every extension package defines a domain-specific exception type derived from
`DeviceEnumerationException`. Platform errors are wrapped — never leaked raw:

```csharp
// ✅ Correct
throw new MidiPortException(
    $"Failed to open MIDI port '{device.Name}': {ex.Message}", ex);

// ❌ Wrong — leaks platform-specific exception type to consumer
throw new COMException("...", hr);
```

The standard exception names are `{Domain}Exception` or `{Domain}PortException`. They
must carry:
- A message that includes the device name and enough context to diagnose the failure.
- The inner exception that carries the platform error code.

---

## Package naming and dependency rules

| Package | Depends on | Must not depend on |
|---|---|---|
| `Periphery` | BCL plus `Microsoft.Extensions.Logging.Abstractions` | Any extension package; any other third-party package |
| `Periphery.{Domain}` | `Periphery` | Other `Periphery.*` extension packages (unless explicit); any third-party package beyond the core's logging abstractions |
| `Periphery.{Domain}.{Platform}` | `Periphery.{Domain}` | Cross-platform code in `Periphery.{Domain}` (peer-only) |
| `Periphery.{Domain}.{Library}` | `Periphery.{Domain}` plus the one third-party library it is named for | Any second third-party library; a `runtime.*` native payload package |

Build-only references do not count against these rows.
`Microsoft.SourceLink.GitHub` and `MinVer` carry `PrivateAssets="All"`, so they
never reach a consumer's dependency graph.

`Periphery.{Domain}.{Library}` is the integration-package row. Such a package
is a leaf: nothing else under `src/` references it. Which integrations belong
here at all, and which belong in the consuming repo instead, is routed by
[`docs/patterns/integration-package-placement.md`](../patterns/integration-package-placement.md).

Platform-specific sub-packages (e.g. `Periphery.Midi.Windows.Midi2`) are used to
isolate WinRT or platform-specific dependencies behind a `[SupportedOSPlatform]` guard,
following the pattern established in ADR-0018.

The core `Periphery` library **must never gain a dependency on any extension package.**
Domain-specific scalar data that flows from an extension package's enumeration logic
back into `DeviceInfo` is stored as a **typed nullable property** on `DeviceInfo` in the
core library — not in the `Properties` bag. The `Properties` bag is reserved for
array-typed or purely diagnostic data with no natural typed home.

---

## Multi-aspect devices — the shared-token pattern

A single physical device can be meaningfully accessed through multiple, parallel OS API
surfaces simultaneously. A USB camera is both a **camera** (video frames, format
negotiation, exposure control — via Media Foundation / V4L2 / AVFoundation) and a **USB
device** (descriptor tree, vendor control transfers — via WinUSB / libusb / IOKit USB
interface vtable). A USB MIDI interface is both a **MIDI device** and a **USB device**.
A Bluetooth audio headset is both an **audio device** and a **Bluetooth device**.

The naive architectural response — have `Periphery.Camera` depend on `Periphery.Usb`
to share USB descriptor access — creates spoke-to-spoke dependency chains between
packages that should be independent peers. It also creates a semantic trap: which
`OpenAsync` factory does the caller invoke first, and who owns the handle lifetime?

### `DeviceInfo` is the shared passport

Every extension package accepts a `DeviceInfo` and opens its own OS-level handle
independently. Because the OS exposes the same physical device through multiple separate
handle types, multiple handles can coexist on the same device simultaneously with no
coordination between packages:

```csharp
// Both handles open simultaneously — they use completely separate OS API paths.
// The OS permits this; the two handle types are independent kernel objects.
await using var camera = await CameraDevice.OpenAsync(deviceInfo);
await using var usb    = await UsbDevice.OpenAsync(deviceInfo);

// Stream video via the camera stack (Media Foundation / V4L2 / AVFoundation)
await foreach (var frame in camera.CaptureAsync(ct))
    ProcessFrame(frame);

// Read firmware version via a vendor-specific USB control transfer
var fw = await usb.ControlTransferAsync(new UsbSetupPacket(...));
```

The caller composes capabilities at the call site. Extension packages are not aware of
each other. `DeviceInfo` is the shared passport — immutable, always available, never
opening a handle — that every package uses to locate the physical device.

### The star topology rule

The package dependency graph is a strict star: all spokes connect to the hub
(`Periphery` core); no spoke connects to another spoke.

```
                              Periphery (core)
                   /       /       |       \       \
          Usb    Hid    Serial   Camera    Midi    Input
```

**An extension package must never take a runtime dependency on another extension
package.** The "unless explicit" qualifier in the dependency table above is a narrow
escape hatch reserved for cases where one package provides a true sub-protocol
foundation for another (e.g. a hypothetical `Periphery.Gatt` building on
`Periphery.Bluetooth` handle infrastructure). In practice such cases should be
extremely rare; when in doubt, route the shared concern through the hub.

If you find yourself wanting a spoke-to-spoke dependency, it is a signal that the
shared concern belongs in the hub:

| Shared concern | Where it goes |
|---|---|
| Scalar metadata available at enumeration time without a handle | Typed `init` property on `DeviceInfo` in `Periphery` core |
| Computed predicate over `DeviceInfo` fields | C# 14 extension property in the relevant package |
| Data that requires an open handle | Static `ReadXxxAsync(DeviceInfo)` on the Layer 1 primitive of the relevant package |
| OS-level metadata call that runs during enumeration | `IDeviceEnricher` registered in the relevant package, populating a core `DeviceInfo` property |

### Where USB metadata actually lives

A common source of apparent cross-package dependency is the desire to access USB
descriptor data from a non-USB extension package (e.g. `Periphery.Camera` wanting
the USB interface class of the camera). Most of this concern dissolves once the
distribution of where data lives is understood:

| Data | Available without a USB handle? | Where it lives |
|---|---|---|
| VID / PID | Yes — all platforms | `DeviceInfo.VendorId`, `DeviceInfo.ProductId` |
| Manufacturer / product / serial string | Yes — all platforms | `DeviceInfo.Manufacturer`, `DeviceInfo.Name` |
| Device class / subclass / protocol | Yes — sysfs, IOKit, registry | `DeviceInfo` enrichment (core or `Periphery.Usb` enricher) |
| `bcdUSB` version | Yes — sysfs, IOKit, registry | `DeviceInfo` enrichment |
| Full config / interface / endpoint tree | **No** — requires open USB handle | `UsbPort.ReadDescriptorsAsync(DeviceInfo)` in `Periphery.Usb` |

The "95% case" for USB metadata is already available in `DeviceInfo` without a handle.
The full descriptor tree is an explicitly `Periphery.Usb` operation. If a caller needs
it alongside camera I/O, they hold both handles simultaneously — as shown above.

---

## Checklist for new extension packages

When authoring a new extension package, verify each item:

- [ ] Layer 1: `{Domain}Port` is `sealed`, `IAsyncDisposable`, static `OpenAsync` only
- [ ] Layer 2: `{Domain}DeviceProxy` accepts `DeviceTracker`, exposes `Port`, fires `PortOpened` / `PortClosed`
- [ ] Layer 3: scalar, enumeration-time domain data added as typed `init` property on `DeviceInfo` in core; data requiring a handle lives on `{Domain}Port` instead
- [ ] `IDeviceEnricher` used only for OS-enumerable metadata (registry/sysfs/IOKit/WMI); never opens device handles or performs device I/O (see ADR-0026)
- [ ] Handle-gated snapshot data exposed via a static `ReadXxxAsync(DeviceInfo)` helper on the Layer 1 port, not via an enricher (ADR-0026 Option D)
- [ ] C# 14 `extension` block computes derived predicates over typed `DeviceInfo` properties — no storage, no Properties bag access
- [ ] All P/Invoke via `[LibraryImport]`; all native callbacks via `[UnmanagedCallersOnly]`
- [ ] GCHandle-pinned context pointer for any callback that crosses the GC boundary
- [ ] Two-zone ring buffer used for any timing-critical or high-frequency callback
- [ ] `IAsyncEnumerable<T>` backed by bounded `Channel<T>` for streaming input
- [ ] `TimeSpan` timestamps relative to port-open `Stopwatch` epoch
- [ ] Domain exception type derived from `DeviceEnumerationException`
- [ ] `PublishAot=true` CI gate present
- [ ] `[SupportedOSPlatform]` guards on all platform-specific APIs
- [ ] `DisposeAsync` frees all `GCHandle`s, signals all drain threads, completes all channels
- [ ] No spoke-to-spoke package dependencies; any shared concern is routed through `Periphery` core or `DeviceInfo` typed properties (see 'Multi-aspect devices' section)

---

## Relationship to Prior ADRs

| ADR | Role |
|---|---|
| ADR-0001 | `DeviceTracker` — foundation of Layer 2 |
| ADR-0006 | `DeviceProfile` — profile-ordered resolution used by Layer 2 |
| ADR-0018 | WinRT TFM decoupling — pattern for platform sub-packages |
| ADR-0019 | `Periphery.Usb` — first definition of Layers 1 and 2; `IDeviceEnricher` |
| ADR-0020 | `Periphery.Hid` — first validation of the pattern; enricher with OS metadata |
| ADR-0021 | `DeviceCategory.Midi` — first use of C# 14 extension properties for enrichment |
| ADR-0022 | `Periphery.Input` — two-zone architecture for keyboard/mouse callbacks |
| ADR-0023 | `Periphery.Midi` — two-zone ring buffer; scheduled output; full AoT constraint spec |
| ADR-0028 | `Periphery.Serial` — first application of the star topology rule (deferred) |

---

## Consequences

### Positive

- **POS-001**: Future extension packages have a concrete checklist and canonical shapes to
  follow. Architecture review reduces to "does it match the pattern?" rather than
  re-litigating the same questions.
- **POS-002**: The pattern has been validated across four independent packages on three
  platforms. Deviations will be immediately visible.
- **POS-003**: Domain-specific typed properties on `DeviceInfo` provide compile-time type
  safety, record equality, JSON serialisation, and IntelliSense completeness — none of
  which the `Properties` bag provides. The cost of a core library PR is the right
  tradeoff for scalar, well-typed domain data.
- **POS-004**: C# 14 extension properties in extension packages provide ergonomic
  computed predicates (`IsMidiInputPort`, `IsHighSpeedUsb`) gated on `using`, layered
  cleanly over typed `DeviceInfo` properties that are always available in the core.
- **POS-005**: The two-zone GC constraint is captured once here and referenced by all
  timing-critical extension packages, rather than rediscovered independently.
- **POS-006**: The star topology rule and `DeviceInfo`-as-shared-token pattern eliminate
  spoke-to-spoke dependencies between extension packages. A USB camera, HID device, or
  MIDI interface that is also a USB device can be accessed via both `CameraDevice` and
  `UsbDevice` simultaneously — the caller composes at the call site, not at the library
  design level.

### Negative

- **NEG-001**: The pattern mandates `sealed` I/O primitives and lifecycle managers. Consumers
  who want to mock or subclass for testing must use wrapper types or interfaces. A
  `I{Domain}Port` interface is not part of the canonical shape but is not prohibited.
- **NEG-002**: The two-zone ring buffer adds implementation complexity to any extension
  package with real-time callbacks. Packages that do not have timing-critical callbacks
  (e.g. a hypothetical `Periphery.Printer`) may find the pattern over-engineered for their
  needs. The GC constraint section defines explicitly when the ring buffer is required.
- **NEG-003**: `[LibraryImport]` and `[UnmanagedCallersOnly]` are more verbose than legacy
  P/Invoke. This is a permanent cost of AoT correctness.

---

## Alternatives Considered

### A — Per-package pattern, no generalisation

The status quo before this ADR. Each package invented its own shape, with informal
convergence via code review. Rejected because the convergence is now strong enough to
be worth making explicit, and the cost of an inconsistent shape in a future package
(wrong exception hierarchy, GC-unsafe callback, missing `DisposeAsync` drain) is high.

### B — A base class for Layer 1 and Layer 2

Considered a `DevicePortBase` and `DeviceProxyBase` abstract base class in the core
library. Rejected: the core library must not know about I/O; abstract base classes in
the core would introduce a dependency inversion that contradicts the zero-dependency
constraint. The pattern is better expressed as a documented contract than as
inheritance machinery.

### C — Source generator for the boilerplate

A Roslyn source generator that produces Layer 1 and Layer 2 skeletons from an attribute.
Not rejected, but deferred. The pattern is not yet stable enough across all packages
for a generator to be worth the complexity. Once `Periphery.Usb`, `Periphery.Hid`,
`Periphery.Midi`, and `Periphery.Input` are all shipped and production-tested, a
generator for the lifecycle boilerplate becomes a reasonable investment.

### D — Store domain data in `DeviceInfo.Properties` and wrap with C# 14 extension properties

Initially proposed as a way to avoid core library PRs entirely. Under this approach,
the extension package's enricher would stash domain data in the `Properties` bag
under a namespaced string key, and C# 14 extension properties would wrap the bag
access with type safety.

Rejected. The `Properties` bag is documented as intentionally narrow — it exists for
array-typed and diagnostic data with no natural typed home. The existing `DeviceInfo`
code already promotes scalar domain data to typed properties: `UsbSpeed`, `UsbClassCode`,
`PortName`, `BatteryChargePercent`, `DisplayResolution` are all category-specific and
null elsewhere. Routing equivalent data through a stringly-typed `object?` dictionary:
- Loses compile-time type safety (cast at every read site)
- Breaks record structural equality (dictionary equality is reference-based)
- Breaks JSON serialisation (no typed converter)
- Is invisible to IntelliSense on `DeviceInfo` without `using` the extension package
- Requires an enricher pass to be populated at all, meaning the value can be `null`
  not because the device lacks the property but because the enricher didn't run

The C# 14 extension properties remain the right mechanism for *computed predicates*
(`IsMidiInputPort`, `IsHighSpeedUsb`) that derive from typed properties. They are not
a substitute for typed properties themselves.

---

## Open Questions

- **OQ-001**: ~~Should the core library ship a `DeviceProxyBase<TPort>` generic helper in
  `Periphery` that provides the `StateChanged` subscription, the `Port` property
  null-transition, and the `PortOpened`/`PortClosed` events?~~ **Resolved — see ADR-0027.**
  The generic base class is adopted. It carries no I/O surface and keeps the core library
  independent of extension package concerns.

- **OQ-002**: ~~Should `IDeviceEnricher` be async or sync?~~ **Resolved.** The interface
  stays `Task<DeviceInfo> EnrichAsync(DeviceInfo, CancellationToken)`. The async signature
  is required to support I/O-gated enrichers (3c sub-kind B) that must open a transient
  device handle. OS-metadata enrichers that are synchronous in practice simply return
  `Task.FromResult(...)` — the allocation is negligible relative to the OS API call they
  wrap. A sync fast-path override is not worth the interface complexity.

- **OQ-003**: ~~Should extension packages be allowed to add entries to `DeviceCategory`
  without a core library PR?~~ **Resolved — see ADR-0025.** Extension packages declare
  their category values as `const DeviceCategory` casts into a documented extension range
  (≥ 1000) and register their OS mappings via `DeviceCategoryRegistry` in a
  `[ModuleInitializer]`. This gives consumers a real `DeviceCategory.CanBus` value —
  not a string, not a wrapper — with zero manual registration. The category maps in the
  three platform providers consult the registry in their default arm before throwing, so
  the exhaustiveness safety net for unregistered values is preserved. See ADR-0025 for
  the full design, value-collision policy, and implementation checklist.

- **OQ-004**: Should the core library expose a `Devices.FindParentAsync(DeviceInfo,
  CancellationToken)` helper? The multi-aspect device pattern (ADR-0024 §"Multi-aspect
  devices") requires navigating from a specialised child node (e.g. a Camera or HID
  interface) up to its USB composite device parent before calling
  `UsbDevice.OpenAsync`. `DeviceInfo.ParentId` already carries the parent's platform ID,
  but retrieving the parent `DeviceInfo` currently requires a second full enumeration
  call filtered by ID — which is clunky and re-enumerates the entire device list. A
  dedicated `FindParentAsync` in core would also have the opportunity to use a cheaper
  OS-level single-node lookup (e.g. `CM_Get_Parent` on Windows, direct sysfs path read
  on Linux, `IORegistryEntryGetParentEntry` on macOS) rather than a full scan.
  Counterargument: parent navigation is an advanced use case; the added API surface may
  not be worth it for the common path. Defer until `Periphery.Usb` is implemented and
  the real-world call pattern is known.

- **OQ-005**: Should `DeviceFilter` gain a `WithContainerId(Guid)` method? On Windows,
  `DeviceInfo.ContainerId` groups every interface of the same physical composite device
  under a single GUID — a USB headset that presents both an audio device and an HID
  device will have matching `ContainerId` values on both `DeviceInfo` records. A
  `WithContainerId` filter would let callers find all software interfaces belonging to
  the same physical device without traversing the parent-child tree. The limitation is
  platform scope: `ContainerId` is a Windows SetupAPI concept; Linux and macOS populate
  `ContainerId` as `null`. A `WithContainerId` filter would silently match nothing on
  those platforms, which is surprising. Options: (a) add the filter with a prominent
  XML doc warning about platform scope; (b) add a cross-platform `FindSiblingsAsync`
  helper in core that uses `ContainerId` on Windows and `ParentId` traversal on
  Linux/macOS; (c) defer to `Periphery.Usb`, which is the most likely consumer.
  Defer until `Periphery.Usb` is implemented and the ergonomic need is confirmed.
