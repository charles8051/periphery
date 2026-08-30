---
title: "ADR-0004: Two-Level Device State Model (IsPresent + IsConnected)"
status: "Accepted"
date: "2025-07-15"
authors: ""
tags: ["architecture", "decision"]
supersedes: "0001-device-tracking-handles.md"
superseded_by: ""
---

# ADR-0004: Two-Level Device State Model (IsPresent + IsConnected)

**Supersedes:** Portions of ADR-0001 §3–4 (single-state tracker model)

---

## Context

The original design used a single boolean (`IsConnected`, later renamed to `IsPresent`) on `DeviceInfo` to indicate whether a device was physically active. The watcher and monitor provider silently filtered out devices where this was `false`, meaning consumers never saw paired-but-inactive devices. This created three problems:

1. **Redundant filtering.** The `Connected()` / `Present()` filter method was a no-op — every `DeviceInfo` a consumer could receive already had the property set to `true`.

2. **Invisible devices.** Bluetooth devices that are paired but out of range, network adapters that are disabled, and other "known to the OS but not active" devices were silently dropped. Consumers could not distinguish "no device exists" from "device exists but isn't active."

3. **Ambiguous naming.** A single property conflated two distinct states: "the OS has an entry for this device" (present in the device tree) and "the hardware is physically active" (driver started, not disconnected). These diverge for Bluetooth, disabled network adapters, wireless printers, and other categories.

---

## Decision

Introduce a two-level state model with orthogonal concepts:

| Concept | Meaning | Where it lives |
|---|---|---|
| **Present** | Device is known to the OS (installed, paired, plugged in) | `DeviceTracker.IsPresent` / `PresentDevices` |
| **Connected** | Device is physically active (driver started, hardware working) | `DeviceInfo.IsConnected`, `DeviceTracker.IsConnected` / `ConnectedDevices` |

**Invariant:** `IsConnected` implies present. A device cannot be active without existing in the OS device tree.

### DeviceInfo

- `IsConnected` (bool) — `true` when the devnode is started and not flagged as disconnected. Populated by `DevNodeHelper.IsDeviceConnected()` on Windows.
- No `IsPresent` property — if you hold a `DeviceInfo`, the device is present by definition.

### DeviceWatcher — four events

| Event | Trigger | WMI source (Windows) |
|---|---|---|
| `Appeared` | Device enters OS device tree | `__InstanceCreationEvent` |
| `Connected` | Device becomes physically active | `__InstanceCreationEvent` (if `IsConnected`) / `__InstanceModificationEvent` (status transition) |
| `Disconnected` | Device becomes physically inactive | `__InstanceModificationEvent` (status transition) / cascade from `Disappeared` |
| `Disappeared` | Device leaves OS device tree | `__InstanceDeletionEvent` |

**Cascade rule:** When a connected device disappears, the watcher fires `Disconnected` before `Disappeared`. The watcher maintains a `_knownConnectedIds` set to enable this.

### DeviceTracker — dual state

- `IsPresent` / `PresentDevices` — driven by `Appeared` / `Disappeared`
- `IsConnected` / `ConnectedDevices` — driven by `Connected` / `Disconnected`
- `IObservable<bool>` pushes `IsConnected` transitions (the primary "is it available?" signal)
- `StateChanged` fires on either dimension changing
- `Unbind()` clears both sets and fires notifications

### DeviceFilter / DeviceQuery

- `Connected(bool)` — filters on `DeviceInfo.IsConnected`
- No `Present()` method — presence is a tracker/watcher concept, not a snapshot concept

### Q1: Enumeration returns all OS-known devices

The `if (!device.IsConnected) continue` gates in `DeviceWatcher.SnapshotCurrentDevicesAsync` and `WindowsDeviceMonitorProvider.OnDeviceCreated` are removed. Enumeration returns everything the OS knows about. The snapshot fires `Appeared` for all devices and `Connected` only for active ones.

### Q2: `__InstanceModificationEvent` for status transitions

`WindowsDeviceMonitorProvider` subscribes to `__InstanceModificationEvent` alongside creation and deletion events. The `OnDeviceModified` handler compares `PreviousInstance` and `TargetInstance` devnode status (via `DevNodeHelper.IsDeviceConnected`) and fires `DeviceConnected` or `DeviceDisconnected` when the connection state transitions. This enables real-time detection of Bluetooth devices coming into/out of range, network adapters being enabled/disabled, and similar status changes for already-present devices.

---

## Categories affected

| Category | "Known but not active" gap? | Mechanism |
|---|---|---|
| Bluetooth | ✅ Very common | Paired but out of range |
| Network | ✅ Common | Adapter disabled in Settings |
| Audio | ✅ Common | BT audio paired but off; USB DAC unplugged |
| Printer | ✅ Common | Network/BT printer powered off |
| HID | 🟡 Depends on parent | BT mouse paired but off = yes; USB keyboard = no |
| USB | ❌ Rare | Present and connected are equivalent |
| Storage | ❌ Rare | eSATA/hot-swap only |
| Display | ❌ Rare | Cable = present = connected |

---

## Consequences

### Positive

- Consumers can query for paired-but-inactive devices (e.g., "show all known Bluetooth devices").
- `Connected()` filter is no longer a no-op — it has clear, testable semantics.
- The tracker dual state enables dashboard UIs that show "device is set up but not in range" vs "device is active."
- The four-event model cleanly separates OS lifecycle (pair/unpair) from physical state (in-range/out-of-range).
- `__InstanceModificationEvent` enables real-time detection of Bluetooth/network status transitions without polling.

### Negative / Risks

- **Breaking change for snapshot consumers.** `Devices.Enumerate().ToListAsync()` now returns all OS-known devices, not just active ones. Consumers who want the old behavior must add `.Connected()`.
- **Watcher state.** The watcher now maintains `_knownConnectedIds` for cascade logic, adding a small memory and thread-safety surface.
- **Modification event volume.** `__InstanceModificationEvent` fires for *any* `Win32_PnPEntity` property change, not just devnode status. The handler filters to connection-state transitions only, but the WMI polling still occurs at the configured interval.

---

## Files changed

### Core API
- `DeviceInfo.cs` — `IsConnected` property
- `DeviceFilter.cs` — `Connected()` method
- `DeviceQuery.cs` — `Connected()` method
- `DeviceTracker.cs` — Dual state (`IsPresent`/`IsConnected`, `PresentDevices`/`ConnectedDevices`)
- `DeviceWatcher.cs` — Four events, cascade logic, Q1 gate removal
- `IDeviceProvider.cs` — `IDeviceMonitorProvider` with four events
- `DeviceChangeEventArgs.cs` — unchanged (reused for all four events)

### Windows provider
- `DevNodeHelper.cs` — `IsDeviceConnected()`
- `WindowsDeviceProvider.cs` — populates `IsConnected`
- `WindowsDeviceMonitorProvider.cs` — fires `DeviceAppeared`/`DeviceDisappeared`/`DeviceConnected`

### Tests
- `DeviceTrackerTests.cs` — rewritten for dual state, includes USB and Bluetooth scenarios
- All other test files updated for `IsConnected`/`Connected()` naming
- `FakeDeviceMonitorProvider` implements four-event interface
