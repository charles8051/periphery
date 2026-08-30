---
title: "ADR-0039: Periphery.Treehopper — clean rebuild of the Treehopper SDK"
status: "Accepted"
status_note: "Shipped - `src/Periphery.Treehopper`, restructured to a pure core by [ADR-0052](0052-periphery-treehopper-pure-core.md)."
date: "2026-05-08"
authors: "@charles8051"
tags: ["architecture", "decision", "usb", "treehopper", "extension", "io", "hand-off"]
supersedes: ""
superseded_by: ""
---

# ADR-0039: Periphery.Treehopper — clean rebuild of the Treehopper SDK

## Context

[Treehopper](https://treehopper.io) is a USB I/O board (GPIO, I²C,
SPI, UART, PWM, ADC) with a high-level C# SDK at
[treehopper-electronics/treehopper-sdk](https://github.com/treehopper-electronics/treehopper-sdk).
The original .NET SDK was written several years ago and has accumulated
the kinds of issues a long-tail SDK does: lifecycle is brittle, async
hygiene is poor, there's no `CancellationToken` anywhere, and many
hardware-mutation operations happen as side effects of property
setters.

A maintainer handed the project off recently. The existing C#
implementation works — but only barely — and is a poor base to build
on. The wire protocol is small (17 command bytes, two bulk endpoints
in each direction) and stable; the value of the SDK is the
ergonomics on top of it, and those ergonomics are exactly what's
broken.

A condensed audit (full version in
[`docs/plans/periphery-treehopper.md`](../plans/periphery-treehopper.md)):

- **Property setters do USB I/O.** `Pin.Mode`, `Pin.DigitalValue`,
  `Led`, `I2c.Enabled`, `I2c.Speed`, `Spi.Enabled` all start an async
  USB transfer when assigned.
- **A global `Settings.PropertyWritesReturnImmediately` flag** flips
  the entire library between `Task.Run(...).Wait()` (sync-over-async
  deadlock hazard) and `.Forget()` (fire-and-forget, errors swallowed).
  Neither is correct for hardware control.
- **No `CancellationToken` anywhere.** Hung USB transfers are
  unkillable from managed code.
- **Constructor does I/O** (`Task.Run(OpenAsync).Wait()` in
  `LibUsbConnection`). Classic deadlock-on-UI-thread setup.
- **`Disconnect()` and `Dispose()` are sync; `ConnectAsync()` is
  async.** Asymmetric cleanup paths.
- **Three duplicate per-OS USB backends** (LibUsb / WinUsb / MacUsb,
  ~1500 lines of mostly-similar interop) — Periphery.Usb (ADR-0038)
  obsoletes the need for these.
- **`TreehopperUsb`** implements 5 interfaces (`INotifyPropertyChanged`,
  `IDisposable`, `IComparable`, `IEquatable<TreehopperUsb>`,
  `IEqualityComparer<TreehopperUsb>`) with inconsistent equality
  semantics across them.
- **`AwaitPinUpdateAsync()` races between callers** by replacing a
  shared `TaskCompletionSource`.
- **No reconnect.** Once a device is gone the `TreehopperUsb`
  instance is dead.

Periphery has the building blocks to fix all of this:

- Discovery via `Devices.Enumerate()` + USB filtering.
- Lifecycle via `DeviceSessionHost<TSession>`.
- A clean `UsbDeviceProxy` from ADR-0038 (Periphery.Usb) gives us
  bulk transfers + descriptor metadata without any per-OS backend
  code in this extension.
- Modern primitives (`IAsyncDisposable`, `CancellationToken`,
  `IAsyncEnumerable<T>`, channels) for the protocol layer.

## Decision

Build `Periphery.Treehopper` as a clean rebuild on top of
Periphery.Usb. Breaking API change vs. the original SDK.

Concretely:

1. **`TreehopperBoard`** replaces `TreehopperUsb`. Implements
   `IAsyncDisposable` only. Identity comes from `DeviceInfo` (no
   custom `Equals` / `GetHashCode` / `IComparable`).
2. **No property setter does I/O.** Every hardware mutation is an
   explicit `Async` method that takes a `CancellationToken`.
3. **No fire-and-forget. No sync-over-async. No global flag.** One
   uniform async story; awaits are honest about latency, errors
   propagate, cancellation is observable.
4. **Pins as leases.** `await board.Pins[7].ConfigureAsync(PinMode.PushPullOutput, ct)`
   returns an `IAsyncDisposable` `PinHandle`. Disposing the handle
   releases the pin. Peripheral leases enforce mutual exclusion on
   reserved pins.
5. **Peripherals as leases.** `await using var i2c = await board.UseI2cAsync(speedKhz, ct)`,
   `UseSpiAsync(...)`, `UseUartAsync(...)`, `UsePwmAsync(...)`,
   `UseOneWireAsync(...)`. Disposing releases the pins.
6. **Pin updates as `IAsyncEnumerable<PinUpdate>`** (channel-backed,
   like `CameraSession`). Multiple consumers, no shared TCS race.
7. **Reconnect via `DeviceSessionHost<TreehopperBoard>`** — same
   pattern as Periphery.Camera and other Tier 1 extensions.

The wire protocol (`DeviceCommands.cs` packet shapes) is preserved
unchanged for firmware compatibility. The byte-level codec lives in
an internal `TreehopperWireProtocol` class testable against a fake
`IUsbBulkChannel`.

```csharp
// One-shot
await using var board = await TreehopperBoard.OpenAsync(deviceInfo, ct);
await using var led = await board.Pins[7].ConfigureAsync(PinMode.PushPullOutput, ct);
await led.WriteAsync(true, ct);

// Streaming pin updates
await foreach (var update in board.PinUpdates.WithCancellation(ct))
    Console.WriteLine($"pin {update.PinIndex} → {update.DigitalValue}");

// Peripheral lease
await using (var i2c = await board.UseI2cAsync(speedKhz: 400, ct))
{
    var data = await i2c.SendReceiveAsync(0x17, new byte[] { 0x31 }, readLen: 2, ct);
}

// Reconnect
await using var host = await DeviceSessionHost<TreehopperBoard>.StartAsync(
    new DeviceProfile(f => f.WithUsbId(TreehopperBoard.Vid, TreehopperBoard.Pid),
                      "Treehopper"),
    TreehopperBoard.OpenAsync, ct: ct);
```

Full API sketch and migration story in
[`docs/plans/periphery-treehopper.md`](../plans/periphery-treehopper.md).

## Rationale

- **The wire protocol is correct and stable.** Firmware updates from
  upstream are infrequent and additive. Reusing the protocol bytes is
  free; reusing the C# layer on top of it costs us a worse SDK.
- **The pain points are real and user-facing.** Property-as-I/O,
  fire-and-forget, sync-over-async, missing cancellation — these are
  the kind of issues that make people stop using a library after one
  weekend project.
- **The replacement effort is tractable.** The wire protocol is
  small; per-pin and per-peripheral classes are mostly thin packet
  encoders; the heavy lifting (USB transfers, reconnect, discovery)
  is borrowed from Periphery.Usb + core.
- **Validates Periphery.Usb in production.** A real-world consumer
  shakes out the foundation extension's rough edges (descriptor
  enrichment gaps, driver-binding edge cases, async transfer
  cancellation) on a known-shape device.
- **Establishes a layered template** — Periphery core (discovery
  + topology) → Periphery.Usb (claim + transfers via Periphery-
  native API) → Periphery.Treehopper (wire protocol + ergonomics).
  Worth establishing as a template for future custom-USB
  extensions (Saleae, J-Link, Bus Pirate, etc.).

## Alternatives considered

- **Keep using the existing SDK and wrap it with Periphery for
  discovery only.** Rejected: doesn't fix any of the user-facing
  bad practices. The original SDK's lifecycle is brittle in ways
  that Periphery's reconnect machinery can't paper over from the
  outside.
- **Fork the existing SDK and patch in place.** Rejected: too many
  cross-cutting concerns (the global `Settings` flag, the
  `INotifyPropertyChanged`-everywhere approach, the per-OS USB
  backends) to fix incrementally without effectively rewriting it.
- **Stay close to the original API for source-compatibility.**
  Rejected: the API shape is the source of the bad practices.
  Preserving the shape preserves the problems. We accept a breaking
  change in exchange for a clean foundation; existing user code
  ports in a few lines per operation.
- **Build only Periphery.Usb and leave Treehopper to a third party.**
  Possible — but the existing SDK has no clear maintainer trajectory
  and someone needs to do this work. We learn the most about the
  hand-off pattern by being that someone.

## Consequences

### What we gain

- A working, modern Treehopper SDK on .NET 8/10 with proper async,
  cancellation, reconnect, and `IAsyncEnumerable` streams.
- Production validation of Periphery.Usb (ADR-0038).
- A reference implementation of "discovery + USB hand-off + protocol
  layer" — the template for `Periphery.Saleae`, `Periphery.JLink`,
  and other custom-USB extensions.
- An honest async story for users who want to use Treehopper inside
  larger applications without fighting the SDK's lifecycle.

### What we accept

- **Breaking API change vs. the existing SDK.** Mitigated by a
  migration guide; for trivial scripts the new shape is three lines
  longer than the old; for non-trivial code it's much shorter and
  more correct.
- **`Treehopper.Libraries` (the catalog of pre-built peripheral
  drivers — port expanders, displays, sensors) is not ported in v1.**
  v2 work; not on the critical path. v1 supports the raw I²C / SPI /
  GPIO surface those libraries need.
- **Naming.** Treehopper is a third-party trademark on a hardware
  product. `Periphery.Treehopper` is fine for an unaffiliated
  open-source rebuild but warrants a courtesy heads-up to the
  upstream maintainers near v1 release.
- **Firmware version skew.** Original SDK warns at runtime if
  firmware is below `MinimumSupportedFirmwareVersion = 111`. New
  library makes this an explicit `TreehopperBoard.OpenAsync`
  failure (typed exception) rather than a `Debug.WriteLine`. Some
  users with old firmware will see a hard failure where the old SDK
  silently misbehaved — net win.

### What we constrain

- **Wire protocol stays compatible** with current Treehopper firmware.
  Any wire-level departures need their own ADR.
- **Public API uses Periphery primitives** (`DeviceInfo`,
  `DeviceSessionHost`, `IAsyncDisposable`, `CancellationToken`) end
  to end. No `INotifyPropertyChanged`, no `Boards.CollectionChanged`,
  no global singletons.

## Affected files (planned)

- `src/Periphery.Treehopper/Periphery.Treehopper.csproj`
- `src/Periphery.Treehopper/TreehopperBoard.cs` — coordinator
- `src/Periphery.Treehopper/Pin.cs`, `PinHandle.cs`, `PinMode.cs`,
  `PinUpdate.cs`
- `src/Periphery.Treehopper/I2cLease.cs`, `SpiLease.cs`,
  `UartLease.cs`, `PwmLease.cs`, `OneWireLease.cs`
- `src/Periphery.Treehopper/Internal/TreehopperWireProtocol.cs` —
  packet codec, testable against a fake `IUsbBulkChannel`
- `examples/Periphery.Treehopper.Example/` — list / blink / I²C
  scan / SPI loopback / reconnect demo
- `tests/Periphery.Treehopper.Tests/` — wire protocol round-trip,
  pin lease enforcement, peripheral concurrency

## Implementation order

See [`docs/plans/periphery-treehopper.md`](../plans/periphery-treehopper.md)
for the full schedule. Steps 2–5 of that plan; ~3–5 weeks after
ADR-0038's Periphery.Usb is in place.

## Migration story

Summarized in the plan doc; a one-line sketch of the trivial case:

```csharp
// before — original Treehopper SDK
var board = await ConnectionService.Instance.GetFirstDeviceAsync();
await board.ConnectAsync();
board.Pins[7].Mode = PinMode.PushPullOutput;
board.Pins[7].DigitalValue = true;

// after — Periphery.Treehopper
var info = await Devices.Enumerate()
    .OfCategory(DeviceCategory.UsbDevice)
    .WithUsbId(TreehopperBoard.Vid, TreehopperBoard.Pid)
    .FirstAsync();
await using var board = await TreehopperBoard.OpenAsync(info);
await using var pin = await board.Pins[7].ConfigureAsync(PinMode.PushPullOutput);
await pin.WriteAsync(true);
```

For non-trivial code (multiple pins, peripherals, reconnect,
cancellation) the new shape is shorter than the original.

## Open questions

See plan doc § "Open questions / risks":

1. Concurrency between peripheral leases and the pin-report stream
   — start with a board-wide `SemaphoreSlim`, profile, split if
   needed.
2. Windows driver-binding ergonomics — Treehopper ships a `.inf`
   that binds to WinUSB; ADR-0038's WinUSB backend should light up
   for it without a Zadig step.
3. `Treehopper.Libraries` port — v2.
4. Naming / upstream coordination.

## Testing

Three tiers of integration testing, each catching what the one below
misses:

- **Lifecycle / reconnect** — real Treehopper boards plus an
  `ILifecycleHarness` abstraction that disconnects/reconnects them
  without touching the cable. See
  [`docs/patterns/usb-lifecycle-testing.md`](../patterns/usb-lifecycle-testing.md).
  First harness impl: Linux `authorized` sysfs toggle, which
  produces real kernel-level USB add/remove events indistinguishable
  from a physical unplug. Runs on the per-PR Linux runner.
- **Wire-level (stretch)** — Saleae Logic 2 (or sigrok-compatible
  alternative) probing the data lines, with an `IBusVerifier`
  abstraction so tests assert against decoded I²C / SPI / UART
  frames. See
  [`docs/patterns/wire-level-testing.md`](../patterns/wire-level-testing.md).
  Catches protocol-encoding, mode-bit, and clock-divider bugs that
  lifecycle testing can't reach. Self-hosted runner with a wired
  rig; nightly / on-demand, not per-PR.
- **Gadget (deferred)** — Linux gadgetfs + `dummy_hcd` running a
  userspace process that emulates Treehopper firmware. Unlocks
  error injection, firmware-version skew, and multi-version
  regression testing. Deferred to v1.x or v2 unless coverage gaps
  cost material time.

## Related ADRs

- [ADR-0035 — Periphery.Camera](0035-periphery-camera.md) — the
  hand-off principle and `DeviceSessionHost` integration template.
- [ADR-0038 — Periphery.Usb](0038-periphery-usb.md) — foundation
  this extension sits on.
