# A GATT client API owned by Periphery: how far it reaches

**Date:** 2026-10-02
**Status:** Exploration. Nothing here is a decision. It feeds issue #319 and any ADR that would
supersede ADR-0085 D7, which keeps GATT in 32feet and out of Periphery.
**Question:** What would a full GATT client API of Periphery's own cover on Windows, Linux and
macOS, and where does it get hard?
**Method:** Measured: BlueZ 5.72's GATT interfaces, introspected over a live link between the Linux
rig's two `btvirt` controllers; 32feet 4.0.45's public surface, by reflection; 32feet's connect and
disconnect behaviour on the [BLE bench](../patterns/ble-bench-testing.md) (#318); CoreBluetooth on
macOS 26.4.1 against an LE HID mouse (#320); the CoreBluetooth delegate protocols, counted in the
macOS 26.4 SDK headers. Documented: the WinRT `Windows.Devices.Bluetooth.GenericAttributeProfile`
reference and Apple's CoreBluetooth reference. Evidence labels as in the
[OS APIs exploration](bluetooth-os-apis-2026-09.md).
**Scope:** a GATT client talking to a peripheral the OS already knows. Out of scope: the GATT
server (peripheral) role, scanning for and connecting to unbonded devices, L2CAP channels, LE
Audio and mesh.

---

## Where things stand

ADR-0085 D7 keeps GATT in 32feet. Periphery finds the device, joins it to 32feet's
`BluetoothDevice`, and keeps a session open with `BleDeviceProxy` (#318). That works on Windows and
Linux. macOS has no path (#319).

32feet 4.0.45's public surface follows the Web Bluetooth API (Measured):

| Concern | 32feet |
|---|---|
| Connect | `ConnectAsync`, `Disconnect`, `IsConnected`, `AutoConnect`, `GattServerDisconnected` |
| Discover | primary and included services, characteristics and descriptors, all or by UUID |
| Read, write | `ReadValueAsync`; `WriteValueWithResponseAsync`; `WriteValueWithoutResponseAsync`; descriptor read and write |
| Notify, indicate | `StartNotificationsAsync`, `StopNotificationsAsync`, `CharacteristicValueChanged` |
| Link | `Mtu`, `RequestMtuAsync`, `PreferredPhy`, `ReadRssi` |
| Absent | a `CancellationToken` on any GATT call; cache control; reliable writes; typed ATT errors; a services-changed event; a build for plain .NET on macOS |

A caller already works around three of its behaviours (Measured, #318). On Windows, `ConnectAsync`
to a silent peripheral returns after about 23 s without throwing, unconnected.
`GattServerDisconnected` also fires for the caller's own `Disconnect()`. On Linux, `Disconnect()` is
BlueZ `Device1.Disconnect()`, which drops the link for every session.

---

## What a full API covers, per platform

| Concern | Windows (WinRT) | Linux (BlueZ over D-Bus) | macOS (CoreBluetooth) |
|---|---|---|---|
| Connection | Implicit: the first GATT access connects. `GattSession.MaintainConnection` holds the link. A session's disposal ends only that session's hold (Documented). | Explicit `Device1.Connect` and `Disconnect`. A disconnect drops the link for every client (Measured). | Explicit `connectPeripheral` and `cancelPeripheralConnection`. The link stays while another app holds it (Documented). |
| Discovery and cache | Per call, `BluetoothCacheMode.Cached` or `Uncached`. `GattServicesChanged` on the device (Documented). | Automatic on connect. `Device1.ServicesResolved` says when it is done (Measured). Objects come and go through `InterfacesAdded` and `InterfacesRemoved`. bluetoothd caches the database per bond, readable only by root. | Explicit and stepwise: services, then characteristics, then descriptors, each with its own callback. `didModifyServices` invalidates (Documented). |
| Read | `ReadValueAsync(cacheMode)` returns a status and an ATT error byte (Documented). | `ReadValue(a{sv})` with an `offset` option (Measured). | `readValue`; the value arrives in `didUpdateValueForCharacteristic`, the same callback as a notification (Documented). |
| Write | `WriteValueWithResultAsync`, with or without response; `GattReliableWriteTransaction` (Documented). | `WriteValue(ay, a{sv})` with `type` `command`, `request` or `reliable`, and `offset` (Measured signature); `AcquireWrite` returns a socket (Measured). | With or without response. Without response is flow-controlled through `canSendWriteWithoutResponse` and `peripheralIsReadyToSendWriteWithoutResponse`. No reliable-write API (Documented). |
| Notify, indicate | Write the CCCD, then `ValueChanged` (Documented). | `StartNotify` and `StopNotify`, then `PropertiesChanged` on `Value`; or `AcquireNotify`, which returns a socket (Measured). | `setNotifyValue`, then `didUpdateValueForCharacteristic` (Documented). |
| MTU | `GattSession.MaxPduSize`, `MaxPduSizeChanged` (Documented). | `MTU` on each characteristic: 517 between the rig's controllers (Measured). | `maximumWriteValueLength(for:)` only (Documented). |
| Errors | `GattCommunicationStatus` (`Success`, `Unreachable`, `ProtocolError`, `AccessDenied`) plus the ATT byte (Documented). | `org.bluez.Error.*` names, with ATT detail in the message text (Documented). | `NSError` in `CBATTErrorDomain` or `CBErrorDomain` (Documented). |
| Security | An encrypted characteristic triggers the OS pairing flow or fails (Documented). | Pairing needs an `Agent1` registered on the bus (Documented). | A system prompt; no API (Documented). |
| OS-claimed services | `0x1812` listed, refused (Measured). | `0x1812` absent before 5.80, read-only after (Source). | `0x1812` not listed (Measured). |
| Delivery | Async operations completing on the thread pool. | Signals read from one socket. | Delegate callbacks on a dispatch queue the client names. |
| From .NET | The WinRT projection, so a `net10.0-windows10.0.19041.0` target, as `Periphery.Ble.InTheHand` already has. | Periphery's managed D-Bus client (ADR-0091 D2). It makes calls and reads signals. It does not negotiate `UNIX_FDS` (Source: its codec ignores the field), so `AcquireNotify` and `AcquireWrite` are unreachable. It cannot serve calls, so it cannot register an `Agent1`. | No binding without the macOS workload. Objective-C runtime P/Invoke, as ADR-0093 uses for IOBluetooth, plus delegate classes defined at run time: `objc_allocateClassPair`, `class_addMethod`, and `[UnmanagedCallersOnly]` trampolines, with manual retain and release. |

The parts that map one to one: UUIDs, characteristic property flags, bytes in and bytes out, and
discovery by UUID.

---

## Where it gets messy, worst first

1. **macOS.** Three problems stack. Plain .NET has no CoreBluetooth binding. The API is delegate
   based: `CBPeripheralDelegate` declares 14 callbacks and `CBCentralManagerDelegate` 9 (Measured:
   SDK headers), and a client needs about a dozen of them, each a function-pointer trampoline into
   managed code with its lifetime managed by hand. And the join from Periphery's bond to a
   CoreBluetooth peripheral exists only for a connected HID device (#319). TCC permission is decided
   per responsible process on top of that (ADR-0093).
2. **Connection semantics do not unify.** Disconnecting ends one session on Windows, the whole link
   on BlueZ (Measured), and this app's hold on macOS (Documented). On Windows, connecting can return
   unconnected (Measured), and access connects implicitly. A single `Connect` and `Disconnect`
   contract is wrong somewhere. `BleDeviceProxy` already absorbs this with a connected check and a
   lock over which session owns the link, and that took six review rounds (#318).
3. **Notifications.** Each platform delivers on a different thread, and CoreBluetooth gives reads
   and notifications the same callback. BlueZ's signal path carries every value as a D-Bus signal.
   Its low-overhead path, `AcquireNotify`, needs file-descriptor passing that Periphery's D-Bus
   client lacks. Throughput on either path is unmeasured.
4. **Pairing.** On BlueZ a characteristic that needs encryption needs an agent, which is an object
   Periphery would have to export over D-Bus. Windows and macOS show system UI that a library
   cannot drive.
5. **Cache invalidation.** A services change leaves stale handles on all three platforms, and each
   reports it differently: `GattServicesChanged`, `InterfacesRemoved` and `InterfacesAdded`, or
   `didModifyServices`.
6. **Errors.** Three taxonomies have to become one exception type, without losing the ATT code.
7. **Testing.** Each platform needs a GATT server the tests control. Windows has the bench DK, whose
   Zephyr image can gain a test service. Linux has `btvirt`, where a D-Bus GATT application
   registered with hci1's bluetoothd would serve any test service. macOS needs a peripheral next to
   the Mac. A pure core covers less here than elsewhere in Periphery, because the OS stacks run ATT
   themselves. What remains pure is UUIDs, property flags, error classification, subscription
   bookkeeping, and the rule for who owns the link.

---

## Three shapes

| | A: keep 32feet | B: Periphery's API over 32feet, own CoreBluetooth | C: Periphery's API, three native backends |
|---|---|---|---|
| ADR-0085 D7 | stands | superseded | superseded |
| macOS | Mac Catalyst apps only, if a target is added (#319) | plain .NET | plain .NET |
| Cancellation, cache control, typed errors, reliable writes | no | not on Windows or Linux, which stay bounded by 32feet | yes |
| Third-party GATT dependency | 32feet | 32feet on Windows and Linux | none |
| New code | the Catalyst join of #319 | a small abstraction matching 32feet's surface, a thin 32feet adapter, a CoreBluetooth backend | the abstraction, a WinRT backend, a BlueZ GATT backend, `UNIX_FDS` and object export in the D-Bus client, and a CoreBluetooth backend |
| Reuses | `Periphery.Ble.InTheHand` | that, plus ADR-0093's Objective-C interop | ADR-0091's D-Bus client, ADR-0093's interop, `BleDeviceProxy`'s lifecycle |

For scale: Linux bond enumeration and watching took about 2,200 lines (the D-Bus client 1,202, the
BlueZ leg 1,015). The IOBluetooth bond leg is 518, and `Periphery.Ble.InTheHand` is 394. Each GATT
backend covers more ground than either existing leg: connection, stepwise discovery, read, three
kinds of write, notification plumbing, and error mapping.

**Lean, if macOS GATT for plain .NET is the goal: B.** Keep the abstraction to what 32feet already
offers, so the 32feet adapter stays thin and the CoreBluetooth backend is the only new native code.
C pays off only if 32feet's gaps become requirements: cancellation, cache control, typed errors, or
an end to its Linux dependency chain (`Linux.Bluetooth` over `Tmds.DBus`, which carried a CVE until
4.0.45). A remains right if macOS can wait for 32feet to ship a macOS build.

---

## Measurements that would settle open points

| Question | What resolves it |
|---|---|
| Does a delegate class defined at run time from .NET receive CoreBluetooth callbacks on a named dispatch queue? | A spike on the MacBook: connect, discover and read through a runtime-defined `CBPeripheralDelegate`. |
| Can a peripheral that is not a HID device be joined to its bond on macOS? | #319: pair the bench DK with a Mac. |
| How many notifications per second survive BlueZ's signal path, against `AcquireNotify`? | A fast notifier in a D-Bus GATT application on hci1, read from hci0. |
| Does WinRT `WriteValueWithResultAsync` to a silent peripheral return `Unreachable` promptly? | The bench, with the DK halted. |

---

## Related

- [ADR-0085](../adr/0085-the-32feet-binding-is-two-integration-packages.md) D7: no GATT abstraction of Periphery's own.
- [ADR-0091](../adr/0091-linux-reads-bonded-bluetooth-devices-from-bluez.md): the managed D-Bus client.
- [ADR-0093](../adr/0093-macos-reads-bonded-bluetooth-devices-from-iobluetooth.md): Objective-C runtime interop and TCC.
- Issues #318 (`BleDeviceProxy`) and #319 (macOS GATT).
- [Bluetooth OS APIs exploration](bluetooth-os-apis-2026-09.md), GATT access.
