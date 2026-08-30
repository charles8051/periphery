---
title: "ADR-0025: Extensible DeviceCategory — Extension Range and DeviceCategoryRegistry"
status: "Proposed"
status_note: "Not implemented - `DeviceCategory` is still a closed enum with no extension range and no `DeviceCategoryRegistry`. See [extension-category-registry.md](../extension-category-registry.md)."
date: "2026-07-14"
authors: "@charles8051 (design)"
tags: ["architecture", "decision", "extension", "device-category", "registry", "enum"]
supersedes: ""
superseded_by: ""
---

# ADR-0025: Extensible DeviceCategory — Extension Range and DeviceCategoryRegistry

## Context

`DeviceCategory` is a C# `enum` declared in the core `Periphery` library. Every value
in the enum has a corresponding entry in `WindowsCategoryMap`, `LinuxCategoryMap`, and
`MacOSCategoryMap` that translates it to OS-level routing tokens — SetupAPI class GUIDs,
udev subsystem strings, and IOKit class names respectively. These tokens are used to
scope OS-level device subscriptions, so a category with no mapping simply returns no
devices.

The goal established in ADR-0024 (OQ-003) is for an extension package to be able to
ship a new device category — for example `DeviceCategory.CanBus` — such that after
`dotnet add package Periphery.CanBus`, a consumer can write:

```csharp
var devices = await Devices.Enumerate()
    .OfCategory(DeviceCategory.CanBus)
    .ToListAsync();
```

`DeviceCategory.CanBus` must be a real `DeviceCategory` value — not a string, not an
`int` cast at the call site, not a wrapper type. The OS subscription must work correctly
on all three platforms without any manual registration by the consumer.

### Why the enum cannot simply be extended from another assembly

C# `enum` is a value type with a closed set of named members. Named members can only be
declared in the assembly that defines the enum. An extension package *can* cast any
integer into a `DeviceCategory` at runtime — `(DeviceCategory)1000` is valid C# — but
it cannot declare a *named member* `DeviceCategory.CanBus` in the core enum without a
core library PR.

The resolution is to separate two concerns that have been conflated:

1. **Naming** — where `DeviceCategory.CanBus` is declared and what integer value it has.
2. **Routing** — how the three platform providers translate that value to OS tokens.

For (1), the extension package declares the name as a `public const DeviceCategory`
field in a well-known value range. For (2), the extension package registers its OS
mappings in the core `DeviceCategoryRegistry` at module load time.

### Why not an injectable `ICategoryMap` per provider

An interface-per-platform approach (`IWindowsCategoryMap`, `ILinuxCategoryMap`,
`IMacOSCategoryMap`) would require extension packages to implement all three platform
interfaces and consumers to register them manually against each provider. This creates
three-platform implementation burden per extension, leaks platform-specific types into
cross-platform extension packages, and requires explicit consumer wiring that contradicts
the goal of zero-configuration `dotnet add package` ergonomics.

### Why not an abstract record / discriminated union for `DeviceCategory`

Converting `DeviceCategory` to an abstract record type would break `switch` expression
exhaustiveness (no closed set to be exhaustive over), `JsonStringEnumConverter`, every
`==` comparison, and the throwing default arm in all three category maps — which is the
load-bearing safety net that catches "you forgot to update a map" errors. The int-cast
approach retains all of these while still allowing extension packages to declare
named constants.

---

## Decision

### 1. Reserve an extension range in `DeviceCategory`

The core `DeviceCategory` enum documents a reserved extension range for values that are
not defined by the core library. Values in this range are valid `DeviceCategory` integers
and will not be assigned to core categories in future versions:

```csharp
public enum DeviceCategory
{
    All = 0,

    Usb,
    Bluetooth,
    // ... all current core values ...
    Camera,

    // ── Extension range ───────────────────────────────────────────────────────
    // Values from 1000 upward are reserved for extension packages.
    // Core library releases will never assign a named value in this range.
    // Extension packages declare their values here as public const fields
    // (see ADR-0025 and docs/extension-category-registry.md).
}
```

No enum member is declared in the extension range. Extension packages declare their own
named constants:

```csharp
// In Periphery.CanBus
namespace Periphery.CanBus;

public static class CanBusDeviceCategory
{
    /// <summary>CAN bus interfaces and adapters.</summary>
    /// <remarks>
    /// Registered extension category — requires <c>Periphery.CanBus</c>.
    /// Value: <c>(DeviceCategory)1000</c> (ADR-0025 extension range).
    /// </remarks>
    public const DeviceCategory CanBus = (DeviceCategory)1000;
}
```

Consumers import the constant with a `using static`:

```csharp
using static Periphery.CanBus.CanBusDeviceCategory;

var adapters = await Devices.Enumerate()
    .OfCategory(CanBus)        // resolves to (DeviceCategory)1000
    .ToListAsync();
```

Or reference it directly:

```csharp
.OfCategory(CanBusDeviceCategory.CanBus)
```

### 2. Add `DeviceCategoryRegistry` to the core library

A thread-safe static registry in the core library holds extension category mappings
for all three platforms:

```csharp
// In Periphery core
public static class DeviceCategoryRegistry
{
    // Thread-safe: written once at module init, read many times during enumeration.
    private static readonly ConcurrentDictionary<DeviceCategory, string[]> s_windowsGuids   = new();
    private static readonly ConcurrentDictionary<DeviceCategory, string[]> s_linuxSubsystems = new();
    private static readonly ConcurrentDictionary<DeviceCategory, string[]> s_macOSClasses    = new();
    private static readonly ConcurrentDictionary<string, DeviceCategory>   s_windowsReverse  = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, DeviceCategory>   s_linuxReverse    = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, DeviceCategory>   s_macOSReverse    = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<DeviceCategory, string>   s_displayNames    = new();
    private static readonly ConcurrentDictionary<string, DeviceCategory>   s_nameToCategory  = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Registers the Windows SetupAPI class GUIDs for an extension category.
    /// Call from a <c>[ModuleInitializer]</c> in the extension package.
    /// </summary>
    public static void RegisterWindows(DeviceCategory category, params string[] classGuids)
    {
        s_windowsGuids[category] = classGuids;
        foreach (var guid in classGuids)
            s_windowsReverse.TryAdd(guid, category);
    }

    /// <summary>
    /// Registers the Linux udev subsystem strings for an extension category.
    /// Call from a <c>[ModuleInitializer]</c> in the extension package.
    /// </summary>
    public static void RegisterLinux(DeviceCategory category, params string[] subsystems)
    {
        s_linuxSubsystems[category] = subsystems;
        foreach (var sub in subsystems)
            s_linuxReverse.TryAdd(sub, category);
    }

    /// <summary>
    /// Registers the macOS IOKit class names for an extension category.
    /// Call from a <c>[ModuleInitializer]</c> in the extension package.
    /// </summary>
    public static void RegisterMacOS(DeviceCategory category, params string[] ioKitClasses)
    {
        s_macOSClasses[category] = ioKitClasses;
        foreach (var cls in ioKitClasses)
            s_macOSReverse.TryAdd(cls, category);
    }

    /// <summary>
    /// Registers a stable display name for an extension category.
    /// Used by <c>DeviceCategoryJsonConverter</c> for serialisation and by
    /// <see cref="TryGetDisplayName"/> for diagnostics and UI display.
    /// The name must be a stable identifier — changing it across package versions
    /// is a breaking change for any serialised data that uses it.
    /// Call from a <c>[ModuleInitializer]</c> in the extension package.
    /// </summary>
    public static void RegisterDisplayName(DeviceCategory category, string name)
    {
        s_displayNames[category] = name;
        s_nameToCategory.TryAdd(name, category);
    }

    /// <summary>
    /// Returns the registered display name for an extension category,
    /// or <c>null</c> if none has been registered.
    /// </summary>
    public static bool TryGetDisplayName(DeviceCategory category, out string? name)
    {
        name = null;
        return s_displayNames.TryGetValue(category, out name!);
    }

    /// <summary>
    /// Resolves a display name back to a <see cref="DeviceCategory"/>.
    /// Used by <c>DeviceCategoryJsonConverter</c> during deserialisation.
    /// </summary>
    public static bool TryResolveByName(string name, out DeviceCategory category)
        => s_nameToCategory.TryGetValue(name, out category);

    internal static bool TryGetWindowsGuids(DeviceCategory c, out string[] guids)
        => s_windowsGuids.TryGetValue(c, out guids!);

    internal static bool TryGetLinuxSubsystems(DeviceCategory c, out string[] subsystems)
        => s_linuxSubsystems.TryGetValue(c, out subsystems!);

    internal static bool TryGetMacOSClasses(DeviceCategory c, out string[] classes)
        => s_macOSClasses.TryGetValue(c, out classes!);

    internal static bool TryResolveWindows(string guid, out DeviceCategory category)
        => s_windowsReverse.TryGetValue(guid, out category);

    internal static bool TryResolveLinux(string subsystem, out DeviceCategory category)
        => s_linuxReverse.TryGetValue(subsystem, out category);

    internal static bool TryResolveMacOS(string ioKitClass, out DeviceCategory category)
        => s_macOSReverse.TryGetValue(ioKitClass, out category);
}
```

### 3. Update the three platform category maps to consult the registry

Each map's `_ =>` default arm now checks the registry before throwing:

```csharp
// WindowsCategoryMap.GetClassGuids — default arm updated
_ => DeviceCategoryRegistry.TryGetWindowsGuids(category, out var guids)
     ? guids
     : throw new ArgumentOutOfRangeException(nameof(category), category,
         $"Unknown DeviceCategory '{category}'. If this is an extension category, " +
         $"ensure the extension package assembly is loaded before enumerating.")
```

The inbound `ResolveCategory` methods also consult the registry after their own table
misses:

```csharp
// WindowsCategoryMap.ResolveCategory — registry fallback
internal static DeviceCategory ResolveCategory(string? classGuid)
{
    if (classGuid is null) return DeviceCategory.All;
    if (s_guidToCategory.TryGetValue(classGuid, out var cat)) return cat;
    if (DeviceCategoryRegistry.TryResolveWindows(classGuid, out var extCat)) return extCat;
    return DeviceCategory.All;
}
```

### 4. Extension packages register in a `[ModuleInitializer]`

`[ModuleInitializer]` methods run automatically when the assembly is first loaded by the
CLR — which happens the moment any type from the assembly is referenced. Because the
consumer references `CanBusDeviceCategory.CanBus` (a type from `Periphery.CanBus`), the
assembly is loaded, the initializer fires, and the registration is complete before the
first call to `Devices.Enumerate()`.

```csharp
// In Periphery.CanBus — internal, automatic, zero consumer friction
internal static class CanBusCategoryRegistration
{
    [ModuleInitializer]
    internal static void Register()
    {
        DeviceCategoryRegistry.RegisterWindows(
            CanBusDeviceCategory.CanBus,
            "{c166523c-fe0c-4a94-a586-f1a80cfbbf3e}"); // Windows CAN bus device interface GUID

        DeviceCategoryRegistry.RegisterLinux(
            CanBusDeviceCategory.CanBus,
            "can");                                      // SocketCAN subsystem

        DeviceCategoryRegistry.RegisterMacOS(
            CanBusDeviceCategory.CanBus,
            "IOCANBusInterface");                        // IOKit class (if applicable)
    }
}
```

### 5. `DeviceCategoryJsonConverter` — consistent serialisation across core and extension values

The existing `JsonStringEnumConverter<DeviceCategory>` on the `DeviceCategory` enum
serialises core values as their member name (`"Usb"`, `"Midi"`) and extension values as
their raw integer (`1000`). To give extension values the same string serialisation as
core values, the core library ships a `DeviceCategoryJsonConverter` that:

1. For **serialisation**: writes the `Enum.GetName` result for core values; falls back
   to `DeviceCategoryRegistry.TryGetDisplayName` for extension values; falls back to
   the integer string representation if no display name is registered.
2. For **deserialisation**: tries `Enum.Parse` first (handles core values and any
   extension value whose name string matches a registered display name); falls back to
   `DeviceCategoryRegistry.TryResolveByName`; falls back to `int.Parse` for raw integer
   round-trips.

```csharp
// In Periphery core — replaces JsonStringEnumConverter<DeviceCategory> on the enum
public sealed class DeviceCategoryJsonConverter : JsonConverter<DeviceCategory>
{
    public override DeviceCategory Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
            return (DeviceCategory)reader.GetInt32();

        var s = reader.GetString();
        if (s is null) return DeviceCategory.All;

        // Core value by name (fast path)
        if (Enum.TryParse<DeviceCategory>(s, ignoreCase: true, out var known))
            return known;

        // Extension value by registered display name
        if (DeviceCategoryRegistry.TryResolveByName(s, out var ext))
            return ext;

        return DeviceCategory.All; // graceful degradation for unknown future values
    }

    public override void Write(Utf8JsonWriter writer, DeviceCategory value, JsonSerializerOptions options)
    {
        // Core value — use enum member name
        var name = Enum.GetName(value);
        if (name is not null)
        {
            writer.WriteStringValue(name);
            return;
        }

        // Extension value — use registered display name if available
        if (DeviceCategoryRegistry.TryGetDisplayName(value, out var displayName))
        {
            writer.WriteStringValue(displayName);
            return;
        }

        // No name registered — fall back to integer
        writer.WriteNumberValue((int)value);
    }
}
```

Extension packages that register a display name get string JSON output automatically.
Extension packages that do not register a name get integer output — which round-trips
correctly as long as the same extension package is loaded on deserialisation.

The `[ModuleInitializer]` in the extension package registers the display name alongside
the OS mappings:

```csharp
[ModuleInitializer]
internal static void Register()
{
    DeviceCategoryRegistry.RegisterDisplayName(CanBusDeviceCategory.CanBus, "CanBus");

    DeviceCategoryRegistry.RegisterWindows(
        CanBusDeviceCategory.CanBus,
        "{c166523c-fe0c-4a94-a586-f1a80cfbbf3e}");

    DeviceCategoryRegistry.RegisterLinux(
        CanBusDeviceCategory.CanBus,
        "can");

    DeviceCategoryRegistry.RegisterMacOS(
        CanBusDeviceCategory.CanBus,
        "IOCANBusInterface");
}
```

With this in place, a `DeviceInfo` containing `Category = CanBusDeviceCategory.CanBus`
serialises as `"Category": "CanBus"` — indistinguishable from a core category in JSON
output.

### 6. Value allocation policy

Extension values in the range ≥ 1000 are allocated by first-party Periphery packages
in `docs/extension-category-registry.md`, which acts as the canonical allocation table.
Each entry records the value, the package that owns it, and the category name:

| Value | Package | Category name |
|---|---|---|
| 1000 | `Periphery.CanBus` | `CanBus` |
| 1001 | `Periphery.Dmx` | `Dmx` |
| 1002 | `Periphery.Infrared` | `Infrared` |
| *(next)* | *(claim via PR)* | |

For third-party packages not in the first-party ecosystem, a deterministic hash of the
category's fully-qualified name into the range 10000–99999 is used to reduce collision
probability without requiring central coordination:

```csharp
// Third-party collision-resistant value derivation
private static DeviceCategory DeriveCategory(string fullyQualifiedName)
{
    uint hash = (uint)fullyQualifiedName.GetDjb2HashCode(); // stable, not culture-sensitive
    return (DeviceCategory)(10_000 + hash % 90_000);
}
```

First-party Periphery packages must not use the 10000–99999 range. Third-party packages
must document their derived value and check it against any known first-party values.

---

## Consequences

### Positive

- **POS-001**: Consumers get a real, named `DeviceCategory.CanBus` value — not a string
  cast, not a wrapper type — after `dotnet add package Periphery.CanBus`. The call site
  is identical to built-in categories.
- **POS-002**: Zero consumer registration friction. `[ModuleInitializer]` fires
  automatically when the extension assembly is loaded, which is guaranteed by the time
  `CanBusDeviceCategory.CanBus` is referenced.
- **POS-003**: The core `enum` remains a real `enum`. `switch` exhaustiveness over known
  values is preserved. `JsonStringEnumConverter` continues to work for core values.
  Extension values serialise as their integer representation (or can be given a custom
  converter in the extension package).
- **POS-004**: The throwing default arm in each platform category map is preserved as a
  safety net for genuinely unregistered values — it fires only if a value in the
  extension range is used without the corresponding package being loaded.
- **POS-005**: The registry is read-mostly after module init. `ConcurrentDictionary`
  read paths are lock-free; no enumeration-time contention.
- **POS-006**: Platform-specific types stay in platform-specific packages. `Periphery.CanBus`
  registers all three platform mappings from a single cross-platform assembly — the
  registration strings (GUIDs, subsystem names, IOKit class names) are just string
  constants, not platform API calls.

### Negative

- **NEG-001**: `DeviceCategory` values in the extension range (≥ 1000) cannot be used in
  exhaustive `switch` expressions over `DeviceCategory` without a default arm. Consumers
  who `switch` on `DeviceCategory` must already have a default arm for `Unknown` /
  future values — this is not a new requirement, but it is worth documenting.
- **NEG-002**: Extension category values serialise as integers if the extension package
  does not call `RegisterDisplayName` in its `[ModuleInitializer]`. Well-behaved
  extension packages should always register a display name. The integer representation
  still round-trips correctly as long as the same extension package is loaded on
  deserialisation.
- **NEG-003**: Value collisions between independently authored extension packages in the
  10000–99999 third-party range are theoretically possible. The hash-based derivation
  reduces (but does not eliminate) this risk. First-party packages avoid it entirely via
  the allocation table.
- **NEG-004**: `[ModuleInitializer]` runs at assembly load time — before `Main`, before
  dependency injection is configured, and on whatever thread first triggers the load.
  The registration code must be fast, allocation-light, and side-effect-free beyond
  populating the registry dictionaries.

---

## Alternatives Considered

### A — String-keyed registry with typed wrappers

Extension packages register under a string name (`"CanBus"`) and expose a typed wrapper
that resolves back to a string at the call site. Rejected: the call site requires a
cast or wrapper type; `OfCategory(CanBus.Category)` is worse ergonomics than
`OfCategory(CanBusDeviceCategory.CanBus)`, and the string loses the type system entirely
at the point where it matters most — the filter predicate.

### B — Injectable `ICategoryMap` per provider

Each extension package implements `IWindowsCategoryMap`, `ILinuxCategoryMap`, and
`IMacOSCategoryMap` and the consumer registers them against each provider. Rejected:
three-platform implementation burden per extension package; platform-specific types
leak into cross-platform extension packages; requires explicit consumer registration
that defeats the `dotnet add package` ergonomics goal (see Context above).

### C — Abstract record / discriminated union for `DeviceCategory`

`DeviceCategory` becomes an abstract record with `KnownCategory` and `ExtensionCategory`
derived types. Extension packages declare `new ExtensionCategory("CanBus")`. Rejected:
breaks `switch` exhaustiveness, `JsonStringEnumConverter`, `==` comparisons, and the
throwing default arm safety net. The naming collision between `Periphery.DeviceCategory`
(the abstract record) and `Periphery.CanBus.DeviceCategory` (a static class of constants)
is confusing. The int-cast approach gives identical call-site ergonomics with none of
these costs.

### D — Core PR required for every new category (status quo)

Every new category, regardless of which package ships it, requires a PR to the core
`Periphery` library. Rejected for first-party extension packages: it couples release
cadences unnecessarily when the mapping data is owned by the extension package and the
core infrastructure is already in place. Accepted as the policy for categories that
belong in the core enum directly (i.e. categories that are universally useful, have
cross-platform OS support, and are expected to be enumerated without any extension
package).

---

## Open Questions

- **OQ-001**: ~~Should `DeviceCategory` serialise extension values as integers or as a
  name string?~~ **Resolved.** `DeviceCategoryJsonConverter` is added to the core
  library and replaces `JsonStringEnumConverter<DeviceCategory>` on the enum attribute.
  It writes the registered display name for extension values (falling back to the integer
  representation if no name is registered) and resolves names back to values on
  deserialisation via `TryResolveByName`. Extension packages must call
  `RegisterDisplayName` in their `[ModuleInitializer]` to get string JSON output.

- **OQ-002**: ~~Should the extension range start at 1000 or at `int.MaxValue / 2`?~~
  **Resolved — keep 1000.** The entire realistic universe of distinct, OS-addressable
  hardware device categories that could ever have cross-platform enumeration support is
  on the order of 30–50 values. The core enum range of 0–999 is already an order of
  magnitude more than will ever be used. A higher base buys nothing in practice and
  makes the extension values harder to read in diagnostics and debug output.
