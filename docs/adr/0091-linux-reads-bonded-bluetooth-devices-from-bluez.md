---
title: "ADR-0091: Linux reads bonded Bluetooth devices from BlueZ, in core, over a managed D-Bus client"
status: "Accepted"
status_note: "No code. Written for issue #258. The BlueZ facts were measured on 2026-09-29 against BlueZ 5.72 with two btvirt controllers, and traced in the BlueZ 5.72 and dbus 1.14.10 sources. Revised the same day after four independent reviews: the transport moved from libdbus-1 to a managed client, and presence now follows BlueZ across restarts. Accepted 2026-09-30: enumeration shipped in #305, and watching with D7 in the change that accepted it."
date: "2026-09-29"
authors: "@charles8051"
tags: ["architecture", "decision", "bluetooth", "ble", "linux", "bluez", "d-bus", "device-enumeration", "device-monitor", "pure-core"]
supersedes: ""
superseded_by: ""
depends_on: ["0004-two-level-device-state-model.md", "0010-udev-linux-provider.md", "0024-extension-package-pattern.md", "0052-periphery-treehopper-pure-core.md", "0057-linux-extension-backends.md", "0083-ble-identity-does-not-survive-repairing.md", "0085-the-32feet-binding-is-two-integration-packages.md", "0089-tests-do-not-depend-on-elapsed-time.md", "0090-supplementary-activity-sources.md"]
---

# ADR-0091: Linux reads bonded Bluetooth devices from BlueZ, in core, over a managed D-Bus client

> Number `0091` is provisional until merge (the next free number after ADR-0090), per this repo's
> "assign the number at merge" convention.
>
> **Amendment (2026-09-30), D8's shell.** The BlueZ watch runs its own receive loop on its
> connection's stream. It does not add the bus socket to `LinuxDeviceMonitorProvider`'s udev poll.
> The client is a managed `Stream` (D2), whose reads take a cancellation token and whose waits take
> a `TimeProvider`, which a raw descriptor in a `poll()` loop would give up. BlueZ's edges and
> udev's therefore reach the watcher on different threads. `DeviceWatcher` locks around every
> provider event, and the Windows provider already raises Bluetooth link edges from a thread of
> their own.
>
> **Amendment (2026-09-30), D8's failure rule.** An error reply or a passed deadline on a snapshot
> keeps the last inventory, and signals go on updating it until a retried snapshot replaces it. It
> does not mark BlueZ absent. Nothing in a failed call says BlueZ's objects changed, and clearing
> them would raise `Disappeared` and then `Appeared` for every bond on one slow reply. Only the owner
> going (D6), the connection going, or `AccessDenied` clears the inventory. A failed first snapshot
> leaves it empty.
>
> **Amendment (2026-09-30), D4's transport.** A bond's `DeviceInfo.BluetoothTransports` (ADR-0085's
> D5 amendment, issue #302) comes from the best evidence BlueZ exposes without root.
>
> - **BlueZ 5.84 and later:** `org.bluez.Bearer.LE1` and `org.bluez.Bearer.BREDR1` are registered on
>   the device path only for a transport the device supports (`src/device.c`). Their properties
>   are experimental and hidden without `-E`, but the interfaces are not, so their presence is
>   exact.
> - **Before 5.84:** `Device1` merges both bearers. The transport is the union of its clues. A
>   `random` `AddressType`, an `Appearance`, or a cached Generic Access (1800) or Generic Attribute
>   (1801) service in `UUIDs` means LE. A `Class` means BR/EDR. With none of them, it is `None`.
> - **The GATT clue is a heuristic.** GATT over BR/EDR exists, but BlueZ discovers GATT over LE in
>   practice. The btvirt bonds on BlueZ 5.72 have a public address and no `Appearance` or `Class`,
>   and this clue alone classifies them as LE.
> - **Not read:** `/var/lib/bluetooth/<adapter>/<device>/info` records `SupportedTechnologies`
>   exactly, but it is `0700 root`.
> - **Before 5.84 a flag is positive evidence.** A missing flag does not show the transport is
>   absent. A dual-mode device bonded over BR/EDR can show only `Class`. Nothing concludes absence
>   from the flags, and `Periphery.Ble.InTheHand`'s join accepts any bond.
>
> The watch needs no change. A `UUIDs`, `Appearance` or `Class` change, or a bearer interface
> added, changes the mapped `DeviceInfo`, which raises `DevicePropertyChanged`.

**Tracks:** `LinuxDeviceProvider`, `LinuxDeviceMonitorProvider`, and `DeviceCategory.Bluetooth` on
Linux. Issue #258.

---

## Status

Accepted 2026-09-30. Enumeration (D1–D4) shipped in #305. Watching (D5–D8) and D7 shipped with this status change.

---

## Context

### What Linux returns today

`OfCategory(DeviceCategory.Bluetooth)` on Linux returns the kernel's `bluetooth` class: one `host`
node per adapter and one `link` node per live connection. A bonded peripheral is absent while it is
disconnected. A connected one appears only as a `link` named after its connection handle, with no
address and no name. `Appeared` and `Disappeared` fire once per connection for that node. This was
predicted from source in #258 and measured on 2026-09-29 against `main` at ec5a61e (comment on
#258).

### Where bonds live

Bonds exist only in BlueZ, as `org.bluez.Device1` objects on the system bus
(`docs/explorations/bluetooth-os-apis-2026-09.md`, §Inventory → Linux). What follows was measured
on Ubuntu 24.04 with BlueZ 5.72 and two virtual LE controllers from `btvirt` (`bluez-test-tools`),
unless it cites a source line.

**Access.**
- One `GetManagedObjects` call on `/` returns every adapter and device with its properties, as
  `a{oa{sa{sv}}}`. bluetoothd answered in 0.2–0.7 ms. The reply was 4.6 KB with two bonds and
  10.9 KB once their GATT objects were cached. Cached GATT objects stay after a disconnection.
- `nobody` can make that call and receive BlueZ's signals. The default context of BlueZ's
  `bluetooth.conf` allows `send_destination="org.bluez"`. BlueZ 5.50 and earlier denied the default
  context. Commit 3ef0ce954b (2018-11-06) changed that for 5.51.
- A call addressed to `org.bluez` while bluetoothd is stopped **starts it**. BlueZ ships a D-Bus
  service file whose `SystemdService=dbus-org.bluez.service` is an alias of an enabled
  `bluetooth.service`, and a message without `NO_AUTO_START` asks the bus to activate it. The same
  call with auto-start off failed with "Name org.bluez does not exist", and `GetNameOwner` returned
  `NameHasNoOwner`. Neither started it.

**What a `Device1` is.** Discovery creates one for every device it sees, and BlueZ removes an
unbonded one after `TemporaryTimeout`, 30 seconds by default. `Paired` means keys were exchanged.
`Bonded` means they were stored, and it exists from BlueZ 5.65. Ubuntu 22.04 ships 5.64. A pairing
that was not bonded turns `Paired` false when its link drops, and the object stays (Source:
`src/device.c:3345-3370`).

**Signals across one lifecycle,** on the central's side:

| Step | Signals |
|---|---|
| Unpair | `InterfacesRemoved` `Device1`, with no `Paired=false` first |
| Discovery | `InterfacesAdded` `Device1` with `Paired=false`, `Connected=false` |
| Connection | `PropertiesChanged` `Connected=true` |
| Pairing completes | `PropertiesChanged` `Paired=true`, `Bonded=true` |
| Remote name read | `PropertiesChanged` `Name`, `Alias` |
| Disconnection | `PropertiesChanged` `Connected=false` |
| bluetoothd stops (`systemctl stop`) | `Connected=false` for each link, adapters power off, then `InterfacesRemoved` for every `Device1` and `Adapter1`, then `NameOwnerChanged` to no owner. The kernel links are gone within 77 ms. |
| bluetoothd starts | `NameOwnerChanged` to a new owner, then `InterfacesAdded` for each adapter and each bond with `Paired=true`, 5–11 ms later. A `GetManagedObjects` sent as soon as the new owner appeared returned no adapters and no devices, twice. |
| bluetoothd is killed (`SIGKILL`) | `NameOwnerChanged` only. The kernel link survives. The restarted daemon re-added the bond with `Connected=false`, sent `Connected=true` 8 ms later, and dropped the link itself 1.8 s after that. |
| Adapter removed and re-added | `InterfacesRemoved` for the bond, then the adapter, with no owner change. Then `InterfacesAdded` for both, same addresses and `hciN` names. The bond store was unchanged. |

`Connected` became true before `Paired` did, on both sides. After a restart, BlueZ sent
`Paired=true` again on the first reconnection. Before the name was read, `Alias` held the address
with dashes, `00-AA-01-00-00-00`. The only property ever sent as invalidated was `RSSI`.

### How BlueZ orders a reply against its signals

BlueZ's gdbus builds a method reply from live state inside one main-loop callback. It sends the
reply through `g_dbus_send_message`, which first flushes every pending property and interface
signal: "Flush pending signal to guarantee message order" (Source: `gdbus/object.c:1523-1524`). So
every signal BlueZ sends before a reply describes a change the reply already contains. dbus-daemon
and dbus-broker relay one connection's messages to a recipient in the order sent. That is how both
implementations behave. The D-Bus specification does not state it.

BlueZ does not signal every change the moment it happens (Source, BlueZ 5.72):
- The `Paired` getter turns true when keys arrive, but its signal waits for service discovery
  (`src/device.c:199`, `pending_paired`).
- `device_update_addr` emits `Address` and `AddressType` when an LE peer's identity address
  arrives, but not the `Alias` whose fallback depends on the address (`src/device.c:4419-4453`,
  `938-944`). The object path keeps the address it was created with.
- `device_merge_duplicate` copies the name and alias from an older object with the same identity,
  sends no signal, and leaves the older object in place (`src/device.c:4505-4530`).

### The dependency rule, read again

The exploration and #258 both say ADR-0024 keeps D-Bus out of core. ADR-0024 does not say that. Its
dependency table limits `Periphery` to "BCL plus `Microsoft.Extensions.Logging.Abstractions`" and
forbids "any other third-party package". It is a rule about NuGet packages. Core already binds
`libudev.so.1`, and ADR-0057 calls that the precedent for native dependencies. The claim came from
treating D-Bus as `Tmds.DBus`, the managed client that 32feet's Linux asset uses (ADR-0085 Context
§4). This ADR adds neither a package nor a native library (D2).

### Why core, and not an integration package

`DeviceCategory.Bluetooth` is a core category. On Windows, core returns bonded peripherals from
cfgmgr32 with no package added. If the Linux inventory lived in a package, the same query would
return bonds or not depending on which packages the consumer had installed.

ADR-0090's supplementary activity source exists for a signal core cannot reach. Once core reads
BlueZ, Linux Bluetooth has no such signal left.

---

## Decision

### D1 — Core enumerates bonded BlueZ devices under `DeviceCategory.Bluetooth`

`LinuxDeviceProvider` yields one `DeviceInfo` per bonded `org.bluez.Device1`, from one
`GetManagedObjects` call.

- **Bonded** means `Bonded=true` when the object carries `Bonded` (BlueZ 5.65 and later), and
  `Paired=true` when it does not. A pairing that was not bonded is not in the result, because it
  flips on every disconnection.
- **The BlueZ leg runs only when the filter could match a BlueZ device:** the category is unset,
  `All` or `Bluetooth`, and the filter has no `VendorId` or `ProductId` hint. D4 sets neither on a
  BlueZ device.
- **Objects that discovery created are not yielded.** They are scan results, and ADR-0085 assigns LE
  scanning to `Periphery.Ble.InTheHand`.

### D2 — A managed D-Bus client over the system bus socket

Core speaks the D-Bus wire protocol itself, over the BCL's `UnixDomainSocketEndPoint`. It binds no
libdbus and no libsystemd.

- **Address:** `DBUS_SYSTEM_BUS_ADDRESS` when it is a `unix:path=` address, otherwise
  `unix:path=/var/run/dbus/system_bus_socket`, the specification's well-known address.
- **Authentication:** a nul byte, then `AUTH EXTERNAL` with no identity, answered with an empty
  `DATA`. dbus-daemon then authenticates the socket's credentials (Source: dbus 1.14.10
  `dbus/dbus-auth.c:1135-1147`). No uid is needed. Then `BEGIN` and `Hello`.
- **No activation:** every message to BlueZ goes to the unique name that `GetNameOwner("org.bluez")`
  returned, with the `NO_AUTO_START` flag set. `GetNameOwner` never activates a service.
- **The codec is pure.** It turns bytes into messages and messages into bytes: both byte orders,
  alignment, and any type inside a variant, skipped by its signature. A malformed message, or one
  over the specification's 128 MiB limit, is an exception, which the shell routes into D3.
- **Deadlines:** every exchange has a deadline from an injected `TimeProvider`, 2 seconds. bluetoothd
  answered in under a millisecond. The caller's `CancellationToken` is honoured throughout.
- **One connection per consumer:** `EnumerateAsync` opens a connection, uses it, and closes it. The
  monitor owns one for its lifetime and touches it only from its own thread.

### D3 — Any failure in the BlueZ leg yields the udev results

Every failure in the BlueZ leg makes it yield nothing, and the udev results still come back. It
never throws for these failures. They include:

- no socket;
- failed authentication;
- `NameHasNoOwner`;
- `AccessDenied`, from BlueZ 5.50 and earlier, local policy, or a sandbox;
- `NoReply`;
- a passed deadline;
- a malformed reply;
- a dropped connection.

Logging is once per process:

- An expected absence (no socket, no owner) logs at Information.
- Any other error logs a warning once per error name.
- `AccessDenied` is latched for the life of the process, and the leg is not attempted again.

### D4 — What a BlueZ device maps to

| `DeviceInfo` | Value |
|---|---|
| `Id` | `bluez:<adapter address>/<device address>`, both in `BluetoothAddress`'s uppercase colon form. Example: `bluez:00:AA:01:00:00:00/00:AA:01:01:00:01` |
| `Name` | `Device1.Alias`, or null when the object has no `Name` and `Alias` is BlueZ's dashed-address fallback |
| `MacAddress` | `Device1.Address` |
| `Category`, `BusType` | `Bluetooth`, `Bluetooth` |
| `Status` | `OK` |
| `IsActive` | `Device1.Connected` |
| `LocationPath` | the object path |
| `ParentId` | null, as on every Linux node today (ADR-0010) |

The adapter address comes from the `Adapter1` object that `Device1.Adapter` names, in the same
reply.

**`Id` is not the object path, for two reasons.**
- The path embeds the adapter's kernel name, `/org/bluez/hci0/dev_…`. The kernel numbers `hciN` in
  registration order.
- The path keeps a private address after BlueZ has rewritten `Address` to the peer's identity
  address.

The pair of addresses is the key BlueZ stores the bond under, `/var/lib/bluetooth/<adapter>/<device>/`.
A peripheral bonded to two adapters is two bonds and yields two `DeviceInfo`s.

### D5 — Presence is the bond, activity is `Connected`

The core keys its state by object path, within one owner's lifetime, and derives `Id` from it.
Every edge is a transition against held state, so a signal that repeats a held value raises
nothing.

| Observation on an object | Edges |
|---|---|
| It becomes bonded (D1), by `InterfacesAdded` or `PropertiesChanged` | `Appeared`, then `Activated` if `Connected` |
| A bonded object is removed, or stops being bonded | `Disappeared` |
| `Connected` changes on a bonded object | `Activated` or `Deactivated` |
| `Address` changes on a bonded object | `Disappeared` for the old `Id`, then `Appeared`, and `Activated` if connected, for the new one |
| `Alias` or `Name` changes on a bonded object | `DevicePropertyChanged` |

Every provider raises `Appeared` and then `Activated` for a device that arrives connected
(`LinuxDeviceMonitorProvider.cs:276-282`). The watcher records activity only from `Activated`.

The core recomputes `Name` when `Address` changes, since BlueZ does not re-send the alias fallback.

When two bonded objects map to one `Id`, the core yields one of them: the connected one, else the
lower object path by ordinal comparison. BlueZ's merge of a duplicate identity leaves both objects
in place.

### D6 — Presence follows BlueZ across restarts and adapter removal

D5 applies to what BlueZ reports, with no hold rule.

- **A clean stop** of bluetoothd raises `Deactivated` for each connected bond, then `Disappeared`
  for every bond, as BlueZ removes them before releasing its name.
- **A start** raises `Appeared` for each bond as BlueZ re-adds it.
- **Removing an adapter** does the same for that adapter's bonds.
- **When `org.bluez` loses its owner** while the core still holds bonds, as after a crash, the core
  raises `Disappeared` for each one.

A tracker therefore sees a bond go away while bluetoothd is down or its adapter is absent. Windows
does the same when a radio is removed, because it enumerates present devices only
(`DevNodeHelper.cs:497-499`, `DIGCF_PRESENT`).

### D7 — Link nodes leave the result

With D5 carrying connection state on the device, a `link` node repeats it without an address. It is
also the node that raises `Appeared` per connection today. The provider skips udev devices with
`DEVTYPE=link`, in enumeration and in monitoring. Adapters stay.

Other devices sit under a link node in sysfs. None of them changes, because the Linux provider sets
no `ParentId` (ADR-0010) and categorises by subsystem. They are:
- kernel-HIDP HID devices and their boot-protocol input devices (BlueZ 5.73 moved BR/EDR HID to
  uhid by default);
- BNEP network interfaces;
- RFCOMM ttys. These move between the link and no parent on every connection, and the monitor
  ignores the `move` uevent. That is #304, and it is independent of this ADR.

### D8 — A pure core decides, and a thin shell does the I/O

**The core is pure:**
- the codec (D2);
- `Map(snapshot) → DeviceInfo[]` for D1 and D4;
- `Step(state, observation, now) → (state, edges, effects)` for D5–D8.

The state holds the owner's unique name, the pending request's serial and deadline, and the retry
schedule. Effects are "send this message", "close" and "wake me at time T".

**The shell** owns the socket, the poll, the `TimeProvider` and the events. The monitor's socket
descriptor joins the udev descriptor in `LinuxDeviceMonitorProvider`'s existing poll. When it is
readable the shell reads what is there and hands the bytes to the codec.

**Each connection runs one sequence.**

1. Connect, authenticate and send `Hello` (D2).
2. Add three match rules, once per connection:
   - `ObjectManager` signals with `sender='org.bluez'`;
   - `PropertiesChanged` with `sender='org.bluez'` and `arg0='org.bluez.Device1'`;
   - `NameOwnerChanged` with `sender='org.freedesktop.DBus'` and `arg0='org.bluez'`.

   A rule on `sender='org.bluez'` follows the name across owners. dbus-daemon keeps duplicate rules
   and caps them at 512 per connection, so re-adding rules on every restart would exhaust them.
3. `GetNameOwner("org.bluez")`. `NameHasNoOwner` means BlueZ is absent until a `NameOwnerChanged`
   gives it an owner.
4. `GetManagedObjects` to that unique name.

**The first snapshot seeds silently.** The monitor's `StartAsync` waits for it within the deadline,
as the udev monitor seeds its cache (`LinuxDeviceMonitorProvider.cs:95-96`). The watcher's own
enumeration then agrees with the monitor, and no bond is announced twice. A later snapshot, after
an owner change, is diffed against held state and raises edges.

**Between a `GetManagedObjects` and its reply, the core drops the owner's signals.** Such a signal
describes a change the reply already contains (§How BlueZ orders a reply). Signals after the reply
are applied against held state. BlueZ's late `Paired` signal then repeats a held value and raises
nothing.

**The core checks every message, because a signal sent directly to a connection bypasses match
rules.**
- `NameOwnerChanged` is accepted only from sender `org.freedesktop.DBus`, on path
  `/org/freedesktop/DBus`, with arg0 `org.bluez`.
- BlueZ signals are accepted only from the current owner's unique name, with the expected
  interface and member.
- A method return or error is accepted only when its reply serial is the pending request's, and it
  comes from that owner or from `org.freedesktop.DBus`. The bus itself sends `NoReply`,
  `AccessDenied` and `ServiceUnknown`.
- Anything else is dropped.

On the monitor, an error or a passed deadline marks BlueZ absent. The core then requests a new
snapshot after 1 s, doubling to a 1-minute ceiling, until one succeeds or the owner changes.
`AccessDenied` stops it for good (D3).

A `PropertiesChanged` that lists one of the properties the core reads as invalidated makes the core
request a new snapshot. BlueZ invalidates only properties whose `exists` callback is false, and none
of the ones read here has one (Source: `src/device.c` property table,
`gdbus/object.c:1693-1700`).

**Tests.**
- **The codec:** byte fixtures in both byte orders, including messages captured from BlueZ.
- **`Map` and `Step`:** tables, with the sequences in Context as fixtures. That covers pair,
  disconnect, clean stop, start, `SIGKILL`, adapter removal, the dropped pre-reply signal and the
  late `Paired`.
- **The shell:** a gated test under `PERIPHERY_LINUX_DEVICE_TESTS` on a host with `btvirt -L -l2`.
  It asserts that `bluez:00:AA:01:00:00:00/00:AA:01:01:00:01` enumerates, and that a connection
  raises `Activated` and then `Deactivated`. Its positive control is `bluetoothctl devices Bonded`
  listing the same address first. The test fails, not skips, when the fixture is missing
  (ADR-0089). ADR-0057's fixture list gains btvirt.

---

## Consequences

### Good

- `OfCategory(Bluetooth)` on Linux returns bonded peripherals with a name, an address and
  connection state, and a watcher gets presence and activity edges for them, as on Windows.
- No package and no native library is added. A host without a system bus pays a failed `connect`.
- A defect in the BlueZ leg costs the Bluetooth results for that query. It cannot crash the host or
  hold a query past the deadline.
- `WithMacAddress` selects a Bluetooth device on Linux, and on Windows once #301 lands.
- Linux Bluetooth needs no ADR-0090 source.

### Bad

- Periphery owns a D-Bus client: the SASL handshake, the message header, alignment, both byte
  orders and signature-driven parsing.
- A bluetoothd restart or an adapter removal takes every affected bond through `Disappeared` and
  `Appeared`.
- The leg runs for most watchers. `DeviceWatcher` passes an unfiltered filter whenever it has a
  tracker (`DeviceWatcher.cs:1141-1145`). Narrowing that is a watcher change, out of scope here.
- While any process is discovering, the `PropertiesChanged` rule delivers RSSI and advertising-data
  changes for every advertiser, which the core parses and ignores. The volume is unmeasured.
- An unfiltered `Devices.Enumerate()` opens a connection each call: connect, authenticate, `Hello`,
  `GetNameOwner`, `GetManagedObjects`.
- With BlueZ absent, D7 removes the link nodes and nothing replaces them.
- D7 changes what a Linux Bluetooth watcher receives. It needs a `BREAKING-CHANGES.md` entry when it
  ships.

### Neutral

- Transport is not decided. BlueZ 5.72's `Device1` has no bearer property. `AddressType=random`
  implies LE, and `public` does not decide it (#302).
- `Periphery.Ble.InTheHand` needs code before a Linux target can return. `BleJoin` requires a
  Windows instance id with LE transport, and D4's `Id` carries neither. `MacAddress` gives it the
  address that 32feet's BlueZ `BluetoothDevice.Id` is built from (exploration, §The 32feet Id).
  `InTheHand.BluetoothLE` 4.0.45 or later pins `Tmds.DBus` 0.95.1.
- Text that encodes the old Linux shape changes with the code:
  - `BluetoothAddress`'s remarks say `MacAddress` is always null for a Bluetooth node.
  - `Periphery.csproj`'s description names only libudev on Linux.

---

## Alternatives considered

**libdbus-1 through `LibraryImport`.** This ADR's first draft. libdbus is the reference
implementation and is present wherever bluetoothd runs. It was rejected on its failure modes:
- Its argument checks abort the process by default (Source: dbus 1.14.10
  `dbus/dbus-internals.c:196`, `fatal_warnings_on_check_failed = TRUE`, then `_dbus_abort()`). A
  binding bug would kill the host.
- `dbus_message_iter_get_basic` writes a value sized by its actual type into caller memory.
- Blocking calls take a timeout but no cancellation, and default to 25 s.
- Its documentation says not to poll its descriptor (`dbus/dbus-connection.c:5141`).

**sd-bus from `libsystemd.so.0`.** Its read API is simpler. Alpine and other distributions without
systemd do not ship it, while they do ship eudev's `libudev.so.1`.

**An integration package over `Tmds.DBus`.** #258's direction. It makes the meaning of a core
category depend on the consumer's package set, and ADR-0024 does not require it.

**The kernel's Bluetooth management socket.** An unprivileged socket may send only read-only info
commands (Source: `net/bluetooth/mgmt.c`, `mgmt_untrusted_commands`), and no management command
lists bonds. bluetoothd owns the key store.

**Read the bond store.** `/var/lib/bluetooth` is `0700 root`, it has no connection state, and its
format is BlueZ's private business.

**Hold presence across a bluetoothd restart.** The first draft's D6. BlueZ removes every device
before it releases its name, so each removal would need a barrier, such as a `Peer.Ping` to the
owner, to tell an unpair from a shutdown.

**Document Linux Bluetooth as adapters and links.** #258's interim. The README promises the same API
on every platform.

**Use the object path as `Id`.** See D4.

---

## Open questions

- **Re-pairing a privacy peripheral.** BlueZ rewrites `Address` to the identity address and keeps
  the object path (Source). That predicts D4's `Id` survives a re-pair, which Windows' does not
  (ADR-0083, #232). Unmeasured. It needs a real radio and the bench's privacy image.
- **Scale.** Reply size and parse cost with many bonds and cached GATT databases. Signal volume
  while other processes discover.
- **Sandboxes.** Snap and Flatpak callers most likely get `AccessDenied`, which D3 absorbs.
  Unverified.

---

## Related

- Issues: #258, #301, #302, #232, #294.
- [ADR-0004](0004-two-level-device-state-model.md): presence and activity.
- [ADR-0010](0010-udev-linux-provider.md): the Linux provider.
- [ADR-0024](0024-extension-package-pattern.md): the dependency table.
- [ADR-0052](0052-periphery-treehopper-pure-core.md): the pure core and effectful shell.
- [ADR-0057](0057-linux-extension-backends.md): the Linux device-test fixtures.
- [ADR-0085](0085-the-32feet-binding-is-two-integration-packages.md): LE scanning, and the Windows-only amendment.
- [ADR-0089](0089-tests-do-not-depend-on-elapsed-time.md): `TimeProvider`, and tests that fail when their fixture is missing.
- [ADR-0090](0090-supplementary-activity-sources.md): the source Linux Bluetooth no longer needs.
- [`docs/explorations/bluetooth-os-apis-2026-09.md`](../explorations/bluetooth-os-apis-2026-09.md).
