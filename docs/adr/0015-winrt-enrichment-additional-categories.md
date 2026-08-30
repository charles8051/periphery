---
title: "ADR-0015: WinRT Enrichment for Additional Device Categories"
status: "Superseded"
date: "2026-07-14"
authors: "@charles8051 (review)"
tags: ["architecture", "decision", "windows", "winrt", "enrichment", "bluetooth", "usb", "audio", "battery"]
supersedes: ""
superseded_by: "0018-winrt-enrichment-tfm-coupling.md"
---

# ADR-0015: WinRT Enrichment for Additional Device Categories

## Context

### What ADR-0013 delivered (Tier 3)

ADR-0013 introduced `WindowsWinRTEnricher` as a Tier 3 enrichment pass. The initial
implementation covered two categories:

| Category | WinRT source | Properties populated |
|---|---|---|
| `Monitor` | `Windows.Devices.Display.DisplayMonitor` | `DisplayResolution`, `MonitorName`, `DisplayPhysicalSizeInInches`, `DisplayDpi`, `DisplayPhysicalConnector`, `DisplayConnectionKind`, `DisplayUsageKind`, `DisplayMaxLuminanceInNits`, `DisplayMaxAvgLuminanceInNits`, `DisplayMinLuminanceInNits` |
| `Battery` | `Windows.Devices.Power.Battery` | `BatteryChargePercent`, `BatteryStatus` |

The implementation proved the model end-to-end:

- `DisplayMonitor.GetDeviceSelector()` + `DeviceInformation.FindAllAsync()` produces a map of
  PnP instance IDs to `DisplayMonitor` objects in a single parallel async batch.
- `DisplayMonitor.FromInterfaceIdAsync(di.Id)` (not `FromIdAsync`) is the correct API when
  `di.Id` is a device interface path from `FindAllAsync`. This was discovered through
  debugger-assisted investigation and is documented in
  `docs/investigations/2026-07-displayresolution-winrt.md`.
- Per-task `try/catch` around each `FromInterfaceIdAsync` call prevents a single failing monitor
  from aborting the entire map build via `Task.WhenAll` exception propagation.
- All tasks fire concurrently; the `Task.WhenAll` barrier completes in parallel time.
- A `MonitorSnapshot` record struct captures all fields extracted from `DisplayMonitor` in one
  activation call, so `EnrichMonitor()` is a pure dictionary lookup with no further WinRT calls.
- The enricher is skipped entirely when `DeviceFilter.NeedsMonitorEnrichment` and
  `NeedsBatteryEnrichment` are both false, so USB/HID/Network queries pay zero cost.

### Remaining high-priority gaps

A follow-up WinRT API audit identified several other device categories that have rich WinRT
APIs with properties Periphery cannot currently provide from SetupAPI alone:

#### Bluetooth -- `Windows.Devices.Bluetooth`

`BluetoothDevice` and `BluetoothLEDevice` expose:

| Property | WinRT source | `DeviceInfo` field |
|---|---|---|
| RSSI (signal strength) | `BluetoothLEAdvertisementWatcher` | `BluetoothRssi` (`short?`) -- new |
| Device class (classic) | `BluetoothDevice.ClassOfDevice` | `BluetoothClassOfDevice` (`uint?`) -- new |
| Connection status | `BluetoothDevice.ConnectionStatus` | `BluetoothConnectionStatus` (`BluetoothConnectionStatus?`) -- new enum |
| Address | `BluetoothDevice.BluetoothAddress` (48-bit uint) | `MacAddress` -- already declared; currently null for BT |
| LE appearance | `BluetoothLEDevice.Appearance` | `BluetoothAppearance` (`ushort?`) -- new |

`BluetoothDevice.FromIdAsync` accepts the device interface path from `DeviceInformation`,
matching the pattern already established for `DisplayMonitor`. The `bluetooth` capability
restriction applies to packaged (UWP/MSIX) apps only; Periphery targets unpackaged .NET
processes where it does not apply.

Available since: Windows 10 1507 (build 10240) -- below our `windows10.0.17763.0` minimum.

#### USB -- `Windows.Devices.Usb`

`UsbDevice` exposes:

| Property | WinRT source | `DeviceInfo` field |
|---|---|---|
| USB version | `UsbDevice.DeviceDescriptor.UsbSpecificationVersion` | `UsbVersion` (`Version?`) -- new |
| Max packet size | `UsbDevice.DeviceDescriptor.MaxPacketSize0` | `UsbMaxPacketSize` (`int?`) -- new |

**Important caveat:** `UsbDevice.FromIdAsync` requires exclusive access to the USB device.
If any other process (driver, HID runtime, audio stack) already holds the device open,
`FromIdAsync` fails with `Access is denied`. Enrichment is therefore limited to devices
whose active driver is `WinUSB`.

Available since: Windows 10 1507 (build 10240).

#### Audio -- `Windows.Media.Devices`

WinRT has no `AudioDevice` analogue to `DisplayMonitor`. However,
`Windows.Media.Devices.MediaDevice` exposes synchronous static queries:

| Property | WinRT source | `DeviceInfo` field |
|---|---|---|
| Default render device ID | `MediaDevice.GetDefaultAudioRenderId(AudioDeviceRole.Default)` | `IsDefaultAudioOutput` (`bool?`) -- new |
| Default capture device ID | `MediaDevice.GetDefaultAudioCaptureId(AudioDeviceRole.Default)` | `IsDefaultAudioInput` (`bool?`) -- new |

No per-device async activation is needed -- both calls happen once at build time, and
enrichment is a plain string comparison against `DeviceInfo.Id`.

Available since: Windows 10 1507 (build 10240).

#### Network -- `Windows.Networking.Connectivity`

The Tier 2 BCL enricher (`WindowsNetworkEnricher`) already covers `MacAddress`,
`IPAddresses`, and `Network` via `System.Net.NetworkInformation`. WinRT adds no new data
beyond what the BCL already provides. **No WinRT enrichment recommended.**

#### Power / Battery -- already implemented

Covered by ADR-0013. No new fields identified.

---

## Decision

Extend `WindowsWinRTEnricher` with three new enrichment passes. All passes follow the
same map-build-then-enrich pattern proven for `Monitor`.

### Pass 1 -- Bluetooth (`BluetoothDevice` + `BluetoothLEDevice`)

**Implement.** Risk is low: `FromIdAsync` uses the same `di.Id` interface path pattern, the
API has been available since Windows 10 1507, and both classic and LE device objects are
agile (thread-safe, no STA requirement).

New `DeviceInfo` properties added:
- `MacAddress` -- populated from `BluetoothDevice.BluetoothAddress` (48-bit uint to 6-byte
  array to `PhysicalAddress`). Per ALT-006, the address is read from the
  `System.DeviceInterface.Bluetooth.DeviceAddress` property bag key in `FindAllAsync` to
  avoid `FromIdAsync` access issues, reserving `FromIdAsync` for `ClassOfDevice`,
  `ConnectionStatus`, and `LEAppearance`.
- `BluetoothClassOfDevice` (`uint?`) -- raw class-of-device value.
- `BluetoothConnectionStatus` (`Periphery.BluetoothConnectionStatus?`) -- new enum mirroring
  `Windows.Devices.Bluetooth.BluetoothConnectionStatus`.
- `BluetoothAppearance` (`ushort?`) -- LE appearance category code (GAP appearance values);
  null for classic Bluetooth devices.

`DeviceFilter.NeedsBluetoothEnrichment` gates the pass.

`BluetoothDevice.GetDeviceSelector()` and `BluetoothLEDevice.GetDeviceSelector()` provide
the AQS selectors for `FindAllAsync`. Both run concurrently; results are merged by instance ID.

**Deferred:** RSSI -- requires an active advertisement scan incompatible with snapshot enumeration.

### Pass 2 -- USB (`UsbDevice`)

**Implement with restricted scope.** Enrichment is limited to `WinUSB`-serviced devices.
The `Driver` property from SetupAPI is available before WinRT enrichment runs, so the
enricher filters on `device.Driver == "WinUSB"` before calling `FromIdAsync`.

New `DeviceInfo` properties added:
- `UsbVersion` (`Version?`) -- USB specification version (e.g. `2.0`, `3.1`). Complements
  `UsbSpeed`, which reflects the negotiated connection speed, not the device's capability.
- `UsbMaxPacketSize` (`int?`) -- control endpoint max packet size from the device descriptor.

Per-task access-denied errors are caught and logged; the device retains all SetupAPI properties.

### Pass 3 -- Audio default device flags (`MediaDevice`)

**Implement.** Both `GetDefaultAudioRenderId` and `GetDefaultAudioCaptureId` are synchronous.
The enricher calls them once at build time; `EnrichAudio()` is a plain string comparison.

New `DeviceInfo` properties added:
- `IsDefaultAudioOutput` (`bool?`) -- `true` if this is the system default audio render device.
- `IsDefaultAudioInput` (`bool?`) -- `true` if this is the system default audio capture device.

`DeviceFilter.NeedsAudioEnrichment` gates the pass.

### Properties not added in this ADR

| Property | Reason deferred |
|---|---|
| `BluetoothRssi` | Requires live advertisement scan; incompatible with snapshot discovery model |
| USB descriptor string backfill | SetupAPI already populates `Manufacturer`/`Name`/`SerialNumber` |
| Audio format enumeration | No `DeviceInfo` field models audio formats |
| Network WinRT enrichment | BCL Tier 2 already covers all modelled fields |

---

## Consequences

### Positive

- **POS-001**: Bluetooth devices gain `MacAddress` -- the most-requested property for
  Bluetooth enumeration (used for device identification in multi-device scenarios).
- **POS-002**: `BluetoothConnectionStatus` provides a reliable connected/disconnected state
  independent of the SetupAPI `IsConnected` heuristic.
- **POS-003**: `UsbVersion` distinguishes USB 2.0/3.x capable devices from their negotiated
  connection speed -- a diagnostic scenario when a USB 3.x device is plugged into a USB 2.0 hub.
- **POS-004**: `IsDefaultAudioOutput` / `IsDefaultAudioInput` are trivial to implement and
  answer the most common audio enumeration question.
- **POS-005**: All three passes follow the proven pattern; no new architectural concepts introduced.
- **POS-006**: The WinUSB restriction on USB enrichment keeps failure rates low and logs clean.

### Negative

- **NEG-001**: An unfiltered `Devices.Enumerate()` fires up to five concurrent `FindAllAsync`
  calls. Category-specific queries eliminate irrelevant passes via `NeedsXxx` flags.
- **NEG-002**: Bluetooth `FromIdAsync` may require the `bluetooth` capability in packaged
  (MSIX/UWP) app contexts. `UnauthorizedAccessException` must be caught per-task.
- **NEG-003**: A null `Driver` (device enumerated before driver starts) must be treated as
  ineligible for USB WinRT enrichment.
- **NEG-004**: `IsDefaultAudioOutput` / `IsDefaultAudioInput` are point-in-time snapshots.
  Changing the default audio device after enumeration produces stale results until
  re-enumeration. This is consistent with the immutable-snapshot design of `DeviceInfo`.

---

## Alternatives Considered

### A -- Extend Bluetooth enrichment to include RSSI

- **ALT-001**: Use `BluetoothLEAdvertisementWatcher` to collect RSSI values over a short
  scan window, then correlate by Bluetooth address.
- **ALT-002**: **Rejected.** Turns a fast parallel snapshot into a time-bounded scan with
  non-deterministic latency. Violates the discovery-only, no-active-scanning principle.

### B -- USB enrichment without driver restriction

- **ALT-003**: Attempt `UsbDevice.FromIdAsync` for all USB devices and catch access-denied
  errors per-task.
- **ALT-004**: **Rejected.** Most USB devices are claimed by class drivers (HID, audio,
  storage). Near-100% failure rate with a flood of logged exceptions.

### C -- Use `DeviceInformation` property bag for Bluetooth address

- **ALT-005**: Request `System.DeviceInterface.Bluetooth.DeviceAddress` directly from
  `FindAllAsync` without calling `BluetoothDevice.FromIdAsync`.
- **ALT-006**: **Partially accepted.** The address is read from the property bag. `FromIdAsync`
  is reserved for `ClassOfDevice`, `ConnectionStatus`, and `LEAppearance` which are not
  available as property bag keys.

---

## Implementation Notes

- **IMP-001**: `DeviceFilter` -- add `NeedsBluetoothEnrichment` (Category is null or Bluetooth),
  `NeedsAudioEnrichment` (Category is null or Audio), and `NeedsUsbWinRTEnrichment` (Category
  is null or Usb; the enricher checks `device.Driver == "WinUSB"` before activating).

- **IMP-002**: `WindowsWinRTEnricher.BuildAsync` -- add three new tasks: `BuildBluetoothMapAsync`,
  `BuildUsbMapAsync` (restricted), and `BuildAudioDefaultsAsync` (returns a
  `(string? renderId, string? captureId)` tuple). All five `Task.WhenAll` branches guarded
  by `NeedsXxx` flags.

- **IMP-003**: `BluetoothSnapshot` record struct -- `MacAddress` (`PhysicalAddress?`),
  `ClassOfDevice` (`uint?`), `ConnectionStatus` (`BluetoothConnectionStatus?`),
  `Appearance` (`ushort?`).

- **IMP-004**: `UsbSnapshot` record struct -- `UsbVersion` (`Version?`), `MaxPacketSize` (`int?`).

- **IMP-005**: New `DeviceInfo` properties:
  - Section `// ++ Bluetooth-specific ++`: `BluetoothClassOfDevice` (`uint?`),
    `BluetoothConnectionStatus` (`Periphery.BluetoothConnectionStatus?`),
    `BluetoothAppearance` (`ushort?`).
  - Section `// ++ USB-specific ++` (existing): `UsbVersion` (`Version?`),
    `UsbMaxPacketSize` (`int?`).
  - Section `// ++ Audio-specific ++` (new): `IsDefaultAudioOutput` (`bool?`),
    `IsDefaultAudioInput` (`bool?`).
  - `MacAddress` already declared in `// ++ Network ++`; Bluetooth enrichment fills it for
    Bluetooth devices that Tier 2 BCL enrichment leaves null.

- **IMP-006**: New enum `BluetoothConnectionStatus` -- values: `Unknown`, `Connected`,
  `Disconnected`. JSON-serialised as string via `JsonStringEnumConverter`.

- **IMP-007**: `DeviceInfoTests` -- extend `Defaults_NullableFieldsAreNull` and
  `AllProperties_CanBeInitialized` for all new properties.

- **IMP-008**: Read `System.DeviceInterface.Bluetooth.DeviceAddress` (a 64-bit ULONG) from
  the `FindAllAsync` property bag for `MacAddress`. Include in `s_bluetoothProps` passed
  to `FindAllAsync`.