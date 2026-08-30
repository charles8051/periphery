# Extension Category Registry

> **Not implemented.** `DeviceCategory` in
> [`src/Periphery/DeviceCategory.cs`](../src/Periphery/DeviceCategory.cs) is a closed
> enum with values 0–13. There is no extension range, no `DeviceCategoryRegistry`, and
> no `RegisterDisplayName`. [ADR-0025](adr/0025-extensible-device-category.md) is still
> **Proposed**. This file is the allocation table that would go live with it — the
> reservations below are claims on numbers, not descriptions of shipped packages.
>
> Nothing today needs an extension category: capability questions are answered by
> **tags** ([ADR-0047](adr/0047-device-tags-vs-multi-category.md),
> [ADR-0051](adr/0051-demote-capability-categories-to-tags.md)), which need no enum
> value at all. Reach for a category only when a single OS subsystem surfaces the
> device directly on every platform.

This file is the intended allocation table for `DeviceCategory` values in the
**extension range (≥ 1000)**. Core library values (0–999) are defined directly in
the `DeviceCategory` enum in [`src/Periphery/DeviceCategory.cs`](../src/Periphery/DeviceCategory.cs).

See [ADR-0025](adr/0025-extensible-device-category.md) for the full design.

---

## First-party reservations (range 1000–9999)

None of these packages exist. The rows reserve the numbers against the day one does.

| Value | Package | Category constant | Notes |
|---|---|---|---|
| 1000 | `Periphery.CanBus` | `CanBusDeviceCategory.CanBus` | SocketCAN (Linux), PEAK/Vector/Kvaser interfaces (Windows) |
| 1001 | `Periphery.Dmx` | `DmxDeviceCategory.Dmx` | DMX512 / Art-Net USB interfaces |
| 1002 | `Periphery.Infrared` | `InfraredDeviceCategory.Infrared` | Consumer IR transceivers |

*To claim a first-party value: open a PR that adds a row to this table and implements
the corresponding `[ModuleInitializer]` registration in the extension package, including
`RegisterDisplayName` so the value serialises as a string rather than an integer. The
first such PR also has to build the registry itself.*

---

## Third-party range (10000–99999)

Third-party packages that are not part of the first-party Periphery ecosystem should
derive their value using a stable hash of their fully-qualified category name rather
than picking an arbitrary integer:

```csharp
// Stable, culture-invariant DJB2 hash → maps into [10000, 99999]
private static DeviceCategory DeriveExtensionValue(string fullyQualifiedName)
{
    uint hash = 5381;
    foreach (char c in fullyQualifiedName)
        hash = ((hash << 5) + hash) ^ (uint)c;
    return (DeviceCategory)(10_000 + hash % 90_000);
}
```

Third-party packages **must**:
1. Document their derived value in their own README.
2. Check it against the first-party table above to confirm no collision.
3. Treat the value as stable across package versions — changing it is a breaking change.
