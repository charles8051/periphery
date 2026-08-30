---
title: "ADR-0047: Device Tags vs Multi-Category — Cross-Cutting Classification on DeviceInfo"
status: "Accepted"
date: "2026-05-26"
authors: "@charles8051 (design)"
tags: ["architecture", "decision", "device-category", "device-info", "enrichment", "classification"]
supersedes: ""
superseded_by: ""
---

# ADR-0047: Device Tags vs Multi-Category — Cross-Cutting Classification on DeviceInfo

## Status

### Spike outcome (2026-05-26)

Implemented end-to-end against the proposed design:

- `DeviceInfo.Tags` (`ImmutableHashSet<string>`, default `Empty`) —
  [DeviceInfo.cs](../../src/Periphery/DeviceInfo.cs)
- `DeviceTags` constants (`Hid`, `Battery`, `Audio`) —
  [DeviceTags.cs](../../src/Periphery/DeviceTags.cs)
- `DeviceFilter.WithTag` / `WithAllTags` / `WithAnyTag` —
  [DeviceFilter.cs](../../src/Periphery/DeviceFilter.cs)
- `WindowsBatteryEnricher` now emits `DeviceTags.Battery` alongside
  the battery field updates —
  [WindowsBatteryEnricher.cs](../../src/Periphery/Windows/WindowsBatteryEnricher.cs)
- 91 targeted unit tests pass (DeviceFilterTests, DeviceInfoTests,
  DeviceInfoDiffTests). Full Periphery suite is green except for one
  pre-existing contract test unrelated to tags (a host-hardware
  assumption bug — separate cleanup task).

The design held under implementation with **one significant
surprise** that's worth pinning here:

**`ImmutableHashSet<string>` does NOT give record value-equality for
free.** The `record`-generated `Equals(DeviceInfo)` uses
`EqualityComparer<ImmutableHashSet<string>>.Default` for the `Tags`
property, which is reference equality. Two `DeviceInfo` records with
logically-equal-but-distinct tag sets compare unequal — exactly the
flicker concern NEG-004 flagged, but the *equality* expression
specifically (separate from change-event semantics).

This is consistent with how `ImmutableArray<IPAddress>` and
`PhysicalAddress` already behave on the record: both use custom
element-wise comparison inside `DeviceInfoDiff` rather than relying
on default equality. Tags now follows the same precedent:
`DeviceInfoDiff.Compute` calls `previous.Tags.SetEquals(current.Tags)`
to detect changes, so property-change events fire correctly when an
enricher mutates tag content. Raw `prev == curr` in consumer code is
reference-based for `Tags`; consumers that need content-based
comparison should use the diff helper or `Tags.SetEquals(other.Tags)`
directly.

§1 below originally claimed "value-equality for free" — that claim
has been corrected in place. NEG-004 is updated to reflect the
implemented mitigation (diff helper handles it; raw equality
doesn't).

### Post-spike refinement — Option B (Category fallback in WithTag)

After the initial spike landed, a follow-up question surfaced: should
every `DeviceCategory` value also be emitted as a `Tags` entry, so
consumers can use one filter idiom (`WithTag`) uniformly? Two clean
options:

- **Option A** — Provider auto-tags Category at enumeration time;
  Tags becomes a superset of Category info. Uniform query, storage
  redundancy, blurred semantics (Tags conflates OS classification with
  enricher findings).
- **Option B** — Tags stays enricher-only; `WithTag` consults both
  Tags AND `Enum.GetName(Category)` as a fallback. Same uniform query
  surface, no storage redundancy, semantic boundary preserved.

**Decision: Option B**, reflected in §3 (enrichers don't redundantly
tag their Category) and §4 (`WithTag` / `WithAllTags` / `WithAnyTag`
match Category by name as a fallback via the private `CarriesTag`
helper). Implementation + tests landed in the same commit as this
amendment.

## Context

`DeviceInfo.Category` is a single-valued `DeviceCategory` enum. The value
answers exactly one question: **which OS subsystem surfaced this device?**
On Windows it derives from the SetupAPI class GUID; on Linux from the
udev subsystem; on macOS from the IOKit class. Each platform provider
keeps a 1:1 map between `DeviceCategory` and its routing tokens
(`WindowsCategoryMap`, `LinuxCategoryMap`, `MacOSCategoryMap`), and
every map has a throwing default arm that catches "you forgot to update
a map" errors.

Real devices, however, have cross-cutting classification needs that
don't fit a single OS bucket:

- The **WayTech UPS** (VID `0665:5161`) on the kiosk
  enumerates under Windows `HIDClass` because the vendor ships a
  vendor-defined HID firmware blob. From Windows' perspective it is a
  "USB Input Device." From the application's perspective it is a
  battery — `BatteryChargePercent` and `IsExternalPowerConnected` are
  the fields a consumer cares about. There is no way to express
  "Category = Hid *and* Battery" today.
- **Smart displays** with built-in USB hubs enumerate as `Monitor` for
  the panel and separately as `Usb` / `Audio` for the hub child
  devices. The composite identity ("this is a monitor that also
  provides audio out") is lost.
- **Composite HID devices** that combine a keyboard, mouse, and HID
  consumer-control surface arrive with one container ID but multiple
  capability surfaces. Today they're reported as a single category
  with the others elided.

The motivating concrete failure: the kiosk's `BatteryTracker` filter is
`OfCategory(Battery)`. The WayTech UPS is the only battery on the
machine, but the OS surfaces it under `Hid`, so the tracker never
matches. We have two options that don't require any architectural
change:

1. **Per-device VID/PID override list inside `WindowsCategoryMap`** —
   special-case `0665:5161 → Battery`. Works, but it has the wrong
   semantics: the device *is* a HID device with a battery interface,
   not exclusively a battery. If we ever want to talk Megatec Q1 to it
   we'll need to query it as HID, and now its `Category` field lies
   about what it is.
2. **Filter on capability fields** (`Where(d => d.BatteryChargePercent
   is not null)`) — works for already-enriched devices, but the
   enricher only runs *if a filter says it cares about
   batteries*, which today is gated on `NeedsBatteryEnrichment` which
   is gated on `Category == Battery`. The capability-fields-as-filter
   approach also requires every classification concept to be either a
   typed `DeviceInfo` property or a free lambda predicate, which
   doesn't survive serialization or `DeviceProfile` declarative
   config.

Both of these are workarounds for a missing concept: **a device's
*category* (the bucket the OS gave us) is not the same as a device's
*roles* (the abstract capabilities the application can use it for)**.
This ADR introduces the distinction explicitly.

### Why not make `DeviceCategory` multi-valued

Three structural objections rule out treating `DeviceCategory` itself
as a set.

**1. `[Flags] enum DeviceCategory` is incompatible with ADR-0025's
extension range.** ADR-0025 reserves integer values ≥ 1000 for
extension packages, and an extension package allocates a *single
integer* per category. `[Flags]` requires every value to be a distinct
power of two — which would force the extension range to start at
2^N where N exceeds the count of core categories, and force every new
core or extension category to consume a doubled value. With 19 current
core values and an open-ended extension policy, the value space
quickly becomes unwieldy.

**2. `IReadOnlySet<DeviceCategory>` breaks the routing-map contract.**
Every platform's `CategoryMap.GetXxxTokens(category)` is shaped as
"one category in → one set of OS tokens out." A device with
`{Hid, Battery}` has ambiguous routing semantics: which subsystem
enumeration produced it? Which subsystem does its `Category` field
identify on emission? Which routing tokens does
`Devices.Enumerate().OfCategory(...)` use to scope the OS-level query?
The answers all require deciding on a *primary* category — at which
point we're back to single-valued `Category` plus a secondary set, i.e.
the design this ADR proposes, just spelled differently.

**3. `DeviceFilter.OfCategory(...)` semantics get muddy.** Today the
predicate is `device.Category == filter.Category`. With a multi-valued
category it has to become `device.Category.Contains(filter.Category)`
— and consumers need to learn that `OfCategory(Hid)` will match
batteries-on-HID, monitors-with-HID-controls, and so on. The single
value today already answers "what kind of OS query found this device"
correctly; making it answer "and what *else* could this device be"
overloads it.

### Why not closed-enum Tags

A closed `DeviceTag` enum (`Hid`, `Battery`, `Audio`, ...) inherits all
of `DeviceCategory`'s ADR-0025 baggage — the tag set would also need an
extension-range policy, a registry, and module initialisers — without
the OS-routing payoff that justifies that complexity for
`DeviceCategory`. Tags don't drive subsystem enumeration; they
annotate already-enumerated devices. An open string set is the right
shape for that.

---

## Decision

### 1. Add `Tags` to `DeviceInfo` as an immutable open set

```csharp
public sealed record DeviceInfo
{
    // ... existing fields unchanged ...

    /// <summary>
    /// Cross-cutting capability tags applied during enrichment. Distinct
    /// from <see cref="Category"/>, which identifies the OS subsystem
    /// that surfaced the device. A single device may carry several tags
    /// (e.g. a UPS may be tagged <c>"Hid"</c> + <c>"Battery"</c>;
    /// a smart monitor may be tagged <c>"Monitor"</c> + <c>"Audio"</c>).
    /// </summary>
    /// <remarks>
    /// Tag values are open strings. Well-known tag constants live on
    /// <see cref="DeviceTags"/>; consumers should reference those rather
    /// than spelling literals at call sites.
    /// </remarks>
    public ImmutableHashSet<string> Tags { get; init; }
        = ImmutableHashSet<string>.Empty;
}
```

`Tags` is **always non-null**, defaults to empty, and uses ordinal
string comparison. The `ImmutableHashSet<string>` shape gives
`O(1)` containment checks at filter-evaluation time.

> ⚠ **Implementation correction (spike 2026-05-26):** The originally
> proposed claim that this shape provides "record value-equality
> semantics for free" was wrong. `ImmutableHashSet<string>` uses
> reference equality in the record-generated `Equals` — two records
> with logically-equal-but-distinct tag set instances compare
> unequal. `DeviceInfoDiff.Compute` handles the comparison correctly
> (via `SetEquals`, matching the precedent set for `IPAddresses` /
> `MacAddress`); change-event firing is correct. Consumer code that
> wants content-equality on `Tags` must use `Tags.SetEquals(other)`
> or the diff helper rather than `==` / record `Equals`.

### 2. Add `DeviceTags` as the well-known tag constant registry

```csharp
namespace Periphery;

/// <summary>
/// Well-known capability tag values used by core enrichers. Extension
/// packages may add their own tag values directly — the set is open —
/// but should document them in their own readme alongside any
/// enricher that emits them.
/// </summary>
public static class DeviceTags
{
    /// <summary>HID-protocol device (any HID usage page).</summary>
    public const string Hid = "Hid";

    /// <summary>Reports battery charge level or AC line status.</summary>
    public const string Battery = "Battery";

    /// <summary>Reports audio playback or capture endpoints.</summary>
    public const string Audio = "Audio";

    // ... grow as enrichers gain new classification rules ...
}
```

The string-constant approach mirrors `WellKnownProperties`. Extension
packages add tags by emitting fresh strings — there is no central
registry to update, no `[ModuleInitializer]` dance, no value
allocation table. Tag *meaning* is documented; tag *storage* is
trivial.

### 3. Enrichers populate `Tags` based on observable signals

The existing `*Enricher` types already inspect cross-cutting signals
during enumeration. Each grows a small rule set that adds tags when
the corresponding signal is present:

```csharp
// WindowsBatteryEnricher — adds DeviceTags.Battery when the device
// exposes a usable battery surface, regardless of its DeviceCategory.
internal static DeviceInfo Apply(DeviceInfo device)
{
    var snapshot = TryReadSnapshot();
    if (snapshot is null) return device;

    var builder = device.Tags.ToBuilder();
    builder.Add(DeviceTags.Battery);

    return device with
    {
        BatteryChargePercent = snapshot.Value.BatteryChargePercent,
        BatteryStatus = snapshot.Value.BatteryStatus,
        IsExternalPowerConnected = snapshot.Value.IsExternalPowerConnected,
        Tags = builder.ToImmutable(),
    };
}
```

For HID devices, the existing `HidDeviceEnricher` in `Periphery.Hid`
adds `DeviceTags.Hid` when it successfully reads usage page / usage —
*but only when the device's `Category` isn't already `Hid`*. For a
keyboard or mouse that lives under `Category=Keyboard` / `Mouse` and
also surfaces a HID descriptor, the tag captures the cross-cutting
"this is HID hardware" capability. For a plain HID-class gamepad
already at `Category=Hid`, the tag is redundant with the Category and
should be skipped (see §4 — `WithTag` matches by Category name as a
fallback, so consumers querying `WithTag(DeviceTags.Hid)` find both
shapes uniformly).

A new `WindowsHidBatteryEnricher` (or equivalent enricher rule) adds
`DeviceTags.Battery` when the HID usage page is `0x84` (Power
Device) or `0x85` (Battery System), or when a VID/PID quirk list
matches — concretely solving the WayTech UPS case without lying
about its `Category`.

The detail of *which* enricher owns *which* tag is out of scope for
this ADR; the contract this ADR pins is that `DeviceInfo.Tags` exists,
that core categories of cross-cutting capability are documented as
`DeviceTags.*` constants, and that enrichers are free to add to the
set during enrichment.

**Enricher convention (Option B, see §4):** Don't redundantly tag
a device with its own `Category` name. The `WithTag` filter already
matches `Enum.GetName(device.Category)` as a fallback, so a gamepad
with `Category=Hid` doesn't need `Tags={Hid}` to satisfy
`WithTag(DeviceTags.Hid)`. Add a tag only when it expresses a
capability the Category doesn't already cover.

### 4. Add tag-aware filter predicates to `DeviceFilter` (Option B)

The tag predicates match against **both** the device's `Tags` set
**and** its `Category` (by enum-member name). This is the "Option B"
unification — Tags stays semantically pure (enricher-detected
capabilities), but the *query* surface treats Category as if it were
also a tag. A consumer writing `WithTag(DeviceTags.Hid)` finds plain
HID gamepads (Category=Hid, Tags=empty) and HID-keyboards-tagged-as-Hid
(Category=Keyboard, Tags={Hid}) uniformly, with no Category-vs-Tags
branching at the call site.

```csharp
public sealed class DeviceFilter
{
    // ... existing members ...

    /// <summary>
    /// Match if the device's Tags contains <paramref name="tag"/> OR
    /// the device's Category enum-name equals <paramref name="tag"/>.
    /// </summary>
    public DeviceFilter WithTag(string tag)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        return Where(d => CarriesTag(d, tag));
    }

    /// <summary>Logical AND across <paramref name="tags"/> using <see cref="WithTag"/>'s match rule.</summary>
    public DeviceFilter WithAllTags(params string[] tags) { /* ... CarriesTag for each ... */ }

    /// <summary>Logical OR across <paramref name="tags"/> using <see cref="WithTag"/>'s match rule.</summary>
    public DeviceFilter WithAnyTag(params string[] tags) { /* ... CarriesTag for each ... */ }

    /// <summary>
    /// True if <paramref name="device"/> carries <paramref name="tag"/>
    /// — explicit in Tags or implicit via Category. DeviceCategory.All
    /// never matches any specific tag (the catch-all isn't a claim).
    /// </summary>
    private static bool CarriesTag(DeviceInfo device, string tag)
    {
        if (device.Tags.Contains(tag)) return true;
        if (device.Category == DeviceCategory.All) return false;
        var categoryName = Enum.GetName(device.Category);
        return categoryName is not null
            && string.Equals(categoryName, tag, StringComparison.Ordinal);
    }
}
```

**Why this shape (Option B) over auto-tagging every Category at
enumeration time (Option A):**

- Tags stays semantically pure — "capabilities enrichers detected,"
  not "OS classification + enricher findings smushed together."
- No storage redundancy. Every device doesn't carry a tag that's
  already encoded in its Category field.
- No synchronized lists. Adding a new `DeviceCategory` member doesn't
  require adding a matching `DeviceTags` constant.
- Filter ergonomics are identical between the two options from the
  consumer's point of view — `WithTag("X")` Just Works either way.
- Consumers that need to distinguish "OS said so" vs "enricher
  detected" still have `device.Category` available directly.

The trade-off is that the filter's match logic is one comparison
heavier, and enricher authors must follow the convention in §3 (don't
redundantly tag your Category) to keep the data clean. Both are minor.

The `DeviceTags` constants are defined so that `DeviceTags.Hid ==
Enum.GetName(DeviceCategory.Hid) == "Hid"`. Keep this invariant when
adding new tags whose names mirror a Category — the filter's Category
fallback relies on exact-string equality.

The kiosk's `BatteryTracker` filter becomes:

```csharp
.WithTag(DeviceTags.Battery)
```

instead of `OfCategory(DeviceCategory.Battery)`. The WayTech UPS
matches because the HID/battery enricher tagged it during enrichment,
even though its `Category` remains `Hid`.

### 5. Enrichment gating mirrors filter inspection

`DeviceFilter` already exposes `NeedsBatteryEnrichment` /
`NeedsMonitorEnrichment` so that providers can skip expensive
enrichment when no filter cares. The tag predicates participate in
the same gating: the filter exposes the set of tags it was configured
to care about, and the provider runs the enrichers that *might* emit
those tags. The concrete mechanism (a `RelevantTags` set on
`DeviceFilter`, an enricher-declared `EmitsTags` set on the provider
side, or a lazier "always run enrichers when any non-category
predicate is configured" rule) is deferred to the implementation PR.

### 6. `DeviceProfile` serialisation grows a `Tags` field

`ProfileDefinition` in consumer config gains an optional `Tags` list:

```jsonc
"BatteryTracker": {
    "Profiles": [
        { "Tags": ["Battery"] }
    ]
}
```

The deserialiser maps it to `WithAllTags(...)`. This makes tag-based
profiles first-class in declarative config, matching how `Category`,
`VendorId`, etc. are configured today.

---

## Consequences

### Positive

- **POS-001**: Cross-cutting classification (the WayTech UPS, smart
  monitors, composite HID) becomes expressible without lying about
  `Category` or special-casing inside platform routing maps. A device
  is identified by *which subsystem found it* (`Category`) and *what
  capabilities it offers* (`Tags`) — orthogonal axes that consumers
  can combine.
- **POS-002**: No changes to `DeviceCategory`, no `[Flags]`-vs-extension
  conflict, no break with ADR-0025. The routing-map contract
  (`category → tokens`) stays single-valued and 1:1.
- **POS-003**: `DeviceFilter`'s existing predicate model absorbs tag
  filtering naturally — the tag predicates compose with category,
  VID/PID, name, and lambda predicates the same way every other
  filter does.
- **POS-004**: Open string set keeps extension packages friction-free.
  `Periphery.Ups`, `Periphery.CanBus`, or any future package can emit
  fresh tags without a core PR, registry registration, or module
  initialiser — at the cost of documenting tag *meaning* alongside the
  enricher that emits it.
- **POS-005**: `DeviceProfile` config grows symmetrically. Operators
  configuring a kiosk role can write `"Tags": ["Battery"]` and the
  tracker matches any device the enricher pipeline tagged that way —
  no special-casing per VID/PID.

### Negative

- **NEG-001**: Tag *spelling* becomes a coordination point across
  enrichers. If `WindowsBatteryEnricher` emits `"Battery"` and a
  third-party UPS enricher emits `"battery"`, they don't match. The
  `DeviceTags` constants class documents the canonical spelling for
  every tag the core ships; third-party packages must follow the same
  convention or define their own constants. Comparison is ordinal,
  case-sensitive — a deliberate choice to keep equality checks fast
  and to force a single canonical spelling.
- **NEG-002**: Two ways to ask "is this a battery?": `Category ==
  Battery` (still works for system batteries that enumerated under
  the Battery subsystem) and `Tags.Contains(DeviceTags.Battery)`
  (works for HID UPSes, ACPI batteries, and any future battery
  surface). Consumers must learn which to use. The guidance is:
  filter by `Tags` for capability questions ("what can I use this
  for?"); filter by `Category` only when the OS-level subsystem
  identity itself matters (e.g. "list every device under
  `HIDClass`"). Until the kiosk migrates, both will coexist —
  but the long-term direction is "tags for capabilities, category
  for subsystem identity."
- **NEG-003**: Enrichment-time tag population means tags are
  invisible to OS-level subscription scoping. A `WithTag("Battery")`
  filter can't *narrow* the platform enumeration — every category-bag
  the OS exposes has to be scanned and enriched before tag filtering
  takes effect. For categories with large bags (HID class is the big
  one on Windows: every USB input device passes through it) this
  means slightly more enrichment work per enumeration. Today's
  enricher gating (`NeedsBatteryEnrichment`) already pays this cost;
  the tag system inherits rather than worsens it.
- **NEG-004**: Tag-set churn during a device's lifetime (a HID UPS
  whose battery interface enricher fails on one poll but succeeds on
  the next) is real. Enrichers should treat tag emission as
  *deterministic given the observable signal* — the tag is added if
  the signal is present *now*, removed if not. Episodic transient
  enricher failure leading to tag flicker is a real concern; the
  open-question section flags it.
  **Spike update (2026-05-26):** Change-event behaviour now follows
  the precedent set by `IPAddresses` / `MacAddress` —
  `DeviceInfoDiff.Compute` invokes `previous.Tags.SetEquals(current.Tags)`,
  so `DeviceTracker` observers fire on *content* changes, not
  identity changes. An enricher rebuilding the same tag set in a new
  `ImmutableHashSet` instance does NOT trigger a change event. The
  remaining flicker surface is genuine enricher inconsistency
  (signal-present-then-absent), which is the deterministic-emission
  rule's job to prevent — not an artifact of the equality plumbing.

---

## Alternatives Considered

### A — Multi-valued `DeviceCategory` via `[Flags]`

Convert `DeviceCategory` to a `[Flags]` enum. Rejected: incompatible
with ADR-0025's reserved-integer extension range (extensions allocate
single integers, not powers of two), would force every existing core
value to be re-bitmapped, and overloads `Category`'s semantic role
(subsystem identity) with a different semantic role (capability set).

### B — Multi-valued `DeviceCategory` via `IReadOnlySet<DeviceCategory>`

Treat `Category` as a set rather than a single value. Rejected: every
platform `CategoryMap.GetXxxTokens(category)` would need a set-aware
inverse, `DeviceFilter.OfCategory` semantics become ambiguous
(any-of vs all-of), and the question "which subsystem surfaced this
device?" loses a definite answer. Picking a *primary* category to
keep the routing maps working would push us right back to
"single-valued Category plus a secondary set," which is this ADR.

### C — Tags as a closed enum (`DeviceTag` enum + ADR-0025 extension range)

Type tags as a closed enum mirroring `DeviceCategory`'s extension
model. Rejected: tags don't drive subsystem enumeration, so they get
none of the OS-routing payoff that justifies the routing-map +
registry + `[ModuleInitializer]` complexity for `DeviceCategory`. An
open string set is the right shape for annotation-only data; closed
enum is appropriate when downstream code does exhaustive switching,
which capability tags are not expected to enable.

### D — Tags as `IReadOnlyDictionary<string, object?>` (overload `Properties`)

Reuse the existing `Properties` bag for tag emission. Rejected:
`Properties` is documented as "raw platform-specific data that has no
first-class typed field" — diagnostic and array-typed leftovers.
Cross-cutting capability classification is a deliberate cross-platform
concept, not a diagnostic leftover, and conflating the two makes
both harder to reason about. A dedicated `Tags` field signals the
intent.

### E — Capability fields only (no tags)

Lean on the typed capability fields (`BatteryChargePercent`,
`HidUsagePage`, etc.) and have consumers filter via lambda
predicates. Rejected: doesn't serialise to declarative config
(`DeviceProfile`), can't be configured by an operator without
writing code, and pushes the "is this a battery?" question onto
every consumer rather than centralising it in the enrichment layer.
It also doesn't compose: "any device that can give me a battery
percentage" requires a bespoke predicate at every call site, vs
`WithTag(DeviceTags.Battery)` once.

### F — Per-device VID/PID overrides in `WindowsCategoryMap`

Special-case `0665:5161 → Battery` (and others) in the platform
routing map. Rejected: changes `Category` to claim the device is
something other than what its OS subsystem says it is. We lose the
ability to say "the WayTech UPS *is* a HID device that *also* exposes
a battery interface" — both halves are true and useful (the second
half lets us filter for batteries; the first half tells us how to
talk to it via `Periphery.Hid`).

---

## Open Questions

- **OQ-001**: Should `Category` be derived from `Tags` (e.g.
  if `Tags.Contains(Battery)` and no other category was set, default
  `Category = Battery`)? **Tentative answer: no.** `Category` is the
  OS-subsystem identity. Synthesising it from tags would re-introduce
  the lying-about-category problem this ADR exists to avoid. The
  WayTech UPS should report `Category = Hid` *and* `Tags = {Hid,
  Battery}` — both true, both useful.

- **OQ-002**: Tag-set membership during enrichment churn — should a
  tag disappearing on the next enrichment pass count as a meaningful
  `DeviceInfo` change for `DeviceTracker` purposes? Today
  `DeviceTracker` re-emits on any `DeviceInfo` value-equality change.
  Tag flicker (enricher succeeds, fails, succeeds) would cause
  spurious re-emissions. **Possible mitigation:** enrichers add tags
  but never remove them within a single device's lifetime; tags are
  cleared only on device removal/re-arrival.

- **OQ-003**: Should `DeviceFilter` expose a tag-aware
  `NeedsXxxEnrichment` hint for providers, or is it sufficient to
  treat "any non-category predicate configured" as "run all
  enrichers"? The former is more precise; the latter is simpler. The
  decision depends on whether any enricher is expensive enough that
  always-running matters in practice — `WindowsBatteryEnricher`'s
  `GetSystemPowerStatus` call is cheap, but a future
  `WindowsHidBatteryEnricher` that opens the HID device to read a
  feature report is *not*.

- **OQ-004**: Should tags participate in `DeviceProfile` *negation*
  ("any device *not* tagged `Audio`")? `DeviceFilter` has no negation
  primitive today. If we add `WithoutTag`, it should match
  `Where(_ => false)` semantics for tags that no enricher ever
  emits (a typo'd tag becomes a "match nothing" filter, not a "match
  everything" filter) — but that contradicts how lambda predicates
  compose. Probably out of scope for the first cut; defer until a
  consumer asks for it.

- **OQ-005**: What's the migration path for the kiosk's
  `BatteryTracker`? The cleanest transition is: introduce `Tags`
  and the enricher rules in Periphery, ship as a new alpha, switch
  the kiosk's filter from `OfCategory(Battery)` to
  `WithTag(DeviceTags.Battery)`, leave `DeviceCategory.Battery`
  alone. Devices that the OS itself enumerated under the Battery
  subsystem will be tagged `Battery` by the enricher anyway, so the
  filter migration is one-line and behaviour-preserving for existing
  hardware while unlocking the UPS case.

- **OQ-006**: Should `CarriesTag` (the Option B "Tags-or-Category"
  rule) be promoted to a public helper so consumers working on a
  `List<DeviceInfo>` — rather than building a `DeviceFilter` — don't
  have to re-derive the OR-rule? Concrete example:
  `BatteryListCommand` does
  `d.Tags.Contains(DeviceTags.Battery) || d.Category == DeviceCategory.Battery`
  inline. That's exactly what `WithTag(DeviceTags.Battery)` evaluates
  to, but `WithTag` lives on `DeviceFilter` and isn't applicable
  post-enumeration. The fix is small: lift the `CarriesTag` private
  static on `DeviceFilter` to a public
  `bool DeviceTags.Carries(DeviceInfo, string)` (or equivalent), and
  have both `DeviceFilter.CarriesTag` and post-enumeration call sites
  share it. Single source of truth for the rule; consumers stop
  copying it. **Tentative answer:** yes — the rule is load-bearing
  enough (it's the whole reason Option B exists) that hiding it as a
  private invites silent drift.

---

## Tag Vocabulary — Intent and Future Candidates

This section captures *what tags are for*, recorded at the time the
feature was designed so that future contributors deciding whether to
add a new `DeviceTags` constant have a principle to test against —
not just a precedent to copy.

### Test for "does this deserve a tag"

A property earns its place in `Tags` (and a constant on `DeviceTags`)
only when it meets all of:

1. **Derived.** The answer requires *enrichment* logic — combining
   multiple OS signals, opening the device and probing a feature
   report, matching a VID/PID quirk table, parsing a descriptor.
   If the answer is "read this typed property and compare," it
   doesn't earn a tag; it already has a better home.
2. **Cross-cutting.** Multiple `DeviceCategory` values can carry
   the capability. A HID UPS, a system battery, and a smart
   monitor's built-in UPS are all "Battery." If only one Category
   would ever carry it, `OfCategory(X)` is already the right query.
3. **Capability-framed.** Describes *what the device can do*, not
   *what it is* (Category) or *what its specs are* (typed
   properties). "Touchscreen" is a capability; "1920×1080" is a
   spec.
4. **Filter-relevant.** Consumers genuinely want to ask "give me
   all X-capable devices" without caring about subsystem.
   Speculative capability framings nobody is actually filtering on
   should wait until they are.

### What does NOT deserve a tag

Examples of attractive-but-wrong candidates:

- **`Virtual` / `Physical`** — `BusType == BusType.Software` is a
  one-line check; `VirtualOnly()` / `PhysicalOnly()` already exist.
  Tagging would create an alternative encoding of the same fact.
- **`Active` / `Inactive`** — `IsActive` is a typed bool with a
  filter (`Active(bool)`).
- **`Wired` / `Wireless`** — for displays, `DisplayConnectionKind`
  covers it; for everything else, `BusType` and `MacAddress`
  presence give the answer. No enricher work needed.
- **`HighSpeed` / `LowSpeed`** — `UsbSpeed` is the typed answer.
- **`IPv4Only` / `IPv6Only`** — derivable from `IPAddresses` in
  one line of LINQ.

The pattern: if a single typed property answers the question with a
simple equality check, the question doesn't need a tag.

### Future tag candidates (as of 2026-05-26)

These are the candidates that meet the four-test bar today. Not all
will land — the rule is "add when an enricher needs it," not "add
because we listed it here." This list exists to remind future readers
what the feature was scoped for, and as a sanity check when someone
proposes a tag that doesn't fit.

- **`Touchscreen`** — monitors with touch capability vs without.
  Requires EDID inspection or matching a HID Touch Digitizer
  interface to the same physical container. Cross-cuts
  `Monitor` + `Hid`. No single property says "this monitor
  accepts touch."
- **`AudioInput` / `AudioOutput`** — finer than `Category=Audio`.
  A headset has both; a speaker only the latter; a mic only the
  former. Spans `Audio` + `Bluetooth` + `Hid` (USB headsets).
- **`Pointing`** — anything that produces pointer events: mice,
  trackballs, touchpads, drawing tablets, touchscreens. Cross-cuts
  `Mouse` + `Hid` + `Touchscreen` (the tag).
- **`Biometric`** — already a `DeviceCategory`, but a Windows
  Hello IR camera is *both* `Camera` and `Biometric`. Tagging
  captures the cross-cutting truth that Category alone forces a
  pick between.
- **`SecureElement`** — TPMs, FIDO tokens, smart cards, some
  YubiKeys. Spans `SmartCard` + `Hid` + `Sensor` depending on
  enumeration path; the capability question ("can I store keys
  here?") doesn't map cleanly to any one Category.
- **`Hotpluggable`** — derivable from `BusType` for easy cases
  (USB yes, PCI no), but eSATA, Thunderbolt, and USB4-tunneled
  PCIe blur that. An enricher could give a clean answer where
  the property alone is ambiguous.

### Convention: don't add constants speculatively

Don't add a `DeviceTags.X` constant until there's a concrete
enricher about to emit it. Pre-defining constants invites the
"this looks like Category=X, why bother filtering by tag?" question
under Option B (since `WithTag("X")` would already match Category=X
via the fallback), and the constant ends up either redundant or
misleading about who actually emits it.

The current `DeviceTags.Audio` constant is already in this
borderline state — no audio enricher exists yet, so `WithTag("Audio")`
today only matches `Category=Audio` via the Option B fallback. It
was included on the assumption that an audio enricher would follow
soon; if that assumption changes, the constant should be removed
rather than kept around as a placeholder.
