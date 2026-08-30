---
title: "ADR-0008: Fluent Tracker Registration API (`AddTracker` / `AddTrackers`)"
status: "Accepted"
date: "2026-03-09"
authors: ""
tags: ["architecture", "decision"]
supersedes: "0001-device-tracking-handles.md"
superseded_by: ""
---

# ADR-0008: Fluent Tracker Registration API (`AddTracker` / `AddTrackers`)

**Supersedes:** Naming portion of ADR-0001 §6 (`Track(...)` registration entry points)

---

## Context

`DeviceWatcher` uses fluent chaining for filters (`OfCategory`, `WithName`, etc.), but tracker registration currently uses `Track(...)` overloads. The `Track(Action<DeviceFilter>)` name reads differently from the rest of the fluent API and can feel inconsistent.

---

## Decision

Add fluent registration methods and keep `Track(...)` as compatibility wrappers:

- `AddTracker(Action<DeviceFilter> configure, string? name = null) : DeviceTracker`
- `AddTracker(DeviceTracker tracker) : DeviceWatcher`
- `AddTrackers(params DeviceTracker[] trackers) : DeviceWatcher`
- `AddTrackers(IEnumerable<DeviceTracker> trackers) : DeviceWatcher`

`Track(...)` overloads remain public and forward to the new methods to avoid breaking existing callers.

---

## Consequences

### Positive

- API reads consistently with existing fluent chain style.
- No breaking change for current users.
- Existing docs/examples can migrate gradually.

### Negative

- Temporary API surface duplication (`Track` + `AddTracker` names).
- Potential long-term deprecation step may be needed.

---

## Examples

```csharp
await using var watcher = Devices.Watch()
    .OfCategory(DeviceCategory.Usb)
    .AddTracker(t => t.WithUsbId("046D", "C52B"), name: "Mouse")
    .AddTrackers(existingTracker1, existingTracker2);

await watcher.StartAsync();
```

```csharp
// Existing code remains valid
var tracker = watcher.Track(t => t.WithUsbId("046D", "C52B"));
```
