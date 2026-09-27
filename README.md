# Periphery

A cross-platform .NET library for discovering hardware devices (USB, Bluetooth, network adapters, displays, and more) with a LINQ-friendly API. One API covers Windows, Linux, and macOS.

```bash
dotnet add package Periphery --prerelease
```

Every release so far is a prerelease. Before upgrading, read [BREAKING-CHANGES.md](https://github.com/charles8051/periphery/blob/main/docs/BREAKING-CHANGES.md).

## Quick Look

```csharp
// All active USB devices
var usb = await Devices.Enumerate()
    .OfCategory(DeviceCategory.Usb)
    .Active()
    .ToListAsync();

// Compose a query with LINQ
var mice = await Devices.Enumerate()
    .OfCategory(DeviceCategory.Hid)
    .ByManufacturer("Logitech")
    .OrderBy(d => d.Name)
    .ToListAsync();

// Find one device by USB VID/PID
var board = await Devices.Enumerate()
    .WithUsbId("1234", "5678")
    .FirstOrDefaultAsync();
```

Watch for devices arriving and leaving:

```csharp
await using var watcher = Devices.Watch().OfCategory(DeviceCategory.Usb);

watcher.Appeared    += (_, e) => Console.WriteLine($"+ {e.Device.Name}");
watcher.Disappeared += (_, e) => Console.WriteLine($"- {e.Device.Name}");

await watcher.StartAsync();
```

Track one device by identity:

```csharp
await using var watcher = Devices.Watch();

var mouse = watcher.AddTracker(
    t => t.OfCategory(DeviceCategory.Usb).WithUsbId("046D", "C52B"),
    name: "Mouse");

mouse.StateChanged += (_, _) => Console.WriteLine($"Mouse present: {mouse.IsPresent}");

await watcher.StartAsync();
```

Open a serial device by identity instead of by COM number. The proxy reopens the port when the device comes back, wherever the OS puts it:

```csharp
var scanner = new DeviceProfile(
    f => f.OfCategory(DeviceCategory.Ports)
          .WithUsbId("0403", "6001")
          .WithSerialNumber("A9012XYZ"),
    name: "Scanner");

SerialPort? port = null;   // from the System.IO.Ports package

await using var proxy = await DeviceProxy.OpenAsync(
    scanner,
    onActivated: (info, ct) =>
    {
        port = new SerialPort(info.PortName.ToString(), 115_200);
        port.Open();
        return Task.CompletedTask;
    },
    onDeactivated: _ =>
    {
        port?.Dispose();
        return Task.CompletedTask;
    });
```

More in [`examples/`](https://github.com/charles8051/periphery/tree/main/examples).

## Device categories

USB, Bluetooth, Network, Display, Monitor, HID, Keyboard, Mouse, Audio, Storage, Ports (serial), Battery, and Camera.

Devices also carry capability tags, such as `Printer`, `Sensor`, or `SmartCard`, that cut across categories:

```csharp
var printers = await Devices.Enumerate().WithTag(DeviceTags.Printer).ToListAsync();
```

## Packages

The core package only discovers devices. Extension packages talk to them.

| Package | What it does |
|---|---|
| [`Periphery`](https://github.com/charles8051/periphery/tree/main/src/Periphery) | Enumerate, watch, and track devices |
| [`Periphery.Camera`](https://github.com/charles8051/periphery/tree/main/src/Periphery.Camera) | Frame capture, with [Avalonia](https://github.com/charles8051/periphery/tree/main/src/Periphery.Camera.Avalonia) and [OpenCvSharp](https://github.com/charles8051/periphery/tree/main/src/Periphery.Camera.OpenCvSharp) integrations |
| [`Periphery.Hid`](https://github.com/charles8051/periphery/tree/main/src/Periphery.Hid) | HID reports, such as battery levels |
| [`Periphery.Monitor`](https://github.com/charles8051/periphery/tree/main/src/Periphery.Monitor) | Monitor brightness, power, input, resolution, and orientation |
| [`Periphery.Usb`](https://github.com/charles8051/periphery/tree/main/src/Periphery.Usb) | Raw USB I/O |
| [`Periphery.Treehopper`](https://github.com/charles8051/periphery/tree/main/src/Periphery.Treehopper) | Treehopper I/O board SDK |
| [`Periphery.Bootloader`](https://github.com/charles8051/periphery/tree/main/src/Periphery.Bootloader) | Firmware flashing over EFM8 and STM32 bootloaders |
| [`Periphery.Cli`](https://github.com/charles8051/periphery/tree/main/src/Periphery.Cli) | The `periphery` command-line tool |

`Periphery.Camera`, `Periphery.Hid`, `Periphery.Monitor`, and `Periphery.Usb` support Windows and Linux.

## Requirements

- [.NET 10](https://dotnet.microsoft.com/) or later. A best-effort `net8.0` build also ships.
- **Linux:** `libudev.so.1`. `Periphery.Usb` also needs `libusb-1.0` 1.0.23 or newer.
- **Windows and macOS:** no native dependencies.

## Design principles

1. **Discovery in the core, I/O in extensions.**
2. **The same API on every platform.**
3. **LINQ-native.** Queries compose with `Where`, `Select`, `OrderBy`, and the rest.
4. **No third-party dependencies** in the core or the I/O extensions. Integrations such as Avalonia and OpenCvSharp are separate, opt-in packages.
5. **Async-first.** Every entry point returns `Task` or `IAsyncEnumerable`.

## Contributing

Contributions are welcome. Start with [CONTRIBUTING.md](https://github.com/charles8051/periphery/blob/main/CONTRIBUTING.md) and [ARCHITECTURE.md](https://github.com/charles8051/periphery/blob/main/docs/ARCHITECTURE.md). Bugs and ideas go in [GitHub issues](https://github.com/charles8051/periphery/issues). Report security issues through [SECURITY.md](https://github.com/charles8051/periphery/blob/main/SECURITY.md).

## License

[PolyForm Small Business 1.0.0](https://polyformproject.org/licenses/small-business/1.0.0) -
see [LICENSE.md](https://github.com/charles8051/periphery/blob/main/LICENSE.md).

Source-available, not open source. Every right the licence grants - including
making changes and redistributing - is granted only for a *permitted purpose*, and
use for the benefit of a company is a permitted purpose only below the
employee-count and revenue thresholds the licence sets.

[LICENSE.md](https://github.com/charles8051/periphery/blob/main/LICENSE.md) is the authoritative statement of the terms. This paragraph
points at it and is not a summary of it.
