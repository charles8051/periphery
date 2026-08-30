---
title: "ADR-0017: WinRT Printer Enrichment via IppPrintDevice"
status: "Rejected"
date: "2026-07-14"
authors: "@charles8051 (review)"
tags: ["architecture", "decision", "windows", "winrt", "enrichment", "printer", "ipp"]
supersedes: ""
superseded_by: ""
---

# ADR-0017: WinRT Printer Enrichment via IppPrintDevice

## Context

The `Printer` device category is currently populated exclusively from SetupAPI / cfgmgr32
(Tier 1). The resulting `DeviceInfo` snapshot contains the OS device name, manufacturer,
class GUID, and hardware IDs, but nothing that distinguishes an IPP network printer from a
local USB printer, or that gives the printer's network address.

### WinRT API: `Windows.Devices.Printers.IppPrintDevice`

`IppPrintDevice` is a WinRT class introduced in **UniversalApiContract 13.0 (Windows 10 build
19041 / version 2004)**. It is distinct from the display and battery enrichers added in
ADR-0015 and ADR-0016, which target build 17763. A separate version guard
(`OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)`) is required.

#### Discovery path

```
IppPrintDevice.GetDeviceSelector()            // returns an AQS filter string
-> DeviceInformation.FindAllAsync(selector, additionalProperties)
-> per-device: IppPrintDevice.FromId(id)      // activates the WinRT object
```

`IppPrintDevice.IsIppPrinter(deviceId)` is a static guard that returns false for printers
without an IPP endpoint (legacy WSD, LPR, LPT, some older USB printers). The enricher must
call this before attempting `FromId`.

#### Properties available at discovery time

| WinRT member | Type | Notes |
|---|---|---|
| `PrinterName` | `string` | Same as OS device name — redundant with `DeviceInfo.Name` |
| `PrinterUri` | `System.Uri` | IPP endpoint URI, e.g. `ipps://printer.local:631/ipp/print` |
| `DeviceKind` | `IppPrintDeviceKind` | `Printer`, `FaxOut`, or `VirtualPrinter` |
| `IsIppFaxOutPrinter` | `bool` | Covered by `DeviceKind == FaxOut` |

#### IPP attributes via `GetPrinterAttributes`

`IppPrintDevice.GetPrinterAttributes(IEnumerable<string> attributeNames)` returns an
`IDictionary<string, IppAttributeValue>` keyed by IPP attribute name. Attributes relevant
to static device discovery (not live operational state):

| IPP attribute | Example value | Scope |
|---|---|---|
| `printer-make-and-model` | `"HP LaserJet Pro M404dn"` | Discovery — richer model string |
| `printer-location` | `"3rd floor, Room 312"` | Discovery — user-set location |
| `color-supported` | `true` | Discovery — hardware capability |
| `sides-supported` | `["one-sided","two-sided-long-edge"]` | Discovery — hardware capability |
| `document-format-supported` | `["application/pdf","image/pwg-raster"]` | Discovery |
| `printer-state` | `idle` / `processing` / `stopped` | **Operational state — out of scope** |
| `printer-state-reasons` | `["none"]` | **Operational state — out of scope** |

Periphery's scope is device discovery, not device operation. `printer-state` and
`printer-state-reasons` are explicitly excluded.

### Constraints

- `IppPrintDevice` covers only IPP-capable printers. Legacy printers (WSD-only, LPR, parallel
  port, some older USB) will not have an IPP endpoint and must be gracefully skipped.
- `IppPrintDevice.GetPrinterAttributes` is a synchronous blocking call on the calling thread
  and can take hundreds of milliseconds per device on a slow network. It must be called on a
  thread-pool thread (already satisfied by `ConfigureAwait(false)` in the async enricher chain).
- The enricher must not call `GetPrinterAttributes` at all for non-IPP printers — the call
  throws if the printer is unavailable.
- `GetPrinterAttributes` shares the same `IEnumerable<string>` → `IIterable<String>` CCW
  marshalling requirement as `DeviceInformation.FindAllAsync`. The existing
  `[GeneratedWinRTExposedExternalType(typeof(string[]))]` registration in
  `WinRTMarshalRegistrations.cs` (ADR-0016) covers this — no additional registration needed.

---

## Decision

**Proposed:** Add a `WindowsPrinterEnricher` (Tier 3) that, for each device in the `Printer`
category, attempts `IppPrintDevice.IsIppPrinter(id)` and, if true, calls `FromId(id)` to
read `PrinterUri` and `DeviceKind`. Optionally fetch `printer-make-and-model` and
`printer-location` via `GetPrinterAttributes` as a Tier 3b pass.

Add two new typed properties to `DeviceInfo`:

| Property | Type | Populated by |
|---|---|---|
| `PrinterUri` | `Uri?` | `IppPrintDevice.PrinterUri` |
| `PrinterKind` | `PrinterKind?` (new enum) | `IppPrintDevice.DeviceKind` |

`printer-make-and-model` maps to the existing `DeviceInfo.Name` path if richer than the OS
name, or a new `PrinterModel` string property. `printer-location` would map to a new
`PrinterLocation` string property. Both are lower priority than `PrinterUri` and `PrinterKind`
and may be deferred to a follow-up.

The minimum OS guard is `OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)`, added
alongside (not replacing) the existing `17763` guard in the enricher pipeline.

---

## Consequences

### Positive

- **POS-001**: Callers can distinguish real network printers from virtual/fax entries and from
  legacy printers that have no IPP endpoint, using the `PrinterKind` enum.
- **POS-002**: `PrinterUri` gives the printer's network address — the only way to get this
  from the OS without P/Invoking into the WinSpool API.
- **POS-003**: The AOT CCW registration added in ADR-0016 already covers the
  `IEnumerable<string>` marshalling required by `GetPrinterAttributes`, so no additional
  trimmer rooting work is needed.
- **POS-004**: Non-IPP printers degrade gracefully — `PrinterUri` and `PrinterKind` are null,
  same pattern as display properties on non-monitor devices.

### Negative

- **NEG-001**: Requires Windows 10 build 19041+, a higher bar than the display enricher
  (17763). Printers on older Windows builds get no enrichment. This is a separate version
  guard from the existing enricher infrastructure and adds a branch in the enricher pipeline.
- **NEG-002**: `GetPrinterAttributes` is a network call for IPP printers. It adds latency per
  printer proportional to network round-trip time. This must be done concurrently (same
  `Task.WhenAll` pattern as the display enricher) to avoid multiplying latency.
- **NEG-003**: `IppPrintDevice` does not cover the full installed-printer surface. A machine
  with 5 printers (2 IPP, 1 WSD, 1 LPR, 1 virtual PDF) would have enriched data on only 2.
  This is a genuine gap but is the best available WinRT path without P/Invoking into WinSpool.

---

## Alternatives Considered

### A — WinSpool P/Invoke (`EnumPrinters`, `GetPrinterInfo`)

- **Description**: Call `EnumPrinters(PRINTER_ENUM_LOCAL | PRINTER_ENUM_NETWORK)` and
  `GetPrinterInfo(2)` via P/Invoke to get `pPortName`, `pLocation`, `pComment`, `Status`,
  and `Attributes` for every installed printer regardless of IPP support.
- **Rejection reason**: P/Invoke into WinSpool breaks cross-platform build symmetry and
  requires unsafe code or `DllImport` declarations. `pPortName` gives a port name
  (`IP_192.168.1.100`) not a URI. The data is less structured than IPP attributes.
  WinSpool is not available on non-Windows TFMs. Deferred — could be a Tier 2 fallback if
  `IppPrintDevice` is unavailable (build < 19041).
- **Revisit condition**: If significant demand arises for printer enrichment on Windows builds
  older than 19041, a WinSpool-based Tier 2 enricher for `pLocation` and `pComment` would
  complement this ADR.

### B — No enrichment (status quo)

- **Description**: Leave Printer devices at Tier 1 (SetupAPI only).
- **Rejection reason**: `PrinterUri` is uniquely valuable — it cannot be obtained from
  SetupAPI without parsing undocumented hardware ID strings. The WinRT path is clean, versioned,
  and already within the established enricher pattern.

### C — Fetch all IPP attributes eagerly

- **Description**: Call `GetPrinterAttributes` with a broad list of attribute names at
  discovery time and surface them all as typed properties.
- **Rejection reason**: Periphery's scope is static device *characteristics*, not operational
  state. Fetching `printer-state`, `printer-state-reasons`, and `job-list` at discovery time
  would produce stale data immediately and blur the library's discovery-only contract.
  `color-supported` and `sides-supported` are static but low consumer demand justifies
  deferring them; they can be added as typed properties in a follow-up without an ADR.

---

## Implementation Notes (for when this ADR is accepted)

- **IMP-001**: Add `PrinterUri` (`Uri?`) and `PrinterKind` (`PrinterKind?`) to `DeviceInfo`.
  Add `PrinterKind` enum with values `Printer`, `FaxOut`, `VirtualPrinter`, matching
  `IppPrintDeviceKind`.
- **IMP-002**: Add `WindowsPrinterEnricher` to `Periphery/Windows/`, gated with
  `[SupportedOSPlatform("windows10.0.19041.0")]`. Follow the same `BuildAsync` / `Enrich`
  split as `WindowsWinRTEnricher`.
- **IMP-003**: Wire into `WindowsDeviceProvider.EnumerateAsync` as a fourth Tier, guarded by
  `OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)` and
  `filter.NeedsPrinterEnrichment` (new property on `DeviceFilter`, returns true when
  `Category` is null, All, or Printer).
- **IMP-004**: Add `NeedsPrinterEnrichment` to `DeviceFilter` following the same pattern as
  `NeedsMonitorEnrichment` and `NeedsBatteryEnrichment`.
- **IMP-005**: Update `DeviceInfoDiff.Compute` for `PrinterUri` and `PrinterKind`.
  Update `DeviceInfoTests`, `DeviceInfoDiffTests`, and `AllTypedProperties_AreCoveredByDiff`.
- **IMP-006**: Update `DeviceInfoJsonContext` with `[JsonSerializable(typeof(Uri))]` if not
  already present. `PrinterKind` enum gets `[JsonConverter(typeof(JsonStringEnumConverter<PrinterKind>))]`
  on the enum declaration.
- **IMP-007**: `WinRTMarshalRegistrations.cs` — no changes needed; `string[]` is already
  registered (ADR-0016) and covers `GetPrinterAttributes`'s `IEnumerable<string>` argument.
