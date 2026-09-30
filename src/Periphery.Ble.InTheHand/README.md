# Periphery.Ble.InTheHand

Open a GATT session on a Bluetooth LE device you found with Periphery, through
[32feet](https://github.com/inthehand/32feet)'s `InTheHand.BluetoothLE`, on Windows and Linux.

```sh
dotnet add package Periphery.Ble.InTheHand --prerelease
```

| Platform | Target | Device to join |
|---|---|---|
| Windows | `net10.0-windows10.0.19041.0` or later | the LE link node, `BTHLE\DEV_<address>` |
| Linux | `net10.0` | the bond Periphery reads from BlueZ, `bluez:<adapter>/<device>` |

On Windows, an unversioned `net10.0-windows` target fails the build with a message: 32feet's only
Windows GATT asset needs 10.0.19041.

```csharp
using InTheHand.Bluetooth;
using Periphery;
using Periphery.Ble.InTheHand;

var node = await Devices.Enumerate()
    .OfCategory(DeviceCategory.Bluetooth)
    .WithName("Heart Rate Sensor")
    .WithBluetoothTransport(BluetoothTransport.LowEnergy)
    .FirstOrDefaultAsync()
    ?? throw new InvalidOperationException("That peripheral is not paired.");

var device = await node.ToBluetoothDeviceAsync()
    ?? throw new InvalidOperationException("32feet could not resolve it.");

await device.Gatt.ConnectAsync();
var service = await device.Gatt.GetPrimaryServiceAsync(BluetoothUuid.FromShortId(0x180D));
```

To keep a session open across disconnects and reboots, use `BleDeviceProxy`. It connects whenever
the peripheral is present, and again after the link drops:

```csharp
await using var proxy = await BleDeviceProxy.OpenAsync(new DeviceProfile(f => f
    .OfCategory(DeviceCategory.Bluetooth)
    .WithMacAddress(node.MacAddress!)
    .WithBluetoothTransport(BluetoothTransport.LowEnergy)));

proxy.DeviceOpened += async (_, session) =>
{
    var heartRate = await session.Gatt.GetPrimaryServiceAsync(BluetoothUuid.FromShortId(0x180D));
};
```

The proxy opens on presence, not activity: a peripheral that is not a HID device has no link until
a central connects. While the peripheral is out of range each attempt raises `OpenFailed`, and on
Windows one attempt takes about 23 s. A private-address peripheral that is re-paired comes back
under a new node on Windows, which a profile bound to the old address does not follow.

`WithBluetoothTransport` keeps peripherals known to support LE: on Windows the LE link node, and
on Linux a bond whose transport BlueZ reveals (see below).

`ToBluetoothDeviceAsync` resolves the device by its address and throws for any other node. The
address is a join key, not an identity: a peripheral that uses private addresses comes back under
a new node and address after it is re-paired.

On Linux:
- 32feet uses the first adapter BlueZ reports, so a bond on a second adapter resolves to `null`.
- BlueZ's `Device1` merges both bearers into one bond. Periphery reads the transport from what
  BlueZ does reveal: the per-bearer interfaces from BlueZ 5.84, else a random address, a cached
  GATT service, `Appearance` or `Class` (#302). Before BlueZ 5.84 these only show support, so a
  bond without `LowEnergy` may still speak LE. `WithBluetoothTransport` leaves such a bond out,
  and the join accepts any bond; a classic-only one fails at `Gatt.ConnectAsync()`.
- The package reaches BlueZ through 32feet, over `Linux.Bluetooth` and `Tmds.DBus`.
