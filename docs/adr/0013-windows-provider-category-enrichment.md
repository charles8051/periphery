---
title: "ADR-0013: Windows Provider Category-Specific Property Enrichment Strategy"
status: "Accepted"
date: "2026-03-10"
authors: "@charles8051 (review)"
tags: ["architecture", "decision", "windows", "provider", "enrichment"]
supersedes: ""
superseded_by: "0018-winrt-enrichment-tfm-coupling.md (Tier 3 only)"
---

# ADR-0013: Windows Provider Category-Specific Property Enrichment Strategy

## Status

> **Amendment (ADR-0018):** The Tier 3 WinRT enrichment path described in this ADR
> (`WindowsWinRTEnricher`, `net*-windows10.0.17763.0` TFMs) has been replaced by a
> pure Win32 DisplayConfig implementation (`WindowsDisplayConfigEnricher`). Tier 1
> and Tier 2 are unchanged.

## Context

ADR-0009 migrated the Windows provider from WMI to SetupAPI / cfgmgr32 P/Invoke and explicitly
deferred WinRT `Windows.Devices.Enumeration` to "revisit later." The SetupAPI baseline is now
stable and proven. A WinMD API audit (March 2026) against the Windows SDK 10.0.26100.0 cache
revealed that while the cfgmgr32 `DEVPROPKEY` property store covers identity, topology, and
driver-level metadata completely, it does not cover several typed properties that `DeviceInfo`
already models:

| `DeviceInfo` property | Declared | Populated (Windows) | Source gap |
|---|---|---|---|
| `PortNumber` | ✅ | ❌ never set | `DEVPKEY_Device_Address` (pid=30) not defined in `DevNodeHelper` |
| `MacAddress` | ✅ | ❌ never set | No DEVPROPKEY; requires BCL `NetworkInterface` |
| `IPAddresses` | ✅ | ❌ never set | No DEVPROPKEY; requires BCL `NetworkInterface` |
| `Network` | ✅ | ❌ never set | No DEVPROPKEY; requires BCL `NetworkInterface` |
| `DriveType` | ✅ | ❌ never set | No DEVPROPKEY; requires BCL `DriveInfo` |
| `DisplayResolution` | ✅ | ❌ never set | WinRT `DisplayMonitor.NativeResolutionInRawPixels` |
| `DisplayBounds` | ✅ | ❌ never set | No direct API; requires DXGI / `EnumDisplayMonitors` |
| `BatteryChargePercent` | ✅ | ❌ never set | WinRT `Battery.GetReport()` |
| `BatteryStatus` | ✅ | ❌ never set | WinRT `Battery.GetReport().Status` |
| `UsbSpeed` | ✅ | ❌ never set | USB hub descriptor P/Invoke (deferred) |
| `MaxPowerMilliamps` | ✅ | ❌ never set | `CM_POWER_DATA` binary blob (deferred) |
| `PortName` | ✅ | ❌ never set | Registry `HKLM\...\Device Parameters\PortName` |

The audit also identified one correctness issue in the notification pipeline:
`CM_NOTIFY_ACTION_DEVICEINSTANCEENUMERATED` (action=7) is silently dropped in
`WindowsDeviceMonitorProvider.OnDeviceNotification`, which can delay newly-enumerated
devices from appearing in the scan cache by up to `PropertyScanInterval` (default 2 s).

Additionally, `DevNodeHelper` has no `GetUInt32Property` helper — only string, string list, and
GUID readers exist — even though `DEVPROP_TYPE_UINT32` values are needed for `PortNumber`
and potentially `Capabilities`.

Three distinct enrichment strategies are available for the blank fields:

1. **Pure cfgmgr32 DEVPROPKEY extension** — add missing key definitions and a `GetUInt32Property`
   helper. Covers `PortNumber` directly. Does not cover display, battery, or network fields.

2. **BCL `System.Net.NetworkInformation` + `System.IO.DriveInfo`** — fully managed, cross-platform
   BCL calls that correlate OS network interfaces and drive info to SetupAPI device nodes via
   `NetworkAdapter.Id` / `ContainerId` matching. No new native dependencies.

3. **WinRT category-specific APIs** — `Windows.Devices.Display.DisplayMonitor` and
   `Windows.Devices.Power.Battery` are the only APIs in the Windows SDK that provide
   `DisplayResolution` and battery charge levels respectively. Both require WinRT COM activation.

The concern from ADR-0009 about WinRT for the *core enumeration path* (AOT risk, COM apartment
requirements, package activation) does not apply equally to optional, category-scoped enrichment
calls made lazily after a device is already enumerated. However, the trade-off still exists and
must be evaluated per-category.

---

## Decision

Adopt a **three-tier enrichment model** for the Windows provider, executed as post-processing
steps inside `ToDeviceInfo()` (or lazy async enrichment where WinRT activation is required):

### Tier 1 — cfgmgr32 DEVPROPKEY extension (implement immediately)

Add `DEVPKEY_Device_Address` (pid=30, `DEVPROP_TYPE_UINT32`) to `DevNodeHelper` along with a
`GetUInt32Property` helper. Wire `PortNumber = GetUInt32Property(devInst, DEVPKEY_Device_Address)`
in `ToDeviceInfo()`. This is zero-risk: same P/Invoke surface already in use, no new DLL
imports, no COM, fully AOT-safe.

Also add `DEVPKEY_Device_BusTypeGuid` (pid=23) to enable future bus-type resolution that is more
precise than the current string-prefix heuristic (`WindowsCategoryMap.InferBusType`).

Fix `CM_NOTIFY_ACTION_DEVICEINSTANCEENUMERATED` (action=7): insert the device into
`_lastKnownDevices` without firing `DeviceConnected`, so the scan loop picks it up cleanly
instead of leaving a cache gap.

### Tier 2 — BCL `NetworkInterface` / `DriveInfo` correlation (implement in near-term)

For devices in the `Network` category, correlate to `System.Net.NetworkInformation.NetworkInterface`
via the `NetworkAdapterId` GUID (obtainable from WinRT `NetworkAdapter`, but also available in
the registry at `HKLM\SYSTEM\CurrentControlSet\Control\Network\{class-guid}\{adapter-guid}`).
Populate `MacAddress`, `IPAddresses`, and `Network` from `NetworkInterface.GetIPProperties()`.

For devices in the `Storage` category, correlate drive letters to device nodes via
`DEVPKEY_Device_Children` (string list of child instance IDs) and then `DriveInfo.GetDrives()`
to populate `DriveType`.

For devices in the `Ports` category, read `HKLM\SYSTEM\CurrentControlSet\Enum\{instanceId}\Device
Parameters\PortName` from the registry to populate `PortName`.

All BCL calls are synchronous, have no COM dependency, and are available on all .NET targets
(8 and 10). The correlation logic lives in dedicated internal helpers
(`WindowsNetworkEnricher`, `WindowsStorageEnricher`, `WindowsPortsEnricher`) to keep
`WindowsDeviceProvider.ToDeviceInfo()` readable.

### Tier 3 — WinRT async enrichment via `[SupportedOSPlatform("windows10.0.17763.0")]` (deferred,
opt-in)

`Windows.Devices.Display.DisplayMonitor.FromIdAsync()` and
`Windows.Devices.Power.Battery.FromIdAsync()` are the only practical sources for
`DisplayResolution` and `BatteryChargePercent` / `BatteryStatus`. Both require WinRT COM
activation.

These are deferred to a future `WindowsEnrichmentProvider` that is:
- Activated only on Windows 10 build 17763 or later (guarded at runtime by `OperatingSystem.IsWindowsVersionAtLeast`)
- Attributed `[SupportedOSPlatform("windows10.0.17763.0")]`
- Opt-in via a provider option flag, not enabled by default
- Safe to call from any .NET 8+ app model (console, service, WPF, WinUI 3, ASP.NET Core) without any special thread setup — the WinRT device-information APIs (`DeviceInformation`, `DisplayMonitor`, `Battery`) are agile objects compatible with both MTA (the CLR default) and STA apartments

`UsbSpeed` and `MaxPowerMilliamps` are further deferred: USB hub port descriptor access requires
additional P/Invoke into `usbioctl.h` (IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX), which is
a separate ADR-level decision.

---

## Consequences

### Positive

- **POS-001**: `DeviceInfo.PortNumber` is populated for all USB and bus-attached devices at zero
  cost — a single new DEVPROPKEY definition and 10-line helper.
- **POS-002**: `MacAddress`, `IPAddresses`, `Network`, `DriveType`, and `PortName` become populated
  using pure BCL calls that carry no new native dependencies and remain AOT-safe.
- **POS-003**: The three-tier model cleanly separates zero-risk P/Invoke extension (Tier 1),
  managed BCL correlation (Tier 2), and WinRT opt-in enrichment (Tier 3). Each tier can
  be implemented and shipped independently.
- **POS-004**: The `DEVICEINSTANCEENUMERATED` fix closes a race window where newly-enumerated
  devices could appear to the scan loop as unknown rather than tracking-eligible.
- **POS-005**: `DEVPKEY_Device_BusTypeGuid` replaces the brittle string-prefix heuristic in
  `WindowsCategoryMap.InferBusType` with a definitive GUID lookup for all major bus types.
- **POS-006**: Tier 3 WinRT enrichment being opt-in preserves the AOT / no-COM baseline that
  ADR-0009 established as a hard requirement.

### Negative

- **NEG-001**: Tier 2 BCL correlation requires that the device node's `ContainerId` or adapter GUID
  correctly matches the OS interface table — this correlation is best-effort and can fail for
  virtual adapters, VPN tunnels, and non-standard bus configurations.
- **NEG-002**: `DriveType` correlation via `DEVPKEY_Device_Children` requires an additional
  `GetStringListProperty` call per storage device node, increasing per-device enumeration cost
  marginally.
- **NEG-003**: Tier 3 enrichment (WinRT) adds a conditional COM activation path that must be
  carefully guarded to avoid `COMException` in non-STA contexts — the same category of failure
  that WMI introduced before ADR-0009.
- **NEG-004**: `UsbSpeed` and `MaxPowerMilliamps` remain `null` on Windows until a separate ADR
  addresses the USB hub IOCTL path, which may surprise callers querying USB devices.
- **NEG-005**: `DisplayBounds` (virtual-desktop position/size) has no single clean WinRT source;
  it would require `EnumDisplayMonitors` / DXGI P/Invoke or `Screen` via Windows Forms, which
  adds a dependency concern. Left unresolved.

---

## Alternatives Considered

### A — Extend cfgmgr32 only (no BCL / no WinRT)

- **ALT-001**: **Description**: Add all missing DEVPROPKEY constants and implement `GetUInt32Property`,
  `GetBinaryProperty` helpers. Accept that network, display, battery, and storage fields remain
  empty unless a future ADR adds WinRT or BCL correlation.
- **ALT-002**: **Rejection Reason**: Leaves `MacAddress`, `IPAddresses`, `DriveType`, and
  `BatteryStatus` permanently null despite BCL equivalents being trivially available. Incurs no
  additional cost or risk to use `NetworkInterface` and `DriveInfo`.

### B — WinRT `DeviceInformation.FindAllAsync()` for all categories

- **ALT-003**: **Description**: Replace SetupAPI enumeration with `DeviceInformation.FindAllAsync()`
  using AQS filters, and supplement `DeviceInfo` from the WinRT property bag
  (`DeviceInformation.Properties`). `DeviceWatcher` replaces cfgmgr32 notification entirely.
- **ALT-004**: **Rejection Reason**: Already evaluated and rejected in ADR-0009. WinRT activation
  is a runtime dependency that breaks AOT, service-hosted, and non-packaged-app contexts.
  Additionally, `DeviceInformation.Properties` requires declaring desired property keys at watcher
  creation; late-bound property access requires a separate `CreateFromIdAsync` round-trip per device.
  The cfgmgr32 P/Invoke approach has lower latency and no activation requirements.

### C — WinRT `DeviceWatcher` for notification only, SetupAPI for enumeration

- **ALT-005**: **Description**: Keep SetupAPI enumeration but replace cfgmgr32
  `CM_Register_Notification` with WinRT `DeviceWatcher` for change notifications. WinRT
  `Updated` events would eliminate the polling scan loop.
- **ALT-006**: **Rejection Reason**: WinRT `DeviceWatcher` still requires COM activation and a
  running WinRT infrastructure. The polling scan loop is a minor overhead (2 s interval,
  incremental diff). `CM_Register_Notification` already provides synchronous kernel-mode
  arrival/removal notifications; the polling loop only handles soft property mutations that
  neither cfgmgr32 nor WinRT `DeviceWatcher` push natively.

### D — USB IOCTL for UsbSpeed / MaxPowerMilliamps (immediate)

- **ALT-007**: **Description**: Add `IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX` P/Invoke to
  read USB connection speed and max power from the hub port descriptor synchronously during
  enumeration.
- **ALT-008**: **Rejection Reason**: Requires opening a handle to the USB hub device with
  `CreateFile`, iterating hub ports to find the matching connection index, and handling hub
  topology across tiers — substantial complexity for two fields. Deferred to a focused ADR.

---

## Implementation Notes

- **IMP-001**: Tier 1 work items — add to `DevNodeHelper.cs`:
  - `private const uint DEVPROP_TYPE_UINT32 = 0x00000007;`
  - `internal static readonly DEVPROPKEY DEVPKEY_Device_Address` (`s_devPropDevice`, pid=30)
  - `internal static readonly DEVPROPKEY DEVPKEY_Device_BusTypeGuid` (`s_devPropDevice`, pid=23)
  - `internal static uint? GetUInt32Property(int devInst, in DEVPROPKEY key)`
  - Wire `PortNumber = (int?)DevNodeHelper.GetUInt32Property(devInst, in DevNodeHelper.DEVPKEY_Device_Address)` in `WindowsDeviceProvider.ToDeviceInfo()`
- **IMP-002**: Fix `OnDeviceNotification` in `WindowsDeviceMonitorProvider`: add
  `case DevNodeHelper.CM_NOTIFY_ACTION_DEVICEINSTANCEENUMERATED:` — call `TryBuildDeviceInfo`
  and update `_lastKnownDevices` without raising any public event.
- **IMP-003**: Tier 2 enrichment helpers should be called from `ToDeviceInfo()` but only for
  their respective categories (guard with `if (device.Category == DeviceCategory.Network)`
  etc.) to avoid enumeration overhead for unrelated devices.
- **IMP-004**: Tier 3 WinRT enrichment requires a separate internal `WindowsWinRTEnricher` class.
  No special thread or apartment setup is needed by the caller — the .NET 8+ CLR initialises
  COM in MTA mode before user code runs, and all three WinRT types used
  (`DeviceInformation`, `DisplayMonitor`, `Battery`) are apartment-agile device-information APIs.
- **IMP-005**: Success criteria for Tier 1: `PortNumber` is non-null on at least one USB device
  in the integration test. Success criteria for Tier 2: `MacAddress` matches `ipconfig /all` for
  the active Ethernet adapter in the integration test.

---

## References

- **REF-001**: ADR-0009 — Migrate Windows Provider from WMI to SetupAPI / cfgmgr32 (superseded
  rationale for WinRT rejection on the core path)
- **REF-002**: `docs/ARCHITECTURE.md` §2.3 (Windows provider), §5 (DeviceInfo model), §10.6.2
  (IsConnected reliability per category)
- **REF-003**: Microsoft Learn — [CM_Get_DevNode_Property](https://learn.microsoft.com/windows/win32/api/cfgmgr32/nf-cfgmgr32-cm_get_devnode_propertyw)
- **REF-004**: Microsoft Learn — [Windows.Devices.Display.DisplayMonitor](https://learn.microsoft.com/uwp/api/windows.devices.display.displaymonitor)
- **REF-005**: Microsoft Learn — [Windows.Devices.Power.Battery](https://learn.microsoft.com/uwp/api/windows.devices.power.battery)
- **REF-006**: Microsoft Learn — [DEVPROPKEY reference / devpkey.h](https://learn.microsoft.com/windows-hardware/drivers/install/devpkey-device-address)
- **REF-007**: WinMD audit cache — `Generated Files\winmd-cache\packages\WindowsSDK\10.0.26100.0\`
  (generated 2026-03-10 from Windows SDK 10.0.26100.0)
