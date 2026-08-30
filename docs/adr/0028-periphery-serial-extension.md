---
title: "ADR-0028: Periphery.Serial — Cross-Platform Serial Port I/O Extension"
status: "Superseded"
date: "2026-07-14"
authors: "@charles8051 (design)"
tags: ["architecture", "decision", "serial", "extension", "api-design", "periphery-serial", "i/o", "pipelines", "uart"]
supersedes: ""
superseded_by: "ADR-0062"
---

# ADR-0028: Periphery.Serial — Cross-Platform Serial Port I/O Extension

## Status

> **Superseded by [ADR-0062](0062-periphery-serial-backend-provider.md).** This
> ADR's decision — `Periphery.Serial` as a single from-scratch native P/Invoke
> implementation — is replaced by a **backend-provider model** (`Periphery.Serial`
> abstraction + `.Bcl` / `.RJCP` backends). The discovery/IO boundary, the
> `PipeReader`/`PipeWriter` surface, `SerialPortOptions`, the custom-baud
> requirement, and the native platform tables below are **retained** as the design
> reference for the (deferred) `Periphery.Serial.Native` backend (ADR-0062 DEC-004).

---

## Context

### The discovery / I/O boundary

`Periphery` is a discovery-only library. Its public contract is explicit: it enumerates
hardware devices and returns immutable `DeviceInfo` snapshots. It never opens device
handles, sends commands, or reads data streams. `Periphery` already discovers serial
ports on all three platforms and exposes them as `DeviceInfo` records with
`DeviceCategory.Ports`. A companion package `Periphery.Serial` provides the natural
next step: opening those discovered ports and performing I/O.

### Why not `System.IO.Ports.SerialPort`

`System.IO.Ports` ships a managed `SerialPort` type with a number of well-documented,
unfixable design flaws that make it unsuitable as the foundation for a modern library:

| Problem | Impact |
|---|---|
| `DataReceived` is unreliable | Fires on a threadpool thread with no delivery guarantee; spurious wakeups with `BytesToRead == 0` |
| `ReadLine()` / `ReadTo()` can deadlock | Timeout is not correctly honoured on Linux; blocks indefinitely if the terminator never arrives |
| `Close()` / `Dispose()` can hang | Background read threads can deadlock during disposal; requires a workaround (`ReadTimeout` shortening) |
| `IsOpen` lies | Returns `true` after a physical disconnect |
| `GetPortNames()` is unreliable | Reads stale registry entries on Windows; returns wrong or incomplete names on Linux and macOS |
| No `CancellationToken` support | The only reliable abort mechanism is a timeout hack |
| No `System.IO.Pipelines` integration | Consumer must manually manage read buffers and framing |

The `GetPortNames()` problem is moot for `Periphery.Serial` because callers always
receive the port name from a `DeviceInfo` already discovered by the core library. The
remaining issues, however, are fundamental to `System.IO.Ports`'s design and cannot
be worked around without replacing the underlying I/O loop.

### Infrastructure reuse argument

`Periphery` already ships three platform P/Invoke layers that can be directly extended:

| Platform | Existing | New for Serial |
|---|---|---|
| Windows | `kernel32.dll`, `setupapi.dll`, `cfgmgr32.dll` via P/Invoke | `CreateFile`, `ReadFile`/`WriteFile` (OVERLAPPED), `SetCommState` (DCB), `SetCommTimeouts`, `WaitCommEvent` — all in `kernel32.dll` |
| Linux | `libudev.so.1`, `libc` via P/Invoke | `open`/`close`, `read`/`write`, `termios`/`termios2`, `epoll`, `ioctl` (TIOCM*) — all in `libc` |
| macOS | IOKit, CoreFoundation, `libc` via P/Invoke | `open`/`close`, `read`/`write`, `termios`, `kqueue`, `ioctl` (TIOCM*) — all in `libc`; port discovery already done via IOKit |

The P/Invoke scaffolding patterns, cross-platform abstraction shape, and `IAsyncDisposable`
lifecycle conventions are already established. The new code is almost entirely new
native API surface inside familiar scaffolding — not new architectural patterns.

### What serial I/O requires at the OS level

A serial port I/O session requires four distinct concerns beyond port discovery:

1. **Open** — obtain a platform handle with exclusive access.
2. **Configure** — baud rate, data bits, stop bits, parity, flow control.
3. **Transfer** — asynchronous read and write with cancellation support.
4. **Modem control** — read/write DTR, RTS, CTS, DSR, DCD, RI pin states.

Custom baud rates (non-standard values such as 74880 or 250000) are a real requirement
for embedded protocols (ESP8266 boot loader, DMX512). `termios2` on Linux supports
arbitrary baud rates via the `BOTHER` flag; Windows DCB supports any DWORD rate;
macOS `termios` is limited to a predefined set (custom rates require an IOKit ioctl
extension).

---

## Decision

Implement `Periphery.Serial` as a companion I/O extension package following the
Layer 1 / Layer 2 extension package pattern defined in ADR-0024.

### Layer 1 — `SerialPort` (the I/O primitive)

`SerialPort` is the explicit, named crossing of the discovery / I/O boundary. It is
created only by `SerialPort.OpenAsync(DeviceInfo, SerialPortOptions)`. The async read
and write surface is exposed via `System.IO.Pipelines` — `PipeReader` and `PipeWriter`
— which sidesteps every `DataReceived`, `ReadLine`, and buffer-management problem in
`System.IO.Ports`:

```csharp
public sealed class SerialPort : IAsyncDisposable
{
    // Discovery context
    public DeviceInfo DeviceInfo { get; }
    public SerialPortOptions Options { get; }

    // Primary I/O surface — System.IO.Pipelines
    public PipeReader Reader { get; }
    public PipeWriter Writer { get; }

    // Modem control lines
    public SerialPinStates PinStates { get; }
    public Task SetDtrAsync(bool value, CancellationToken ct = default);
    public Task SetRtsAsync(bool value, CancellationToken ct = default);

    // Factory — the explicit crossing of the discovery / I/O boundary
    public static Task<SerialPort> OpenAsync(
        DeviceInfo device,
        SerialPortOptions options,
        CancellationToken ct = default);
}

public sealed record SerialPortOptions
{
    public int BaudRate { get; init; } = 9600;
    public DataBits DataBits { get; init; } = DataBits.Eight;
    public StopBits StopBits { get; init; } = StopBits.One;
    public Parity Parity { get; init; } = Parity.None;
    public FlowControl FlowControl { get; init; } = FlowControl.None;
    public TimeSpan ReadTimeout { get; init; } = Timeout.InfiniteTimeSpan;
    public TimeSpan WriteTimeout { get; init; } = Timeout.InfiniteTimeSpan;
}

[Flags]
public enum SerialPinStates
{
    None = 0,
    Cts  = 1 << 0,   // Clear To Send
    Dsr  = 1 << 1,   // Data Set Ready
    Dcd  = 1 << 2,   // Data Carrier Detect
    Ring = 1 << 3,   // Ring Indicator
    Dtr  = 1 << 4,   // Data Terminal Ready (output)
    Rts  = 1 << 5,   // Request To Send (output)
}
```

The `PipeReader` / `PipeWriter` surface enables callers to implement framing protocols
(newline-delimited, length-prefixed, COBS, HDLC, etc.) using standard
`System.IO.Pipelines` idioms without any buffer management boilerplate. It also
integrates naturally with `System.IO.Pipelines`-aware libraries such as `Bedrock.Framework`.

### Layer 2 — `SerialPortHandle` (the lifecycle manager)

`SerialPortHandle` composes around a `DeviceTracker` and manages the `SerialPort`
open/close lifecycle automatically: opening a port when the tracker transitions to
connected, disposing it on disconnect. The caller configures port options once and
receives a live `SerialPort?` reference:

```csharp
public sealed class SerialPortHandle : INotifyPropertyChanged, IAsyncDisposable
{
    public static Task<SerialPortHandle> OpenAsync(
        DeviceProfile profile,
        SerialPortOptions options,
        CancellationToken ct = default);

    public bool IsConnected { get; }
    public DeviceInfo? DeviceInfo { get; }
    public SerialPort? Port { get; }

    public event EventHandler<SerialPort>? PortOpened;
    public event EventHandler? PortClosed;
    public event PropertyChangedEventHandler? PropertyChanged;
}
```

### Call-site shape

```csharp
// One-shot: discover and open a specific serial port
var device = await Devices.Enumerate()
    .OfCategory(DeviceCategory.Ports)
    .WithName("USB Serial")
    .WithUsbId("0403", "6001")   // FTDI FT232R
    .FirstOrDefaultAsync();

var options = new SerialPortOptions { BaudRate = 115200 };
await using var port = await SerialPort.OpenAsync(device, options);

// Read lines using System.IO.Pipelines
await foreach (var line in port.Reader.ReadLinesAsync(ct))
    Console.WriteLine(line);

// Lifecycle-managed: auto-opens on connect, auto-closes on disconnect
var profile = new DeviceProfile(f => f
    .WithUsbId("0403", "6001")
    .WithName("USB Serial"));

await using var handle = await SerialPortHandle.OpenAsync(
    profile,
    new SerialPortOptions { BaudRate = 115200, FlowControl = FlowControl.RtsCts },
    ct);

handle.PortOpened  += (_, p) => Console.WriteLine($"Port opened: {p.DeviceInfo.Name}");
handle.PortClosed  += (_, _) => Console.WriteLine("Port closed.");
```

### Platform implementation

#### Windows — `kernel32.dll` + OVERLAPPED

The Windows implementation uses `FILE_FLAG_OVERLAPPED` I/O throughout. All reads and
writes are submitted as `OVERLAPPED` operations and awaited via
`ThreadPool.UnsafeQueueNativeOverlapped` / `NativeOverlapped` callbacks, keeping the
managed async model honest. The `PipeWriter` fill loop calls `ReadFile` with an
`OVERLAPPED` and posts the result to the pipe on completion:

| Operation | Win32 API |
|---|---|
| Open | `CreateFile(\\.\COMn, GENERIC_READ\|GENERIC_WRITE, 0, NULL, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, NULL)` |
| Configure | `GetCommState` / `SetCommState` (DCB) + `SetCommTimeouts` |
| Read | `ReadFile` with `OVERLAPPED` + `GetOverlappedResult` |
| Write | `WriteFile` with `OVERLAPPED` + `GetOverlappedResult` |
| Cancel | `CancelIoEx` on the file handle |
| Modem lines | `GetCommModemStatus` (read) + `EscapeCommFunction` (DTR/RTS) |
| Events | `SetCommMask` + `WaitCommEvent` (OVERLAPPED) for pin-change notifications |
| Flush | `PurgeComm(PURGE_RXABORT \| PURGE_TXABORT)` before `CloseHandle` |

The Windows OVERLAPPED path is the most implementation-complex piece of this library.
`CancelIoEx` provides reliable cancellation; `PurgeComm` before `CloseHandle` prevents
`CloseHandle` from blocking on in-flight I/O. Custom baud rates are set directly in
the `BaudRate` field of the `DCB` structure — Windows accepts any DWORD value.

#### Linux — `libc` + `termios2` + `epoll`

The Linux implementation opens ports in non-blocking mode and drives reads via
`epoll_wait` on a dedicated I/O thread that feeds the `PipeWriter`:

| Operation | Linux API |
|---|---|
| Open | `open(/dev/ttyUSBn, O_RDWR \| O_NOCTTY \| O_NONBLOCK)` |
| Configure | `tcgetattr` / `tcsetattr` (standard baud) or `ioctl(TCSETS2, termios2)` (custom baud via `BOTHER`) |
| Read | `read(fd, buf, n)` driven by `epoll_wait(EPOLLIN)` |
| Write | `write(fd, buf, n)` — non-blocking; retry on `EAGAIN` |
| Cancel | `eventfd` added to the `epoll` set; signal on cancellation |
| Modem lines | `ioctl(TIOCMGET)` (read) + `ioctl(TIOCMBIS / TIOCMBIC)` (set/clear) |
| Flush | `tcflush(TCIOFLUSH)` before `close` |

`termios2` (accessed via `ioctl(TCGETS2)` / `ioctl(TCSETS2)`) is required for custom
baud rates; the `BOTHER` flag in `c_ispeed` / `c_ospeed` accepts any integer value.
Standard baud rates continue to use the `tcgetattr` / `tcsetattr` path for maximum
kernel compatibility.

#### macOS — `libc` + `termios` + `kqueue`

The macOS implementation is structurally identical to Linux with two differences:

1. **Port naming**: `/dev/cu.*` is used exclusively (not `/dev/tty.*`). The `cu`
   (call-up) device does not block `open()` waiting for a carrier detect signal,
   which is the correct behaviour for initiating a connection. `tty` devices are
   for answering incoming connections and will block indefinitely if CD is not asserted.

2. **Async I/O multiplexing**: `kqueue` / `kevent` is used instead of `epoll`.
   A `EVFILT_READ` filter on the serial fd drives the read loop; a `EVFILT_USER`
   event provides the cancellation signal.

Custom baud rates on macOS require `ioctl(IOSSIOSPEED)` from `<IOKit/serial/ioss.h>`,
which is a macOS-specific extension to `termios`. The set of supported rates is
wider than standard `termios` but narrower than Linux `termios2`; unsupported rates
throw `SerialPortException` with a clear message.

---

## Consequences

### Positive

- **POS-001**: Callers receive a `PipeReader`/`PipeWriter` surface that eliminates
  every `System.IO.Ports` buffer-management, deadlock, and event-reliability problem.
- **POS-002**: `CancellationToken` is respected at the native level on all platforms
  (`CancelIoEx` on Windows, `eventfd`/`EVFILT_USER` on Linux/macOS) — not simulated
  via timeout hacks.
- **POS-003**: Discovery and I/O are cleanly separated: the caller always knows which
  physical device they are talking to because `DeviceInfo` is on `SerialPort`.
  `GetPortNames()` ambiguity is eliminated.
- **POS-004**: Custom baud rates (74880, 250000, etc.) are supported on all three
  platforms, which is a concrete gap in `System.IO.Ports`.
- **POS-005**: `SerialPortHandle` provides the same plug-and-play reconnection
  semantics as `DeviceTracker` — consumers never write open/close lifecycle code.
- **POS-006**: Re-uses all existing P/Invoke scaffolding; no new dependency on
  `System.IO.Ports` or any third-party package.

### Negative

- **NEG-001**: Windows OVERLAPPED I/O with correct cancellation and `NativeOverlapped`
  lifetime management is non-trivial to implement and test correctly.
- **NEG-002**: End-to-end testing requires physical hardware or a virtual serial port
  pair (`com0com` on Windows, `socat` on Linux/macOS). Unit testing the platform
  backends in isolation requires a virtual COM port abstraction.
- **NEG-003**: macOS custom baud rate support (`IOSSIOSPEED`) is limited relative to
  Linux `termios2`; some embedded protocols using non-standard rates may not be
  achievable on macOS.
- **NEG-004**: `Periphery.Serial` inherits the `Periphery` core constraint of zero
  external runtime dependencies, ruling out `RJCP.SerialPortStream` as a foundation
  even though it solves many of the same problems.

---

## Alternatives Considered

### `System.IO.Ports.SerialPort` as the implementation foundation

- **ALT-001**: **Description**: Wrap `System.IO.Ports.SerialPort.BaseStream` in a
  `PipeReader` to expose the Pipelines surface while delegating actual I/O to the
  BCL type.
- **ALT-002**: **Rejection Reason**: `BaseStream` reads do respect `ReadTimeout`, but
  `Close()`/`Dispose()` deadlock risk and `IsOpen` unreliability remain. Port
  enumeration via `GetPortNames()` is still unreliable on Linux and macOS. The wrapper
  approach inherits all the fundamental design problems; it does not fix them.

### `RJCP.SerialPortStream`

- **ALT-003**: **Description**: Take a dependency on `RJCP.SerialPortStream`, which
  already provides a correctly-implemented async serial I/O layer on all three
  platforms using native I/O directly.
- **ALT-004**: **Rejection Reason**: Violates the zero-external-runtime-dependencies
  constraint. `RJCP.SerialPortStream` is an excellent library and is the right choice
  for applications that do not need to build on Periphery's discovery infrastructure.
  For `Periphery.Serial`, the goal is a first-class citizen in the Periphery ecosystem
  with seamless `DeviceInfo` ↔ `SerialPort` integration and no additional supply-chain
  surface.

### Defer indefinitely / never build

- **ALT-005**: **Description**: Document serial I/O as out of scope and direct users
  to `System.IO.Ports` or `RJCP.SerialPortStream`.
- **ALT-006**: **Rejection Reason**: Serial ports are a first-class device category in
  Periphery (`DeviceCategory.Ports`), and the extension library pattern established in
  ADR-0024 exists precisely to support this kind of companion I/O package. Indefinite
  deferral leaves a conspicuous gap in the ecosystem. The design is straightforward;
  the only reason to defer is sequencing behind `Periphery.Hid` and `Periphery.Usb`.
