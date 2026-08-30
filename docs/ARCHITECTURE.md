# Periphery — Architecture

This document describes the high-level architecture of Periphery, the rationale behind key design decisions, and guidance for contributors adding new platforms or device categories.

> **Architecture reviews & explorations.** Point-in-time deepening surveys live in
> [`docs/explorations/`](explorations/). The whole-codebase
> [architecture deepening review (2026-06)](explorations/architecture-deepening-review-2026-06.md)
> — module/interface/depth/seam analysis across the `src/` projects as they stood in
> June 2026 (25 then, 28 today), two axes
> (architecture + functional-core/imperative-shell standards), judged against the
> Treehopper pure core ([ADR-0052](adr/0052-periphery-treehopper-pure-core.md)) — has a
> visual [HTML companion](explorations/architecture-deepening-review-2026-06.html).

---

## 1. Guiding Constraints

| Constraint | Rationale |
|---|---|
| Discovery-only **core** | The core library stays enumeration-only — it reports what's connected, not how to talk to it. Protocol-level I/O (camera frame capture, HID reports, serial, …) lives in companion extension libraries (`Periphery.Camera`, `Periphery.Hid`, …) layered on the core's device model. Keeping the core I/O-free holds its scope tight and its runtime dependency surface to a single abstractions package. |
| No third-party runtime deps in the core or the I/O extensions | Minimise supply-chain risk and deployment friction. `Microsoft.Extensions.Logging.Abstractions` is the one exception. An opt-in integration package takes exactly the library it is named for; see [`docs/patterns/integration-package-placement.md`](patterns/integration-package-placement.md). |
| Async-first public API | Hardware enumeration involves I/O waits; callers should never block. |
| LINQ composability | Device queries should feel like querying any other .NET collection. |
| Platform parity at the abstraction layer | A device category is only added to the public API when it can be meaningfully supported on all target platforms. Platform providers can ship incrementally. |

---

## 2. Layered Architecture

```
┌─────────────────────────────────────────────────┐
│              Consumer Application                │
├─────────────────────────────────────────────────┤
│                  Public API                      │
│   Devices.Enumerate()  ·  Devices.Watch()        │
│   DeviceQuery  ·  DeviceInfo  ·  Events          │
├─────────────────────────────────────────────────┤
│              Abstraction Layer                    │
│   IDeviceProvider  ·  IDeviceMonitorProvider      │
│   DeviceCategory   ·  DeviceFilter                │
├────────────┬────────────┬───────────────────────┤
│  Windows   │   Linux    │   macOS               │
│  Provider  │   Provider │   Provider            │
│  (SetupAPI / │   (udev /  │   (IOKit /            │
│  cfgmgr32) │   netlink) │   Notification Ports) │
└────────────┴────────────┴───────────────────────┘
```

### 2.1 Public API Layer

The top-level entry point is a static `Devices` class exposing:

```csharp
// Snapshot query — lazy IAsyncEnumerable, materialised on demand
var result = await Devices.Enumerate()
    .OfCategory(DeviceCategory.Usb)
    .WithName("Keyboard")
    .ToListAsync();

// Category is just another filter; there is no Find / FindAsync overload
var mice = await Devices.Enumerate()
    .OfCategory(DeviceCategory.Hid)
    .WithName("Mouse")
    .ByManufacturer("Logitech")
    .ToListAsync();

// Continuous monitoring
await using var watcher = Devices.Watch()
    .OfCategory(DeviceCategory.Bluetooth);
watcher.Activated += (_, e) => Console.WriteLine(e.Device.Name);
await watcher.StartAsync();

// Per-device tracking (see ADR-0001, ADR-0006)
await using var watcher = Devices.Watch();
var mouse   = watcher.AddTracker(t => t.OfCategory(DeviceCategory.Usb).WithUsbId("046D", "C52B"));
var airpods = watcher.AddTracker(t => t.OfCategory(DeviceCategory.Bluetooth).WithName("AirPods"));
await watcher.StartAsync();
Console.WriteLine($"present={mouse.IsPresent} status={mouse.ActivityStatus}");
```

`DeviceQuery` implements `IAsyncEnumerable<DeviceInfo>` and exposes LINQ-style fluent methods. All filters — structured properties and arbitrary lambdas alike — are evaluated **in-memory** by `DeviceFilter.Matches()`. Platform providers *may* inspect the filter’s structured properties (category, name, manufacturer, USB VID/PID) to narrow the OS-level query as a performance hint, but correctness never depends on this. The authoritative filtering always happens in `Matches()`.

### 2.2 Abstraction Layer

Two core provider interfaces decouple the public API from platform specifics:

Both are **public** — implement them to inject a fake device list or synthesise
device events in tests, and pass your implementation to the `DeviceQuery` /
`DeviceWatcher` constructors.

```csharp
public interface IDeviceProvider
{
    /// Enumerate devices. The provider receives a DeviceFilter and may
    /// inspect its structured properties (Category, NameContains, etc.)
    /// to narrow the OS query. All results are re-filtered in-memory
    /// by DeviceFilter.Matches() — provider push-down is optional.
    IAsyncEnumerable<DeviceInfo> EnumerateAsync(
        DeviceFilter filter,
        CancellationToken ct = default);
}

public interface IDeviceMonitorProvider : IAsyncDisposable
{
    Task StartAsync(DeviceFilter filter, CancellationToken ct = default);

    // Presence in the OS device tree — install, pair, uninstall, unpair.
    event EventHandler<DeviceChangeEventArgs>? DeviceAppeared;
    event EventHandler<DeviceChangeEventArgs>? DeviceDisappeared;

    // Physical activity — driver started/stopped, Bluetooth in/out of range.
    event EventHandler<DeviceChangeEventArgs>? DeviceActivated;
    event EventHandler<DeviceChangeEventArgs>? DeviceDeactivated;

    // Property mutation on an existing device; carries previous + current snapshots.
    event EventHandler<DeviceModificationEventArgs>? DevicePropertyChanged;
}
```

Presence and activity are **orthogonal** (ADR-0004). A device can be present but
inactive (installed, driver stopped) or activate without a separate appearance
event. `DeviceWatcher` surfaces the same four transitions as `Appeared` /
`Disappeared` / `Activated` / `Deactivated`, plus `PropertyChanged`.

A `DeviceFilter` carries **structured properties** (category, name, manufacturer, USB IDs) and **convenience methods** that compose typed lambda predicates for common `DeviceInfo` fields (serial number, bus type, drive type, MAC address, display resolution, etc.). The `Matches()` method is the single source of truth — it evaluates every filter in-memory, making correctness independent of what any provider does or does not optimise.

A `DeviceTracker` holds one or more priority-ordered `DeviceProfile`s and exposes a single resolved `Device?` reference — the first profile with exactly one active device wins. A per-profile soft latch (keyed on `DeviceInfo.Id`) prevents a second matching device from disturbing a resolved tracker while the first remains active; the latch releases automatically on deactivation. `ActiveProfile` identifies which profile resolved.

Tracker state is a **three-valued observation, not a boolean verdict** (ADR-0056, ADR-0073). `ActivityStatus` is `Unknown` / `Absent` / `Present` / `Active`; a freshly constructed tracker reads `Unknown` until initial enumeration settles, which is distinct from `Absent` ("enumerated and confirmed gone"). `IsPresent` and `IsActive` are convenience projections over it. Resolution itself is a pure value transform — `DeviceTrackerResolution` folds `Apply*` events into a `DeviceTrackerState`, with the tracker shell owning subscriptions and event dispatch.

Trackers implement `INotifyPropertyChanged`, `IObservable<DeviceTrackerState>`, edge events (`Appeared` / `Disappeared` / `Activated` / `Deactivated`, ADR-0029), and a `StateChanged` event. `Reconfigure` and `ReplaceProfiles` change what a tracker matches at runtime (ADR-0046). Trackers are long-lived — event subscribers and Rx observers survive watcher disposal, and trackers can be re-attached to new watchers. `MultiDeviceTracker` is the set-valued sibling, added via `AddMultiTracker`. See [ADR-0006](adr/0006-device-profile-single-device-resolution.md) for design rationale (supersedes ADR-0001 §3–§6).

#### Layer 2: Device Handles (reconnect-resilient lifecycle)

`DeviceProxyBase<TDevice, TException>` is an abstract base class (in the core `Periphery` assembly) that owns the reconnect state machine shared by all device handles. It composes a `DeviceTracker` + `DeviceWatcher` internally, guards mutable state behind a `SemaphoreSlim`, manages a per-connection `CancellationTokenSource`, and exposes `IsOpen` / `INotifyPropertyChanged` / `DeviceOpened` / `DeviceClosed` / `OpenFailed` consistently. Derived classes override three hooks:

| Hook | When | Inside lock? |
|---|---|---|
| `OpenDeviceAsync` (abstract) | Open the platform device from a `DeviceInfo` snapshot | Yes |
| `OnActivatedAsync` (virtual) | Init gate — runs before `IsOpen` becomes `true`; throw to abort | Yes |
| `OnDeactivatedAsync` (virtual) | Teardown — runs during close, before device disposal | Yes |

Three types cover the consumer profiles:

| Type | Assembly | Purpose |
|---|---|---|
| `DeviceProxyBase<TDevice, TException>` | `Periphery` | Abstract base for **extension packages** that need typed exceptions, custom device types, and sealed leaf classes (`HidDeviceProxy`, `UsbDeviceProxy`, `MonitorDeviceProxy`). |
| `DeviceProxy<TDevice>` | `Periphery` | Sealed, delegate-configured handle for **application code** with a disposable device type — no derived class needed. Inherits `DeviceProxyBase<TDevice, Exception>`. |
| `DeviceProxy` | `Periphery` | Non-generic, delegate-configured handle for **application code** that manages its own resources in closures. Owns its own lightweight state machine — no `TDevice` or `IAsyncDisposable` wrapper required. |

**Factory methods — owned vs. shared watcher:**

All handle types expose two factory shapes:

| Factory | Watcher ownership | Use case |
|---|---|---|
| `OpenAsync(DeviceProfile, ...)` | Handle creates and owns its own `DeviceTracker` + `DeviceWatcher`. Watcher disposed on handle disposal. | Simple single-device scenarios. |
| `Create(DeviceTracker, ...)` | Borrows a caller-owned tracker already attached to an external `DeviceWatcher`. Handle does not dispose the watcher. | Shared-watcher scenarios — one watcher powers multiple devices to reduce system calls. Checks `tracker.IsActive` on construction to handle already-active trackers. |

See [ADR-0027](adr/0027-device-handle-base-class.md) for full design rationale.

### 2.3 Platform Provider Layer

Each provider lives in its own platform-conditional source file (or project, if the dependency graph demands it). Providers are resolved at runtime via `OperatingSystem.IsWindows()` / `IsLinux()` / `IsMacOS()`.

#### Windows Provider (current)

| Component | Role |
|---|---|
| `DevNodeHelper` | Core P/Invoke infrastructure wrapping `setupapi.dll` and `cfgmgr32.dll`. Handles device enumeration via `SetupDiGetClassDevs`, property retrieval via `CM_Get_DevNode_Property`, and device-node status flags (`DN_STARTED`, `DN_DEVICE_DISCONNECTED`) to determine physical presence. |
| `WindowsDeviceMonitorProvider` | Orchestrates `CM_Register_Notification` callbacks and snapshot queries; manages a `ConcurrentDictionary` of known devices; raises the appear/disappear/activate/deactivate/property-changed events. Also hosts the message-only window that receives `WM_DISPLAYCHANGE` (ADR-0066). |
| `DeviceClassGuids` / `WindowsCategoryMap` | Map well-known Windows device-setup class GUIDs to categories and human-readable names. |
| `DisplayConfigInterop` / `WindowsDisplayConfigEnricher` / `WindowsEdidEnricher` | Monitor identity and geometry via DisplayConfig, with a registry EDID fallback when DisplayConfig returns zero paths (ADR-0044, ADR-0064, ADR-0068). |
| `DevNodeHelper.Reset` / `WindowsDeviceReset` | Reset rungs behind `IDeviceReset` (ADR-0060, ADR-0075). |
| `Windows*Enricher` | Per-category enrichment (battery, network, ports, storage, monitor) run inside the enrichment pipeline. |

**Query flow:**

```
Devices.Enumerate().OfCategory(Usb)
  → WindowsDeviceProvider.EnumerateAsync()
    → SetupDiGetClassDevs(Usb GUID, DIGCF_PRESENT)
      → for each device: CM_Get_DevNode_Property(DEVPROPKEY)
        → DevNodeHelper.IsDeviceConnected()
          → yield DeviceInfo
```

**Monitor flow:**

```
Devices.Watch().OfCategory(Usb)
  → WindowsDeviceMonitorProvider (category filters)
    → StartAsync()
      1. Register CM_Register_Notification callback for device interface changes
      2. Snapshot currently-connected devices (fires DeviceConnected for each)
      Registration-then-snapshot ordering ensures no events are lost during the snapshot window.
```

#### Linux Provider (implemented — ADR-0010)

- **Enumeration:** `libudev.so.1` via `[LibraryImport]` P/Invoke (`LinuxDeviceProvider`). Uses `udev_enumerate_*` to scan devices, `udev_device_get_property_value` and `udev_device_get_sysattr_value` for property retrieval.
- **Monitoring:** `udev_monitor` subscribed to the `"udev"` netlink source (`LinuxDeviceMonitorProvider`). Polls the monitor file descriptor on a dedicated `TaskCreationOptions.LongRunning` task.
- **Category mapping:** `LinuxCategoryMap` maps udev subsystems to `DeviceCategory` values, using property hints (`ID_INPUT_KEYBOARD`, `ID_INPUT_MOUSE`) to disambiguate shared subsystems.
- **Physical presence:** Reads the `authorized` sysattr; `authorized != "0"` → connected.
- **Event mapping:** `"add"` → `DeviceAppeared` (+ `DeviceConnected` if authorized); `"remove"` → `DeviceDisappeared`; `"bind"` → `DeviceConnected`; `"unbind"` → `DeviceDisconnected`; `"change"` → `DevicePropertyChanged` (via `DeviceInfoDiff`).

#### macOS Provider

- **Enumeration:** IOKit `IOServiceGetMatchingServices` with matching dictionaries per device class (`IOKitInterop.cs`).
- **Monitoring:** `IOServiceAddMatchingNotification` on a GCD dispatch queue via `IONotificationPortSetDispatchQueue` (no NSRunLoop dependency). Background scan loop for property mutations.
- **Physical presence:** Inspect `IOService` properties (e.g. `sessionID`, `PortStatus`).
- **Interop:** `[LibraryImport]` P/Invoke into `IOKit.framework` and `CoreFoundation.framework` (AOT/trim-safe, Apple Silicon compatible).
- **Category mapping:** `MacOSCategoryMap.cs` maps `DeviceCategory` to IOKit class names (e.g. `IOUSBDevice`, `IOHIDDevice`).
- **See:** ADR-0011 for full design rationale and property mapping table.

---

## 3. Device Category Model

`DeviceCategory` is an enum that maps to platform-specific identifiers:

```csharp
public enum DeviceCategory
{
    All = 0,    // No category filter applied
    Usb,
    Bluetooth,
    Network,
    Display,    // GPUs and display adapters
    Monitor,    // Screens
    Hid,        // Excludes keyboards and mice — they have their own members
    Keyboard,
    Mouse,
    Audio,
    Storage,
    Ports,      // Serial and parallel
    Battery,
    Camera,
}
```

The enum is **closed**. [ADR-0025](adr/0025-extensible-device-category.md) proposes an
extension range (≥ 1000) plus a `DeviceCategoryRegistry` so third-party packages can
add their own; that is not implemented — see
[extension-category-registry.md](extension-category-registry.md).

Each platform provider maintains an internal mapping:

| Category | Windows (ClassGuid) | Linux (subsystem) | macOS (IOKit class) |
|---|---|---|---|
| Usb | `{36fc9e60-...}` | `usb` | `IOUSBDevice` |
| Bluetooth | `{e0cbf06c-...}` | `bluetooth` | `IOBluetoothDevice` |
| Network | `{4d36e972-...}` | `net` | `IONetworkInterface` |
| Display | `{4d36e968-...}` | `drm` | `IODisplayConnect` |
| Hid | `{745a17a0-...}` | `hid` / `input` | `IOHIDDevice` |
| Keyboard | `{4d36e96b-...}` | `input` + `ID_INPUT_KEYBOARD=1` | HID usage page 1 / usage 6 |
| Mouse | `{4d36e96f-...}` | `input` + `ID_INPUT_MOUSE=1` | HID usage page 1 / usage 2 |
| Camera | `{ca3e7ab9-...}` | `video4linux` | `IOVideoDevice` |
| Ports | `{4d36e978-...}` | `tty` | `IOSerialBSDClient` |

> Five former categories — **Sensor, SmartCard, Imaging, Printer, Biometric** — were demoted to capability **tags** in [ADR-0051](adr/0051-demote-capability-categories-to-tags.md); their per-platform detection signals are in §3.1. None had a clean single-subsystem identity (each resolved only by post-filtering a USB class code or HID usage page) — the smell that distinguishes a capability from a subsystem.

The `DeviceClassGuids` lookup table already captures the Windows side of this mapping.

### 3.1 Category vs Tags — when to use which

`DeviceCategory` answers a single question: **which OS subsystem surfaced this device?** It's single-valued, drives platform-level enumeration routing (SetupAPI class GUID, udev subsystem, IOKit class), and reflects how the OS itself classified the hardware at enumeration time.

`DeviceInfo.Tags` (ADR-0047) answers a different question: **what capabilities does this device have?** It's a multi-valued open string set populated by enrichers during enumeration. The same physical device often carries both a Category and one-or-more Tags, and they don't always agree on the obvious answer:

| Device | `Category` | `Tags` | Why |
|---|---|---|---|
| Plain HID gamepad | `Hid` | `{}` | OS-classified; no enricher adds a tag beyond what Category already says. |
| HID-class UPS (WayTech, Cypress 0665) | `Hid` | `{Battery}` | OS surfaces it under HID; battery enricher detects the power-device surface and tags accordingly. |
| Laptop ACPI battery | `Battery` | `{}` | OS classifies it directly; battery enricher's signal matches the Category, no redundant tag. |
| Smart monitor with audio | `Monitor` | `{Audio}` | OS classification picks one; audio enricher annotates the cross-cutting capability. |
| USB scanner | `Usb` | `{Imaging}` | "Imaging" is a capability, not a subsystem — the OS surfaces it under USB; the imaging enricher tags it (ADR-0051). |

**Capability tags from ADR-0051.** Five identifiers that used to be `DeviceCategory` members are now tags, because each describes what a device *does* and only ever resolved by post-filtering a USB class code or HID usage page — never a clean subsystem. Their per-platform detection signals (relocated out of the category maps into core enrichers):

| Tag | Windows | Linux | macOS |
|---|---|---|---|
| `Sensor` | `Sensor` setup class | `iio` subsystem | HID usage page `0x20` |
| `SmartCard` | `SmartCardReader` class | USB class `0x0B` † | `IOUSBSmartCardController` |
| `Imaging` | `Image` setup class | USB class `0x06` † | USB class `0x06` † |
| `Printer` | `Printer` / `PnpPrinters` / `PrintQueue` classes | USB class `0x07` † | USB class `0x07` † |
| `Biometric` | `Biometric` setup class | — | — |

> **† Dormant pending cross-platform `UsbClassCode`.** USB-class detection requires `DeviceInfo.UsbClassCode`, populated on Windows today; the Linux/macOS branches and `EnricherScope` arms are written but inert until that field is populated off-Windows (deferred — Windows-first build-out, ADR-0051). The Windows class-GUID signals are live now, so each tag works on Windows exactly as its former category did. `Biometric` has no standard cross-platform signal (USB biometric readers are vendor-specific), so it's Windows-only by design.

**Practical guidance for consumer code:**

- **Default to `WithTag(...)` for capability questions.** "Give me anything I can read battery data from", "give me anything that's HID hardware". This is the dominant pattern; it survives white-label oddities where the OS classifies a device under a Category that doesn't reflect its real role.
- **Use `OfCategory(...)` only when you genuinely want a subsystem.** "Enumerate every HID device on the system", diagnostic listings, provider-level routing. Rare in application code; common in tooling.
- **Combine them when you can.** `OfCategory(Hid).WithTag(Battery)` narrows the OS-level enumeration to HID devices first (fewer devices to enrich), then tag-filters the result. Faster than `WithTag(Battery)` alone if the host has many non-HID devices.

**The `WithTag` Category-fallback (Option B in ADR-0047 §4):** The tag predicates also match against `Enum.GetName(device.Category)`, so `WithTag("Hid")` finds a plain gamepad (Category=Hid, Tags empty) and a HID-tagged keyboard (Category=Keyboard, Tags={Hid}) uniformly. Enrichers don't need to redundantly tag their device's Category — the filter does the unification at query time. The `DeviceTags` constants are deliberately defined to match `DeviceCategory` enum-member names (`DeviceTags.Hid == "Hid"`) so this fallback works as consumers expect.

See [ADR-0047](adr/0047-device-tags-vs-multi-category.md) for the full design rationale; [ADR-0048](adr/0048-hid-battery-support.md) for the concrete enricher that landed `DeviceTags.Battery` end-to-end; and [ADR-0051](adr/0051-demote-capability-categories-to-tags.md) for the demotion of five capability categories to tags.

---

## 4. Filtering Pipeline

The goal is to let users write:

```csharp
var mice = await Devices.Enumerate()
    .OfCategory(DeviceCategory.Hid)
    .Where(d => d.Name.Contains("Mouse"))
    .Active()
    .OrderBy(d => d.Name)
    .ToListAsync();
```

All filtering is evaluated **in-memory** by `DeviceFilter.Matches()`. Platform providers may use the filter’s structured properties as optional performance hints to narrow OS-level queries (e.g. WQL `WHERE` clauses on Windows), but this is a transparent optimisation — the caller never needs to know or care whether a filter was “pushed down”.

```
User LINQ expression
  └─ DeviceFilter.Matches() evaluates all predicates in-memory
       ├─ Structured properties (category, name, manufacturer, USB IDs)
       ├─ Convenience filters (serial number, bus type, drive type, MAC, etc.)
       └─ Arbitrary lambda predicates
```

This keeps the API fully general (any lambda works) and means every filter method is available on `DeviceQuery`, `DeviceWatcher`, and tracked device handles uniformly.

---

## 5. `DeviceInfo` — The Core Model

```csharp
public sealed record DeviceInfo
{
    // Identity
    required DeviceId Id;               // Platform-native unique ID (case-insensitive value type)
    string? Name;                       // Human-readable name
    DeviceCategory Category;            // Resolved category enum
    string? Manufacturer;               // Vendor name
    Guid? ClassGuid;                    // Windows class GUID (null on other platforms)
    string? ClassName;                  // Setup-class name / udev subsystem / IOKit class
    Guid? ContainerId;                  // Groups multi-interface devices

    // Hardware IDs
    HardwareId? VendorId;               // USB VID or equivalent (BCL-typed)
    HardwareId? ProductId;              // USB PID or equivalent (BCL-typed)
    string? SerialNumber;               // Device serial number

    // Status
    bool IsActive;                      // Physically active (driver started) — ADR-0004
    DeviceStatus Status;                // OS-reported status enum

    // Bus / Location
    BusType BusType;                    // USB, PCI, Bluetooth, etc.
    string? LocationPath;               // Bus address or port location path

    // Driver
    string? Driver;                     // Active driver or service name
    Version? DriverVersion;             // Driver or firmware version (BCL-typed)

    // Network
    PhysicalAddress? MacAddress;        // MAC address (BCL-typed)
    ImmutableArray<IPAddress>? IPAddresses; // Assigned IP addresses
    IPNetwork? Network;                 // Subnet information

    // Display / Monitor (ADR-0064, ADR-0068, ADR-0070, ADR-0072)
    Size? DisplayResolution;            // Native resolution (BCL-typed)
    Rectangle? DisplayBounds;           // On-desktop footprint, rotation-applied (BCL-typed)
    DisplayOrientation? DisplayOrientation; // Rotation vs. native, 0/90/180/270
    string? MonitorName;                // EDID / DisplayConfig friendly name
    float? DisplayPhysicalSizeInInches; // Diagonal
    SizeF? DisplayDpi;                  // Effective DPI
    DisplayConnector? DisplayPhysicalConnector; // HDMI, DisplayPort, …
    DisplayConnectionKind? DisplayConnectionKind; // Wired / Wireless / Internal / Indirect
    DisplayUsageKind? DisplayUsageKind; // Physical vs. virtual plane
    float? DisplayMaxLuminanceInNits;   // HDR luminance triple
    float? DisplayMaxAvgLuminanceInNits;
    float? DisplayMinLuminanceInNits;

    // Storage
    DriveType? DriveType;               // Fixed, Removable, etc. (BCL-typed)

    // Topology (ADR-0002)
    DeviceId? ParentId;                 // Parent device in the device tree
    int? PortNumber;                    // Hub/bus port number (1-based)

    // USB-specific (ADR-0002)
    UsbSpeed? UsbSpeed;                 // Negotiated USB speed
    int? MaxPowerMilliamps;             // Max power draw in mA
    UsbClassCode? UsbClassCode;         // USB class/subclass/protocol triple

    // HID (ADR-0020, ADR-0048)
    ushort? HidUsagePage;               // Top-level collection usage page
    ushort? HidUsage;                   // Top-level collection usage
    int? HidMaxInputReportLength;
    int? HidMaxOutputReportLength;
    int? HidMaxFeatureReportLength;

    // Serial / COM port (ADR-0002)
    SerialPortName? PortName;           // OS port name for SerialPort interop

    // Battery / Power (ADR-0003, ADR-0048)
    int? BatteryChargePercent;          // 0–100 charge level
    BatteryStatus? BatteryStatus;       // Charging, Discharging, Full, NotCharging
    bool? IsExternalPowerConnected;     // AC/USB-PD connected
    bool? IsBatteryLow;                 // Device-reported low-battery flag

    // Enricher routing hints (ADR-0026)
    string? Subsystem;                  // udev subsystem (Linux)
    string? IOServiceClass;             // IOKit class (macOS)

    // Capabilities and extensibility
    ImmutableHashSet<string> Tags;      // Capability tags (ADR-0047, ADR-0051)
    IReadOnlyDictionary<string, object?> Properties; // Platform-specific property bag
}
```

`Id` and `ParentId` are `DeviceId`, not `string` — a value type with
case-insensitive equality, so a device that re-enumerates with different casing is
still recognised as the same device. `Serialization/` holds the
`System.Text.Json` converters for every BCL-typed and value-typed field above.

The record remains immutable; snapshots are never mutated after creation.

---

## 6. Concurrency & Thread Safety

- `WindowsDeviceMonitorProvider` stores known devices in a `ConcurrentDictionary` keyed by `DeviceId` (case-insensitive by construction).
- `CM_Register_Notification` callbacks arrive on thread-pool threads; the dictionary handles concurrent add/remove safely.
- Events (`DeviceAppeared`, `DeviceDisappeared`, `DeviceActivated`, `DeviceDeactivated`, `DevicePropertyChanged`) are raised on the callback thread. Consumers needing UI-thread dispatch must marshal themselves (consistent with standard .NET event patterns).
- The watcher-then-snapshot ordering in `StartAsync` guarantees no device is missed during the race window between starting event watchers and running the snapshot query.

---

## 7. Adding a New Platform Provider

1. **Create the provider class** implementing `IDeviceProvider` and optionally `IDeviceMonitorProvider`.
2. **Map `DeviceCategory` values** to the platform's native identifiers (subsystem paths, IOKit classes, etc.).
3. **Populate `DeviceInfo`** from native enumeration results. Ensure `IsActive` accurately reflects whether the device is physically active.
4. **Register the provider** in the runtime provider resolver (guarded by `OperatingSystem.IsXxx()`).
5. **Add integration tests** using the test infrastructure (mock device trees where possible; real-hardware tests gated behind a test category).

---

## 8. Adding a New Device Category — or a Capability Tag

**First decide which you're adding (ADR-0051).** A **category** is justified only when a *single OS subsystem* surfaces the device directly on each platform (a SetupAPI class GUID / udev subsystem / IOKit class), single-valued. If the thing you want to express is a *capability* a device has — especially if it only resolves by post-filtering a USB class code or HID usage page — add a **tag** via an enricher instead (see §3.1), not a category.

**To add a genuine subsystem category:**

1. Add the value to `DeviceCategory`.
2. Add the platform mapping in **every** provider (Windows GUID, Linux subsystem, macOS IOKit class). If a platform cannot support the category, throw `PlatformNotSupportedException` with a clear message.
3. Add the GUID constant and dictionary entry in `DeviceClassGuids` (Windows).
4. Add a corresponding entry to the category table in this document and in the README.

**To add a capability tag:** add a `DeviceTags` constant and an `ITagEmittingEnricher` that reads already-populated `DeviceInfo` fields (`ClassGuid` / `Subsystem` / `IOServiceClass` / `HidUsagePage` / `UsbClassCode`) and emits the tag — no provider or category-map changes. `SensorEnricher` / `ImagingEnricher` / `PrinterEnricher` are the templates.

---

## 9. Testing Strategy

| Layer | Approach |
|---|---|
| `DevNodeHelper` | Unit tests: verify P/Invoke wrappers, property retrieval, and device-node status resolution. |
| `DeviceClassGuids` | Unit tests: verify GUID ↔ name round-trips, coverage of all entries. |
| Provider contracts | Interface-based tests with mock/fake providers to validate filtering, ordering, and event semantics. |
| Platform integration | On-device tests (CI agents with known hardware or VMs) behind a `[Category("Integration")]` gate. |
| LINQ pipeline | Unit tests: verify `DeviceFilter.Matches()` evaluates all predicates correctly. |

---

## 10. Known Issues & Future Work

This section documents current limitations and areas requiring further investigation or design work.

### 10.1 Test Coverage

**Status:** ✅ Broad. ~2,000 `[Fact]` / `[Theory]` methods across 21 test projects.
`tests/Periphery.Tests` alone carries ~830. There is not one test project per `src/`
project — 28 src projects, 21 test projects — because the GUI and CLI front-ends are
covered through the libraries they drive.

**Structure of the core suite (`tests/Periphery.Tests/`):**

| Folder | What it covers |
|---|---|
| `Api/` | Public-surface shape: what is exported, and what stays internal |
| `Query/` | `DeviceQuery` filter stacking, ordering, limiting, materialisation |
| `Watcher/` | `DeviceWatcher` lifecycle, event ordering, thread safety |
| `Tracker/` | `DeviceTracker` resolution, reconfigure, `MultiDeviceTracker` |
| `Handle/` | `DeviceProxy*`, `DeviceSessionHost`, `MultiDeviceSessionHost` |
| `Model/` | `DeviceInfo`, `DeviceId`, `HardwareId`, diffing, JSON round-trips |
| `Contracts/` | One shared contract suite run against every provider — fake, Windows, Linux, macOS, and real hardware |
| `Platform/` | Per-platform mapping and enrichment logic |
| `Fakes/` | `FakeDeviceProvider` / `FakeDeviceMonitorProvider` |

**The provider-contract pattern is the load-bearing piece.** `DeviceProviderContractTests`
and `DeviceMonitorProviderContractTests` define the invariants every provider must
satisfy; the fake, Windows, Linux, macOS, and hardware suites all inherit them. A new
provider gets its coverage by subclassing, and a provider that diverges from the
contract fails on the platform that runs it — which is why `macos-ci.yml`'s hosted tier
is worth its runner-minutes (see §10.4).

**Gates:** hardware-dependent tests are excluded by `--filter "Category!=Integration"`
everywhere except the device rigs. `Fixture` traits further split rig tests by what
physical hardware they need.

**Remaining gaps:**
- Windows and macOS have no device rig running automatically — the Linux rig is the
  only one with a wired-up CI job, and the macOS rig is not yet provisioned.
- `net8.0` assets are compiled but never executed; the test projects are `net10.0`-only
  ([ADR-0069](adr/0069-restore-net8-tfm-untested.md)).
- No coverage collection in CI (see §10.4).

---

### 10.2 Logging & Diagnostics

**Status:** ✅ Implemented. Optional structured logging available via `Microsoft.Extensions.Logging.Abstractions`.

**Implementation:**
- ✅ `PeripheryLoggerFactory` - Static configuration for optional `ILoggerFactory` injection
- ✅ Conservative, high-level logging at key decision points
- ✅ No logging by default (uses `NullLoggerFactory`)
- ✅ Zero performance impact when logging is disabled

**Logging Levels:**

| Level | Events Logged |
|-------|---------------|
| **Information** | Device enumeration start/completion with counts, watcher lifecycle (start/stop) |
| **Warning** | Individual device parsing failures (enumeration continues), event handler errors |
| **Error** | Provider initialization failures, Win32/SetupAPI errors, COM exceptions, access denied |
| **Debug** | SetupAPI enumeration, filter application, device counts, event details |
| **Trace** | Individual device matches/rejections, filtered-out events |

**Usage:**

```csharp
// Enable logging
using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddConsole().SetMinimumLevel(LogLevel.Information);
});
PeripheryLoggerFactory.SetLoggerFactory(loggerFactory);

// All Periphery operations now log to console
var devices = await Devices.Enumerate().OfCategory(DeviceCategory.Usb).ToListAsync();
// Output: "Device enumeration completed for category Usb. Found: 15, Skipped: 0"

// Disable logging
PeripheryLoggerFactory.SetLoggerFactory(null);
```

**Logged Components:**
- `WindowsDeviceProvider` - SetupAPI enumeration lifecycle, device counts, parsing failures
- `WindowsDeviceMonitorProvider` - Watcher initialization, snapshot operations, connection events
- `DeviceQuery` - Query execution, filter matching, ordering/limiting application
- `DeviceWatcher` - Lifecycle (start/stop), event counts, filtered device notifications

**Performance:**
- Logging uses `ILogger` with structured logging (no string concatenation overhead)
- Log level checks prevent unnecessary allocations
- Minimal instrumentation points (entry/exit + errors only)

**See:** [`examples/scripts/device-dump.cs`](../examples/scripts/device-dump.cs) for a
runnable demonstration (`dotnet run device-dump.cs`).

**Tracked:** ✅ Complete

---

### 10.3 DeviceWatcher Thread Safety

**Status:** ✅ Implemented with comprehensive synchronization and testing.

**Implementation:**
- ✅ `SemaphoreSlim` for async-safe synchronization of lifecycle methods
- ✅ `Interlocked` operations for thread-safe event counter increments
- ✅ Disposal protection with `_disposed` flag
- ✅ Idempotent `DisposeAsync()` with proper semaphore cleanup
- ✅ Thread-safety documentation in XML comments

**Thread-Safety Guarantees:**

| Operation | Thread Safety | Notes |
|-----------|---------------|-------|
| **Fluent filters** (`WithName`, `ByManufacturer`, etc.) | ❌ **Not thread-safe** | Configure all filters before calling `StartAsync()` |
| **`StartAsync()` / `StopAsync()`** | ✅ **Thread-safe** | Protected by `SemaphoreSlim`; concurrent calls are serialized |
| **`DisposeAsync()`** | ✅ **Idempotent** | Multiple concurrent calls are safe; disposes exactly once |
| **Event handlers** | ✅ **Thread-safe** | Can be added from multiple threads; invoked on thread-pool threads |
| **Event raising** | ✅ **Thread-safe** | Uses `Interlocked` for counters; filters applied atomically |

**Synchronization Mechanisms:**
- **`SemaphoreSlim _lifecycleLock`** - Protects `StartAsync()`, `StopAsync()`, and `DisposeAsync()` state transitions
- **`Interlocked.Increment()`** - Thread-safe event counter increments
- **`volatile bool _started`** - Visible across threads for fast-path checks
- **`volatile bool _disposed`** - Prevents use-after-dispose

**Testing:** covered by `tests/Periphery.Tests/Watcher/` (disposal protection, state
management, filter validation) plus `Category=Integration` stress tests that need real
SetupAPI enumeration. Run the latter with `dotnet test --filter "Category=Integration"`.

**Stress Testing:**
- Concurrent `StartAsync()` from 10 threads (only one succeeds)
- Concurrent `DisposeAsync()` from 10 threads (idempotent)
- 20 rapid start-stop cycles without deadlock
- 10 watchers started/stopped concurrently

**Performance:**
- Minimal overhead: lock only acquired during lifecycle transitions
- No locks during event handling (concurrent events supported)
- Event counters use lock-free `Interlocked` operations

**Tracked:** ✅ Complete
---

### 10.4 CI/CD Pipeline

**Status:** ✅ Implemented. Automated build, test, publish, and PR-review workflows.

**Workflows:**

| Workflow | Trigger | Runners | Purpose |
|----------|---------|---------|---------|
| **linux-ci.yml** | Push to `main`, PRs, manual dispatch | Self-hosted Linux (SDK container); the Linux device rig | Everyday build + unit tests; device-backed integration tests on manual dispatch |
| **macos-ci.yml** | Push to `main`, PRs (both skip Markdown-only changes), manual dispatch | Hosted `macos-latest`; the macOS device rig (not yet provisioned) | Automatic macOS coverage for the IOKit P/Invoke surface |
| **adr-lint.yml** | Push / PR touching `docs/adr/**` or the validator | Self-hosted Linux (SDK container) | `scripts/validate-adrs.sh` — frontmatter shape, status vocabulary, cross-reference resolution |
| **build.yml** | Manual dispatch only (boolean inputs pick the OSes) | Hosted `windows-latest` / `ubuntu-latest` / `macos-latest` | Cross-platform build verification + unit tests |
| **publish.yml** | Tags (`v*.*.*`) | Hosted `ubuntu-latest` for the publish job; self-hosted Linux + Windows for the test gate and release binaries | Test gate, NuGet publish to nuget.org (Trusted Publishing), self-contained release binaries |
| **peanut-gallery.yml** | PR opened/reopened/synchronize/ready, PR comments | Self-hosted Linux | Persona-driven automated PR review |
| **metrics-report.yml** | Daily cron (13:00 UTC), manual dispatch | Self-hosted Linux | Trailing-7-day Peanut Gallery metrics posted to the tracking issue |

**macOS CI Workflow (`macos-ci.yml`):**

`macos-latest` is a **hosted** runner and bills at a 10× minute multiplier on a private
repo. Three things keep that affordable, and none should be removed casually: build +
unit/contract tests only (no device tier on the hosted runner), Markdown-only pushes
don't trigger a run, and superseded runs are cancelled. It exists because nothing else
exercises `src/Periphery/MacOS/` automatically — `build.yml` is dispatch-only, so the
IOKit bindings could rot between manual runs. The `MacOS*ContractTests` inherit the
shared provider-contract invariants (§10.1) and run them against real IOKit here.

Its `device-tests` tier mirrors `linux-ci.yml`'s: dispatch-only, and additionally
pinned to `main`, because the job hands the dispatched ref's code the rig's local
privileges. The macOS rig does not exist yet — the wiring landed ahead of the
hardware so provisioning is a runner registration and nothing else.

**Build Workflow (`build.yml`):**
- ✅ `workflow_dispatch` only — cross-OS runs cost hosted runner-minutes, so there is
  no `push` or `pull_request` trigger. The dispatch form has `windows` / `linux` /
  `macos` booleans (all default true); a `setup` job builds the matrix from them and
  fails fast if none are ticked.
- ✅ Build matrix over the ticked OSes, `fail-fast: false`
- ✅ Unit tests only — `dotnet test --filter "Category!=Integration"` (integration tests
  need live hardware and aren't suitable for hosted runners). Published libraries multi-target
  `net8.0;net10.0` but the test projects are `net10.0`-only and CI runs only that leg
  ([ADR-0069](adr/0069-restore-net8-tfm-untested.md) supersedes ADR-0067) — so the
  `net8.0` assets are compiled but never executed, and the suite runs once per
  OS, not once per framework.
- ✅ `.trx` test-result artifacts, `retention-days: 14`, uploaded `continue-on-error`
  so an artifact-quota failure can't red-X an otherwise green run

**Linux CI Workflow (`linux-ci.yml`):**

This is the workflow that actually gates day-to-day work — self-hosted runner-minutes
are free, so it runs on every push to `main` and every PR.

- `build-test` — an ephemeral container runner (`mcr.microsoft.com/dotnet/sdk:10.0`);
  restore, build, unit tests (`Category!=Integration`), `.trx` artifact (14-day retention)
- `device-tests` — the dedicated Linux device rig (v4l2loopback test
  pattern, uhid virtual Megatec UPS, QEMU-emulated USB HID). Runs the
  `Category=Integration` suite natively so it can reach `/dev`, behind a rig preflight
  check. **Manual dispatch only** (`if: github.event_name == 'workflow_dispatch'`) — the
  rig is powered on demand, so on push/PR the job no-ops rather than queueing forever.

**Test Execution:**

```bash
# Unit tests — what build.yml and linux-ci.yml's build-test job run
dotnet test --filter "Category!=Integration"

# Integration tests — only ever run on the Linux device rig
# (linux-ci.yml device-tests, manual dispatch), or locally against real hardware
dotnet test --filter "Category=Integration"
```

**Code Coverage:**

No workflow collects coverage. There is no coverlet run, no Cobertura artifact, and no
Codecov upload in `.github/workflows/`. Every test project still references
`coverlet.collector`, so coverage is available locally:

```bash
dotnet test --collect:"XPlat Code Coverage"
```

**Publish Workflow (`publish.yml`):**
- Triggers on semver tags (`v0.1.0`, `v1.0.0`, `v1.7.0-alpha.2`, …)
- `test-linux` + `test-windows` gate the release — Release build, unit suites only
  (`Category!=Integration`), `--blame-hang-timeout 5m --blame-crash`
- `publish` rebuilds non-incrementally (`dotnet build --no-incremental`, i.e. the Rebuild
  target) so every assembly in the release is compiled from the tagged commit, then packs
  those exact outputs with `--no-build`. The self-hosted runners reuse their `_work` dir,
  so `packages/` is wiped first and a completeness gate asserts that every
  `IsPackable=true` project in `Periphery.slnx` produced a `.nupkg` — a partial family
  fails the release rather than shipping quietly. Packages go to nuget.org with
  `--skip-duplicate`, authenticated by Trusted Publishing (OIDC) rather than a stored
  key. See [PUBLISHING.md](../PUBLISHING.md).
- `release-binaries` attaches self-contained single-file `Periphery.Cli` builds to the
  GitHub Release — `win-x64` and `win-arm64` zips, plus a `linux-x64` tar.gz from
  `release-binaries-linux`, which is a separate job because only a Linux runner can
  set the Unix execute bit; `release-flasher` attaches the dual-mode `treehopper-flash.exe`.
  Tags with a hyphen suffix are flagged as GitHub prereleases.
- Runner selection reads the `LINUX_RUNNER` / `WINDOWS_RUNNER` repo variables, defaulting
  to the maintainer's self-hosted runners; set them to a hosted label (as JSON) to fall back.

**Branch Protection (Recommended):**
```yaml
# .github/branch-protection.yml (if using https://github.com/apps/settings)
main:
  required_status_checks:
    strict: true
    contexts:
      # build.yml is dispatch-only and cannot be a required check. adr-lint only
      # runs on docs/adr/** changes, so it cannot be required either — it would
      # never report on a code PR.
      #
      # NOT USABLE AS WRITTEN - see the two paragraphs below. `build-test` is
      # reported by BOTH linux-ci.yml and macos-ci.yml, and GitHub matches
      # required contexts by name, so this line cannot say which one must pass.
      - build-test
      - build-test-fork
  required_pull_request_reviews:
    required_approving_review_count: 1
```

**This configuration cannot currently be made correct, and the reason is a name
collision.** linux-ci.yml and macos-ci.yml both name their job `build-test`, and
GitHub matches required contexts by name rather than by workflow. The two collapse
into one context that *either* can satisfy. Two concrete holes follow:

- On a same-repository PR, a **failing Linux `build-test` can be masked by a passing
  macOS `build-test`**, because only the name is matched.
- On a fork PR the Linux `build-test` is skipped, and GitHub counts a skipped job as
  satisfying its requirement — so the context is satisfied whatever macOS did.

Requiring `build-test-fork` alongside it closes the fork half on Linux and nothing
else. **Do not read this snippet as enforcing Linux and macOS; it enforces neither
specifically.** The fix is to give the two jobs distinct names
(`build-test-linux` / `build-test-macos`) and require the distinct contexts; that is
a workflow change rather than a docs change, and there is no branch protection
configured on this repository today, so nothing is depending on the current names.

What the workflows actually *run* on a PR is Linux and macOS build + unit sweeps
(since `#284`), which covers the IOKit P/Invoke surface but still leaves Windows
unverified on merge. That is what runs — not what is enforced. Windows verification is deliberately
opt-in, so **dispatch `build.yml` by hand before merging anything Windows-sensitive**
(SetupAPI/cfgmgr32 P/Invoke, `MfInterop`, DisplayConfig, WinUSB, anything under an
`OperatingSystem.IsWindows()` guard). The earlier version of this section listed
`build-and-test (windows-latest, 8.0.x)`-style contexts and a `code-coverage` context
as required checks; no job has ever reported those names, so that configuration would
have blocked every merge rather than raising the bar.

**CI/CD Conventions:**
- ✅ `fail-fast: false` on the build matrix — all ticked platforms run even if one fails
- ✅ Integration tests are excluded from every automatic run; they only execute on the
  device rig via manual dispatch
- ✅ Test-result uploads are diagnostic and marked `continue-on-error` — an artifact
  upload failure never blocks a release or fails a green run
- ✅ Cheap self-hosted Linux verification is automatic; the one hosted leg that runs
  automatically (macOS) is scoped to build + unit tests and skips Markdown-only pushes.
  The full cross-OS matrix stays opt-in (`workflow_dispatch`)
- ✅ Dotnet telemetry / first-run experience disabled for faster builds

**Performance:**
- Unit test suite: seconds per platform on hosted runners
- Device integration suite: minutes, and only on the Linux device rig
- `linux-ci.yml` on a PR: build + unit tests on one self-hosted container runner

**Usage:**

```bash
# Everyday CI — automatic on push to main and on PRs
git push origin my-branch && gh pr create

# Cross-platform build verification (manual only — this is how Windows gets checked)
gh workflow run build.yml -f windows=true -f linux=true -f macos=true

# Device integration tests (rig must be powered on)
gh workflow run linux-ci.yml

# Validate the ADR corpus locally, same script adr-lint.yml runs
bash scripts/validate-adrs.sh

# Run integration tests locally
dotnet test --filter "Category=Integration"

# Trigger publish workflow
git tag -a v1.0.0 -m "Release v1.0.0"
git push origin v1.0.0
```

**Tracked:** ✅ Complete
---

### 10.5 Performance Optimization Opportunities

**Status:** No performance benchmarks or profiling data.

**Potential Optimizations:**
- `DeviceQuery` buffers all devices before ordering/limiting — could stream with `System.Linq.Async` for large result sets
- SetupAPI enumeration allocates per-device — pooling for property retrieval buffers?
- `DeviceFilter.Matches()` evaluates predicates sequentially — short-circuit ordering?

**Recommendation:** Establish performance baselines with BenchmarkDotNet before optimizing. Profile real-world usage patterns (100s-1000s of devices).

**Tracked:** Future investigation after baseline metrics available

---

### 10.6 Cross-Platform Provider Implementation Concerns

> **Retrospective, written before the providers shipped.** Both the Linux (ADR-0010)
> and macOS (ADR-0011) providers are implemented now, and some of what follows was
> settled differently than proposed — Linux went with `libudev` P/Invoke rather than a
> raw sysfs walker plus netlink parser, and `IsConnected` was renamed `IsActive` when
> presence and activity were split (ADR-0004, ADR-0056). The *asymmetries* it catalogues
> are still real and still shape the code, which is why the section stays. Read the
> naming as historical and the divergences as live.

**Status:** 🔶 Analysis retained for the divergences it documents; the implementation plan in §10.6.10 is done.

This section captures concrete issues identified by comparing the Windows provider implementation against the planned Linux (`sysfs` / `libudev` / `netlink`) and macOS (`IOKit`) backends. Each concern is rated by severity:

- 🔴 **Critical** — Requires design changes or new abstractions before implementation can begin.
- 🟡 **Significant** — Self-contained risk within a single provider; needs deliberate handling.
- 🟢 **Low** — Informational; current design accommodates this naturally.

---

#### 10.6.1 🔴 No Unified Enumeration Source on Linux or macOS

**Windows:** `SetupDiGetClassDevs` with a class GUID (or `DIGCF_ALLCLASSES`) returns every PnP device in a device information set. Properties (name, manufacturer, class GUID, status) are retrieved per-device via `CM_Get_DevNode_Property` with `DEVPROPKEY` constants. `DeviceFilter` structured properties are used as optional hints to narrow the class GUID passed to `SetupDiGetClassDevs`.

**Linux:** No single enumeration source exists. Each device category lives in a separate sysfs subsystem:

| Category | Sysfs path(s) |
|----------|---------------|
| Usb | `/sys/bus/usb/devices/` |
| Network | `/sys/class/net/` |
| Bluetooth | `/sys/class/bluetooth/` (basic) or D-Bus/BlueZ |
| Hid | `/sys/class/input/`, `/sys/bus/hid/devices/` |
| Display | `/sys/class/drm/` |
| Audio | `/sys/class/sound/` |
| Storage | `/sys/class/block/`, `/sys/block/` |

**macOS:** IOKit matching dictionaries are per-class. `IOServiceGetMatchingServices` requires a class-specific matching dictionary — there is no wildcard "all devices" query.

**Impact on `IDeviceProvider.EnumerateAsync`:**

When `filter.Category` is `DeviceCategory.All`, the Linux/macOS providers must fan out across *every* subsystem and merge results into a single `IAsyncEnumerable<DeviceInfo>` stream. This introduces:
- Significantly more I/O than a single SetupAPI enumeration
- A design decision on parallelism (enumerate subsystems concurrently vs. sequentially)
- Ordering differences across platforms (Windows returns SetupAPI enumeration order; Linux returns filesystem walk order)

**Recommended approach:**
- Create an internal `ISubsystemEnumerator` per sysfs subsystem (Linux) or IOKit class (macOS)
- The top-level provider orchestrates enumerators, optionally using `Channel<DeviceInfo>` to merge concurrent subsystem walks into one async stream
- Category filter → select which subsystem enumerators to activate (performance hint)

**Tracked:** Must resolve before Linux provider implementation

---

#### 10.6.2 🔴 Activity Semantics Diverge Per-Platform and Per-Category

> Named `IsConnected` when written; the property is `DeviceInfo.IsActive` today.

**Windows:** `DevNodeHelper.IsDeviceConnected` calls into `cfgmgr32.dll` (`CM_Get_DevNode_Status`) and checks `DN_STARTED` / `DN_DEVICE_DISCONNECTED` flags. This works uniformly for *all* PnP device categories with a single codepath.

**Linux:** Physical-presence detection varies by subsystem — there is no single sysfs attribute:

| Category | Detection method | Reliability |
|----------|-----------------|-------------|
| USB | `/sys/bus/usb/devices/X/authorized` == `1` | ✅ High |
| Network | `/sys/class/net/X/operstate` (`up`, `down`, `dormant`, `unknown`) | 🟡 `unknown` is ambiguous |
| Bluetooth | D-Bus query to BlueZ `org.bluez.Device1.Connected` property | 🟡 Requires D-Bus; property may lag |
| HID | Presence in `/sys/class/input/` implies connected (removed on disconnect) | ✅ High |
| Display | `/sys/class/drm/cardX-*/status` (`connected`, `disconnected`) | ✅ High |
| Audio | `/sys/class/sound/cardX/` existence + `/proc/asound/cards` | 🟡 Soft-removed cards may linger |
| Storage | `/sys/block/X/device/state` or checking if device file exists | 🟡 Varies by driver |

**macOS:** IOKit properties also vary per-class:

| Category | Detection method | Reliability |
|----------|-----------------|-------------|
| USB | `IOService` `sessionID` presence or `USBDeviceProperty` | ✅ High |
| Bluetooth | `IOBluetoothDevice` `isConnected` property | ✅ High |
| Network | `IONetworkInterface` `isEnabled` + link status | 🟡 Link ≠ physical |
| HID | `IOHIDDevice` open/close status | ✅ High |
| Display | `IODisplayConnect` `IODisplayIsConnected` | ✅ High |

**Impact:** The `IsConnected` contract ("physically active, driver started, not disconnected") cannot be uniformly satisfied across all categories on all platforms. Some categories will only have a best-effort heuristic.

**Recommended approach:**
- Implement `IsConnected` with the best available heuristic per category/platform
- Preserve the platform-specific raw value in `Properties` (e.g., `"operstate"` on Linux, `"IODisplayIsConnected"` on macOS) so consumers can refine
- `DeviceInfo.IsConnected` has `<remarks>` documenting that reliability varies by platform and category
- Consider a future `ConnectionConfidence` enum (`Definitive`, `Heuristic`, `Unknown`) if consumer demand warrants it

**Tracked:** Must resolve per-category heuristics during Linux provider implementation

---

#### 10.6.3 🟢 Filter Evaluation Is In-Memory; Provider Hints Vary by Platform

All filtering is authoritative in `DeviceFilter.Matches()`, evaluated in-memory. Providers *may* use structured filter properties to narrow OS queries as an optimisation, but correctness never depends on this. The table below documents what each platform *can* hint on:

**Windows (SetupAPI):**

| Filter | SetupAPI mechanism | Provider can hint? |
|--------|-----------|-------------------|
| `Category` | `SetupDiGetClassDevs(classGuid)` | ✅ Yes |
| `NameContains` | — | ❌ In-memory only |
| `ManufacturerContains` | — | ❌ In-memory only |
| `VendorId` / `ProductId` | — | ❌ In-memory only |
| All other filters | — | ❌ In-memory only |

**Linux (sysfs):** No query language. The provider walks directories and reads attribute files.

| Filter | Linux approach | Provider can hint? |
|--------|---------------|-------------------|
| `Category` | Select which `/sys/class/` or `/sys/bus/` paths to walk | ✅ Yes (directory selection) |
| All other filters | Read attributes per-device, then filter | ❌ In-memory only |

**macOS (IOKit):** Matching dictionaries support limited hints.

| Filter | macOS approach | Provider can hint? |
|--------|---------------|-------------------|
| `Category` | Matching dictionary with IOKit class name | ✅ Yes |
| `VendorId` / `ProductId` | `kUSBVendorID` / `kUSBProductID` (USB only) | ✅ Yes (USB only) |
| All other filters | Not supported in matching dict | ❌ In-memory only |

**Impact:** None on correctness. `DeviceFilter.Matches()` handles everything. Providers that narrow queries merely reduce the number of devices marshalled across the OS boundary, which is a minor performance benefit.

**Tracked:** Informational; no blocking action required

---

#### 10.6.4 🟡 `DeviceId` Format Is Platform-Specific

**Windows:** `USB\VID_1234&PID_5678\serialnumber` — backslash-delimited with bus prefix.

**Linux:** Sysfs paths like `/sys/devices/pci0000:00/0000:00:14.0/usb1/1-2` or bus-relative IDs like `1-2:1.0`.

**macOS:** IORegistry paths like `IOService:/AppleACPIPlatformExpert/PCI0@0/AppleACPIPCI/XHC1@14/XHC1@14000000/...` or numeric entry IDs.

**Impact:**

1. **`BusType` inference** — `WindowsCategoryMap.InferBusType` parses the device-ID prefix (`USB\` → `BusType.USB`, `PCI\` → `BusType.PCI`, etc.). Linux and macOS providers need their own inference logic:
   - **Linux:** Infer from sysfs subsystem path (e.g., `/sys/bus/usb/` → `USB`, `/sys/bus/pci/` → `PCI`)
   - **macOS:** Infer from IOKit class hierarchy or registry plane
   - Each platform will need its own `CategoryMap` equivalent

2. **Consumer portability** — If consumers store `DeviceInfo.Id` values (e.g., in a `HashSet`, config file, or database), those values are not portable across platforms. This is inherent and unavoidable, but should be documented.

3. **`DeviceWatcher` deduplication** — The watcher snapshot-then-monitor flow compares devices by ID. Since both the query provider and monitor provider run on the same OS, IDs will be consistent within a single platform. No cross-platform deduplication issue exists at runtime.

**Recommendation:**
- Each platform provider implements its own `InferBusType` logic
- Add a `/// <remarks>` note to `DeviceInfo.Id` documenting platform-specific format
- Consider a `WellKnownProperties` static class with per-platform property key constants

**Tracked:** Implementation detail for each provider; no blocking design change

---

#### 10.6.5 🟡 `ClassGuid` Is Windows-Only

`DeviceInfo.ClassGuid` is populated from the Windows SetupAPI class GUID. On Linux and macOS, this property will always be `null`. The Windows category resolution (`WindowsCategoryMap.ResolveCategory`) depends on it, but this is internal to the Windows provider.

**Impact:** Each platform needs its own category resolution:

| Platform | Category resolution input | Implementation |
|----------|--------------------------|----------------|
| Windows | Class GUID string | `WindowsCategoryMap.ResolveCategory(classGuid)` |
| Linux | Sysfs subsystem name | `LinuxCategoryMap.ResolveCategory("usb")` → `DeviceCategory.Usb` |
| macOS | IOKit class name | `MacOsCategoryMap.ResolveCategory("IOUSBDevice")` → `DeviceCategory.Usb` |

**Recommendation:** Straightforward; each provider creates a `*CategoryMap` static class mirroring `WindowsCategoryMap`.

**Tracked:** Implementation detail

---

#### 10.6.6 🟡 Monitor Provider Architecture Mismatch

The `IDeviceMonitorProvider` interface is simple (start, events, dispose), but the underlying mechanism differs radically:

| Platform | Mechanism | Delivery | Complexity |
|----------|-----------|----------|------------|
| **Windows** | `CM_Register_Notification` (device interface change) | Push-based, callback on thread-pool | Low — cfgmgr32 handles registration and delivery |
| **Linux** | Netlink socket (`AF_NETLINK`, `NETLINK_KOBJECT_UEVENT`) | Push-based, raw `uevent` message parsing | Medium-High |
| **macOS** | IOKit notification ports + `CFRunLoop` or GCD dispatch queue | Push-based, requires run-loop integration | High |

**Linux concerns:**
- `AF_NETLINK` is not supported by `System.Net.Sockets`. Requires raw P/Invoke for `socket()`, `bind()`, `recv()` (or `recvmsg()`).
- Netlink delivers events for *all* device subsystems; category filtering is entirely in-memory.
- `uevent` messages are plain-text `KEY=VALUE\0` format — a parser must be written.
- The provider needs a background `Task` with a persistent read loop, plus proper cancellation and disposal.

**macOS concerns:**
- IOKit notifications require a `CFRunLoop`. .NET has no native run-loop integration.
- Options: (a) spin up a dedicated thread running `CFRunLoopRun()`, or (b) P/Invoke into GCD (`dispatch_queue_create` / `dispatch_async`).
- Marshalling events from the run-loop/dispatch-queue to the caller thread requires careful design.
- `IOServiceAddMatchingNotification` requires an `IONotificationPort` — lifecycle management adds dispose complexity.

**Recommended approach:**
- **Linux:** Dedicated `Task.Run` loop reading from netlink socket via P/Invoke. Use `CancellationToken` + socket close for clean shutdown. Parse `uevent` messages into `DeviceInfo` using the same sysfs attribute readers as the enumeration provider.
- **macOS:** Dedicated thread with `CFRunLoopRun()`. IOKit notification callbacks post to a `Channel<DeviceChangeEventArgs>`, and a consuming `Task` raises the .NET events. Dispose stops the run-loop via `CFRunLoopStop()`.

**Tracked:** Highest-complexity work item for each platform

---

#### 10.6.7 🟡 `SerialNumber` Parsing Is Platform-Specific

**Windows:** `ParseSerialNumber` extracts the serial from the PnP device ID (`USB\VID_xxxx&PID_yyyy\SERIAL`) by taking the segment after the last backslash and excluding Windows-generated instance IDs (those containing `&`).

**Linux:** Serial numbers live in different sysfs attributes depending on the bus type:

| Bus | Sysfs path |
|-----|-----------|
| USB | `/sys/bus/usb/devices/X/serial` |
| SCSI/NVMe/SATA | `/sys/block/sdX/device/serial` (may require `ioctl`) |
| Bluetooth | MAC address is typically used as identifier (no serial) |
| PCI | Not typically available |

**macOS:** Serial numbers come from IOKit properties:

| Bus | IOKit property |
|-----|---------------|
| USB | `kUSBSerialNumberString` |
| Storage | `Serial Number` in IOMedia/IOBlockStorageDevice |
| Bluetooth | `BTAddress` |

**Impact:** Each provider needs bus-aware serial-number extraction. The `DeviceInfo.SerialNumber` property is nullable, so returning `null` for unsupported bus types is safe.

**Tracked:** Implementation detail for each provider

---

#### 10.6.8 🟡 `Properties` Bag Population Diverges

The Windows provider populates three well-known keys: `PNPDeviceID`, `ClassName`, `RawStatus`. The `DeviceInfo` XML docs already document planned keys for each platform.

**Planned keys:**

| Platform | Keys |
|----------|------|
| **Windows** | `PNPDeviceID`, `ClassName`, `RawStatus` |
| **Linux** | `SUBSYSTEM`, `DEVPATH`, `ID_VENDOR_FROM_DATABASE`, `ID_MODEL_FROM_DATABASE`, `DEVNAME` |
| **macOS** | `IOServiceClass`, `IORegistryEntryPath`, `IOObjectClass` |

**Impact:** Consumers writing cross-platform code cannot rely on any specific `Properties` key. This is by design (the property bag is for platform-specific extras), but discoverability is poor.

**Recommendation:**
- Create a `WellKnownProperties` static class (already referenced in `DeviceInfo` XML docs) with string constants grouped by platform:
  ```csharp
  public static class WellKnownProperties
  {
      public static class Windows { public const string PnpDeviceId = "PNPDeviceID"; ... }
      public static class Linux   { public const string Subsystem = "SUBSYSTEM"; ... }
      public static class MacOS   { public const string IOServiceClass = "IOServiceClass"; ... }
  }
  ```
- Document which keys each provider populates in the class XML docs and here

**Tracked:** Should be implemented before or alongside the first non-Windows provider

---

#### 10.6.9 🟢 Abstractions That Transfer Well

The following design decisions require no changes for cross-platform implementation:

| Aspect | Why it works |
|--------|-------------|
| `DeviceFilter.Matches()` is authoritative | Evaluates all filters in-memory. Provider query narrowing is optional and never affects correctness. |
| `DeviceQuery` / `DeviceWatcher` fluent API | Entirely platform-agnostic; delegates all platform work to the provider layer. |
| `DeviceCategory` enum | Abstract enough to map to any platform's classification scheme. |
| `DeviceStatus` enum | Intentionally coarse (`OK`, `Error`, `Disabled`, `Unknown`) — achievable on all platforms. |
| `BusType` enum | Covers all major bus types across platforms; `Unknown` is the safe default. |
| Immutable `DeviceInfo` record | No mutation concerns; providers just construct and return it. |
| `DeviceProviderFactory` runtime dispatch | Clean extension point — adding a new platform is one `if` branch + provider class. |
| Error handling pattern | `DeviceProviderException` wrapping platform-specific errors is reusable as-is. |
| Logging infrastructure | `PeripheryLoggerFactory` / `ILogger<T>` is platform-agnostic; providers just use `_logger`. |

---

#### 10.6.10 Recommended Implementation Order — ✅ done

> **Complete, and not quite as planned.** Phases 1–4 shipped; Linux enumeration and
> monitoring both went through `libudev` (`UdevInterop`, `udev_monitor` on a long-running
> poll task) rather than the sysfs-walker / netlink-parser split below, which is why
> `UeventParser` and the netlink socket layer do not exist. macOS landed on GCD via
> `IONotificationPortSetDispatchQueue`, avoiding the CFRunLoop dependency the plan flagged
> as highest-complexity. Phase 5's `WellKnownProperties` class was never built; the CI
> matrix arrived in a different shape (§10.4). Kept as a record of what was expected.

```
Phase 1: Linux Enumeration
  ├─ LinuxCategoryMap (subsystem → DeviceCategory)
  ├─ LinuxDeviceProvider : IDeviceProvider
  │   ├─ Sysfs directory walker per subsystem
  │   ├─ Attribute readers (name, manufacturer, VID/PID, serial)
  │   └─ Per-category IsConnected heuristics
  └─ Unit tests with mocked sysfs trees

Phase 2: Linux Monitoring
  ├─ Netlink socket P/Invoke layer
  ├─ UeventParser (KEY=VALUE message parser)
  ├─ LinuxDeviceMonitorProvider : IDeviceMonitorProvider
  └─ Integration tests (require real Linux kernel)

Phase 3: macOS Enumeration
  ├─ MacOsCategoryMap (IOKit class → DeviceCategory)
  ├─ IOKit P/Invoke layer (IOServiceGetMatchingServices, IORegistryEntry*)
  ├─ MacOsDeviceProvider : IDeviceProvider
  └─ Unit tests with IOKit mocking (if feasible)

Phase 4: macOS Monitoring
  ├─ CFRunLoop / GCD integration layer
  ├─ IOKit notification port management
  ├─ MacOsDeviceMonitorProvider : IDeviceMonitorProvider
  └─ Integration tests (require macOS hardware)

Phase 5: Cross-Platform Validation
  ├─ WellKnownProperties static class
  ├─ DeviceInfo.Id / IsConnected documentation updates
  ├─ CI matrix validation (all platforms × all frameworks)
  └─ Performance baseline comparison across providers
```

**Rationale:** Start with Linux enumeration because sysfs is well-documented, has no native library dependencies (pure file I/O), and provides the fastest path to a second working platform. Defer monitoring to Phase 2 because netlink P/Invoke is the highest-risk Linux work item. macOS follows because IOKit requires more P/Invoke surface area and the CFRunLoop integration is the single highest-complexity item across all providers.

**Tracked:** Phased implementation — see individual subsections above for per-concern tracking

---

## 11. Open Questions

- ~~**NuGet packaging:**~~ ✅ **Resolved.** One package per `src/` project, each a single cross-platform assembly with runtime `OperatingSystem.IsXxx()` guards — no RID-specific packages. Published to nuget.org (see [PUBLISHING.md](../PUBLISHING.md)).
- ~~**IAsyncEnumerable vs materialised list:**~~ ✅ **Resolved.** `Devices.Enumerate()` returns `DeviceQuery : IAsyncEnumerable<DeviceInfo>` (lazy streaming); the terminal operators (`ToListAsync`, `FirstOrDefaultAsync`, `CountAsync`, `AnyAsync`) materialise it. There is no separate eager entry point. Category is just another filter via `.OfCategory()`, not a method parameter.
- ~~**Expression tree depth:**~~ ✅ **Resolved (MVP).** `DeviceFilter` exposes structured properties and convenience filter methods that cover the most common `DeviceInfo` fields. All filtering is evaluated in-memory by `Matches()`. Providers may inspect structured properties to narrow OS queries as an optimisation, but this is transparent to consumers.
- **Hot-reload / re-scan:** Should `IDeviceMonitor` support an explicit `RescanAsync()` to re-snapshot without restart?
