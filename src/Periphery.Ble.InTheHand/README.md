# Periphery.Ble.InTheHand

Open a GATT session on a Bluetooth LE device you found with Periphery, through
[32feet](https://github.com/inthehand/32feet)'s `InTheHand.BluetoothLE`.

```sh
dotnet add package Periphery.Ble.InTheHand --prerelease
```

Windows only: target `net10.0-windows10.0.19041.0` or later. Restore refuses any other target
(NU1202). 32feet's only Windows GATT asset needs 10.0.19041, and the join below needs a Windows
instance id. Linux has no address to join on yet (issue #258).

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
node. It resolves the device by the address in the node's instance id. The address is a join key,
not an identity: a peripheral that uses private addresses comes back under a new node and address
after it is re-paired.
