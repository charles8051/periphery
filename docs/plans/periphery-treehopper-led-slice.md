# Vertical slice — `Periphery.Treehopper` on-board LED blink

> **Status:** delivered. The slice landed and is runnable as
> [`examples/Periphery.Examples.TreehopperLed`](../../examples/Periphery.Examples.TreehopperLed).
> The subsystem has since been restructured around a pure core
> ([ADR-0052](../adr/0052-periphery-treehopper-pure-core.md)), so the internals no
> longer match this plan even where the ergonomics do.

A first, end-to-end increment of [ADR-0039](../adr/0039-periphery-treehopper.md):
a `Periphery.Treehopper` extension on top of `Periphery.Usb` that can open a
Treehopper and toggle its on-board LED with an ergonomic, async-first API.

## Target ergonomics

```csharp
// One-shot
var info = await Devices.Enumerate()
    .WithUsbId(TreehopperBoard.Vid, TreehopperBoard.Pid)
    .FirstOrDefaultAsync();
await using var board = await TreehopperBoard.OpenAsync(info);
await board.SetLedAsync(true);     // on-board LED on
await board.SetLedAsync(false);    // off

// Reconnect-aware (same DeviceSessionHost path Hid/Camera use — no ObservableCollection)
await using var host = await DeviceSessionHost<TreehopperBoard>.StartAsync(
    new DeviceProfile(f => f.WithUsbId(TreehopperBoard.Vid, TreehopperBoard.Pid), "Treehopper"),
    TreehopperBoard.OpenAsync);
var board2 = await host.WaitForSessionAsync();
await board2.SetLedAsync(true);
```

## Why this is low-risk: the mechanism is already proven live

The raw path is validated on real hardware by the Periphery.Usb spike
(`examples/Periphery.Examples.Usb blink`):

> discovery (`Devices.Enumerate().WithUsbId`) → `UsbDevice.OpenAsync` (overlapped
> WinUSB) → `BulkWriteAsync(0x02, [0x0E, on])` → **physical LED blink**.

So this slice is pure ergonomics + protocol encapsulation + tests over a working
path. No unknown hardware or interop risk remains — it's a wrapper exercise.

## Wire protocol (preserved verbatim from the existing SDK)

ADR-0039 keeps the protocol bytes unchanged for firmware compatibility.

| Item | Value |
|------|-------|
| VID / PID | `0x10C4` / `0x8A7E` |
| Peripheral-config OUT endpoint | `0x02` |
| (pin-config OUT / pin-report IN / peripheral-response IN) | `0x01` / `0x81` / `0x82` |
| `DeviceCommands.ConfigureDevice` | `0x01` |
| `DeviceCommands.LedConfig` | `0x0E` (14) |
| Init packet (high-impedance pins) | `[0x01, 0x00]` → ep `0x02` |
| LED packet | `[0x0E, on ? 1 : 0]` → ep `0x02` |

## API surface (slice only)

```csharp
public sealed class TreehopperBoard : IAsyncDisposable
{
    public const ushort Vid = 0x10C4;
    public const ushort Pid = 0x8A7E;

    public DeviceInfo DeviceInfo { get; }

    public Task SetLedAsync(bool on, CancellationToken ct = default);

    // createSession-shaped: drops straight into DeviceSessionHost<TreehopperBoard>.StartAsync
    public static Task<TreehopperBoard> OpenAsync(DeviceInfo deviceInfo, CancellationToken ct = default);

    public ValueTask DisposeAsync();
}

// Pure byte codec — no I/O, unit-testable in isolation (ADR-0039's
// "TreehopperWireProtocol testable against a fake IUsbBackend").
internal static class TreehopperWireProtocol
{
    public const byte PeripheralConfigEndpoint = 0x02;
    public enum DeviceCommand : byte { ConfigureDevice = 0x01, LedConfig = 0x0E, Reboot = 0x0C }
    public static byte[] ConfigureDevice() => [ (byte)DeviceCommand.ConfigureDevice, 0x00 ];
    public static byte[] Led(bool on)      => [ (byte)DeviceCommand.LedConfig, (byte)(on ? 1 : 0) ];
}
```

Behaviour:
- `OpenAsync` → `UsbDevice.OpenAsync(info, ct)`, then send `ConfigureDevice()` to
  `PeripheralConfigEndpoint` (init), then construct the board over the `UsbDevice`.
- `SetLedAsync(on)` → `_usb.BulkWriteAsync(PeripheralConfigEndpoint, Led(on), ct)`.
- `DisposeAsync` → disposes the wrapped `UsbDevice`.

## Build steps

1. **`src/Periphery.Treehopper/Periphery.Treehopper.csproj`** — clone the
   `Periphery.Usb.csproj` conventions; `ProjectReference` → `Periphery` +
   `Periphery.Usb`; `PackageId` `Periphery.Treehopper`; `InternalsVisibleTo
   Periphery.Treehopper.Tests`.
2. **`TreehopperBoard.cs`** — Layer-1 board (wraps `UsbDevice`;
   `OpenAsync`/`SetLedAsync`/`DisposeAsync`; `Vid`/`Pid`).
3. **`Internal/TreehopperWireProtocol.cs`** — `DeviceCommand` subset + packet
   builders + endpoint const.
4. **`TreehopperException.cs`** — thin `: System.IO.IOException` (mirror
   `UsbException`); thrown when a board op fails.
5. *(optional, natural next increment)* **`TreehopperBoardProxy.cs`** —
   reconnect handle, mirrors `UsbDeviceProxy` (`: DeviceProxyBase<TreehopperBoard,
   TreehopperException>`).
6. **`examples/Periphery.Examples.Treehopper/`** — `blink` via
   `board.SetLedAsync(...)` (the ergonomic "after" of the raw Usb example).
7. **`tests/Periphery.Treehopper.Tests/`** — see test strategy.
8. Register all three projects in `Periphery.slnx`.
9. Build `net8.0`+`net10.0`; run tests; run the example `blink` against the
   board (the raw bytes are already proven, so this just confirms the wrapper).

## Test strategy

- **Pure-codec tests** on `TreehopperWireProtocol` — deterministic bytes
  (`Led(true) == [0x0E, 0x01]`, `Led(false) == [0x0E, 0x00]`,
  `ConfigureDevice() == [0x01, 0x00]`). No hardware, no `UsbDevice`. This is the
  bulk of the coverage and the ADR's intended codec test.
- **Board-over-fake-transport (optional):** to exercise `SetLedAsync` end-to-end
  without hardware, reuse the spike's `TestUsbBackend` + `UsbDevice.CreateForTest`.
  Requires `Periphery.Usb` to add `InternalsVisibleTo("Periphery.Treehopper.Tests")`
  (one line) **or** a small seam letting `TreehopperBoard` accept an injected
  `UsbDevice`. Reusing `CreateForTest` is the least new surface; assert the fake
  backend saw a write of `[0x0E, 1]` to endpoint `0x02`.
- **Hardware smoke:** example `blink` (identical bytes to the already-passing raw
  demo).

## Deliberately out of slice

The rest of ADR-0039 builds on the same foundation afterward: pins-as-leases,
I2C/SPI/UART/PWM peripheral leases, the `IAsyncEnumerable` pin-report stream
(now feasible via `UsbDevice.ReadBulkStreamAsync`), full descriptor/config
parsing, multi-interface, and the libusb Linux/macOS backend. The **firmware
reboot-on-connect dance is *not* needed here** — that's only for the SPI / LED-
strip path; the on-board LED is the cleanest possible first peripheral, which is
exactly why it's the right slice.

## Notes

- Reconnect uses `DeviceSessionHost<TreehopperBoard>` exactly as Hid/Camera do —
  no hand-rolled `ObservableCollection`, which is the architectural fix from the
  Treehopper-SDK reentrancy crash that motivated this whole direction.
- Keep the `ConfigureDevice` init for parity with the existing SDK; the live
  blink works with it in place.
