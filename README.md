# Periphery

A modern, cross-platform .NET library for discovering hardware devices — USB, Bluetooth, network adapters, displays, and more — with a clean, LINQ-friendly API.

> **Windows, Linux, and macOS providers are complete.**

## Why Periphery?

Enumerating connected hardware today means reaching for platform-specific APIs — SetupAPI on Windows, `udev` on Linux, IOKit on macOS — each with its own conventions and quirks.
Periphery provides a **single high-level surface** that abstracts those differences away so you can:

- **Discover** devices by category (USB, Bluetooth, Display, Network, ...) with one API.
- **Query** devices using familiar LINQ expressions.
- **Monitor** arrival, departure, activation, and deactivation via events.
- **Write cross-platform code** that just works on Windows, Linux, and macOS.

The **core library focuses on discovery** — it tells you what's plugged in. Protocol-level I/O lives in **companion extension libraries** layered on the same device model. Keeping the core enumeration-only holds its runtime dependency surface to a single abstractions package (`Microsoft.Extensions.Logging.Abstractions`); the extensions opt into real device communication when you need it.

| Package | What it does |
|---|---|
| [`Periphery`](https://github.com/charles8051/periphery/tree/main/src/Periphery) | Core: enumeration, watching, tracking. The only runtime dependency is `Microsoft.Extensions.Logging.Abstractions` |
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

> **Extensions are Windows + Linux.** Enumeration works on all three platforms, but the
> I/O extensions ship Windows and Linux backends only — `CameraDevice`, `HidDevice`,
> `UsbDevice`, and `MonitorDevice` throw `PlatformNotSupportedException` on macOS. The
> AVFoundation and IOKit HID/USB backends are planned, not written.

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

// Two orthogonal transitions. Presence is whether the OS knows the device at
// all; activity is whether it is usable right now. For Bluetooth that is
// exactly the difference between paired and connected.
watcher.Appeared    += (_, e) => Console.WriteLine($"+ paired:       {e.Device.Name}");
watcher.Activated   += (_, e) => Console.WriteLine($"+ connected:    {e.Device.Name}");
watcher.Deactivated += (_, e) => Console.WriteLine($"- disconnected: {e.Device.Name}");
watcher.Disappeared += (_, e) => Console.WriteLine($"- unpaired:     {e.Device.Name}");

await watcher.StartAsync();
```

Most categories collapse the two. A USB device becomes present and active on the same
plug event, so `Appeared` and `Activated` arrive together and either one will do.
Bluetooth is where they come apart: a paired speaker that is switched off stays present
and goes inactive, and a single `IsConnected` flag would either hide it or claim it is
gone. Network adapters behave the same way when disabled. See
[ADR-0004](https://github.com/charles8051/periphery/blob/main/docs/adr/0004-two-level-device-state-model.md).

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
// Bind a serial device by identity, not by COM number. The OS assigns the port
// name, and it moves across reboots and re-plugs; the VID/PID and serial number
// do not. The proxy reopens the port wherever it lands next.
var scanner = new DeviceProfile(
    f => f.OfCategory(DeviceCategory.Ports).WithUsbId("0403", "6001"),
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

- [.NET 10](https://dotnet.microsoft.com/) or later (libraries also ship a `net8.0` target, offered best-effort — it is built but not covered by the test suite; see [ADR-0069](https://github.com/charles8051/periphery/blob/main/docs/adr/0069-restore-net8-tfm-untested.md))
- **Windows:** No additional dependencies (uses SetupAPI and cfgmgr32 via P/Invoke)
- **Linux:** Requires `libudev.so.1`. Systemd-based distros already have it; on a minimal image install
  `libudev-dev` or `eudev-dev`.
  - `Periphery.Usb` also needs `libusb-1.0.so.0` 1.0.23 or newer — `libusb-1.0-0` on Debian and Ubuntu.
  - `Periphery.Hid` and `Periphery.Camera` need nothing extra. They call the kernel ABIs directly,
    hidraw and V4L2.
  - Opening a device node usually takes a udev rule or a group membership: `video` for cameras, hidraw
    and usbfs rules for HID and USB. See
    [ADR-0057](https://github.com/charles8051/periphery/blob/main/docs/adr/0057-linux-extension-backends.md).
- **macOS:** No additional dependencies (uses IOKit.framework and CoreFoundation.framework via P/Invoke)

## Getting Started

```bash
git clone https://github.com/charles8051/periphery.git
cd periphery
dotnet build
```

Packages are published to [nuget.org](https://www.nuget.org/profiles/clee781).
Every release so far is a prerelease, so the flag is required — without it NuGet
reports *"There are no stable versions available"* and adds nothing:

```bash
dotnet add package Periphery --prerelease
```

## Device Categories

A **category** answers *which OS subsystem surfaced this device* — it's single-valued and drives enumeration routing (SetupAPI class GUID / udev subsystem / IOKit class). All providers are complete on all three platforms.

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

A **tag** answers a different question — *what can this device do?* Tags are multi-valued, cross-cutting, and added by enrichers during enumeration; query them with `WithTag(...)`. Five identifiers that used to be categories are now tags ([ADR-0051](https://github.com/charles8051/periphery/blob/main/docs/adr/0051-demote-capability-categories-to-tags.md)), because each describes a capability a device *has* rather than the subsystem that surfaced it:

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

> 🟡 **Windows-first.** The Windows class-GUID signals are live now, so each tag works on Windows exactly as the old category did. The Linux/macOS USB-class paths are written and dormant — they light up when cross-platform `DeviceInfo.UsbClassCode` population lands (deferred while Periphery builds out Windows depth first; see [ADR-0051](https://github.com/charles8051/periphery/blob/main/docs/adr/0051-demote-capability-categories-to-tags.md)). `Hid`, `Audio`, and `Battery` are also available as capability tags emitted by enrichers (e.g. HID battery levels via [`Periphery.Hid`](https://github.com/charles8051/periphery/tree/main/src/Periphery.Hid)).

## OpenCV without `VideoCapture(0)`

`VideoCapture(0)` is an index into whatever order the OS enumerated in, and it
moves when a device is replugged or a virtual camera installs itself. On a
machine with two identical cameras, no argument means *the one on the left*.
Periphery answers that for every category on all three platforms;
[`Periphery.Camera.OpenCvSharp`](https://github.com/charles8051/periphery/tree/main/src/Periphery.Camera.OpenCvSharp) hands the
pixels to OpenCV without a copy.

Worked examples, the three entry points, the native-payload choice and the
lease-lifetime trap are in
[that package's README](https://github.com/charles8051/periphery/blob/main/src/Periphery.Camera.OpenCvSharp/README.md).

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

1. **Discovery in the core, interaction in extensions.** The core tells you what's connected; protocol-level communication (camera capture, HID reports, raw USB, DDC/CI, …) lives in companion extension libraries layered on the core's device model.
2. **Platform parity.** Every device category exposed in the public API must be supportable on all target platforms, even if implementations ship incrementally.
3. **LINQ-native.** Device queries compose naturally with `Where`, `Select`, `OrderBy`, and friends.
4. **No third-party dependencies in the core or the I/O extensions.** Platform back-ends use only built-in OS APIs (SetupAPI/cfgmgr32, udev, IOKit) via P/Invoke or native interop. `Microsoft.Extensions.Logging.Abstractions` is the one exception. An opt-in integration package is where a third-party dependency belongs: `Periphery.Camera.Avalonia` references `Avalonia` and `Periphery.Camera.OpenCvSharp` references `OpenCvSharp4`, and you take either package only if you want it. See [`docs/patterns/integration-package-placement.md`](https://github.com/charles8051/periphery/blob/main/docs/patterns/integration-package-placement.md).
5. **Async-first.** Hardware enumeration can be slow; all public entry points return `Task` or `IAsyncEnumerable`.

## Contributing

Contributions are welcome. Start with [CONTRIBUTING.md](https://github.com/charles8051/periphery/blob/main/CONTRIBUTING.md) for how to
build, test and format, and [ARCHITECTURE.md](https://github.com/charles8051/periphery/blob/main/docs/ARCHITECTURE.md) for a deeper look
at the design before opening a PR.

Security reports go through [SECURITY.md](https://github.com/charles8051/periphery/blob/main/SECURITY.md), not the issue tracker.

Deferred work, bugs and design questions are tracked as
[GitHub issues](https://github.com/charles8051/periphery/issues) — that is the
only backlog. Architectural decisions live in [docs/adr/](https://github.com/charles8051/periphery/tree/main/docs/adr); an issue
that needs one references it by number.

### Formatting

CSharpier, pinned as a local tool. The tree is not formatted yet, so format only
the files you touch — see [CONTRIBUTING.md](https://github.com/charles8051/periphery/blob/main/CONTRIBUTING.md#formatting) for the
commands and why a repo-wide pass is its own change.

## License

[PolyForm Small Business 1.0.0](https://polyformproject.org/licenses/small-business/1.0.0) -
see [LICENSE.md](https://github.com/charles8051/periphery/blob/main/LICENSE.md).

Source-available, not open source. Every right the licence grants - including
making changes and redistributing - is granted only for a *permitted purpose*, and
use for the benefit of a company is a permitted purpose only below the
employee-count and revenue thresholds the licence sets.

[LICENSE.md](https://github.com/charles8051/periphery/blob/main/LICENSE.md) is the authoritative statement of the terms. This paragraph
points at it and is not a summary of it.