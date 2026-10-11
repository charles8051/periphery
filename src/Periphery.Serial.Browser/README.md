# Periphery.Serial.Browser

Web Serial for a Blazor WebAssembly page, as an `IDuplexPipe`. The user picks a port in the browser's
chooser, and the page reads and writes it through `System.IO.Pipelines`, the same shape
[Periphery](https://github.com/charles8051/periphery)'s protocol code takes on the desktop. It needs
neither `System.IO.Ports` nor the rest of Periphery.

The design is
[ADR-0092](https://github.com/charles8051/periphery/blob/main/docs/adr/0092-browser-devices-come-from-grants.md).

## Use

```csharp
// Program.cs, before the app runs
await BrowserSerial.InitializeAsync();
```

```razor
<button @onclick="ConnectAsync">Connect</button>

@code {
    async Task ConnectAsync()
    {
        // From the click handler: the browser shows its chooser only during a user gesture.
        var port = await BrowserSerial.RequestPortAsync([new SerialPortRequestFilter { UsbVendorId = 0x10C4 }]);
        if (port is null)
            return; // dismissed

        await port.OpenAsync(new SerialPortOpenOptions { BaudRate = 115200, Parity = SerialPortParity.Even });

        // Optional: drive DTR and RTS, for example into an STM32's BOOT0 and NRST.
        await port.SetSignalsAsync(dataTerminalReady: true, requestToSend: true);

        // Anything that takes an IDuplexPipe, such as Stm32SerialProgrammer.ConnectAsync.
        var pipe = port.Pipe;
    }
}
```

## Requirements

A Chromium-based browser, and a page served over HTTPS or from `localhost`.

## Behaviour

- A port has no id that survives a replug. Hold the `BrowserSerialPort` for as long as it is in use.
- `Pipe.Input` completes when the port closes, and faults with `IOException` when the browser loses
  the port. A failed write surfaces as `IOException` on the next write to `Pipe.Output`.
- A parity, framing, break or overrun error loses its bytes, and reading goes on.
- `DisposeAsync` stops sending, closes the port, and completes `Pipe.Input`. Bytes written to
  `Pipe.Output` and not yet sent are dropped.
