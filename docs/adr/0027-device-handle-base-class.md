---
title: "ADR-0027: DeviceProxyBase — Reconnect-Resilient Lifecycle Base Class"
status: "Accepted"
date: "2026-07-15"
amended: "2026-07-16"
authors: "@charles8051 (design)"
tags: ["architecture", "decision", "lifecycle", "extension", "api-design", "base-class", "reconnect", "i/o"]
supersedes: ""
superseded_by: ""
---

# ADR-0027: DeviceProxyBase — Reconnect-Resilient Lifecycle Base Class

## Status

> **Amendment (2026-07-16).** Original proposal covered only the abstract base class
> for extension packages. This amendment adds: (1) an awaitable init-gate vs blind-
> callback distinction, (2) per-connection `CancellationToken` design, (3) a sealed
> delegate-configured sibling type `DeviceProxy<TDevice>` for application code, and
> (4) a managed `onLoop` delegate that folds long-running task lifecycle into the
> abstraction.
>
> **Amendment (2026-07-16, #2).** Adds: (5) a non-generic `DeviceProxy` for
> closure-based application code that doesn't need a `TDevice` wrapper, (6)
> `Create(DeviceTracker)` factory overloads on all handle types for shared-watcher
> scenarios where one `DeviceWatcher` powers multiple devices, and (7) optional
> watcher ownership in `DeviceProxyBase` (two constructors: owned vs. borrowed).
>
> **Amendment (2026-06-11, #3).** The non-generic `DeviceProxy` (amendment #2,
> item 5) originally shipped as a *standalone* "lightweight state machine" that
> re-implemented the entire reconnect lifecycle rather than deriving from
> `DeviceProxyBase` — a third hand-copy of the loop POS-001 exists to eliminate.
> Per ADR-0055 it has now been **unified onto `DeviceProxyBase`**: it derives from
> `DeviceProxyBase<DeviceProxy.Sentinel, Exception>`, where `Sentinel` is an inert
> `IAsyncDisposable` that carries the `DeviceInfo` snapshot to the closure hooks
> (no real device handle exists in the closure model). The `onActivated` init gate
> runs inside the overridden `OpenDeviceAsync` so an init throw still surfaces as
> `OpenFailed`. Its public surface is unchanged except that it now *inherits* the
> injectable `IReconnectPolicy` seam, `State`/`ConnectionState.GaveUp`/
> `LastOpenFault`, and re-enumeration reset for free. The duplicate
> `s_reconnectBackoff` / `RunWorkerAsync` / `RequestReconnect` / `ReconnectAsync`
> copy is gone; the reconnect loop now lives only in the base.

---

## Context

### 1. The repeated pattern

Every Layer 2 extension handle in the Periphery ecosystem follows the same skeleton:

| Concern | Implementation |
|---|---|
| Compose a `DeviceTracker` + `DeviceWatcher` | Constructor / factory |
| Subscribe to `StateChanged` | Route to open / close methods |
| Acquire `SemaphoreSlim` before mutating state | `_openLock.WaitAsync()` |
| Open the platform device | `HidDevice.OpenAsync`, `SerialPort.Open`, … |
| Expose `IsConnected` + `INotifyPropertyChanged` | Boilerplate property |
| Fire `DeviceOpened` / `DeviceClosed` events | Boilerplate events |
| Dispose watcher + device on teardown | `IAsyncDisposable` |

`HidDeviceProxy` (ADR-0020) was the first implementation. `SerialPortHandle`
(ADR-0028) will be the second. A third is inevitable (e.g. `UsbDeviceProxy`,
ADR-0019). Without a shared base class, each handle will re-implement 150+ lines
of identical concurrency, reconnect, and lifecycle logic.

### 2. Why a base class instead of composition

The reconnect state machine is *internal* — it guards mutable state behind a
`SemaphoreSlim` and interleaves with `INotifyPropertyChanged` notifications. A
composition helper would require exposing lock internals or forcing an awkward
delegation pattern. Inheritance is the natural fit: the base class owns the
invariant skeleton; derived classes supply only the device-specific open/close
logic.

### 3. Awaitable init-gate vs blind callback

Design exploration of the barcode-scanner use case (USB device exposing a serial
COM port) revealed two distinct categories of work that consumers perform when a
device activates:

| Category | Examples | Requirement |
|---|---|---|
| **Init gate** (precondition) | Send heartbeat, read device parameters, verify firmware version | Must complete *before* `IsConnected` becomes `true`. Failure means "not really connected." |
| **Notification** (post-connected) | Start a read loop, update UI, log telemetry | Runs *after* `IsConnected` is `true`. Fire-and-forget / event-driven. |

Blind `EventHandler<TDevice>` callbacks (the current `DeviceOpened` pattern in
`HidDeviceProxy`) are **insufficient for init gates** because:

- `async void` event handlers are fire-and-forget — the handle cannot await them.
- `IsConnected` flips to `true` before the init work completes, so consumers see
  a "connected" device that hasn't finished its handshake.
- There is no way to signal init failure back to the handle.

The solution is an **awaitable init delegate** (or virtual method) that the handle
invokes *inside* the open lock, *before* setting `IsConnected = true`. This delegate
receives the opened device and a per-connection `CancellationToken`, and can throw
to abort the connection attempt.

### 4. Per-connection CancellationToken

A device can disconnect while init work is in progress (e.g. the heartbeat command
is in-flight when the USB cable is pulled). Without cancellation, the init delegate
blocks the open lock until it times out or fails on its own.

Each connection attempt creates a fresh `CancellationTokenSource`. The token is
passed to the init delegate (and the loop delegate — see below). When `CloseDeviceAsync`
is called (due to disconnect or disposal), the CTS is cancelled *before* acquiring
the open lock, so any in-flight init or loop work can exit promptly.

```
Disconnect arrives
  │
  ▼
Cancel per-connection CTS          ← unblocks init / loop awaits
  │
  ▼
Acquire _openLock
  │
  ▼
Null device, IsConnected = false
  │
  ▼
Raise DeviceClosed
  │
  ▼
DisposeAsync(device)
  │
  ▼
Release _openLock
```

### 5. Three consumer profiles

| Consumer | Needs | Shape |
|---|---|---|
| **Extension package** (Periphery.Hid, .Serial, .Usb) | Full control over device type, exception hierarchy, factory pattern. Ships as a NuGet library. | `DeviceProxyBase<TDevice, TException>` — abstract, override `OnConnectedAsync` / `OnDisconnectingAsync` / `OnLoopAsync`. |
| **Application code** (disposable device) | Quick one-off handle for a specific device with an `IAsyncDisposable` wrapper. No desire to create a derived class. | `DeviceProxy<TDevice>` — sealed, configure via `Func<>` delegates. |
| **Application code** (closure-managed) | Manage resources in closures without creating a `TDevice` wrapper. No `IAsyncDisposable` ceremony. | `DeviceProxy` — non-generic, lightweight state machine, delegates receive `DeviceInfo` directly. |

### 6. Shared-watcher pattern (owned vs. borrowed)

The original design assumed each handle owns its own `DeviceWatcher`. Applications
that track 10+ devices would create 10+ watchers — each issuing its own OS-level
subscription. A shared-watcher pattern allows one `DeviceWatcher` to power
multiple `DeviceTracker`s (and therefore multiple handles) simultaneously.

All handle types now expose two factory shapes:

| Factory | Watcher ownership | Use case |
|---|---|---|
| `OpenAsync(DeviceProfile, ...)` | Handle creates and owns its own tracker + watcher. Disposed on handle disposal. | Simple single-device scenarios. |
| `Create(DeviceTracker, ...)` | Borrows a caller-owned tracker attached to an external watcher. Handle does not dispose the watcher. | Shared-watcher — one watcher, many devices. |

`DeviceProxyBase` supports this via two constructors: one taking `(tracker, ownedWatcher)` and one taking just `(tracker)`. The `Create` factories call `CheckInitialState()` after construction to handle trackers that are already active when the handle is created (the watcher may already be running).

Both types share the same internal state machine, concurrency model, and lifecycle
guarantees. The sealed type delegates to the base class by forwarding the `Func<>`
parameters to the virtual methods.

---

## Decision

### Shape A: `DeviceProxyBase<TDevice, TException>` (abstract, for extension packages)

```csharp
public abstract class DeviceProxyBase<TDevice, TException>
    : INotifyPropertyChanged, IAsyncDisposable
    where TDevice : class, IAsyncDisposable
    where TException : Exception
{
    private readonly DeviceTracker _tracker;
    private readonly DeviceWatcher _watcher;
    private TDevice? _device;
    private bool _isConnected;
    private bool _disposed;
    private readonly SemaphoreSlim _openLock = new(1, 1);
    private CancellationTokenSource? _connectionCts;
    private Task? _loopTask;

    protected DeviceProxyBase(DeviceTracker tracker, DeviceWatcher watcher)
    {
        _tracker = tracker;
        _watcher = watcher;
        _tracker.StateChanged += OnTrackerStateChanged;
    }

    // --- Public state -------------------------------------------------------

    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (_isConnected == value) return;
            _isConnected = value;
            PropertyChanged?.Invoke(this,
                new PropertyChangedEventArgs(nameof(IsConnected)));
        }
    }

    public DeviceInfo? DeviceInfo => _tracker.Device;
    public TDevice? Device => _device;

    // --- Events -------------------------------------------------------------

    /// <summary>
    /// Raised after <see cref="OnConnectedAsync"/> completes successfully and
    /// <see cref="IsConnected"/> is <see langword="true"/>. Use for notification
    /// work (UI updates, telemetry) — NOT for init gates.
    /// </summary>
    public event EventHandler<TDevice>? DeviceOpened;

    public event EventHandler? DeviceClosed;
    public event EventHandler<TException>? OpenFailed;
    public event PropertyChangedEventHandler? PropertyChanged;

    // --- Hooks (override in derived classes) ---------------------------------

    /// <summary>
    /// Opens the platform device. Called inside the open lock.
    /// </summary>
    protected abstract Task<TDevice> OpenDeviceAsync(
        DeviceInfo deviceInfo, CancellationToken ct);

    /// <summary>
    /// Awaitable init gate. Called inside the open lock, BEFORE
    /// <see cref="IsConnected"/> becomes <see langword="true"/>.
    /// Throw to abort the connection attempt.
    /// </summary>
    protected virtual Task OnConnectedAsync(
        TDevice device, CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Teardown hook. Called inside the open lock during close.
    /// </summary>
    protected virtual Task OnDisconnectingAsync(
        TDevice device) => Task.CompletedTask;

    /// <summary>
    /// Long-running loop. Called OUTSIDE the open lock, AFTER
    /// <see cref="IsConnected"/> is <see langword="true"/>.
    /// Return normally or throw (non-CT) to trigger device close and reconnect.
    /// </summary>
    protected virtual Task OnLoopAsync(
        TDevice device, CancellationToken ct) => Task.CompletedTask;

    // --- State machine (same as HidDeviceProxy pattern) --------------------

    private void OnTrackerStateChanged(object? sender, DeviceTrackerState state)
    {
        if (state.IsActive)
            _ = TryOpenDeviceAsync(state.Device!);
        else
            _ = CloseDeviceAsync();
    }

    private async Task TryOpenDeviceAsync(DeviceInfo deviceInfo)
    {
        await _openLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed || _isConnected) return;

            var cts = new CancellationTokenSource();
            _connectionCts = cts;

            TDevice opened;
            try
            {
                opened = await OpenDeviceAsync(deviceInfo, cts.Token)
                    .ConfigureAwait(false);
            }
            catch (TException ex)
            {
                OpenFailed?.Invoke(this, ex);
                return;
            }

            try
            {
                await OnConnectedAsync(opened, cts.Token)
                    .ConfigureAwait(false);
            }
            catch
            {
                await opened.DisposeAsync().ConfigureAwait(false);
                return;
            }

            _device = opened;
            IsConnected = true;
            DeviceOpened?.Invoke(this, opened);
        }
        finally
        {
            _openLock.Release();
        }

        // Start loop OUTSIDE the lock
        _loopTask = RunLoopAsync();
    }

    private async Task RunLoopAsync()
    {
        var device = _device;
        var cts = _connectionCts;
        if (device is null || cts is null) return;

        try
        {
            await OnLoopAsync(device, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // Normal disconnect — CT was cancelled by CloseDeviceAsync.
            return;
        }
        catch
        {
            // Loop exited unexpectedly — trigger close + reconnect.
            _ = CloseDeviceAsync();
        }
    }

    private async Task CloseDeviceAsync()
    {
        // Cancel in-flight init / loop work BEFORE acquiring the lock.
        _connectionCts?.Cancel();

        await _openLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_device is null) return;

            var closing = _device;
            _device = null;
            IsConnected = false;
            DeviceClosed?.Invoke(this, EventArgs.Empty);

            await OnDisconnectingAsync(closing).ConfigureAwait(false);
            await closing.DisposeAsync().ConfigureAwait(false);

            _connectionCts?.Dispose();
            _connectionCts = null;
            _loopTask = null;
        }
        finally
        {
            _openLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _tracker.StateChanged -= OnTrackerStateChanged;
        await CloseDeviceAsync().ConfigureAwait(false);
        await _watcher.DisposeAsync().ConfigureAwait(false);
        _openLock.Dispose();
    }
}
```

### Shape B: `DeviceProxy<TDevice>` (sealed, delegate-configured, for application code)

```csharp
public sealed class DeviceProxy<TDevice>
    : DeviceProxyBase<TDevice, Exception>
    where TDevice : class, IAsyncDisposable
{
    private readonly Func<DeviceInfo, CancellationToken, Task<TDevice>> _openDevice;
    private readonly Func<TDevice, CancellationToken, Task>? _onConnected;
    private readonly Func<TDevice, Task>? _onDisconnecting;
    private readonly Func<TDevice, CancellationToken, Task>? _onLoop;

    private DeviceProxy(
        DeviceTracker tracker,
        DeviceWatcher watcher,
        Func<DeviceInfo, CancellationToken, Task<TDevice>> openDevice,
        Func<TDevice, CancellationToken, Task>? onConnected,
        Func<TDevice, Task>? onDisconnecting,
        Func<TDevice, CancellationToken, Task>? onLoop)
        : base(tracker, watcher)
    {
        _openDevice = openDevice;
        _onConnected = onConnected;
        _onDisconnecting = onDisconnecting;
        _onLoop = onLoop;
    }

    protected override Task<TDevice> OpenDeviceAsync(
        DeviceInfo deviceInfo, CancellationToken ct)
        => _openDevice(deviceInfo, ct);

    protected override Task OnConnectedAsync(
        TDevice device, CancellationToken ct)
        => _onConnected?.Invoke(device, ct) ?? Task.CompletedTask;

    protected override Task OnDisconnectingAsync(TDevice device)
        => _onDisconnecting?.Invoke(device) ?? Task.CompletedTask;

    protected override Task OnLoopAsync(
        TDevice device, CancellationToken ct)
        => _onLoop?.Invoke(device, ct) ?? Task.CompletedTask;

    /// <summary>
    /// Creates a delegate-configured device handle and starts the watcher.
    /// </summary>
    public static async Task<DeviceProxy<TDevice>> OpenAsync(
        DeviceProfile profile,
        Func<DeviceInfo, CancellationToken, Task<TDevice>> openDevice,
        Func<TDevice, CancellationToken, Task>? onConnected = null,
        Func<TDevice, Task>? onDisconnecting = null,
        Func<TDevice, CancellationToken, Task>? onLoop = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(openDevice);

        var tracker = new DeviceTracker(profile.Filter, profile.Name);
        var watcher = Devices.Watch().AddTracker(tracker);
        var handle = new DeviceProxy<TDevice>(
            tracker, watcher, openDevice, onConnected, onDisconnecting, onLoop);

        await watcher.StartAsync(ct).ConfigureAwait(false);
        return handle;
    }
}
```

### HidDeviceProxy collapsed onto the base class

After extraction, `HidDeviceProxy` reduces to:

```csharp
public sealed class HidDeviceProxy
    : DeviceProxyBase<HidDevice, HidException>
{
    private HidDeviceProxy(DeviceTracker tracker, DeviceWatcher watcher)
        : base(tracker, watcher) { }

    protected override Task<HidDevice> OpenDeviceAsync(
        DeviceInfo deviceInfo, CancellationToken ct)
        => HidDevice.OpenAsync(deviceInfo, ct);

    public static async Task<HidDeviceProxy> OpenAsync(
        DeviceProfile profile, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var tracker = new DeviceTracker(profile.Filter, profile.Name);
        var watcher = Devices.Watch().AddTracker(tracker);
        var handle = new HidDeviceProxy(tracker, watcher);
        await watcher.StartAsync(ct).ConfigureAwait(false);
        return handle;
    }
}
```

All 150+ lines of concurrency, reconnect, and lifecycle logic are eliminated from
the leaf class.

### Usage example: barcode scanner with `System.IO.Ports.SerialPort`

This example uses the delegate-configured `DeviceProxy<TDevice>` with the BCL
`System.IO.Ports.SerialPort` wrapped in a thin `AsyncSerialPort` adapter to
satisfy `IAsyncDisposable`.

```csharp
// --- Thin IAsyncDisposable wrapper over System.IO.Ports.SerialPort ----------

public sealed class AsyncSerialPort : IAsyncDisposable
{
    public SerialPort Port { get; }

    public AsyncSerialPort(string portName, int baudRate)
    {
        Port = new SerialPort(portName, baudRate);
        Port.Open();
    }

    public ValueTask DisposeAsync()
    {
        Port.Close();
        Port.Dispose();
        return ValueTask.CompletedTask;
    }
}

// --- Application setup -------------------------------------------------------

var scannerProfile = new DeviceProfile(f =>
{
    f.OfCategory(DeviceCategory.Ports);
    f.WithUsbId(vendorId: 0x05E0, productId: 0x1200);
}, name: "Barcode Scanner");

await using var scanner = await DeviceProxy<AsyncSerialPort>.OpenAsync(
    profile: scannerProfile,

    openDevice: (deviceInfo, ct) =>
    {
        var port = new AsyncSerialPort(
            deviceInfo.PortName!.Value.Value, baudRate: 115200);
        return Task.FromResult(port);
    },

    onConnected: async (port, ct) =>
    {
        // Init gate — runs BEFORE IsConnected becomes true.
        // Send heartbeat, read device parameters.
        port.Port.Write("HB\r\n");
        await Task.Delay(200, ct);

        var response = port.Port.ReadExisting();
        if (!response.Contains("OK"))
            throw new InvalidOperationException("Heartbeat failed");
    },

    onDisconnecting: port =>
    {
        // Teardown — runs during close, inside the lock.
        return Task.CompletedTask;
    },

    onLoop: async (port, ct) =>
    {
        // Managed read loop — runs AFTER IsConnected is true.
        // Return or throw to trigger close + reconnect.
        while (!ct.IsCancellationRequested)
        {
            var barcode = port.Port.ReadLine();
            Console.WriteLine($"Scanned: {barcode}");
        }
    }
);

// scanner.IsConnected is now observable. The loop runs automatically
// each time the device connects. Disconnection cancels the loop and
// disposes the port. Reconnection re-opens and re-runs the init gate.
```

### Lifecycle sequence (single connection)

```
DeviceTracker.StateChanged (IsActive = true)
  │
  ▼
Acquire _openLock
  │
  ▼
Create per-connection CancellationTokenSource
  │
  ▼
OpenDeviceAsync(deviceInfo, ct)         ← abstract / delegate
  │  failure → OpenFailed event, return
  │
  ▼
OnConnectedAsync(device, ct)            ← init gate (virtual / delegate)
  │  failure → DisposeAsync(device), return
  │  disconnect mid-init → CT cancelled, OperationCanceledException
  │
  ▼
_device = opened
IsConnected = true
DeviceOpened event                      ← notification (blind callback)
  │
  ▼
Release _openLock
  │
  ▼
OnLoopAsync(device, ct)                 ← OUTSIDE lock (virtual / delegate)
  │  normal exit or non-CT exception → CloseDeviceAsync() → reconnect
  │  CT cancellation → normal disconnect, no re-trigger
  │
  ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─
  
DeviceTracker.StateChanged (IsActive = false)  — or loop exit
  │
  ▼
Cancel per-connection CTS               ← unblocks init / loop
  │
  ▼
Acquire _openLock
  │
  ▼
_device = null
IsConnected = false
DeviceClosed event
  │
  ▼
OnDisconnectingAsync(device)            ← teardown hook
  │
  ▼
DisposeAsync(device)
  │
  ▼
Dispose CTS
Release _openLock
```

### Where each type lives

| Type | Assembly | Namespace |
|---|---|---|
| `DeviceProxyBase<TDevice, TException>` | `Periphery` | `Periphery` |
| `DeviceProxy<TDevice>` | `Periphery` | `Periphery` |
| `DeviceProxy` | `Periphery` | `Periphery` |
| `HidDeviceProxy` | `Periphery.Hid` | `Periphery.Hid` |
| `SerialPortHandle` | `Periphery.Serial` | `Periphery.Serial` |

All core handle types ship in the `Periphery` assembly so extension packages
(and application code) can derive from / use them without taking an extra
dependency.

---

## Consequences

### Positive

- **POS-001:** Eliminates ~150 lines of duplicated concurrency / reconnect /
  lifecycle boilerplate from every Layer 2 handle.
- **POS-002:** New extension handles (Serial, USB, MIDI) only supply open/close
  logic — the happy path is 20–30 lines.
- **POS-003:** Single place to fix reconnect bugs, add telemetry, or refine the
  concurrency model.
- **POS-004:** `INotifyPropertyChanged` and `IsConnected` are guaranteed
  consistent across all handles.
- **POS-005:** Awaitable init gate ensures `IsConnected` is semantically honest —
  consumers never see a "connected" device that hasn't completed its handshake.
- **POS-006:** Per-connection `CancellationToken` makes disconnect-during-init
  safe and deterministic — no more blocked semaphores waiting for timeouts.
- **POS-007:** `DeviceProxy<TDevice>` eliminates the need for application code
  to create derived classes for one-off device integrations.
- **POS-008:** Non-generic `DeviceProxy` eliminates the `IAsyncDisposable`
  ceremony entirely — application code manages resources in closures with zero
  type boilerplate.
- **POS-009:** `Create(DeviceTracker)` overloads enable shared-watcher patterns
  where a single `DeviceWatcher` powers multiple handles, reducing OS-level
  subscriptions from N to 1.
- **POS-010:** Managed `onLoop` eliminates manual `CancellationTokenSource`
  management, `DeviceOpened` subscription for read loops, and `Task` lifecycle
  tracking — the three most error-prone aspects of the current `HidDeviceProxy`
  consumer pattern.

### Negative

- **NEG-001:** Adds an inheritance layer — consumers must understand the open /
  close hook contract.
- **NEG-002:** Generic constraints (`where TDevice : class, IAsyncDisposable`)
  require device types to implement `IAsyncDisposable`. Types that don't (e.g.
  `System.IO.Ports.SerialPort`) need a thin wrapper.
- **NEG-003:** Three handle types (`DeviceProxyBase`, `DeviceProxy<TDevice>`,
  `DeviceProxy`) increase the API surface. Mitigation: clear guidance in docs
  and trigger conditions below — extension authors use the base class, app
  authors choose generic or non-generic based on whether they have a disposable
  device type.

---

## Trigger Conditions

### When to use `DeviceProxyBase<TDevice, TException>`

- You are building a **NuGet extension package** (Periphery.Hid, .Serial, .Usb).
- You need a **custom device type** with a rich API surface.
- You need a **typed exception hierarchy** (e.g. `HidException`, `SerialException`).
- You want to ship a **static factory** (`OpenAsync`) that hides the tracker/watcher wiring.

### When to use `DeviceProxy<TDevice>`

- You are writing **application code** (not a reusable library).
- You have a device that exposes a simple I/O surface (serial port, socket, pipe).
- You already have (or can easily create) an `IAsyncDisposable` device type.
- You want **delegate configuration** without creating a derived class.
- You want the **managed loop** to handle your read/poll cycle automatically.

### When to use `DeviceProxy` (non-generic)

- You are writing **application code** and don't want to create a `TDevice` wrapper.
- You prefer **closure-captured state** (e.g. a `SerialPort` field in a view-model).
- You want the **lightest ceremony** — just pass `onActivated` / `onDeactivated` lambdas.

### When to use `Create(DeviceTracker)` instead of `OpenAsync`

- Your application tracks **multiple devices** simultaneously.
- You want a **single `DeviceWatcher`** to reduce OS-level subscriptions.
- You manage the watcher lifecycle yourself (e.g. in a DI container or app host).

---

## References

- ADR-0001: Device Tracking Handles (`DeviceTracker`, `DeviceProfile`)
- ADR-0020: Periphery.Hid — the first Layer 2 handle (model for extraction)
- ADR-0024: Extension Package Pattern — generalised contract
- ADR-0028: Periphery.Serial — second Layer 2 handle (validates the base class)
- ADR-0029: DeviceTracker Edge Events (Appeared / Disappeared / Activated / Deactivated)
