# Device Handle Lifecycle

This pattern shows how to track a specific device and automatically open a
platform handle to it whenever it connects, keeping it open through disconnects
and reconnects without any manual watcher management. Three factory shapes
cover three levels of ceremony — choose the one that fits your situation.

---

## When to use this pattern

Use a device handle when you need a **long-lived, open connection** to a
specific device — not just its presence in the device tree. Examples:

- Reading from a serial port continuously until the cable is unplugged.
- Sending HID output reports whenever the gamepad is available.
- Streaming audio from a USB microphone and recovering transparently on reconnect.

If you only need to know *whether* a device is present (no I/O), use a plain
`DeviceTracker` instead (see `configuration-driven-tracking.md`).

---

## Choosing a factory shape

| Shape | Factory | Own watcher? | TDevice required? | Use when |
|---|---|---|---|---|
| **Self-contained** | `OpenAsync(DeviceProfile, ...)` | Yes — handle creates + owns it | No / Yes | One device, one handle, simple setup |
| **Shared watcher** | `Create(DeviceTracker, ...)` | No — borrows caller's watcher | No / Yes | Multiple devices sharing one OS subscription |
| **Extension package** | `OpenAsync` on a typed handle (e.g. `HidDeviceProxy`) | Yes | Yes (built-in) | Consuming a library that wraps `DeviceProxyBase` |

---

## Shape 1 — Non-generic `DeviceProxy` (closure-managed state)

The lowest-ceremony option. Your resources live in closure-captured variables;
the delegates receive a `DeviceInfo` snapshot. No wrapper type, no
`IAsyncDisposable` ceremony.

### Example: USB barcode scanner over a serial port

```csharp
using System.IO.Ports;
using Periphery;

SerialPort? port = null;

var scannerProfile = new DeviceProfile(f =>
{
    f.OfCategory(DeviceCategory.Ports);
    f.WithUsbId(vendorId: 0x05E0, productId: 0x1200);
}, name: "Barcode Scanner");

await using var scanner = await DeviceProxy.OpenAsync(
    scannerProfile,

    onActivated: async (deviceInfo, ct) =>
    {
        port = new SerialPort(deviceInfo.PortName!.Value.Value, 115200);
        port.Open();

        // Init gate — runs BEFORE IsOpen becomes true.
        // Throw here to abort the connection and retry on next connect.
        port.Write("HB\r\n");
        await Task.Delay(200, ct);
        if (!port.ReadExisting().Contains("OK"))
        {
            port.Close();
            port.Dispose();
            port = null;
            throw new InvalidOperationException("Heartbeat failed");
        }
    },

    onDeactivated: _ =>
    {
        port?.Close();
        port?.Dispose();
        port = null;
        return Task.CompletedTask;
    });

// scanner.IsOpen is now observable and bindable.
Console.ReadLine(); // keep alive
```

### Delegate contract

| Delegate | When it runs | Inside open lock? | Throw to… |
|---|---|---|---|
| `onActivated(DeviceInfo, CancellationToken)` | Device becomes active | Yes | Abort connection (device stays closed; retried on next connect) |
| `onDeactivated(DeviceInfo)` | Device becomes inactive or handle is disposed | Yes | — (exceptions are swallowed) |

The `CancellationToken` passed to `onActivated` is cancelled when the device
disconnects or the handle is disposed. Use it to exit `await` calls promptly
rather than blocking until timeout.

---

## Shape 2 — Generic `DeviceProxy<TDevice>` (disposable device type)

Use this when you already have a type that wraps your platform device and
implements `IAsyncDisposable`. The handle disposes it for you on close.

### Example: USB gamepad with typed HID wrapper

```csharp
using Periphery;

var gamepadProfile = new DeviceProfile(f =>
{
    f.OfCategory(DeviceCategory.Hid);
    f.WithUsbId(vendorId: 0x045E, productId: 0x02EA); // Xbox controller
}, name: "Xbox Controller");

await using var gamepad = await DeviceProxy<MyHidDevice>.OpenAsync(
    gamepadProfile,

    openDevice: (deviceInfo, ct) =>
        Task.FromResult(new MyHidDevice(deviceInfo.Id)),

    onActivated: async (device, ct) =>
    {
        // Init gate — verify firmware version before reporting IsOpen.
        var version = await device.ReadFirmwareVersionAsync(ct);
        if (version < new Version(2, 0))
            throw new InvalidOperationException($"Firmware {version} is too old");
    });

gamepad.DeviceOpened   += (_, device) => Console.WriteLine("Controller ready");
gamepad.DeviceClosed   += (_, _)      => Console.WriteLine("Controller unplugged");
gamepad.OpenFailed     += (_, ex)     => Console.WriteLine($"Open failed: {ex.Message}");
gamepad.PropertyChanged += (_, e)     =>
{
    if (e.PropertyName == nameof(gamepad.IsOpen))
        UpdateUiLed(gamepad.IsOpen);
};

Console.ReadLine();
```

> **`DeviceOpened` vs `onActivated`:** Use `onActivated` for work that must
> complete before the device is considered ready (handshake, auth, firmware
> check). Use `DeviceOpened` for fire-and-forget notification work (UI update,
> telemetry) after the connection is established.

---

## Shape 3 — Typed handle from an extension package (`HidDeviceProxy`)

Extension packages such as `Periphery.Hid` ship sealed leaf classes that
inherit `DeviceProxyBase`. They expose the same lifecycle events with a
typed device and typed exception.

### Example: HID device with typed exception handling

```csharp
using Periphery;
using Periphery.Hid;

var profile = new DeviceProfile(f =>
{
    f.OfCategory(DeviceCategory.Hid);
    f.WithUsbId(vendorId: 0x16C0, productId: 0x0486);
}, name: "Custom HID Device");

await using var handle = await HidDeviceProxy.OpenAsync(profile);

handle.DeviceOpened += (_, device) =>
{
    // device is a HidDevice — strongly typed, no cast needed.
    _ = Task.Run(() => ReadLoopAsync(device));
};

handle.OpenFailed += (_, ex) =>
{
    // ex is HidException — no cast needed.
    if (ex is HidAccessDeniedException)
        Console.WriteLine("Run as administrator or add a udev rule.");
    else
        Console.WriteLine($"HID open failed: {ex.Message}");
};

Console.ReadLine();

static async Task ReadLoopAsync(HidDevice device)
{
    var buffer = new byte[device.DeviceInfo.HidMaxInputReportLength ?? 64];
    while (true)
    {
        var read = await device.ReadAsync(buffer);
        ProcessReport(buffer[..read]);
    }
}
```

---

## Shared watcher — multiple devices, one OS subscription

When your application tracks several devices simultaneously, create one
`DeviceWatcher`, add all trackers to it, then call `DeviceProxy.Create`
(or `HidDeviceProxy.Create`) with each tracker. This opens a single
OS-level subscription instead of one per device.

### Example: POS terminal (scanner + cash drawer + receipt printer)

```csharp
using System.IO.Ports;
using Periphery;

SerialPort? scannerPort = null;
SerialPort? drawerPort  = null;
SerialPort? printerPort = null;

// ── Single watcher, three trackers ──────────────────────────────────
var watcher = Devices.Watch();

var scannerTracker = watcher.AddTracker(f =>
{
    f.OfCategory(DeviceCategory.Ports);
    f.WithUsbId(0x05E0, 0x1200);
}, "Barcode Scanner");

var drawerTracker = watcher.AddTracker(f =>
{
    f.OfCategory(DeviceCategory.Ports);
    f.WithUsbId(0x0DD4, 0x0100);
}, "Cash Drawer");

var printerTracker = watcher.AddTracker(f =>
{
    f.OfCategory(DeviceCategory.Ports);
    f.WithUsbId(0x04B8, 0x0202);
}, "Receipt Printer");

await watcher.StartAsync();

// ── One handle per tracker, all borrowing the same watcher ──────────
await using var scanner = DeviceProxy.Create(scannerTracker,
    onActivated: (info, ct) =>
    {
        scannerPort = new SerialPort(info.PortName!.Value.Value, 115200);
        scannerPort.Open();
        return Task.CompletedTask;
    },
    onDeactivated: _ =>
    {
        scannerPort?.Close(); scannerPort?.Dispose(); scannerPort = null;
        return Task.CompletedTask;
    });

await using var drawer = DeviceProxy.Create(drawerTracker,
    onActivated: (info, ct) =>
    {
        drawerPort = new SerialPort(info.PortName!.Value.Value, 9600);
        drawerPort.Open();
        return Task.CompletedTask;
    },
    onDeactivated: _ =>
    {
        drawerPort?.Close(); drawerPort?.Dispose(); drawerPort = null;
        return Task.CompletedTask;
    });

await using var printer = DeviceProxy.Create(printerTracker,
    onActivated: (info, ct) =>
    {
        printerPort = new SerialPort(info.PortName!.Value.Value, 9600);
        printerPort.Open();
        return Task.CompletedTask;
    },
    onDeactivated: _ =>
    {
        printerPort?.Close(); printerPort?.Dispose(); printerPort = null;
        return Task.CompletedTask;
    });

// ── Watcher lifetime is yours to manage ─────────────────────────────
Console.ReadLine();
await watcher.DisposeAsync();
```

> **Already-active trackers:** If `watcher.StartAsync()` is called before
> `Create`, the tracker may already have an active device by the time the
> handle is created. `Create` checks this automatically — `onActivated` fires
> immediately without waiting for a new connect event.

---

## Lifecycle events quick reference

All three shapes expose the same events through `DeviceProxyBase` (or
directly on the non-generic `DeviceProxy`):

| Event | When | Payload |
|---|---|---|
| `DeviceOpened` | After `onActivated` / `OnActivatedAsync` succeeds, `IsOpen = true` | The open device (`TDevice` or n/a) |
| `DeviceClosed` | Before device is disposed, `IsOpen = false` | `EventArgs.Empty` |
| `OpenFailed` | When `openDevice` / `OpenDeviceAsync` throws | The exception |
| `PropertyChanged` | When `IsOpen` flips | `nameof(IsOpen)` |

`IsOpen` implements `INotifyPropertyChanged` and can be bound directly
in XAML. For a stream, subscribe to the tracker — it implements
`IObservable<DeviceTrackerState>`; the handle itself exposes `PropertyChanged`.

---

## Key design points

| Concern | How it's handled |
|---|---|
| **Reconnect** | Automatic. The state machine re-runs `onActivated` / `OnActivatedAsync` each time the device reappears. |
| **Init gate** | `onActivated` / `OnActivatedAsync` runs inside the open lock before `IsOpen` flips. Throw to abort; the device stays closed. |
| **Disconnect during init** | The per-connection `CancellationToken` is cancelled, unblocking any awaiting `onActivated` work. The lock is then acquired and the half-open device is discarded. |
| **Watcher ownership** | `OpenAsync` — handle owns and disposes its watcher. `Create` — caller owns the watcher; the handle never disposes it. |
| **Thread safety** | `IsOpen` and `DeviceInfo` are safe to read from any thread. All events fire on thread-pool threads; UI dispatch is your responsibility. |
