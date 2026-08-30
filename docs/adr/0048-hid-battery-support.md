---
title: "ADR-0048: HID Battery Support — Feature Reports, Power-Device Class Parser, and Vendor Quirks"
status: "Accepted"
date: "2026-05-26"
authors: "@charles8051 (design)"
tags: ["architecture", "decision", "periphery-hid", "hid", "battery", "ups", "feature-reports", "quirks", "enrichment", "megatec", "voltronic", "qs", "dialect-detection"]
supersedes: ""
superseded_by: ""
---

# ADR-0048: HID Battery Support — Feature Reports, Power-Device Class Parser, and Vendor Quirks

## Status

### Spike outcome — Phase 1 (2026-05-26)

Phase 1 of the spike landed end-to-end against the kiosk's WayTech UPS
(VID `0665`:`5161`, Cypress 0665 family — the motivating concrete case):

- **Library** — `HidD_GetFeature` / `HidD_SetFeature` P/Invokes in
  `HidInterop.cs`; `ReadFeatureReportAsync` / `WriteFeatureReportAsync`
  on `IHidBackend` and `HidDevice`; `WindowsHidBackend` implementation
  wraps the sync HID-control-pipe calls in `Task.Run` for async API
  consistency.
- **CLI for hardware iteration** — `periphery hid feature read|write`
  and `periphery hid report read|write` shipped via Periphery.Cli;
  installed on the kiosk via `dotnet tool install -g Periphery.Cli`
  so each iteration is a one-line `dotnet tool update` instead of a
  full kiosk rebuild + deploy.

Two material findings came out of the spike that change Phase 2's
specifics. Both worth pinning here so future readers don't try to
reproduce the same mistakes:

**Finding 1 — Cypress 0665 family routes Megatec Q1 over input/output
reports, not feature reports.** The WayTech's HID descriptor advertises:

```
UsagePage=0xFF00  Usage=0x0001
MaxInput=8        MaxOutput=8        MaxFeature=0
```

`MaxFeature=0` means the device exposes no feature reports at all.
The Q1 protocol on this device family rides 8-byte input/output reports
with **fragmentation**: the ~46-byte ASCII status response
(`(MMM.M NNN.N PPP.P QQQ RR.R S.SS TT.T b7b6b5b4b3b2b1b0\r`) arrives
across ~6 input reads, terminated by `\r`. Padding for short writes is
zero-fill to `MaxOutputReportLength`. Confirmed by both passive
piggyback on ViewPower's polling traffic and active `Q1\r` writes from
our CLI.

The ADR's original §5 said *"issues `Q1\r` as a feature-report-0 write
and parses the ASCII status string returned via a feature-report-0
read."* That's wrong for this device family. §5 is rewritten below.
Architectural shape (codec interface, quirks table, two-pass enricher)
holds; only the *implementation* of `MegatecQ1Codec` changes.

The right design implication is that `IHidUpsCodec` stays
transport-agnostic — it takes a `HidDevice` and the codec chooses
internally whether to use feature reports or input/output reports.
Other Megatec dialects (and the standard HID PDC path) may use
feature reports; the Cypress 0665 family doesn't. The codec's
transport choice is per-codec, not per-interface.

**Coexistence note** — Other HID handles to the same device can be
open in parallel (e.g. vendor monitoring software). Our `CreateFile`
opens with `FILE_SHARE_READ | FILE_SHARE_WRITE`; the spike confirmed
the device's *input* stream is multicast to all open handles
(command echoes and responses both visible to any reader). The codec
must therefore tolerate noise on the input stream and look for its
own command's response prefix rather than assuming the next inbound
bytes are its.

**Finding 2 — Periphery.Hid can't open a HID device by SetupAPI
instance ID.** `HidDevice.OpenAsync` passes `DeviceInfo.Id` straight
to `CreateFile`. The enumeration returns IDs of the form
`HID\VID_0665&PID_5161\6&1B6066C6&0&0000` (SetupAPI device-instance
ID), but `CreateFile` needs the device-interface path
`\\?\HID#VID_0665&PID_5161#6&1B6066C6&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}`.
Today the open fails with `ERROR_INVALID_NAME`; the spike worked
around it by hand-constructing the interface path.

Proper fix: use SetupAPI's `SetupDiGetClassDevs` +
`SetupDiOpenDeviceInfo` + `SetupDiEnumDeviceInterfaces` +
`SetupDiGetDeviceInterfaceDetail` to resolve the instance ID into
its interface path inside `HidDevice.OpenAsync` before calling
`CreateFile`. Falls through cleanly when the input already starts
with `\\?\`. **This is a prerequisite for Phase 2** — `MegatecQ1Codec`
and `WindowsHidBatteryEnricher` shouldn't have to know about Windows
interface-path mechanics; the open layer owns that.

### Phase 2 direction

With those findings absorbed:

1. **Periphery.Hid open-by-instance-ID resolution lands first.**
   Foundation work the codec sits on top of. Tracked separately.
2. **`MegatecQ1Wire` (new helper, internal to Periphery.Hid)** —
   honors OQ-001's forward-compat note. Encapsulates command framing
   (pad to `MaxOutputReportLength`, append `\r`), output-report write
   (split across reports if command > report length), input-report
   reassembly (read until response-prefix character, then accumulate
   until `\r`), and echo skipping. A future `MegatecQ1Control`
   surface for write-back commands (graceful shutdown, self-test
   trigger, beeper mute) reuses the same wire helper.
3. **`MegatecQ1Codec` (public, implements `IHidUpsCodec`)** — uses
   `MegatecQ1Wire` to issue `Q1`, parses the ASCII status response
   into `HidBatterySnapshot`. Implementation detail spelled out in
   the rewritten §5.
4. **`HidQuirks` table** with WayTech `0665:5161 → MegatecQ1Codec`
   registered at module init. Other clones added as we get hardware
   to test them against.
5. **`WindowsHidBatteryEnricher`** — vendor path only for v1 (per
   the original OQ-002 decision; standard PDC 0x84/0x85 path
   deferred until we have a compliant UPS for testing).

Kiosk-side filter migration (the `BatteryTracker` switch from
`OfCategory(Battery)` to `WithTag(DeviceTags.Battery)`) is downstream
of all of this and is the kiosk repo's concern — Periphery's job
ends at "the WayTech enumerates with `Tags = {Battery}` and live
battery fields populated."

### Post-Phase-2 refactor — ADR-0026 compliance (2026-05-26)

The original Phase 2 implementation of §3 — a single
`WindowsHidBatteryEnricher.EnrichAsync(DeviceInfo)` that opened a HID
handle, ran the codec, and returned a `DeviceInfo` with battery fields
populated and the `Battery` tag added — was discovered to violate two
load-bearing invariants in ADR-0026:

1. **`IDeviceEnricher` must not open device handles.** Sub-kind A only.
2. **`DeviceInfo` must remain a zero-I/O snapshot.** Battery fields
   populated from a handle-gated read break that invariant.

ADR-0026 was already Proposed (alongside ADR-0024 §3c which references
it) before this work started; the issue was on me for not reading the
extant ADR landscape before designing §3. Refactor to comply:

- **`HidBatteryEnricher`** (Periphery.Hid, top-level — no longer
  Windows-specific because it does no I/O): pure-metadata classification
  enricher. Checks `HidQuirks.TryGetUpsCodec(vid, pid)`; if a codec is
  registered, returns the device with `DeviceTags.Battery` added to
  `Tags`. **Does not** populate `BatteryChargePercent` /
  `BatteryStatus` / `IsExternalPowerConnected` — those stay `null`
  unless the OS itself surfaced them at enumeration time. Compliant
  sub-kind A.

- **`HidBattery.ReadSnapshotAsync(DeviceInfo, CancellationToken)`** in
  `Periphery.Hid.Codecs` — the ADR-0026 Option D static snapshot
  helper. Opens a transient HID handle via `HidDevice.OpenAsync`,
  looks up the codec, calls `IHidUpsCodec.ReadSnapshotAsync`, closes,
  returns a `HidBatterySnapshot` (the domain record). Returns `null`
  when no codec is registered for the device's (vid, pid). Throws
  `HidException` on open/read failure. **Does not modify**
  `DeviceInfo`.

Consumers compose the two: `HidBatteryEnricher.Enrich` during
enumeration post-processing (free; no I/O); `HidBattery.ReadSnapshotAsync`
explicitly per-device when live data is needed (I/O cost visible at the
call site). The kiosk's `BatteryTracker.WithTag(DeviceTags.Battery)`
filter still works because the classification enricher emits the tag;
the kiosk's `BatteryPoller` hosted service calls `ReadSnapshotAsync` on
its bound device at whatever cadence operations want.

The codec / wire / quirks layers (§4 and §5 below) are unchanged —
they were already ADR-0026-compatible by virtue of being the layer
*beneath* the enricher boundary. Only the enricher entry-point shape
needed rework, and the CLI commands (`periphery battery list / show`)
were updated to the two-call pattern.

§3 below is preserved as the historical record of the original design;
read it alongside this addendum.

### Review findings (2026-05-27)

A review of the landed work surfaced one architectural gap and one
information-loss issue that should be tracked here rather than
discovered cold the next time someone picks this up.

#### Gap — auto-registration is load-bearing for §POS-001

~~`HidBatteryEnricher.Enrich` is implemented but is **not** plumbed
into core enumeration.~~ **Resolved (2026-05-27, same day).** Option 1
(land the ADR-0024 §3c hook) shipped. Concretely:

- `IDeviceEnricher` interface in core (`src/Periphery/IDeviceEnricher.cs`)
  with the ADR-0024 §3c signature (`CanEnrich` + async `EnrichAsync`).
- `DeviceEnrichers` static registry (`src/Periphery/DeviceEnrichers.cs`)
  — Register / Unregister / Snapshot, lock-free reads via
  `ImmutableArray`, mirrors `HidQuirks`'s shape from ADR-0048's quirk
  registry.
- `HidBatteryEnricher` refactored from a static helper class to a
  sealed class implementing `IDeviceEnricher`. A `[ModuleInitializer]`
  in `Periphery.Hid` registers `HidBatteryEnricher.Instance` so
  consumers don't need to call it explicitly.
- `WindowsEnrichmentPipeline` (`src/Periphery/Windows/`) runs the
  registry's snapshot per device. Wired into
  `WindowsDeviceProvider.EnumerateAsync` (async), `TryBuildDeviceInfo`
  (sync — used by the monitor's [UnmanagedCallersOnly] arrival path),
  the monitor provider's seed loop, and `ScanLoopAsync`. Per-enricher
  exceptions are caught and logged so a misbehaving extension can't
  nuke an enumeration.
- `BatteryListCommand` / `BatteryShowCommand` no longer call
  `HidBatteryEnricher` directly — core enumeration tags HID UPSs as
  Battery automatically.

§POS-001's promise (`BatteryTracker.WithTag(DeviceTags.Battery)` just
works for HID UPSs) is now delivered end-to-end on Windows. The
Linux/macOS providers don't yet run the pipeline — flagged in
ADR-0026 OQ-004 resolution as deferred follow-up.

#### Information loss — `BatteryStatus` collapses `batteryLow`

`MegatecQ1Codec.ParseQ1` maps both `(utilityFail=true,
batteryLow=true)` and `(utilityFail=true, batteryLow=false)` to
`BatteryStatus.Discharging`. The device reports the "imminent
shutdown" signal explicitly in status bit 6, but the codec drops it
on the floor because the current `BatteryStatus` enum has no value to
carry it. The status-bit table is documented in `MegatecQ1Codec`'s
XML doc, so the loss is at least *visible* — but a consumer asking
the framework "is the UPS about to shut down?" gets no answer.

Three options for resolving, in increasing scope:

1. **Extend `BatteryStatus`** with a `Critical` (or `LowBattery`)
   value and propagate the distinction through every battery
   enricher. Most semantically honest. Touches the cross-platform
   enum, so every provider has to think about when to emit it
   (Windows: `GetSystemPowerStatus` already exposes
   `BatteryFlag & 0x04` for "low"; the mapping is straightforward).
2. **Surface `batteryLow` as a separate `bool?` field on
   `HidBatterySnapshot`.** Keeps `BatteryStatus` orthogonal but means
   consumers read two fields to act on low-battery. Domain-record-
   local, no enum churn.
3. **Live with the loss.** Defensible if no consumer is asking for
   the distinction yet — the kiosk's current shutdown story doesn't
   depend on it. Flagging it here so the next pickup doesn't have to
   rediscover the codec's lossy mapping by reading the wire trace.

**Resolution (2026-05-27, same day).** Option 2, extended to
DeviceInfo too. The orthogonal-axis argument was decisive: `BatteryStatus`
describes flow direction (Charging/Discharging/Full/NotCharging —
mutually exclusive); "low" describes a charge-level threshold that may
hold simultaneously with any flow direction. Collapsing both into a
single enum value forces a choice between losing flow direction or
losing the low signal at every emission site. The fix:

- `DeviceInfo.IsBatteryLow` (`bool?`) — cross-platform field next to
  the existing battery trio.
- `HidBatterySnapshot.IsBatteryLow` — same field on the codec-local
  snapshot record.
- `WindowsBatteryEnricher` populates `IsBatteryLow` from
  `BatteryFlag & BATTERY_FLAG_CRITICAL` (0x04), null when the OS
  flag is `BatteryFlagUnknown` or no battery is present.
- `MegatecQ1Codec` populates from status bit 6; the codec's
  `BatteryStatus` mapping simplifies to a clean utility-fail ⇒
  Discharging vs NotCharging fork because the orthogonal axis no
  longer fights it.
- `DeviceInfoDiff` participates; `BatteryListCommand` adds a "Low"
  column; `BatteryShowCommand` includes the field in its dump.

### Addendum — Megatec Qx claim-and-bind dialect detection (2026-06-05)

A field finding on a deployed box overturns one assumption
baked into §4/§5: that a `(VID, PID)` pin is enough to choose the protocol. It
isn't. The codec internals are reworked accordingly; the §4/§5 architectural
shape (transport-agnostic `IHidUpsCodec`, the `HidQuirks` `(VID, PID)`→codec
table, the two-call enricher + snapshot) is unchanged. §5 below is preserved as
the historical record; read it alongside this addendum.

#### The finding: same silicon, different verb

The kiosk's UPS is a Cypress `0665:5161` Megatec-clone — the exact device the
spike built `MegatecQ1Codec` against. But every `Q1\r` status query times out at
3s while the device is otherwise healthy and talking. Live listen-then-write wire
probes (periphery as sole consumer) showed it **echoes `Q1` then stays silent**,
yet **answers the Voltronic `QS` status verb**:

```
QS\r → (117.4 117.4 117.4 000 60.2 13.7 --.- 00001001\r
```

That is the *exact* `(MMM.M NNN.N PPP.P QQQ RR.R SS.S TT.T b7..b0` shape the codec
already parses — input 117.4V, output 117.4V, 0% load, 60.2Hz, battery 13.7V
(~100% on a 12V cell), status `00001001` (on line power, not low, beeper on).
**Only the verb differs, not the response format.** This unit implements the
Voltronic `QS` dialect, not Megatec `Q1`.

Per the NUT project, `Q1` is the Megatec-spec status inquiry and `QS` is a
Voltronic command; NUT models these as the **Megatec Qx family** of distinct
subdrivers (`q1`, `voltronic`, `voltronic-qs`, …) that share the `(…)` response
shape but differ in verb. Critically, **VID:PID cannot select the dialect** — the
Q1 units and this QS unit are all `0665:5161` Cypress silicon. That is why NUT
probes at runtime (`nutdrv_qx` runs each subdriver's `claim()` in turn and binds
the first that answers) rather than keying off USB IDs.

#### Caveat: the May "Q1 validated" reading was probably a multicast artifact

The spike's claim that this WayTech "answered Q1" (Status section, 2026-05-26) is
now suspect. The HID input endpoint is multicast across every open handle, and the
vendor monitor (ViewPower) polls `QS` continuously. A `QS` reply landing on our
shared input stream — a well-formed `(…` line — is indistinguishable from an
answer to our own `Q1` write. The spike almost certainly bound a `QS` reply that
ViewPower elicited and attributed it to `Q1`. **Conclusion: do not privilege
`Q1`.** Treat every dialect as a peer in the candidate set; the only honest
disambiguator is a probe on a quiet bus.

#### Design: claim-and-bind, functional core / imperative shell

`MegatecQ1Codec` is replaced by `MegatecQxCodec` (and `MegatecQ1Wire` →
`MegatecWire`, `ParseQ1` → `MegatecStatus.Parse`). The split:

- **Pure core (data + total functions).** `MegatecStatus.Parse(string)` decodes
  the shared `(…)` line — dialect-agnostic, the same code for Q1, QS, … —
  and `MegatecStatus.IsWellFormed(string?)` is the non-throwing predicate
  detection uses to decide whether a probe answered. `MegatecDialect` carries
  the verb + response-prefix as data, with an ordered `Candidates` set (`Q1`,
  `QS`; extensible to e.g. Voltronic `D`). No I/O, no clock — exhaustively
  unit-tested, including the captured `QS` line above.
- **Imperative shell.** `MegatecQxCodec` owns the handshake: on first contact
  with a device it probes each candidate verb (over `MegatecWire`) and **binds
  the first that returns a well-formed status line**, caching the verb per device
  id. Subsequent reads send only the bound verb — a one-time handshake, not a
  per-read fallback. If a bound verb ever stops returning a well-formed line (a
  mis-detection from input cross-talk, or a unit hot-swapped onto the same port)
  the binding is dropped so the next read re-detects (self-healing). Cadence
  stays a consumer concern (OQ-003 unchanged).

The verb is therefore *data*, detection is *I/O done once in the shell*, and the
parse is a *pure value transform* — matching the functional-core /
imperative-shell preference. The detection policy is factored as a testable
`MegatecQxCodec.DetectAsync(probe, ct)` that takes a probe delegate, so the
Q1→QS negotiation is unit-tested with fakes and no real timeouts.

`HidQuirks`'s `(VID, PID)` table is unchanged in shape, but a registration now
only routes a device to the **Megatec-Qx codec**; the dialect is resolved by
probe, not by the table. A sibling codec (its own `IHidUpsCodec`) is reserved for
a dialect whose response *format* diverges, not merely its verb — collapsing
verb-only variants into one codec keeps the family in one place (NEG-004's "split
at ~5 codecs" smell is about format divergence, not verb count).

#### Detection robustness

Because the input pipe is multicast, claim-and-bind is only fully reliable when
this process is the sole consumer during the first-contact handshake (another
consumer's `QS` reply can be misread as an answer to our `Q1`, exactly as the
spike was). The bound steady state is unaffected, and self-heal recovers a
mis-binding once the noise clears. Operationally: detect with the vendor monitor
stopped (the kiosk should be the sole UPS poller regardless). A future hardening
— drain the input pipe before each probe, or correlate by timing — is possible
but unnecessary for the single-poller deployment.

#### Live validation (2026-06-05, field)

Packed the fixed `Periphery.Cli` (`1.0.0-qsfix`, bundling the rebuilt
`Periphery.Hid`) and updated the box's global tool. As sole consumer,
`periphery battery list` reports **100% / NotCharging / AC yes / Low no** for
`0665:5161` — answered via `QS` — where the prior `Q1`-only build timed out at 3s
moments earlier. The kiosk consumer's own call site is unchanged - it already
calls `HidBattery.ReadSnapshotAsync`, at consumer revision `af77c0d` - so the
kiosk's battery indicator lights up once that consumer is rebuilt against the
fixed Periphery package and redeployed.

## Context

`Periphery.Hid` today exposes a two-method I/O surface — `ReadReportAsync`
(input reports) and `WriteReportAsync` (output reports) — via the
internal `IHidBackend`, with `WindowsHidBackend` backing it via
`CreateFile` + `FileStream` overlapped I/O. Enrichment surfaces
`HidUsagePage`, `HidUsage`, and max-report-length fields on
`DeviceInfo` without opening a handle.

What it doesn't have: **feature report I/O**. `HidInterop.cs` exports
`HidD_GetAttributes`, `HidD_GetPreparsedData`, `HidD_FreePreparsedData`,
and `HidP_GetCaps` — but neither `HidD_GetFeature` nor `HidD_SetFeature`.
`IHidBackend` has no feature-report methods. This is the single capability
that blocks first-class support for an entire class of devices.

### The motivating case: HID-class UPSs

The kiosk's battery is a WayTech UPS (`0665:5161`,
Cypress Semiconductor HID controller). It enumerates under Windows
`HIDClass` as "USB Input Device" and is the only battery surface on
the machine. The kiosk's `BatteryTracker` filter currently never
matches because the OS surfaces this device under HID, not under the
Battery subsystem. ADR-0047 (device tags) covers the *classification*
half — letting us tag the UPS as `Battery` without lying about its
`Category` — but tagging only matters if the tag is *load-bearing*:
the enricher needs a way to actually *talk to the UPS* to populate
`BatteryChargePercent`, `BatteryStatus`, `IsExternalPowerConnected`.

That conversation happens over feature reports.

### Two HID battery worlds

**The standard.** USB-IF document *HID Usage Tables for Power Devices*
(HID PDC 1.0) defines Usage Page `0x84` (Power Device) and Usage Page
`0x85` (Battery System) along with a structured report layout:
voltage, current, capacity, runtime, AC presence, etc. Compliant UPSs
self-describe via their report descriptor — Windows ships `hidbatt.sys`
as the generic driver. Logic for the enricher's standard path is
self-contained:

```
if HidUsagePage in (0x84, 0x85):
    parse standard PDC feature reports → fields
    tag DeviceTags.Battery
```

**The reality.** Most cheap UPSs aren't compliant. Vendors buy a
generic HID-capable USB controller (the Cypress `0665` family is the
big one), reflash it with their own VID/PID, and ship a proprietary
protocol — most commonly **Megatec Q1**: ASCII commands written to
feature report 0, ASCII status read back from feature report 0. NUT
(Network UPS Tools) handles a couple dozen of these via its
`nutdrv_qx` driver with a per-VID/PID dispatch table for protocol
dialects.

The vendor path requires per-device knowledge keyed by VID/PID:

```
if HidUsagePage == 0xFFxx and (VID, PID) in known_ups_quirks:
    dispatch to quirk_table[VID, PID] codec → fields
    tag DeviceTags.Battery
```

### Why this generalises beyond batteries

The Cypress `0665` pattern — generic HID silicon + vendor-defined
usage page + per-VID/PID protocol dispatch — repeats across a wide
slice of the hardware Periphery is asked to enumerate:

- **POS hardware.** Barcode scanners (kiosk uses Posiflex `065A:A002`)
  enumerate as compliant HID Keyboard for the scan stream but have
  vendor side-channels for beep / good-read LED / config-via-barcode.
  Cash drawer kicks. Customer-facing pole displays. MSR pinpad
  config / encryption-mode toggles.
- **Industrial IO.** Phidgets (`06C2`) is the in-kiosk example today,
  consumed via the Phidgets SDK. There's a wide market of HID-class
  relay boards, ADC daughterboards, thermocouple amplifiers from
  no-name vendors that ship vendor-defined HID without an SDK.
- **Other bus-bridge silicon under many brand names.**
  STMicro `0483`, Microchip `04D8`, Van Ooijen Technische Informatica's
  `16C0` sub-licensed VID block (hundreds of hobbyist projects under
  sub-PIDs), various Cypress sub-families. Every one of these will,
  over time, ship something we want to identify by VID/PID rather
  than self-description.

The shape of the solution this ADR proposes — strongly-typed parsers
for self-describing devices + a VID/PID quirk table with a consumer
override for vendor-defined devices — is load-bearing for the UPS
case *and* for the broader vendor-HID surface. Building it once and
generalising is cheaper than landing a UPS-only path and re-doing it
for scanners in six months.

---

## Decision

### 1. `IHidBackend` gains feature-report methods

```csharp
internal interface IHidBackend : IAsyncDisposable
{
    // ... existing UsagePage / Usage / MaxXxxReportLength / Read/WriteReportAsync ...

    ValueTask<HidReport> ReadFeatureReportAsync(byte reportId, CancellationToken ct);
    ValueTask WriteFeatureReportAsync(HidReport report, CancellationToken ct);
}
```

`WindowsHidBackend` implements both via `HidD_GetFeature` and
`HidD_SetFeature` P/Invokes added to `HidInterop.cs`. Buffer shape
mirrors the existing report I/O: byte 0 is the report ID, payload
follows; max length comes from `MaxFeatureReportLength` (already
populated at enumeration via `HidP_GetCaps`).

`HidDevice` and `HidDeviceProxy` (Layer 1 / Layer 2) get the
corresponding public methods that delegate to the backend, matching
the shape of the existing report I/O surface. Reconnect-resilience in
`HidDeviceProxy` covers feature reports the same way it covers input
and output reports.

### 2. Strongly-typed Usage Page parser

The current `HidUsagePage` and `HidUsage` properties on `DeviceInfo`
are raw `ushort`s — fine as enumeration metadata but useless when we
need to decode a report. Add a `Periphery.Hid.UsagePages` namespace
with a strongly-typed parser per usage page:

```csharp
namespace Periphery.Hid.UsagePages;

public static class PowerDevice    // Usage Page 0x84
{
    // Report-layout structs that map onto the PDC spec's input/feature
    // reports — voltage, current, present-status flags, etc.
    public readonly struct PresentStatus { /* AC online, charging, ... */ }
    public readonly struct Voltage       { /* ... */ }
    // ... full PDC 1.0 surface as the kiosk drives it ...

    // Parser entry points
    public static PresentStatus ParsePresentStatus(ReadOnlySpan<byte> featureReport);
    public static Voltage       ParseVoltage(ReadOnlySpan<byte> featureReport);
}

public static class BatterySystem  // Usage Page 0x85
{
    public readonly struct RemainingCapacity { /* ... */ }
    public readonly struct RunTimeToEmpty   { /* ... */ }
    // ...
}
```

Only the slice of the PDC spec that has an obvious consumer is
implemented in v1 (whatever populates the existing battery fields on
`DeviceInfo`); the namespace is open to growth for new fields. Other
usage pages (Generic Desktop, Consumer Control, etc.) can grow their
own modules as future enrichers need them — the namespace shape is
established here, the breadth is incremental.

### 3. `WindowsHidBatteryEnricher` lives in `Periphery.Hid`

A new internal enricher in `Periphery.Hid.Windows` participates in the
standard enrichment pipeline. It runs when the filter could match a
battery (`NeedsBatteryEnrichment` is true; see ADR-0047 OQ-003 for the
gating refinement) and the device is on the HID bus.

Two-pass match, in order:

1. **Standard path.** If `HidUsagePage` is `0x0084` or `0x0085`, open
   the device, read the relevant feature reports, parse via
   `UsagePages.PowerDevice` / `BatterySystem`, populate
   `BatteryChargePercent` / `BatteryStatus` /
   `IsExternalPowerConnected`, tag with `DeviceTags.Battery`.
2. **Vendor path.** If the standard path didn't match and `(VendorId,
   ProductId)` is registered in `HidQuirks.Ups`, dispatch to the
   quirk's codec (Megatec Q1 for `0665:5161`), populate the same
   fields, tag with `DeviceTags.Battery`.

Both paths populate the same `DeviceInfo` fields and emit the same
tag. Consumers downstream cannot tell which path matched (and
shouldn't have to care). `Category` stays `Hid` in both cases — ADR-0047
keeps that distinction clean.

The enricher populates at enumeration time only. Continuous polling
is deliberately **not** a Periphery concern (see OQ-003): consumers
that need fresh battery state grab the codec via
`HidQuirks.GetUpsCodec(vid, pid)` and run their own polling loop at
whatever cadence fits their operational model.

### 4. `HidQuirks` — built-in defaults + consumer override

A static class in `Periphery.Hid`:

```csharp
namespace Periphery.Hid;

public static class HidQuirks
{
    /// <summary>
    /// Registers a vendor-defined HID UPS that doesn't implement the
    /// standard HID Power Device class. The enricher will dispatch to
    /// <paramref name="codec"/> when a device matching <paramref name="vendorId"/>
    /// and <paramref name="productId"/> is enriched. Built-in entries
    /// for known clones are registered automatically at module init.
    /// Overrides an existing registration (built-in or consumer) for
    /// the same (vid, pid); the override is logged at
    /// <see cref="LogLevel.Information"/> so operators notice it.
    /// </summary>
    public static void RegisterUps(HardwareId vendorId, HardwareId productId, IHidUpsCodec codec);

    /// <summary>
    /// Collision-aware variant for safety-conscious consumers. Returns
    /// <c>false</c> without modifying the table if a registration
    /// already exists for (<paramref name="vendorId"/>, <paramref name="productId"/>);
    /// <paramref name="wasOverride"/> indicates whether an existing
    /// entry would have been replaced.
    /// </summary>
    public static bool TryRegisterUps(
        HardwareId vendorId, HardwareId productId, IHidUpsCodec codec,
        out bool wasOverride);

    /// <summary>
    /// Returns the registered UPS codec for a device, or <c>null</c>
    /// if no codec is registered. Exposed so consumers can drive their
    /// own polling loops — Periphery does not poll battery state.
    /// </summary>
    public static IHidUpsCodec? GetUpsCodec(HardwareId vendorId, HardwareId productId);
}
```

Built-in registrations happen in a `[ModuleInitializer]` so the
baseline table is populated before any enumeration. The initial
baseline contains entries for the clones we have hardware to test
against — WayTech `0665:5161` (Megatec Q1) plus whatever else can be
cribbed from `nutdrv_qx`'s table with reasonable confidence.

Consumers add a new clone with a single call at startup:

```csharp
HidQuirks.RegisterUps("0BAD", "BEEF", new MegatecQ1Codec());
```

The override surface intentionally stays small — three methods, no DI,
no plugin loader. If `HidQuirks` grows beyond UPSs to general
vendor-HID dispatch (scanner-beep, cash-drawer-kick, etc.) the
register surface generalises the same way (`RegisterScannerBeep`,
`RegisterCashDrawer`, etc.) without restructuring.

`GetUpsCodec` is the consumer-facing accessor that makes
continuous polling a *consumer concern*. The kiosk's hosted polling
service grabs the codec for its bound battery device and reads
snapshots on whatever cadence the operational story demands — none
of that cadence policy lives in Periphery.

### 5. Megatec Q1 codec lives in `Periphery.Hid`

`IHidUpsCodec` is a thin interface in `Periphery.Hid`:

```csharp
public interface IHidUpsCodec
{
    /// <summary>
    /// Reads battery status from <paramref name="device"/> and returns
    /// the populated fields. The enricher copies these onto the
    /// emitted <see cref="DeviceInfo"/>.
    /// </summary>
    ValueTask<HidBatterySnapshot> ReadSnapshotAsync(HidDevice device, CancellationToken ct);
}

public readonly record struct HidBatterySnapshot(
    int? BatteryChargePercent,
    BatteryStatus? BatteryStatus,
    bool? IsExternalPowerConnected);
```

`IHidUpsCodec` is **read-only by design** — the enricher boundary
doesn't span write operations, and a future UPS *control* surface
(graceful-shutdown signaling, self-test triggers, beeper mute) will
get its own `IHidUpsControl` interface alongside this one. To keep
that forward-compat cheap, `MegatecQ1Codec` is implemented on top of
a thin `MegatecQ1Wire` helper (frame encode/decode, ASCII parsing)
that a future `MegatecQ1Control` can reuse without duplicating the
wire format. See OQ-001.

The Megatec Q1 implementation (`Periphery.Hid.Codecs.MegatecQ1Codec`)
sits on top of `MegatecQ1Wire` (an internal low-level helper) and
uses **input/output reports** rather than feature reports — corrected
post-spike, see the Status section for the original-design error and
the wire trace that disproved it.

#### `MegatecQ1Wire` — fragmentation + ASCII reassembly

```csharp
namespace Periphery.Hid.Codecs;

/// <summary>
/// Low-level transport for ASCII command/response protocols over HID
/// input/output reports (Megatec Q1, Voltronic QS, similar dialects).
/// Stateless; reused by every codec in the Megatec family.
/// </summary>
internal static class MegatecQ1Wire
{
    /// <summary>
    /// Sends <paramref name="command"/> followed by <c>'\r'</c> as
    /// output-report writes (padded with <c>'\0'</c> to
    /// <see cref="HidDevice.MaxOutputReportLength"/>), then reads
    /// input reports until the response prefix character appears,
    /// accumulates until <c>'\r'</c>, and returns the response.
    /// </summary>
    /// <param name="responsePrefix">
    /// First character of the expected response — <c>'('</c> for the Q1
    /// status query, <c>'#'</c> for the F rating query. Tolerates noise
    /// before the prefix (command echoes from other shared-handle
    /// consumers, leftover bytes from prior requests).
    /// </param>
    /// <param name="timeout">
    /// Wall-clock cap; if no full response arrives within this window
    /// the wire returns <c>null</c>.
    /// </param>
    public static async ValueTask<string?> RequestAsync(
        HidDevice device,
        string command,
        char responsePrefix,
        TimeSpan timeout,
        CancellationToken ct);
}
```

Echo skipping is the critical detail. The spike showed that ViewPower
(or any other shared-handle consumer) writing `QID\r` or `GM\r` causes
those bytes to surface on *our* input stream as well — the HID input
endpoint multicasts to every open handle. The codec must accept noise
between requests and recognise its own response by prefix.

#### `MegatecQ1Codec` — public IHidUpsCodec implementation

```csharp
namespace Periphery.Hid.Codecs;

public sealed class MegatecQ1Codec : IHidUpsCodec
{
    public async ValueTask<HidBatterySnapshot> ReadSnapshotAsync(
        HidDevice device, CancellationToken ct)
    {
        var response = await MegatecQ1Wire.RequestAsync(
            device, "Q1", responsePrefix: '(',
            timeout: TimeSpan.FromSeconds(3), ct);

        if (response is null)
            throw new HidTransferException(
                "Megatec Q1 request timed out — device did not respond " +
                "within 3 seconds.", innerException: new IOException("Q1 timeout"));

        return ParseQ1(response);
    }

    /// <summary>
    /// Parses the standard Megatec Q1 status response:
    /// <c>(MMM.M NNN.N PPP.P QQQ RR.R S.SS TT.T b7b6b5b4b3b2b1b0</c>
    /// (the leading <c>'('</c> is included; the trailing <c>'\r'</c>
    /// is already stripped by the wire layer).
    /// Status-bit semantics, MSB→LSB:
    ///   b7 = utility fail (1 = on battery)
    ///   b6 = battery low
    ///   b5 = bypass / boost active
    ///   b4 = UPS failed
    ///   b3 = UPS type (0 = online, 1 = standby/offline)
    ///   b2 = test in progress
    ///   b1 = shutdown active
    ///   b0 = beeper on
    /// </summary>
    private static HidBatterySnapshot ParseQ1(string response);
}
```

`BatteryChargePercent` deserves a footnote: Megatec Q1 doesn't directly
report a percent. The codec estimates it from battery voltage (single
12V cell convention: ~13.7V float = 100%, ~10.5V cutoff = 0%) but
flags it as approximate in the XML doc on the field. Consumers that
need precise charge state should query the UPS via QGS or similar
extended queries — a per-device extension to consider when more
firmware is in scope.

#### Co-location and future split

`MegatecQ1Wire`, `MegatecQ1Codec`, `IHidUpsCodec`, and
`HidBatterySnapshot` all ship in `Periphery.Hid.Codecs`. Other
dialects (Voltronic, MegaTec II, Phoenixtec-with-quirks) added as
siblings when we have hardware. The "should this be its own package?"
question stays deferred — split when there's a concrete reason
(e.g. a control-surface companion that crosses the read-only enricher
boundary, or the codec list growing past ~5).

---

## Consequences

### Positive

- **POS-001**: Kiosk's WayTech UPS becomes a battery-tagged
  `DeviceInfo` with `BatteryChargePercent` / `BatteryStatus` /
  `IsExternalPowerConnected` populated, without lying about its
  `Category` (which stays `Hid` per ADR-0047). The kiosk's
  `BatteryTracker` filter flips from `OfCategory(Battery)` to
  `WithTag(DeviceTags.Battery)` and lights up.
- **POS-002**: Periphery.Hid gains feature-report I/O — a capability
  that's been missing for *any* HID device that uses feature reports
  for status or config (which is most non-trivial HID hardware).
- **POS-003**: Strongly-typed PDC parsers turn a binary report dump
  into named fields. Future battery-related features (HMD batteries,
  laptop batteries on platforms without `Win32_Battery`) can reuse the
  same parser surface.
- **POS-004**: `HidQuirks` consumer-override means a new no-name UPS
  unblocks the kiosk same-day — no Periphery release required for the
  field case. Same shape generalises to future vendor-HID quirks
  (scanner beep, cash drawer kick, industrial relay state).
- **POS-005**: All four decisions co-locate in `Periphery.Hid`. No new
  package, no ADR-0024/0025 extension boilerplate. The cohesive
  "HID-class hardware" boundary is preserved.

### Negative

- **NEG-001**: `HidQuirks` becomes a coordination point. Consumer
  registrations and future built-in entries can collide on
  `(VID, PID)` keys. Policy: last-registered-wins, with a debug log on
  collision so operators can spot the override. Document the built-in
  table so consumers know what's already covered.
- **NEG-002**: `UsagePages.PowerDevice` / `BatterySystem` is
  non-trivial code in core. The HID PDC spec is dense; getting the
  parsers right (especially the bit-packed status flags) requires
  reference hardware that implements the standard cleanly. Risk:
  parser bugs that show up only on the one compliant UPS someone
  deploys six months from now. Mitigation: ship the parsers behind
  unit tests against captured report byte sequences from real
  hardware, and treat the standard path as best-effort until at
  least one compliant device has been verified.
- **NEG-003**: Megatec Q1 (and any sibling codec) opens a HID handle
  during enrichment. Opening a HID device on Windows requires no
  privilege but does require the device not be exclusively held by
  another process (`hidbatt.sys` does *not* take exclusive ownership
  for standard PDC devices, but vendor-specific drivers might).
  Failure mode: enrichment can't talk to the device, no battery
  fields populated, no `Battery` tag. Same outcome as offline; not a
  regression but worth logging.
- **NEG-004**: Codec dispatch will accumulate. Today's Q1 is small;
  Voltronic, MegaTec II, and the Phoenixtec dialect will each be
  comparable. At ~5+ codecs the `Codecs` namespace deserves its own
  split — into `Periphery.Hid.Ups` or similar. Not a problem now;
  flagged so we recognise the smell when it shows up.
- **NEG-005**: Enumeration-time-only population means battery fields
  are stale once the UPS state changes (e.g. AC fails → charge starts
  dropping). The `BatteryTracker` will still match (the tag is sticky)
  but the *values* on `DeviceInfo` won't update. Polling is OQ-003.

---

## Alternatives Considered

### A — Consumer-side enricher in the kiosk consumer

The kiosk implements its own UPS enricher against raw HID reports;
Periphery.Hid stays unchanged. Rejected: fastest unblock for one
device but doesn't generalise. Every future vendor-HID device in any
Periphery consumer would re-implement the same pattern. The kiosk
hardware market (POS scanners, cash drawers, etc.) makes this an
inevitability, not a hypothetical.

### B — New `Periphery.Ups` extension package

Spin out a dedicated UPS package with its own `[ModuleInitializer]`
quirk registration, its own ADR-0024-shaped extension contract.
Rejected: ADR-0024/0025 boilerplate for what is, today, one codec
and one enricher rule. The split makes sense once UPS-specific
behaviour grows beyond enrichment (UPS *control* — shutdown signaling,
self-test triggers — would be the trigger). Stays a defer-until-needed
move.

### C — `byte[]`-typed feature-report API only (no usage-page parser)

Mirror the existing input/output report API: feature-report I/O takes
and returns `byte[]`. Enrichers parse manually. Rejected: parsing the
HID PDC report layout is non-trivial bit-packing work that absolutely
should not be duplicated at every consumer site. Strongly-typed
parsers in core are the right place to amortise the spec-reading.
Raw-byte feature-report I/O is still available through the same
methods — the parser is layered on top, not in place of.

### D — Hardcoded quirk list only (no consumer override)

`HidQuirks.Ups` is a `private const` table inside `Periphery.Hid`; new
clones require a Periphery release. Rejected: friction is wrong-side
for the kiosk's field-deployment model. When an operator swaps a
failed WayTech UPS for a different no-name brand, we want them to be
able to ship a one-line config-or-startup change, not wait on a
Periphery cut.

### E — `IHidQuirk` plugin point (DI / extension-package shape)

Full ADR-0024-style extensibility for quirks: `IHidQuirk` interface,
DI registration, module-initializer registration like
`DeviceCategoryRegistry`. Rejected: over-engineered for the current
need. The `HidQuirks.RegisterUps` static API gives 90% of the
flexibility for 10% of the surface. If quirk authorship ever
generalises beyond first-party + occasional consumer overrides (e.g.
third-party "Periphery vendor pack" packages emerge), revisit.

### F — Synthesise `Category = Battery` for HID UPSs

Special-case HID UPS detection in `WindowsCategoryMap` to claim the
device is a `Battery` instead of `Hid`. Rejected: this is what
ADR-0047 explicitly exists to avoid. The device *is* a HID device that
also exposes a battery surface — both halves are true and useful (the
first half is required for talking to it via `HidDevice`). Tags handle
"and also a battery"; category stays truthful.

---

## Open Questions

- **OQ-001**: ~~Should the codec interface leave room for a future
  UPS *control* surface (graceful-shutdown signaling, self-test
  triggers, beeper mute)?~~ **Resolved.** `IHidUpsCodec` stays
  read-only — mixing write operations into the codec would muddle
  the read-only enricher boundary that ADR-0026 establishes. A
  future `IHidUpsControl` interface will live alongside `IHidUpsCodec`
  when the kiosk's low-battery-shutdown story actually needs writes.
  Forward-compat is bought cheaply by implementing `MegatecQ1Codec`
  on top of a thin `MegatecQ1Wire` helper (frame encode/decode,
  ASCII parsing) that a future `MegatecQ1Control` can reuse without
  duplicating wire format.

- **OQ-002**: ~~Should `UsagePages.PowerDevice` / `BatterySystem`
  cover the full HID PDC 1.0 spec, or only the slice that populates
  today's `DeviceInfo` battery fields?~~ **Resolved.** Narrow scope.
  Implement only what populates the three battery fields in v1; grow
  the parser as new `DeviceInfo` fields are added. Same incremental
  "as enrichers need it" pattern ADR-0047 uses for tags. We don't
  have a compliant UPS to test the full spec against, so building it
  speculatively would produce code with no test target.

- **OQ-003**: ~~Should Periphery run a continuous polling loop to
  keep battery state fresh?~~ **Resolved — no.** Polling cadence is
  operational behaviour; baking it into the framework overreaches.
  The codec is exposed via `HidQuirks.GetUpsCodec(vid, pid)` so
  consumers can wire up their own polling at whatever cadence their
  story demands. The kiosk runs a `BatteryPollerHostedService` using
  the codec; Periphery stays opinion-free about how often. Keeps the
  codec stateless and the enricher boundary read-only.

- **OQ-004**: ~~Where do future codec packages live if Megatec Q1
  grows neighbours?~~ **Resolved.** Stay in `Periphery.Hid` until a
  concrete reason to split shows up. Pre-splitting "to keep the
  option open" is speculative; per Periphery's stated stance there are
  no external consumers to placate, and the migration is mechanical
  when it happens (NEG-004 flags the smell to watch for).

- **OQ-005**: ~~`HidQuirks` collision policy when a consumer
  registers a `(VID, PID)` that's also in the built-in table.~~
  **Resolved.** Last-write-wins by default — the override *is* the
  point of the consumer API, and forcing every consumer to
  unregister-first would add friction without preventing bugs. Log
  collisions at **Information** (not Debug) so unintentional
  overrides get noticed but the channel doesn't go silent. A
  `TryRegisterUps(... out bool wasOverride)` variant covers
  safety-conscious consumers who want to refuse-on-collision.

- **OQ-006**: Does the `HidQuirks` shape generalise to the broader
  vendor-HID surface (POS scanner beep, cash drawer kick, industrial
  relay) as one class with `RegisterUps` / `RegisterScannerBeep` /
  etc., or as separate quirk classes per domain? **Lean: one
  class** — discoverability (`HidQuirks.` autocompletes the full
  menu) beats remembering which of `HidUpsQuirks` / `HidPosQuirks` /
  `HidIndustrialQuirks` to look in, and `Math` / `Path` show that
  static classes with many methods stay readable at scale far past
  what `HidQuirks` will ever reach. Decision deferred until the
  second domain shows up to drive it with evidence rather than
  prediction.
