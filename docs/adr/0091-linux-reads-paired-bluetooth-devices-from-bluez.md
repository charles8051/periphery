---
title: "ADR-0091: Linux reads paired Bluetooth devices from BlueZ, in core, through libdbus-1"
status: "Proposed"
status_note: "No code. Written for issue #258. The BlueZ facts it rests on were measured on 2026-09-29 against BlueZ 5.72 with two btvirt controllers: unprivileged GetManagedObjects, and the Device1 signal sequence across an unpair, a pair, a connection and a disconnection."
date: "2026-09-29"
authors: "@charles8051"
tags: ["architecture", "decision", "bluetooth", "ble", "linux", "bluez", "d-bus", "device-enumeration", "device-monitor", "native-interop"]
supersedes: ""
superseded_by: ""
depends_on: ["0004-two-level-device-state-model.md", "0024-extension-package-pattern.md", "0052-periphery-treehopper-pure-core.md", "0083-ble-identity-does-not-survive-repairing.md", "0085-the-32feet-binding-is-two-integration-packages.md", "0090-supplementary-activity-sources.md"]
---

# ADR-0091: Linux reads paired Bluetooth devices from BlueZ, in core, through libdbus-1

> Number `0091` is provisional until merge (the next free number after ADR-0090), per this repo's
> "assign the number at merge" convention.

**Tracks:** `LinuxDeviceProvider`, `LinuxDeviceMonitorProvider`, and `DeviceCategory.Bluetooth` on
Linux. Issue #258.

---

## Status

Proposed 2026-09-29. No code.

---

## Context

### What Linux returns today

`OfCategory(DeviceCategory.Bluetooth)` on Linux returns the kernel's `bluetooth` class: one `host`
node per adapter and one `link` node per live connection. A paired peripheral is absent while it is
disconnected. A connected one appears only as a `link` named after its connection handle, with no
address and no name. `Appeared` and `Disappeared` fire once per connection for that node. This was
predicted from source in #258 and measured on 2026-09-29 against `main` at ec5a61e (comment on
#258).

### Where paired devices live

Bonds exist only in BlueZ, as `org.bluez.Device1` objects on the system bus
(`docs/explorations/bluetooth-os-apis-2026-09.md`, §Inventory → Linux). Measured on Ubuntu 24.04
with BlueZ 5.72 and two virtual LE controllers from `btvirt` (`bluez-test-tools`):

- One `org.freedesktop.DBus.ObjectManager.GetManagedObjects` call on `/` returns every adapter and
  device with its properties, as `a{oa{sa{sv}}}`.
- An unprivileged user can make that call. BlueZ's shipped `bluetooth.conf` grants
  `send_destination="org.bluez"` to the default context.
- `bluetoothd` links `libdbus-1.so.3`, so every host that runs BlueZ has libdbus-1.
- BlueZ stores a bond under `/var/lib/bluetooth/<adapter address>/<device address>/`. The directory
  is `0700 root`.

A `Device1` object is not a bond. Discovery creates one for every device it sees, and BlueZ removes
an unpaired one after `TemporaryTimeout`, 30 seconds by default. `Paired` means keys were
exchanged. `Bonded` means they were stored, and it exists only from BlueZ 5.65. Ubuntu 22.04 ships
5.64 (exploration, finding 8).

The signals across one unpair, pair, connection and disconnection, from `dbus-monitor` on the
central's side:

| Step | Signal on `/org/bluez/hci0/dev_…` |
|---|---|
| Unpair | `InterfacesRemoved`, `org.bluez.Device1` |
| Discovery | `InterfacesAdded`, `Device1` with `Paired=false`, `Connected=false` |
| Connection | `PropertiesChanged`: `Connected=true` |
| Pairing completes | `PropertiesChanged`: `Paired=true`, `Bonded=true` |
| Remote name read | `PropertiesChanged`: `Name`, `Alias` |
| Disconnection | `PropertiesChanged`: `Connected=false` |

`Connected` became true before `Paired` did, on both sides. Before the name was read, `Alias` held
the address with dashes, `00-AA-01-00-00-00`.

### The dependency rule, read again

The exploration and #258 both say ADR-0024 keeps D-Bus out of core. ADR-0024 does not say that. Its
dependency table limits `Periphery` to "BCL plus `Microsoft.Extensions.Logging.Abstractions`" and
forbids "any other third-party package". It is a rule about NuGet dependencies. Core already binds a
native library on Linux: `UdevInterop` declares `libudev.so.1` through `LibraryImport`. The claim
came from treating D-Bus as `Tmds.DBus`, the managed client that 32feet's Linux asset uses (ADR-0085
Context §4).

### Why core, and not an integration package

`DeviceCategory.Bluetooth` is a core category. On Windows, core returns bonded peripherals from
cfgmgr32 with no package added. If the Linux inventory lived in a package, the same query would
return bonds or not depending on which packages the consumer had installed.

ADR-0090's supplementary activity source exists for a signal core cannot reach. Once core reads
BlueZ, Linux Bluetooth has no such signal left.

---

## Decision

### D1 — Core enumerates paired BlueZ devices under `DeviceCategory.Bluetooth`

When the filter's category is `Bluetooth` or `All`, `LinuxDeviceProvider` makes one
`GetManagedObjects` call and yields one `DeviceInfo` per `org.bluez.Device1` whose `Paired` is
true. Any other category never touches the bus.

It selects on `Paired`, not `Bonded`, so it works on BlueZ before 5.65. It does not yield objects
that discovery created. Those are scan results, and ADR-0085 assigns LE scanning to
`Periphery.Ble.InTheHand`.

### D2 — The transport is libdbus-1, bound like libudev

`LibraryImport` declarations against `libdbus-1.so.3`, beside `UdevInterop`. Three rules for the
connection:

- Open it with `dbus_bus_get_private(DBUS_BUS_SYSTEM, …)`, never the shared `dbus_bus_get`. A shared
  connection belongs to whichever in-process libdbus user opened it first, and Periphery must not
  dispatch or close someone else's connection.
- Call `dbus_connection_set_exit_on_disconnect(connection, FALSE)` straight after opening it.
  libdbus sets exit-on-disconnect on every bus connection it opens, and it ends the process with
  `_exit` when the bus goes away. A library must not do that to its host.
- Release it with `dbus_connection_close`, then `dbus_connection_unref`.

### D3 — BlueZ absent is an empty answer, not an error

libudev's absence throws `DeviceProviderException`, because nothing works without it. BlueZ is
optional. A container usually has no system bus, and a server may not run `bluetoothd`.

If `libdbus-1.so.3` does not load, the system bus cannot be reached, or `org.bluez` has no owner,
enumeration yields the udev results alone. The provider logs one warning per instance and does not
throw.

### D4 — What a BlueZ device maps to

| `DeviceInfo` | Source |
|---|---|
| `Id` | `bluez:<adapter address>/<device address>`, for example `bluez:00:AA:01:00:00:00/00:AA:01:01:00:01` |
| `Name` | `Device1.Alias` |
| `MacAddress` | `Device1.Address` |
| `Category`, `BusType` | `Bluetooth`, `Bluetooth` |
| `IsActive` | `Device1.Connected` |
| `ParentId` | the adapter's sysfs path, resolved through `/sys/class/bluetooth/hciN` |

The adapter address comes from the `Adapter1` object that `Device1.Adapter` names, in the same
`GetManagedObjects` reply. Both addresses in `Id` are parsed as `BluetoothAddress` and written in
its uppercase colon form.

**`Id` is not the object path.** The path embeds the adapter's kernel name,
`/org/bluez/hci0/dev_…`. The kernel numbers `hciN` in registration order, so adding a second
adapter or re-plugging one can renumber it and change every `Id` under it. The pair of addresses is
the key BlueZ stores the bond under on disk. A peripheral paired with two adapters is two bonds and
yields two `DeviceInfo`s.

**`Name` is `Alias`**, which BlueZ fills from a user-set alias, then the remote name, then the
address.

**`MacAddress` carries the address** because `DeviceInfo` documents that property for Bluetooth
devices. `WithMacAddress` then selects a Bluetooth device. #301 does the same on Windows.

### D5 — Presence is `Paired`, activity is `Connected`

ADR-0004's two levels map onto the two properties:

| Observation for a `Device1` | Edge |
|---|---|
| `Paired` becomes true | `Appeared`, with `IsActive` from `Connected` |
| `InterfacesAdded` with `Paired=true` | `Appeared` |
| `InterfacesRemoved`, or `Paired` becomes false | `Disappeared` |
| `Connected` changes on a paired device | `Activated` or `Deactivated` |
| `Alias` changes on a paired device | `DevicePropertyChanged` |

Nothing is raised for an unpaired object. In the measured sequence the link came up before pairing
finished, so a new bond appears already active and no `Activated` precedes its `Appeared`.

### D6 — A `bluetoothd` restart holds presence

When `org.bluez` loses its owner, every BlueZ object vanishes, but the bonds stay on disk. The
provider raises `Deactivated` for each device that was connected and keeps every device present.

When `org.bluez` gains an owner, the provider calls `GetManagedObjects` again and diffs the reply
against what it holds. Bonds that came or went raise `Appeared` or `Disappeared`, and connection
changes raise `Activated` or `Deactivated`.

### D7 — Link nodes leave the result

With D5 carrying connection state on the device, a `link` node repeats it without an address. It is
also the node that raises `Appeared` per connection today. The provider skips udev devices with
`DEVTYPE=link`, in enumeration and in monitoring. Adapters stay.

A BR/EDR HID device's sysfs parent is its link node: `hidp_setup_hid` sets
`hid->dev.parent = &session->conn->hcon->dev` (Source: `net/bluetooth/hidp/core.c`). The same
function writes the adapter's address to `phys` and the peer's to `uniq`, which udev reports as
`HID_PHYS` and `HID_UNIQ`. A node whose parent is a skipped link node takes `ParentId`
`bluez:<HID_PHYS>/<HID_UNIQ>`, written as in D4, which is the `Id` of the paired device. The HID
node then sits under the peripheral. The value is built from the node's own properties, with no
bus call. A node without
both addresses takes the link's parent, the adapter.

### D8 — A pure core decides, and the shell only talks to the bus

The shell owns libdbus: the connection, the calls, the file descriptor and the dispatch loop. It
turns each reply and signal into an immutable value before anything is decided. The core is pure:

- `Map(snapshot) → DeviceInfo[]` covers D1 and D4.
- `Step(state, observation) → (state, edges)` covers D5 and D6. An observation is a snapshot with
  its buffered signals, an interface added or removed, a property change, an owner lost, or an
  owner gained. Each carries its sender.

Both are tested as tables, with no bus, as in the Periphery.Treehopper split (ADR-0052).

For watching, `dbus_connection_get_unix_fd` gives the connection's descriptor.
`LinuxDeviceMonitorProvider` already polls the udev monitor's descriptor on a dedicated thread, with
a 100 ms timeout. The bus descriptor joins that poll. When it is readable the shell calls
`dbus_connection_read_write(connection, 0)` and drains `dbus_connection_pop_message`. It registers
three match rules, all with `sender='org.bluez'` except the last:

- `ObjectManager` signals;
- `PropertiesChanged` with `arg0='org.bluez.Device1'`;
- `NameOwnerChanged` from the bus with `arg0='org.bluez'`.

**The snapshot and the signals combine so that the result converges.** Nothing in BlueZ's API ties
the moment it builds a `GetManagedObjects` reply to the signals it sends around it. A signal can
describe a change the reply already contains, or one it does not. At start, and again whenever
`org.bluez` gains an owner, the shell runs one sequence:

1. Add the match rules.
2. Take the owner's unique name: from `GetNameOwner("org.bluez")` at start, or from the
   `NameOwnerChanged` that reported the new owner.
3. Send `GetManagedObjects` to that unique name with `dbus_connection_send`, not the blocking call,
   and keep its serial.
4. Buffer every BlueZ signal that arrives before the method return whose reply serial matches.

The core applies the snapshot, then the buffered signals in arrival order, then each later signal
as it arrives. It raises the edges for the snapshot and the buffer as one diff against what it held
before, and one diff per later signal.

This rests on one premise, which BlueZ declares. Every property the core reads is annotated
`org.freedesktop.DBus.Property.EmitsChangedSignal=true`: `busctl introspect` on BlueZ 5.72 shows
`emits-change` on `Adapter`, `Address`, `Alias`, `Paired`, `Bonded` and `Connected`. Each change to
one of them is therefore followed by a `PropertiesChanged` that carries the new value.
`InterfacesAdded` carries every property, and `InterfacesRemoved` is final. The bus relays one
sender's messages in the order sent.

So once the last signal BlueZ sent for a property has arrived, the core holds that property's
current value. That holds whether each earlier signal was older or newer than the snapshot, and the
core never has to tell which. A signal can arrive after the snapshot that holds its value, and the
core holds a stale value only while a signal is still in flight.

This is a convergence guarantee, not a replay of history. The snapshot and the buffer raise one net
edge per property, and each later signal raises at most one. A change that was undone before the
snapshot was built can still raise a pair of edges after it, when its signals arrive late.
`Alias` can briefly show an older name the same way.

A `PropertiesChanged` that lists one of these properties as invalidated, value omitted, breaks the
annotation. The core logs it and runs the snapshot sequence again.

The core drops any message whose sender is not the current owner's unique name. That covers a late
signal from a `bluetoothd` that has exited, and a reply to a request sent to that owner. It also
drops a method return whose reply serial is not its latest request's.

`EnumerateAsync` takes one snapshot and subscribes to nothing, so it has no ordering to settle.

---

## Consequences

### Good

- `OfCategory(Bluetooth)` on Linux returns paired peripherals with a name, an address and
  connection state, and a watcher gets presence and activity edges for them, as on Windows.
- No package dependency is added. `libdbus-1.so.3` loads only when Bluetooth is in scope.
- `WithMacAddress` selects a Bluetooth device on Linux, and on Windows once #301 lands.
- `Periphery.Ble.InTheHand` gains a Linux join key. 32feet's BlueZ `BluetoothDevice.Id` is the
  colon-form address, which is `MacAddress`. A `net10.0` target can come back, on
  `InTheHand.BluetoothLE` 4.0.45 or later, which pins `Tmds.DBus` 0.95.1.
- Linux Bluetooth needs no ADR-0090 source.

### Bad

- Core owns a libdbus binding. Walking `a{oa{sa{sv}}}` takes the message-iterator API, and
  `DBusMessageIter` is a caller-allocated struct. Its fields are declared in the public header
  `dbus/dbus-message.h`, so the binding copies that declaration rather than guessing a size.
- With BlueZ absent, D7 removes the link nodes and D1 adds nothing, so Linux loses the one
  connection signal it had. That signal carried no identity.
- With BlueZ absent, a BR/EDR HID node's D7 `ParentId` names a paired device that does not
  enumerate.
- An unfiltered `Devices.Enumerate()` on Linux makes one system-bus round trip when libdbus is
  present.
- D7 changes what a Linux Bluetooth watcher receives. It needs a `BREAKING-CHANGES.md` entry when it
  ships.

### Neutral

- Transport is not decided. BlueZ 5.72's `Device1` has no bearer property. `AddressType=random`
  implies LE, and `public` does not decide it (#302).
- The README's Linux requirements gain `libdbus-1`, optional, for the Bluetooth inventory.

---

## Alternatives considered

**An integration package over `Tmds.DBus`.** #258's direction. It makes the meaning of a core
category depend on the consumer's package set, and ADR-0024 does not require it.

**A managed D-Bus client over a Unix socket.** It would need no native library, and its codec would
be pure. Periphery would own a D-Bus implementation: SASL `EXTERNAL` authentication, which needs the
process uid and so libc anyway, and the marshalling rules for alignment, signatures and variants.
libdbus-1 is the reference implementation and is present wherever BlueZ runs. Revisit if the
iterator binding turns out larger than a codec would be.

**Read the bond store.** `/var/lib/bluetooth` is `0700 root`, it has no connection state, and its
format is BlueZ's private business.

**Document Linux Bluetooth as adapters and links.** #258's interim. The README promises the same API
on every platform, and this makes the promise false by definition instead of by accident.

**Use the object path as `Id`.** See D4.

---

## Open questions

- **Re-pairing a privacy peripheral.** If BlueZ keys a bond and `Device1.Address` by the identity
  address of a peripheral that advertises resolvable private addresses, D4's `Id` survives a
  re-pair, which Windows does not (ADR-0083, #232). Unmeasured. It needs a real radio and the
  bench's privacy image.
- **Scale.** `GetManagedObjects` latency with many objects, including GATT objects, is unmeasured.
- **Two adapters.** D4's `ParentId` and `Id` with two adapters are untested. `btvirt -l2` gives two
  controllers, so this can be measured without hardware.
- **`Paired` becoming false without removal.** Not observed. D5 handles it either way.

---

## Related

- Issues: #258, #301, #302, #232, #294.
- [ADR-0004](0004-two-level-device-state-model.md): presence and activity.
- [ADR-0024](0024-extension-package-pattern.md): the dependency table.
- [ADR-0085](0085-the-32feet-binding-is-two-integration-packages.md): LE scanning, and the Windows-only amendment.
- [ADR-0090](0090-supplementary-activity-sources.md): the source Linux Bluetooth no longer needs.
- [`docs/explorations/bluetooth-os-apis-2026-09.md`](../explorations/bluetooth-os-apis-2026-09.md).
