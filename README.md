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
| [`Periphery`](src/Periphery) | Core: enumeration, watching, tracking. The only runtime dependency is `Microsoft.Extensions.Logging.Abstractions` |
| [`Periphery.Camera`](src/Periphery.Camera) | Frame capture (Media Foundation / V4L2) |
| [`Periphery.Camera.Avalonia`](src/Periphery.Camera.Avalonia) | `CameraPreview` control for Avalonia UI |
| [`Periphery.Camera.OpenCvSharp`](src/Periphery.Camera.OpenCvSharp) | Captured frames as an OpenCV `Mat`, without `VideoCapture` |
| [`Periphery.Camera.Testing`](src/Periphery.Camera.Testing) | Hardware-free camera test seam (ADR-0065) |
| [`Periphery.Hid`](src/Periphery.Hid) | HID reports (e.g. battery levels) |
| [`Periphery.Monitor`](src/Periphery.Monitor) | DDC/CI brightness / power / input, resolution, orientation |
| [`Periphery.Usb`](src/Periphery.Usb) | Raw USB I/O via WinUSB / libusb backends |
| [`Periphery.Treehopper`](src/Periphery.Treehopper) | Treehopper board SDK on a pure core (ADR-0052) |
| [`Periphery.Firmware`](src/Periphery.Firmware) + [`Periphery.Bootloader`](src/Periphery.Bootloader) | Firmware images and the bootloader contract (ADR-0061) |
| [`Periphery.Bootloader.Efm8.Usb`](src/Periphery.Bootloader.Efm8.Usb) | EFM8 USB bootloader backend |
| [`Periphery.Treehopper.Control`](src/Periphery.Treehopper.Control) | Board control surface |
| [`Periphery.Treehopper.Control.Cli`](src/Periphery.Treehopper.Control.Cli) | Command-line front end for board control |
| [`Periphery.Treehopper.Firmware`](src/Periphery.Treehopper.Firmware) | Treehopper firmware images |
| [`Periphery.Treehopper.Libraries`](src/Periphery.Treehopper.Libraries) | Peripheral drivers on the Treehopper board (LED strips, displays) |
| [`Periphery.Cli`](src/Periphery.Cli) | `periphery` command-line device tooling |

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

// Two orthogonal transitions: presence in the OS device tree, and activity.
watcher.Appeared    += (_, e) => Console.WriteLine($"+ appeared:    {e.Device.Name}");
watcher.Activated   += (_, e) => Console.WriteLine($"+ activated:   {e.Device.Name}");
watcher.Deactivated += (_, e) => Console.WriteLine($"- deactivated: {e.Device.Name}");
watcher.Disappeared += (_, e) => Console.WriteLine($"- disappeared: {e.Device.Name}");

await watcher.StartAsync();
```

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
// Trackers can be created upfront (e.g. from configuration) and attached later
var tracker = new DeviceTracker(t => t.OfCategory(DeviceCategory.Usb).WithUsbId("046D", "C52B"), name: "Mouse");
tracker.StateChanged += (_, _) => UpdateDashboard();

await using var watcher = Devices.Watch().AddTracker(tracker);
await watcher.StartAsync();
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

A **tag** answers a different question — *what can this device do?* Tags are multi-valued, cross-cutting, and added by enrichers during enumeration; query them with `WithTag(...)`. Five identifiers that used to be categories are now tags ([ADR-0051](docs/adr/0051-demote-capability-categories-to-tags.md)), because each describes a capability a device *has* rather than the subsystem that surfaced it:

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

> 🟡 **Windows-first.** The Windows class-GUID signals are live now, so each tag works on Windows exactly as the old category did. The Linux/macOS USB-class paths are written and dormant — they light up when cross-platform `DeviceInfo.UsbClassCode` population lands (deferred while Periphery builds out Windows depth first; see [ADR-0051](docs/adr/0051-demote-capability-categories-to-tags.md)). `Hid`, `Audio`, and `Battery` are also available as capability tags emitted by enrichers (e.g. HID battery levels via [`Periphery.Hid`](src/Periphery.Hid)).

## OpenCV Without `VideoCapture(0)`

OpenCV has no camera identity model. `VideoCapture(0)` is an index into whatever
order the operating system enumerated in, and it moves when a device is
replugged, when a virtual camera installs itself, or when a laptop lid opens. On
a machine with two identical cameras there is no argument you can pass that
means *the one on the left*. Periphery already answers that question, for every
device category, on all three platforms. The half that was missing was handing
the pixels to OpenCV, and [`Periphery.Camera.OpenCvSharp`](src/Periphery.Camera.OpenCvSharp)
is that half.

```csharp
using OpenCvSharp;
using Periphery;
using Periphery.Camera;
using Periphery.Camera.OpenCvSharp;

// Pick the camera by what it is, not by where it landed in a list.
var device = await Devices.Enumerate()
    .OfCategory(DeviceCategory.Camera)
    .WithUsbId("046D", "0825")
    .FirstOrDefaultAsync()
    ?? throw new InvalidOperationException("That camera is not attached.");

var snapshot = await CameraDevice.ReadSnapshotAsync(device);
var format = snapshot.Formats
    .WithinBox(1280, 720)
    .PreferPixelFormat(CameraPixelFormat.Yuy2)
    .ThenByHighestFrameRate()
    .First();

await using var session = await CameraSession.OpenAsync(device, new CameraConfiguration(format));

await foreach (var frame in session.CaptureAsync())
{
    using (frame)
    using (var bgr = frame.ToBgr())     // any capture format -> CV_8UC3 BGR
    {
        Cv2.ImShow("preview", bgr);
        Cv2.WaitKey(1);
    }
}
```

No `VideoCapture`, no index, no format string. The camera was chosen by
vendor and product ID; it could equally have been chosen by serial number, by
name, by USB port path, or by a `DeviceWatcher` that hands you the camera the
moment someone plugs it in.

### Two identical cameras

This is the case the index model cannot express at all. Two of the same webcam
report the same name and the same VID/PID, so the only thing that separates them
is identity the OS assigns — a serial number if the device has one, and the
physical port path if it does not.

```csharp
var cameras = await Devices.Enumerate()
    .OfCategory(DeviceCategory.Camera)
    .WithName("HD Pro Webcam C920")
    .ToListAsync();

// Real UVC cameras usually carry a serial; the ones that don't are told apart
// by the port they are plugged into, which is stable across reboots.
var left  = cameras.Single(c => c.SerialNumber == "A1B2C3D4");
var right = cameras.Single(c => c.SerialNumber == "E5F6A7B8");

await using var leftSession  = await CameraSession.OpenAsync(left,  new CameraConfiguration(format));
await using var rightSession = await CameraSession.OpenAsync(right, new CameraConfiguration(format));

// Two sessions, two independent capture loops, each pinned to a known lens.
```

Assigning a stereo pair the wrong way round is not a crash; it is a depth map
that is quietly inverted. An index cannot tell you which one you got, and a
serial number can.

### Three entry points, separated by who owns the pixels

| Call | Copies | Lifetime | Use it when |
|---|---|---|---|
| `frame.AsMat()` | no | valid inside the returned `MatScope` | you convert or measure inside the capture loop |
| `frame.ToMat()` | yes | you own the `Mat` | the raw capture format has to outlive the frame |
| `frame.ToBgr()` | yes | you own the `Mat` | you want an image rather than a capture format |

`AsMat` is the default. Wrapping costs nothing measurable, and a 1080p YUY2 to
BGR conversion is 0.126 ms against 1.83 ms for the clone `ToMat` has to make —
copying "to be safe" is fourteen times the cost of the conversion it is
supposedly protecting. The reason it returns a scope rather than a `Mat` is that
frames are pooled: once the lease is released the buffer is handed to the next
frame and refilled, and a `Mat` still pointing at it reads a later frame's
pixels rather than faulting. A type you have to dispose puts that decision in
the call-site syntax.

### The native payload is yours to pick

`Periphery.Camera.OpenCvSharp` references `OpenCvSharp4` — the managed binding —
and no `OpenCvSharp4.runtime.*` package. Install the payload for the platform
you deploy to: `OpenCvSharp4.runtime.win` on Windows,
`OpenCvSharp4.official.runtime.linux-x64` on Linux. **macOS has no current
first-party package** — the newest is 4.6.0.20230105 against a 4.13 binding — so
a macOS deployment needs a third-party build. Without a payload the package
restores and compiles, and the first OpenCV call throws
`DllNotFoundException`.

The package is named after the binding rather than the library, because
`OpenCvSharp4` and `Emgu.CV` are incompatible bindings of the same OpenCV and a
`Periphery.Camera.OpenCv` would claim a name a future Emgu package could not
share.

### MJPEG

MJPEG is the default 1080p30 mode on most UVC webcams and it has no `Mat` shape,
so the three methods split:

- **`AsMat` throws.** There is no header to build over a compressed blob, and
  therefore no zero-copy path to offer.
- **`ToMat` throws.** A byte-for-byte copy of JPEG is a `1 x n` vector of
  encoded bytes, which is not what "a copy of the frame's pixels" should hand
  back.
- **`ToBgr` decodes it**, straight from the frame's span with no intermediate
  `byte[]`. This is what makes `ToBgr` total over every format a camera can
  deliver, and it is why the package has a third method rather than two.

Both refusals name `ToBgr` in the message. The one other refusal is `Gray16` to
BGR: narrowing 16 bits to 8 needs a range, and a fixed `/257` renders most depth
and IR sensors black — take the CV_16UC1 `Mat` and apply `Cv2.Normalize` or
`Cv2.ConvertScaleAbs` yourself.

### Do not hold the lease through inference

Convert inside the lease; do the slow work outside it.

```csharp
await foreach (var frame in session.CaptureAsync())
{
    Mat bgr;
    using (frame)
        bgr = frame.ToBgr();        // ~0.1 ms, and the lease ends here

    await queue.Writer.WriteAsync(bgr);   // inference runs on the other side
}
```

A consumer slower than the frame interval parks the producer, because the
session's delivery channel is `BoundedChannelFullMode.Wait`. Frames are then
lost upstream inside Media Foundation or V4L2, where Periphery cannot count
them: `FramesDropped` stays at zero while the delivered rate halves. That stall
has no instrument today
([#322](https://github.com/charles8051/periphery/issues/322)), so the symptom is
a frame rate that is quietly wrong rather than a warning in a log.

## Requirements

- [.NET 10](https://dotnet.microsoft.com/) or later (libraries also ship a `net8.0` target, offered best-effort — it is built but not covered by the test suite; see ADR-0069)
- **Windows:** No additional dependencies (uses SetupAPI and cfgmgr32 via P/Invoke)
- **Linux:** Requires `libudev.so.1` (included in systemd-based distros; install `libudev-dev` or `eudev-dev` on minimal images). `Periphery.Usb` additionally requires `libusb-1.0.so.0` >= 1.0.23 (`libusb-1.0-0` on Debian/Ubuntu); `Periphery.Hid` and `Periphery.Camera` use the kernel ABIs directly (hidraw, V4L2) with no extra libraries. Device-node access typically needs udev rules or group membership (`video` for cameras; hidraw/usbfs rules for HID/USB - see ADR-0057).
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
4. **No third-party dependencies in the core or the I/O extensions.** Platform back-ends use only built-in OS APIs (SetupAPI/cfgmgr32, udev, IOKit) via P/Invoke or native interop. `Microsoft.Extensions.Logging.Abstractions` is the one exception. An opt-in integration package is where a third-party dependency belongs: `Periphery.Camera.Avalonia` references `Avalonia` and `Periphery.Camera.OpenCvSharp` references `OpenCvSharp4`, and you take either package only if you want it. See [`docs/patterns/integration-package-placement.md`](docs/patterns/integration-package-placement.md).
5. **Async-first.** Hardware enumeration can be slow; all public entry points return `Task` or `IAsyncEnumerable`.

## Contributing

Contributions are welcome. Start with [CONTRIBUTING.md](CONTRIBUTING.md) for how to
build, test and format, and [ARCHITECTURE.md](docs/ARCHITECTURE.md) for a deeper look
at the design before opening a PR.

Security reports go through [SECURITY.md](SECURITY.md), not the issue tracker.

Deferred work, bugs and design questions are tracked as
[GitHub issues](https://github.com/charles8051/periphery/issues) — that is the
only backlog. Architectural decisions live in [docs/adr/](docs/adr/); an issue
that needs one references it by number.

### Formatting

Formatting is [CSharpier](https://csharpier.com), pinned as a local tool so everyone runs the same
version:

```
dotnet tool restore
dotnet csharpier format .
```

`.csharpierrc` sets `endOfLine: lf` to match `.gitattributes`; everything else is CSharpier's default.

**The existing tree is not formatted yet.** CSharpier would currently rewrite most of it, so a
repo-wide pass is a deliberate change of its own rather than something to fold into an unrelated PR.
Until that happens, format the files you touch and leave the rest alone.

## License

[PolyForm Small Business 1.0.0](https://polyformproject.org/licenses/small-business/1.0.0) -
see [LICENSE.md](LICENSE.md).

Source-available, not open source. Every right the licence grants - including
making changes and redistributing - is granted only for a *permitted purpose*, and
use for the benefit of a company is a permitted purpose only below the
employee-count and revenue thresholds the licence sets.

[LICENSE.md](LICENSE.md) is the authoritative statement of the terms. This paragraph
points at it and is not a summary of it.