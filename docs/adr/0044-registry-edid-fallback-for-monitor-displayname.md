---
title: "ADR-0044: Registry EDID Fallback for Monitor DisplayName when DisplayConfig Returns Zero Paths"
status: "Accepted"
status_note: "Shipped - `WindowsEdidEnricher` alongside `WindowsDisplayConfigEnricher`."
date: "2026-05-22"
authors: "@charles8051 (kiosk investigation)"
tags: ["architecture", "decision", "windows", "displayconfig", "edid", "enrichment", "monitor", "win10-iot"]
supersedes: ""
superseded_by: ""
---

# ADR-0044: Registry EDID Fallback for Monitor DisplayName when DisplayConfig Returns Zero Paths

> **Note (2026-05-25).** The property this ADR is about was subsequently
> renamed `DeviceInfo.DisplayName` → `DeviceInfo.MonitorName` for clarity
> (the original name read like "user-facing display string for this device"
> when it was actually "EDID friendly name of a monitor, null on every
> other category"). The body and filename retain the original name as a
> historical anchor; in current code, `MonitorName` is the field this
> fallback populates.

## Context

### What we observed

ADR-0018 settled monitor enrichment on Win32 DisplayConfig P/Invoke (`QueryDisplayConfig` + `DisplayConfigGetDeviceInfo`), populating `DisplayName`, `DisplayResolution`, `DisplayBounds`, `DisplayPhysicalConnector`, and `DisplayConnectionKind`. The path is TFM-decoupled, AOT-friendly, and modeled directly on Avalonia's working approach. On a normal Windows desktop it works.

A kiosk running **Windows 10 IoT Enterprise LTSC 2019 (kernel `10.0.17763.771`)** surfaced a hard failure mode: every monitor reports `DisplayName=null`, even with EDID present, EDID readable via WMI, and monitors physically connected.

A trace-instrumented build of Periphery.Cli (env-var-gated `PERIPHERY_DISPLAYCONFIG_TRACE=1`, see `WindowsDisplayConfigEnricher.Trace`) deployed to the kiosk produced:

```
GetDisplayConfigBufferSizes(QDC_ALL_PATHS)         rc=0  pathCount=0  modeCount=0
GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS) rc=0  pathCount=0  modeCount=0
QueryDisplayConfig                                 rc=87 (ERROR_INVALID_PARAMETER)
```

**Both flag values return zero paths**, even though:

- Two monitors are physically connected to the kiosk
- `Get-WmiObject -Namespace root\wmi -Class WmiMonitorID` returns clean EDID friendly names (`"15" LCD"` and `"OML323"`)
- PnP enumeration sees both monitors (`Periphery.Cli devices list --category Monitor` returns 2 devices with their `HardwareID`s, `ContainerId`s, etc.)
- The monitors are functioning (the kiosk has a UI on them)

The OS reports DisplayConfig has nothing to enumerate. `QueryDisplayConfig`'s subsequent `ERROR_INVALID_PARAMETER` is consequential — it's being given zero-sized buffers.

The whole tier-3 cascade (`DisplayName`, `DisplayResolution`, `DisplayBounds`, `DisplayPhysicalConnector`, `DisplayConnectionKind`) is therefore null on this Win10 IoT image regardless of monitor. This isn't a Periphery bug — the OS API itself returns no data. It also isn't unique to Periphery: any consumer of `QueryDisplayConfig` on this build sees the same thing (Avalonia's `Screen.DisplayName` is almost certainly also null on the kiosk for the same reason).

### Why this matters

The kiosk consumer's `WindowManagerService` correlates Periphery trackers to Avalonia `Screen` instances by `DisplayName` — works on a developer workstation, silent no-op on the kiosk. The root cause is upstream of both consumers.

More broadly, "DisplayConfig works on most Windows machines but not all" is a real reliability boundary for any consumer wanting a monitor's friendly name. We need a fallback that doesn't depend on the failing API.

### What still works on this OS

- **WMI `WmiMonitorID`** exposes the EDID `UserFriendlyName`, `ManufacturerName`, `ProductCodeID`, `SerialNumberID`. Confirmed via PowerShell on the live kiosk.
- **Registry** caches the raw EDID block at `HKLM\SYSTEM\CurrentControlSet\Enum\<deviceInstanceId>\Device Parameters\EDID` as a `REG_BINARY`. Windows populates this at PnP enumeration time, before DisplayConfig is involved. This is exactly where the bytes WMI and DisplayConfig both parse internally come from.
- **`SetupAPI` / `cfgmgr32`** continues to enumerate the monitors and return their `HardwareID`s (which embed the EDID monitor-name code, e.g. a four-letter vendor code plus a product hex code).

So the EDID data is on the machine; only DisplayConfig is failing to surface it.

## Decision drivers

| Driver | Notes |
|---|---|
| **Honor ADR-0009 (no WMI).** | WMI was rejected for being not-AOT-safe, having a runtime service dependency (`winmgmt`) that's disabled in hardened IoT builds, having polling-based events, and being a secondary projection over the same SetupAPI data. Reaching for `System.Management` as a fallback would re-introduce all of that, plus an explicit irony — the original "WMI may be disabled on IoT" concern is what we'd be banking on working. |
| **Honor ADR-0018 (TFM decoupling).** | Solution must work on plain `net*` TFMs without `*-windows10.0.x` coupling. |
| **Match existing P/Invoke style.** | `WindowsPortsEnricher` (and to a lesser extent `WindowsNetworkEnricher`) already reads from `HKLM\SYSTEM\CurrentControlSet\Enum\<instanceId>\Device Parameters\` via `Microsoft.Win32.Registry`. The pattern is established. |
| **Don't claim more than we can deliver.** | Resolution / Bounds / Connector / ConnectionKind have no equivalent in the cached EDID block alone. The fallback should fix `DisplayName` and only `DisplayName` — silently expanding the contract elsewhere would create new "sometimes null, sometimes not" surprises. |

## Decision

When `WindowsDisplayConfigEnricher.Enrich(device)` is called on a Monitor and DisplayConfig hasn't supplied a `FriendlyName` (either no snapshot in the map, or a snapshot with `FriendlyName=null`), fall back to **`WindowsEdidEnricher.GetMonitorFriendlyName(device.Id)`**:

- Open `HKLM\SYSTEM\CurrentControlSet\Enum\<device.Id>\Device Parameters` via `Microsoft.Win32.Registry.LocalMachine.OpenSubKey`.
- Read the `EDID` value as a `byte[]`.
- Parse the four 18-byte monitor descriptor blocks at offsets `0x36`, `0x48`, `0x5A`, `0x6C` for the Display Product Name descriptor (tag `0xFC`, 13 ASCII characters at descriptor bytes `5..17`).
- Trim `0x0A` / `0x00` / `0x20` padding.
- Return the parsed name, or `null` on any failure mode.

The fallback is invoked **only** when DisplayConfig didn't deliver. When DisplayConfig succeeds (the common case on a normal Windows desktop), the registry read is skipped — no performance regression for the path that already works.

Only `DisplayName` is affected by this ADR. The other DisplayConfig-sourced fields stay null when DisplayConfig is unavailable; they have no clean registry equivalent and faking values would be worse than admitting we don't know.

## Implementation

- **New file**: `src/Periphery/Windows/WindowsEdidEnricher.cs` — static helper, `[SupportedOSPlatform("windows")]`. Pure `Microsoft.Win32.Registry` + EDID-block parsing. No new dependencies. Same shape as `WindowsPortsEnricher`.
- **Modified**: `src/Periphery/Windows/WindowsDisplayConfigEnricher.cs` — `Enrich(device)` calls `WindowsEdidEnricher.GetMonitorFriendlyName(device.Id)` when `snap.FriendlyName` is null. When the device isn't in the DisplayConfig map at all (the kiosk case), the method still returns a meaningful result — just with `DisplayName` populated from the registry and the other enriched fields untouched.

The env-var-gated diagnostic tracing (`PERIPHERY_DISPLAYCONFIG_TRACE=1`) introduced during this investigation is kept in place; it costs nothing when the variable is unset and is load-bearing for the next time someone hits an enrichment failure on an unexpected Windows build.

## Alternatives considered

### A) WMI `WmiMonitorID` fallback via `System.Management`

Works on the kiosk (verified). But re-introduces `System.Management` and `winmgmt` runtime dependencies that ADR-0009 spent capital eliminating. Specifically violates the "AOT-safe" property by re-rooting reflection-heavy COM interop. Rejected on architectural grounds even though it would functionally work for this case.

### B) Just-accept-null and move correlation responsibility upstream

Don't enrich on the kiosk; require consumers (`WindowManagerService` etc.) to use a non-DisplayName correlation strategy (e.g. Id-prefix matching against Avalonia screens). The kiosk consumer BACKLOG already tracks this as a complementary fix.

This is a real position but it's narrower than necessary — any future Periphery consumer that wants a monitor's friendly name on a Win10 IoT host hits the same wall. Solving it at the Periphery layer is strictly additive and removes a known-broken case from every downstream's mental model.

### C) Investigate / fix the OS-level DisplayConfig failure

Why is `QueryDisplayConfig` broken on this Win10 IoT build? Possible causes: missing graphics driver components in the LTSC 2019 image, Hyper-V virtual display driver interference, an out-of-date GPU driver. Could be solvable by Windows Update or a driver upgrade.

Out of scope for Periphery — this is a kiosk-side OS issue. Even if fixed on this specific kiosk, the architectural lesson holds: DisplayConfig is not universally reliable on Windows, and a Periphery consumer can't depend on it being available. The registry-EDID fallback makes Periphery's monitor enrichment robust regardless of what's wrong with DisplayConfig on a given machine.

### D) Cross-reference DisplayConfig output to fill in Avalonia gaps

Not relevant — Avalonia's enrichment is Avalonia's problem. We're scoped to Periphery's `DeviceInfo.MonitorName`.

## Consequences

### Positive

- Periphery's `DisplayName` is populated on Win10 IoT builds where DisplayConfig is non-functional, without breaking ADR-0009 (no WMI) or ADR-0018 (no TFM coupling).
- The new helper follows the existing `WindowsPortsEnricher` pattern verbatim — same registry-key shape, same `Microsoft.Win32.Registry` access, same static-method API. No new architectural surface.
- Failure modes are explicit: the helper returns `null` for "no registry value," "EDID too short," "no `0xFC` descriptor," and "name is all-whitespace padding." A consumer that sees `DisplayName=null` learns "neither DisplayConfig nor the cached EDID gave us a friendly name on this machine" — same contract as today, just more cases where the answer is non-null.

### Negative

- Adds one registry open per Monitor enrichment when DisplayConfig didn't supply a name. Fast (registry is on-disk-cached and access is sub-millisecond), but not free. The `??` short-circuit ensures the cost is zero on machines where DisplayConfig works.
- Couples Periphery to the EDID byte layout (a fixed standard, but if EDID 2.x or a vendor-specific extension ever changes the descriptor format, the parser needs an update). Mitigated by the parser only touching the base block (well-documented) and falling through cleanly when descriptors don't match.
- We now have two code paths that can populate `DisplayName`. The trace logging makes which path was taken visible when needed; the contract from the caller's perspective is unchanged.

### Neutral

- Other enriched fields (Resolution, Bounds, PhysicalConnector, ConnectionKind) remain unpopulated on machines where DisplayConfig fails. This ADR explicitly does not try to backfill those — the base EDID block has resolution information but the active-mode bounds and the connector type are state DisplayConfig assembles from multiple sources, with no clean registry analog.

## Open questions

- **Should the fallback also run when DisplayConfig delivers a non-null but generic name (e.g. `"Default Monitor"`)?** Currently the `??` only fires on null. If a real-world driver sets a stub friendly name we'd miss the better EDID name. No evidence of that today; leaving as-is until a case surfaces.
- **EDID extension blocks.** CEA-861 extension blocks can carry product-name descriptors too. The base block is authoritative when present; if the base block lacks a `0xFC` descriptor but an extension block has one, we'd miss it. No evidence of that on the kiosk; leaving as-is.
- **Does Avalonia have the same problem on this kiosk?** Almost certainly yes (it also uses DisplayConfig). Out of scope here — tracked in the kiosk consumer's BACKLOG under "WindowManagerService monitor-match strategy for Win10 IoT."
