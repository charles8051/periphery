# Periphery.Treehopper

Drive a [Treehopper](https://treehopper.io) USB I/O board: 20 pins of GPIO, analog input, PWM, I²C, SPI, UART, 1-Wire and an 8080-style parallel bus. Built on `Periphery.Usb`.

```sh
dotnet add package Periphery.Treehopper --prerelease
```

```csharp
using Periphery.Treehopper;

await using var board = await TreehopperBoard.OpenFirstAsync();
await board.SetLedAsync(true);

await using var led = await board.Pins[5].ConfigureAsync(PinMode.PushPullOutput);
await led.WriteAsync(true);

await using var sensor = await board.Pins[0].ConfigureAsync(PinMode.AnalogInput);
Console.WriteLine($"{await sensor.ReadVoltageAsync():0.000} V");
```

`OpenFirstAsync` throws `TreehopperException` when no board is attached. To choose among several boards, call `TreehopperBoard.EnumerateAsync()` and pass the `DeviceInfo` you want to `OpenAsync`.

## Pins

`board.Pins[n].ConfigureAsync(mode)` returns a `PinHandle`. Disposing the handle returns the pin to a high-impedance digital input.

| Mode | Use |
|---|---|
| `DigitalInput` | `ReadDigitalAsync`, `WatchAsync` |
| `PushPullOutput` | `WriteAsync` |
| `OpenDrainOutput` | Set the level with `ReconcileAsync` (see below). `WriteAsync` switches the pin to push-pull. |
| `AnalogInput` | `ReadAnalogAsync` (0–4092), `ReadVoltageAsync` |

Pass an `AdcReferenceLevel` to `ConfigureAsync` to change an analog pin's reference. The default is the 3.3 V rail.

`Pin.ConfigureSoftPwmAsync(duty)` drives soft PWM at about 61 Hz on any pin.

Reads return the board's latest report. The firmware reports only when a monitored input changes. `WatchAsync` yields the current state and then one snapshot per change:

```csharp
await using var button = await board.Pins[3].ConfigureAsync(PinMode.DigitalInput);
await foreach (var snapshot in button.WatchAsync(ct))
    Console.WriteLine(snapshot.Digital ? "released" : "pressed");
```

`board.Reports` streams every pin at once. Each enumeration is an independent subscription.

## Peripherals

Each `Use*Async` call enables a module and returns a lease. Disposing the lease disables it.

```csharp
await using var i2c = await board.UseI2cAsync(speedKhz: 400);
var whoAmI = await i2c.WriteReadAsync(0x68, new byte[] { 0x75 }, 1);

await using var spi = await board.UseSpiAsync(clockMhz: 6, SpiMode.Mode00);
var rx = await spi.TransferAsync(new byte[] { 0x9F, 0, 0, 0 }, chipSelectPin: 4);
```

| Method | Lease | Operations |
|---|---|---|
| `UseI2cAsync` | `I2cLease` | `WriteAsync`, `ReadAsync`, `WriteReadAsync`, `PingAsync` |
| `UseSpiAsync` | `SpiLease` | `TransferAsync`, `WriteAsync`, `ReadAsync`, with per-transfer chip select, clock and mode |
| `UseUartAsync` | `UartLease` | `SendAsync`, `ReceiveAsync` |
| `UseOneWireAsync` | `OneWireLease` | `ResetAsync`, `SearchAsync`, `ResetAndMatchAsync`, `SendAsync`, `ReceiveAsync` |
| `UsePwmAsync` | `PwmLease` | `SetDutyCycleAsync`, `SetPulseWidthAsync`, `SetFrequencyAsync` on three hardware channels |
| `UseParallelAsync` | `ParallelLease` | `WriteCommandAsync`, `WriteDataAsync` on a 4–16 bit bus |

A failed I²C transfer throws `TreehopperI2cException`, which carries the address and an `I2cTransferError`.

SPI clocks between 0.8 and 6 MHz are rounded up to 6 MHz. The EFM8 has a silicon bug in that band.

## Board configuration

Every change above is an edit to an immutable `BoardConfig`. The board diffs the new config against the last one it applied and sends only the difference. `ReconcileAsync` applies several changes in one step:

```csharp
await board.ReconcileAsync(cfg => cfg with
{
    LedOn = true,
    I2c = new I2cConfig(SpeedKhz: 400),
    Pins = cfg.Pins.SetItem(5, new PinConfig(PinMode.PushPullOutput, DigitalValue: true)),
});
```

`ResyncAsync` resets the firmware and re-applies the whole current config.

## Identity and reset

| Method | Effect |
|---|---|
| `UpdateNameAsync`, `UpdateSerialAsync` | Write the USB name or serial to EEPROM. Each is limited to 61 UTF-8 bytes. Other apps see it after a reboot. |
| `RebootAsync` | Reboots the board. The handle is dead afterwards. |
| `RebootIntoBootloaderAsync` | Reboots into the EFM8 USB-HID bootloader. |
| `RescueResetAsync` | Resets the board over EP0 when the bulk endpoint has stopped. The static overload works without opening the board. Needs firmware with the rescue handler. |

`Version` and `VersionString` report the firmware version from the USB descriptor.

If a response is left unread on the board's response endpoint, `IsResponsePipeDesynced` latches true. Every later request on that handle throws `TreehopperDesyncException`. Reopen the board to recover.

## Diagnostics

Pass an `ILoggerFactory` to `OpenAsync(deviceInfo, ct, loggerFactory)` for open, close, transaction and report-producer logs. Metrics go to the `Periphery.Treehopper` and `Periphery.Usb` meters with or without a logger.

## Firmware

The EFM8 firmware sources are under [`firmware/Treehopper-EFM8`](https://github.com/charles8051/periphery/tree/main/src/Periphery.Treehopper/firmware/Treehopper-EFM8). They are built with Keil, outside the .NET build.

## Related packages

| Package | What it adds |
|---|---|
| [`Periphery.Treehopper.Firmware`](https://github.com/charles8051/periphery/tree/main/src/Periphery.Treehopper.Firmware) | Reflash a board's firmware through its bootloader |
| [`Periphery.Treehopper.Libraries`](https://github.com/charles8051/periphery/tree/main/src/Periphery.Treehopper.Libraries) | Device drivers, starting with APA102 LED strips |
| [`Periphery.Treehopper.Control`](https://github.com/charles8051/periphery/tree/main/src/Periphery.Treehopper.Control) | The core of the Treehopper control app, with CLI and Avalonia front-ends |
