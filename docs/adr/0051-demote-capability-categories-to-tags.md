---
title: "ADR-0051: Demote Capability-Derived Categories to Tags — Category = Subsystem Identity, Tags = Capability"
status: "Accepted"
date: "2026-05-31"
authors: "@charles8051 (design)"
tags: ["architecture", "decision", "device-category", "device-tags", "enrichment", "refactor", "load-bearing", "macos", "linux", "classification", "breaking-change"]
supersedes: ""
superseded_by: ""
---

# ADR-0051: Demote Capability-Derived Categories to Tags

## Status

> **Load-bearing refactor.** This changes the public `DeviceCategory` enum (removes
> five members), relocates classification logic out of the three platform category
> maps into enrichers, and adds enumeration-scoping infrastructure for tags. It
> partially supersedes **ADR-0014** (the macOS Tier-2 coverage just shipped for
> exactly the categories being demoted) and builds directly on **ADR-0047** (the
> Category-vs-Tags split). It has one external consumer in its blast radius —
> **The kiosk consumer** — analysed in detail below.

---

## Context

### The conflation that only Windows hides

`DeviceCategory` is a single-valued enum that, in practice, answers two different
questions at once:

1. **"Which OS subsystem surfaced this device?"** — the routing identity.
2. **"What kind of device is it?"** — the capability.

On **Windows** these collapse into one clean token: a SetupAPI device setup-class GUID
is 1:1 per devnode, so `Category = Printer` is simultaneously "the OS put this in the
Printer setup class" and "this is a printer." The model feels right because the platform
it was first built against makes the two questions coincide.

On **Linux** (udev subsystems) and **macOS** (IOKit class hierarchy) they do *not*
coincide, and ADR-0047 already named the fix — `Category` = subsystem identity, `Tags` =
capability — but only applied it to one cross-cutting case (Battery, ADR-0048). As the
Linux and macOS backends harden, the conflation stops being a latent wart and becomes an
active source of broken queries and per-platform special-casing. This ADR takes the
ADR-0047 split to its conclusion for the categories where it actually bites.

### The seams are in the code today, not hypothetical

Three concrete impedance mismatches, confirmed against the current maps:

**Seam (a) — Forward maps are non-injective; you must fetch-superset-then-filter.**
`LinuxCategoryMap.GetSubsystems` routes `Imaging`, `Biometric`, `SmartCard`, `Printer`
all to `["usb"]`; `MacOSCategoryMap.GetIOKitClasses` routes `Printer`, `Imaging`,
`Biometric` all to `IOUSBDevice`/`IOUSBHostDevice` and `Sensor` to `IOHIDDevice`. The
category cannot scope the OS query, so the provider fetches the whole bus and filters
in memory — ADR-0014 formalised this as the "Tier 2 post-filter" approach.

**Seam (b) — Reverse maps are lossy, and Linux lacks the disambiguation macOS has.**
`MacOSCategoryMap` carries `ResolveUsbCategory` (USB class `0x06→Imaging`, `0x07→Printer`,
`0x0B→SmartCard`) and `ResolveHidCategory` (usage page `0x20→Sensor`).
`LinuxCategoryMap.ResolveCategory` switches on the **subsystem string only**
(`"usb" => DeviceCategory.Usb`, flat — confirmed: it is the sole resolver, called at
`LinuxDeviceProvider.cs:176`, with no USB-class-code inspection). Consequence as the code
stands: `OfCategory(Printer | Imaging | SmartCard | Biometric)` on Linux fetches the
`usb` superset, relabels every device `Usb`, and — since `DeviceFilter.Matches` gates on
`device.Category == filter.Category` — **matches nothing**. Those four categories are
effectively non-functional on Linux right now.

**Seam (c) — Single-valued `Category` can't express fan-out, which is the norm off
Windows.** One physical device surfaces as many subsystem nodes on Linux/macOS (a USB
headset: `sound` + `input` + `hid` + `usb`), each forced into one category. This is
ADR-0047's motivating problem, structurally worse off Windows (where `ContainerId` at
least groups the interfaces).

### Why now

Three forcing functions converge:

- **ADR-0014 just shipped Tier-2 macOS coverage** for `Printer`, `Imaging`, `Biometric`,
  `Sensor` — the exact categories this ADR demotes. Continuing to invest in making them
  first-class categories on the generic-bus platforms is throwing good effort after a
  model we've decided is wrong. Better to redirect that just-written `ResolveUsbCategory`
  / `ResolveHidCategory` logic into the tag layer before more code is built on it.
- **The GPS/`Serial.Nmea` decision** (ADR-0050 and its follow-ups) already chose
  "`Category = Ports` + `{Gps}` tag" over a `DeviceCategory.Gps`. That is instance #1 of
  the principle this ADR generalises; ratifying the principle keeps GPS from being a
  one-off.
- **`Periphery`'s "no consumers" stance is no longer strictly true.** The kiosk consumer
  is a live consumer (on the floated `1.0.0-alpha.*` feed). The blast radius below treats
  it as a real, if light, migration — exactly the moment to make a breaking change cleanly
  rather than after more consumers appear.

### Prerequisite recap (ADR-0047, implemented)

`DeviceInfo.Tags` (`ImmutableHashSet<string>`, default empty), `DeviceTags` constants,
`DeviceFilter.WithTag/WithAllTags/WithAnyTag`, and the shared `DeviceTags.Carries` rule
all exist and are exercised by the Battery case. `Carries` implements ADR-0047 "Option B":
`WithTag("X")` matches if `device.Tags.Contains("X")` **or**
`Enum.GetName(device.Category) == "X"`. This fallback is load-bearing for the migration
story (see Decision §6).

---

## Decision

### 1. The split: which categories stay, which become tags

**Keep as `DeviceCategory`** — each maps to a dedicated, distinct subsystem/class on all
three platforms (the routing identity *is* the answer):

`All`, `Usb`, `Bluetooth`, `Network`, `Display`, `Monitor`, `Hid`, `Keyboard`, `Mouse`,
`Audio`, `Storage`, `Ports`, `Battery`.

**Demote to capability tags** — off Windows these are uniformly "a USB/HID device that
happens to be X," distinguished only by a class code, HID usage page, or niche subsystem:

`Imaging`, `Biometric`, `SmartCard`, `Printer`, `Sensor`.

### 2. Per-category detection signal (the relocation map)

The demotion is mostly **relocation of logic that already exists** in the reverse maps,
not new detection invention. Each demoted category's enricher reads the same platform
signal that drives its category mapping today and emits a tag instead of setting
`Category`:

| Demoted → Tag | Windows signal | Linux signal | macOS signal | Risk |
|---|---|---|---|---|
| **`Sensor`** | `DeviceClassGuids.Sensor` GUID | `iio` subsystem | HID usage page `0x20` | Low. Three genuinely distinct device populations today — the textbook capability-not-subsystem case. |
| **`SmartCard`** | `SmartCardReader` GUID | `usb` + USB class `0x0B` | `IOUSBSmartCardController` / USB class `0x0B` | Low. macOS had a direct IOKit class (Tier 1). |
| **`Imaging`** | `Image` GUID | `usb` + USB class `0x06` | USB class `0x06` | Low. |
| **`Biometric`** | `Biometric` GUID | `usb` (best-effort) | USB class `0xFF` (best-effort) | Medium. Weakest signal off Windows; macOS cannot see Touch ID regardless (ADR-0014 NEG-002). |
| **`Printer`** ⚠ | `Printer` **+** `PnpPrinters` **+** `PrintQueue` GUIDs (the spooler stack) | `usb` + USB class `0x07` | USB class `0x07` | **High.** On Windows the signal is the print-spooler setup-class family, **broader than USB class 0x07** — the Windows enricher must key off the three class GUIDs, not the USB class code. This is the category the kiosk consumer depends on. |

### 3. Resolved `Category` for demoted devices

After demotion, a device whose only classification was a demoted category resolves to
**`DeviceCategory.All`** on Windows (the catch-all the reverse map already returns for
unmapped class GUIDs), or to its honest bus category where one applies (`Usb` on
Linux/macOS for the USB-class cases). The capability is carried entirely by the tag. This
is acceptable precisely because the tag — not `Category` — is now the query surface for
these devices (see §6).

### 4. Enrichers are metadata-only and ADR-0026 compliant

Every demoted-category enricher reads OS metadata already available at enumeration time —
the Windows `ClassGuid`/`ClassName` the provider already populates, udev properties
(`ID_USB...`, USB class), or IORegistry property dictionaries. **No enricher opens a
device handle or performs device I/O** (ADR-0026 hard contract). The macOS
`ResolveUsbCategory` / `ResolveHidCategory` helpers move wholesale into enrichers; the
Linux gap (Seam b) is closed by the same enrichers reading the USB class code that
`LinuxCategoryMap` never inspected. Net: little novel logic, mostly relocation.

### 5. Enumeration-scoping & gating — the one piece of genuinely new infrastructure

This is the load-bearing addition and the part most likely to be gotten wrong. ADR-0047
§5 deferred the exact gating mechanism; this ADR pins it.

**Problem.** Today the provider scopes the OS query from `DeviceFilter.Category` via the
forward map. Remove the five arms and a bare `WithTag("Printer")` query (`Category = All`)
would enumerate *everything*, enrich, then filter — correct but needlessly broad — and,
worse, if the emitting enricher is gated off, the tag is never set and the query **silently
matches nothing**.

**Decision.** Give tag-emitting enrichers two declarations:

- `EmitsTags` — the set of tag strings this enricher can produce.
- An **internal per-platform enumeration scope** — the same class GUIDs / udev subsystems /
  IOKit classes that used to live in the forward category map, now owned by the enricher.

When a filter requests tag `T`, the provider:
1. **Unions** the enumeration scope of every registered enricher whose `EmitsTags`
   contains `T` into the OS query scope (so `WithTag("Printer")` enumerates the printer
   class GUIDs / USB bus, not the entire machine), and
2. **Guarantees those enrichers run** on the enumerated set (extending the existing
   `NeedsBatteryEnrichment` / `NeedsMonitorEnrichment` gating pattern to a general
   tag-driven `RelevantTags`/`EmitsTags` match), and
3. applies the tag filter.

Crucially, this relocates the *forward routing table* from the public `DeviceCategory`-keyed
map into an **internal, tag-keyed enricher detail**. ADR-0047's constraint that tags have
no public forward-routing surface still holds — consumers never see a tag→subsystem map;
it is an enumeration optimisation, invisible at the API. A device that requests a tag on a
platform where no enricher emits it correctly matches nothing (the capability genuinely
cannot be detected there).

### 6. `WithTag` Option-B interaction — why the enrichers are mandatory, not optional

Today `WithTag("Sensor")` matches via the `Carries` fallback because
`Enum.GetName(DeviceCategory.Sensor) == "Sensor"`. **Once `Sensor` is removed from the
enum, that fallback can no longer produce the name**, so `WithTag("Sensor")` matches only
devices an enricher explicitly tagged. Therefore the demotion is a package deal: removing a
category member and shipping its tag-emitting enricher must land **together**. A member
removed without its enricher is a capability that becomes silently unqueryable. This is the
single most important sequencing constraint in the rollout.

### 7. Tag constants ship with their enrichers

Per ADR-0047's anti-speculation rule, add `DeviceTags.Imaging`, `.Biometric`,
`.SmartCard`, `.Printer`, `.Sensor` constants **in the same change that adds the emitting
enricher** — never ahead of it. Where the enricher lives in core (Windows class-GUID
reader) the constant lives in core `DeviceTags`; an extension-package enricher may emit a
fresh string instead (the set is open).

### 8. Serialization

`DeviceCategory` is still serialised via `[JsonConverter(typeof(JsonStringEnumConverter<DeviceCategory>))]`
(ADR-0025's converter was never implemented). Removing members is a **hard runtime break**:
any persisted JSON containing `"Category":"Printer"` (etc.) throws `JsonException` on
deserialisation. Two options:

- **8a (recommended, matches the stance): clean break.** Accept that old snapshots /
  configs naming a demoted category fail to deserialise; re-enumerate or migrate the config.
  Simplest, no lingering compatibility code.
- **8b: lenient inbound converter.** Ship a `DeviceCategoryJsonConverter` that maps the five
  retired names to `Category = All` and surfaces the capability as a tag on read. Adds
  permanent translation code for a one-time migration; contradicts the "no shims" stance.

Recommend **8a** internally; the one place a lenient read is *pragmatically* valuable is the
consumer config boundary (the kiosk consumer's `ProfileDefinition`), addressed in the blast radius.

---

## Blast Radius Analysis

Counts below are from a full sweep of `repos/periphery` and the kiosk consumer. They are
facts (actual references), not estimates of effort.

### In-repo (`periphery`) — ~17 files

| Area | Files | Sites | What changes |
|---|---|---|---|
| **Enum definition** | `src/Periphery/DeviceCategory.cs` | 5 members | Delete `Imaging`, `Biometric`, `Sensor`, `SmartCard`, `Printer`. |
| **Windows map** | `Windows/WindowsCategoryMap.cs` | ~11 lines | Remove 5 `GetClassGuids` arms + the `s_guidToCategory` entries (incl. 3 Printer-family GUIDs). |
| **Linux map** | `Linux/LinuxCategoryMap.cs` | ~6 lines | Remove 4 `GetSubsystems` arms + `iio`→`Sensor` resolver case. |
| **macOS map** | `MacOS/MacOSCategoryMap.cs` | ~10 lines | Remove 5 `GetIOKitClasses` arms; **relocate** `ResolveUsbCategory` (0x06/0x07/0x0B) and the `0x20`→`Sensor` arm of `ResolveHidCategory` into enrichers; drop the `IOUSBSmartCardController` dict entry. |
| **CLI rendering** | `Periphery.Cli` (`CategoryMeta.cs`, `device-dashboard.cs`) | ~5+ arms | Remove per-category colour/label arms; render the tags instead. |
| **Filter/query** | `DeviceFilter.cs`, `DeviceQuery.cs`, `DeviceTags.cs` | gating + new tag scope | Generalise `Needs*Enrichment` gating to `RelevantTags`/`EmitsTags` (§5). `OfCategory`/`WithTag` signatures unchanged. |
| **Enricher infra** | `IDeviceEnricher.cs`, `DeviceEnrichers.cs`, `EnrichmentPipeline.cs` (promoted to core from `Windows/WindowsEnrichmentPipeline.cs`), all three providers | contract + cross-platform | `ITagEmittingEnricher` / `EnricherScope` / `ScopeForTags` (§5, **done**); pipeline promoted to core and run from the Linux + macOS `ToDeviceInfo` builders so tags fire on every platform (step 2a, **done**). |
| **Serialization** | `DeviceCategory.cs:8`, `Serialization/DeviceInfoJsonContext.cs` | 1 annotation | Breaking per §8; optional lenient converter. |
| **Tests** | `Platform/MacOSCategoryMapTests.cs` (11 asserts), `Platform/LinuxDeviceProviderTests.cs` (1), `Contracts/DeviceProviderContractTests.cs` (1) | 13 assertions | Re-point map tests at tags; the contract test that used `OfCategory(SmartCard)` as a "matches nothing" probe must pick a surviving category or assert the new tag path. **Add** new enricher tests asserting each tag is emitted on the right signal. |
| **Docs** | `docs/ARCHITECTURE.md` §3 (category table, ~6 rows), §8; `docs/adr/0047` §7 (the Biometric "future candidate" example) | doc edits | Remove the 5 rows; document the subsystem-vs-capability split as the model. |

**New code required (the real work, not just deletion):** the tag-emitting enrichers
(a Windows class-GUID reader covering all five; Linux USB-class + `iio` reader; macOS
USB-class + HID-usage reader), the `DeviceTags` constants, and the §5 enumeration-scope /
gating infrastructure. The detection logic is largely relocated from the maps; the gating
infrastructure is genuinely new.

### ADR supersession

- **ADR-0014 (macOS device category coverage) — partially superseded.** It is superseded
  for `SmartCard` (its **Tier 1**) and all of **Tier 2** (`Printer`, `Imaging`, `Biometric`,
  `Sensor`); the `IOUSBSmartCardController` mapping plus the `ResolveUsbCategory` /
  `ResolveHidCategory` additions **relocate** from category resolution into tag-emitting
  enrichers (they are not deleted). Only its Tier-1 `Camera` (`IOVideoDevice`) and `Ports`
  (`IOSerialBSDClient`) additions remain as `DeviceCategory` values.
- **ADR-0003 (device category expansion) — partially superseded** for the five members.
- **ADR-0047** is the foundation; this ADR is its generalisation, not a supersession.
- **ADR-0050 (GPS / `Serial.Nmea`)** is the sibling instance; cross-reference both ways.

### External consumer — the kiosk consumer (light, with a sequencing dependency)

The kiosk consumer uses `Periphery` via the floated `1.0.0-alpha.*` feed
(`Directory.Packages.props`). It is a **declarative, config-driven** consumer — almost all
device selection is JSON, not code.

**What it touches among the five demoted categories: only `Printer`.**

| Site | Today | Impact | Required edit |
|---|---|---|---|
| the kiosk consumer's `appsettings.json` — `ReceiptPrinter` profile (~line 67) | `"Category": "Printer"` | **Hard break.** `"Printer"` no longer deserialises into `DeviceCategory?`. | Change to a tag-based profile (`"Tags": ["Printer"]`). |
| the kiosk consumer's `Configuration/ProfileDefinition.cs` | has `DeviceCategory? Category` → `filter.OfCategory(...)` only | Schema gap. | Add a `Tags`/`Tag` field and wire `filter.WithTag(...)`. |
| `appsettings.json` — `Battery` profile | `"Category": "Battery"` | **No break** (`Battery` survives). Optional: align to `WithTag(Battery)` per ADR-0047 OQ-005. | None required. |
| `appsettings.json` — `FrontCamera`/`InternalCamera` (`Camera`), `MainScreen`/`SignageScreen` (`Monitor`); `CameraRoleProvisioner.cs` `OfCategory(DeviceCategory.Camera)` | survive unchanged | **No break.** | None. |
| `docs/patterns/configuration-driven-tracking.md` | shows `Category`/`OfCategory` | doc drift | Add the tag-based example. |

**The coupling that matters: sequencing.** The kiosk consumer's receipt printer becomes
**undiscoverable** the moment `Printer` leaves the enum *until* Periphery ships the
`Printer`-tag enricher that tags the printer device (§6). And because the Windows signal is
the spooler class family, not USB class 0x07 (§2), that enricher must be the Windows
class-GUID reader specifically. So the consumer migration is **gated on the Periphery
refactor landing with the Printer enricher complete**. The kiosk edit itself is mechanical
and small; it simply cannot precede the Periphery side.

Scope of the consumer change: one config profile, one schema field + wiring, one doc
example. Mechanical. (A handoff task is filed for it.)

### Not in blast radius (verified)

`FrameFlow` and the other consumers use `Periphery.Camera`, not the demoted
categories. The kiosk consumer's `Periphery.Camera` usage (camera enumeration) is untouched.

---

## Consequences

### Positive

- **POS-001**: `Category` retreats to the one question every platform answers cleanly and
  1:1 — "which subsystem surfaced this device" — eliminating Seam (b)'s broken Linux
  queries and the per-platform reverse-map special-casing.
- **POS-002**: Capability becomes a uniform, cross-platform, composable query
  (`WithTag("Imaging")`) that works identically on all three OSes and composes with
  category, VID/PID, and name filters.
- **POS-003**: Multi-aspect / fan-out devices (Seam c) are expressible: one device can
  carry several capability tags without lying about its subsystem.
- **POS-004**: GPS stops being a special case — it is instance #1 of the ratified
  principle. The same enricher pattern serves GPS (`{Gps}` on a `Ports` device) and the
  five demoted categories.
- **POS-005**: Detection logic is mostly *relocated*, not rewritten — `ResolveUsbCategory`,
  `ResolveHidCategory`, and the Windows GUID dictionary entries move into enrichers largely
  intact, lowering the risk of the refactor.

### Negative

- **NEG-001**: It is a breaking change to a public enum and to serialised data (§8). Under
  the no-consumers-mostly stance this is acceptable, but it is no longer *free* — one
  consumer must migrate in lockstep.
- **NEG-002**: Windows loses out-of-the-box categorisation for these five (they were
  *clean* on Windows). Equivalent behaviour is re-provided via enrichers reading the same
  class GUIDs, but a consumer who did `OfCategory(Printer)` now must do `WithTag("Printer")`
  and depends on the enricher having run.
- **NEG-003**: Tags are enrichment-time, so they remain invisible to OS subscription
  scoping at the *public* level (ADR-0047 NEG-003). The §5 internal enumeration-scope hint
  recovers query efficiency, but it is new infrastructure that must be correct or
  `WithTag` either over-scans or under-matches.
- **NEG-004**: The "remove member + ship enricher together" coupling (§6) is a real
  footgun. A partial landing silently breaks a capability query rather than failing loudly.
  Mitigated by treating each category's removal+enricher as one atomic change with a test
  that asserts the tag is emitted.
- **NEG-005**: Biometric's signal is weak off Windows and absent for Touch ID (inherited
  from ADR-0014). The tag will be best-effort, same as the category was — no regression,
  but no improvement either.

---

## Alternatives Considered

### ALT-A — Keep the categories; just fix the Linux disambiguation
Port `ResolveUsbCategory` into `LinuxCategoryMap` so Seam (b) closes, and leave all five as
categories. **Rejected:** fixes (b) but not (c), entrenches the subsystem/capability
conflation, and means writing the same disambiguation three times (once per provider's
reverse map) instead of once (per-bus enricher). Also leaves GPS as an unresolved special
case.

### ALT-B — Multi-valued `DeviceCategory`
Make `Category` a `[Flags]` enum or `IReadOnlySet<DeviceCategory>`. **Rejected** — fully
litigated in ADR-0047 (breaks the ADR-0025 extension range, the 1:1 routing-map contract,
and `switch` exhaustiveness). This ADR is the chosen alternative from that analysis, applied.

### ALT-C — Demote only the worst offender (`Sensor`), keep the other four
`Sensor` is the clearest capability-not-subsystem case; do just it. **Rejected:** the other
four exhibit the identical Linux/macOS pathology (all → `usb`), so a partial demotion leaves
the model half-converted and the Linux `OfCategory(Printer/Imaging/SmartCard)` queries still
broken. The principle is cleaner applied to the whole capability-derived set.

### ALT-D — Do nothing (defer until more backends harden)
**Rejected:** the cost only grows. ADR-0014's Tier-2 code just landed against the model
being changed; every additional consumer raises the migration cost; and the Linux queries
are broken *now*.

---

## Migration / Rollout Plan

Ordered so the tree is never in a "category removed, capability unqueryable" state. Each
category is one atomic step (§6).

1. **Land the §5 mechanism first** (no behaviour change) — **done**. The declarative half:
   `ITagEmittingEnricher` (`EmitsTags` + per-platform `EnricherScope`) on the enricher
   contract. The computational half: `DeviceFilter.RelevantTags` and
   `DeviceEnrichers.ScopeForTags(...)`. **Landed inert** — no provider consults
   `ScopeForTags`, and the registered-enricher pipeline still runs every enricher, so
   enumeration is byte-for-byte unchanged. `HidBatteryEnricher` declares
   `EmitsTags={Battery}` + its HID scope as the worked example; its existing Battery
   tagging is the green regression check.
   **Provider activation (the `ScopeForTags` narrowing) is deferred to its own step** — the
   **Activation** note under step 2. It is a *performance* optimisation, not correctness: a
   bare `WithTag(...)` query is already correct without it (enumerate all → enrich → filter,
   the same broad scan tag queries do today). It also can't be behaviour-neutral until the
   inline `WindowsBatteryEnricher` becomes a scoped registered `ITagEmittingEnricher` — the
   `Battery` tag is emitted from two scopes (HID via `HidBatteryEnricher`, the Battery class
   via the inline one), so narrowing `WithTag("Battery")` would miss the system battery until
   both scopes are registered.

   **1b — Cross-platform enrichment pipeline** (prerequisite discovered during step 2) —
   **done**. The registered-enricher pipeline previously ran only on Windows, so demoting any
   category that *also* resolves on Linux/macOS (`Sensor` via `iio`, macOS HID usage `0x20`,
   etc.) would **regress** those platforms: removing the enum member also removes the ADR-0047
   Option-B Category-name fallback that makes `WithTag("Sensor")` match there today, and with
   no enricher emitting the tag the device becomes unfindable. Fixed by promoting
   `WindowsEnrichmentPipeline` → core `EnrichmentPipeline` and invoking it from the Linux and
   macOS `ToDeviceInfo` builders — the single point every enumerate + monitor path funnels
   through, which also keeps monitor diffs enriched-against-enriched. Bonus: the `Battery` tag
   now fires on Linux/macOS too, not just Windows. Full `Periphery.Tests` suite green (715
   passed); the Linux/macOS provider edits are compile-validated only (they can't run on a
   Windows host).
2. **Per category, in one commit each** — `Sensor` (**done**), `SmartCard` (**done**),
   `Imaging` (**done**), `Biometric` (**done**), `Printer` (**done** — highest risk,
   consumer-coupled). All five demoted:
   a. Add the tag-emitting enricher(s) (Windows class-GUID reader arm; Linux/macOS
      class-code/usage arm) + the `DeviceTags.<X>` constant.
   b. Remove the enum member and its three platform-map arms.
   c. Re-point the affected tests; add an enricher test asserting the tag emits on signal.

   `Sensor` shipped as a core registered `SensorEnricher` detecting the Windows `Sensor`
   class GUID / Linux `iio` subsystem / macOS HID usage `0x20` (the macOS provider now
   populates `DeviceInfo.HidUsagePage` so the enricher can read it), plus `DeviceTags.Sensor`
   and removal from the enum, all three maps, and the CLI renderer. Full suite green (721
   passed); Linux/macOS provider edits compile-validated, the pure map functions
   runtime-validated on Windows.

   `SmartCard` shipped as a core registered `SmartCardEnricher` + `DeviceTags.SmartCard`,
   detecting the Windows `SmartCardReader` class GUID, the macOS `IOUSBSmartCardController`
   class (via `DeviceInfo.IOServiceClass`), or USB device class `0x0B` (CCID). The `0x0B`
   check is forward-compatible — effective wherever `DeviceInfo.UsbClassCode` is populated,
   which is **Windows only today** (parsed from PnP hardware IDs). Linux smart-card detection
   and the macOS USB Tier-2 fallback light up once `UsbClassCode` is populated on Linux/macOS
   — a **shared prerequisite** for the `Imaging` (`0x06`) and `Printer` (`0x07`) demotions,
   which are USB-class-only off Windows. Interim gap: the marginal macOS usb-`0x0B`
   best-effort path (ADR-0014) until that population lands; the macOS *primary* path
   (`IOUSBSmartCardController`) and Windows are unaffected. Full suite green (725 passed).

   `Imaging`, `Biometric`, and `Printer` then shipped **Windows-first** (one commit each:
   `42335e9`, `ed45c7d`, `d6afa37`). `Imaging` → core `ImagingEnricher` + `DeviceTags.Imaging`,
   detecting the Windows `Image` setup class or USB class `0x06`. `Printer` → core
   `PrinterEnricher` + `DeviceTags.Printer`, detecting the three Windows spooler-stack class
   GUIDs (`Printer`/`PnpPrinters`/`PrintQueue`) or USB class `0x07` — keying off the class-GUID
   family, not the narrower USB class, exactly as the `Printer ⚠` blast-radius row requires.
   `Biometric` → core `BiometricEnricher` + `DeviceTags.Biometric`, Windows `Biometric` setup
   class **only**: USB has no biometric base class (readers are vendor-specific `0xFF`, which
   would over-match), and the former category was itself functional only on Windows, so this is
   a faithful, zero-regression demotion with an empty Linux/macOS scope. `Printer` was the last
   USB-class category, so the macOS Tier-2 scaffolding collapsed — `GetIOKitClasses` lost its
   Tier-2 arm and `ResolveUsbCategory` is now a documented always-null extension point. Full
   suite green (735 passed, 12 skipped).

   **Cross-platform USB-class detection — deferred by decision, not blocked.** The shared
   prerequisite above (populate `DeviceInfo.UsbClassCode` on Linux/macOS) is **intentionally
   deferred** until cross-platform CI/CD exists; Periphery is building out Windows
   depth first. The enrichers are written to extend with zero restructuring — each already
   carries its Linux/macOS USB-class branch and `EnricherScope` arm, dormant only because the
   field is `null` off Windows. The Windows class-GUID signals (live now) cover every device
   the old categories covered on Windows, so nothing regressed; the off-Windows paths light up
   the moment `UsbClassCode` population lands, no enricher edits required.

   **Activation (deferred — performance, not correctness).** Wire
   `DeviceEnrichers.ScopeForTags(filter.RelevantTags)` into provider enumeration so a bare
   `WithTag(...)` query (no `OfCategory`) scans only the relevant subsystems instead of every
   device. Prerequisite: convert the inline `WindowsBatteryEnricher` into a scoped registered
   `ITagEmittingEnricher` so the `Battery` scope-union is complete (HID + Battery class).
   Deferred to its own step after the demotions because the demotions are already correct
   without it — this only changes scan breadth, and decoupling keeps each demotion low-risk.
3. **Docs (done):** `README.md` (category table split into Categories + Capability Tags;
   the discovery-only framing reframed to discovery-only *core* + I/O extension libraries) and
   `ARCHITECTURE.md` §1/§3/§3.1/§8 updated to the subsystem-vs-capability model; ADR-0014
   (Tier-2 + SmartCard) and ADR-0003 (Tier-2) supersession notes in place.
4. **Publish** a new `Periphery` alpha to the local feed.
5. **the consumer migration** (separate session — handoff filed): only after step 2's
   `Printer` commit is published. Change the `ReceiptPrinter` profile to a tag, add the
   `Tags` field to `ProfileDefinition`, verify the kiosk discovers the printer end-to-end.
6. **Serialization:** ship clean-break (§8a). Revisit a lenient converter only if a second
   consumer needs to read old snapshots.

---

## Open Questions

All four were resolved on 2026-05-31 (the design owner accepted the draft leanings); kept
here with their rationale for the decision trail.

- **OQ-001 — Resolved: data, not a method.** The §5 enumeration-scope hint is data on the
  enricher — `EmitsTags` plus per-platform `string[]` scope arrays (the class GUIDs / udev
  subsystems / IOKit classes relocated from the forward category map). Simpler and
  serialisable for diagnostics; revisit a predicate form (`ShouldEnumerate(...)`) only if a
  composite-signal enricher ever needs one.
- **OQ-002 — Resolved: uniform `{Printer}` tag.** A demoted-category device resolves to
  `All` (or its honest bus category) with the capability carried entirely by the tag — no
  special narrower identity on Windows. A consumer that needs to narrow the scan (e.g.
  the kiosk consumer's often-serial receipt printer) composes `OfCategory(Ports).WithTag("Printer")`
  at the call site.
- **OQ-003 — Resolved: clean break (§8a).** No lenient inbound JSON converter. Persisted
  JSON naming a demoted category fails to deserialise and must be re-enumerated or migrated;
  the consumer edit is one line and a shim would be permanent code for a one-time event.
  the kiosk consumer hand-edits its `appsettings.json` (see the migration handoff).
- **OQ-004 — Resolved: `{Gps}` only.** A GPS receiver is *not* additionally tagged
  `{Sensor}`. Its enricher is VID/PID-based on a `Ports` device, not the HID-usage-page-`0x20`
  signal that drives the `Sensor` tag. Revisit only if a HID-usage GPS sensor ever appears.

---

## Relationship to Prior ADRs

| ADR | Relationship |
|---|---|
| ADR-0003 | Device category expansion — **partially superseded** (the five demoted members). |
| ADR-0014 | macOS category coverage — **superseded for `SmartCard` + all Tier-2 (Printer, Imaging, Biometric, Sensor)**; only Tier-1 `Camera` and `Ports` stand. `IOUSBSmartCardController`, `ResolveUsbCategory`, `ResolveHidCategory` relocate into enrichers. |
| ADR-0024 | Extension package pattern — enricher/`IDeviceEnricher` contract this extends with `EmitsTags`. |
| ADR-0025 | Extensible `DeviceCategory` — orthogonal; the extension range (≥1000) is unaffected by removing core members. |
| ADR-0026 | Enricher I/O boundary — the demoted-category enrichers honour the metadata-only / no-handle contract. |
| ADR-0047 | Device tags vs multi-category — **foundation**; this ADR is its generalisation and pins its deferred §5 gating question. |
| ADR-0048 | HID battery support — the existing tag-emitter pattern the new enrichers follow. |
| ADR-0050 | `Periphery.Serial.Nmea` / GPS — **sibling instance**; GPS-as-tag is the same principle. |
