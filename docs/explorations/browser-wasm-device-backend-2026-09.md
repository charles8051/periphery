# A browser/WASM device backend

**Date:** 2026-09-17
**Status:** Exploration. Nothing here is a decision. The findings revise
[ADR-0007](../adr/0007-niche-platform-feasibility.md) §3, which surveyed this in 2025-07 without
running anything, and would feed a `Periphery.Browser` ADR if the work is taken up.
**Method:** A Blazor WebAssembly spike built for this document on .NET SDK 10.0.400, run in
Chromium 152, driving a live STM32G431 in system-bootloader mode over a CP210x bridge. The Web API
surfaces were read from a granted port at runtime rather than from the specification, where a grant
existed. The .NET runtime constraints are from primary sources. The spike is not checked in.
**Scope:** what an `IDeviceProvider` / `IDeviceMonitorProvider` backend can supply inside a browser,
and what the rest of core does when it gets one. Out of scope: non-Chromium browsers, WebRTC and
WebGPU device categories, and packaging.

## Evidence labels

| Label | Meaning |
|---|---|
| Measured | Observed for this document. |
| Source | Read in the source code of the named component. |
| Documented | Stated in the vendor's own documentation. |
| Inferred | Follows from the above. Not observed. |

---

## Findings, ranked

Ranked by how directly each one blocks a backend that core would accept.

| # | Finding | Evidence | Resolves with |
|---|---|---|---|
| 1 | Enumerate-all does not exist. Every Web device API returns only devices the user has already granted, one click per device. `Devices.FindAsync()` cannot mean what it means on a desktop. | Documented | Nothing. It is the browser's permission model. |
| 2 | There is no unsolicited appeared edge. An ungranted device is invisible, and a grant revoked in browser settings is withdrawn silently. `DeviceAppeared` can only ever fire from the backend's own request resolving. | Documented | Nothing. |
| 3 | Topology has no source. No parent, no port number, no hub chain. `ParentId`, `PortNumber`, `LocationPath` and `PortPath` (ADR-0078/0079/0080) cannot be populated, so the rooted forest cannot be built. | Documented | Nothing. |
| 4 | For a USB serial port, `getInfo()` returns VID and PID only. `SerialPortInfo` also defines `bluetoothServiceClassId`, which was absent on the measured device. Two identical bridges are indistinguishable either way, so `DeviceInfo.Id` has no durable source on this API. | Measured, Documented | Nothing keys it reliably. Object reference holds across two enumerations in one document and is unproven across a replug. |
| 5 | `EnrichmentPipeline.RunRegisteredSync` blocks on `GetAwaiter().GetResult()`. On the single WASM thread an enricher that awaits a JS promise deadlocks the app. | Source | Give the browser provider an async-only enrichment path, or register no async enricher. |
| 6 | `DeviceWatcher` offloads startup with `Task.Run` to spare a UI thread. WASM has no thread pool, so the offload is a no-op and the documented "events fire on thread-pool threads" contract stops holding. | Source | Document the browser exception, or gate the offload on `Environment.ProcessorCount`/OS check. |
| 7 | Driver state has no analogue: `Status`, `Driver`, `DriverVersion`, `ClassGuid`, `ClassName`, and everything `DeviceFaultClassifier` reads. | Documented | Nothing. The browser does not model a driver. |
| 8 | `ContainerId` has no analogue. A composite device's interfaces cannot be grouped. | Documented | Nothing. |
| 9 | Reset has no analogue beyond WebUSB's `reset()`. No port cycle, no forced re-enumerate. | Documented | Nothing for `ResetStrategy`'s power rung. |
| 10 | Grant-scoped enumeration maps onto `EnumerateAsync`, and physical arrival and departure map onto activated/deactivated. The two contract transitions land on separate sources: the grant is the device-tree analogue, `connect`/`disconnect` is presence. | Measured, Documented | Already answered. See [Tier 1](#tier-1---grant-scoped-enumeration-and-lifecycle). |
| 11 | An embedded Chromium without a port chooser has `navigator.serial` and still cannot grant. `requestPort()` rejects `NotFoundError` with a user gesture present. | Measured | Nothing in code. A chooser is the host browser's to provide. |
| 12 | JS interop cannot cross a thread even with multithreading enabled, and .NET 11 does not change this. | Documented | Not before .NET 12, and not promised there. See [Runtime timeline](#runtime-timeline). |

---

## The contract a backend must satisfy

Smaller than the platform code behind it suggests. From
[`IDeviceProvider.cs`](../../src/Periphery/IDeviceProvider.cs):

```csharp
public interface IDeviceProvider
{
    IAsyncEnumerable<DeviceInfo> EnumerateAsync(DeviceFilter filter, CancellationToken ct = default);
}

public interface IDeviceMonitorProvider : IAsyncDisposable
{
    Task StartAsync(DeviceFilter filter, CancellationToken ct = default);
    event EventHandler<DeviceChangeEventArgs>? DeviceAppeared;
    event EventHandler<DeviceChangeEventArgs>? DeviceDisappeared;
    event EventHandler<DeviceChangeEventArgs>? DeviceActivated;
    event EventHandler<DeviceChangeEventArgs>? DeviceDeactivated;
    event EventHandler<DeviceModificationEventArgs>? DevicePropertyChanged;
}
```

Neither requires P/Invoke. `DeviceProviderFactory` throws `PlatformNotSupportedException` when no OS
matches, which is the insertion point a browser backend would take.

The layer above is provider-agnostic by construction. `IDeviceProvider`'s own remarks state that a
provider may narrow the OS query as a performance hint but that `DeviceFilter.Matches` is always
re-evaluated in memory by the caller, so "correctness never depends on provider cooperation". A
filter therefore keeps working against a `DeviceInfo` with most fields null. (Source.)

---

## Measured: the .NET flashing stack already runs in WASM

This was the first question, and it is settled. A stock `dotnet new blazorwasm` referencing
`Periphery.Bootloader.Stm32.Serial` and `Periphery.Firmware` builds and publishes. The published
`_framework` payload carries the protocol stack and nothing else from the serial family:

```
CallAndResponse.wasm
CallAndResponse.Protocol.Stm32Bootloader.wasm
Periphery.Bootloader.Stm32.Serial.wasm
Periphery.wasm
System.IO.Pipelines.wasm
```

`Periphery.Serial` and `System.IO.Ports` are absent. `Stm32SerialProgrammer`'s public constructor
and `ConnectAsync` both take a caller-owned `IDuplexPipe` and explicitly do not own the transport
beneath it, so supplying the pipe from elsewhere lets the linker drop the whole BCL serial path.
(Measured.)

Against a live STM32G431 the browser reported:

```
SYNC OK - the part answered 0x7F
family=STM32 chip=0x468 bootloader=3.1 transfer=256
commands=Get, GetVersion, GetId, ReadMemory, Go, WriteMemory, ExtendedEraseMemory,
         WriteProtect, WriteUnprotect, ReadoutProtect, ReadoutUnprotect
```

identical to the desktop control run over `BclSerialDuplexPipe` on the same part. (Measured, both.)

Bootloader entry came from the control lines: DTR carries BOOT0, RTS carries NRST, both asserted by
`SerialPort.DtrEnable`/`RtsEnable` set true. Hold reset with BOOT0 high, release, and the part
samples BOOT0 on the rising edge. Web Serial reaches the same lines through
`port.setSignals({dataTerminalReady, requestToSend})`. (Measured on both transports.)

Two consequences for a core backend:

- Single-threaded WASM did not deadlock the CallAndResponse transceiver across a full sync, Get and
  Get ID exchange. The framing layer is await-clean. (Measured.)
- Nothing in that path touches `IDeviceProvider`. The flashing case never wanted enumerate-all; it
  wanted one port the user picked, which is the one thing the browser is good at. (Inferred.)

---

## Tier 1 - grant-scoped enumeration and lifecycle

Read from the granted port at runtime:

```json
{
  "serialGrants": 1,
  "serialInfos": [{ "usbVendorId": 4292, "usbProductId": 60000 }],
  "serialOwnKeys": ["onconnect", "ondisconnect", "readable", "writable", "close",
                    "forget", "getInfo", "getSignals", "open", "setSignals", "connected"],
  "containerEvents": [true, true]
}
```

`4292` is `0x10C4` and `60000` is `0xEA60`. `port.connected` read `true` while `getSignals()` threw
`InvalidStateError` on the same port, in a document that had never opened it. `connected` therefore
tracks the port's link to its device, not whether script has called `open()`. (Measured.)

`connect` and `disconnect` exist in two places. Both `navigator.serial` and `SerialPort` carry
`onconnect` and `ondisconnect`, and `SerialPort` carries `connected`. (Measured on
`SerialPort.prototype`; Documented on MDN.) A backend listens on the container, because a port it
has never seen has no object to attach a handler to.

The contract keeps two transitions orthogonal: appeared and disappeared mean the device entered or
left the OS device tree, activated and deactivated mean it became physically active or inactive. The
browser does not supply both from one source, and an earlier draft of this table wrongly mapped the
same event pair to both rows.

| Contract member | Browser source | Note |
|---|---|---|
| `EnumerateAsync` | `navigator.{serial.getPorts, usb.getDevices, hid.getDevices, bluetooth.getDevices}()` | granted devices only |
| `DeviceAppeared` | a grant arriving, which is only ever the backend's own `requestPort()` / `requestDevice()` resolving | no event exists; the browser never announces a grant |
| `DeviceDisappeared` | `forget()` | the backend's own call. A grant revoked through browser settings is withdrawn silently |
| `DeviceActivated` / `DeviceDeactivated` | `connect` / `disconnect` | physical arrival and departure of a granted device |
| `IsActive` | `port.connected` | independent of open state (Measured) |
| `DevicePropertyChanged` | no source | a granted device's descriptor does not change under you |

The grant is the browser's analogue of the OS device tree, so the two transitions do land on
distinct sources. What no browser API offers is an *unsolicited* appeared edge: a device the user has
never granted is invisible, and a revocation raises nothing.

Whether a live plug-in actually dispatches the container event was not observed here. Only the
handler surface was. The Windows provider raises no live arrival at all, its `Appeared` firing only
from the startup snapshot, so if the browser does dispatch, a browser backend would be the first one
where arrival is a push edge. (Documented, with the dispatch unmeasured; Source for Windows.)

---

## Tier 2 - `DeviceInfo` fidelity, by API

| `DeviceInfo` field | WebUSB | WebHID | Web Serial | Web Bluetooth |
|---|---|---|---|---|
| `Id` | `serialNumber` when present | synthesized | none | opaque `id` |
| `Name` | `productName` | `productName` | none | `name` |
| `Manufacturer` | `manufacturerName` | none | none | none |
| `VendorId` / `ProductId` | yes | yes | yes | none |
| `SerialNumber` | yes, when the device provides one | none | none | none |
| `UsbClassCode` | `deviceClass` | n/a | none | n/a |
| `HidUsagePage` / `HidUsage` | n/a | `collections` | n/a | n/a |
| `HidMax*ReportLength` | n/a | `collections` | n/a | n/a |
| `PortName` | n/a | n/a | none | n/a |
| `IsActive` | `opened` | `opened` | `connected` | `gatt.connected` |
| `BusType` | USB, constant | USB, constant | unknown | Bluetooth, constant |

The `IsActive` row is not one concept across the four. Web Serial's `connected` is device presence,
measured true on a port this document never opened. WebUSB's and WebHID's `opened` is whether script
holds the device open, which is a different axis and closer to nothing in the contract. A backend
would have to derive `IsActive` per API rather than reading one property name.

Web Serial is the thinnest of the four and it is the one the flashing path uses. `SerialPortInfo`
also defines `bluetoothServiceClassId`, for a port backed by a Bluetooth RFCOMM service rather than
USB; `getInfo()` on the measured USB port returned the two USB keys and nothing else, so the
"nothing else" here is about that device, not about the API. (Measured for the port; Documented for
the dictionary.)

The WebUSB and WebHID columns are Documented, not Measured: the profile carried zero USB and zero
HID grants, so neither could be sampled. The Web Serial column is Measured.

`MediaDevices.enumerateDevices()` is a fifth source, covering cameras and microphones with a
`devicechange` event, and is the only one with no user-gesture gate for the device list itself.
Labels require permission. (Documented.)

---

## Identity

`DeviceInfo.Id` is `required`, and `DeviceId` documents it as stable across a disconnect and
reconnect of the same device. Three cases:

- **WebUSB.** `serialNumber` when the device provides one. A device that ships a blank or duplicated
  serial descriptor gives a colliding id, the same hazard the desktop providers already have.
  (Documented.)
- **Web Serial.** Nothing usable. Two identical bridges return the same `getInfo()`. (Measured.)

Object reference is the obvious next candidate, and it does not survive scrutiny as a resolution.
Two `getPorts()` calls in one document returned the identical `SerialPort` object, `a[0] === b[0]`.
(Measured.) That is the whole of the evidence. It says nothing about the three other APIs, and
nothing about an unplug and replug, which was never performed. No Web API documents wrapper-object
identity as a durable device identifier across reconnection, so a backend keyed this way can report
a replugged device as a new one.

The honest position is that a browser backend cannot satisfy `DeviceId`'s documented invariant.
Whatever it synthesizes is at best stable within a document and is gone on reload, so a caller that
persists an association against an id will lose it. An ADR has to either narrow the contract or let
the browser provider declare the exception; it cannot rely on object identity to paper over the gap.
Both experiments that would move this are in the closing table.

---

## Two blockers above the provider

Both are in shared core code rather than in any backend, and both are specific to having one thread.

[`EnrichmentPipeline.cs:80`](../../src/Periphery/EnrichmentPipeline.cs#L80):

```csharp
device = task.IsCompletedSuccessfully ? task.Result : task.GetAwaiter().GetResult();
```

The surrounding remarks already name the constraint: the sync path is "safe for the current crop of
enrichers, which are dictionary-lookup-sync and return completed tasks", and "a future
genuinely-async enricher would block the calling thread". On a desktop that is a stall. On the
single WASM thread it is a deadlock, because the promise it is waiting for can only be resolved by
the event loop it is blocking. (Source.)

[`DeviceWatcher.cs:971`](../../src/Periphery/DeviceWatcher.cs#L971) wraps provider registration and
the initial snapshot in `Task.Run`, with a comment explaining that the work is synchronous OS work
and the offload keeps a latency-sensitive caller free. WASM has no thread pool, so the continuation
runs on the same thread it was trying to spare. Not a deadlock; the comment's closing promise, that
this "honours the watcher's contract that events fire on thread-pool threads", simply stops being
true in a browser. (Source.)

---

## The interop shape a transport must use

Reusable beyond this exploration, and it cost several iterations to find.

`[JSImport]` will not marshal an array inside a promise: `Task<byte[]>` and `Task<int[]>` returned as
`JSType.Promise<JSType.Array<JSType.Number>>` are both rejected by the source generator (SYSLIB1072).
`JSType.MemoryView` is the zero-copy option but it pins managed memory and therefore cannot span an
`await`. A read is consequently two calls: await the chunk and report its length, then copy it
synchronously.

```csharp
[JSImport("readBegin", Module)]
internal static partial Task<int> ReadBeginAsync();

[JSImport("readEnd", Module)]
internal static partial void ReadEnd([JSMarshalAs<JSType.MemoryView>] Span<byte> destination);
```

```js
let pending = null;
export async function readBegin() { pending = await backend.read(); return pending.length; }
export function readEnd(destination) { if (pending?.length) destination.set(pending); pending = null; }
```

`MemoryView.set` asserts a typed array, so the JS side must hand back a `Uint8Array`, never a plain
number array. (Measured: the assertion failure reads `Expected function Uint8Array()`.)

The resulting pipe needs no dedicated thread, unlike
[`BclSerialDuplexPipe`](../../src/Periphery.Serial/BclSerialDuplexPipe.cs) and its RJCP sibling, both of
which run a synchronous read pump on their own thread because a synchronous driver read is the only
one that honours a timeout on Windows. `reader.read()` is already a promise, so the browser pump is
an await loop, and cancellation is observed on the pipe rather than inside the read. (Inferred from
the measured spike.)

---

## Runtime timeline

.NET 11 is at RC1 with GA expected November 2026. It carries large WebAssembly changes and none of
them lift the constraints above.

What lands: CoreCLR replaces Mono on WebAssembly as a preview, running an interpreter plus
ReadyToRun with RyuJIT for AOT codegen, reaching an end-to-end libraries test suite run in Preview 7
and not intended for general use; a `blazorwebworker` project template with `InvokeVoidAsync`,
cancellation and timeout; `IHostedService` in the browser; environment variables through
`IConfiguration`; a new `Microsoft.AspNetCore.Components.Gateway` dev server; smaller publish output.
(Documented.)

What does not change: `src/mono/wasm/features.md` on `main` still states that JS interop via
`[JSImport]`/`[JSExport]` is limited to the main thread even when multithreading is enabled, that
blocking the main thread with `Task.Wait` or `Monitor.Enter` is unsupported and dangerous, and that
"the work on the proper design for this is still in progress". Multithreading remains experimental
and off by default behind `WasmEnableThreads`. (Documented.)

So findings 4 and 5 survive .NET 11 unchanged, and enabling threads does not help finding 4, because
the JS call a browser enricher would make cannot leave the main thread regardless. The Web Worker
template is a separate .NET instance in a worker communicating by message, which moves CPU work off
the UI thread without giving the interop path shared-thread concurrency.

Nothing in the Preview 7 runtime notes or the .NET 11 overview addresses JS interop marshalling
either way, so the two-call read pattern above is recorded as current rather than as permanent.

The next checkpoint is CoreCLR-on-WASM stabilising, targeted at .NET 12. Whether it brings
cross-thread interop is not promised anywhere read for this document.

Sources: [dotnet/runtime `src/mono/wasm/features.md`](https://github.com/dotnet/runtime/blob/main/src/mono/wasm/features.md),
[What's new in .NET 11](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/overview),
[What's new in ASP.NET Core in .NET 11](https://learn.microsoft.com/en-us/aspnet/core/release-notes/aspnetcore-11),
[.NET 11 Preview 7 runtime notes](https://github.com/dotnet/core/blob/main/release-notes/11.0/preview/preview7/runtime.md).

---

## The shape this would take

Not a decision. Recorded so an ADR has something to argue with.

`Periphery.Browser`, targeting `net10.0-browser`, implementing `IDeviceProvider` and
`IDeviceMonitorProvider` over Web Serial, WebUSB, WebHID and Web Bluetooth, plus one method the
interfaces do not have:

```csharp
Task<DeviceInfo> RequestAsync(BrowserDeviceRequest request, CancellationToken ct = default);
```

callable only from a user gesture. ADR-0007 §3.5 proposed this same shape and called the result
"technically possible, but philosophically misaligned" with Periphery's discovery model. The
measurements here do not overturn that. They narrow it: the misalignment is entirely in
enumerate-all and topology, and a consumer that wants a device the user just pointed at meets none
of it.

Whether that consumer is worth a package is the question an ADR would answer, and this document does
not.

---

## What would resolve the open questions

| Question | Experiment |
|---|---|
| Is WebUSB's `DeviceInfo` fidelity as good as the spec suggests? | Grant one composite USB device, then read `vendorId`, `productId`, `serialNumber`, `manufacturerName`, `productName`, `deviceClass` and the configuration descriptors. |
| Does WebHID fill the four HID fields? | Grant one HID device and read `collections` against `HidUsagePage`, `HidUsage` and the three report lengths. |
| Do `DeviceTracker` and `DeviceWatcher` run clean on one thread? | Drive them from a fake `IDeviceProvider` inside a Blazor WASM host. Neither was exercised by the flashing spike. |
| Does finding 4 actually deadlock, rather than merely stall? | Register an enricher that awaits a `[JSImport]` promise, enumerate, and observe. |
| Is the two-call read still required on .NET 11? | Rebuild the spike against the .NET 11 SDK and restore the `Task<byte[]>` signature. |
| Does a grant survive a browser restart? | Reload after restarting Chromium and call `getPorts()`. |
| Does the same JS object come back after a physical replug? | Hold the `SerialPort` from `getPorts()`, unplug the bridge, replug it, call `getPorts()` again and compare by reference. Repeat on WebUSB and WebHID, which were never sampled. |
| Does a live plug-in actually dispatch the container `connect` event? | Register `navigator.serial.onconnect`, then plug in a granted bridge. Only the handler surface was observed here, never a dispatch. |
| Does a grant revoked in browser settings raise anything? | Revoke the site's serial permission from Chromium settings with the page open and watch for any event. |
