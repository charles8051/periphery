# Periphery.Usb.Browser

WebUSB for [Periphery.Usb](https://github.com/charles8051/periphery) in a Blazor WebAssembly page. The
user picks a device in the browser's chooser, and the page drives it with the same `UsbDevice` it uses
on the desktop.

A page sees only the devices the user has granted it. It cannot enumerate the machine's devices, so
`Devices.*` throws `PlatformNotSupportedException` in a browser. The design is
[ADR-0092](https://github.com/charles8051/periphery/blob/main/docs/adr/0092-browser-devices-come-from-grants.md).

## Use

```csharp
// Program.cs, before the app runs
await BrowserUsb.InitializeAsync();
```

```razor
<button @onclick="PickAsync">Connect</button>

@code {
    async Task PickAsync()
    {
        // From the click handler: the browser shows its chooser only during a user gesture.
        var device = await BrowserUsb.RequestDeviceAsync(
            [new UsbDeviceRequestFilter { VendorId = new HardwareId(0x10C4), ProductId = new HardwareId(0x8A7E) }]);
        if (device is null)
            return; // dismissed

        await using var usb = await UsbDevice.OpenAsync(device);
        // control and bulk transfers, as on the desktop
    }
}
```

A reloaded page finds devices granted earlier with `BrowserUsb.GetGrantedDevicesAsync()`, without a
gesture.

## Requirements

- A Chromium-based browser, and a page served over HTTPS or from `localhost`.
- On Windows, the device's driver must be WinUSB. On Linux, the user needs a udev rule for it.
- The device's interface 0 must not be a class WebUSB blocks: audio, HID, mass storage, smart card,
  video, audio/video or wireless controller.

## Identity

A device with a serial number gets the id `webusb:VVVV:PPPP:<serial>`, which survives a replug, a
reload and a browser restart while the grant lasts. A device without one, or two attached devices
sharing one, gets `webusb-session:VVVV:PPPP:<n>`, which lasts until it is unplugged.

## Cancellation

WebUSB cannot cancel a single transfer. A cancelled or timed-out call returns at once and leaves its
transfer running. The next read on that endpoint takes the transfer over, and the next write or
control transfer waits for it to finish. A write that timed out may still reach the device.
