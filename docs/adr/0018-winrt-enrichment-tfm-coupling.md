---
title: "ADR-0018: Replace WinRT Display Enrichment with Win32 DisplayConfig (TFM Decoupling)"
status: "Accepted"
date: "2026-07-14"
authors: "@charles8051 (review)"
tags: ["architecture", "decision", "windows", "displayconfig", "tfm", "cross-platform"]
supersedes: ["0015-winrt-enrichment-additional-categories.md", "0016-winrt-aot-ccw-registration.md"]
superseded_by: ""
---

# ADR-0018: Replace WinRT Display Enrichment with Win32 DisplayConfig (TFM Decoupling)

## Context

### The TFM coupling problem

`WindowsWinRTEnricher` used `Windows.Devices.Display.DisplayMonitor` (WinRT) to populate
enriched monitor properties. These types only exist inside `#if WINDOWS10_0_17763_0_OR_GREATER`,
which is only true for the `net*-windows10.0.17763.0` TFMs. Any consumer on a plain `net*` TFM
received the no-op stub DLL regardless of which platform they ran on — monitor enrichment was
silently absent for the majority of real consumers.

### Avalonia investigation

Investigation of the Avalonia source (`Avalonia.Win32`) showed that identical display data
(friendly name, resolution, connector type, virtual-desktop bounds) is available via pure
Win32 P/Invoke without any Windows-specific TFM:

| | Periphery (before) | Avalonia / Periphery (after) |
|---|---|---|
| Package | `Microsoft.Windows.SDK.NET.ref` (WinRT projection) | Manual `[LibraryImport]` P/Invoke |
| Mechanism | WinRT objects (`DisplayMonitor`) | `QueryDisplayConfig` + `DisplayConfigGetDeviceInfo` |
| TFM required | `net*-windows10.0.17763.0` | Any `net*` |
| Guard | `#if WINDOWS10_0_17763_0_OR_GREATER` | `[SupportedOSPlatform("windows")]` only |

### Properties available via Win32 DisplayConfig

| Property | Win32 source |
|---|---|
| `MonitorName` | `DISPLAYCONFIG_TARGET_DEVICE_NAME.monitorFriendlyDeviceName` |
| `DisplayResolution` | `DISPLAYCONFIG_TARGET_PREFERRED_MODE` (native resolution) |
| `DisplayBounds` | `DISPLAYCONFIG_MODE_INFO` source mode (active resolution + virtual-desktop position) |
| `DisplayPhysicalConnector` | `DISPLAYCONFIG_TARGET_DEVICE_NAME.outputTechnology` mapped to `DisplayConnector` |
| `DisplayConnectionKind` | Inferred from `outputTechnology` (MIRACAST=Wireless, INTERNAL=Internal, etc.) |

### Properties not currently populated (future work)

| Property | Notes |
|---|---|
| `DisplayPhysicalSizeInInches` | Requires EDID parsing; no reliable Win32 shortcut for external displays |
| `DisplayDpi` | `GetDpiForMonitor` (shcore.dll, Win8.1+) — straightforward future addition |
| `DisplayUsageKind` | HMD classification; no Win32 equivalent |
| HDR luminance nits | EDID HDR metadata; `DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO` gives capability only |
| `BatteryChargePercent` / `BatteryStatus` | `IOCTL_BATTERY_QUERY_STATUS` (per-device) or `GetSystemPowerStatus` (system-wide) |

---

## Decision

Replace `WindowsWinRTEnricher` with `WindowsDisplayConfigEnricher`, a synchronous Win32-only
enricher. Remove all Windows-specific TFMs, the `Microsoft.Windows.CsWinRT` build-time package
(ADR-0016), and `WinRTMarshalRegistrations.cs` from the solution entirely.

### Matching strategy

`DISPLAYCONFIG_TARGET_DEVICE_NAME.monitorDevicePath` is the `GUID_DEVINTERFACE_MONITOR`
device interface path. `CM_Get_Device_Interface_Property(monitorDevicePath, DEVPKEY_Device_InstanceId)`
resolves it to the canonical PnP instance ID used as `DeviceInfo.Id`.

### Key struct fix

`DISPLAYCONFIG_TARGET_PREFERRED_MODE` contains `DISPLAYCONFIG_TARGET_MODE` which opens with
a `UINT64` (8-byte alignment). After `header(20) + width(4) + height(4) = 28`, the compiler
inserts 4 bytes of padding, making the correct size **80 bytes**, not 76. Passing `size = 76`
caused `DisplayConfigGetDeviceInfo` to return `ERROR_INVALID_PARAMETER` silently.

### Files changed

| File | Change |
|---|---|
| `Periphery/Windows/DisplayConfigInterop.cs` | New — blittable structs and `[LibraryImport]` |
| `Periphery/Windows/WindowsDisplayConfigEnricher.cs` | New — synchronous Win32 enricher |
| `Periphery/Windows/DevNodeHelper.cs` | Added `CM_Get_Device_Interface_Property` + `GetDeviceInterfaceInstanceId` |
| `Periphery/Windows/WindowsDeviceProvider.cs` | Swapped enrichers; removed `BuildWinRTEnricherAsync` |
| `Periphery/Windows/WindowsWinRTEnricher.cs` | **Deleted** |
| `Periphery/Windows/WinRTMarshalRegistrations.cs` | **Deleted** |
| `Periphery/Periphery.csproj` | `TargetFrameworks` → `net8.0;net10.0`; removed `EnableWindowsTargeting`, `CsWinRT` |
| `Periphery.Examples/Periphery.Examples.csproj` | Reverted to `net10.0` only |
| `example-scripts/device-dump.cs` | TFM reverted to `net10.0` |

---

## Consequences

### Positive

- **POS-001**: Any consumer on any TFM running on Windows receives full monitor enrichment.
- **POS-002**: `Periphery.Examples` and `device-dump.cs` target plain `net10.0`.
- **POS-003**: CI builds on Linux and macOS no longer cross-compile a Windows binary they cannot run.
- **POS-004**: Enumeration is fully synchronous in its enrichment path — no async WinRT batch at startup.
- **POS-005**: `Microsoft.Windows.CsWinRT` and the AOT complexity of ADR-0016 are entirely gone.

### Negative

- **NEG-001**: `DisplayPhysicalSizeInInches` is always null (no reliable Win32 equivalent for external monitors).
- **NEG-002**: `DisplayUsageKind` is always null (HMD classification requires WinRT).
- **NEG-003**: HDR luminance nit values are null (requires EDID parsing).
- **NEG-004**: `BatteryChargePercent` / `BatteryStatus` are null (Win32 replacement deferred).
- **NEG-005**: `DisplayDpi` is null (deferred; `GetDpiForMonitor` is a straightforward future addition).

---

## Alternatives Considered

### A — Multi-target + `EnableWindowsTargeting`
Interim workaround that unblocked CI without solving the root cause. Replaced by this ADR.

### B — `[ModuleInitializer]` enricher registration hook
Does not solve TFM coupling — the module initialiser only runs if its assembly is loaded,
which requires the consumer to target the Windows TFM. Useful as a future internal
refactor (testable enricher registration) but irrelevant to the TFM problem.

### C — Separate `Periphery.WinRT` opt-in package
Would address all NEG items. Deferred: Win32 covers common cases; EDID/battery can be
added via Win32 as needed. Revisit if HDR nit values or HMD detection become requested features.

### D — WinRT via raw COM P/Invoke (Avalonia composition pattern)
`RoActivateInstance` / `RoGetActivationFactory` + hand-rolled COM vtable dispatch would
regain WinRT data without a Windows TFM. Rejected: high complexity for marginal gain given
that the missing properties (physical size, HDR nits, usage kind) are edge-case use cases.