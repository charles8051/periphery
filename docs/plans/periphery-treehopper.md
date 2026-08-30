# Plan: Periphery.Usb + Periphery.Treehopper

> **Status:** delivered. `src/Periphery.Usb` (WinUSB + libusb backends) and
> `src/Periphery.Treehopper` both shipped, the latter restructured to a pure core
> by [ADR-0052](../adr/0052-periphery-treehopper-pure-core.md). The board family
> grew past this plan's scope — `Periphery.Treehopper.Libraries`, `.Control`,
> `.Firmware`, `.Flasher`, and CLI/GUI front-ends. Kept as the original design record.
> **Scope:** add a Periphery.Usb extension (Periphery-native raw USB
> I/O via per-platform WinUSB + libusb backends) and rebuild the
> Treehopper SDK on top of it as Periphery.Treehopper.

## Why

[Treehopper](https://treehopper.io) is a great little USB I/O board —
GPIO, I²C, SPI, UART, PWM, ADC, all over USB, with high-level C#
helpers. The [original SDK](https://github.com/treehopper-electronics/treehopper-sdk)
was written several years ago and shows it: lifecycle is brittle,
property setters do USB I/O, there's a global static config flag that
flips the entire library between sync-blocking and fire-and-forget,
and there's no `CancellationToken` anywhere. There's a Treehopper-shaped
hole in modern .NET tooling that Periphery is well-positioned to fill.

Rebuilding it on Periphery gives us:

1. Discovery + reconnect for free (Periphery core's USB enrichment +
   `DeviceSessionHost<TSession>`).
2. A clean async/cancellation story for the I/O layer.
3. A reason to land **Periphery.Usb** as the foundation extension —
   useful on its own and reusable for future custom-USB scenarios
   (instruments, programmers, dev boards, etc.).

The design follows ADR-0026's discovery/I-O boundary, but **explicitly
diverges from ADR-0035's hand-off principle for raw USB**. There isn't
a clean third-party USB library in .NET worth deferring to (LibUsbDotNet
is LGPL-3.0 with no recent stable release; nothing else is
cross-platform). Periphery.Usb owns the I/O surface itself via a small
WinUSB / libusb interop layer — see ADR-0038 for the full rationale.

---

## Audit: what's wrong with the existing C# SDK

Read against `master` of `treehopper-electronics/treehopper-sdk`,
`NET/API/Treehopper/` and `NET/API/Treehopper.Desktop/`. Pain points
fall into three buckets.

### Bucket 1 — async hygiene

These are the most user-facing problems.

1. **Property setters trigger USB I/O.** [`Pin.Mode`][pin-mode] (digital
   in/out, analog in, soft PWM, ...), [`Pin.DigitalValue`][pin-dv],
   [`TreehopperUsb.Led`][led], [`HardwareI2c.Enabled`][i2c-enabled],
   [`HardwareI2c.Speed`][i2c-speed], [`HardwareSpi.Enabled`][spi-enabled].
   Each setter starts an async USB transfer.
2. **Global flag chooses one of two bad strategies.**
   `TreehopperUsb.Settings.PropertyWritesReturnImmediately` is a
   single static `bool` that flips the library between:
   - `Task.Run(() => DoIoAsync()).Wait()` — sync-over-async deadlock
     hazard, blocks the calling thread;
   - `DoIoAsync().Forget()` — fire-and-forget, errors are swallowed,
     the call appears to succeed even when it didn't.

   Neither is correct for a hardware-control library. There is no
   third option exposed.
3. **No `CancellationToken` anywhere.** Open, send, receive, every
   peripheral method — none of them accept one. A hung USB transfer
   is unkillable from managed code.
4. **`Disconnect()` and `Dispose()` are sync.** `ConnectAsync()` is
   async. The asymmetry hurts cleanup paths and async-using.
5. **Constructor does I/O.** `LibUsbConnection`'s ctor calls
   `Task.Run(OpenAsync).Wait()` to read string descriptors, then
   `Close()`. Sync-over-async in a ctor is a classic deadlock-on-UI-
   thread setup.
6. **`AwaitPinUpdateAsync()` races between callers.** It replaces
   the `TaskCompletionSource` on each call, so two concurrent waiters
   compete for one signal. Last-writer-wins.

[pin-mode]:        https://github.com/treehopper-electronics/treehopper-sdk/blob/master/NET/API/Treehopper/Pin.cs
[pin-dv]:          https://github.com/treehopper-electronics/treehopper-sdk/blob/master/NET/API/Treehopper/Pin.cs
[led]:             https://github.com/treehopper-electronics/treehopper-sdk/blob/master/NET/API/Treehopper/TreehopperUsb.cs
[i2c-enabled]:     https://github.com/treehopper-electronics/treehopper-sdk/blob/master/NET/API/Treehopper/HardwareI2c.cs
[i2c-speed]:       https://github.com/treehopper-electronics/treehopper-sdk/blob/master/NET/API/Treehopper/HardwareI2c.cs
[spi-enabled]:     https://github.com/treehopper-electronics/treehopper-sdk/blob/master/NET/API/Treehopper/HardwareSpi.cs

### Bucket 2 — type and lifecycle design

7. **`TreehopperUsb` does too much.** It implements
   `INotifyPropertyChanged`, `IDisposable`, `IComparable`,
   `IEquatable<TreehopperUsb>`, **and** `IEqualityComparer<TreehopperUsb>`
   simultaneously. `Equals(TreehopperUsb x, TreehopperUsb y)` compares
   `DevicePath`; `Equals(object obj)` compares `SerialNumber`.
   Inconsistent.
8. **Equality keyed on `DevicePath`** which is a per-platform string
   (libusb pointer-as-string on Linux, symbolic link on Windows,
   IOReg path on macOS). Periphery already exposes a stable
   `HardwareId` independent of platform.
9. **Singleton `ConnectionService.Instance`.** Global state for
   discovery; not testable, not parameterizable, can't run two
   independent boards on one process cleanly.
10. **No reconnect.** Once `Boards.CollectionChanged` fires a remove,
    the `TreehopperUsb` instance is dead. Apps wire reconnect
    themselves — Periphery has `DeviceSessionHost<TSession>` for
    exactly this.
11. **Pins, modules, and managers are tightly coupled to the board
    instance** through internal fields. Disposing a peripheral
    doesn't release the pins it claimed.
12. **`SoftPwmManager` and `HardwarePwmManager`** live as fields on
    `TreehopperUsb` and mutate when `Pin.Mode = SoftPwm` is set
    (via the property-as-IO setter). State is hidden and racy.

### Bucket 3 — USB layer

13. **Three duplicate per-OS backends.** `LibUsb/`, `WinUsb/`,
    `MacUsb/` each duplicate device enumeration, hot-plug, claim-
    interface, bulk transfer. ~1500 lines of mostly-similar P/Invoke.
14. **Polling `libusb_handle_events`** with `Task.Delay(100)` in an
    unkillable `Task.Run` loop. Should be event-completion-driven
    via `libusb_handle_events_completed`.
15. **Bulk transfers are sync with hardcoded 1000 ms timeout**, no
    cancellation, no override.
16. **`pinListenerTask`** is `Task.Start()`-ed (no
    `Task.Run`/`TaskFactory.StartNew`), runs forever, with a
    switch-on-libusb-error-code loop that decides whether to
    continue, close, or ignore.
17. **WinUsb path ships hand-rolled SetupAPI bindings**
    (~600 lines) for discovery — Periphery core already does this
    via `WindowsDeviceMonitorProvider`/`WindowsCategoryMap`.

The good news: the **wire protocol** is well-defined and small (see
`DeviceCommands.cs` — 17 command bytes, fixed-shape packets, two bulk
endpoints in each direction). Re-implementing it on top of a clean
USB hand-off is not a big lift.

---

## Periphery.Usb — foundation extension

See ADR-0038 for the decision and full rationale on owning the USB
I/O surface ourselves rather than handing off to LibUsbDotNet. Short
version: LibUsbDotNet is LGPL-3.0 (problematic under NativeAOT), has
no proper release since 2023, and there isn't a third-party USB
ecosystem in .NET worth deferring to the way camera, audio, and
serial each have one.

### Goals

- Discover any USB device via Periphery core's existing USB enumeration.
- Provide a Periphery-native `UsbDeviceProxy` that owns lifecycle
  and exposes claim-interface + control/bulk/interrupt/iso transfers
  with a clean async + cancellation surface.
- Per-platform backends behind a small `IUsbBackend` shim: WinUSB
  direct on Windows (no native binary; in-OS), libusb on Linux/macOS.
- Lifecycle (open on connect, dispose on disconnect, reconnect
  transparently) via `DeviceSessionHost<UsbSession>`.

### Non-goals

- Per-USB-class abstractions (HID, MSC, etc.). Those are separate
  Periphery extensions or are already handled by core.
- Driver installation. We surface "this device is bound to driver
  X" as metadata; the user runs Zadig / writes a `.inf` if they care.
- Iso transfers in v1. Bulk + interrupt + control covers every
  consumer we have lined up; iso is real work and can be a follow-up.

### API sketch

```csharp
// Discovery — already in core, just filter to USB
var devices = await Devices.Enumerate()
    .OfCategory(DeviceCategory.UsbDevice)
    .WithUsbId("10C4", "8A7E")              // Treehopper VID/PID
    .ToListAsync();

// One-shot open — Periphery-native types throughout
await using var proxy = await UsbDeviceProxy.OpenAsync(devices[0], ct);
proxy.ClaimInterface(0);
var rx = await proxy.BulkReadAsync(endpointAddress: 0x81, count: 64, ct);
await proxy.BulkWriteAsync(endpointAddress: 0x01, data, ct);

// Reconnect-aware
var profile = new DeviceProfile(f => f.WithUsbId("10C4", "8A7E"), "Treehopper");
await using var host = await DeviceSessionHost<UsbSession>.StartAsync(
    profile,
    createSession: (info, ct) => UsbSession.OpenAsync(info, ct),
    ct: ct);
```

### Discovery metadata Periphery.Usb adds

Already partially there in core; finalize as part of this extension:

| Field                  | Source                                        | Meaning                                         |
|---|---|---|
| `Vid`, `Pid`           | descriptor                                    | already exposed by core                         |
| `SerialNumber`         | string descriptor                             | already exposed                                 |
| `Configurations[]`     | descriptor walk                               | for users that need non-default config          |
| `Interfaces[]`         | descriptor walk                               | needed before claiming                          |
| `Endpoints[]`          | descriptor walk                               | direction, type (control/bulk/interrupt/iso), MPS |
| `BoundDriver`          | SetupAPI / `/sys/bus/usb/devices/.../driver` / IOKit | "WinUSB" / "usbhid" / "uvcvideo" / "(unbound)"  |
| `Claimable`            | derived from `BoundDriver` per platform       | predicts whether claim-interface will succeed |

The last two are the most valuable bits and the hardest cross-
platform. Plan to ship without them in v1 and add them once we have
Periphery.Treehopper as a real-world test bed.

### Open questions

- **WinUSB hot-plug on non-class devices.** Periphery core already
  gets device-arrival events via SetupAPI. Worth a smoke test that
  the path lights up cleanly for WinUSB-bound devices like
  Treehopper specifically.
- **libusb async transfer model.** v1 uses a dedicated event-handling
  thread loop on `libusb_handle_events_completed`. Revisit if it
  shows as a profiling hotspot.
- **macOS arm64 libusb binary distribution.** Either system-installed
  via Homebrew or shipped via NuGet `runtimes/osx-arm64/native/` —
  decide before v1.
- **Driver-binding metadata** is genuinely hard cross-platform.
  Defer to a follow-up enricher.
- **Hot-plug events.** Core already has `DeviceWatcher`. The question
  is whether USB-specific descriptor metadata gets re-fetched on
  reconnect or whether we trust it's identical (same serial, same
  interfaces). Default: trust, re-fetch on demand.

---

## Periphery.Treehopper — protocol layer

Built on Periphery.Usb. Wraps the Treehopper wire protocol
(`DeviceCommands.cs`) in a modern .NET API with the lifecycle and
async hygiene the original SDK lacks.

### Design principles

1. **No property setter does I/O.** Every hardware mutation is an
   explicit `Async` method that takes a `CancellationToken`.
2. **No fire-and-forget.** No `.Forget()`. No global "return
   immediately" flag. Every I/O call returns a `Task` you can await,
   cancel, or observe failures from.
3. **No sync-over-async.** No `Task.Run(...).Wait()`, no
   `Result`-on-Task. `IAsyncDisposable` for cleanup.
4. **Identity from Periphery.** Equality, hashing, reconnect matching
   all delegate to `DeviceInfo.HardwareId` from core.
5. **Peripherals are leases.** `await board.UseI2cAsync(...)` returns
   an `IAsyncDisposable` handle; disposing it releases the pins it
   reserved. Trying to use I²C while SPI holds the same pins fails
   with a clear exception.
6. **Pin updates are an async stream**, not a TCS replay. Multiple
   readers, optional cancellation, no race on `AwaitPinUpdateAsync`.

### API sketch

```csharp
// Discovery
var devices = await Devices.Enumerate()
    .OfCategory(DeviceCategory.UsbDevice)
    .WithUsbId(TreehopperBoard.Vid, TreehopperBoard.Pid)
    .ToListAsync();

// One-shot open
await using var board = await TreehopperBoard.OpenAsync(devices[0], ct);

Console.WriteLine($"{board.Name} — firmware {board.FirmwareVersion}");

// Pins — explicit configuration, no property-as-IO
await using var led = await board.Pins[7].ConfigureAsync(
    PinMode.PushPullOutput, ct);
await led.WriteAsync(true, ct);
await led.WriteAsync(false, ct);

await using var sensor = await board.Pins[12].ConfigureAsync(
    PinMode.AnalogInput, AdcReferenceLevel.Vref_3V3, ct);
double volts = await sensor.ReadVoltageAsync(ct);

// Streaming pin updates (replaces OnPinValuesUpdated + AwaitPinUpdateAsync)
await foreach (var update in board.PinUpdates.WithCancellation(ct))
{
    Console.WriteLine($"pin {update.PinIndex} → {update.DigitalValue}");
}

// Hardware peripherals — exclusive lease
await using (var i2c = await board.UseI2cAsync(speedKhz: 400, ct))
{
    var data = await i2c.SendReceiveAsync(
        address: 0x17, write: new byte[] { 0x31 }, readLen: 2, ct);
}

await using (var spi = await board.UseSpiAsync(
    mode: SpiMode.Mode00, speedMhz: 6, ct))
{
    var rx = await spi.SendReceiveAsync(tx, chipSelect: board.Pins[10], ct);
}

// Onboard LED — explicit, async, cancellable
await board.SetLedAsync(true, ct);

// Reconnect-resilient
await using var host = await DeviceSessionHost<TreehopperBoard>.StartAsync(
    profile: new DeviceProfile(
        f => f.WithUsbId(TreehopperBoard.Vid, TreehopperBoard.Pid),
        name: "Treehopper"),
    createSession: TreehopperBoard.OpenAsync,
    ct: ct);

host.PropertyChanged += (_, e) =>
{
    if (e.PropertyName == nameof(host.Status))
        Console.WriteLine($"Treehopper: {host.Status.GetType().Name}");
};
```

### Internals

- **`TreehopperBoard`** — replaces `TreehopperUsb`. Implements
  `IAsyncDisposable` only. Holds an `UsbDeviceProxy` from
  Periphery.Usb. Identity via `DeviceInfo`.
- **`PinController`** — internal. Owns the pin update producer
  (channel-based, like `CameraSession`), claims/releases per-pin
  state, surfaces `IAsyncEnumerable<PinUpdate>`.
- **`PinHandle : IAsyncDisposable`** — returned by
  `Pin.ConfigureAsync(...)`. Configures the pin once on construction,
  releases (and resets to `Unassigned`) on dispose.
- **`I2cLease`, `SpiLease`, `UartLease`, `PwmLease`** — internal
  exclusive-access types. Each peripheral has at most one outstanding
  lease.
- **`TreehopperWireProtocol`** — internal. Encodes/decodes the 17
  `DeviceCommand` packets against two `UsbDeviceProxy` bulk
  endpoints (one IN pair, one OUT pair). Single point of byte-level
  coupling with the firmware.

The split is: Pin/peripheral classes are public ergonomics;
`TreehopperBoard` is the public coordinator; `TreehopperWireProtocol`
is the private wire layer. The original SDK conflated all three —
this version separates them so the wire protocol can be tested in
isolation against a fake `IUsbBulkChannel`.

### Cancellation strategy

- All public async methods accept a `CancellationToken`. Internally
  composed with a `_disposeCts` so disposal cancels in-flight calls.
- Bulk-transfer cancellation is honored at the `UsbDeviceProxy`
  boundary; if a transfer can't actually be killed mid-flight
  (libusb / WinUSB constraint depending on platform), at minimum
  the C# task observes cancellation promptly and the next dispatch
  loop iteration short-circuits.
- The pin-update producer task lives on a `_pinReportCts` separate
  from per-call cancellation, so cancelling a `WriteAsync` doesn't
  tear down the pin-report stream.

### What we're keeping from the original SDK

- The wire protocol (it's correct and stable; firmware is the long-
  pole upstream we don't control).
- The `DeviceCommands` byte values (compatibility with existing
  Treehopper firmware).
- Names and concepts where they're clear: `PinMode`, `SpiMode`,
  `I2cTransferException`, `AdcReferenceLevel`, etc.
- `Treehopper.Libraries` — the catalog of pre-built peripheral
  drivers (port expanders, displays, sensors). Out of scope for v1
  but will sit on top of Periphery.Treehopper unchanged in spirit.

### What we're not keeping

- `INotifyPropertyChanged` everywhere. Pin values fire either as
  events or as `IObservable<T>` / `IAsyncEnumerable<T>` — opt-in.
- Equality / hashing methods on the board class. Use `DeviceInfo`.
- `Settings.PropertyWritesReturnImmediately`. Replaced by
  cancellation tokens and explicit awaits.
- The `ConnectionService.Instance` singleton.
- Per-OS connection backends. Periphery core + Periphery.Usb's
  WinUSB / libusb backends do this once for everyone.

---

## Migration story for existing Treehopper SDK users

This is a breaking API change. Reasonable, given the lifecycle issues
in the original SDK make most non-trivial code subtly broken anyway.

### Minimum viable migration

```csharp
// before
var board = await ConnectionService.Instance.GetFirstDeviceAsync();
await board.ConnectAsync();
board.Pins[7].Mode = PinMode.PushPullOutput;
board.Pins[7].DigitalValue = true;

// after
var info = (await Devices.Enumerate()
    .OfCategory(DeviceCategory.UsbDevice)
    .WithUsbId(TreehopperBoard.Vid, TreehopperBoard.Pid)
    .ToListAsync()).First();
await using var board = await TreehopperBoard.OpenAsync(info);
await using var pin = await board.Pins[7].ConfigureAsync(PinMode.PushPullOutput);
await pin.WriteAsync(true);
```

Three more lines. The new shape is more verbose for the trivial case;
the trade-off is that for non-trivial cases (multiple pins,
peripherals, reconnect, cancellation) it's far less code than the
original SDK forced.

### Ergonomic shorthand

For existing users porting scripts, an optional `TreehopperShorthand`
extension method bag could give back synchronous-feeling pin writes
that internally wait properly:

```csharp
await board.Pins[7].SetPushPullOutputAsync(true, ct);   // configure + write in one
```

Worth adding only if there's actual demand.

### Treehopper.Libraries port

Out of scope for v1. The libraries currently call the property-style
API, so they'd need a lightweight rewrite against the new
`I2cLease.SendReceiveAsync` etc. Mechanical, but bulk. Defer to v2.

---

## Implementation order

1. **Periphery.Usb** (2–3 weeks)
   - csproj, project reference layout under `src/Periphery.Usb/`
   - `IUsbBackend` shim + per-platform implementations:
     - Windows: WinUSB direct via `[LibraryImport("winusb.dll")]`
       (~150–200 lines)
     - Linux/macOS: libusb-1.0 direct via
       `[LibraryImport("libusb-1.0…")]` (~150–250 lines)
   - `UsbDeviceProxy.OpenAsync` over a `DeviceInfo` from core
   - Public transfer surface: `BulkReadAsync`, `BulkWriteAsync`,
     `InterruptReadAsync`, `InterruptWriteAsync`, `ControlTransferAsync`
   - Discovery metadata gaps from existing core enrichment
     (interfaces, endpoints) — may need a small enricher in
     `src/Periphery/Windows/WindowsUsbDescriptorEnricher.cs` and a
     Linux equivalent
   - Tests via a fake `IUsbBackend` shim (Periphery.Tests pattern)
   - One example: `examples/Periphery.Usb.Example/` — list devices,
     dump descriptors, claim and read from the first matching device
   - Pattern doc: `docs/patterns/usb-device-handoff.md` (the term
     "hand-off" is from the consumer's perspective — they get a
     usable device proxy from Periphery; we just don't pretend the
     proxy came from a third-party library)
   - ADR-0038

2. **Periphery.Treehopper — protocol layer** (1 week)
   - `TreehopperWireProtocol` against `IUsbBulkChannel` shim
   - Unit tests for every `DeviceCommands` packet shape
   - No board class yet — just packet encoding / decoding

3. **Periphery.Treehopper — board surface** (1–2 weeks)
   - `TreehopperBoard.OpenAsync(DeviceInfo)`
   - `Pin`, `PinHandle`, `PinMode` enum surface
   - `IAsyncEnumerable<PinUpdate>` producer
   - `SetLedAsync`, `RebootAsync`, name/serial accessors
   - LED + GPIO example
   - Tests against a `FakeUsbBulkChannel` that round-trips firmware
     packet shapes

4. **Periphery.Treehopper — peripherals** (1–2 weeks)
   - `UseI2cAsync`, `UseSpiAsync`, `UseUartAsync`, `UsePwmAsync`,
     `UseOneWireAsync`
   - Pin reservation + lease enforcement
   - Examples for I²C scan, SPI loopback, UART echo, PWM dimmer

5. **Reconnect resilience + lifecycle test harness** (~3–5 days)
   - `ILifecycleHarness` interface in
     `tests/Periphery.Treehopper.IntegrationTests/`
   - First impl: `LinuxAuthorizedHarness` (sysfs `authorized` toggle
     against a real Treehopper). udev rule documented so tests
     don't need sudo at runtime.
   - Integration tests for the high-value scenarios catalogued in
     [`docs/patterns/usb-lifecycle-testing.md`](../patterns/usb-lifecycle-testing.md):
     mid-transfer disconnect, reconnect identity, multi-board
     safety, rapid-cycle replug, disconnect during peripheral lease.
   - Example: Treehopper-as-status-LED that survives unplug.
   - Other harness flavors (`WslUsbipdHarness`, `QemuQmpHarness`,
     `UhubctlHarness`) added on demand, not up front.

6. **Documentation + ADR-0039** (couple of days)
   - Migration guide from the original SDK
   - Cross-link from the Periphery.Usb README
   - Move this plan doc into ADR-0039 once accepted, or keep it as a
     reference plan

7. **Wire-level test rig (stretch, post-v1)** (~2 weeks)
   - `SaleaeBusVerifier` against Logic 2's gRPC automation API
   - `tests/Periphery.Treehopper.WireTests/` — small, targeted set
     (~10 tests across I²C / SPI / UART / GPIO / PWM / 1-Wire /
     parallel) that captures real bus traffic and asserts against
     decoded frames
   - Self-hosted nightly / on-demand runner with the wiring rig
     documented and photographed
   - Catches protocol-encoding, mode-bit, and clock-divider bugs the
     other tiers can't reach. See
     [`docs/patterns/wire-level-testing.md`](../patterns/wire-level-testing.md).

Total estimate: ~6–8 weeks for v1 (steps 1–6) covering GPIO, I²C,
SPI, UART, PWM, ADC, soft-PWM, and reconnect (revised up from the
prior ~5–7 to account for hand-rolling the per-platform USB
backends instead of taking a third-party dep). Wire-level test rig
(step 7) is a stretch goal; ~2 additional weeks once the rig is
wired up. Treehopper.Libraries port is a separate v2.

---

## Open questions / risks

1. **libusb on macOS arm64.** Need to confirm whether we ship the
   binary via NuGet `runtimes/osx-arm64/native/` or rely on Homebrew.
   Worth deciding before v1 so users don't hit silent missing-dylib
   failures.
2. **Windows driver binding.** Treehopper ships a `.inf` that binds
   it to WinUSB. Verify our `BoundDriver` discovery surface lights up
   correctly and the WinUSB backend claims it without ceremony. If a
   user has the wrong driver, the error message should say so.
3. **Firmware version skew.** Original SDK warns at runtime if
   firmware is below `MinimumSupportedFirmwareVersion = 111`. New
   library should make this an explicit `TreehopperBoard.OpenAsync`
   failure (with a typed exception) rather than a `Debug.WriteLine`.
4. **Concurrency with the soft-PWM and pin-update streams.** Original
   SDK uses an `AsyncLock` (`ComsLock`) around all coms. Plan to keep
   a similar `SemaphoreSlim` internally so peripheral leases can
   interleave with pin reports. Worth measuring whether the lock is
   per-board or per-endpoint — bulk-out probably needs serialization,
   bulk-in (pin reports) doesn't.
5. **Treehopper.Libraries.** The catalog port is the longest tail of
   this work. Some libraries are non-trivial (real-time MIDI, HID,
   display controllers). Rather than block v1, ship v1 against the
   raw I²C/SPI surface and let the catalog port land incrementally.
6. **Naming.** Treehopper is a third-party trademark. Periphery.Treehopper
   is fine for an unaffiliated open-source rebuild, but worth a
   courtesy heads-up to the original maintainers when v1 is close.

---

## Out of scope

- Treehopper Pro variants (different VID/PID, different pin count) —
  add as v1.x once the v1 board is stable.
- Firmware updater + bootloader-mode protocol. **Update 2026-06-03:**
  promoted from out-of-scope to the backlog (P1). The bootloader is a
  USB-HID device, so `Periphery.Hid` makes it cheap — no separate USB
  backend needed. Tracked as a deferred-work item at the time; the
  firmware-reflash work that followed is ADR-0064.
- Non-USB Treehopper variants (none currently exist).
- Cross-language SDK parity (Java, MATLAB, PowerShell, Python). Out of
  the .NET-extension scope by definition.

---

## What this validates beyond Treehopper

If this plan ships cleanly, we'll have:

- A reusable `Periphery.Usb` foundation that future custom-USB
  extensions sit on (Saleae logic analyzer, J-Link programmer,
  Bus Pirate, etc.) — Periphery-native API, license-clean,
  AOT-friendly.
- A worked example of when **not** to apply the hand-off principle.
  Camera, audio, and serial each defer to a strong third-party
  ecosystem; raw USB doesn't have one. Future extension ADRs should
  evaluate against both options rather than defaulting either way.
  This is a useful counter-example to keep around so the principle
  doesn't calcify into a rule.
- A worked example of replacing a `Task.Run(...).Wait()` /
  `.Forget()` SDK with proper async + cancellation. Useful pattern
  doc for whenever someone tries to bring a similarly-aged SDK
  forward.
- The `ILifecycleHarness` testing pattern documented in
  [`docs/patterns/usb-lifecycle-testing.md`](../patterns/usb-lifecycle-testing.md)
  generalizes to every future USB-based extension. The same
  Linux `authorized` toggle that drives Treehopper lifecycle tests
  drives Periphery.Saleae, Periphery.JLink, etc. — write the
  abstraction once, reuse for the rest.
