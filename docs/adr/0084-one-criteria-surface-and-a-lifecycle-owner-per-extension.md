---
title: "ADR-0084: One criteria surface, a bindable filter, streaming terminals, and a lifecycle owner in every extension"
status: "Proposed"
date: "2026-08-31"
authors: "@charles8051"
tags: ["architecture", "decision", "api-design", "fluent", "devicequery", "devicefilter", "devicewatcher", "camera", "ergonomics"]
supersedes: ""
superseded_by: ""
---

# ADR-0084: One criteria surface, a bindable filter, streaming terminals, and a lifecycle owner in every extension

## Status

Proposed. Records the findings of an end-to-end review of the public API surface
and the six changes that follow from them. Extends ADR-0008, which introduced the
fluent registration verbs, to the criteria half of the same chain. Does not
change any device model, provider, or platform contract.

---

## Context

The entry surface is small and reads well. `Devices.Enumerate()` and
`Devices.Watch()` are two verbs, the criteria chain is discoverable, and
`DeviceProfile` + `DeviceProxy` — bind a device by identity, reopen it wherever
the OS puts it next — is the strongest idea in the library. Nothing below
proposes changing any of that.

The problems are one layer down, and they are structural rather than cosmetic.
Five of them, each with a concrete cost.

### 1. Three fluent surfaces re-declare the same criteria

`DeviceQuery`, `DeviceFilter`, and `DeviceWatcher` each hand-roll their own
`With*` chain: 26, 27, and 25 public criteria methods respectively, of which only
19 are common to all three. Every criterion is written three times, in three
files, with three sets of XML docs.

They have already drifted:

| Criterion | `DeviceQuery` | `DeviceFilter` | `DeviceWatcher` |
| --- | :---: | :---: | :---: |
| `WithTag` / `WithAllTags` / `WithAnyTag` | ✅ | ✅ | ❌ |
| `WithIdStartsWith` | ❌ | ✅ | ❌ |
| `WithContainerId` | ❌ | ✅ | ❌ |
| `Active` | ✅ | ✅ | ❌ |
| `OrderBy` / `Take` | ✅ | ❌ | ❌ |

Two of these are correct and stay that way. `Active` is meaningless on a watcher,
whose whole job is to report the activation edge, and `OrderBy`/`Take` are
result-shaping operators with no meaning on a predicate.

The other five gaps are accidents of hand-copying. Capability tags (ADR-0051) are
the library's own recommended way to select a printer or a HID device, and a
watcher-level filter cannot express one. A consumer that selects a tracker by tag
and a watcher by category is writing two vocabularies for the same question.

The maintenance cost compounds. Adding one criterion today means editing three
files and remembering that the third exists.

### 2. There is no bindable filter, so every consumer hand-writes the binder

`DeviceFilter` is a mutable builder configured through an `Action<DeviceFilter>`
delegate. That is a good runtime shape and a bad configuration shape: a delegate
cannot be deserialized, diffed, logged, or round-tripped.

So `docs/surface/configuration-driven-tracking.md` — the library's own
recommended pattern — instructs every consumer to write a DTO with one property
per criterion, plus an if-ladder that replays it onto a `DeviceFilter`:

```csharp
// what the guide tells every consumer to write, once, by hand
if (Category.HasValue)      filter.OfCategory(Category.Value);
if (DeviceName is not null) filter.WithName(DeviceName);
if (VendorId is not null)   filter.WithUsbId(VendorId, ProductId);
// ... one arm per criterion, forever
```

That ladder is mechanical translation of a surface Periphery already owns. Every
consumer writes the same one, and every copy silently falls behind whenever a
criterion is added here. The DTO in the guide is already missing `Tags`,
`IdStartsWith`, and `ContainerId`.

The failure mode is also poor. `DeviceProfile`'s constructor rejects an empty
filter with an `ArgumentException` naming the `configure` parameter — correct for
a delegate written in C#, useless when the real cause is a typo in a JSON overlay
three layers up. A consumer cannot pre-validate without duplicating the
`HasAnyCriteria` check, which is `internal`.

### 3. `DeviceQuery` buffers the entire enumeration before yielding anything

`DeviceQuery.GetAsyncEnumerator` collects every match into a `List<DeviceInfo>`,
sorts and limits it, and only then begins yielding. `ToListAsync`,
`FirstOrDefaultAsync`, `CountAsync`, and `AnyAsync` are all written on top of that
enumerator.

`FirstOrDefaultAsync()` therefore walks every device on the box even when the
first device examined is a match, and `.Take(1)` does not help. On Windows the
per-device cost is a cfgmgr32 property read plus whatever the enrichment pipeline
is configured to do, and monitor and battery enrichment are opted into by the
filter itself. A `FirstOrDefaultAsync` looking for one monitor pays the full-box
walk with monitor enrichment on.

The buffer exists to support `OrderBy` and `Take`, which genuinely require
materialization. It is unconditional, so the queries that use neither pay for
both. Nothing in the type's contract promises this. The class implements
`IAsyncEnumerable<DeviceInfo>` and reads as a streaming type.

### 4. `DeviceInfo` is 51 flat nullable properties

Every property of every category lives on one record. A HID mouse carries
`DisplayBounds`, `DriveType`, `MacAddress`, and `IPAddresses`, all null — four
fields that are populated on *some* device, just never this one. Autocomplete
stops being a discovery tool at that width, and the type does not tell a reader
which properties can ever be populated together.

A second, larger problem sits inside the first, and it is worth not conflating
them. Issue `#93` establishes that six display fields — `DisplayUsageKind`,
`DisplayDpi`, `DisplayPhysicalSizeInInches` and all three luminance fields —
have no producer anywhere in `src/` and are null on **every** device on every
platform. That is not surface in the wrong place. It is surface that describes
nothing, and no amount of regrouping fixes it.

The flat shape is deliberate and worth keeping. It serializes cleanly, it is
trivially `with`-able, and it avoids a polymorphic hierarchy that would force a
cast at every call site. The problem is that it is the only shape offered. There
is no narrower view for a caller who already knows they hold a display.

### 5. `Periphery.Camera` has no lifecycle owner, unlike every other extension

`docs/surface/periphery-session-integration-guide.md` states the principle
plainly: **Periphery owns lifecycle.** Three of the four I/O extensions honour
it.

| Package | Device type | Lifecycle owner |
| --- | --- | --- |
| `Periphery.Usb` | `UsbDevice` | `UsbDeviceProxy` |
| `Periphery.Hid` | `HidDevice` | `HidDeviceProxy` |
| `Periphery.Monitor` | `MonitorDevice` | `MonitorDeviceProxy` |
| `Periphery.Camera` | `CameraDevice`, `CameraSession` | **none** |

`CameraSession.For(DeviceInfo)` takes a snapshot value, not a `DeviceProfile` or
an `IDeviceTracker`. A consumer that wants a camera bound by identity and
reopened after a replug writes the tracker subscription, the open, the frame
pump, the disposal-on-every-exit-path, and the restart. That is the work
`DeviceProxyBase` already does for the other three transports.

Camera also has a failure mode the others do not, and which no existing type
models: **the device stays enumerated and Active while the stream is dead.** A
wedged UVC pipeline produces no frames and no PnP edge, so a tracker-driven
reopen never fires. Detecting it needs a frame-arrival deadline, which is
session-level knowledge the consumer must reconstruct from outside. ADR-0082
establishes that a camera session is lossy. Nothing yet owns the case where it is
lossy forever.

Issue `#123` documents the other half of the same wedge from the teardown side:
`CameraSession.RunBoundedAsync` and `MfCameraBackend.DisposeAsync` each abandon
cleanup on a hard timeout, and an abandoned cleanup still holding the device
cascaded one wedged mode into nineteen consecutive open failures on a C270.
`#123` lists a reset rung (ADR-0060) among its directions. This ADR's D5 is the
lifecycle half; the two meet at the same escalation ladder.

### What this ADR does not propose for camera

Issue `#121` settled the scope question for `Periphery.Camera` in the other
direction, and that decision stands: **no frame router, no fan-out primitive.**
`FrameFlow.Graph` already owns fan-out, Periphery cannot take the `IFrame` /
`IRefCounted` dependency it would need (ADR-0045), and an opinionated router is a
thing consumers adapt away from. `CaptureAsync()` plus refcounting is the right
minimum for frame *distribution*.

D5 is not a reversal of that. `#121` is about where a frame goes after it is
delivered, which is application policy. D5 is about who opens the device, who
reopens it after a replug, and who disposes the session on a fault — which
`docs/surface/periphery-session-integration-guide.md` places on Periphery's side
of the line, and which the other three extensions already do. The test that keeps
them apart: `CameraDeviceProxy` hands a consumer one frame at a time and has no
opinion about what happens next.

### What these five have in common

Each is Periphery declining to own something it is best positioned to own: the
criteria vocabulary, the configuration binding, the streaming contract, the
per-category view, and the camera's lifecycle. In each case the work does not
disappear. It moves to every consumer, is written slightly differently in each,
and drifts from the library independently.

---

## Decision

### D1 — Superseded: no interface. Parity by test, and two gaps of five

**This decision was implemented and the interface was not built.** Three
independent reviews of the implementation plan refuted every benefit claimed
below, and two of them found a correctness bug the plan would have shipped.
The original text is kept after the revision, because the reasoning that was
wrong is more useful than a silent edit.

#### What was measured

Each claim was compiled and run, not reasoned about.

**The interface does not enforce the parity that matters.** A C# interface
constrains arity, parameter types and return type. It does **not** constrain
default values, `params`, parameter names, or nullability — and an implementing
class may declare a *different* default with no diagnostic at all:

```
q.WithName("x")                                  ->  Ordinal            (class default)
((IDeviceCriteria<DeviceQuery>)q).WithName("x")  ->  OrdinalIgnoreCase  (interface default)
```

Same call, different behaviour, chosen by static type. Those four axes are
exactly where three hand-copied forwarder sets drift, so the interface would
have guarded everything except the likely failure.

**`<inheritdoc/>` does not dedupe the docs.** Roslyn does not expand it; the
literal tag is written into the generated XML, and `Periphery.csproj` ships that
file in the package. Converting the fluent surface to `<inheritdoc/>` would
replace real prose with an unresolved tag for every consumer not reading it
through an IDE.

**"A type meaning something filterable" already exists, and the interface cannot
be it.** `DeviceWatcherWaitSource` does `Devices.Watch().Where(filter.Matches)`
— that is the polymorphic case, solved by `DeviceFilter` plus `Matches`. Every
filterable-parameter site in the tree takes `DeviceFilter`. And a self-typed
generic interface yields no existential type: there is no
`IDeviceCriteria<?>` in C#, so every consumer must itself become generic in
`TSelf`.

#### The gaps were not all accidental

Three of the five are load-bearing omissions, and closing them would have
shipped a Windows-only bug.

Tags are produced by the enrichment pipeline. `WindowsDeviceMonitorProvider`
seeds its last-known-device cache with the plain unenriched build and says so in
a comment that enumerates what watcher filters may match on — tags are not in
that list. That cached record is what a removal event carries, and
`DeviceWatcher` gates every event on `_filter.Matches`. So a watcher filtered
`WithTag(DeviceTags.Printer)` would fire `Appeared` (the startup snapshot runs
the query provider, which *does* enrich) and never fire `Disappeared`, leaking
the device as permanently present. Linux and macOS enrich inside their single
device build, so the feature would also have been platform-divergent.

`Active` is likewise deliberate, and for a better reason than the original text
gave. A watcher's filter is evaluated against **post-transition** state: the
`Deactivated` handler runs when `IsActive` is already `false`, so a watcher
filtered `Active(true)` would suppress the very event it was watching for.

#### What was done instead

1. **No `IDeviceCriteria<TSelf>`.** No new public type.
2. **Two gaps closed, not five** — `WithIdStartsWith` and `WithContainerId` on
   `DeviceQuery` and `DeviceWatcher`. Both are carried by the unenriched build
   on every path, so both match symmetrically.
3. **The three tag filters stay off `DeviceWatcher`**, with the asymmetry
   documented on the type.
4. **A parity test replaces the interface**, asserting both that every
   `DeviceFilter` criterion is present on the other two surfaces *or* carries a
   written reason for its absence, and that every forwarder matches on the four
   axes an interface would not have checked.
5. **A capture bug fixed on the way past.** `WithAllTags`/`WithAnyTag` closed
   over the caller's `params` array rather than snapshotting it, so mutating
   that array afterwards silently rewrote a filter's criteria — including on a
   started `DeviceWatcher`, whose configure-time guard had already passed.

The exclusion list in that test is strictly more expressive than an interface:
it records *why* a member is absent, which an interface cannot.

#### Consequence for D2

D2 is no longer blocked on D1. Its text already requires the parity test to be
scoped to `DeviceFilter`'s public surface rather than the interface's, and that
test now exists.

---

<details>
<summary>Original D1 as proposed (superseded)</summary>

Extract the shared criteria vocabulary into a single generic interface
implemented by all three types.

```csharp
public interface IDeviceCriteria<out TSelf>
{
    TSelf OfCategory(DeviceCategory category);
    TSelf Where(Func<DeviceInfo, bool> predicate);
    TSelf WithName(string text, StringComparison comparison = StringComparison.OrdinalIgnoreCase);
    TSelf WithUsbId(HardwareId vendorId, HardwareId? productId = null);
    TSelf WithTag(string tag);
    TSelf WithAllTags(params string[] tags);
    TSelf WithAnyTag(params string[] tags);
    TSelf WithIdStartsWith(string prefix, StringComparison comparison = StringComparison.OrdinalIgnoreCase);
    TSelf WithContainerId(Guid containerId);
    // ... the full shared set
}
```

*Claimed:* every criterion declared once; adding one becomes one interface
member plus one `DeviceFilter` implementation; the five accidental gaps close as
a consequence. The first is false for a plain interface — an interface declares
no bodies, so all three types still write a forwarder. The third is false for
three of the five.

</details>

### D2 — A bindable `DeviceFilterSpec`

**Implemented, with the property set corrected.** The sketch below covered
**14 of the 26** data-expressible criteria while simultaneously mandating a test
that every one of them have a property. Those two statements contradicted each
other; the test would have failed on 12 members. The same class of gap as D1's
"five accidental gaps", found the same way — by counting rather than assuming.

Missing from the sketch: `WithStatus`, `WithDriveType`, `WithMacAddress`,
`WithDriver`, `WithMinResolution`, `WithUsbSpeed`, `WithParent`, `WithPortName`
(two overloads), `WithBatteryStatus`, `PhysicalOnly`, `VirtualOnly`.

#### What shipped

`DeviceFilterSpec`, a non-positional `sealed record` with **24 properties**
covering every criterion except `Where(...)`, which takes a delegate and is
excluded by construction. Replay is `DeviceFilter.Apply(spec)`, with
`DeviceQuery.Apply(spec)` and `DeviceProfile.FromSpec(spec, name)` alongside.

Six decisions worth recording, each of which changed the design from the sketch:

**`Apply` calls the public criteria methods, and nothing else.** This is a
correctness constraint, not a style preference. `DeviceFilter` carries structured
hints providers read to narrow the OS query — `Category` drives the Windows
class-GUID pushdown and the two enrichment flags, `VendorId`/`ProductId` narrow
the walk, and tag methods populate `RelevantTags`. An `Apply` that hand-rolled
equivalent `Where` predicates would match identically and silently degrade every
spec-built filter into a full-system scan.

**Unparseable values throw, naming the property.** The fluent
`WithUsbId(string, string?)` answers a bad vendor id with `Where(_ => false)` — a
permanent silent no-match. Defensible at a C# call site; the worst available
behaviour for a config DTO, where the cause is a typo three layers up and the
symptom is a device that never appears. `Apply` diverges deliberately, and the
divergence is documented on both.

**A misspelled member throws on the JSON path.**
`[JsonUnmappedMemberHandling(Disallow)]`, because the default is to bind a
wrongly-cased document to an *empty* spec — and an empty spec applied to a
filter matches every device on the box.

That attribute means nothing to `IConfiguration`, which is case-insensitive and
silently ignores keys it does not recognise. Measured: a document with
`Category` and `Catgory` binds cleanly, keeping the first and dropping the
second without a word. The strictness has to be asked for —
`Get<DeviceFilterSpec>(o => o.ErrorOnUnknownConfiguration = true)` throws naming
every unrecognised key — so the type documents both halves rather than claiming
a guarantee it only holds on one path, and the guide's example uses the strict
form.

**Equality is hand-written.** The compiler compares `string[]` by reference, so
two specs bound from the same JSON would have been unequal — while the type
advertises value semantics by being a record, and "did the bound configuration
change?" is asked with `==`. ADR-0047 records this exact surprise on
`DeviceInfo.Tags`; that one was mitigated by routing comparison through a diff
helper, which a config DTO has no equivalent of. Tags compare as sets, Ordinal,
with null and empty equal.

**`Physicality` is a named enum, not `bool?`.** `"physicality": "Virtual"` reads
correctly where `"physical": false` does not, and both fluent methods are
one-liners over `BusType.Software` — a classification platform work may yet
refine. An enum can gain a member. `Active` stays `bool?`, because
`DeviceFilter.Active(bool)` genuinely takes a boolean.

**`Apply` refuses an empty spec.** A spec with nothing set is a no-op, and a
no-op on a fresh filter is a filter matching every device — the exact shape a
mistyped configuration binds to. `IConfiguration` cannot be made strict from
here, so this is the one point in the library where that fail-open can be turned
into an error, and it is.

**Empty tag arrays are skipped, not forwarded.** `WithAnyTag([])` means "match
nothing"; an absent configuration value must not mean that. `Apply` branches.

#### What was deliberately left out

**String comparison.** Every string criterion uses its `OrdinalIgnoreCase`
default. Exposing it would put `CurrentCulture` within reach of a config file and
make matching depend on machine locale, and no consumer in the tree overrides it.
Additive to add later.

**`DeviceWatcher.Apply`.** A spec can carry `Active`, `AllTags` and `AnyTags` —
the three criteria a watcher must not honour (D1). A watcher-level `Apply` would
either ignore four properties silently or throw on specs valid everywhere else.
Bind a spec to a filter or a profile and hand the watcher a tracker.

**`ToSpec()`, permanently.** A `DeviceFilter` keeps no record of which method
produced which predicate — they collapse into one list — and a filter carrying a
`Where` lambda has no data form. The conversion is one-way by construction, and
saying so now is cheaper than being asked for a lossy version later.

#### Cost

Every future criterion now needs a spec property, an `Apply` branch, and a JSON
consideration, enforced by test. That is the intended trade and not a surprise,
but it is a tax on adding criteria and should be named as one.

---

<details>
<summary>Original D2 sketch (superseded — covered 14 of 26 criteria)</summary>

A `sealed record` with `Category`, `AllTags`, `AnyTags`, `DeviceName`,
`Manufacturer`, `VendorId`, `ProductId`, `SerialNumber`, `Id`, `IdStartsWith`,
`ContainerId`, `BusType`, `Active`, plus `HasAnyCriteria` and `ToString()`; with
`DeviceFilter.Apply`, `DeviceQuery.Matching`, and `DeviceProfile.FromSpec`.

Two further defects in the sketch beyond the missing properties:
`HasAnyCriteria` was written `{ get; }`, a get-only auto-property that is
permanently `false` and participates in the generated equality; and the query
entry point was named `Matching` while the filter's was `Apply`, which the D1
parity test would have flagged as a missing forwarder.

</details>

### D3 — `OrderBy` is the only thing that buffers

`DeviceQuery.GetAsyncEnumerator` yields matches as they arrive unless `OrderBy`
has been called. **`Take` does not force buffering** — a sort needs every
candidate before it can name the first result, but a limit does not.

| Query shape | Path |
| --- | --- |
| Neither | Stream every match |
| `Take(n)` only | Stream, and stop the source enumeration after the *n*th match |
| `OrderBy` (with or without `Take`) | Buffer, sort, limit — today's path, unchanged |

So `FirstOrDefaultAsync` and `AnyAsync` stop at the first match, and `.Take(1)`
touches one device rather than all of them. `ToListAsync` and `CountAsync` are
unchanged in observable behaviour.

The streaming `Take` must dispose the provider's enumerator once it has yielded
*n*, rather than draining it — otherwise the early return buys nothing, which is
the failure the current buffer already has. `GetAsyncEnumerator` is an iterator,
so `await foreach`'s own disposal covers the caller-breaks-early case; the
`Take` path needs the same discipline on its own exit.

Ordering of streamed results is provider order, which is what the current
unordered path already yields. This changes only when items are produced and how
many devices are touched, both of which the `IAsyncEnumerable` contract already
permitted.

### D4 — Typed facets over the flat record

Keep `DeviceInfo` exactly as it is. Add narrow readonly-struct views over the
subsets that travel together.

```csharp
public readonly struct DisplayFacet { public Size? Resolution { get; } public Rectangle? Bounds { get; } /* ... */ }
public readonly struct UsbFacet     { public UsbSpeed? Speed { get; } public UsbClassCode? ClassCode { get; } /* ... */ }
public readonly struct HidFacet     { }
public readonly struct BatteryFacet { }
public readonly struct StorageFacet { }
public readonly struct NetworkFacet { }

public static class DeviceInfoFacets
{
    public static DisplayFacet AsDisplay(this DeviceInfo device);
    public static UsbFacet     AsUsb(this DeviceInfo device);
    // one accessor per facet — no predicate, see below
}
```

Facets are views over the same record: no copying beyond the struct, no
allocation, no second source of truth. `DeviceInfo` remains the serialization
shape and the only thing providers populate.

**D4 ships no predicate.** An earlier draft paired each facet with
`TryAsDisplay(out …)`, first keyed on whether the facet's properties were
populated and then — after that was correctly rejected — keyed on
`Category`/`Tags`. Both are wrong, for different reasons, and the second is the
instructive one.

Keying on populated-ness reads the absence of a *reading* as the absence of a
*capability*, which is the inference ADR-0073 exists to reject. A monitor whose
enrichment did not run would report "not a display", and a caller would believe
it.

But keying on `Category`/`Tags` makes the predicate a **second spelling of a
question the library already answers**. `DeviceTags.Carries` folds category and
tags into one call today (ADR-0047, ADR-0051). A `TryAsDisplay` defined as
`Carries` plus a struct forces every caller to decide which of two identical
questions to ask, and every facet to define its own mapping — six more places for
the answers to diverge.

So classification stays exactly where it is, and D4 does only the thing that is
actually missing:

```csharp
// classification — unchanged, one spelling
if (DeviceTags.Carries(device, DeviceTags.Imaging)) { … }

// grouping — what D4 adds
var display = device.AsDisplay();
if (display.Bounds is { } bounds) { … }
```

`As*` is total. It always returns a facet, because it is a **view, not a cast**.
Reading `AsDisplay().Bounds` on a mouse yields null, which is exactly what
reading `device.DisplayBounds` yields today — D4 changes how those fields are
grouped, not what they say. That also keeps the two cases a predicate
conflates properly distinct:

| Situation | `DeviceTags.Carries` | Facet fields |
| --- | --- | --- |
| Not a display | false | null |
| A display the OS did not describe (enrichment did not run) | true | null |
| A described display | true | populated |

The middle row is the one a predicate cannot express and the reason not to build
one.

**`DisplayFacet` cannot ship the six fields that have no producer.** Issue `#93`
establishes that `DisplayUsageKind`, `DisplayDpi`,
`DisplayPhysicalSizeInInches`, and all three luminance fields are populated by
nothing anywhere in `src/` and are permanently null on every platform. A typed
facade over them would make dead surface look deliberate and harder to remove. So
D4 waits on `#93`, and takes whichever answer it reaches — a producer, or a
deletion.

Otherwise purely additive and optional. Callers who prefer the flat record keep
using it.

**What this does not do.** `DeviceInfo.Properties` is not the mechanism for this
and is not extended by it. That bag is documented as intentionally narrow —
inherently array-typed or purely diagnostic raw platform data, three well-known
Windows keys — and its stated policy is that anything scalar and universally
meaningful gets *promoted* to a typed field. That promotion rule is why the
record is 51 wide. D4 changes how the promoted surface is *read*, and proposes no
change to what gets promoted or to what the bag holds.

### D5 — `CameraDeviceProxy`, and a stall deadline on the session

Give `Periphery.Camera` the lifecycle owner the other three extensions have.

```csharp
await using var camera = await CameraDeviceProxy.OpenAsync(
    profile,                                   // or an IDeviceTracker
    configure: b => b.PreferNv12().MaxResolution(1920, 1080),
    onFrame: (frame, ct) => sink.WriteAsync(frame, ct),
    recoveryPolicy: policy,
    ct);
```

Built on `DeviceProxyBase`, so it inherits the activation window, the reconnect
loop, `IRecoveryPolicy` (ADR-0055), and `IDeviceReset` escalation (ADR-0060) that
the USB, HID, and monitor proxies already use. The proxy owns the frame pump and
the guarantee that the session is disposed on every exit path.

For the stall case, `CameraSessionOptions` gains a frame-arrival deadline:

```csharp
public TimeSpan? StallTimeout { get; init; }
```

When set, a session that delivers no frame within the window ends its capture
with a `CameraStallException`. That turns an invisible wedge into an ordinary
session fault, which `CameraDeviceProxy` recovers through the same
`IRecoveryPolicy` ladder as any other fault. Unset preserves today's behaviour
exactly.

**The recovery must not reopen into an abandoned cleanup.** This is the cascade
in `#123`: a stalled session's teardown can be abandoned on a timeout while still
holding the device, and an immediate reopen contends with it. A proxy that
retries a stall on the default backoff would have turned that C270 run into
nineteen automated failures instead of nineteen manual ones. So `StallTimeout`
lands *after* `#123`'s first direction — make abandonment observable, and have a
subsequent `OpenAsync` on the same device either wait for the abandoned cleanup
or fail fast naming it. Until a stalled camera can be reopened deterministically,
`CameraDeviceProxy` has nothing sound to escalate to.

**This gates the proxy's whole recovery path, not just `StallTimeout`.** Any
automatic reopen contends with an abandoned cleanup, whatever fault triggered
it, so the ordering constraint is on D5 as a decision rather than on one option
of it — which is why the sequencing section lists D5 blocked on `#123` outright.

Two consequences follow, and they are worth stating rather than leaving to
implementation:

- **The stall is reported before it is ever retried.** `StallTimeout`'s first
  increment ships as a metric and a fault, with `CameraDeviceProxy` surfacing
  `GaveUp` and leaving the restart to the application. That is the whole of the
  behaviour until `#123` lands.
- **Automatic reopen is the second step, not the first.** It is enabled only
  once a reopen can observe the previous teardown — at which point it is an
  ordinary `IRecoveryPolicy` ladder like the other three proxies, and a
  consumer that wants the conservative behaviour still gets it by injecting a
  `GiveUp`-on-first-fault policy.

Shipping the reopen before the observation would automate exactly the cascade
`#123` measured, which is the argument for the ordering rather than an aside
about it.

`CameraSession.For(DeviceInfo)` and the direct `OpenAsync` path stay. The proxy
is the recommended composition, not the only one — the same relationship
`UsbDeviceProxy` has to `UsbDevice`.

### D6 — `DeviceWatcher.StartAsync` may be retried, and accepts a policy

`StartAsync` currently sets `_started = true` before the provider registration and
the initial snapshot, and does not roll it back on failure. A watcher whose start
throws is therefore permanently unusable: the retry throws
`InvalidOperationException("The watcher has already been started.")`, so the
caller must discard the instance and rebuild every tracker and every event
subscription attached to it.

**A start attempt is transactional.** Moving the `_started` assignment is not
enough on its own, and the naive version is worse than the bug it fixes.
`StartAsync` does two things: it registers with the monitor provider, then it
takes the initial snapshot. If registration succeeds and the snapshot throws,
clearing `_started` alone leaves a live registration behind — and the retry adds
a second one. The consumer gets duplicate `Appeared`/`Activated` events for every
device, a leaked provider handle, and a `DisposeAsync` that only unregisters one.
That is a worse failure than the unstartable watcher, because it is silent.

So each attempt owns everything it created:

- Registration is held locally until the attempt commits. On any failure or
  cancellation, the attempt detaches its handlers, disposes the provider it
  registered, and clears `_deviceCache` and `_knownConnectedIds` before
  rethrowing.
- The attempt commits by assigning `_started = true` and publishing the provider,
  after both the registration and the snapshot have completed.
- A failed attempt therefore leaves no provider-side state, and the retry starts
  from the same position the first attempt did.

Trackers and event subscriptions are untouched throughout — they are watcher
state, not attempt state, which is the whole point of retrying in place.

**The snapshot's events are raised on commit, not during the walk.** An event
cannot be un-raised, so an attempt that raises `Appeared` for four devices and
then throws on the fifth leaves the consumer holding four arrivals for a start
that failed — and the retry raises them a second time. Documenting that as an
accepted residue is not good enough when the whole point of D6 is that a retry
is safe.

So the snapshot enumerates into a local list and raises nothing. The attempt
commits by assigning `_started`, publishing the provider, seeding
`_deviceCache` and `_knownConnectedIds`, and only then draining that list into
`Appeared`/`Activated` and the per-tracker fan-out. A failed attempt discards
the list, having raised nothing, so a retry cannot duplicate anything.

**Commit is the point of no return, so the drain cannot fail the start.** Once
`_started` is assigned the watcher *is* started, and an exception from a
consumer's own `Appeared` handler must not be reported as a start failure — a
caller cannot distinguish that from a registration failure, and rolling back
underneath a committed, already-notified watcher is exactly the duplicate-state
problem this decision exists to remove.

The post-commit drain therefore isolates each handler: a throwing subscriber is
caught, logged against that device, and the drain continues. `StartAsync` cannot
throw once the commit has happened.

The two failure regions are cleanly split as a result. Before commit, anything
that throws rolls the attempt back and propagates, and the retry is safe. After
commit, nothing propagates, and there is nothing to retry.

**This is new behaviour, not an existing convention being extended.**
`DeviceWatcher` contains no exception handling around event dispatch anywhere
today — `src/Periphery/DeviceWatcher.cs` has no `catch` clause at all — so a
throwing subscriber currently escapes into whichever thread raised the event,
including the provider's pump thread on a live edge. Isolating the snapshot
drain fixes the path D6 touches and deliberately leaves the live-event path
alone, because changing dispatch semantics for every event is a larger decision
than this one and belongs in its own ADR. Worth filing: the live path has the
same defect and a worse blast radius.

This makes the whole attempt transactional rather than only its provider-side
half, and it costs one list of `DeviceInfo` for the duration of the walk. The
observable change is that snapshot events now arrive after `StartAsync` has
committed rather than interleaved with it. Live events that arrive during the
walk are already queued by the monitor provider and are delivered after the
snapshot drains, which is the ordering the current code documents as its reason
for registering before snapshotting.

The watcher also accepts the recovery abstraction the library already has:

```csharp
public Task StartAsync(CancellationToken ct = default);
public Task StartAsync(IRecoveryPolicy recoveryPolicy, CancellationToken ct);
```

The overload retries the start according to the policy, honouring `Retry(delay)`
and `GiveUp` from `RecoveryDirective`. `Reset` is not meaningful for a provider
registration and is treated as `GiveUp`. The no-policy overload is unchanged: one
attempt, throw on failure.

**The token is not optional on the policy overload.** With both parameters
defaulted, an existing `StartAsync(default)` becomes ambiguous — `default`
converts to `CancellationToken` and to `IRecoveryPolicy` alike, so the call fails
with CS0121. Requiring the token keeps the one-argument call unambiguous.

This matters more than convenience. The watcher is a consumer's entire view of its
hardware — every tracker reports through it — so a transient start failure that
cannot be retried leaves an application permanently blind, with its trackers
frozen at whatever they last read.

---

## Consequences

### Positive

- One place to add a criterion, and no way to add it to two of three surfaces.
- Capability tags (ADR-0051) become expressible at watcher level, which is where
  the README already tells readers to select devices by capability.
- Configuration-driven tracking stops being a copy-paste pattern in a document
  and becomes a supported API. `docs/surface/configuration-driven-tracking.md`
  shrinks to a binding example.
- `FirstOrDefaultAsync` on a filtered category stops paying a full-box property
  read. This is the single largest enumeration cost in the library.
- Autocomplete on a display returns display properties.
- `Periphery.Camera` stops being the odd extension out, and the stream-stall
  failure mode gets a name and a recovery path.
- A watcher start that fails is recoverable without rebuilding application state.

### Negative

- `IDeviceCriteria<TSelf>` is a large interface, and adding a member to it is a
  breaking change for any external implementer. Mitigated by documenting it as
  not-for-implementation; the three implementations are already sealed.
- `DeviceFilterSpec` is a second way to express a filter, and the two can drift
  the way the three fluent surfaces did. Mitigated by a test asserting that every
  data-expressible `IDeviceCriteria` member has a corresponding spec property.
- D3 changes timing a caller may have come to depend on. A `FirstOrDefaultAsync`
  that previously observed a device appearing late in the walk may now return
  earlier. This is a behaviour change within a documented streaming contract, and
  lands on a major.
- D6's transactional attempt makes the failure path do real work — detach,
  dispose, clear — where today it does none. A bug in that rollback is a leaked
  provider registration, which is harder to spot than the unstartable watcher it
  replaces. It needs a test that faults the snapshot specifically and asserts the
  provider was disposed and no duplicate events were raised on the retry.
- Deferring the snapshot's events to commit changes when a consumer first hears
  about existing devices: after `StartAsync` returns rather than during it. Code
  that subscribes and then awaits `StartAsync` is unaffected; code that relied on
  observing arrivals mid-call is not.
- The deferred snapshot holds one `DeviceInfo` list for the duration of the walk.
  On a large box that is a few hundred records, and it is transient.
- Six facet structs are six more types in the core namespace, for information
  already reachable.
- D4 adds six struct types and an accessor each, for information already
  reachable off the record. The grouping is the whole of the benefit, so it is
  worth confirming the autocomplete problem is felt before paying for it.
- `CameraDeviceProxy` adds a dependency from `Periphery.Camera` onto the core
  proxy machinery. `Periphery.Camera` already references core, so no new package
  edge.

### Neutral

- No device model, provider, enricher, or platform contract changes.
- D1, D2, D4, D5, and the policy overload in D6 are additive. D3 and the
  `_started` rollback in D6 are behavioural and land together on a major.

---

## Alternatives considered

**Collapse `DeviceQuery` and `DeviceWatcher` into one type.** They share a
criteria vocabulary and nothing else. One is a pull enumeration that completes,
the other a push subscription that runs until disposed. Merging them produces a
type where half the members throw depending on how it was constructed.

**Make `DeviceFilter` itself the configuration DTO.** It is mutable, carries
lambda predicates that cannot serialize, and exposes `Matches` plus internal
enrichment hints that have no meaning in a config file. A separate record keeps
the runtime type free to hold delegates.

**Source-generate the three fluent surfaces from one definition.** Solves the
duplication without an interface, but leaves three unrelated types with no shared
contract, so a method taking "something filterable" still cannot be written. The
interface is the thing consumers actually want.

**Split `DeviceInfo` into a polymorphic hierarchy.** Every call site casts,
serialization needs a discriminator, and a device that is both a display and a
USB device has no place in a single-inheritance tree. Facets are views, not
types, and a device can have several.

**Leave camera lifecycle to consumers, on the grounds that frame pumping is
application policy.** The session-integration guide draws the line at lifecycle
versus message exchange, and reopen-after-replug is lifecycle by that definition.
The other three extensions already sit on Periphery's side of that line. This is
the strongest objection to D5, because `#121` rejected a camera fan-out primitive
on adjacent reasoning; see "What this ADR does not propose for camera" above for
where the two differ.

---

## Sequencing

1. **D1** — the interface, and the five gap closures it implies. Additive.
2. **D2** — `DeviceFilterSpec` on top of D1's settled vocabulary. Additive.
3. **D6 policy overload** — additive. The `_started` rollback ships with D3.
4. **D4** — facets. Additive, and independent of D1–D3 and D5–D6, but
   **blocked on `#93`**: `DisplayFacet` cannot be specified until the six
   display fields with no producer either get one or are deleted.
5. **D3** — streaming terminals. Behavioural; lands on a major with the
   `_started` rollback.
6. **D5** — `CameraDeviceProxy` and `StallTimeout`. Largest single piece,
   independent of D1–D4, and **blocked on `#123`**: a stall the proxy cannot
   reopen from deterministically is not worth automating.

`#70` (hoist the three identical `DeviceProxy` factory bodies into
`DeviceProxyBase`) is the same duplication class as D1, one layer down. Doing it
before D5 means `CameraDeviceProxy` is a fourth caller of a shared factory rather
than a fourth copy of one.

---

## References

### ADRs

- ADR-0008 — fluent tracker registration (`AddTracker` / `AddTrackers`)
- ADR-0045 — substrate independence from Crossbar (the `IFrame` / `IRefCounted`
  participation protocol `#121` cites as unavailable to Periphery)
- ADR-0047 — device tags vs multi-category; `Category` is OS subsystem identity,
  `Tags` are capability annotations. D4's `TryAs*` answers from these
- ADR-0051 — capability categories demoted to tags
- ADR-0073 — Periphery reports observations, not verdicts. Why D4's predicate
  cannot key on whether a property happens to be populated
- ADR-0055 — injectable reconnect policy (`IRecoveryPolicy`), shipped in
  `DeviceProxyBase` with `ExponentialBackoffRecoveryPolicy.Default`
- ADR-0060 — device reset and recovery escalation
- ADR-0065 — camera testing seam
- ADR-0081 — a delivered frame has tight rows
- ADR-0082 — a camera session is lossy

### Issues

- `#121` — document the frame fan-out recipe; do not ship a router. Sets the
  scope posture D5 must not violate.
- `#123` — abandoned camera teardown is invisible and cascades into the next
  open. Blocks D5's `StallTimeout`.
- `#70` — hoist the three identical `DeviceProxy` factory bodies into
  `DeviceProxyBase`. Same duplication class as D1; worth doing before D5.
- `#17` — teardown-time backend error mis-classified as a capture fault.
  Adjacent to `CameraStallException` classification.
- `#93` — six `DeviceInfo` display properties have no producer and are
  permanently null. Blocks D4's `DisplayFacet`.
- `#16` — closed during this review. It asked for ADR-0055's `IReconnectPolicy`,
  which shipped as `IRecoveryPolicy`; `DeviceProxyBase` consults it and
  `ConnectionState` exists.

### Guides

- `docs/surface/configuration-driven-tracking.md`
- `docs/surface/periphery-session-integration-guide.md`
