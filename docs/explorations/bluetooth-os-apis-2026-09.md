# Bluetooth OS APIs on Windows, Linux and macOS

**Date:** 2026-09-13
**Status:** Exploration. Nothing here is a decision. The findings feed ADR-0085 and issue #232.
**Method:** Primary sources where they exist: the BlueZ D-Bus API documents and BlueZ source, the
Linux kernel source, the Windows SDK headers and Microsoft Learn, the 32feet source, and Apple's
developer documentation. Third-party reports fill the gaps and are labelled as such. Windows was
also measured, read-only, on one Windows 11 host with two bonded BR/EDR peripherals and two bonded
LE peripherals, through cfgmgr32 device properties and [`scratch/BleOsProbe`](../../scratch/BleOsProbe).
No Linux or macOS Bluetooth hardware was available.
**Scope:** device inventory, identity, liveness, GATT access, pairing, scanning, permissions and
caching. Classic profiles other than RFCOMM, LE Audio, mesh, and acting as a peripheral are out of
scope.

## Evidence labels

| Label | Meaning |
|---|---|
| Measured | Observed on hardware for this document. |
| Source | Read in the source code of the named component. |
| Documented | Stated in the vendor's own documentation. |
| Reported | Third-party reports. Not confirmed here. |
| Inferred | Follows from the above. Not observed. |

---

## Predicted issues

Ranked by how directly each one breaks something Periphery ships or has decided to build.

| # | Issue | Platforms | Evidence | Resolves with |
|---|---|---|---|---|
| 1 | `OfCategory(Bluetooth)` returns adapters and address-less link objects. It never returns a bonded device. | Linux | Source | Nothing in core. Bonds exist only in BlueZ, over D-Bus. |
| 2 | `OfCategory(Bluetooth)` matches `IOBluetoothDevice` registry objects. Those historically existed only for connected BR/EDR devices, and may not exist at all under the macOS 12 userspace stack. | macOS | Reported | `ioreg -r -l -c IOBluetoothDevice` on macOS 13+ with a connected BR/EDR device, then a connected LE device. |
| 3 | 32feet's `BluetoothDevice.Id` has a different format on each platform. On Windows it drops leading zeros. | All | Source, Measured | Parse to a number before comparing. |
| 4 | CoreBluetooth exposes no address for an LE peripheral. D5's `BluetoothAddress` cannot exist for LE on macOS. | macOS | Documented | Nothing. It is a platform privacy decision. |
| 5 | A GATT service the OS has claimed is refused on Windows, absent on BlueZ before 5.80, and read-only on BlueZ 5.80+. A service filter can match a device whose service no client can use. | All | Measured, Source | Document per platform. macOS is unverified. |
| 6 | ADR-0085 Context §1 says a 32feet poll is the only live Bluetooth signal on Windows. WinRT documents two push sources that were never measured. | Windows | Documented | A link toggle under an AEP `DeviceWatcher` and under `BluetoothLEDevice.ConnectionStatusChanged`. |
| 7 | Windows keys a privacy-enabled LE peripheral by the resolvable-private-form address it saw at pairing. This changes #232's expected result for the RPA column. | Windows | Measured | #232's rotation run, on Windows. |
| 8 | A BlueZ `Device1` object is not a bond. Discovery creates temporary objects that BlueZ removes after 30 s. `Bonded` exists only from BlueZ 5.65, and Ubuntu 22.04 ships 5.64. | Linux | Documented, Source | Select on `Paired`. Treat `Bonded` as optional. |
| 9 | Pairing has three shapes: an API on Windows, an agent on Linux, and no API on macOS. On macOS, 32feet's `IsPaired` is always `false`. | All | Documented, Source | No common surface. See [Pairing](#pairing). |
| 10 | TCC attributes a console process's CoreBluetooth use to the terminal. A binary built against the macOS 11+ SDK without `NSBluetoothAlwaysUsageDescription` is terminated on first use. | macOS | Reported | Run the CLI and an example on a Mac, from a terminal with and without Bluetooth permission. |
| 11 | Windows records LE address type and privacy in `DEVPKEY_Bluetooth_DeviceFlags`. Core can read both without WinRT. | Windows | Measured | Freshness across a re-pair is unmeasured. |
| 12 | Every WinRT Bluetooth Id embeds the local radio's address. The Microsoft stack runs one radio at a time. | Windows | Measured, Reported | Swap the adapter and compare Ids. |

---

## Inventory

### Windows

The cfgmgr32 provider already sees every bond. [ARCHITECTURE.md §10.6.2](../ARCHITECTURE.md)
measured the node shapes, which node carries bonding and connection, and which edges cfgmgr32
raises. That is not repeated here.

WinRT association endpoints (AEPs) are a second inventory over the same bonds.
`BluetoothLEDevice.GetDeviceSelectorFromPairingState(true)` and the `BluetoothDevice` equivalent
returned exactly the bonded devices the devnode tree held: two LE, two BR/EDR (Measured). The
`false` selector returns devices seen in advertising or inquiry that are not bonded (Documented).
Those devices have no devnode.

32feet's `Bluetooth.GetPairedDevicesAsync()` returned the two LE devices and neither BR/EDR device
(Measured). ADR-0085 Context §2 found the mirror image in the classic package.

### Linux

The kernel's `bluetooth` class registers two device types, `host` and `link` (Source:
`net/bluetooth/hci_sysfs.c`). A `host` is an adapter, `hci0`. A `link` is one live connection,
named `hci0:<handle>`. It is added when the link comes up and removed when the link goes down. The
file defines no attribute on a `link`, so sysfs carries neither the remote address nor the link type.

Periphery's Linux provider enumerates udev by subsystem and maps `bluetooth` to
`DeviceCategory.Bluetooth`. On Linux, then:

- a bonded, disconnected peripheral is absent;
- a connected peripheral appears as a `link` whose name changes with every connection handle, and
  which sysfs cannot join to an address;
- a Bluetooth HID peripheral also appears under `hid` and `input` while connected, with bus `0005`
  in `HID_ID` (Inferred from the kernel's bus constants and the provider's `HID_ID` parsing).

Bonds exist only in BlueZ, as `org.bluez.Device1` objects on the system D-Bus (Documented). A
`Device1` object does not mean a bond. Discovery creates one for every device seen, and BlueZ
removes one that is not paired, trusted or connected after `TemporaryTimeout`, 30 seconds by default
(Documented: `main.conf`).

`Device1` has `Paired` and, from BlueZ 5.65, `Bonded` (Source: BlueZ ChangeLog). `Paired` means
keys were exchanged. `Bonded` means they were stored. Ubuntu 22.04 ships BlueZ 5.64 and has no
`Bonded`. Ubuntu 24.04 ships 5.72 (Documented: packages.ubuntu.com).

ADR-0024 keeps D-Bus out of core. A bonded Bluetooth inventory on Linux is therefore reachable only
from an integration package. 32feet's Linux asset reaches BlueZ through `Linux.Bluetooth` and
`Tmds.DBus` (ADR-0085 Context §4).

### macOS

Periphery's macOS provider matches the IOKit class `IOBluetoothDevice`
([`MacOSCategoryMap.cs`](../../src/Periphery/MacOS/MacOSCategoryMap.cs)). ADR-0011 chose it over
CoreBluetooth to avoid a run loop and an app bundle.

`IOBluetoothDevice` names two different things. One is the Objective-C class in
IOBluetooth.framework. It represents any known remote device and offers `pairedDevices()`. The
other was a kernel registry object in IOBluetoothFamily. A registry path posted from macOS 10.14.6
places it at `IOBluetoothHCIController/AppleBroadcomBluetoothHostController/IOBluetoothDevice/IOBluetoothL2CAPChannel/IOBluetoothHIDDriver`
(Reported: hidapi issue 127). An object that parents L2CAP channels exists only while a link exists. Even on those
releases, `IOServiceMatching("IOBluetoothDevice")` could return only connected BR/EDR devices. It
never returned a bond, and never an LE device.

macOS 12 moved much of the stack into the userspace `bluetoothd`. The C function
`IOBluetoothRegisterForDeviceConnectNotifications` became a missing symbol on Monterey (Reported:
Apple Developer Forums thread 685545). Whether any registry object of class `IOBluetoothDevice`
exists on current macOS is unverified. If none does, `OfCategory(Bluetooth)` has returned nothing on
macOS since macOS 12. CI cannot settle this, because hosted macOS runners have no Bluetooth radio.

CoreBluetooth keeps no inventory of bonds. `retrievePeripherals(withIdentifiers:)` returns
peripherals the app already knows by identifier. `retrieveConnectedPeripherals(withServices:)`
returns peripherals connected to the system with matching services, "including those that other
apps have connected" (Documented). A disconnected bonded LE peripheral with an unknown identifier
cannot be found without a scan.

---

## Identity

| | Windows | Linux (BlueZ) | macOS |
|---|---|---|---|
| Remote address | Yes | Yes | BR/EDR through IOBluetooth only. None for LE. |
| Address type | `BluetoothLEDevice.BluetoothAddressType`, and `BDIF_LE_RANDOM_ADDRESS_TYPE` on the devnode | `Device1.AddressType` | None |
| Key for a bonded privacy peripheral | The address seen at pairing (Measured, one device) | The identity address (Documented) | A per-host UUID |
| 32feet `BluetoothDevice.Id` | `BluetoothAddress.ToString("X6")` | `Device1.Address`, as `AA:BB:CC:DD:EE:FF` | `CBPeripheral.Identifier` |

### Address type and privacy

An LE address is public or random. The top two bits of a random address give its kind: `11` is
static, `01` is resolvable private (RPA), `00` is non-resolvable. A peripheral using privacy
advertises a rotating RPA. A host holding the peripheral's IRK resolves each new RPA to the
peripheral's identity address, which is public or static random.

BlueZ documents its behaviour directly. `AddressType` "represents address type used for connection
and Identity Address after pairing" (Documented: `doc/org.bluez.Device.rst`).

Windows was measured. Both bonded LE devices report `BluetoothAddressType.Random`, and the devnode
flags agree:

| Device | Address top bits | LE bits in `DEVPKEY_Bluetooth_DeviceFlags` |
|---|---|---|
| LE HID mouse | `11`, static random | `LE_PAIRED LE_PERSONAL LE_RANDOM_ADDRESS_TYPE LE_NAME` |
| LE peripheral with privacy | `01`, resolvable private form | `LE_PAIRED LE_PERSONAL LE_PRIVACY_ENABLED LE_RANDOM_ADDRESS_TYPE LE_NAME LE_SC_PAIRED` |

Flag values come from `bthdef.h` in Windows SDK 10.0.26100. Both BR/EDR devnodes also set
`0x04000000`, which that header does not define.

For the privacy peripheral, `BluetoothLEDevice.BluetoothAddress`,
`System.Devices.Aep.DeviceAddress` and the `BTHLE\DEV_` instance ID all carry the same
resolvable-private-form address (Measured). Windows did not re-key the bond to an identity address.
It kept the address it saw at pairing.

This bears on #232 in two ways.

The RPA column's expected result was that the key does not hold. On Windows the key is not the
rotating address. It is fixed at pairing. The question becomes whether Windows resolves later RPAs
back to that devnode, which is what the IRK is for. This peripheral has not connected since it was
paired, so the host cannot show it either way.

The mouse's bond is static random with no privacy flag. If this is the mouse ADR-0083 measured, the
address change that ADR recorded came from the peripheral generating a new static address in
pairing mode, not from RPA rotation. The host does not record whether it is the same mouse.

### The 32feet Id

**Windows.** `GetId()` returns `NativeDevice.BluetoothAddress.ToString("X6")` (Source:
`Platforms/Windows/BluetoothDevice.windows.cs`). `X6` sets a minimum width, not a fixed one. On the
measurement host both Ids were twelve uppercase hex digits and ordinal-equal to
`BluetoothAddress.ToString("X12")` (Measured). Neither address starts with a zero nibble. An address
beginning `00:` produces a ten-digit Id, while D5's regex always captures twelve digits. A string
comparison between a D5 `BluetoothAddress` and a 32feet Id fails for every such address.

Public addresses often begin `00:`. Static random addresses and RPAs cannot, because their top bits
are `11` and `01`.

`FromIdAsync` parses its argument as hex, so a twelve-digit padded string is accepted (Source).
Both measured Ids round-tripped through `FromIdAsync` (Measured).

**Linux.** `Init()` sets the Id from `Device1.Address`, the colon-separated form (Source).
`FromIdAsync` passes it to the adapter's device lookup.

**macOS.** `GetId()` returns `CBPeripheral.Identifier.ToString()` (Source). CoreBluetooth assigns it
per host. Stability across scanning sessions is not guaranteed (Reported: Silicon Labs).

### WinRT Ids embed the radio

WinRT AEP Ids have the shape `BluetoothLE#BluetoothLE<radio address>-<device address>` for LE and
`Bluetooth#Bluetooth<radio address>-<device address>` for BR/EDR (Measured). The local adapter's
address is part of every remote device's Id. Replacing the adapter therefore changes every WinRT Id.
The Microsoft stack supports one active radio at a time (Reported: Microsoft Q&A).

---

## Liveness

### Windows

cfgmgr32 raises nothing for a link change on an existing devnode. ARCHITECTURE.md §10.6.2 measured
it. ADR-0085 Context §1 concluded from it that a 32feet poll is the only live signal.

WinRT documents two push sources above cfgmgr32 that were outside that measurement:

- An AEP `DeviceWatcher` that requests `System.Devices.Aep.IsConnected` raises `Updated` with the
  changed properties. Microsoft's GATT client guide builds its device list this way (Documented).
- `BluetoothLEDevice.ConnectionStatusChanged` fires per device while the app holds the object
  (Documented). 32feet's public surface has `BluetoothDevice.GattServerDisconnected` (Measured, by
  reflection over the 4.0.44 assembly). How its Windows asset raises that event was not traced.

The AEP watcher was run for this document. It raised `Added` for each bonded LE device, one
`Updated` each carrying `IsPaired` and `IsPresent`, then `EnumerationCompleted` (Measured). No link
changed during the run, because all four devices were disconnected throughout. The transition
behaviour is still unmeasured, so Context §1's "only" is not yet established.

Both sources need WinRT, which is ADR-0018's TFM coupling. Neither is reachable from core's bare
TFM.

### Linux

`Device1.Connected` changes raise `PropertiesChanged`. `Device1.Disconnected(reason, message)`
names a reason: `Timeout`, `Local`, `Remote`, `Authentication` or `Suspend` (Documented). The
kernel's `link` object also produces a udev add and remove per connection (Inferred from the
`hci_sysfs.c` device lifecycle). Core already receives those edges and cannot attribute them to a
peripheral.

BlueZ reconnects by itself after a link loss. `ReconnectAttempts` defaults to 7, at intervals of 1,
2, 4, 8, 16, 32 and 64 seconds (Documented: `main.conf`). A Periphery recovery loop on top of that
policy competes with it.

### macOS

`CBCentralManager.registerForConnectionEvents(options:)` is not available on macOS. Its platforms
are iOS, iPadOS, Mac Catalyst, tvOS, visionOS and watchOS (Documented). A macOS process learns of
connections it made itself, and can list current system connections with
`retrieveConnectedPeripherals(withServices:)`.

---

## GATT access

| OS-claimed service | Windows | BlueZ before 5.80 | BlueZ 5.80+ | macOS |
|---|---|---|---|---|
| HID over GATT `0x1812` | Listed. Characteristics refused with `AccessDenied` (Measured) | No `GattService1` object while a built-in profile claims it | Exported read-only by default | Unverified |
| Battery `0x180F` | Accessible (Measured) | Same | Same | Unverified |
| Device Information `0x180A` | Accessible (Measured) | Same | Same | Unverified |

**BlueZ.** `add_gatt_service` in `src/device.c` marks a service claimed when a built-in profile
probes it, and leaves it unclaimed when the profile was registered externally over D-Bus (Source).
Before commit `dbd6591` (2024-12-08, first released in 5.80), `export_service` in
`src/gatt-client.c` returned early for any claimed service, so the service had no D-Bus object at
all. From 5.80, `[GATT] ExportClaimedServices` defaults to `read-only`, and writes return
`NotAuthorized` (Source). The BlueZ columns above assume the distribution built the input, battery
and deviceinfo profiles, which is the usual configuration but was not checked per distribution
(Inferred).

The UUIDs stay visible either way. `add_gatt_service` calls `btd_device_add_uuid` before the claim,
so `Device1.UUIDs` lists every discovered primary service, claimed or not (Source). A Linux service
filter can read `Device1.UUIDs` without any GATT access.

**Windows.** The probe used `BluetoothCacheMode.Cached` and opened no connection.
`GetGattServicesAsync` listed `0x1812`, and `GetCharacteristicsAsync` refused it. Every other
service returned its characteristics: Generic Access, Device Information, Battery and a vendor
16-bit service on the mouse; Generic Attribute, Generic Access, the Nordic UART Service and a vendor
128-bit service on the other peripheral.

**Web Bluetooth.** The Web Bluetooth GATT blocklist also excludes `0x1812`, along with several
vendor DFU services and FIDO services (Documented: WebBluetoothCG registries). 32feet's BLE package
models the Web Bluetooth API. A code search of the 32feet repository found no copy of that list
(Source), so the refusal measured on Windows came from the OS.

---

## Pairing

| | Windows | Linux | macOS |
|---|---|---|---|
| OS API | `DeviceInformationPairing.PairAsync`, and `DeviceInformationCustomPairing` for PIN and confirmation (Documented) | `Device1.Pair()`, which needs a registered agent or a default agent (Documented) | None in CoreBluetooth. Accessing a characteristic that needs encryption makes macOS prompt (Reported: bleak). `IOBluetoothDevicePair` covers BR/EDR (Documented). |
| 32feet | `PairAsync()` calls `DeviceInformation.Pairing.PairAsync()` (Source) | `PairAsync(code)` registers a `DisplayOnly` agent, requests default-agent status, pairs, then unregisters (Source) | `PairAsync` throws `PlatformNotSupportedException`. `IsPaired` returns `false` unconditionally (Source). |

On Linux, requesting default-agent status displaces the desktop session's pairing agent for the
duration of the call. Whether BlueZ restores the previous default afterwards is unverified. With no
agent registered, as on a headless host, `Pair()` fails (Documented).

On macOS, code that branches on 32feet's `IsPaired` treats every peripheral as unpaired.

After pairing, Windows creates the bond's devnodes with arrival edges, which core receives
(ARCHITECTURE.md §10.6.2 measured this on a re-pair). On Linux a new bond is a `Device1` property
change, and core sees nothing. On macOS there is no inventory to appear in.

---

## Scanning

| | Windows | Linux | macOS |
|---|---|---|---|
| API | `BluetoothLEAdvertisementWatcher` | `Adapter1.StartDiscovery` with `SetDiscoveryFilter` | `CBCentralManager.scanForPeripherals` |
| Active and passive | Both, through `ScanningMode` (Documented) | `Transport`, `RSSI`, `Pathloss`, `UUIDs` and `Pattern` filters (Documented) | Active only in bleak's backend (Reported) |
| Sharing | Per watcher | One discovery session per adapter shared by all D-Bus clients. Filters from every client are merged, and each client must re-check what it receives (Documented). | Per manager |
| Address in results | Yes | Yes | No. Identifier only. |

`StopDiscovery` ends a client's session. Discovery itself stops only when every client has stopped
(Documented).

---

## Permissions

**Windows.** A packaged app declares the `bluetooth` device capability (Documented). An unpackaged
desktop process needs none. The probe ran unpackaged, and `DeviceAccessInformation.CurrentStatus`
reported `Allowed` (Measured).

**Linux.** Access to `org.bluez` is governed by the system D-Bus policy. Not researched here.

**macOS.** CoreBluetooth requires `NSBluetoothAlwaysUsageDescription`. For a binary built against
the macOS 11 SDK or later, TCC terminates the process when the key is missing (Reported: Chromium
issue 40148059, bleak issue 761). A console process has no bundle, so TCC attributes it to the
terminal app that launched it, and granting that terminal permission fixes it for that terminal only
(Reported: Apple Developer Forums thread 675773). ADR-0011 already records the
`com.apple.security.device.bluetooth` entitlement for sandboxed apps.

---

## Caching and connection lifetime

**Windows.** Each GATT call takes `BluetoothCacheMode.Cached` or `Uncached`. Creating a
`BluetoothLEDevice` does not connect. `GattSession.MaintainConnection`, an uncached discovery, or a
read or write does. Disposing every reference disconnects after a short timeout when no other app
holds the device. A GATT operation against an unreachable device times out after seven seconds, and
each queued request can take that long (Documented: Microsoft Learn, Bluetooth GATT Client).

**BlueZ.** `[GATT] Cache` defaults to `always`, which caches attributes even for unpaired devices
(Documented: `main.conf`). Caching arrived in 5.51 (Source: ChangeLog).

**macOS.** Not researched.

---

## What `OfCategory(Bluetooth)` returns today

| Platform | Result |
|---|---|
| Windows | Radio nodes, every bonded device node, and service nodes of class Bluetooth. Service nodes the OS gives a function class, such as HID, land in that category instead (Measured: ARCHITECTURE.md §10.6.2, #233). |
| Linux | Adapters and `hciN:<handle>` link objects. No bonds and no addresses (Source). |
| macOS | Registry objects of class `IOBluetoothDevice`: at most connected BR/EDR devices, and possibly nothing on macOS 12+ (Reported). |

---

## Open questions

| Question | What resolves it |
|---|---|
| Does macOS 13+ register any `IOBluetoothDevice` objects? | `ioreg -r -l -c IOBluetoothDevice` with a connected BR/EDR device, then a connected LE device. |
| Does an AEP watcher raise `Updated` for `IsConnected` on a link change? | A link toggle during `dotnet run --project scratch/BleOsProbe -- 120`. |
| Does Windows resolve a rotated RPA back to the devnode keyed at pairing? | #232, on Windows, with a sniffer confirming the rotation. |
| Does `BDIF_LE_CONNECTED` track the link on LE devnodes? | The same link toggle, reading the devnode flags before and after. |
| Does CoreBluetooth on macOS hide `0x1812`? | `discoverServices(nil)` against an LE HID peripheral on a Mac. |
| Does BlueZ restore the desktop's default agent after 32feet's `PairAsync(code)`? | Pair from 32feet in a GNOME session, then pair from Settings. |
| What does `0x04000000` mean in `DEVPKEY_Bluetooth_DeviceFlags`? | Not defined in SDK 10.0.26100's `bthdef.h`. |

---

## Sources

BlueZ

- [`doc/org.bluez.Device.rst`](https://github.com/bluez/bluez/blob/master/doc/org.bluez.Device.rst)
- [`doc/org.bluez.Adapter.rst`](https://github.com/bluez/bluez/blob/master/doc/org.bluez.Adapter.rst)
- [`src/main.conf`](https://github.com/bluez/bluez/blob/master/src/main.conf)
- [`src/gatt-client.c`](https://github.com/bluez/bluez/blob/master/src/gatt-client.c)
- [`src/device.c`](https://github.com/bluez/bluez/blob/master/src/device.c), `add_gatt_service`
- [Commit `dbd6591`, "main.conf: Add GATT.ExportClaimedServices"](https://github.com/bluez/bluez/commit/dbd6591bd1d02ace39debd8a67c75b3cbe9c4d66)
- [ChangeLog](https://github.com/bluez/bluez/blob/master/ChangeLog)
- [Ubuntu 22.04 `bluez`](https://packages.ubuntu.com/jammy/bluez), [Ubuntu 24.04 `bluez`](https://packages.ubuntu.com/noble/bluez)

Linux kernel

- [`net/bluetooth/hci_sysfs.c`](https://github.com/torvalds/linux/blob/master/net/bluetooth/hci_sysfs.c)

Windows

- [Bluetooth GATT Client](https://learn.microsoft.com/en-us/windows/apps/develop/devices-sensors/gatt-client)
- [`BluetoothLEAdvertisementWatcher.ScanningMode`](https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.advertisement.bluetoothleadvertisementwatcher.scanningmode)
- [`DeviceInformationCustomPairing`](https://learn.microsoft.com/en-us/uwp/api/windows.devices.enumeration.deviceinformationcustompairing)
- [`BluetoothLEDevice.ConnectionStatusChanged`](https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.bluetoothledevice.connectionstatuschanged)
- `bthdef.h`, Windows SDK 10.0.26100, `BDIF_*` definitions
- [Microsoft Q&A: multiple Bluetooth adapters](https://learn.microsoft.com/en-us/answers/questions/4032619/can-a-windows-11-pc-use-2-bluetooth-adapters-at-on)

Apple

- [`CBCentralManager.registerForConnectionEvents(options:)`](https://developer.apple.com/documentation/corebluetooth/cbcentralmanager/registerforconnectionevents(options:))
- [`CBCentralManager.retrieveConnectedPeripherals(withServices:)`](https://developer.apple.com/documentation/corebluetooth/cbcentralmanager/retrieveconnectedperipherals(withservices:))
- [`IOBluetoothDevicePair`](https://developer.apple.com/documentation/iobluetooth/iobluetoothdevicepair)
- [Apple Developer Forums 675773: command-line utility and Core Bluetooth permissions](https://developer.apple.com/forums/thread/675773)
- [Apple Developer Forums 685545: `IOBluetoothRegisterForDeviceConnectNotifications` on Monterey](https://developer.apple.com/forums/thread/685545)
- [Chromium issue 40148059: macOS 11 Bluetooth permission](https://issues.chromium.org/issues/40148059)

32feet

- [`Platforms/Windows/BluetoothDevice.windows.cs`](https://github.com/inthehand/32feet/blob/main/InTheHand.BluetoothLE/Platforms/Windows/BluetoothDevice.windows.cs)
- [`Platforms/Linux/BluetoothDevice.linux.cs`](https://github.com/inthehand/32feet/blob/main/InTheHand.BluetoothLE/Platforms/Linux/BluetoothDevice.linux.cs)
- [`Platforms/Apple/BluetoothDevice.unified.cs`](https://github.com/inthehand/32feet/blob/main/InTheHand.BluetoothLE/Platforms/Apple/BluetoothDevice.unified.cs)

Other

- [Web Bluetooth GATT blocklist](https://github.com/WebBluetoothCG/registries/blob/master/gatt_blocklist.txt)
- [bleak: macOS backend](https://bleak.readthedocs.io/en/latest/backends/macos.html)
- [bleak issue 761: crash without a usage description](https://github.com/hbldh/bleak/issues/761)
- [hidapi issue 127: registry path containing `IOBluetoothDevice`, macOS 10.14.6](https://github.com/libusb/hidapi/issues/127)
- [Silicon Labs: CoreBluetooth peripheral identifiers](https://community.silabs.com/s/article/x-how-to-get-a-ble-peripheral-mac-address-with-ios-and-corebluetooth)
