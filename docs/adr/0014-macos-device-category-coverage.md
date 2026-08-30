---
title: "ADR-0014: Extend macOS Device Category Coverage"
status: "Accepted"
date: "2026-07-15"
authors: "@charles8051 (proposal)"
tags: ["architecture", "decision", "macos", "iokit", "device-categories"]
supersedes: ""
superseded_by: ""
---

# ADR-0014: Extend macOS Device Category Coverage

## Status

> **Partially superseded — ADR-0051 (2026-05-31).** ADR-0051 demotes `Printer`,
> `Imaging`, `Sensor`, `Biometric` (this ADR's **Tier 2**) **and** `SmartCard` (this
> ADR's **Tier 1**) from `DeviceCategory` to capability **tags** (`DeviceInfo.Tags`).
> The IOKit detection added here is **relocated into tag-emitting enrichers, not
> deleted** — specifically the `IOUSBSmartCardController` class mapping, the
> `ResolveUsbCategory` USB-class arms (`0x06`→Imaging, `0x07`→Printer, `0x0B`→SmartCard),
> and the `ResolveHidCategory` usage-page-`0x20`→Sensor arm. Only the **Tier-1**
> additions for **Camera** (`IOVideoDevice`) and **Ports** (`IOSerialBSDClient`) remain
> as `DeviceCategory` values. See ADR-0051 for the migration plan.

## Context

The README category table currently shows seven `DeviceCategory` values that are fully supported on
Windows and Linux but return no results on macOS:

| Category | Windows | Linux | macOS |
|---|---|---|---|
| Camera | ✅ | ✅ | — |
| Printer | ✅ | ✅ | — |
| Ports (Serial) | ✅ | ✅ | — |
| Sensor | ✅ | ✅ | — |
| Imaging | ✅ | ✅ | — |
| Biometric | ✅ | ✅ | — |
| Smart Card | ✅ | ✅ | — |

In `MacOSCategoryMap.GetIOKitClasses`, all seven are mapped to an empty array (`[]`):

```csharp
DeviceCategory.Imaging or DeviceCategory.Biometric or DeviceCategory.Sensor
    or DeviceCategory.Ports or DeviceCategory.SmartCard or DeviceCategory.Printer
    or DeviceCategory.Camera => [],
```

This means a consumer querying any of these categories on macOS silently receives zero results
rather than the `PlatformNotSupportedException` called for in ARCHITECTURE.md §8 when a category
genuinely cannot be supported. The categories _can_ be supported; they simply haven't been wired up.

### Existing infrastructure

ADR-0011 established the IOKit P/Invoke foundation (`IOKitInterop.cs`, `MacOSDeviceProvider`,
`MacOSDeviceMonitorProvider`, `MacOSCategoryMap`) using `IOServiceMatching` to build per-class
matching dictionaries. The provider loops over the class names returned by
`MacOSCategoryMap.GetIOKitClasses` and calls `IOServiceGetMatchingServices` for each.

Two extension points in that infrastructure are relevant here:

1. **IOKit class name matching** — `IOServiceMatching(className)` creates a dictionary that
   matches any `IOService` whose class hierarchy includes `className`. Works for categories that
   map cleanly to a single registered IOKit class (e.g. `IOAudioDevice`, `AppleSmartBattery`).

2. **Property-filtered matching** — `IOServiceMatching` can be combined with additional
   `CFDictionary` entries for property-based push-down (e.g. `kUSBDeviceClass`, `PrimaryUsagePage`).
   The current provider does not use this path; it relies entirely on class-name matching and
   deduplicates in-memory. Extending the `GetIOKitClasses` return type to carry optional property
   hints would enable push-down for the USB-class-based categories.

### macOS IOKit surface for each missing category

| Category | IOKit / OS API | Notes |
|---|---|---|
| **Camera** | `IOVideoDevice` (macOS 10.14 +); `IOUSBDevice` subclass filtered on USB class 0x0E (Video) | `IOVideoDevice` is a documented IOKit class registered by camera drivers. Built-in FaceTime cameras and USB UVC webcams both register under this class. |
| **Printer** | `IOUSBDevice` with `kUSBDeviceClass` = 0x07 (Printer); `IOPrinter` (CUPS driver stack, macOS 10.x+); Bluetooth printers via `IOBluetoothDevice` | No single IOKit class covers all printer types. USB printers use device class 0x07; network/AirPrint printers are managed by CUPS and have no IOKit presence at the device level. |
| **Ports (Serial)** | `IOSerialBSDClient` | The authoritative IOKit class for serial port devices on macOS. Properties `IODialinDevice` and `IOCalloutDevice` expose the BSD device paths (`/dev/tty.*`, `/dev/cu.*`). Covers USB-serial adapters (FTDI, CP210x, CH34x), Bluetooth serial, and native UART ports. |
| **Sensor** | `IOHIDDevice` with `PrimaryUsagePage` = 0x20 (HID Sensor usage page); `AppleLMUController` (ambient light); `SMCSensors` (SMC thermal/fan, internal only) | External USB/HID sensors register under `IOHIDDevice` with usage page 0x20. Built-in platform sensors (ALS, lid, accelerometer) use private Apple IOKit classes and are not accessible via a public matching class name. |
| **Imaging** | `IOUSBDevice` with USB class 0x06 (Still Image Capture); `IOScannerDevice` (Image Capture framework kernel extension, deprecated in macOS 13) | USB scanners use class code 0x06 or vendor-specific class 0xFF. The Image Capture Architecture (`ICADevice`) is a higher-level framework on top of IOKit and is out of scope for a P/Invoke enumeration library. |
| **Biometric** | `IOUSBDevice` vendor-specific (USB class 0xFF); no standard IOKit class exists for fingerprint readers | External USB fingerprint readers use vendor-specific USB class codes. Apple's own Touch ID is handled exclusively by the Secure Enclave and is not exposed through any public IOKit interface. Support is therefore best-effort and device-discovery only (no biometric _data_). |
| **Smart Card** | `IOUSBSmartCardController`; `IOUSB_CCID` (driver class for CCID-compliant readers, USB class 0x0B); `CryptoTokenKit` (higher-level, requires entitlement) | USB smart card readers that use the CCID protocol register under driver class `com.apple.driver.usb.ccid`. IOKit class `IOUSBSmartCardController` covers hardware readers. `CryptoTokenKit` is entitlement-gated and out of scope. |

### Risk: matching dict property push-down

`IOServiceMatching` creates a matching dictionary keyed only on `IOProviderClass`. Filtering on
`kUSBDeviceClass` (used for Printer, Imaging, Biometric) requires adding an extra key to the
`CFDictionary` before passing it to `IOServiceGetMatchingServices`. The current provider has no
mechanism for this; `MacOSCategoryMap.GetIOKitClasses` returns a plain `string[]` and the provider
always calls `IOServiceMatching(className)` unconditionally.

Two implementation approaches address this:

- **A1 — Post-filter only**: Return the parent class (e.g. `IOUSBDevice`) from `GetIOKitClasses`
  and apply the USB-class-code predicate in `DeviceFilter.Matches()` after enumeration. Simple to
  implement; has higher enumeration overhead because all USB devices are fetched before filtering.
  Acceptable given that `DeviceFilter.Matches()` is already the authoritative filter gate.

- **A2 — Matching dict hints**: Extend `GetIOKitClasses` to return a richer type carrying an
  optional `IReadOnlyDictionary<string, object>` of property hints. The provider builds the
  matching dict from those hints, enabling IOKit-level push-down. Lower overhead; more invasive
  change to the existing provider/map contract.

---

## Decision

Extend macOS category coverage in two tiers based on how cleanly each category maps to an IOKit
class name.

### Tier 1 — Direct IOKit class name mapping (no provider contract changes required)

Add entries to `MacOSCategoryMap.GetIOKitClasses` using existing, public IOKit class names:

| Category | IOKit class name(s) |
|---|---|
| Camera | `IOVideoDevice` |
| Ports (Serial) | `IOSerialBSDClient` |
| Smart Card | `IOUSBSmartCardController` |

These three map cleanly to registered, public IOKit classes. No matching-dict changes are needed.
`MacOSDeviceProvider.ToDeviceInfo` must be updated to populate relevant `DeviceInfo` fields for
each new class (e.g. `SerialPortName` for `IOSerialBSDClient`).

### Tier 2 — Post-filter via parent class (approach A1, deferred push-down)

Use the parent IOKit class and rely on in-memory filtering via `DeviceFilter.Matches()` to
distinguish the category:

| Category | Parent IOKit class | Distinguishing predicate |
|---|---|---|
| Printer | `IOUSBDevice` / `IOUSBHostDevice` | USB device class property (`kUSBDeviceClass` == 7) |
| Sensor | `IOHIDDevice` | `PrimaryUsagePage` == 0x20 |
| Imaging | `IOUSBDevice` / `IOUSBHostDevice` | USB device class property (`kUSBDeviceClass` == 6) |
| Biometric | `IOUSBDevice` / `IOUSBHostDevice` | Best-effort: USB device class == 0xFF + HID usage fingerprint (no reliable discriminator) |

For Tier 2 categories, `MacOSCategoryMap.ResolveCategory` is extended so that when a
`kUSBDeviceClass` value is available in the service's property dictionary, it is used to override
the category resolved from the IOKit class name alone.

### Monitoring

`MacOSDeviceMonitorProvider` uses the same `MacOSCategoryMap.GetIOKitClasses` lookup to register
`IOServiceAddMatchingNotification` callbacks. Tier 1 additions require no provider changes beyond
the map update. Tier 2 additions inherit the parent-class monitor subscriptions that already
exist (USB, HID) and are correctly categorised at notification time through the same post-filter
logic.

### Biometric — caveat

`DeviceCategory.Biometric` is implemented as best-effort on macOS. Apple's Touch ID and other
Secure Enclave biometrics are inaccessible via public IOKit APIs. Only external USB fingerprint
readers that enumerate as standard `IOUSBDevice` entries are discoverable. This limitation is
documented in the XML doc comment on the `MacOSCategoryMap` entry and in ARCHITECTURE.md §3.

---

## Consequences

### Positive

- **POS-001**: The README category table reaches full macOS parity for five of seven categories
  (Camera, Printer, Ports, Sensor, Imaging) with no public API changes.
- **POS-002**: `IOSerialBSDClient` enumeration populates `SerialPortName` on `DeviceInfo`, enabling
  consumers to bridge discovered serial devices directly to `System.IO.Ports.SerialPort` by name.
- **POS-003**: `IOVideoDevice` is a stable, documented IOKit class with no entitlement requirements,
  available since macOS 10.14 (Mojave) — the minimum macOS version that supports .NET 8.
- **POS-004**: Approach A1 (post-filter) keeps the provider contract unchanged. The existing
  `string[]` return type of `GetIOKitClasses` and the `IOServiceMatching` loop in the provider
  require no structural refactoring.
- **POS-005**: Tier 2 monitoring correctness is inherited "for free" from existing USB and HID
  notification subscriptions already registered in `MacOSDeviceMonitorProvider`.

### Negative

- **NEG-001**: Tier 2 post-filtering fetches a superset of results from IOKit (all `IOUSBDevice`
  entries when querying Printer or Imaging). On machines with many USB devices this adds minor
  enumeration overhead. The impact is bounded because deduplication by registry entry ID already
  prevents double-counting.
- **NEG-002**: `DeviceCategory.Biometric` on macOS cannot cover Apple Touch ID or Secure Enclave
  biometrics. Consumers depending on built-in biometric detection must use `LocalAuthentication`
  framework directly. This divergence from Windows/Linux behaviour must be documented.
- **NEG-003**: `IOUSBSmartCardController` is the public IOKit class for CCID-compliant USB smart
  card readers. Contactless (NFC) smart card readers and `CryptoTokenKit` virtual tokens are
  out of scope and not discoverable through this path. The README table will remain `🟡` for
  Smart Card on macOS until NFC coverage is added.
- **NEG-004**: Approach A1 defers matching dict push-down (A2) to a follow-up. If profiling
  reveals meaningful overhead from fetching all USB devices for Printer/Imaging/Biometric
  categories, A2 can be adopted without a public API break.

---

## Alternatives Considered

### AVFoundation for Camera (`AVCaptureDevice`)

- **ALT-001**: **Description**: P/Invoke into `AVFoundation.framework` using `AVCaptureDevice.devicesWithMediaType(AVMediaTypeVideo)` / `AVMediaTypeAudio` for camera and microphone enumeration. Delivers richer metadata (format descriptions, supported frame rates) than IOKit.
- **ALT-002**: **Rejection Reason**: `AVFoundation` requires a running app bundle with `NSCameraUsageDescription` in `Info.plist` and triggers a macOS permission prompt on first access. A headless library used in CLI tools or `launchd` daemons cannot fulfil these requirements. `IOVideoDevice` covers the discovery use case without entitlement constraints.

### CryptoTokenKit for Smart Card

- **ALT-003**: **Description**: Use `CryptoTokenKit.framework` (`TKSmartCardSlotManager`) for smart card reader enumeration. Provides higher-level access including ATR reading.
- **ALT-004**: **Rejection Reason**: `CryptoTokenKit` requires the `com.apple.security.smartcard` entitlement, which must be embedded in a signed app bundle. This is incompatible with the library's zero-entitlement / zero-bundle constraint. IOKit `IOUSBSmartCardController` gives hardware reader discovery without entitlements.

### `system_profiler` subprocess for all categories

- **ALT-005**: **Description**: Extend the subprocess approach (already rejected in ADR-0011) to cover the missing categories by parsing `system_profiler SPCameraDataType`, `SPPrinterDataType`, etc.
- **ALT-006**: **Rejection Reason**: Subprocess approach rejected in ADR-0011 for enumeration latency, format instability, and lack of a real-time monitoring path. The same reasons apply here.

### Extend `GetIOKitClasses` to carry property hints (Approach A2) immediately

- **ALT-007**: **Description**: Change `GetIOKitClasses` return type to `IReadOnlyList<IOKitMatchingSpec>` (class name + optional property bag) so the provider can build property-filtered matching dicts for Printer, Imaging, and Biometric without fetching all USB devices.
- **ALT-008**: **Rejection Reason**: The provider/map contract change has no user-visible benefit at this time given the bounded overhead of approach A1. A2 is deferred (NEG-004) and can be applied as a non-breaking internal refactor if profiling justifies it.

---

## Implementation Notes

- **IMP-001**: Add `IOVideoDevice`, `IOSerialBSDClient`, and `IOUSBSmartCardController` constants to `MacOSCategoryMap.cs` alongside the existing class name constants. Update `GetIOKitClasses` and `ResolveCategory` switch expressions.
- **IMP-002**: Update `MacOSDeviceProvider.ToDeviceInfo` to extract `IODialinDevice` / `IOCalloutDevice` properties from `IOSerialBSDClient` services and assign them to `DeviceInfo.PortName` (introduced in ADR-0002).
- **IMP-003**: Extend `MacOSCategoryMap.ResolveCategory` to inspect `kUSBDeviceClass` from the properties dictionary when the IOKit class name is `IOUSBDevice` or `IOUSBHostDevice`, mapping class code 0x07 → `Printer`, 0x06 → `Imaging`, and 0x0B → `SmartCard`.
- **IMP-004**: Add a `ResolveHidSensorCategory` helper analogous to the existing `ResolveHidCategory`, returning `DeviceCategory.Sensor` when `PrimaryUsagePage` == 0x20.
- **IMP-005**: Update `ARCHITECTURE.md §3` category table to reflect the new macOS IOKit class mappings and add a footnote for the Biometric best-effort caveat.
- **IMP-006**: Update the README category table: Camera, Printer, Ports, Sensor, Imaging → `✅`; Biometric → `🟡` (best-effort, external USB only); Smart Card → `🟡` (USB CCID only, no NFC).
- **IMP-007**: Add test cases in `Periphery.Tests` covering `MacOSCategoryMap.GetIOKitClasses` for all seven newly-mapped categories, and `ResolveCategory` round-trips for USB class code overrides.

---

## References

- **REF-001**: [ADR-0011 — macOS Provider via IOKit + Notification Ports](0011-iokit-macos-provider.md) — establishes the P/Invoke foundation this ADR extends.
- **REF-002**: [ADR-0003 — Device Category Expansion](0003-device-category-expansion.md) — original rationale for the full `DeviceCategory` enum.
- **REF-003**: [ADR-0002 — Device Tree Topology](0002-device-tree-topology.md) — introduces `SerialPortName` on `DeviceInfo`, populated by the Ports implementation here.
- **REF-004**: [Apple IOKit Fundamentals — Matching Dictionaries](https://developer.apple.com/documentation/iokit/iokitlib_h) — IOKit P/Invoke reference.
- **REF-005**: [IOSerialBSDClient — Apple Open Source](https://opensource.apple.com/source/IOSerialFamily/) — IOKit class used for Ports enumeration.
- **REF-006**: [USB Class Codes — USB-IF](https://www.usb.org/defined-class-codes) — authoritative source for USB device class codes referenced in Tier 2 mapping.
