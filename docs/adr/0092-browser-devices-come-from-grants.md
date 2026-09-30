---
title: "ADR-0092: In a browser, Periphery opens the device the user picked, over WebUSB and Web Serial, and does not enumerate"
status: "Proposed"
status_note: "No code. Drafted 2026-09-30 and revised the same day after five reviews and a source check of the WebUSB claims. The review narrowed it: the first draft also put an IDeviceProvider and monitor over browser grants, which is now deferred (see Deferred). Chromium and Web Serial facts are Source (Chromium and spec text) unless labelled. Nothing WebUSB-specific is Measured yet; the gates under Verification say what would make it so."
date: "2026-09-30"
authors: "@charles8051"
tags: ["architecture", "decision", "browser", "wasm", "webusb", "web-serial", "usb", "transport", "identity", "pure-core"]
supersedes: ""
superseded_by: ""
depends_on: ["0004-two-level-device-state-model.md", "0007-niche-platform-feasibility.md", "0024-extension-package-pattern.md", "0038-periphery-usb.md", "0039-periphery-treehopper.md", "0060-device-reset-and-recovery-escalation.md", "0061-firmware-flashing-platform.md", "0062-periphery-serial-backend-provider.md", "0065-camera-testing-seam.md", "0069-restore-net8-tfm-untested.md", "0078-device-topology-is-a-rooted-forest.md", "0083-ble-identity-does-not-survive-repairing.md", "0085-the-32feet-binding-is-two-integration-packages.md", "0089-tests-do-not-depend-on-elapsed-time.md"]
---

# ADR-0092: In a browser, Periphery opens the device the user picked, over WebUSB and Web Serial, and does not enumerate

> Number `0092` is provisional until merge (the next free number after ADR-0091), per this repo's
> "assign the number at merge" convention.

**Tracks:** two new packages, `Periphery.Usb.Browser` and `Periphery.Serial.Browser`; one internal
backend seam in `Periphery.Usb`. Revises [ADR-0007](0007-niche-platform-feasibility.md) §3. Answers
the scope question in nordic-dfu OQ-10 for WebUSB and Web Serial only.

---

## Status

Proposed 2026-09-30. No code.

---

## Context

### What is already known

[`browser-wasm-device-backend-2026-09.md`](../explorations/browser-wasm-device-backend-2026-09.md)
ran a Blazor WebAssembly spike on .NET SDK 10.0.400 in Chromium 152. Its evidence labels
(Measured, Source, Documented, Inferred) are used unchanged here. It established:

- `Periphery.Bootloader.Stm32.Serial` runs in WASM and synced a live STM32G431 over Web Serial,
  with output identical to the desktop run. The programmer takes a caller-owned `IDuplexPipe`, so the
  linker drops `System.IO.Ports`. (Measured.)
- Every Web device API returns only granted devices. There is no enumerate-all. (Documented.)
- Topology, driver state and `ContainerId` have no browser source. (Documented.)
- JS interop cannot leave the main thread, and .NET 11 does not change that. (Documented.)

It closed on one question: whether a consumer that wants the device the user just picked is worth a
package. ADR-0007 §3.5 proposed a `Periphery.Browser` package and called forcing the
`IDeviceProvider` contract onto a browser "philosophically misaligned".

### The consumer

No page in this repo consumes a browser backend yet. The cases the packages serve are these:

- Drive a vendor-class USB board, such as a Treehopper, from a page with no install.
- Flash an STM32 from a page, over its USART bootloader (Web Serial, already Measured) or its USB
  DFU bootloader (WebUSB). DFU is interface class `0xFE`, which WebUSB does not block. (Source.)

[ADR-0061](0061-firmware-flashing-platform.md) does not mention a browser. The flashing case comes
from nordic-dfu OQ-10, which asked whether browser flashing is in scope for this repo. This ADR
answers yes for WebUSB and Web Serial. Web Bluetooth, which OQ-10 is about, stays its own decision.

The Treehopper's own firmware update is not in this slice. Its EFM8 bootloader is `10C4:EAC9`, a HID
device reached through `Periphery.Hid` (`Efm8HidProgrammer`), and WebUSB refuses HID interfaces.
That needs WebHID.

Both cases want one device the user picked, then its transfers. Neither wants enumerate-all.

### What WebUSB gives a vendor-class device

The Treehopper is the worked device. Its firmware is in this repo. Its interface is class `0xFF`
(`descriptors.c:85`) with four 64-byte bulk endpoints: `0x81`, `0x82`, `0x01`, `0x02`. It answers the
Microsoft OS 1.0 string request (`0xEE`) and the extended compat ID request (`callback.c:150`), so
Windows binds WinUSB without an `.inf`. Its VID/PID is `10C4:8A7E`. (Source.)

What Chromium does, from the WebUSB spec and Chromium source:

| Fact | Consequence |
|---|---|
| `claimInterface()` rejects seven protected classes: audio, HID, mass storage, smart card, video, audio/video, wireless controller. `0xFF` is not restricted. | The Treehopper's interface is claimable. |
| The static blocklist (`usb_blocklist.cc`) has one `10C4` entry, `10c4:8acf`. Entries are exact VID/PID. Entries can also arrive by server-side config. | `10C4:8A7E` is not on the static list. |
| On Windows, `open()` requires the device's driver to be WinUSB, or each function of a composite device to be WinUSB. | The Treehopper qualifies. A device without MS OS descriptors needs a driver bound first. |
| Every `USBDevice` method returns a promise. | No synchronous open, claim or transfer. |
| There is no per-transfer cancel, `AbortSignal` or timeout. The proposal, whatwg/usb#25, has been open since 2015. `close()` rejects every pending transfer with `AbortError`. On Windows, `reset()` and `selectConfiguration()` are not implemented and fail with `NetworkError`. | Cancelling one transfer means closing the device. |
| `getDevices()` lists devices that are granted and attached. A replug produces a new `USBDevice` object. | The JS object is not an identity. |
| A grant is persisted, keyed on VID, PID and serial number, only when the device reports a serial. A device without one has its grant dropped on unplug, and replugging it fires no `connect`. | Identity is durable only with a serial. |
| A grant revoked from site settings raises no event. Chromium closes the device, and pending and later calls reject with `NotFoundError`. | Revocation surfaces as a failing transfer. |
| `controlTransferIn` `GET_DESCRIPTOR` to the device needs no claimed interface, only an open and configured device. Since M150, standard OUT requests to the device (`SET_CONFIGURATION`, `SET_ADDRESS` and others) reject with `SecurityError`. | Raw descriptors are readable. |
| `navigator.usb` is exposed in dedicated workers (Chrome 70). `requestDevice()` is window-only. | A worker can find a granted device with `getDevices()`. |
| A second claim of an interface another process holds rejects with `NetworkError`. On Windows, `open()` succeeds and the failure arrives at `claimInterface()`. | Exclusivity is the OS's. |

Web Serial behaves the same way on attachment. `getPorts()` lists only available ports, and a wired
port is available only while connected. A replug creates a new port with a new token. (Source.)

### Where the browser meets core

All Source.

- `DeviceProviderFactory` (`IDeviceProvider.cs:87`) dispatches on `OperatingSystem.Is*` and throws
  `PlatformNotSupportedException` for anything else. `Devices.*` goes through it.
- `UsbDevice.OpenAsync` (`UsbDevice.cs:218`) dispatches the same way to `WinUsbBackend` or
  `LibUsbBackend`. `TreehopperBoard.OpenAsync(DeviceInfo)` and `Stm32DfuProgrammer` call it.
- `IUsbBackend` is `internal`. `UsbDevice` documents interface 0 as claimed at open, and
  `WinUsbBackend` supports interface 0 only (`WinUsbBackend.cs:151-167`).
- `UsbDevice` builds the transfer deadline per call from a `TimeProvider` and cancels the token it
  passes to the backend (`UsbDevice.cs:284-291`).
- `CameraDevice.BackendFactory` (`CameraDevice.cs:279`, ADR-0065) is an internal, settable backend
  hook that a separate package fills.

---

## Decision

### D1 — The browser path opens a picked device, and there is no browser device provider

The browser packages give a caller a device the user granted, and the transports to use it. They do
not implement `IDeviceProvider` or `IDeviceMonitorProvider`. `DeviceProviderFactory` gets no browser
branch, and `Devices.*` keeps throwing `PlatformNotSupportedException` in a browser. That exception is
the correct answer to "enumerate the machine's devices" in a page.

What works in a browser after this ADR, and what throws:

| Works | Throws `PlatformNotSupportedException` |
|---|---|
| `UsbDevice.OpenAsync(DeviceInfo)` for a granted device | `Devices.*` |
| `TreehopperBoard.OpenAsync(DeviceInfo)` | `TreehopperBoard.EnumerateAsync`, `OpenFirstAsync` |
| `Stm32DfuProgrammer` over a granted DFU device | `DeviceSessionHost.ForDeviceAsync`, `StartAsync`; `DeviceProxy.OpenAsync` |
| `Stm32SerialProgrammer` over the Web Serial pipe | the FlashAnything pipeline, whose serial provider needs a `PortName` (`Stm32SerialBootloaderProvider.cs:55`) |

The device provider is deferred, not rejected. See Deferred.

### D2 — Two packages, one per transport

```
Periphery.Usb.Browser     ->  Periphery.Usb (internals, exact pin)   WebUSB request, grant registry, IUsbBackend
Periphery.Serial.Browser  ->  System.IO.Pipelines                    Web Serial request, IDuplexPipe, control lines
```

`Periphery.Usb.Browser` is ADR-0024's `{Domain}.{Platform}` row. It implements the `internal`
`IUsbBackend`, so it lives in this repo (Q1 of
[`integration-package-placement.md`](../patterns/integration-package-placement.md)). `Periphery.Usb`
adds an `InternalsVisibleTo` for it, and it packs an exact `[x.y.z]` dependency on `Periphery.Usb`,
as `Periphery.Camera.Testing` does.

`Periphery.Serial.Browser` takes no dependency on `Periphery.Serial`. That package is one BCL pipe
over `System.IO.Ports` and shares no code with a Web Serial pipe. A page that flashes over Web Serial
takes neither `Periphery.Usb` nor `System.IO.Ports`. The name follows ADR-0062's `Periphery.Serial.*`
family.

Neither package touches `Periphery` core internals.

### D3 — Both packages target `net10.0` and declare the browser platform

Each targets `net10.0` with `<SupportedPlatform Include="browser" />` and
`[SupportedOSPlatform("browser")]` on the shell types. A desktop caller gets CA1416 at compile time
and `PlatformNotSupportedException` at run time.

`net10.0-browser` was the first draft's choice, and a review measured it fail. A stock
`dotnet new blazorwasm` project targets `net10.0` (RID `browser-wasm`), and restoring a
`net10.0-browser`-only package there fails with NU1202. A `net10.0` test project cannot reference it
either. (Measured by the review, SDK 10.0.401.)

`net8.0` is left out, as ADR-0085 D3 did for its package. The spike ran on .NET 10 only, and .NET 8
leaves support on 2026-11-10. ADR-0069's default is unchanged elsewhere.

Each project uses `Microsoft.NET.Sdk.Razor`, which packs `wwwroot/` as a static web asset served at
`_content/<PackageId>/`. Plain `Microsoft.NET.Sdk` packs only `lib/`. (Measured by the review.)

### D4 — A device enters by request, and each package is initialised first

```csharp
public static class BrowserUsb
{
    public static Task InitializeAsync(CancellationToken ct = default);
    public static Task<DeviceInfo?> RequestDeviceAsync(
        IReadOnlyList<UsbDeviceRequestFilter> filters, CancellationToken ct = default);
    public static Task<IReadOnlyList<DeviceInfo>> GetGrantedDevicesAsync(CancellationToken ct = default);
}

public static class BrowserSerial
{
    public static Task InitializeAsync(CancellationToken ct = default);
    public static Task<BrowserSerialPort?> RequestPortAsync(
        IReadOnlyList<SerialPortRequestFilter> filters, CancellationToken ct = default);
}
```

`InitializeAsync` imports the package's JS module and, for USB, installs the backend hook (D6). It
runs at startup. Chromium's transient activation lasts about 5 seconds, so the module import must
not sit between a click and the request.

`RequestDeviceAsync` and `RequestPortAsync` must run under transient activation, for example from
a Blazor `@onclick` handler. Without it they throw `InvalidOperationException` carrying the browser's
`SecurityError`. They return `null` on `NotFoundError`, which means the user dismissed the chooser
or the host has no chooser (exploration finding 11). The two cannot be told apart, so neither is
reported as cancellation. Nothing retries a request.

`GetGrantedDevicesAsync` wraps `getDevices()`. It is how a reloaded page finds a grant it already
has without a click.

`BrowserSerialPort` exposes an `IDuplexPipe`, `UsbVendorId` and `UsbProductId` (nullable, since a
port can be Bluetooth RFCOMM), and `SetSignalsAsync(dtr, rts)` for bootloader entry. It carries no
`DeviceInfo` and no id. Its consumer holds the port object.

### D5 — A WebUSB `DeviceInfo.Id` has two forms, and a document-wide registry mints them

| Case | `Id` | Stable across |
|---|---|---|
| Serial number present, unique among attached grants | `webusb:VVVV:PPPP:<serial>` | replug, reload and restart, while the grant lasts |
| No serial number, or two attached devices sharing VID, PID and serial | `webusb-session:VVVV:PPPP:<n>` | the current attachment only |

`VVVV` and `PPPP` are uppercase hex. The serial comparison is `OrdinalIgnoreCase`, matching
`DeviceId`.

The first form uses the three facts Chromium keys a persisted grant on. It is the platform's
identity, not one Periphery composed. It names a serial number, not a physical unit: two units that
share a serial and are never attached together get the same id, as they do in a Windows USB instance
ID and in Chromium's grant. When two attached devices share it, both get session ids. The
first draft gave the durable id to whichever enumerated first, which after a reload can name the
other unit. That is the fingerprint-as-fact problem [ADR-0083](0083-ble-identity-does-not-survive-repairing.md)
D1 rules out.

The session form survives nothing, because Chromium drops a serial-less grant on unplug. The
`-session` prefix is how a consumer tells the forms apart.

One registry per document owns the grants. It holds the id-to-`USBDevice` map and the counter behind
`<n>`, and drops an entry on `disconnect`. Minting is a pure function in `Periphery.Usb.Browser.Core`:

```csharp
(UsbGrantRegistry next, DeviceId id) Admit(UsbGrantRegistry state, UsbGrantFacts facts, GrantHandle handle);
```

`GrantHandle` is an opaque token the shell assigns to each JS object. The shell holds the JS
objects. The pure core never sees one.

The `DeviceInfo` projection is pure too. It fills `Id`, `Name` (`productName`), `Manufacturer`,
`VendorId`, `ProductId`, `SerialNumber`, `UsbClassCode` and `BusType.USB`, and leaves everything else
at its default. `Status` is `DeviceStatus.Unknown`.

### D6 — `UsbDevice.OpenAsync` reaches the browser through an internal hook

`Periphery.Usb` gains a hook of the `CameraDevice.BackendFactory` shape:

```csharp
internal static class UsbBackendHost
{
    internal static Func<DeviceInfo, CancellationToken, Task<IUsbBackend>>? Browser { get; set; }
}
```

`UsbDevice.OpenAsync` adds a branch ahead of its `PlatformNotSupportedException`. In a browser with
the hook set, it opens through the hook. With the hook unset, the exception names
`BrowserUsb.InitializeAsync`. An id that is not in the registry throws `UsbDeviceNotFoundException`.

The WebUSB backend:

- awaits `open()`. If no configuration is active, it calls `selectConfiguration` with the
  `configurationValue` of the device's first configuration, which need not be 1. That call is not
  implemented on Windows, where the OS configures the device, so on Windows a device with no active
  configuration fails to open;
- claims interface 0 at open. `ClaimInterface(0)` and `ReleaseInterface(0)` are no-ops until
  dispose, and any other interface throws `NotSupportedException`, all as in `WinUsbBackend`;
- reads the device and configuration descriptors with `GET_DESCRIPTOR` and parses them with the
  existing `UsbDescriptors` parsers;
- maps the three primitives to `controlTransferIn`/`controlTransferOut` and
  `transferIn`/`transferOut`, under D7's rules;
- maps every outcome to Periphery's exceptions, never letting a `JSException` escape:

| WebUSB outcome | Surfaces as |
|---|---|
| `NotFoundError` (disconnected or revoked) | `UsbDeviceRemovedException` |
| resolved with status `stall` | `UsbTransferException`, after `clearHalt` on that endpoint |
| resolved with status `babble` | `UsbTransferException` |
| `NetworkError` | `UsbTransferException` (on claim: `UsbException` naming another process) |
| `InvalidStateError`, `SecurityError` | `UsbException` |
| `AbortError` from the backend's own `close()` | `ObjectDisposedException` |

`stall` and `babble` resolve the promise rather than reject it, so a mapping that looked only at
rejections would report them as success. `TreehopperBoard` depends on `UsbException` in four places
(`TreehopperBoard.cs:784,838,899,949`).

### D7 — Cancelling a transfer abandons it, and each endpoint follows a pure state machine

Closing the device is the only way to abort a pending WebUSB transfer (Context). Closing on a
cancelled command read would kill the Treehopper's pin-report read on another endpoint. So
cancellation and timeout complete the caller's task with `OperationCanceledException` or
`UsbTimeoutException` and leave the JS transfer running. The deadline stays per call, so a read that
takes over an abandoned transfer gets its own full window.

Each endpoint follows a state machine in `Periphery.Usb.Browser.Core`, as a pure
`(EndpointState, EndpointInput) -> (EndpointState, EndpointOutput)` function. The shell owns the
promises.

Bulk and interrupt IN:

1. Every `transferIn` is issued with the caller's length rounded up to the endpoint's
   `wMaxPacketSize`. A transfer that ends on a packet boundary cannot babble.
2. At most one `transferIn` is outstanding per endpoint. A read that finds an abandoned one takes it
   over instead of issuing another. A second concurrent `transferIn` would take the next packet out
   of order.
3. Bytes that arrive beyond the reading caller's buffer are kept in the endpoint's queue. The next
   read drains the queue before it issues a transfer. Rules 1 and 2 are what make surplus possible:
   a transfer rounded up to a packet, or one sized by an earlier caller, can return more than the
   current buffer holds. The host has already taken those bytes from the device, so reporting an
   overflow would drop them. WinUSB's default partial-read policy behaves the same way.
   `LibUsbBackend` reports an overflow instead (`LibUsbBackend.cs:876`), so Linux desktop and the
   browser differ here.
4. An abandoned transfer that ends in a fault holds the fault. The next read on that endpoint gets
   it.

Bulk and interrupt OUT:

5. A write waits for any abandoned write on its endpoint to settle before it is issued, which keeps
   writes in order.
6. A write cancelled while it waits is dropped and never issued.
7. A timed-out write that was already issued may still reach the device. After a desktop timeout,
   `CancelIoEx` may or may not stop it. A caller that retries a timed-out write must tolerate a
   duplicate on both.

`UsbDevice` raises `onIssued` when a write clears its own gate (`UsbDevice.cs:327-328`). In the
browser the backend may still hold the write back, so there `onIssued` means "handed to the backend".
`TreehopperBoard` only over-latches on it, which is the safe direction.

Control transfers on endpoint 0:

8. A control transfer never takes over and never holds. A new one waits for an abandoned one to
   settle, and the abandoned result is discarded. A DFU `UPLOAD`'s flash bytes must never be read as
   the reply to a later `GETSTATUS`.
9. Because of rule 8, an abandoned control OUT, such as a DFU `DNLOAD`, reaches the device before
   any later control transfer. Its effect on the device is unknown to the caller. A caller must
   re-read device state before it retries a request that is not idempotent. `Stm32DfuProgrammer`
   does: it opens with no transfer timeout, never retries a `DNLOAD`, and starts each operation from
   `EnsureIdleAsync`, which reads `GETSTATUS` and aborts to `dfuIDLE`
   (`Stm32DfuProgrammer.cs:72,109,172-191`).

Disposal:

10. `DisposeAsync` fails every waiting caller, discards held data and faults, then calls `close()`.

### D8 — Nothing in the browser path blocks on a task

Neither package calls `Wait()`, `Result` or `GetAwaiter().GetResult()`. The banned-API analyzer the
test projects already use (`tests/Directory.Build.targets`) is applied to both.

The code above them is clean today and not enforced: `UsbDevice`, `TreehopperBoard` and
`Stm32DfuProgrammer` hold no sync-over-async, no dedicated thread and no `Thread.Sleep`. Their
`Task.Run` and `Task.Delay` calls run on the one thread. (Source, per the review.)

Moving transfers to a dedicated worker is not part of this decision. See Open questions.

### D9 — Reset in the browser is the device's own

For [ADR-0060](0060-device-reset-and-recovery-escalation.md), `SoftProtocol` still works, because a
device supplies it over its own open transport. `UsbPortCycle` and `PnpDisableEnable` have no browser
source. WebUSB's `reset()` is a bus reset that no `ResetKind` names, and it is not implemented on
Windows. Nothing new is offered.

### D10 — The JS modules load from a rooted path and read in two calls

`InitializeAsync` loads `_content/<PackageId>/<module>.js` with `JSHost.ImportAsync`, from a path
rooted at the document's `<base>`. A dot-relative path can resolve under `/_framework/`.

Reads use the exploration's two-call shape: await the length, then copy synchronously into a
`MemoryView` span. `[JSImport]` still rejects an array inside a promise with SYSLIB1072 on SDK
10.0.401. (Measured by the review.)

---

## Deferred: a browser device provider

The first draft of this ADR also put `IDeviceProvider` and `IDeviceMonitorProvider` over browser
grants. The review found that neither consumer needs it, and that it costs the following:

- `DeviceId` documents an id that survives a reconnect. The session forms do not, and
  `DeviceProfile.ForDevice` promises to follow one instance across reconnects
  (`DeviceProfile.cs:92-106`). A tracker pinned to a session id stays absent after a replug.
- A Web Serial port has no key that survives a replug, so a reconnect cannot be matched to its
  earlier entry. The watcher would accept an `Activated` for an id that never had `Appeared`, which
  breaks [ADR-0004](0004-two-level-device-state-model.md)'s rule that active implies present
  (`DeviceWatcher.cs:1483-1535`).
- Revocation raises no event, and a page can revoke while visible, so a resync bounds nothing.
- `DeviceWatcher` would raise events on the main thread, not the thread pool, and its contract says
  otherwise.
- Parentage would have to be declared `Unavailable` per [ADR-0078](0078-device-topology-is-a-rooted-forest.md)
  D8, not `Root`.

Revisit when a page needs live grant events, for example to react to a board being plugged back in.
The pieces a provider would use are already here: the registry (D5), the `DeviceInfo` projection
(D5) and `GetGrantedDevicesAsync` (D4).

---

## Consequences

### Positive

- A page can drive a Treehopper, and flash an STM32 over USART or DFU, with no install. That depends
  on the gates below. On Windows the device must bind WinUSB, and on Linux the user needs a udev rule.
- A Web Serial page takes neither `Periphery.Usb` nor `System.IO.Ports`.
- The rules are pure values: id minting (D5), the `DeviceInfo` projection (D5), the endpoint state
  machine (D7) and the outcome mapping (D6). All four are tested in the existing desktop test job
  with no browser and no clock, per [ADR-0089](0089-tests-do-not-depend-on-elapsed-time.md).

### Negative

- `Periphery.Usb.Browser` is version-locked to `Periphery.Usb` by its exact pin.
- A serial-less device's id lasts one attachment.
- A consumer written against `Devices.*` or `DeviceSessionHost` does not run in a browser without
  change (D1).
- A write may still reach the device after its caller saw a timeout (D7 rule 7).
- The transfer shell cannot run in CI. The gates below cover it by hand.

---

## Alternatives considered

**A browser device provider now.** Deferred. See Deferred.

**One `Periphery.Browser` package.** Rejected. A Web Serial page would take `Periphery.Usb`
exact-pinned, and the name fits no ADR-0024 row.

**Browser code in `Periphery.Usb` and core, beside the OS backends.** Rejected. Every desktop consumer
would carry a JS asset and a request API that means something on one platform.

**A `DeviceProviderFactory` branch returning the grant set.** Rejected. Code written for
enumerate-all would read "not granted" as "not present".

**Make `IUsbBackend` public.** Rejected. ADR-0065 declined to widen internals per consumer.

**Abort on cancel by closing the device.** Rejected. It kills every pending transfer on the device.

**Target `net10.0-browser`.** Rejected. NU1202 in the stock Blazor template (D3).

**WebHID and Web Bluetooth now.** Out of scope. WebHID is what the Treehopper's bootloader needs.
Web Bluetooth is what nordic-dfu path D needs. Each gets its own decision.

---

## Verification

This moves to Accepted when every gate below passes, with the result recorded here. A failing gate
reopens the decision it names.

| Gate | Pass condition | Reopens |
|---|---|---|
| Package restore | Both packages pack, restore and build in a stock `dotnet new blazorwasm` project, and a `net10.0` test project runs the pure-core tests. | D2, D3 |
| Request from a click | After `InitializeAsync` at startup, `RequestDeviceAsync` from an `@onclick` handler opens the chooser. | D4, D10 |
| Treehopper on Windows | In Chromium on Windows, `TreehopperBoard.OpenAsync` on a granted board sets a pin and receives a pin report. | D6, D7 |
| Treehopper on Linux | The same on Linux with a udev rule. | D6, D7 |
| Treehopper on macOS | The same on macOS. | D6, D7 |
| Descriptor fidelity | Descriptors read through `GET_DESCRIPTOR` in the page are byte-identical to the desktop read of the same board. | D6 |
| Durable id | After a replug and after a browser restart, `GetGrantedDevicesAsync` returns the board with the same `webusb:` id, with no chooser. | D5 |
| Exclusive claim | With the board held open by a desktop process, `UsbDevice.OpenAsync` in the page throws a `UsbException`, not a `JSException`. | D6 |
| Revocation | Revoking the grant from the page-info bubble with the board open makes the next transfer throw `UsbDeviceRemovedException`. | D6 |
| Abandon and take over | On a raw `UsbDevice`, a read on `0x82` times out while the device is silent. The device then answers, and the next read on `0x82` returns those bytes. The `0x81` report read never faults. | D7 |
| Board-level timeout | On `TreehopperBoard`, a timed-out response read latches the desync (`TreehopperBoard.cs:875-876`), and a following pin reconcile succeeds. | D7 |
| STM32 DFU | `Stm32DfuProgrammer` reads the bootloader version from a granted STM32 in DFU mode, on Windows and on Linux. Record whether Windows needed a driver step. | D1, D6 |
| DFU after a cancel | Cancel a flash mid-download, then flash again with a new `Stm32DfuProgrammer`. The second flash verifies. | D7 rule 9 |
| Web Serial pipe | `Stm32SerialProgrammer` syncs over `BrowserSerialPort` with bootloader entry through `SetSignalsAsync`. The exploration's spike already passed this; the gate re-runs it on the package. | D4 |

---

## Open questions

| Question | What resolves it |
|---|---|
| Should transfers run in a dedicated worker, off the UI thread? | `navigator.usb` exists in dedicated workers, and a worker can reach a grant through `getDevices()`. What is untested is a .NET instance in a worker driving a `UsbDevice`, which the `blazorwebworker` template (.NET 11 SDK) would host. |
| Is `10C4:8A7E` blocked by server-delivered blocklist additions? | The "Treehopper on Windows" gate. They are not visible in source. |

---

## Related

- [`browser-wasm-device-backend-2026-09.md`](../explorations/browser-wasm-device-backend-2026-09.md),
  the exploration this decides on.
- [ADR-0007](0007-niche-platform-feasibility.md) §3, the 2025-07 survey this revises.
- [nordic-dfu spec](../feature-specs/firmware-flashing/nordic-dfu/spec.md) OQ-10, the browser scope
  question.
- [`integration-package-placement.md`](../patterns/integration-package-placement.md), which routes
  `Periphery.Usb.Browser` here.
