---
title: "ADR-0022: Periphery.Input — System-Wide Keyboard and Mouse Input Monitoring"
status: "Proposed"
status_note: "Not implemented - there is no `Periphery.Input` package."
date: "2026-07-14"
authors: "@charles8051 (design)"
tags: ["architecture", "decision", "input", "keyboard", "mouse", "extension", "api-design", "periphery-input"]
supersedes: ""
superseded_by: ""
---

# ADR-0022: Periphery.Input — System-Wide Keyboard and Mouse Input Monitoring

## Context

### Why input monitoring is not Periphery.Hid

ADR-0020 (`Periphery.Hid`) established that the OS keyboard and mouse class drivers
hold **exclusive access** to their HID device nodes on Windows and macOS. A caller
cannot open a standard keyboard or mouse via `HidDevice.OpenAsync` on those platforms;
the OS driver stack has already claimed the handle before user-space gets the chance.

The correct level at which to observe keyboard and mouse input is **above** the HID
class driver, in the processed input event stream the OS produces from it:

```
Physical device (USB/Bluetooth)
    └─ HID class driver (kbdhid.sys / hid-input / IOHIDSystem)
           └─ Input event subsystem          ← Periphery.Input lives here
                  └─ Application / window manager
```

This is an entirely separate OS API surface from raw HID. It delivers structured,
processed events (key codes, button states, delta coordinates) rather than raw
report byte buffers. It is also the only user-space path available on Windows and
macOS — there is no raw HID alternative for standard keyboards and mice.

### What the OS provides at this layer

| Platform | API | Delivery model |
|---|---|---|
| Windows | Raw Input (`RegisterRawInputDevices` / `WM_INPUT`) | `WM_INPUT` messages to a registered window |
| macOS | Quartz Event Services (`CGEventTap`) | `CFRunLoop`-based callback |
| Linux | evdev (`/dev/input/eventN` via `epoll`) | Non-blocking file descriptor reads |

All three provide system-wide event delivery — the monitoring process does not need
to be the focused foreground application.

### Per-device event sourcing

A meaningful cross-platform invariant is that events carry **device identity** where
the OS supports it:

| Platform | Device identity in events |
|---|---|
| Windows | `RAWINPUT.header.hDevice` — matches back to the `HID\...` device path |
| Linux | Each `/dev/input/eventN` fd is a single physical device |
| macOS | `CGEventTap` does **not** expose the source device on a per-event basis |

macOS is the limiting case. `CGEventTap` coalesces all keyboard and mouse input into
a single session-level stream with no per-event device attribution. `IOHIDManager`
can supply per-device callbacks at the raw HID level, but as established in ADR-0020
it cannot open keyboard/mouse devices on macOS without exclusive access. The
cross-platform abstraction must therefore treat `DeviceInfo?` on each event as
**nullable and best-effort**: populated on Windows and Linux, null on macOS.

### Permission requirements

Observing keyboard and mouse input globally is a privacy-sensitive operation. The
platforms treat it differently:

| Platform | Requirement |
|---|---|
| Windows | None — Raw Input requires no elevation or special permission |
| macOS | "Input Monitoring" permission required (System Settings → Privacy & Security → Input Monitoring). Mandatory since Catalina (10.15). |
| Linux | Membership in the `input` group or a udev rule granting read on `/dev/input/event*` |

The macOS permission model has a critical failure mode: **if "Input Monitoring" is not
granted, `CGEventTapCreate` succeeds but the tap receives no events**. There is no
error returned. Callers must explicitly check permission before starting a session, or
they will create a session that silently delivers nothing.

If the user previously denied the permission request, the system dialog will not
appear again. Callers must detect the denied state and direct the user to System
Settings manually.

### Threading

All three platform backends require a dedicated OS loop to deliver events:

| Platform | Required loop | Mechanism |
|---|---|---|
| Windows | Win32 message pump | Hidden `HWND_MESSAGE` window on a dedicated thread; `GetMessage` / `DispatchMessage` |
| macOS | Core Foundation run loop | `CFRunLoopRun()` on a dedicated thread |
| Linux | epoll loop | `epoll_wait` on a dedicated thread |

This threading is an internal implementation detail. Callers receive events via an
`IAsyncEnumerable<T>` backed by a `Channel<T>` that the dedicated loop writes into.

---

## Decision

Implement `Periphery.Input` as a standalone extension package for system-wide,
read-only keyboard and mouse input monitoring.

This design follows the **Layer 1 / Layer 2** extension package pattern established
in ADR-0024. `InputSession` is the Layer 1 I/O primitive; `InputDeviceProxy` is
the Layer 2 lifecycle manager. There is no Layer 3 enrichment (no OS-enumerable
HID metadata is domain-specific to the input event subsystem).

### Scope

- **In scope:** Reading keyboard key events and mouse movement / button / wheel events
  system-wide. Permission checking. Per-device attribution where the OS supports it.
- **Out of scope:** Input interception (blocking or modifying events). Synthesizing or
  injecting input events. Touchpad, touchscreen, or pen/stylus input. Gamepad /
  joystick input (covered by `Periphery.Hid`).

### `InputPermission` — permission lifecycle (macOS-critical)

```csharp
public enum InputMonitoringPermission
{
    /// Permission has been explicitly granted.
    Granted,

    /// Permission has not been requested yet; requesting will show a system dialog.
    NotDetermined,

    /// Permission was previously denied; the system dialog will not appear again.
    /// The user must navigate to System Settings manually.
    Denied,

    /// The current platform does not require explicit permission (Windows, Linux).
    NotRequired,
}

public static class InputPermission
{
    /// Returns the current permission status without requesting it.
    public static InputMonitoringPermission Check();

    /// On macOS with NotDetermined status, shows the system permission dialog and
    /// waits for the user's response. On all other platforms, returns immediately
    /// with the result of Check().
    public static Task<InputMonitoringPermission> RequestAsync(
        CancellationToken ct = default);
}
```

Callers are expected to call `InputPermission.Check()` before `InputSession.StartAsync`
and handle `Denied` by guiding the user to System Settings. Failing to check and
starting a session on macOS without permission produces a session that delivers no
events — by design, no exception is thrown, because `CGEventTapCreate` itself does
not fail.

### `InputSession` — the Layer 1 monitoring primitive

`InputSession` is the I/O primitive: it opens the OS-level tap/registration, exposes
the event streams, and releases all OS resources on disposal.

```csharp
public sealed class InputSession : IAsyncDisposable
{
    // The continuous event streams — backed by internal Channel<T> buffers.
    // Each is a single-consumer IAsyncEnumerable; for fan-out, callers buffer themselves.
    public IAsyncEnumerable<KeyboardEvent> KeyboardEvents { get; }
    public IAsyncEnumerable<MouseEvent> MouseEvents { get; }

    // Physical devices currently observed (populated from OS enumeration at Start time).
    // Null entries are not present; count may be 0 on macOS (no per-session device list).
    public IReadOnlyList<DeviceInfo> Keyboards { get; }
    public IReadOnlyList<DeviceInfo> Mice { get; }

    // Factory — starts the OS tap / registration and the internal pump thread.
    // Throws InputPermissionException if permission has been denied (macOS).
    // Throws PlatformNotSupportedException if the current OS is unsupported.
    public static Task<InputSession> StartAsync(
        InputSessionOptions? options = null,
        CancellationToken ct = default);

    public ValueTask DisposeAsync();
}

public sealed class InputSessionOptions
{
    /// Maximum number of events to buffer before the oldest are dropped.
    /// Default: 256. Increase for high-polling-rate mice (1000+ Hz).
    public int BufferCapacity { get; init; } = 256;

    /// If set, only events from the specified devices are delivered.
    /// Not supported on macOS (CGEventTap has no per-device filtering).
    public IReadOnlyList<DeviceInfo>? DeviceFilter { get; init; }
}
```

### Event types

```csharp
public readonly struct KeyboardEvent
{
    /// The key that was pressed or released.
    public Key Key { get; init; }

    /// Whether this is a key-down or key-up transition.
    public KeyboardEventKind Kind { get; init; }

    /// Raw scan code from the hardware. Platform-specific; use Key for portable logic.
    public uint ScanCode { get; init; }

    /// True if the key is an extended key (e.g. right Ctrl, right Alt, arrow keys).
    public bool IsExtendedKey { get; init; }

    /// The physical device that produced this event.
    /// Null on macOS (CGEventTap does not expose per-event device identity).
    public DeviceInfo? Device { get; init; }

    /// Wall-clock timestamp. Sub-millisecond precision on Windows and Linux;
    /// millisecond precision on macOS (CGEvent timestamp is in Mach absolute time
    /// units, converted to DateTimeOffset at callback time).
    public DateTimeOffset Timestamp { get; init; }
}

public enum KeyboardEventKind { KeyDown, KeyUp }

public readonly struct MouseEvent
{
    /// The kind of mouse event.
    public MouseEventKind Kind { get; init; }

    /// Relative movement since the last event. Meaningful only when Kind is Move.
    public (int X, int Y) Delta { get; init; }

    /// The button involved. None for Move and Wheel events.
    public MouseButton Button { get; init; }

    /// Signed wheel delta. Positive = scroll up/forward. Meaningful only when Kind is Wheel.
    public int WheelDelta { get; init; }

    /// The physical device that produced this event.
    /// Null on macOS (CGEventTap does not expose per-event device identity).
    public DeviceInfo? Device { get; init; }

    /// Wall-clock timestamp. Same precision caveats as KeyboardEvent.Timestamp.
    public DateTimeOffset Timestamp { get; init; }
}

public enum MouseEventKind { Move, ButtonDown, ButtonUp, Wheel }

public enum MouseButton { None, Left, Right, Middle, X1, X2 }
```

`Key` is a platform-normalised enum covering standard keys. It maps from:
- Windows virtual key codes (`RAWKEYBOARD.VKey`)
- macOS `CGKeyCode` values
- Linux evdev `KEY_*` constants

Raw scan codes are preserved in `KeyboardEvent.ScanCode` for callers that need
hardware-level key identity.

### `InputDeviceProxy` — the Layer 2 lifecycle manager

`InputDeviceProxy` follows the same `DeviceTracker` composition pattern as
`HidDeviceProxy` (ADR-0020) and `UsbDeviceProxy` (ADR-0019), and is the canonical
Layer 2 shape defined in ADR-0024. It bridges a tracked device into an active
`InputSession`:

```csharp
public sealed class InputDeviceProxy : INotifyPropertyChanged, IAsyncDisposable
{
    public InputDeviceProxy(DeviceTracker tracker);

    public bool IsConnected { get; }
    public DeviceInfo? DeviceInfo { get; }

    /// Non-null while the tracked device is connected and the session is running.
    public InputSession? Session { get; }

    public event EventHandler<InputSession>? SessionStarted;
    public event EventHandler? SessionStopped;
    public event PropertyChangedEventHandler? PropertyChanged;
}
```

`InputDeviceProxy` is most useful for specific-device scenarios (e.g. monitoring a
known gaming keyboard for macro key events). For system-wide monitoring that does not
need to track a particular device, callers use `InputSession.StartAsync` directly.

### Call-site shapes

```csharp
// System-wide: read all keyboard events until cancelled
await using var session = await InputSession.StartAsync();

await foreach (var evt in session.KeyboardEvents.WithCancellation(cts.Token))
{
    Console.WriteLine($"{evt.Kind} {evt.Key}  device={evt.Device?.Name ?? "unknown"}");
}

// Permission-aware startup (macOS)
var permission = InputPermission.Check();
if (permission == InputMonitoringPermission.NotDetermined)
    permission = await InputPermission.RequestAsync();

if (permission != InputMonitoringPermission.Granted
    && permission != InputMonitoringPermission.NotRequired)
{
    Console.Error.WriteLine("Input Monitoring permission required. " +
        "Please enable it in System Settings → Privacy & Security → Input Monitoring.");
    return;
}

await using var session = await InputSession.StartAsync();

// Device-specific: track a known keyboard and monitor its events
var tracker = new DeviceTracker("Gaming Keyboard",
    new DeviceProfile(f => f.WithUsbId("1B1C", "1B55")));

await using var handle = new InputDeviceProxy(tracker);

handle.SessionStarted += async (_, session) =>
{
    await foreach (var evt in session.KeyboardEvents)
        ProcessMacroKey(evt);
};

await using var watcher = Devices.Watch().AddTrackers(tracker);
await watcher.StartAsync();

// Mouse + keyboard in parallel
await using var session = await InputSession.StartAsync();

var keyTask = Task.Run(async () =>
{
    await foreach (var evt in session.KeyboardEvents.WithCancellation(cts.Token))
        OnKey(evt);
});

var mouseTask = Task.Run(async () =>
{
    await foreach (var evt in session.MouseEvents.WithCancellation(cts.Token))
        OnMouse(evt);
});

await Task.WhenAll(keyTask, mouseTask);
```

### Platform backends

| Platform | Backend | Notes |
|---|---|---|
| Windows | `RegisterRawInputDevices` (`RIDEV_INPUTSINK`) targeting a hidden `HWND_MESSAGE` window; `GetRawInputData` on `WM_INPUT` messages | Hidden window created on a dedicated STA thread running `GetMessage` / `DispatchMessage`. Device identity from `RAWINPUT.header.hDevice` → `GetRawInputDeviceInfo`. |
| macOS | `CGEventTapCreate` with `kCGHIDEventTap`, `kCGHeadInsertEventTap`, `kCGEventTapOptionListenOnly`; tap scheduled on a dedicated `CFRunLoop` thread | Listen-only tap — cannot intercept. Permission check via `IOHIDCheckAccess(kIOHIDRequestTypeListenEvent)` before tap creation. No per-event device identity. |
| Linux | `open("/dev/input/eventN")` for each keyboard/mouse device; `epoll` on all fds; `read` of `input_event` structs | Devices identified by `ioctl(EVIOCGNAME)` and event capability bits (`EV_KEY` + `KEY_A` → keyboard; `EV_REL` + `REL_X`/`REL_Y` → mouse). Requires `input` group membership or appropriate udev rule. |

### Internal architecture

```
InputSession.StartAsync()
    │
    ├─ Platform probe ──► WindowsInputBackend / MacOSInputBackend / LinuxInputBackend
    │                          (implements IInputBackend)
    │
    ├─ Allocate Channel<KeyboardEvent>(capacity) + Channel<MouseEvent>(capacity)
    │
    ├─ Backend.StartAsync(keyChannel.Writer, mouseChannel.Writer)
    │       └─ Spin dedicated OS-loop thread
    │              Windows: hidden HWND_MESSAGE + GetMessage pump
    │              macOS:   CGEventTap + CFRunLoopRun
    │              Linux:   epoll_wait loop over /dev/input/event* fds
    │
    └─ Expose keyChannel.Reader / mouseChannel.Reader as IAsyncEnumerable<T>

InputSession.DisposeAsync()
    └─ Signal OS-loop thread to exit → join → complete channels → release handles
```

The `Channel<T>` buffer decouples the OS delivery thread from async consumers.
`BoundedChannelOptions` with `BoundedChannelFullMode.DropOldest` prevents unbounded
growth if the consumer falls behind (configurable via `InputSessionOptions.BufferCapacity`).

---

## Relationship to Periphery.Hid and ADR-0024

`Periphery.Input` and `Periphery.Hid` operate at different levels of the OS stack and
serve different use cases. They are independent packages with no direct dependency on
each other.

`Periphery.Input` instantiates the ADR-0024 Layer 1 / Layer 2 pattern with a
non-standard entry point: `InputSession.StartAsync()` takes no `DeviceInfo` argument
because system-wide input monitoring is not scoped to a single device. The
`InputDeviceProxy` (Layer 2) composition around `DeviceTracker` follows the canonical
shape exactly.

| | `Periphery.Hid` | `Periphery.Input` |
|---|---|---|
| OS layer | Raw HID device handle | Input event subsystem |
| Entry point | `HidDevice.OpenAsync(DeviceInfo)` | `InputSession.StartAsync()` |
| Keyboards / mice | ❌ Exclusive access on Win/macOS | ✅ Primary use case |
| Custom HID devices | ✅ Primary use case | ❌ Not applicable |
| Event model | `ReadReportAsync` (pull, per-device) | `IAsyncEnumerable<T>` (push, system-wide) |
| Device identity | Always known (you opened the device) | Known on Win/Linux; null on macOS |

---

## Relationship to ADR-0025

`Periphery.Input` does not introduce new `DeviceCategory` values in v1. If finer-grained
categories (e.g. `DeviceCategory.Keyboard`, `DeviceCategory.Mouse`) are needed later,
they are registered via `[ModuleInitializer]` + `DeviceCategoryRegistry` following the
pattern in ADR-0025, without core library changes.

---

## Consequences

### Positive

- **POS-001**: No exclusive-access problem. The input event subsystem is designed for
  system-wide monitoring — multiple consumers can observe the same keyboard or mouse
  simultaneously.
- **POS-002**: No driver prerequisites on any platform. Raw Input, CGEventTap, and evdev
  are all inbox OS APIs with no third-party dependencies, consistent with the library's
  zero-dependency constraint.
- **POS-003**: `IAsyncEnumerable<T>` backed by `Channel<T>` integrates naturally with
  `await foreach` and the Periphery async-first convention.
- **POS-004**: The `InputDeviceProxy` pattern reuses the `DeviceTracker` composition
  established by ADR-0019 and ADR-0020, validating the generality of that pattern for
  a third extension package.
- **POS-005**: Explicit `InputPermission` API surfaces the macOS permission lifecycle
  as a first-class concern rather than a silent failure mode.

### Negative

- **NEG-001**: **macOS device identity is unavailable.** `CGEventTap` does not expose
  which physical device produced each event. Callers who need per-device attribution
  on macOS (e.g. distinguishing an internal keyboard from an external USB keyboard)
  cannot rely on `KeyboardEvent.Device` and must use workarounds (e.g. tracking
  simultaneously via `IOHIDManager` and correlating by timestamp).
- **NEG-002**: **macOS silent permission failure.** If "Input Monitoring" permission
  is not granted, the tap runs but delivers nothing. The `InputPermission.Check()` /
  `RequestAsync()` surface mitigates this, but callers who skip the check will
  experience a working session object that never emits events.
- **NEG-003**: **Linux requires elevated group or udev rule.** Reading from
  `/dev/input/event*` requires either root or `input` group membership. This is a
  deployment concern, not an API concern, but must be documented prominently.
- **NEG-004**: **Single consumer per stream.** `KeyboardEvents` and `MouseEvents` are
  single-consumer `IAsyncEnumerable<T>` over a `ChannelReader<T>`. Callers requiring
  fan-out must buffer events themselves. Multiple `InputSession` instances are possible
  but each creates its own OS-level tap/registration.
- **NEG-005**: **`Key` enum normalisation is non-trivial.** Mapping Windows virtual
  key codes, macOS `CGKeyCode` values, and Linux `KEY_*` evdev constants to a single
  `Key` enum requires careful handling of extended keys, numpad variants, and
  platform-specific keys with no cross-platform equivalent. Some keys will be
  represented as `Key.Unknown` with `ScanCode` preserved for identification.
- **NEG-006**: **Mouse coordinates are relative, not absolute.** Raw Input, CGEventTap
  in its standard mode, and evdev all deliver relative delta movements. Absolute cursor
  position is not provided by this API; callers who need screen coordinates must
  accumulate deltas or query the OS cursor position separately.

---

## Alternatives Considered

### A — Expose keyboard/mouse via `Periphery.Hid`

Rejected. As established in ADR-0020 (NEG-001), opening keyboard and mouse devices via
`HidDevice.OpenAsync` fails with exclusive-access errors on Windows and macOS. HID
report reading is the wrong abstraction for this use case on two of three platforms.
Even on Linux where hidraw access works, it bypasses the OS input processing that
translates scan codes to key codes and raw motion to cursor deltas.

### B — Use `IOHIDManager` on macOS for per-event device identity

`IOHIDManager` can register per-device callbacks and does supply device identity.
However, as established in ADR-0020, `IOHIDManager` cannot open keyboard/mouse devices
on macOS because the `IOHIDSystem` holds exclusive access. `IOHIDManager` is therefore
not a viable replacement for `CGEventTap` on macOS, and cannot close the device-identity
gap described in NEG-001.

### C — Use `NSEvent.addGlobalMonitorForEvents` on macOS

AppKit's `NSEvent` global monitor API works for macOS applications with a full AppKit
run loop (GUI apps). It requires the app to be a proper macOS application bundle and
has the same "Input Monitoring" permission requirement as `CGEventTap`. For a general
library targeting console apps, services, and non-AppKit hosts, `CGEventTap` is more
appropriate and has no AppKit dependency.

### D — Provide interception (blocking) as well as monitoring

Explicitly deferred. Active interception (`kCGEventTapOptionDefault` on macOS;
low-level hooks via `SetWindowsHookEx(WH_KEYBOARD_LL)` on Windows) requires a
stricter permission posture — both "Input Monitoring" **and** "Accessibility" on macOS,
and latency-sensitive hook-proc timing constraints on Windows (hook removal after
~300 ms). These concerns are separable from passive monitoring and belong in a
dedicated future ADR if the use case is validated.

---

## Open Questions

- **OQ-001**: Should `Key` be an `enum` or a `readonly struct` with well-known static
  instances? An enum is simpler but cannot represent unknown platform keys without
  polluting the enum values. A struct can carry the raw code alongside a nullable
  well-known key, at the cost of a more complex type.
- **OQ-002**: Should `MouseEvent` include absolute cursor position (queried from the OS
  at callback time) in addition to the delta? This would be convenient but is not part
  of the raw event data — it is a separate OS query that may not be coherent under
  heavy load or multi-monitor setups with mixed DPI.
- **OQ-003**: Should `InputSession` expose a unified `IAsyncEnumerable<InputEvent>`
  (a discriminated union of `KeyboardEvent` and `MouseEvent`) in addition to the
  separate typed streams? A unified stream simplifies single-consumer scenarios that
  care about ordering between keyboard and mouse events.
- **OQ-004**: On Linux, should `Periphery.Input` also read from `/dev/input/event*`
  devices under Wayland compositors, or is this out of scope? Wayland restricts direct
  evdev access from non-compositor processes; the answer will depend on whether headless
  / service use cases are a priority.
