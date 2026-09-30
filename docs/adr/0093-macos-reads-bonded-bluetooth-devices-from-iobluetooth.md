---
title: "ADR-0093: macOS reads bonded Bluetooth devices from IOBluetooth, and only with the process's Bluetooth permission"
status: "Accepted"
status_note: "Written for issue #259 and shipped with its implementation. 2026-09-30 amendment: a connected HID node's Transport marks its bond LowEnergy or BrEdr, kept for the process. Measured on 2026-09-30 on a MacBook Air (Apple silicon) running macOS 26.4.1, with one paired LE mouse, over SSH and inside Terminal.app. The source was chosen by the maintainer from three measured options."
date: "2026-09-30"
authors: "@charles8051"
tags: ["architecture", "decision", "bluetooth", "ble", "macos", "iobluetooth", "tcc", "device-enumeration", "device-monitor", "pure-core"]
supersedes: ""
superseded_by: ""
depends_on: ["0004-two-level-device-state-model.md", "0011-iokit-macos-provider.md", "0014-macos-device-category-coverage.md", "0089-tests-do-not-depend-on-elapsed-time.md", "0091-linux-reads-bonded-bluetooth-devices-from-bluez.md"]
---

# ADR-0093: macOS reads bonded Bluetooth devices from IOBluetooth, and only with the process's Bluetooth permission

## Status

Accepted 2026-09-30, and implemented with it.

> **Amendment (2026-09-30), D3's LE evidence.** A connected Bluetooth HID device has an IOKit
> node carrying `DeviceAddress` and `Transport`, and reading it needs no Bluetooth permission.
> `Transport = "Bluetooth Low Energy"` marks the bond at that address `LowEnergy`, and
> `"Bluetooth"` marks it `BrEdr`. The node exists only while the link is up, so the provider keeps
> each address's transports for the life of the process: a peripheral does not stop supporting a
> transport when its link drops. An LE bond that has not connected since the process started still
> reads `None`, and so does an LE device that is not a HID device.

---

## Context

### What macOS returned

ADR-0011 mapped `DeviceCategory.Bluetooth` to the IOKit registry class `IOBluetoothDevice`. On macOS
26.4.1 the registry holds exactly one entry of that class, and it is not a remote device. It is the
Mac's own `Bluetooth-Incoming-Port` serial service: `DeviceType = "Serial"`, a `BD_ADDR` equal to
the Mac's own radio, and an `IOUserBluetoothSerialDriver` beneath it. `OfCategory(Bluetooth)`
returned that entry, with no name and no address, and never the paired mouse (#259). The same
service already appears under `Ports` as `Bluetooth-Incoming-Port`.

### Where the bonds are

Measured from a process started over SSH, then inside Terminal.app after Terminal was given
Bluetooth access:

| Source | Over SSH | In Terminal.app |
|---|---|---|
| `+[IOBluetoothDevice pairedDevices]` | 0 devices, no error | the LE mouse: address, name, `isConnected`, `classOfDevice = 0` |
| `+[CBManager authorization]` | 0, not determined | 3, allowed always |
| `system_profiler -json SPBluetoothDataType` | the mouse, with address and connection | the same |
| `/Library/Preferences/com.apple.Bluetooth.plist` | no device list | — |

- TCC decides Bluetooth access per **responsible process**. Over SSH that is `sshd-keygen-wrapper`,
  which cannot show a prompt. `tccd` answered `kTCCServiceBluetoothAlways` with `authValue=1`, and
  IOBluetooth then reported the controller powered off and returned an empty list.
- Reading `CBManager.authorization` never prompts, so a process can learn which case it is in.
- `pairedDevices` cost about 50 ms on the first call and 0.1 ms after it.
- From .NET, `objc_msgSend` through P/Invoke returned the same bonds from the main thread, a
  thread-pool thread and a dedicated thread.
- IOBluetooth's `registerForConnectNotifications:` fired on a connection, on a background thread.
  The poll saw `isConnected` change 0.3 s later.
- `isLowEnergyDevice` answers on this build, but it is not in the public header.

---

## Decision

### D1 — Core enumerates IOBluetooth's bonds under `DeviceCategory.Bluetooth`

`MacOSDeviceProvider` asks `+[IOBluetoothDevice pairedDevices]` for `All`, `Bluetooth` and
unfiltered queries, unless the filter names a USB vendor or product id, as ADR-0091 D1 does. The
call goes through the Objective-C runtime (`objc_getClass`, `sel_registerName`, `objc_msgSend`),
inside an autorelease pool, with one fixed prototype per message.

`DeviceCategory.Bluetooth` no longer queries any IOKit registry class. The incoming serial service
stays under `Ports`.

### D2 — IOBluetooth is asked only when the process is allowed

The provider reads `CBManager.authorization` first and asks IOBluetooth only when it is
`AllowedAlways`. Otherwise it reports no bonds and logs the reason once per process, at Warning.

Asking without permission returns an empty list and no error, which would read as "no bonds". In an
app bundle it could also prompt, from inside a library call the app did not make for that
purpose. Requesting access stays the application's job.

### D3 — What a bond maps to

| `DeviceInfo` | From |
|---|---|
| `Id` | `iobluetooth:` plus the address, upper case, colon-separated |
| `Name` | `name`, or null when empty |
| `MacAddress` | `addressString` |
| `IsActive` | `isConnected` |
| `Category`, `BusType` | `Bluetooth` |
| `Status` | `OK` |
| `BluetoothTransports` | `BrEdr` when `classOfDevice` is non-zero, else `None` |

A class of device is BR/EDR evidence. No public property says LE, so an LE bond is `None`, which
`WithBluetoothTransport` leaves out. The flags mean "known to support", as on Linux before BlueZ
5.84 (ADR-0091).

### D4 — The watch polls, and an unavailable read changes nothing

`MacOSDeviceMonitorProvider` reads the bonds every 2 s on its `TimeProvider` and steps a pure
function, `IOBluetoothInventory.Step(held, snapshot)`. A new bond raises `Appeared`, then `Activated`
if it is connected. A bond gone raises `Disappeared`. `isConnected` changing raises `Activated` or
`Deactivated`, then `PropertyChanged`. The first read seeds silently, since the watcher's own
enumeration announces those bonds. An unavailable read, for want of permission or the framework,
keeps what is held and raises nothing. A permission granted while the watch runs announces the
bonds on the next poll.

Polling is needed anyway: IOBluetooth has no notification for a bond being added or removed. The
connect notification measured above would shorten the connection latency, at the cost of an
Objective-C class defined at run time. It is left for later.

---

## Consequences

- A console program reports bonds only when its terminal app has Bluetooth access. Over SSH, under
  `launchd`, or in CI it reports none, and says so once in the log.
- An app bundle that wants bonds must carry `NSBluetoothAlwaysUsageDescription` and obtain access
  itself, for example by creating a `CBCentralManager`, before Periphery reports any.
- Connection edges arrive up to 2 s late.
- LE bonds report `BluetoothTransports.None`.
- `OfCategory(Bluetooth)` no longer returns the incoming serial service.

---

## Alternatives considered

- **`system_profiler -json SPBluetoothDataType`.** Needs no permission and lists LE bonds with their
  connection. It is a subprocess of about 0.2 s per read, its JSON schema is undocumented, and it
  can only be polled. Kept as the ground truth in the device test.
- **IOBluetooth, falling back to `system_profiler` without permission.** Works everywhere, at the
  cost of two sources that must map to the same `DeviceInfo`.
- **CoreBluetooth.** Keeps no inventory of bonds, and gives an LE peripheral a per-host UUID rather
  than an address.

---

## Open questions

- **A BR/EDR bond.** None was paired on the measurement Mac, so D3's `BrEdr` flag is untested there.
- **LE evidence for a non-HID device.** The D3 amendment covers HID devices only.
- **Several radios.** Not measured.

---

## Related

- Issue: #259.
- [ADR-0004](0004-two-level-device-state-model.md): presence and activity.
- [ADR-0011](0011-iokit-macos-provider.md): the macOS provider and its original Bluetooth mapping.
- [ADR-0014](0014-macos-device-category-coverage.md): macOS category coverage.
- [ADR-0089](0089-tests-do-not-depend-on-elapsed-time.md): `TimeProvider`, and tests that fail when their fixture is missing.
- [ADR-0091](0091-linux-reads-bonded-bluetooth-devices-from-bluez.md): the same decision on Linux.
- [`docs/explorations/bluetooth-os-apis-2026-09.md`](../explorations/bluetooth-os-apis-2026-09.md).
