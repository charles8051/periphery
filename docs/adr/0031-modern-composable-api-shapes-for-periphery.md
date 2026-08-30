---
title: "ADR-0031: Modern Composable API Shapes for Periphery"
status: "Proposed"
status_note: "Still open as written. Parts landed under other decisions: DEC-004 as `DeviceTrackerResolution` / `DeviceTrackerState`, DEC-008 as `WithTag` ([ADR-0047](0047-device-tags-vs-multi-category.md)), DEC-005 as three-valued `DeviceActivityStatus` ([ADR-0056](0056-device-activity-unknown-initial-state.md), [ADR-0073](0073-observations-not-verdicts.md)), DEC-006 on the topology trunk ([ADR-0079](0079-port-path-is-a-parsed-value.md), [ADR-0080](0080-ancestor-walking-is-one-fold.md)). The `DeviceChange` record, typed facet views, and query-level projections were not built."
date: ""
authors: ""
tags: ["architecture", "decision"]
supersedes: ""
superseded_by: ""
---

# ADR-0031: Modern Composable API Shapes for Periphery

## Context

Periphery already has a strong architectural foundation:

- discovery only
- async-first APIs
- LINQ composability
- immutable `DeviceInfo` snapshots
- explicit separation between enumeration, watching, tracking, and reconnect-oriented device handles
- platform-specific provider abstractions hidden beneath a platform-agnostic public API

This architecture is already modern in its fundamentals. The next opportunity is not to abandon it, but to make the public surface more expressive, more typed, more pipeline-friendly, and more discoverable for both application authors and extension-package authors.

Several current characteristics create room for a second wave of ergonomic design:

1. `DeviceInfo` is broad and useful, but category-specific workflows still require callers to mentally filter a large property bag of nullable fields.
2. `DeviceWatcher` is event-friendly, but modern headless/service code often composes more naturally with `IAsyncEnumerable<T>` than with event subscription.
3. `DeviceTracker` has rich semantics (`Device`, `PresentDevice`, `IsConnected`, `IsPresent`, `IsAmbiguous`, `AmbiguousDevices`) that could be made more explicit and pattern-match-friendly.
4. Topology information exists in fields such as `ParentId`, `ContainerId`, and `PortNumber`, but the API surface does not yet elevate topology into a navigable public model.
5. Cross-platform honesty around properties such as `IsConnected` would benefit from explicit confidence signaling rather than documentation remarks alone.
6. The `Properties` bag is a good escape hatch, but advanced callers and extension packages would benefit from more typed platform-specific views.

The goal of this ADR is to outline a set of additive, composition-oriented public API shapes that make Periphery feel more modern and more expressive while preserving the architecture documented in `docs/ARCHITECTURE.md`.

## Decision

Adopt a set of additive API patterns focused on typed views, async streams, resolution unions, topology composition, and explicit confidence modeling.

### DEC-001: Add a first-class `DeviceChange` model and async-stream watch surface

Introduce a public record representing device changes over time:

```csharp
public enum DeviceChangeKind
{
    Appeared,
    Disappeared,
    Connected,
    Disconnected,
    Updated
}

public sealed record DeviceChange(
    DeviceChangeKind Kind,
    DeviceInfo Current,
    DeviceInfo? Previous = null);
```

Add an async-stream-oriented watch API alongside the existing event model:

```csharp
await foreach (var change in Devices.Watch()
    .OfCategory(DeviceCategory.Usb)
    .Changes(ct))
{
    Console.WriteLine($"{change.Kind}: {change.Current.Name}");
}
```

This API complements events rather than replacing them. Events remain appropriate for UI-style integration and existing consumers. `IAsyncEnumerable<DeviceChange>` becomes the modern composition surface for service code, worker processes, pipelines, and extension packages.

### DEC-002: Add typed device facet/view projections over `DeviceInfo`

Preserve `DeviceInfo` as the canonical immutable snapshot, but add category-specific projected views that expose only semantically relevant properties.

Example:

```csharp
public sealed record UsbDeviceInfo(DeviceInfo Device)
{
    public string Id => Device.Id;
    public string? Name => Device.Name;
    public HardwareId? VendorId => Device.VendorId;
    public HardwareId? ProductId => Device.ProductId;
    public UsbSpeed? UsbSpeed => Device.UsbSpeed;
    public UsbClassCode? UsbClassCode => Device.UsbClassCode;
    public int? MaxPowerMilliamps => Device.MaxPowerMilliamps;
}

public sealed record NetworkAdapterInfo(DeviceInfo Device)
{
    public string Id => Device.Id;
    public string? Name => Device.Name;
    public PhysicalAddress? MacAddress => Device.MacAddress;
    public ImmutableArray<IPAddress>? IPAddresses => Device.IPAddresses;
    public IPNetwork? Network => Device.Network;
}
```

Add projection helpers:

```csharp
public static class DeviceInfoExtensions
{
    public static UsbDeviceInfo? AsUsb(this DeviceInfo device)
        => device.Category == DeviceCategory.Usb ? new UsbDeviceInfo(device) : null;

    public static NetworkAdapterInfo? AsNetworkAdapter(this DeviceInfo device)
        => device.Category == DeviceCategory.Network ? new NetworkAdapterInfo(device) : null;
}
```

Usage:

```csharp
var usb = device.AsUsb();
if (usb is not null)
{
    Console.WriteLine($"VID={usb.VendorId} PID={usb.ProductId}");
}
```

### DEC-003: Add query-level typed projection helpers

Allow the same narrowing pattern to apply directly on `DeviceQuery` and watcher/query pipelines.

Example:

```csharp
var usbDevices = await Devices.Enumerate()
    .OfCategory(DeviceCategory.Usb)
    .AsUsbDevices()
    .Where(d => d.VendorId == HardwareId.Parse("046D"))
    .ToListAsync();
```

And:

```csharp
var adapters = await Devices.Enumerate()
    .OfCategory(DeviceCategory.Network)
    .AsNetworkAdapters()
    .Where(a => a.MacAddress is not null)
    .ToListAsync();
```

These helpers remain projections over `DeviceInfo`; they do not introduce category-specific providers or fragment the public model.

### DEC-004: Introduce a discriminated-union-style tracker resolution model

Keep the existing convenience booleans and properties, but add a first-class union-like result that makes tracker state explicit and pattern-match-friendly.

Example:

```csharp
public abstract record DeviceResolution;

public sealed record NoMatch : DeviceResolution;
public sealed record UniqueMatch(DeviceInfo Device) : DeviceResolution;
public sealed record AmbiguousMatch(IReadOnlyList<DeviceInfo> Candidates) : DeviceResolution;
public sealed record MatchedButDisconnected(DeviceInfo Device) : DeviceResolution;
```

`DeviceTracker` gains:

```csharp
public DeviceResolution Resolution { get; }
```

Usage:

```csharp
switch (tracker.Resolution)
{
    case UniqueMatch(var device):
        Console.WriteLine($"Resolved: {device.Name}");
        break;

    case AmbiguousMatch(var candidates):
        Console.WriteLine($"Ambiguous: {candidates.Count} devices matched.");
        break;

    case MatchedButDisconnected(var device):
        Console.WriteLine($"Known device is present but disconnected: {device.Name}");
        break;

    case NoMatch:
        Console.WriteLine("No device matched.");
        break;
}
```

This reduces the semantic burden on the caller to infer meaning from several correlated booleans.

### DEC-005: Add explicit connection-confidence metadata

Where Periphery exposes `IsConnected`, also expose how trustworthy that determination is on the current platform/category.

Example:

```csharp
public enum Confidence
{
    Unknown,
    Heuristic,
    Definitive
}
```

Add to `DeviceInfo`:

```csharp
public Confidence ConnectionConfidence { get; init; }
```

Usage:

```csharp
if (device.IsConnected && device.ConnectionConfidence == Confidence.Definitive)
{
    Console.WriteLine("Device is definitely connected.");
}
else if (device.IsConnected)
{
    Console.WriteLine("Device appears connected, but the signal is heuristic.");
}
```

This makes cross-platform honesty a first-class design feature rather than a documentation footnote.

### DEC-006: Elevate topology into a composable public model

Build on `ParentId`, `ContainerId`, and `PortNumber` by adding optional topology helpers and a public topology snapshot model.

Example:

```csharp
public sealed record DeviceNode(
    DeviceInfo Device,
    DeviceNode? Parent,
    IReadOnlyList<DeviceNode> Children);
```

Possible usage:

```csharp
var topology = await Devices.GetTopologyAsync(ct);

foreach (var root in topology.Roots)
{
    Print(root, depth: 0);
}

static void Print(DeviceNode node, int depth)
{
    Console.WriteLine($"{new string(' ', depth * 2)}- {node.Device.Name}");
    foreach (var child in node.Children)
    {
        Print(child, depth + 1);
    }
}
```

And per-device navigation helpers:

```csharp
var parent = await device.GetParentAsync(ct);
var children = await device.GetChildrenAsync(ct);
```

This improves diagnostic scenarios, hardware tree visualization, hub/port workflows, and extension-package composition.

### DEC-007: Add typed platform-detail views in addition to `Properties`

Retain `Properties` as an escape hatch, but provide typed platform-specific detail shapes for callers who need strongly typed access to platform extras.

Example:

```csharp
public abstract record PlatformDeviceDetails;

public sealed record WindowsDeviceDetails(
    string? PnpDeviceId,
    string? ClassName,
    uint? RawStatus) : PlatformDeviceDetails;

public sealed record LinuxDeviceDetails(
    string? Subsystem,
    string? DevPath,
    string? DevName) : PlatformDeviceDetails;

public sealed record MacOsDeviceDetails(
    string? IOServiceClass,
    string? IORegistryEntryPath,
    string? IOObjectClass) : PlatformDeviceDetails;
```

Usage:

```csharp
switch (device.PlatformDetails)
{
    case WindowsDeviceDetails(var pnpId, var className, _):
        Console.WriteLine($"Windows PnP ID: {pnpId}, class: {className}");
        break;

    case LinuxDeviceDetails(var subsystem, var devPath, _):
        Console.WriteLine($"Linux subsystem: {subsystem}, path: {devPath}");
        break;
}
```

This should coexist with `WellKnownProperties`, not necessarily replace it.

### DEC-008: Add semantic/capability-oriented filters

Preserve the current field-oriented filter pipeline, but add intent-level helpers for common semantic queries.

Example:

```csharp
var removableStorage = await Devices.Enumerate()
    .IsRemovableStorage()
    .IsPhysicallyAttached()
    .ToListAsync();
```

And:

```csharp
var externalDisplays = await Devices.Enumerate()
    .HasCapability(DeviceCapability.ExternalDisplay)
    .ToListAsync();
```

These helpers compile down to ordinary `DeviceFilter` predicates and preserve the authoritative in-memory filtering model.

### DEC-009: Add scenario entry points as veneers over category filters

Keep the existing generic `Devices` API, but add discoverable scenario entry points that simply preconfigure category filters and projections.

Example:

```csharp
var displays = await Devices.Displays()
    .Where(d => d.IsConnected)
    .ToListAsync();

var networkAdapters = await Devices.NetworkAdapters()
    .ToListAsync();
```

And:

```csharp
var keyboards = await Devices.Input()
    .Where(d => d.Category == DeviceCategory.Keyboard)
    .ToListAsync();
```

These are discoverability helpers, not a new architectural layer.

## Rationale

### 1. Typed projections reduce nullable-field fatigue

`DeviceInfo` should remain the canonical snapshot model because it is stable, immutable, and platform-agnostic. But callers often work in category-specific workflows. Typed views let the API communicate intent and surface only the properties that matter in a given scenario.

### 2. Async streams are a natural fit for watch semantics

Periphery already embraces `Task` and `IAsyncEnumerable<T>` for asynchronous, composable APIs. Device monitoring is inherently stream-shaped. A stream of `DeviceChange` records is a better fit for worker services, diagnostics pipelines, and extension packages than events alone.

### 3. Tracker semantics deserve a first-class shape

`DeviceTracker` currently exposes meaningful state, but that meaning is spread across several related members. A discriminated-union-style `Resolution` makes the API more explicit, more pattern-match-friendly, and more self-documenting.

### 4. Cross-platform honesty should be modeled, not merely documented

Different platforms and categories provide different quality signals for connection state. Explicit confidence metadata communicates that reality honestly and productively.

### 5. Topology is already present in the data model and should become navigable

The library already knows enough to support higher-level topology composition. Exposing this as a proper model multiplies the value of the existing fields.

### 6. Typed platform detail views improve extension-package authoring

Extension packages and diagnostics tooling often need more than the cross-platform abstraction, but raw string dictionaries are poor IntelliSense and poor documentation. Typed views give advanced callers a stronger foundation without weakening the main API.

## Consequences

### Positive

- **POS-001**: The API becomes more expressive without abandoning its existing architecture.
- **POS-002**: Extension packages gain clearer and more type-safe composition points.
- **POS-003**: Monitoring workflows become easier to compose in background services and pipeline-oriented code.
- **POS-004**: Tracker semantics become more obvious and harder to misuse.
- **POS-005**: Cross-platform reliability differences become visible and honest.
- **POS-006**: Topology and category-specific workflows become much more discoverable.

### Negative

- **NEG-001**: The public surface area grows and requires careful naming discipline.
- **NEG-002**: Multiple access styles (generic snapshot, typed view, scenario entry point, stream, events) increase documentation burden.
- **NEG-003**: Typed view proliferation must be managed carefully to avoid a fragmented API.
- **NEG-004**: Topology helpers may require snapshot materialization and indexing work that must be implemented efficiently.

## Alternatives Considered

### Keep the API exactly as-is and rely on documentation/examples

- **ALT-001**: Preserve the current API and communicate advanced usage through samples only.
- **ALT-002**: Rejected because many of these ideas are really API-shape improvements, not just usage guidance.

### Split `DeviceInfo` into an inheritance hierarchy

- **ALT-003**: Introduce `UsbDeviceInfo : DeviceInfo`, `NetworkDeviceInfo : DeviceInfo`, and similar subclasses.
- **ALT-004**: Rejected because Periphery's current immutable snapshot model is composition-friendly and cross-platform. Projection/wrapper views preserve that strength without forcing inheritance into the core model.

### Replace events entirely with async streams

- **ALT-005**: Remove event-based watching and standardize solely on `IAsyncEnumerable<DeviceChange>`.
- **ALT-006**: Rejected because events remain useful and familiar for some consumers, especially UI and lightweight scenarios.

## Implementation Notes

- **IMP-001**: `DeviceChange` should unify existing connection/disconnection and future property-change signaling.
- **IMP-002**: Typed device views should be thin wrappers over `DeviceInfo`, not duplicated mutable state.
- **IMP-003**: Query projection helpers should remain LINQ-friendly and avoid introducing category-specific query engines.
- **IMP-004**: `Resolution` should be derived from the same internal tracker state already used to populate `Device`, `PresentDevice`, and ambiguity flags.
- **IMP-005**: `ConnectionConfidence` should be populated by providers using the same platform/category heuristics discussed in `docs/ARCHITECTURE.md`.
- **IMP-006**: Topology helpers should be additive and should avoid forcing all consumers to materialize a tree if they only need flat queries.
- **IMP-007**: Typed platform details should complement, not replace, `Properties` and `WellKnownProperties`.

## References

- [docs/ARCHITECTURE.md](../ARCHITECTURE.md)
- [ADR-0002](0002-device-tree-topology.md)
- [ADR-0005](0005-property-change-events.md)
- [ADR-0006](0006-device-profile-single-device-resolution.md)
- [ADR-0012](0012-state-change-and-property-change-events.md)
- [ADR-0024](0024-extension-package-pattern.md)
- [ADR-0025](0025-extensible-device-category.md)
- [ADR-0027](0027-device-handle-base-class.md)
- [ADR-0029](0029-devicetracker-edge-events.md)
- [ADR-0030](0030-application-level-reconnect.md)