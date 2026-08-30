---
title: "ADR-0054: Events over polling — drop the Windows whole-tree property scan"
status: "Accepted"
date: "2026-06-08"
authors: "@charles8051 (profiling + analysis)"
tags: ["architecture", "decision"]
supersedes: "0012-state-change-and-property-change-events.md"
superseded_by: ""
depends_on: ["0005-property-change-events.md", "0009-setupapi-windows-provider.md", "0012-state-change-and-property-change-events.md", "0026-enricher-io-boundary.md", "0048-hid-battery-support.md"]
---

# ADR-0054: Events over polling — drop the Windows whole-tree property scan

**Tracks:** `WindowsDeviceMonitorProvider`, `DeviceWatcher`, `DeviceTracker`
**Depends on:** ADR-0005 (property-change events), ADR-0009 (SetupAPI Windows provider), ADR-0012 (state/property-change events), ADR-0026 (enricher I/O boundary), ADR-0048 (HID battery support)
**Supersedes:** ADR-0012 **Decision 2** (the `PeriodicTimer` whole-tree property re-scan). ADR-0012 Decision 1 (cfgmgr32 instance notifications) and Decision 3 (AOT callback shim) stand.

---

## Context

ADR-0005 committed Periphery to **"no application-level polling"** for `DevicePropertyChanged`. ADR-0009's move from WMI to cfgmgr32 removed the only Windows surface that delivered property changes (cfgmgr32 has no `DEVICEINSTANCEPROPERTYCHANGED` action). ADR-0012 **Decision 2** restored it with a `PeriodicTimer` background loop (`ScanLoopAsync`, default 2 s) that **re-enumerates every device instance, rebuilds a full `DeviceInfo` via `ToDeviceInfo` (+ runs the enrichment pipeline), and diffs against a cache** to synthesize `DevicePropertyChanged` and soft `DeviceConnected`/`DeviceDisconnected`. It argued this was "functionally identical to what WMI's `WITHIN 2` was doing" and flagged: *"Profiling should confirm this before reducing the default interval"* — the cost was assumed to be "single-digit milliseconds" on ~100–300 devices.

That assumption was wrong on real fleet hardware.

### What profiling found (2026-06-08)

On a deployed 2-core / 4-thread box, an out-of-process consumer — which tracks **exactly one device** (a USB payment pad by VID/PID) — sat at a steady **~16 % of one core (~4 % of the 4-thread machine), ~320 ms of CPU per 2 s tick**, idle. Per-thread profiling (`NtQueryInformationThread` Win32-start-address resolution against the full 32-bit module list) attributed 100 % of it to a single **managed thread-pool** workload, traced to `ScanLoopAsync`. It is **not** a vendor ActiveX control (loaded, idle) nor the pipe/STA host code (idle). The same scan runs in the **main kiosk** (many trackers) and is a meaningful slice of its ~25–29 % idle-CPU floor.

The cost compounds because of `DeviceWatcher`: when **any** tracker is registered, the watcher subscribes the monitor provider **unfiltered** (`new DeviceFilter()`), so the scan walks the **entire device tree** every tick even when the consumer cares about one device. cfgmgr32 property reads (`ToDeviceInfo`) are slow Win32 calls; doing them for every node twice a second on weak hardware is the hot path.

### What the scan actually buys (and doesn't)

The stated justification was catching property drift "like battery level." That does not hold up:

- **HID-UPS battery never flows through the scan.** Per ADR-0026 / ADR-0048, `HidBatteryEnricher` is metadata-only — it *tags* a device as a battery (a dictionary lookup, zero I/O) and deliberately leaves `BatteryChargePercent`/`BatteryStatus`/etc. `null`. Live UPS telemetry is a **consumer-side** call to `HidBattery.ReadSnapshotAsync` (a handle-gated codec read). The enricher's presence in the scan exists only to keep the Battery *tag* stable across diffs, not to read a level.
- **Consumer audit (the kiosk consumer).** The *only* consumer of `DevicePropertyChanged` in the kiosk is `BatteryService`, and it reads the OS-populated battery fields off the event — which are `null` for the HID UPS, so on the fleet it is a **no-op**. The kiosk's real battery telemetry is `BatteryService`'s own 10 s `HidBattery` poll, independent of the scan. Screen-role assignment matches monitors by **EDID identity** (arrival/removal *events*), not by a polled resolution property. Nothing else consumes `PropertyChanged`.
- The scan's `PropertyChanged` path is in fact a **latent hazard**: a property diff on the UPS device would hand `BatteryService` a `null`-battery snapshot and clobber the last good poll until the next tick.

So the whole-tree scan pays a per-process, whole-device-tree CPU tax every 2 s to synthesize property-change events that, on this fleet, **nothing usefully consumes** — and the one thing it was meant to catch (battery) is delivered by a consumer poll regardless.

---

## Decision Drivers

| Concern | Requirement |
|---|---|
| Idle CPU | A watcher that tracks presence/identity should poll **nothing**. |
| Keep what works | cfgmgr32 arrival/removal/hard-connect/disconnect events (ADR-0012 D1) and AOT shim (D3) are kept. |
| Honesty about Windows | Windows has **no generic property-change push**. Don't fake one with a tree poll; put freshness where the OS signal (or the lack of one) actually lives. |
| Cross-platform | Linux (udev `change`/`bind`/`unbind`) and macOS (IOKit `kIOGeneralInterest`) deliver property + soft-state changes natively and are unaffected. |
| No regression for real consumers | Audited: removing the Windows scan strands no kiosk consumer (see Context). |

---

## Decisions

### Decision 1 — Remove the Windows whole-tree property re-scan (supersedes ADR-0012 D2)

Delete `ScanLoopAsync` and its `PeriodicTimer`/`_lastKnownDevices` machinery from `WindowsDeviceMonitorProvider`. Windows `DevicePropertyChanged` is **no longer synthesized by polling**. The two cfgmgr32 notification registrations from ADR-0012 Decision 1 remain and are the sole Windows event source:

| cfgmgr32 action | Provider fires |
|---|---|
| `DEVICEINTERFACEARRIVAL` / `DEVICEINSTANCESTARTED` | `DeviceAppeared` / `DeviceConnected` |
| `DEVICEINTERFACEREMOVAL` / `DEVICEINSTANCEREMOVED` | `DeviceDisappeared` / `DeviceDisconnected` |

This restores the *spirit* of ADR-0005 ("no application-level polling"), while being honest that Windows simply does not push property changes — so Periphery stops pretending it can observe them generically.

### Decision 2 — Property freshness is the consumer's responsibility, via the property's own signal

Periphery surfaces **identity and presence** (event-driven). Keeping a mutable property *fresh* belongs to whoever needs it, using the right mechanism for that property:

- **No OS push (HID UPS battery):** the consumer polls that one device on its own cadence — the pattern the kiosk consumer's battery service already implements over `HidBattery.ReadSnapshotAsync` (ADR-0048). This is the *only* legitimate poll, and it is scoped to a single device, not the tree.
- **Has an OS push:** use it. Display resolution/config → `WM_DISPLAYCHANGE` (a windowed/UI consumer already receives this; e.g. Avalonia surfaces it as a screen-change). System (ACPI) battery → `RegisterPowerSettingNotification(GUID_BATTERY_PERCENTAGE_REMAINING)`.

Periphery does **not** walk the device tree to manufacture any of these.

### Decision 3 — `DevicePropertyChanged` stays in the API, fired only from genuine OS push

The `DevicePropertyChanged` event and its diff model (ADR-0005) remain. It fires from native push sources — Linux udev `"change"`, macOS `kIOGeneralInterest` property-change — and is **dormant on Windows** until/unless a *specific* OS notification is wired for a *specific* property (a targeted, event-driven add, never a return to tree polling). The public contract is unchanged; its Windows firing rate simply drops to what the OS actually pushes (≈ none generically).

### Decision 4 — Soft connect/disconnect on Windows becomes event-only

The removed scan also synthesized *soft* `DeviceConnected`/`DeviceDisconnected` (a driver `DN_STARTED` flip with no instance start/stop — chiefly Bluetooth-out-of-range). On Windows this is now a **known gap**: hard plug/unplug/driver-stop is covered by the instance events (Decision 1); genuinely-soft transitions are not observed by default. This is acceptable for the known consumers (wired-USB devices fire hard instance events; the one "device stopped responding" case that matters — the UPS — is detected by the consumer's own poll failing, not by the watcher). If a real soft-state need arises, restore it via the **device-class-specific** signal (e.g. Windows radio/Bluetooth APIs) or a **demand-scoped, status-only** poll of just the tracked device(s) using `CM_Get_DevNode_Status` — never a whole-tree property re-read. Linux/macOS keep native soft-state (udev `bind`/`unbind`, IOKit) unchanged.

---

## Consequences

**Positive**

- A watcher that only tracks presence/identity does **zero idle polling**. That consumer's ~16 %-of-a-core idle workload goes to ~0; the main kiosk's CPU floor drops by the corresponding slice. Reclaimed in *every* process that hosts a `DeviceWatcher`.
- Removes the latent `BatteryService` null-clobber (the scan could overwrite good HID telemetry with `null` on any UPS property diff).
- Simpler `WindowsDeviceMonitorProvider`: no scan task, cache, or lock contention between the scan and the notification callbacks (ADR-0012 explicitly listed that contention + event-duplication as risks — both disappear).
- Brings Windows in line with the event-driven Linux/macOS providers for the common case.

**Negative / accepted**

- **Windows loses generic property-drift detection** for already-present devices (resolution, name, soft-active, etc.). Audited as non-load-bearing for the kiosk consumer (Context). Other future consumers needing a specific property's freshness must wire its specific OS signal or a scoped consumer poll.
- **Platform asymmetry:** Linux/macOS still push property + soft-state changes; Windows pushes only what a specific notification is wired for (none generic today). Documented as the honest state of the Windows PnP surface, not a Periphery deficiency.
- **Soft connect/disconnect gap on Windows** (Decision 4). Restorable narrowly if ever needed.

---

## Alternatives considered

- **Keep polling but scope to tracked devices and lengthen the interval.** Rejected as a half-measure: it is still a per-consumer poll that walks the tree for status, still costs CPU in every watcher-hosting process, and does not deliver the one property it was justified by (battery is a consumer poll regardless). The right answer is no generic poll, not a cheaper one.
- **Status-only whole-tree scan** (drop the `ToDeviceInfo` property read, keep a `CM_Get_DevNode_Status` pass for soft-state). Cheaper, but still an always-on whole-tree walk for a capability no consumer uses. Kept on the shelf as the *scoped, opt-in* path to restore soft-state (Decision 4) rather than an always-on default.
- **Re-introduce WMI `__InstanceModificationEvent WITHIN N`.** Rejected — it is what ADR-0009 deliberately moved off, re-adds the `System.Management` dependency, and polls internally anyway.
- **Wire specific Windows property notifications now** (display, system battery) inside Periphery. Deferred — build them when a consumer actually needs that property's freshness, event-driven and targeted, not speculatively.

---

## Implementation notes (for the follow-up change)

- Delete `ScanLoopAsync`, `_propertyScanInterval`/`PropertyScanInterval`, `_scanCts`/`_scanTask`, `_lastKnownDevices`/`_cacheLock`, and the `WindowsDeviceMonitorProvider` constructor's interval parameter. Keep the two `CM_Register_Notification` paths + the `[UnmanagedCallersOnly]` shim.
- `DeviceWatcher`/`DeviceTracker` keep `PropertyChanged` plumbing; it simply receives fewer events on Windows. Re-check the "unfiltered provider when trackers exist" choice — without the scan, that flag now only affects event fan-out, not a tree walk.
- Update tests that assert Windows property-change/soft-state via the scan (Periphery's "make it right, tests follow architecture" stance — ADR-0026). Drop or re-target them to the event paths.
- Validate on real hardware: redeploy the consumer against the change and re-measure the idle floor (expect its single hot thread to vanish).
