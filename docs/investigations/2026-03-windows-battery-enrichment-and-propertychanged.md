# Investigation: Windows Battery Fields Missing and `PropertyChanged` Not Firing

**Date:** 2026-03-16  
**Status:** Resolved  
**Affected properties:** `DeviceInfo.BatteryChargePercent`, `DeviceInfo.BatteryStatus`, `DeviceInfo.IsExternalPowerConnected`  
**Affected components:** `WindowsDeviceProvider`, `WindowsDeviceMonitorProvider`, `DeviceWatcher`

---

## Symptoms

On Windows:

- Battery devices were visible, but these fields were always `null`:
  - `BatteryChargePercent`
  - `BatteryStatus`
  - `IsExternalPowerConnected`
- The battery monitoring example did not report battery-related `PropertyChanged` events when AC power was plugged or unplugged.

---

## Initial question

Did this require WinRT enrichment, or could it be implemented with the existing Win32-based provider stack?

---

## Findings

### Finding 1 — The data model already supported battery state

The public model was already correct. `DeviceInfo` included all three battery-related fields:

- `BatteryChargePercent`
- `BatteryStatus`
- `IsExternalPowerConnected`

The issue was not API shape; it was Windows provider population.

---

### Finding 2 — Windows enumeration never populated battery fields

The Windows provider returned `DeviceInfo` snapshots from `WindowsDeviceProvider.ToDeviceInfo(...)`, but that method only populated SetupAPI/cfgmgr32-backed fields plus existing enrichers like network, storage, ports, and display.

Battery-specific values were declared in architecture and ADR docs as intentionally modeled but not yet populated on Windows.

This meant:

- enumeration could return battery devices,
- but the battery status fields were always blank.

---

### Finding 3 — WinRT was not required for the first useful implementation

For the requested values, a Win32 system-power snapshot is sufficient:

- AC connected / disconnected
- battery percentage
- charging flag

`GetSystemPowerStatus` provides these values without:

- WinRT projection packages,
- Windows-specific TFMs,
- COM activation,
- CsWinRT/AOT complexity.

This is a **system-wide** power view, not a per-battery-device telemetry channel, but it is enough to populate the modeled fields for `DeviceCategory.Battery` devices.

---

### Finding 4 — After adding battery enrichment, `PropertyChanged` still did not fire

After enumeration enrichment was added, battery data appeared in initial snapshots, but runtime property changes still did not surface through `DeviceWatcher.PropertyChanged`.

The reason was in the Windows monitor pipeline:

- `WindowsDeviceMonitorProvider` seeds `_lastKnownDevices` at startup.
- It then re-enumerates devices every `PropertyScanInterval` (default 2 seconds).
- It computes diffs via `DeviceInfoDiff.Compute(previous, current)`.

However, both the initial cache seed and the periodic scan used raw `WindowsDeviceProvider.ToDeviceInfo(...)` snapshots, not enriched battery snapshots.

So the comparison pipeline effectively did this:

- previous: raw snapshot with `BatteryChargePercent = null`
- current: raw snapshot with `BatteryChargePercent = null`
- diff: no battery change detected

As a result, no battery-related `PropertyChanged` events were raised even though the watcher plumbing itself was correct.

---

## Root cause

There were two separate Windows gaps:

### Root cause A — No Windows battery enricher existed

Battery fields were modeled but never populated on Windows.

### Root cause B — The monitor provider’s periodic diff path bypassed enrichment

The property-change path compared raw `ToDeviceInfo(...)` snapshots instead of enriched snapshots, so battery mutations never entered the diff set.

---

## Fix implemented

### Fix 1 — Added `WindowsBatteryEnricher`

A new Win32-based enricher was added:

- File: [Periphery/Windows/WindowsBatteryEnricher.cs](../../src/Periphery/Windows/WindowsBatteryEnricher.cs)

It uses `GetSystemPowerStatus` and maps the system power snapshot to:

- `BatteryChargePercent`
- `BatteryStatus`
- `IsExternalPowerConnected`

Status mapping:

- charging flag set → `BatteryStatus.Charging`
- external power + 100% → `BatteryStatus.Full`
- external power + not charging → `BatteryStatus.NotCharging`
- no external power → `BatteryStatus.Discharging`
- unknown OS state → `BatteryStatus.Unknown`

---

### Fix 2 — Wired enrichment into Windows enumeration

The main enumeration path now captures one system battery snapshot per enumeration and applies it to `DeviceCategory.Battery` devices.

- File: [Periphery/Windows/WindowsDeviceProvider.cs](../../src/Periphery/Windows/WindowsDeviceProvider.cs#L69-L111)

This made battery fields appear in normal queries and initial snapshots.

---

### Fix 3 — Wired enrichment into monitor device-arrival path

`TryBuildDeviceInfo(...)` now also applies battery enrichment so newly arriving battery devices carry the same fields.

- File: [Periphery/Windows/WindowsDeviceProvider.cs](../../src/Periphery/Windows/WindowsDeviceProvider.cs#L207-L216)

---

### Fix 4 — Wired enrichment into monitor cache seeding and periodic scan

This was the critical fix for `PropertyChanged`.

`WindowsDeviceMonitorProvider` now:

1. reads a battery snapshot before seeding `_lastKnownDevices`, and
2. reads a battery snapshot on each periodic scan tick before diffing.

Changed locations:

- cache seed: [Periphery/Windows/WindowsDeviceMonitorProvider.cs](../../src/Periphery/Windows/WindowsDeviceMonitorProvider.cs#L121-L135)
- scan loop: [Periphery/Windows/WindowsDeviceMonitorProvider.cs](../../src/Periphery/Windows/WindowsDeviceMonitorProvider.cs#L351-L370)

With this change, the monitor now compares:

- previous: enriched battery snapshot
- current: enriched battery snapshot

So AC plug/unplug and other battery-state changes can now appear in `DeviceInfoDiff` and flow through `DeviceWatcher.PropertyChanged`.

---

## Why `DeviceWatcher` itself was not the bug

`DeviceWatcher` already handled property-change events correctly:

- it listens to provider `DevicePropertyChanged`,
- recomputes the changed property set,
- raises `PropertyChanged` when the watcher filter matches.

Relevant code:

- [Periphery/DeviceWatcher.cs](../../src/Periphery/DeviceWatcher.cs#L789-L803)
- [Periphery/DeviceInfoDiff.cs](../../src/Periphery/DeviceInfoDiff.cs#L33-L72)

`DeviceInfoDiff` already included:

- `BatteryChargePercent`
- `BatteryStatus`
- `IsExternalPowerConnected`

So once the Windows monitor started supplying enriched snapshots, the event pipeline worked as designed.

---

## Validation

Targeted tests were added and passed:

- battery status mapping tests
- enrichment applies only to battery-category devices
- watcher/provider regression tests

Executed validations:

- `WindowsDeviceProviderTests` — passed
- `DeviceWatcherEventTests` — passed

Combined focused run after the monitor fix:

- 33 tests passed, 0 failed

---

## Result

Windows now supports battery enrichment without WinRT for the following fields:

- `BatteryChargePercent`
- `BatteryStatus`
- `IsExternalPowerConnected`

`DeviceWatcher.PropertyChanged` can now report battery state changes on Windows via the existing periodic scan path.

---

## Remaining limitation

This implementation uses **system-wide** power state, not per-physical-battery telemetry.

That means:

- it is correct for common laptop/portable scenarios,
- it is lightweight and TFM-neutral,
- but it does not distinguish multiple batteries independently.

If per-battery-device accuracy becomes necessary later, the next step is a lower-level Windows battery path such as:

- battery device IOCTLs (`IOCTL_BATTERY_QUERY_STATUS`), or
- another per-device battery API.

That would be a separate enhancement, not required for the current feature.

---

## Conclusion

WinRT was **not** required to solve this problem.

The actual missing pieces were:

1. a Windows battery enricher, and
2. using that enrichment inside the Windows monitor’s periodic diff path.

Once both were added, the battery fields appeared and `PropertyChanged` started working for battery state transitions.
