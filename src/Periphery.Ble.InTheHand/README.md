# Periphery.Ble.InTheHand

Open a GATT session on a Bluetooth LE device you found with Periphery, through
[32feet](https://github.com/inthehand/32feet)'s `InTheHand.BluetoothLE`.

```sh
dotnet add package Periphery.Ble.InTheHand --prerelease
```

On Windows, target `net10.0-windows10.0.19041.0` or later. An unversioned `net10.0-windows` target
fails the build with a message saying so: 32feet's only Windows GATT asset needs 10.0.19041, and
without it NuGet would hand you the Linux provider, which throws at first call.

```csharp
using InTheHand.Bluetooth;
using Periphery;
using Periphery.Ble.InTheHand;

var node = await Devices.Enumerate()
    .OfCategory(DeviceCategory.Bluetooth)
    .WithName("Heart Rate Sensor")
    .Where(d => BluetoothAddress.TryParseInstanceId(d.Id.Value, out _, out var t)
                && t == BluetoothTransport.LowEnergy)
    .FirstOrDefaultAsync()
    ?? throw new InvalidOperationException("That peripheral is not paired.");

var device = await node.ToBluetoothDeviceAsync()
    ?? throw new InvalidOperationException("32feet could not resolve it.");

await device.Gatt.ConnectAsync();
var service = await device.Gatt.GetPrimaryServiceAsync(BluetoothUuid.FromShortId(0x180D));
```

`ToBluetoothDeviceAsync` takes an LE link node, `BTHLE\DEV_<address>`, and throws for any other
node. It resolves the device by the address in the node's instance id, which only Windows carries.
The address is a join key, not an identity: a peripheral that uses private addresses comes back
under a new node and address after it is re-paired.

## Vulnerable dependency on the bare target

On `net10.0`, 32feet brings `Linux.Bluetooth` and `Tmds.DBus` 0.20.0, which has a high-severity
advisory ([CVE-2026-39959](https://github.com/advisories/GHSA-xrw6-gwf8-vvr9)). Restore reports it
as NU1903. The exposure follows your target framework, not this package: a
`net10.0-windows10.0.19041.0` build does not include either package. On Linux, the bare target is
the only one, so the exposure cannot be avoided there until 32feet takes Tmds.DBus 0.92.0.
