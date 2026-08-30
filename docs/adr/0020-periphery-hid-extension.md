---
title: "ADR-0020: Periphery.Hid — HID Extension Library as the First I/O Extension"
status: "Accepted"
status_note: "Shipped - `src/Periphery.Hid`, with Windows and Linux (hidraw) backends."
date: "2026-07-14"
authors: "@charles8051 (design)"
tags: ["architecture", "decision", "hid", "extension", "api-design", "periphery-hid", "i/o"]
supersedes: ""
superseded_by: ""
---

# ADR-0020: Periphery.Hid — HID Extension Library as the First I/O Extension

## Context

ADR-0019 established the two-layer pattern for I/O extension packages:
- **Layer 1** — an I/O primitive (`UsbDevice`) that bridges from enumeration to an open
  platform handle.
- **Layer 2** — a lifecycle manager (`UsbDeviceProxy`) that composes around `DeviceTracker`
  and manages the open/close cycle automatically.

Before implementing full USB I/O (raw transfers, endpoint management, descriptor tree,
interface claiming), a simpler starting point is desirable that:

1. Validates the extension architecture end-to-end on all three platforms.
2. Delivers immediate real-world value (keyboards, mice, gamepads, custom HID devices).
3. Has a transfer surface small enough to implement and test in a single iteration.

### Why HID is the right starting point

**The OS abstracts all USB complexity for HID devices.** When the HID class driver binds
to a USB device, it negotiates the USB descriptor tree, claims the interface, and selects
the interrupt endpoints. By the time user-space opens the device, the entire protocol
stack has been handled by the OS. The application sees exactly one abstraction: **reports**.

A HID report is a fixed-size byte buffer prefixed with a one-byte report ID. The complete
transfer surface is:

- `ReadReport` — receive an input report from the device (interrupt IN)
- `WriteReport` — send an output report to the device (interrupt OUT or control)
- `GetFeatureReport` — retrieve a feature report via a control transfer
- `SetFeatureReport` — send a feature report via a control transfer

**All device-specific metadata** needed to use a HID device — `UsagePage`, `Usage`,
maximum report sizes — is available from OS enumeration APIs without opening a handle:

| Platform | Source |
|---|---|
| Windows | `HidD_GetAttributes`, `HidP_GetCaps` on the HID device path (pre-open) |
| Linux | `ioctl(HIDIOCGRDESCSIZE)`, `ioctl(HIDIOCGRDESC)` on `/dev/hidrawN` |
| macOS | `IOHIDDeviceGetProperty` on the `IOHIDDevice` service |

This means the `IDeviceEnricher` path (ADR-0019, required change 1) can populate
`UsagePage`, `Usage`, and report sizes into `DeviceInfo` during enumeration, making
them available for filtering and display without opening a handle.

### Platform I/O comparison

| | Raw USB (ADR-0019) | HID |
|---|---|---|
| Windows kernel driver | WinUSB (requires driver install or co-installer) | HID class driver (inbox, always present) |
| Windows user-space | `WinUsb_WritePipe` / `WinUsb_ReadPipe` | `CreateFile` + `WriteFile` / `ReadFile` |
| Linux user-space | `libusb` or `usbfs` ioctl | `open("/dev/hidrawN")` + `read`/`write` |
| macOS user-space | IOKit USB interface vtable | `IOHIDDeviceOpen` + callbacks |
| Interface claiming | Required (Linux) | Handled by OS HID driver |
| Endpoint discovery | Manual | Not required |
| Descriptor parsing | Required | Not required |
| Driver prerequisites | WinUSB / libwdi / co-installer | None |

---

## Decision

Implement `Periphery.Hid` as the first I/O extension package, validating the ADR-0019
extension architecture before building the more complex `Periphery.Usb`.

This design maps directly to the canonical **Layer 1 / Layer 2 / Layer 3** extension
package pattern established in ADR-0024. `HidDevice` is the Layer 1 I/O primitive;
`HidDeviceProxy` is the Layer 2 lifecycle manager; `HidDeviceEnricher` plus typed
`DeviceInfo` properties are the Layer 3 enrichment.

### `HidDevice` — the Layer 1 I/O primitive

```csharp
public sealed class HidDevice : IAsyncDisposable
{
    // Discovery context — VendorId, ProductId, Name already on DeviceInfo
    public DeviceInfo DeviceInfo { get; }

    // OS-enumerable metadata (populated without opening the device)
    public ushort UsagePage { get; }
    public ushort Usage { get; }
    public int MaxInputReportLength { get; }
    public int MaxOutputReportLength { get; }
    public int MaxFeatureReportLength { get; }

    // Transfer surface — the complete HID API
    public Task<HidReport> ReadReportAsync(CancellationToken ct = default);
    public Task WriteReportAsync(HidReport report, CancellationToken ct = default);
    public Task<HidReport> GetFeatureReportAsync(byte reportId,
        CancellationToken ct = default);
    public Task SetFeatureReportAsync(HidReport report,
        CancellationToken ct = default);

    // Factory — the explicit crossing of the discovery / I/O boundary
    public static Task<HidDevice> OpenAsync(DeviceInfo device,
        CancellationToken ct = default);
}

public readonly struct HidReport
{
    public byte ReportId { get; }
    public ReadOnlyMemory<byte> Data { get; }

    public HidReport(byte reportId, ReadOnlyMemory<byte> data);
}
```

### `HidDeviceProxy` — the Layer 2 lifecycle manager

Created via a static factory rather than a constructor, so the caller never needs to
manage the `DeviceTracker` lifecycle directly. The factory accepts a `DeviceProfile`
(or a filter-builder delegate) and internally constructs the `DeviceTracker`,
composes it with the `DeviceWatcher`, and returns a ready-to-use handle.

```csharp
public sealed class HidDeviceProxy : INotifyPropertyChanged, IAsyncDisposable
{
    // Factory — preferred entry point; hides DeviceTracker composition
    public static Task<HidDeviceProxy> OpenAsync(
        DeviceProfile profile,
        CancellationToken ct = default);

    // Connection state at the handle level — distinct from DeviceInfo.IsActive
    //   DeviceInfo.IsActive  : OS sees the device (driver up, enumerated)
    //   HidDeviceProxy.IsConnected : app has an open platform handle, ready for I/O
    public bool IsConnected { get; }
    public DeviceInfo? DeviceInfo { get; }
    public HidDevice? Device { get; }

    public event EventHandler<HidDevice>? DeviceOpened;
    public event EventHandler? DeviceClosed;
    public event PropertyChangedEventHandler? PropertyChanged;
}
```

#### `IsActive` vs `IsConnected` — semantic layering

| Property | Layer | Meaning |
|---|---|---|
| `DeviceInfo.IsActive` | Core (enumeration snapshot) | OS driver started; hardware visible at scan time |
| `HidDeviceProxy.IsConnected` | Extension (live handle state) | `OpenAsync` succeeded; platform file handle is open and ready for I/O |

`OpenAsync` on the handle bridges the two layers: it waits for a matching device where
`DeviceInfo.IsActive == true`, then opens the platform handle and sets `IsConnected = true`.
The two can diverge — a device can be `IsActive` (enumerable) but not yet connected
(handle not opened), or temporarily `!IsActive` (unplugged) while the reconnect loop
waits for it to reappear.

### Call-site shape

```csharp
// Layer 1 — one-shot: open a specific already-enumerated device
var info = await Devices.Enumerate()
    .OfCategory(DeviceCategory.Hid)
    .WithUsbId("045E", "02EA")   // Xbox controller
    .FirstOrDefaultAsync();

await using var hid = await HidDevice.OpenAsync(info);
var report = await hid.ReadReportAsync();
Console.WriteLine($"Report ID {report.ReportId}: {report.Data.Length} bytes");

// Layer 2 — reconnect-resilient: factory hides DeviceTracker composition
var profile = new DeviceProfile("Xbox Controller",
    f => f.WithUsbId("045E", "02EA"));

await using var handle = await HidDeviceProxy.OpenAsync(profile);

handle.DeviceOpened += async (_, hid) =>
{
    // hid.DeviceInfo.IsActive == true  (OS sees it)
    // handle.IsConnected == true       (platform handle is open, ready for I/O)
    Console.WriteLine($"Opened: {hid.DeviceInfo.Name}  " +
                      $"UsagePage=0x{hid.UsagePage:X4} Usage=0x{hid.Usage:X4}");
    while (handle.IsConnected)
    {
        var report = await hid.ReadReportAsync();
        ProcessInputReport(report.Data.Span);
    }
};

handle.DeviceClosed += (_, _) => Console.WriteLine("Controller disconnected.");
// No manual watcher wiring — the factory handles it
```

### Platform backends

Each platform gets a dedicated internal implementation behind the `HidDevice` abstraction:

| Platform | Backend | Notes |
|---|---|---|
| Windows | `CreateFile` on `\\?\HID#...` device path; `ReadFile`/`WriteFile` with overlapped I/O | Device path from `SetupDiGetDeviceInterfaceDetail` using `GUID_DEVINTERFACE_HID` |
| Linux | `open("/dev/hidrawN", O_RDWR \| O_NONBLOCK)` + async `read`/`write` via `epoll` | Device node resolved from `DeviceInfo.Id` via `/sys/class/hidraw/hidrawN/device` symlink |
| macOS | `IOHIDDeviceOpen` + `IOHIDDeviceRegisterInputReportCallback` | Service ref from `DeviceInfo.Id` (IOKit registry entry ID) |

### `HidDeviceEnricher` — Layer 3 OS metadata

Implements `IDeviceEnricher` (ADR-0019 required change 1, ADR-0024 §3c) as a **sub-kind A**
enricher (OS-metadata only, no handle opened) to populate the following typed `init`
properties directly on `DeviceInfo` during enumeration. This follows the ADR-0024
Layer 3 promotion rule: scalar, enumeration-time values become typed `init` properties
on `DeviceInfo`, not entries in `Properties`.

| Property | Type | Source |
|---|---|---|
| `HidUsagePage` | `ushort?` | `HidP_GetCaps` (Windows), `ioctl(HIDIOCGRDESC)` parse (Linux), `IOHIDDeviceGetProperty(kIOHIDPrimaryUsagePageKey)` (macOS) |
| `HidUsage` | `ushort?` | Same sources as `HidUsagePage` |
| `HidMaxInputReportLength` | `int?` | `HidP_GetCaps.InputReportByteLength` (Windows); report descriptor parse (Linux/macOS) |
| `HidMaxOutputReportLength` | `int?` | Same |
| `HidMaxFeatureReportLength` | `int?` | Same |

A C#14 extension property block in `Periphery.Hid` computes predicates over these
typed properties, providing a fluent filter API without adding HID-specific methods
to the core `DeviceInfo` type:

```csharp
// In Periphery.Hid (net10.0)
extension(DeviceInfo device)
{
    /// <summary>The HID usage page, or <see langword="null"/> if not yet enriched.</summary>
    public ushort? HidUsagePage => device.HidUsagePage;

    /// <summary>The HID usage, or <see langword="null"/> if not yet enriched.</summary>
    public ushort? HidUsage => device.HidUsage;

    /// <summary>
    /// True when this is a Generic Desktop device (usage page 0x0001).
    /// Requires the <see cref="HidDeviceEnricher"/> to have been registered on the query.
    /// </summary>
    public bool IsGenericDesktop =>
        device.HidUsagePage == HidUsagePage.GenericDesktop;
}
```

Consumers who register this enricher can filter by usage page without ever opening a
handle:

```csharp
Devices.Enumerate()
    .OfCategory(DeviceCategory.Hid)
    .WithEnricher(new HidDeviceEnricher())  // populates typed HidUsagePage, HidUsage, etc.
    .Where(d => d.IsGenericDesktop)         // C#14 extension property
    .ToListAsync();
```

---

## Relationship to ADR-0019 and ADR-0024

`Periphery.Hid` is a direct validation of the ADR-0019 architecture, and the first
package to instantiate the canonical three-layer model formalised in ADR-0024.

| ADR-0019/0024 requirement | Validated by `Periphery.Hid` |
|---|---|
| `IDeviceEnricher` interface (sub-kind A) | `HidDeviceEnricher` reads OS HID caps without opening a handle |
| Layer 3 promotion rule: scalar OS-enumerable values → typed `init` property on `DeviceInfo` | `HidUsagePage`, `HidUsage`, `HidMaxInputReportLength`, `HidMaxOutputReportLength`, `HidMaxFeatureReportLength` as typed nullable properties |
| Layer 3 promotion rule: computed predicates → C#14 extension property block only | `IsGenericDesktop` and similar predicates live in `extension(DeviceInfo)` in `Periphery.Hid`, not as methods on `DeviceInfo` |
| Layer 1: `static OpenAsync(DeviceInfo)`, `IAsyncDisposable` | `HidDevice.OpenAsync` + `DisposeAsync` |
| Layer 2: composes `DeviceTracker` via `StateChanged`; static factory hides composition | `HidDeviceProxy.OpenAsync(DeviceProfile)` — factory owns `DeviceTracker` internally |
| `IsActive` vs `IsConnected` semantic split | `DeviceInfo.IsActive` = OS enumeration snapshot; `HidDeviceProxy.IsConnected` = open platform handle |

Once `Periphery.Hid` ships, `Periphery.Usb` is the same skeleton with a larger transfer
surface, a descriptor tree, and explicit endpoint/interface management.

---

## Relationship to ADR-0025

**ADR-0025** (Extensible `DeviceCategory`) answers OQ-003 (below). Fine-grained HID
sub-categories such as `DeviceCategory.Gamepad`, `DeviceCategory.Keyboard`, and
`DeviceCategory.Mouse` are registered by `Periphery.Hid` at startup via
`[ModuleInitializer]` calling `DeviceCategoryRegistry.Register*` and
`RegisterDisplayName`. Platform map default arms consult the registry before throwing,
so these categories round-trip through JSON via `DeviceCategoryJsonConverter` and
work in all filter operations without core library changes.

---

## Consequences

### Positive

- **POS-001**: No driver prerequisites. HID devices work on Windows, Linux, and macOS with
  inbox OS drivers. No WinUSB, no `libwdi`, no udev rules beyond read permission on
  `/dev/hidraw*`.
- **POS-002**: The transfer surface is four methods. The entire API can be understood,
  implemented, and tested in a single iteration.
- **POS-003**: A large population of test targets is available on every developer machine
  (keyboards, mice, gamepads, audio controls, custom firmware devices).
- **POS-004**: Validates the ADR-0019 extension architecture cheaply before the larger
  `Periphery.Usb` investment.
- **POS-005**: `UsagePage` and `Usage` are universally recognised HID fields that consumers
  use for device classification — more specific than `DeviceCategory.Hid` alone.

### Negative

- **NEG-001**: Windows restricts concurrent HID access for some device classes (keyboards,
  mice) at the OS level. `OpenAsync` will fail with `ACCESS_DENIED` for exclusive-mode
  devices unless the caller has elevated privileges or uses a shared-access flag.
- **NEG-002**: Linux `/dev/hidraw*` nodes require either `root` or a `udev` rule granting
  read/write permissions. This is a deployment concern, not an API concern, but must be
  documented prominently.
- **NEG-003**: `HidDevice.ReadReportAsync` is a single-consumer blocking read. Multiple
  concurrent readers are not supported in v1; consumers who need fan-out must implement
  their own buffering.
- **NEG-004**: The HID report descriptor (the raw byte array describing the report format)
  is not parsed in v1. Consumers who need semantic field extraction (e.g. "byte 3 is the
  X axis") must parse it themselves. A structured report descriptor parser is deferred.

---

## Alternatives Considered

### A — Start with `Periphery.Serial`

Serial ports are even simpler than HID (just a stream), but `System.IO.Ports.SerialPort`
already exists in the BCL and covers most use cases. `Periphery.Serial` would add
enumeration integration but minimal new I/O value. HID has no BCL equivalent.

### B — Start with `Periphery.Usb` directly

The full USB surface is implementable, but descriptor parsing, endpoint discovery,
interface claiming, and multiple transfer types create too large a surface for a first
implementation. Defects in the extension architecture are harder to find and fix under
that surface area. HID is the right minimum viable validation.

### C — Implement HID as part of the core `Periphery` library

Rejected. HID I/O violates the core library's discovery-only contract. Even if HID
report reading is simple, adding it to the core library introduces platform-native I/O
handles, `IAsyncDisposable` resources, and OS-level access constraints that have no
place in an enumeration library.

---

## Open Questions

- **OQ-001**: Should `HidDeviceProxy` expose an `IAsyncEnumerable<HidReport>` stream
  in addition to the callback/event model? A streaming interface would be more idiomatic
  for high-frequency input devices (gamepads, motion sensors).
- **OQ-002**: Should `Periphery.Hid` ship with a `HidUsagePage` enum covering the
  standard HID usage pages (Generic Desktop, Keyboard, Consumer, etc.)? Useful for
  filtering but duplicates data already in the USB HID specification.
- **OQ-003**: ~~Should `Periphery.Hid` also add `DeviceCategory.Gamepad`,
  `DeviceCategory.Keyboard`, `DeviceCategory.Mouse` as sub-categories resolvable from
  `UsagePage`/`Usage`?~~ **Resolved → ADR-0025.** Extension categories are registered
  by `Periphery.Hid` via `[ModuleInitializer]` + `DeviceCategoryRegistry` in the
  extension range (≥ 100 000 for third-party; first-party allocation in the 1000–9999
  range). `DeviceCategoryJsonConverter` handles serialisation of these values
  transparently.