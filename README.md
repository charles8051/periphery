# Periphery

A cross-platform .NET library for discovering hardware devices (USB, Bluetooth, network adapters, displays, and more) with a LINQ-friendly API.

## Why Periphery?

Each OS has its own device API: SetupAPI on Windows, `udev` on Linux, IOKit on macOS. Periphery puts one API over all three, so you can:

- **Discover** devices by category (USB, Bluetooth, Display, Network, ...).
- **Query** devices with LINQ.
- **Monitor** arrival, departure, activation, and deactivation via events.
- **Run the same code** on Windows, Linux, and macOS.

The core library only enumerates devices. Its one runtime dependency is `Microsoft.Extensions.Logging.Abstractions`. Device I/O lives in extension packages built on the same device model.

| Package | What it does |
|---|---|
| [`Periphery`](https://github.com/charles8051/periphery/tree/main/src/Periphery) | Core: enumeration, watching, tracking |
| [`Periphery.Camera`](https://github.com/charles8051/periphery/tree/main/src/Periphery.Camera) | Frame capture (Media Foundation / V4L2) |
| [`Periphery.Camera.Avalonia`](https://github.com/charles8051/periphery/tree/main/src/Periphery.Camera.Avalonia) | `CameraPreview` control for Avalonia UI |
| [`Periphery.Camera.OpenCvSharp`](https://github.com/charles8051/periphery/tree/main/src/Periphery.Camera.OpenCvSharp) | Captured frames as an OpenCV `Mat`, without `VideoCapture` |
| [`Periphery.Camera.Testing`](https://github.com/charles8051/periphery/tree/main/src/Periphery.Camera.Testing) | Hardware-free camera test seam (ADR-0065) |
| [`Periphery.Hid`](https://github.com/charles8051/periphery/tree/main/src/Periphery.Hid) | HID reports (e.g. battery levels) |
| [`Periphery.Monitor`](https://github.com/charles8051/periphery/tree/main/src/Periphery.Monitor) | DDC/CI brightness / power / input, resolution, orientation |
| [`Periphery.Usb`](https://github.com/charles8051/periphery/tree/main/src/Periphery.Usb) | Raw USB I/O via WinUSB / libusb backends |
| [`Periphery.Treehopper`](https://github.com/charles8051/periphery/tree/main/src/Periphery.Treehopper) | Treehopper board SDK on a pure core (ADR-0052) |
| [`Periphery.Firmware`](https://github.com/charles8051/periphery/tree/main/src/Periphery.Firmware) + [`Periphery.Bootloader`](https://github.com/charles8051/periphery/tree/main/src/Periphery.Bootloader) | Firmware images and the bootloader contract (ADR-0061) |
| [`Periphery.Bootloader.Efm8.Usb`](https://github.com/charles8051/periphery/tree/main/src/Periphery.Bootloader.Efm8.Usb) | EFM8 USB bootloader backend |
| [`Periphery.Treehopper.Control`](https://github.com/charles8051/periphery/tree/main/src/Periphery.Treehopper.Control) | Board control surface |
| [`Periphery.Treehopper.Control.Cli`](https://github.com/charles8051/periphery/tree/main/src/Periphery.Treehopper.Control.Cli) | Command-line front end for board control |
| [`Periphery.Treehopper.Firmware`](https://github.com/charles8051/periphery/tree/main/src/Periphery.Treehopper.Firmware) | Treehopper firmware images |
| [`Periphery.Treehopper.Libraries`](https://github.com/charles8051/periphery/tree/main/src/Periphery.Treehopper.Libraries) | Peripheral drivers on the Treehopper board (LED strips, displays) |
| [`Periphery.Cli`](https://github.com/charles8051/periphery/tree/main/src/Periphery.Cli) | `periphery` command-line device tooling |

> **Extensions are Windows + Linux.** On macOS, `CameraDevice`, `HidDevice`, `UsbDevice`,
> and `MonitorDevice` throw `PlatformNotSupportedException`. The AVFoundation and IOKit
> HID/USB backends are not written yet.

## Quick Look

```csharp
// One-shot: all active USB devices
var usb = await Devices.Enumerate()
    .OfCategory(DeviceCategory.Usb)
    .Active()
    .ToListAsync();

foreach (var device in usb)
    Console.WriteLine($"{device.Name} ({device.Id})");

// Fluent query with LINQ
var mice = await Devices.Enumerate()
    .OfCategory(DeviceCategory.Hid)
    .WithName("Mouse")
    .ByManufacturer("Logitech")
    .OrderBy(d => d.Name)
    .Take(5)
    .ToListAsync();

// USB VID/PID lookup
var specific = await Devices.Enumerate()
    .OfCategory(DeviceCategory.Usb)
    .WithUsbId("1234", "5678")
    .FirstOrDefaultAsync();

// IAsyncEnumerable — works with await foreach
await foreach (var device in Devices.Enumerate().OfCategory(DeviceCategory.Network))
    Console.WriteLine($"{device.Name} ({device.BusType})");
```

```csharp
// Real-time monitoring
await using var watcher = Devices.Watch()
    .OfCategory(DeviceCategory.Bluetooth);

// Presence: the OS knows the device. Activity: the device is usable now.
// For Bluetooth, that is paired vs. connected.
watcher.Appeared    += (_, e) => Console.WriteLine($"+ paired:       {e.Device.Name}");
watcher.Activated   += (_, e) => Console.WriteLine($"+ connected:    {e.Device.Name}");
watcher.Deactivated += (_, e) => Console.WriteLine($"- disconnected: {e.Device.Name}");
watcher.Disappeared += (_, e) => Console.WriteLine($"- unpaired:     {e.Device.Name}");

await watcher.StartAsync();
```

For most categories the two coincide. A USB device becomes present and active on the
same plug event. They diverge for Bluetooth, where a paired speaker that is switched off
stays present but goes inactive, and for disabled network adapters. See
[ADR-0004](https://github.com/charles8051/periphery/blob/main/docs/adr/0004-two-level-device-state-model.md).

> **On Windows, poll for Bluetooth activity.** `IsActive` is correct, but cfgmgr32 sends
> no notification when an already-paired device connects or disconnects. `Activated` and
> `Deactivated` do not fire for those transitions. Linux and macOS raise both events. See
> [ADR-0054](https://github.com/charles8051/periphery/blob/main/docs/adr/0054-windows-property-freshness-events-over-polling.md).
>
> `DeviceInfo` is an immutable snapshot. Enumerate again on each poll and compare by `Id`:
>
> ```csharp
> var wasActive = new Dictionary<DeviceId, bool>();
>
> while (!ct.IsCancellationRequested)
> {
>     foreach (var device in await Devices.Enumerate()
>                  .OfCategory(DeviceCategory.Bluetooth)
>                  .ToListAsync(ct))
>     {
>         if (wasActive.TryGetValue(device.Id, out var before) && before != device.IsActive)
>             Console.WriteLine($"{device.Name}: {(device.IsActive ? "connected" : "disconnected")}");
>
>         wasActive[device.Id] = device.IsActive;
>     }
>
>     await Task.Delay(TimeSpan.FromSeconds(2), ct);
> }
> ```
>
> Filter on `DeviceCategory.Bluetooth`. A Bluetooth peripheral enumerates as several
> devnodes, and only the `BTHENUM\DEV_…` node tracks link state.

```csharp
// Per-device tracking — each tracker has dual state (IsPresent + IsActive)
await using var watcher = Devices.Watch();

var mouse   = watcher.AddTracker(t => t.OfCategory(DeviceCategory.Usb).WithUsbId("046D", "C52B"), name: "Mouse");
var airpods = watcher.AddTracker(t => t.OfCategory(DeviceCategory.Bluetooth).WithName("AirPods"), name: "AirPods");

// ActivityStatus starts at Unknown and settles once initial enumeration completes (ADR-0056).
mouse.StateChanged += (_, _) => Console.WriteLine($"Mouse: {mouse.ActivityStatus}");

await watcher.StartAsync();
```

```csharp
// Bind a serial device by identity, not COM number. The port name moves across
// reboots and re-plugs; the proxy reopens the port wherever it lands.
var scanner = new DeviceProfile(
    f => f.OfCategory(DeviceCategory.Ports)
          .WithUsbId("0403", "6001")
          .WithSerialNumber("A9012XYZ"),   // optional if only one such device is attached
    name: "Scanner");

SerialPort? port = null;   // needs the System.IO.Ports package

await using var handle = await DeviceProxy.OpenAsync(
    scanner,
    onActivated: (info, ct) =>
    {
        port = new SerialPort(info.PortName!.Value.Value, baudRate: 115_200);
        port.Open();
        return Task.CompletedTask;
    },
    onDeactivated: _ =>
    {
        port?.Dispose();
        port = null;
        return Task.CompletedTask;
    });
```

`DeviceProxy` also takes `whileOpen` for a read loop, and a retry policy for devices
that enumerate before they are ready. See
[`examples/scripts/serial-device-handle.cs`](https://github.com/charles8051/periphery/blob/main/examples/scripts/serial-device-handle.cs).

```csharp
// Trackers can be created upfront (e.g. from configuration) and attached later
var tracker = new DeviceTracker(t => t.OfCategory(DeviceCategory.Usb).WithUsbId("046D", "C52B"), name: "Mouse");
tracker.StateChanged += (_, _) => UpdateDashboard();

await using var watcher = Devices.Watch().AddTracker(tracker);
await watcher.StartAsync();
```

## Requirements

- [.NET 10](https://dotnet.microsoft.com/) or later. Libraries also target `net8.0`, which is built but not tested ([ADR-0069](https://github.com/charles8051/periphery/blob/main/docs/adr/0069-restore-net8-tfm-untested.md)).
- **Windows:** no additional dependencies.
- **Linux:** `libudev.so.1`. Systemd-based distros have it; on a minimal image install `libudev-dev` or `eudev-dev`.
  - `Periphery.Usb` also needs `libusb-1.0.so.0` 1.0.23 or newer (`libusb-1.0-0` on Debian and Ubuntu).
  - Opening a device node usually takes a udev rule or group membership: `video` for cameras,
    hidraw and usbfs rules for HID and USB. See
    [ADR-0057](https://github.com/charles8051/periphery/blob/main/docs/adr/0057-linux-extension-backends.md).
- **macOS:** no additional dependencies.

## Getting Started

Packages are on [nuget.org](https://www.nuget.org/profiles/clee781). Every release so far
is a prerelease, so pass `--prerelease`:

```bash
dotnet add package Periphery --prerelease
```

Before upgrading, read [docs/BREAKING-CHANGES.md](docs/BREAKING-CHANGES.md).

To build from source:

```bash
git clone https://github.com/charles8051/periphery.git
cd periphery
dotnet build
```

## Device Categories

A **category** is the OS subsystem that surfaced the device: a SetupAPI class GUID, udev subsystem, or IOKit class. Each device has exactly one.

| Category | Windows | Linux | macOS |
|---|---|---|---|
| USB | ✅ | ✅ | ✅ |
| Bluetooth | ✅ | ✅ | ✅ |
| Network Adapters | ✅ | ✅ | ✅ |
| Display (GPU / adapter) | ✅ | ✅ | ✅ |
| Monitor (screen) | ✅ | ✅ | ✅ |
| HID | ✅ | ✅ | ✅ |
| Keyboard | ✅ | ✅ | ✅ |
| Mouse | ✅ | ✅ | ✅ |
| Audio | ✅ | ✅ | ✅ |
| Storage | ✅ | ✅ | ✅ |
| Ports (Serial) | ✅ | ✅ | ✅ |
| Battery | ✅ | ✅ | ✅ |
| Camera | ✅ | ✅ | ✅ |

## Capability Tags

A **tag** says what a device can do. A device can carry several, added by enrichers during enumeration. Query them with `WithTag(...)`. The five tags below were categories before [ADR-0051](https://github.com/charles8051/periphery/blob/main/docs/adr/0051-demote-capability-categories-to-tags.md).

```csharp
// "any scanner / still-image device", whichever subsystem it enumerated under
var scanners = await Devices.Enumerate().WithTag(DeviceTags.Imaging).ToListAsync();

// compose with a category to narrow the scan first
var receiptPrinter = await Devices.Enumerate()
    .OfCategory(DeviceCategory.Ports)      // a serial-attached printer
    .WithTag(DeviceTags.Printer)
    .FirstOrDefaultAsync();
```

| Tag | Windows | Linux | macOS | Detection signal |
|---|---|---|---|---|
| `Sensor` | ✅ | ✅ | ✅ | `Sensor` class / `iio` subsystem / HID usage page `0x20` |
| `SmartCard` | ✅ | 🟡 | ✅ | `SmartCardReader` class / `IOUSBSmartCardController` / USB class `0x0B` |
| `Imaging` | ✅ | 🟡 | 🟡 | `Image` class / USB class `0x06` |
| `Printer` | ✅ | 🟡 | 🟡 | `Printer` / `PnpPrinters` / `PrintQueue` classes / USB class `0x07` |
| `Biometric` | ✅ | — | — | `Biometric` class (Windows-only — USB biometric readers are vendor-specific) |

> 🟡 The Linux and macOS USB-class detection is written but dormant until cross-platform
> `DeviceInfo.UsbClassCode` population lands. Enrichers also emit `Hid`, `Audio`, and
> `Battery` tags, for example HID battery levels from
> [`Periphery.Hid`](https://github.com/charles8051/periphery/tree/main/src/Periphery.Hid).

## OpenCV without `VideoCapture(0)`

`VideoCapture(0)` indexes the OS enumeration order. That order changes when a device is
replugged or a virtual camera installs, and it cannot tell two identical cameras apart.
Periphery selects the camera by identity, and
[`Periphery.Camera.OpenCvSharp`](https://github.com/charles8051/periphery/tree/main/src/Periphery.Camera.OpenCvSharp)
hands its frames to OpenCV without a copy. Examples and lease-lifetime rules are in
[that package's README](https://github.com/charles8051/periphery/blob/main/src/Periphery.Camera.OpenCvSharp/README.md).

## One camera, several consumers

A preview, an inference graph and an encoder each need their own latency, queue depth
and drop policy. `Periphery.Camera` has no built-in router. It hands out refcounted
frames; fan them out with a producer loop and one bounded channel per consumer. The
recipe and `BufferCount` sizing are in
[the `Periphery.Camera` README](https://github.com/charles8051/periphery/blob/main/src/Periphery.Camera/README.md).

## Repository Layout

```
periphery/
├── Periphery.slnx                  # Solution — 28 src, 21 test, 7 example, 1 benchmark project
├── src/
│   ├── Periphery/                  # Core: enumeration, watching, tracking (net8.0;net10.0)
│   ├── Periphery.Camera[.Avalonia|.OpenCvSharp|.Testing]/
│   ├── Periphery.Hid/  Periphery.Monitor/  Periphery.Usb/
│   ├── Periphery.Treehopper[.Control|.Firmware|.Flasher|.Libraries][.Cli|.Gui]/
│   ├── Periphery.Firmware/  Periphery.Bootloader[.Efm8.Usb|.Stm32.Usb]/
│   ├── Periphery.FlashAnything[.Cli|.Gui][.Core]/
│   └── Periphery.Cli/  Periphery.Diagnostics/
├── tests/                          # One test project per src package, plus
│                                   #   *.Interop.Tests where a suite needs a
│                                   #   native payload (Category=Integration)
├── examples/                       # Runnable samples + single-file `dotnet run` scripts
├── benchmarks/                     # BenchmarkDotNet suites
└── docs/
    ├── ARCHITECTURE.md             # Detailed architecture & design decisions
    ├── adr/                        # Architecture Decision Records
    ├── patterns/                   # Cross-cutting conventions
    ├── surface/                    # Consumer-facing usage guides
    ├── plans/  feature-specs/      # Point-in-time design work
    └── explorations/  investigations/
```

Inside the core library:

```
src/Periphery/
├── Devices.cs                      # Static entry point: Enumerate(), Watch()
├── DeviceQuery.cs                  # Fluent, composable query (IAsyncEnumerable)
├── DeviceWatcher.cs                # Real-time Appeared/Activated/Deactivated/Disappeared monitor
├── DeviceTracker.cs                # Per-device observable state handle
├── MultiDeviceTracker.cs           # Observable set of matching devices
├── DeviceSessionHost.cs            # Session publication over a reconnecting handle
├── DeviceProxy[Base].cs            # Reconnect-resilient device handles (ADR-0027)
├── DeviceProfile.cs                # Named candidate in a multi-profile tracker
├── DeviceInfo.cs                   # Immutable device snapshot record
├── DeviceCategory.cs  DeviceTags.cs
├── DeviceFilter.cs                 # Filter predicate composition — the one source of truth
├── IDeviceProvider.cs              # Provider interfaces + runtime factory
├── *Enricher.cs                    # Tag-emitting enrichers (ADR-0026, ADR-0051)
├── DeviceReset.cs  Reset*.cs  *RecoveryPolicy.cs   # Reset/recovery escalation (ADR-0060)
├── Windows/                        # SetupAPI + cfgmgr32 + DisplayConfig
├── Linux/                          # libudev
├── MacOS/                          # IOKit / CoreFoundation
└── Serialization/                  # System.Text.Json converters for the BCL-typed fields
```

## Design Principles

1. **Discovery in the core, I/O in extensions.** Camera capture, HID reports, raw USB and DDC/CI live in extension packages built on the core's device model.
2. **Platform parity.** Every public device category must be supportable on every target platform.
3. **LINQ-native.** Queries compose with `Where`, `Select`, `OrderBy`, and the rest.
4. **No third-party dependencies in the core or the I/O extensions.** Platform backends call OS APIs through P/Invoke. `Microsoft.Extensions.Logging.Abstractions` is the one exception. Third-party dependencies go in opt-in integration packages, such as `Periphery.Camera.Avalonia` (Avalonia) and `Periphery.Camera.OpenCvSharp` (OpenCvSharp4). See [`docs/patterns/integration-package-placement.md`](https://github.com/charles8051/periphery/blob/main/docs/patterns/integration-package-placement.md).
5. **Async-first.** Every public entry point returns `Task` or `IAsyncEnumerable`.

## Contributing

Contributions are welcome. [CONTRIBUTING.md](https://github.com/charles8051/periphery/blob/main/CONTRIBUTING.md)
covers build, test and formatting; [ARCHITECTURE.md](https://github.com/charles8051/periphery/blob/main/docs/ARCHITECTURE.md)
covers the design. Report security issues through [SECURITY.md](https://github.com/charles8051/periphery/blob/main/SECURITY.md),
not the issue tracker.

[GitHub issues](https://github.com/charles8051/periphery/issues) are the only backlog.
Architectural decisions live in [docs/adr/](https://github.com/charles8051/periphery/tree/main/docs/adr).

### Formatting

CSharpier, pinned as a local tool. The tree is not formatted yet, so format only the
files you touch. See [CONTRIBUTING.md](https://github.com/charles8051/periphery/blob/main/CONTRIBUTING.md#formatting).

## License

[PolyForm Small Business 1.0.0](https://polyformproject.org/licenses/small-business/1.0.0) -
see [LICENSE.md](https://github.com/charles8051/periphery/blob/main/LICENSE.md).

Source-available, not open source. Every right the licence grants - including
making changes and redistributing - is granted only for a *permitted purpose*, and
use for the benefit of a company is a permitted purpose only below the
employee-count and revenue thresholds the licence sets.

[LICENSE.md](https://github.com/charles8051/periphery/blob/main/LICENSE.md) is the authoritative statement of the terms. This paragraph
points at it and is not a summary of it.
