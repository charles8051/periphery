---
title: "ADR-0002: Device Tree Topology & USB Enrichment"
status: "Accepted"
date: "2025-07-15"
authors: "@charles8051 (proposal)"
tags: ["architecture", "decision"]
supersedes: ""
superseded_by: ""
---

# ADR-0002: Device Tree Topology & USB Enrichment

**Tracks:** Parent-child device relationships, hub/port topology, USB-specific metadata, serial port name mapping

---

## Context

Periphery currently returns a flat list of devices. Each `DeviceInfo` has an `Id`, `LocationPath`, and `ContainerId` — but no way to express that a keyboard is plugged into port 3 of a hub, which is plugged into a root hub on a USB 3.0 controller.

Tools like USB Tree Viewer (Windows) show a rich hierarchy: controller → root hub → external hub → device, with per-node metadata like speed negotiation, power draw, and port number. Users have expressed interest in similar capabilities cross-platform.

### What's in scope

Metadata available through **OS enumeration APIs** without opening a handle to the device:

| Data | Windows | Linux | macOS |
|---|---|---|---|
| Parent device ID | `CM_Get_Parent` → `CM_Get_Device_ID` | sysfs path hierarchy | IOKit `IORegistryEntryGetParentEntry` |
| Children | `CM_Get_Child` + `CM_Get_Sibling` | sysfs `readdir` on parent | IOKit `IORegistryEntryGetChildIterator` |
| Port number | `DEVPKEY_Device_Address` | sysfs `devnum` / `port` | IOKit `PortNum` property |
| USB speed | `Win32_USBHub.USBVersion` or registry | sysfs `speed` (Mbps) | IOKit `USBDeviceSpeed` |
| Max power (mA) | Registry or `Win32_USBHub` | sysfs `bMaxPower` | IOKit `MaxPower` property |
| USB class/subclass/protocol | WMI `CompatibleID` parsing | sysfs `bDeviceClass` etc. | IOKit properties |
| Serial port name | Registry `DEVICEMAP\SERIALCOMM` or `DEVPKEY_Device_FriendlyName` | udev `DEVNAME` (`/dev/ttyUSB0`) | IOKit `IOCalloutDevice` (`/dev/cu.*`) |

### What's out of scope

**Raw USB descriptors** (device descriptor, configuration descriptor, interface descriptors, endpoint descriptors, HID report descriptors, BOS descriptors). Reading these requires opening a device handle and issuing USB control transfers — that's device I/O, not discovery:

| Platform | Descriptor access mechanism |
|---|---|
| Windows | `DeviceIoControl` with `IOCTL_USB_GET_DESCRIPTOR_FROM_NODE_CONNECTION` (WinUSB) |
| Linux | Raw read from `/dev/bus/usb/NNN/NNN` or `libusb_get_*_descriptor()` |
| macOS | `IOUSBDeviceInterface` → `GetDeviceDescriptor()` |

Per ARCHITECTURE.md §1: *"Discovery only — this library enumerates hardware devices; it does NOT interact with them."* Descriptors belong in a companion library (e.g. a future `Periphery.Usb` built on `libusb`).

---

## Decision Drivers

- **"Where is this device plugged in?"** — the most common question Periphery can't answer today.
- **Cross-platform parity** — all three platforms expose tree topology through enumeration APIs. This is not a Windows-only feature.
- **No new OS dependencies** — cfgmgr32 (Windows), sysfs (Linux), IOKit (macOS) are already the planned provider foundations.
- **Incremental** — topology can be added without breaking any existing API surface.
- **BCL types** — consistent with the project's preference for rich value types over strings.

---

## Proposed Changes

### 1. New properties on `DeviceInfo`

```csharp
public sealed record DeviceInfo
{
    // ... existing properties ...

    // ── Topology ───────────────────────────────────────────────────────

    /// <summary>
    /// Platform-native ID of this device's parent in the device tree.
    /// <c>null</c> for root devices (e.g. PCI host controllers).
    /// </summary>
    public string? ParentId { get; init; }

    /// <summary>
    /// Hub port number this device is attached to (1-based).
    /// <c>null</c> if not a bus-attached device or not available.
    /// </summary>
    public int? PortNumber { get; init; }

    // ── USB-specific ───────────────────────────────────────────────────

    /// <summary>
    /// Negotiated USB speed, if the device is on a USB bus.
    /// <c>null</c> for non-USB devices.
    /// </summary>
    public UsbSpeed? UsbSpeed { get; init; }

    /// <summary>
    /// Maximum power the device is configured to draw, in milliamps.
    /// <c>null</c> if not available or not a USB device.
    /// </summary>
    public int? MaxPowerMilliamps { get; init; }

    /// <summary>
    /// USB class/subclass/protocol triple identifying the device function.
    /// <c>null</c> for non-USB devices.
    /// </summary>
    public UsbClassCode? UsbClassCode { get; init; }

    // ── Serial / COM port ──────────────────────────────────────────────

    /// <summary>
    /// OS serial port name for COM/serial devices.
    /// Use <c>PortName.Value</c> to get the string for <c>new SerialPort()</c>.
    /// <c>null</c> for non-serial devices.
    /// </summary>
    public SerialPortName? PortName { get; init; }
}
```

### 2. New `UsbSpeed` enum

```csharp
/// <summary>
/// USB signalling speed.
/// </summary>
public enum UsbSpeed
{
    /// <summary>USB 1.0 — 1.5 Mbps.</summary>
    Low,

    /// <summary>USB 1.1 — 12 Mbps.</summary>
    Full,

    /// <summary>USB 2.0 — 480 Mbps.</summary>
    High,

    /// <summary>USB 3.0 (Gen 1) — 5 Gbps.</summary>
    Super,

    /// <summary>USB 3.1 (Gen 2) — 10 Gbps.</summary>
    SuperPlus,

    /// <summary>USB 3.2 (Gen 2×2) — 20 Gbps.</summary>
    SuperPlusx2,

    /// <summary>USB4 — 40 Gbps.</summary>
    Usb4,
}
```

### 3. New `UsbClassCode` struct

Replaces three separate `byte?` properties with a single typed struct holding the USB class/subclass/protocol triple. Follows the `HardwareId` pattern — thin wrapper with value equality, formatting, and well-known constants for all USB-IF defined base classes.

```csharp
public readonly struct UsbClassCode : IEquatable<UsbClassCode>
{
    public byte Class { get; }
    public byte Subclass { get; }
    public byte Protocol { get; }

    // Well-known base classes (~25 from USB-IF spec)
    public static readonly UsbClassCode UseInterfaceDescriptor = new(0x00, 0x00, 0x00);
    public static readonly UsbClassCode Audio = new(0x01, 0x00, 0x00);
    public static readonly UsbClassCode CdcControl = new(0x02, 0x00, 0x00);
    public static readonly UsbClassCode Hid = new(0x03, 0x00, 0x00);
    public static readonly UsbClassCode Physical = new(0x05, 0x00, 0x00);
    public static readonly UsbClassCode Image = new(0x06, 0x00, 0x00);
    public static readonly UsbClassCode Printer = new(0x07, 0x00, 0x00);
    public static readonly UsbClassCode MassStorage = new(0x08, 0x00, 0x00);
    public static readonly UsbClassCode Hub = new(0x09, 0x00, 0x00);
    // ... all USB-IF defined classes, subclasses, and triples
    public static readonly UsbClassCode VendorSpecific = new(0xFF, 0x00, 0x00);

    // Matching helpers
    public bool IsClass(byte classCode) => Class == classCode;
}
```

### 4. New `SerialPortName` struct

A thin value type wrapping the OS serial port name string. Guarantees the value is non-null and non-empty. `Value` returns the string ready for `new SerialPort(portName.Value)`.

```csharp
public readonly struct SerialPortName : IEquatable<SerialPortName>
{
    public string Value { get; }
    public override string ToString() => Value;

    // Parse / TryParse for round-tripping
    public static SerialPortName Parse(string s) { ... }
    public static bool TryParse(string? s, out SerialPortName result) { ... }
}
```

### 5. Tree traversal API on `DeviceQuery`

```csharp
// Walk up: who is my parent?
DeviceInfo mouse = ...;
var parent = await Devices.Enumerate()
    .Where(d => d.Id == mouse.ParentId)
    .FirstOrDefaultAsync();

// Walk down: what's connected to this hub?
var children = await Devices.Enumerate()
    .Where(d => d.ParentId == hub.Id)
    .ToListAsync();
```

No new query methods needed initially — `ParentId` + existing `Where()` is sufficient. A convenience `.Children()` / `.Parent()` extension could be added later if the pattern is common enough.

### 6. Provider changes

| Provider | Work required |
|---|---|
| **Windows** | Add `CM_Get_Parent` + `CM_Get_Device_ID` to `DevNodeHelper`. Read `DEVPKEY_Device_Address` for port number. Parse USB speed from registry or WMI. Populate new `DeviceInfo` fields in `ToDeviceInfo()`. |
| **Linux** (future) | Parse sysfs path for parent relationship (path structure encodes topology). Read `speed`, `bMaxPower`, `bDeviceClass` from sysfs attributes. |
| **macOS** (future) | Walk IOKit registry tree with `IORegistryEntryGetParentEntry`. Read IOKit properties for speed, power, class codes. |

### 7. Filter additions

| Filter | Property | Rationale |
|---|---|---|
| `.WithUsbSpeed(UsbSpeed)` | `UsbSpeed` | "Show me all USB 3.0+ devices" |
| `.WithParent(string parentId)` | `ParentId` | "Show me devices on this hub" |
| `.WithPortName(string)` | `PortName` | "Find the device on COM3" |

These are convenience methods; all are achievable via `.Where()` today.

---

## Analysis

### What this enables

**USB Tree Viewer "lite"** — a console or UI tool that renders:

```
USB xHCI Controller (PCI)
└── Root Hub (USB 3.0)
    ├── Port 1: Logitech Mouse [USB 2.0, 100mA]
    ├── Port 2: (empty)
    ├── Port 3: USB Hub
    │   ├── Port 1: Keyboard [USB 2.0, 500mA]
    │   └── Port 2: Webcam [USB 2.0, 500mA]
    └── Port 4: External SSD [USB 3.0, 900mA]
```

All of this is constructable from `ParentId` + `PortNumber` + `UsbSpeed` + `MaxPowerMilliamps` without any device I/O.

### What this does NOT enable

- Raw USB descriptors (device, config, interface, endpoint, HID report, BOS)
- String descriptor reads (iManufacturer, iProduct, iSerialNumber beyond what the OS caches)
- USB control/bulk/interrupt/isochronous transfers
- Device configuration changes (set configuration, set interface, etc.)

These require a dedicated USB I/O library. If there's demand, a future `Periphery.Usb` package could provide descriptor access, likely wrapping `libusb` for cross-platform support.

### Platform data availability

| Property | Windows | Linux | macOS | Notes |
|---|---|---|---|---|
| `ParentId` | ✅ cfgmgr32 | ✅ sysfs path | ✅ IOKit registry | Core topology |
| `PortNumber` | ✅ `DEVPKEY_Device_Address` | ✅ sysfs `port` | ✅ IOKit `PortNum` | 1-based |
| `UsbSpeed` | ⚠️ Indirect (registry / WMI) | ✅ sysfs `speed` | ✅ IOKit `USBDeviceSpeed` | Windows requires extra work |
| `MaxPowerMilliamps` | ⚠️ Registry | ✅ sysfs `bMaxPower` | ✅ IOKit `MaxPower` | Windows least reliable |
| `UsbDeviceClass` | ✅ `CompatibleID` parsing | ✅ sysfs `bDeviceClass` | ✅ IOKit property | Standard USB triple |
| `PortName` | ✅ Registry `DEVICEMAP\SERIALCOMM` | ✅ udev `DEVNAME` | ✅ IOKit `IOCalloutDevice` | Bridges discovery → `SerialPort` |

Windows is the weakest

---

## Impact on Existing Types

| Type | Change |
|---|---|
| `DeviceInfo` | Add 6 new nullable properties (topology + USB + serial port name) |
| `DeviceFilter` | Add 3 convenience methods (`WithUsbSpeed`, `WithParent`, `WithPortName`) |
| `DeviceQuery` / `DeviceWatcher` | Surface same 3 methods on fluent API |
| `DevNodeHelper` | Add `CM_Get_Parent`, `CM_Get_Device_ID` P/Invokes |
| `WindowsDeviceProvider` | Populate new fields in `ToDeviceInfo()` |
| `UsbSpeed` | New enum |
| `UsbClassCode` | New struct (replaces three `byte?` properties) |
| `SerialPortName` | New struct (replaces `string?` port name) |
| Tests | `DeviceInfoTests` for new defaults/init, `DeviceFilterTests` for new filters |

No breaking changes to existing API.

---

## Decisions (resolved from open questions)

1. **`ParentId` is `string?`.** Mirrors the existing `Id` property — opaque, platform-specific, no parsing needed. A `DeviceRef` struct adds ceremony for no real benefit.
2. **No `Depth` property.** Trivially computable by walking `ParentId`. Adding it to the record creates a field that could go stale if the tree is re-enumerated.
3. **No tree-building helper in core.** A materialized `DeviceTreeNode` graph is a consumer convenience. It belongs in `Periphery.Examples` or a future utility package, not the core library. *(Superseded by ADR-0078 (Proposed): the library built four private ancestor walks with three different bounds, so the graph is not merely a consumer convenience; ADR-0078 D3 also places the type in core. See ADR-0079 for the smaller parser that serves most of the demonstrated need.)*
4. **`UsbClassCode` struct with all USB-IF defined triples.** Replaces three separate `byte?` properties. The USB-IF spec defines ~25 base classes and ~150–250 total (class, subclass, protocol) triples — completely manageable. Well-known constants for base classes live as `static readonly` fields; nested static classes group subclass/protocol triples. Raw bytes always available as fallback.
5. **Topology is bus-agnostic.** `ParentId` and `PortNumber` work for PCI (slot number), Bluetooth (controller → device), SATA (port), Thunderbolt (daisy-chain), and all other tree/star topologies. No shipping bus protocol uses mesh topology. USB-specific properties (`UsbSpeed`, `MaxPowerMilliamps`, `UsbClassCode`) are correctly scoped as nullable.
6. **`SerialPortName` value type.** Wraps the OS port name string with validation (non-null, non-empty). `Value` returns the string ready for `new SerialPort(portName.Value)`. No implicit conversion to `string` — forces explicit `.Value` to prevent accidental misuse. Follows the `HardwareId` pattern.

---

## References

- [USB Tree Viewer](https://www.uwe-sieber.de/usbtreeview_e.html) — The tool that inspired this discussion
- [cfgmgr32 API](https://learn.microsoft.com/en-us/windows/win32/api/cfgmgr32/) — Windows device tree traversal
- [sysfs USB topology](https://www.kernel.org/doc/Documentation/usb/proc_usb_info.txt) — Linux USB device tree layout
- [IOKit Fundamentals](https://developer.apple.com/library/archive/documentation/DeviceDrivers/Conceptual/IOKitFundamentals/) — macOS device registry
- `docs/ARCHITECTURE.md` — Layering, provider contracts, "discovery only" principle
- `Periphery/Windows/DevNodeHelper.cs` — Existing cfgmgr32 P/Invoke surface (would be extended)
