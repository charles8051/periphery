---
title: "ADR-0050: GPS Support via Periphery.Serial.Nmea — NMEA-0183 Codec and {Gps} Capability Tag"
status: "Proposed"
status_note: "Not implemented - there is no `Periphery.Serial` or `Periphery.Serial.Nmea` package. Sequences behind `Periphery.Serial` ([ADR-0062](0062-periphery-serial-backend-provider.md), which superseded ADR-0028)."
date: "2026-05-31"
authors: "@charles8051 (design)"
tags: ["architecture", "decision", "gps", "gnss", "nmea", "ubx", "rtcm", "serial", "periphery-serial-nmea", "extension", "streaming", "tags", "discovery", "applies-adr-0051"]
supersedes: ""
superseded_by: ""
---

# ADR-0050: GPS Support via Periphery.Serial.Nmea

## Status

> **Applies ADR-0051.** GPS is the motivating first instance of the
> "`Category` = subsystem identity, `Tags` = capability" principle ratified in
> ADR-0051. This ADR decides the GPS-specific shape — an NMEA codec packaged as
> `Periphery.Serial.Nmea` plus a `{Gps}` capability tag — as an *application* of that
> principle. An earlier draft of this ADR explored a standalone `Periphery.Gps`
> peer-spoke package with its own `DeviceCategory`/transport design; that exploration is
> retained under **Alternatives Considered** (ALT-A) and is superseded by the decision
> below. Implementation sequences behind `Periphery.Serial`
> ([ADR-0062](0062-periphery-serial-backend-provider.md)). This ADR was drafted
> against ADR-0028; ADR-0062 superseded it with a backend-provider model but
> **retained the `PipeReader`/`PipeWriter` surface** (ADR-0062 DEC-002), so every
> decision below stands unchanged. Serial references have been repointed.

---

## Context

### GPS is a streaming sensor, not a snapshot device

ADR-0005 already ruled GPS out of the snapshot/property-change model — it is "continuous
high-frequency data; needs a subscription/stream model, not snapshot diffs." A receiver
emits a continuous stream of position fixes (typically 1–10 Hz); its value is the *stream*,
not an enumeration-time property. That puts GPS in the `Periphery.Serial` (ADR-0062) /
`Periphery.Camera` (ADR-0035) family — payload is an `IAsyncEnumerable<T>` of timestamped
data — not the enrich-a-`DeviceInfo` family. There is no useful scalar GPS property to
promote onto `DeviceInfo`.

### What a "GPS receiver" actually is, per transport

"GPS receiver" names a point in a four-axis space — physical transport, wire protocol,
device class, and OS abstraction. Conflating those axes is what makes the device look
harder to classify than it is. This ADR scopes itself to one cell: **a byte stream
carrying NMEA 0183, reached through a serial port.** The axes are separated here so the
scope boundary is explicit rather than implied.

**Axis 1 — transport (how bytes reach the host).**

| # | Transport | Host view | VID/PID identifies |
|---|---|---|---|
| 1a | **Native USB CDC-ACM on the GNSS chip** | COM port / `ttyACM` — `DeviceCategory.Ports` | The **GNSS chip**. u-blox `1546:01A7`/`01A8`/`01A9`. |
| 1b | **USB-serial bridge + UART GNSS module** | COM port / `ttyUSB` — `DeviceCategory.Ports` | The **bridge**, not the receiver. CP2102 `10C4:EA60`, FTDI `0403:6001`, PL2303 `067B:2303`, CH340 `1A86:7523`. |
| 1c | **Bluetooth Classic SPP** | COM port (Bluetooth SIG GNSS Profile 1.0 carries NMEA over SPP) | n/a |
| 1d | **BLE** | GATT — usually Nordic UART Service, sometimes Location and Navigation Service `0x1819` | n/a |
| 1e | **Raw UART / I2C (u-blox DDC) / SPI** | Nothing — embedded, no host enumeration | n/a |
| 1f | **Linux `gnss` class** | `/dev/gnss0` — a GNSS-class node, **usually with no `tty` beside it** when a serdev driver claims the UART. Not `DeviceCategory.Ports`. | n/a — `/sys/class/gnss/*/type` states the protocol directly |
| 2 | **`gpsd` daemon** | TCP `localhost:2947`, JSON — not a device | n/a |
| 3 | **OS fused-location service** | `Windows.Devices.Geolocation`, `CoreLocation`, `GeoClue` — not a device, privacy-gated | n/a |
| 4 | **Windows GNSS DDI / Sensors v1.0 class driver** | A UMDF 2.0 driver behind the location platform — not a byte stream | n/a |
| 5 | **NMEA 2000 (CAN)** | Marine bus adapter — binary PGNs, not sentences | n/a |
| 6 | **Cellular module with GNSS** | The modem's port, driven by AT commands (`AT+QGPS...`) | The modem |

**Axis 2 — wire protocol (what the bytes mean).** NMEA 0183 is the standard text layer;
its versions differ materially (2.30 appended the FAA mode indicator to RMC/GLL/VTG, 4.10
added signal ID to GSV/GSA, 4.11 standardised system ID across constellations). Vendor
extensions reuse the same `$...*HH` framing with proprietary payloads (`$PUBX`, `$PMTK`,
`$PAIR`, `$PSRF`, `$PGRM`, `$PGLOR`, `$PASHR`). Vendor **binary** protocols are a separate
family (UBX with its `0xB5 0x62` sync and Fletcher-8 checksum, SiRF Binary, Trimble
TSIP/TAIP, Septentrio SBF, NovAtel OEM) and are the only way to get raw pseudorange and
carrier-phase — **NMEA carries only the computed fix**, which is why RTK and PPP cannot
consume it. Corrections are a third family flowing the other direction (RTCM 2.x/3.x,
RTCM SSR, SPARTN, CMR/CMR+), typically delivered over NTRIP, which is HTTP/1.1 and is the
envelope rather than a format.

**Axis 3 — device class.** Consumer puck (NMEA only, 1 Hz), module/breakout (NMEA plus
vendor binary, configurable rate), RTK rover or base (requires a **write** path for
corrections), dead-reckoning/INS-fused, timing receiver or GPSDO (the value is the PPS
edge on a hardware pin, not the sentence), marine instrument (GPS is one talker among
depth, wind, AIS, heading), cellular module, SDR.

**Axis 4 — OS abstraction.** Linux stacks three layers: the raw tty, the kernel `gnss`
class, and gpsd/GeoClue above it. Windows stacks the COM port, the Sensors v1.0 class
driver, and the GNSS DDI. macOS has the tty and `CoreLocation` with nothing between them
for an external receiver.

Transports **1a** and **1b** are the overwhelmingly common case and the one that fits
Periphery's identity: raw device I/O over a byte stream. Everything else is out of scope
for this ADR (see Alternatives; NMEA 2000, BLE, and the correction path are called out in
OQ-005 through OQ-007).

### The discoverability problem (why GPS is a tag, not a category)

The receiver Periphery cares about — the byte stream on a serial port — is **invisible to
category-based discovery**, the finding that, generalised, became ADR-0051:

- The `Sensor` enum comment nominally claimed GPS, but the *serial* receiver hits none of
  the three sensor signals (Windows Sensor-class GUID, Linux `iio`, macOS HID usage
  `0x20`). ADR-0051 demotes `Sensor` itself to a tag; its OQ-004 resolves that a GPS
  receiver is tagged `{Gps}` only.
- The Windows GPS setup-class GUID `{6bdd1fc3-810f-11d0-bec7-08002be2092f}` exists in
  `DeviceClassGuids` only as a friendly-name string, wired to no `DeviceCategory` (it
  resolves to `All`).
- Transport 1a/1b enumerates as a plain `DeviceCategory.Ports` entry, indistinguishable
  from an FTDI cable. Selection is by VID/PID heuristic or user choice.

So a `DeviceCategory.Gps` would match almost nothing on the byte-stream transport. GPS is
a **capability on a generic serial port** — exactly the ADR-0051 case for a tag.

Two corrections to the sharper form of that claim, added on review. Neither reverses the
decision; both narrow what the enricher may honestly assert.

**Windows does route GPS to `Sensor` — for a different device node.** With the vendor
driver package installed, a u-blox dongle presents *both* a GNSS location Sensor device
*and* a virtual COM port; Windows additionally has a first-class GNSS DDI (a UMDF 2.0
driver behind the location platform, reached only through the location API). So one
physical dongle can yield **two `DeviceInfo` entries** under different categories. The
`{Gps}`-on-`Ports` decision is unaffected — the sensor node is not a byte stream and
Periphery does not open it — but the premise "no platform routes a GPS receiver to
`Sensor`" is false as stated, and the sensor node is a real, separately enumerated device
a caller may also see.

**Linux does have an OS-level GPS signal — attached to a different device node.** The
kernel `gnss` class exposes `/dev/gnss0` with `/sys/class/gnss/*/type` reading `NMEA`,
`SiRF`, or `UBX`: a definitive statement of what the device is, with no heuristic and no
device I/O. "There is no reliable OS signal" was a Windows/macOS observation that this ADR
wrongly generalised to all three platforms.

It does not, however, rescue serial-port discovery. The class is populated chiefly by
serdev-bound drivers, and when one claims the UART the receiver typically appears as
`/dev/gnss0` **with no `ttyUSB`/`ttyACM` node beside it**. That makes it transport **1f** —
a receiver that is not a serial port — rather than a better identity for transport 1a/1b.
Recorded here so the finding is not lost, and left out of §2's enricher deliberately.

### What already exists

`Periphery.Serial` (ADR-0062, designed, deferred behind Hid/Usb) exposes an `ISerialPort`
with a `System.IO.Pipelines` `PipeReader`/`PipeWriter` surface — precisely the byte source
a NMEA parser consumes. ADR-0062 replaced ADR-0028's single native implementation with a
backend-provider model (`Periphery.Serial.Bcl` / `Periphery.Serial.RJCP`) and turned the
sealed `SerialPort` into the `ISerialPort` interface seam; the pipe surface this ADR
consumes was retained verbatim, so the backend choice is invisible to the codec. The GPS-specific value-add is **parsing and fix synthesis**, not
I/O; the I/O is already solved.

---

## Decision

### 1. Package: `Periphery.Serial.Nmea` (a sub-package of Serial, not a peer spoke)

Ship the NMEA support as `Periphery.Serial.Nmea` — a `Periphery.{Domain}.{Sub}` sub-package
of `Periphery.Serial`, containing a **transport-agnostic NMEA-0183 sentence codec** plus a
**`GpsFix` assembler**. This is preferred over a standalone `Periphery.Gps` peer spoke
(ALT-A) for three reasons:

- **No star-topology violation.** A `Periphery.{Domain}.{Sub}` package depending on
  `Periphery.{Domain}` is the *sanctioned* direction in ADR-0024's package table (like
  `Periphery.Midi.Windows.Midi2` → `Periphery.Midi`). A peer `Periphery.Gps` would have
  forced the spoke-to-spoke escape-hatch question; the sub-package sidesteps it entirely.
- **Honest abstraction.** NMEA 0183 is a marine/serial *sentence protocol* — depth
  sounders, wind, AIS, autopilots, and heading sensors speak it too. GPS fixes
  (`GGA`/`RMC`/`GSA`/`GSV`) are *one sentence family* within NMEA. `Periphery.Gps` would
  have under-named the thing; `Periphery.Serial.Nmea` names the protocol and treats GPS
  fixes as the headline interpretation layer on top.
- **Decouples from Serial's schedule.** The codec core consumes a `PipeReader` /
  `ReadOnlySequence<byte>`, so it can be built and unit-tested against recorded NMEA logs
  *before* `Periphery.Serial` ships; the `SerialPort` convenience factory lands when Serial
  does.

```csharp
// Periphery.Serial.Nmea — transport-agnostic core
public sealed partial class NmeaReader : IAsyncDisposable
{
    public IAsyncEnumerable<NmeaSentence> Sentences { get; }   // validated, checksummed
    public IAsyncEnumerable<GpsFix> Fixes { get; }             // assembled from a sentence group
    public IAsyncEnumerable<GpsSatelliteSnapshot> Satellites { get; }

    public static NmeaReader FromReader(PipeReader reader, NmeaOptions? options = null);
    public static NmeaReader FromStream(Stream stream, NmeaOptions? options = null);

    public ValueTask DisposeAsync();
}

// Convenience factory — lands with Periphery.Serial (ADR-0062).
// Static factory on NmeaReader, matching HidDevice.OpenAsync / UsbDevice.OpenAsync.
// Opens via the configured backend provider and wraps the resulting ISerialPort.Reader.
public sealed partial class NmeaReader
{
    public static Task<NmeaReader> OpenAsync(
        DeviceInfo serialPort, SerialPortOptions? options = null, CancellationToken ct = default);

    public static NmeaReader FromSerialPort(ISerialPort port, NmeaOptions? options = null);
}
```

### 2. Discovery: `Category = Ports` + `{Gps}` capability tag (per ADR-0051)

There is **no `DeviceCategory.Gps`**. A GPS receiver is `DeviceCategory.Ports` (the
subsystem truth — it is a serial port) carrying a `{Gps}` capability tag, emitted by a
**metadata-only enricher** (ADR-0026: no handle, no device I/O). Definitive "does this
port actually emit `$GPGGA`?" confirmation is the **codec's** job on open, not the
enricher's.

**The enricher tags on exactly one signal.** The original draft specified a VID/PID quirk
table without qualification; the transport taxonomy above shows the qualification it
needs. Everything else that looks like a signal is listed here as a non-signal, because
naming what must *not* tag is the substance of this decision.

| Signal | Verdict |
|---|---|
| VID/PID matches a curated **native-USB GNSS** table (u-blox `1546:01A7`/`01A8`/`01A9`, …) | **Tags.** On transport 1a the VID/PID belongs to the GNSS chip itself, so the device *is* a receiver. This is the whole of the enricher. |
| Bare bridge VID/PID (`10C4:EA60`, `0403:6001`, `067B:2303`, `1A86:7523`) | **Must not tag.** Shared with thousands of unrelated devices; matching would tag every CP2102 serial cable as a GPS. |
| USB `iProduct` / interface string behind a bridge VID/PID | **Must not tag.** The descriptor belongs to the *bridge*, not to the module on the UART behind it. The string is vendor-writable, frequently generic or stale, and a non-GNSS product using the same bridge can carry a GNSS-looking string. May be surfaced as a **user-selection hint**; it is not evidence of capability. |
| Linux `gnss` class (`/sys/class/gnss/*/type`) | **Not this enricher's signal.** It describes a *different device node* — see below. |

A `{Gps}` tag that is wrong is worse than one that is absent: it is a capability claim
callers act on. So the enricher claims only where the OS identity is the receiver's own,
and a bridge-attached receiver (transport 1b) is found by user selection or by opening the
port and looking for sentences — which is the codec's job anyway.

```csharp
var gps = await Devices.Enumerate()
    .OfCategory(DeviceCategory.Ports)   // scope the scan to serial ports
    .WithTag("Gps")                     // capability tag from the VID/PID enricher
    .FirstOrDefaultAsync();
```

**Where the I/O lives.** `NmeaReader.OpenAsync(DeviceInfo, …)` takes discovery *metadata*
and opens through `Periphery.Serial`'s backend provider; it ships in
`Periphery.Serial.Nmea`, not in core `Periphery`. That is the established shape for an
ADR-0024 extension package — `HidDevice.OpenAsync(DeviceInfo, ct)` and
`UsbDevice.OpenAsync(DeviceInfo, ct)` are the same handoff. `DeviceInfo` and its enrichers
stay metadata-only (ADR-0026); nothing in core enumeration opens a handle. A static
factory on the reader is preferred over a `this DeviceInfo` extension method so the shape
matches Hid and Usb rather than inventing a third convention.

The `{Gps}` constant and its enricher ship together (ADR-0047 anti-speculation rule),
emitted as a fresh `"Gps"` string from `Periphery.Serial.Nmea` (the tag set is open; no
core PR). Per ADR-0051 OQ-004, the serial receiver is tagged **`{Gps}` only**, not also
`{Sensor}` — its signal is the native-USB VID/PID above, not the HID-usage sensor signal. A Windows
GNSS *sensor* node for the same physical dongle (see Context) is a separate `DeviceInfo`
that this enricher does not touch; `SensorEnricher` may tag it `{Sensor}` on its own
signal, and that is correct — they are two device nodes, not one device tagged twice.

Because the enricher declares `ITagEmittingEnricher.Scope`, that scope must cover serial-
port enumeration on each platform, or a bare `WithTag("Gps")` with no `OfCategory`
silently finds nothing.

**The Linux `gnss` class is a different device node, not a stronger signal on this one.**
`/sys/class/gnss/*/type` reads `NMEA`, `SiRF`, or `UBX` with no heuristic and no I/O, which
makes it tempting as a discovery signal. It is not one *for a `Ports` `DeviceInfo`*, for a
reason that is easy to miss: the class is populated chiefly by serdev-bound drivers
(`gnss-ublox`), and when such a driver claims the UART **there is usually no `ttyUSB` /
`ttyACM` node at all** — the receiver appears as `/dev/gnss0` and nothing else. So the
`gnss` entry is not a better identity for a serial port; it is a receiver that is not a
serial port. Tagging a `Ports` entry from it would require an association rule this ADR
does not have, and inventing one risks tagging the wrong port on a machine with several.
Transport **1f** in the taxonomy above; whether Periphery should surface it as a device in
its own right is **OQ-005**, and it is out of scope here either way.

### 3. The fix model — receiver UTC *and* a monotonic stamp

```csharp
public sealed record GpsFix
{
    public required DateTimeOffset UtcTime { get; init; }       // receiver wall-clock — the point of the device
    public required TimeSpan CaptureTimestamp { get; init; }    // monotonic, from open (ADR-0024 epoch) for ordering
    public required double LatitudeDegrees { get; init; }
    public required double LongitudeDegrees { get; init; }
    public double? AltitudeMetersMsl { get; init; }
    public GpsFixQuality Quality { get; init; }
    public int SatellitesInUse { get; init; }
    public double? Hdop { get; init; }
    public double? SpeedMetersPerSecond { get; init; }
    public double? CourseDegreesTrue { get; init; }
    public GnssConstellations Constellations { get; init; }
}

public enum GpsFixQuality { NoFix, Gps, Dgps, Pps, RtkFixed, RtkFloat, Estimated, Manual, Simulation }
```

Carrying both `UtcTime` and `CaptureTimestamp` is a **deliberate, documented deviation**
from ADR-0024's "monotonic `TimeSpan` only, no `DateTimeOffset`" rule. GPS is, among other
things, a clock; suppressing the receiver-reported UTC would discard the point of the
device. The monotonic stamp is retained for intra-stream sequencing.

### 4. Decoder & AoT posture

NMEA 0183 is line-oriented ASCII with a `*HH` XOR checksum — a `SequenceReader<byte>` over
the `PipeReader`, allocation-light, no native callbacks. A single `GpsFix` is synthesised
from a sentence group arriving in one epoch (`GGA` position/altitude/quality, `RMC`
UTC/date/speed/course, `GSA` DOP/mode, `GSV` satellites); multi-constellation receivers use
`GN` talker IDs. The **ADR-0024 two-zone GC ring buffer is not required**: fixes arrive at
≤ 10 Hz and carry their own authoritative timestamp, so a GC pause on the drain path cannot
corrupt them (unlike MIDI/HID, where the OS callback timestamp is the only clock). UBX
(binary, length-prefixed, Fletcher checksum) is an optional second decoder behind the same
`Fixes` surface (OQ-002). Serial P/Invoke is inherited from `Periphery.Serial`;
`Periphery.Serial.Nmea` has essentially no new native surface.

---

## Consequences

### Positive

- **POS-001**: GPS is treated as the streaming sensor ADR-0005 said it was, and as the
  capability-on-a-generic-port that ADR-0051 says it is — no special-case category, no
  star-topology exception.
- **POS-002**: The codec consumes a `PipeReader`, so it reuses `Periphery.Serial`'s solved
  I/O without duplicating it, is testable against recorded logs with no hardware, and can
  be authored before Serial ships.
- **POS-003**: Naming the package `.Nmea` rather than `.Gps` captures the broader NMEA-0183
  device family (marine instruments) honestly; GPS-fix assembly is a layer, not the whole.
- **POS-004**: Discovery via `{Gps}` tag composes (`OfCategory(Ports).WithTag("Gps")`) and
  needs no core enum change.

### Negative

- **NEG-001**: Discovery coverage is **asymmetric by transport, by design**. Native-USB
  receivers (1a) are identified from their own VID/PID. Bridge-based receivers (1b) are
  **not discovered at all** — at the descriptor level they are indistinguishable from any
  other CP2102/FTDI/CH340 cable, and the only differentiator (the bridge's `iProduct`
  string) is vendor-writable and not authoritative. This ADR accepts total false negatives
  on 1b rather than any false positives, because `{Gps}` is a capability claim callers act
  on. The cost is real: a large share of hobby and budget receivers are 1b, and for those
  the user must select the port manually. No design fixes it — the information is not
  present in the OS.
- **NEG-002**: `GpsFix` carrying `DateTimeOffset` is a conscious deviation from ADR-0024's
  timestamp rule (NEG, justified: GPS is a clock).
- **NEG-003**: Sequenced behind `Periphery.Serial` (ADR-0062), which is itself deferred
  behind Hid/Usb. The codec can lead; the end-to-end "open a GPS dongle" cannot until
  Serial exists.
- **NEG-004**: The v1 surface is **read-only**, so RTK is out of reach even though
  `GpsFixQuality` enumerates `RtkFixed` and `RtkFloat`. An RTK rover needs corrections
  (RTCM 3.x / SPARTN, usually over NTRIP) written back down the same link, and
  `NmeaReader.FromReader(PipeReader)` has no writer. Those quality values are reportable
  when an *externally* corrected receiver produces them — Periphery just cannot be what
  supplies the corrections. Deliberate scope boundary, not an oversight (OQ-007).

---

## Alternatives Considered

### ALT-A — Standalone `Periphery.Gps` peer-spoke package (this ADR's earlier draft)

A transport-agnostic `GpsReceiver` shipped as a peer spoke of `Periphery.Serial`, with
`gpsd` and OS-location as pluggable sources, and the category/timestamp questions explored
as open forks. **Rejected** in favour of the `Periphery.Serial.Nmea` sub-package: the peer
spoke forced the spoke-to-spoke star-topology escape-hatch question (ADR-0024) that the
sub-package avoids, and `Periphery.Gps` under-named the NMEA-0183 protocol family. The
exploration was valuable — it surfaced the discoverability finding that became ADR-0051 —
but the sub-package is the cleaner home.

### ALT-B — `DeviceCategory.Gps`

A first-class category. **Rejected** per ADR-0051: on the dominant transport the OS exposes
a generic serial port with no GPS signal, so the category would match almost nothing while
implying discoverability that does not exist. The `{Gps}` tag is the honest expression.

### ALT-C — OS fused-location (`Geolocator`/`CoreLocation`/`GeoClue`)

Wrap the OS location services (transport 3). **Out of scope.** Fused location is not a
peripheral, has no `DeviceInfo`, and is privacy-capability-gated; it belongs in a separate
library (a hypothetical `Periphery.Location`), not in a serial NMEA codec.

### ALT-D — `gpsd` client

A TCP client to `localhost:2947` (transport 2). **Out of scope for v1**, but a natural
later source: because `NmeaReader.FromReader` is transport-agnostic, a `gpsd`/NMEA-over-TCP
source can feed the same codec without touching the core (OQ-003).

---

## Open Questions

- **OQ-001**: Where exactly does the GPS enricher (and the `"Gps"` tag constant)
  live — in `Periphery.Serial.Nmea`, or in a tiny separate enricher package so the pure
  codec has zero enricher concern? Lean: in `Periphery.Serial.Nmea` (one package, the
  enricher emits a fresh `"Gps"` string).
- **OQ-002**: NMEA-only for v1, or include UBX binary decoding? UBX unlocks higher rates
  and raw measurements (RTK) but is vendor-specific. Lean: NMEA 0183 first; UBX behind the
  same `Fixes` surface later.
- **OQ-003**: Add a `gpsd` / NMEA-over-TCP source (transport 2) as a follow-up source on
  the transport-agnostic core? Lean: yes, post-v1, once a consumer needs it.
- **OQ-004**: Naming of the data model — `Periphery.Serial.Nmea` is the package; keep the
  fix model in GNSS terms (`GnssConstellations`) where accurate while the package keeps the
  familiar `Nmea`/`Gps` vocabulary. Lean: yes (package = protocol, model = GNSS-accurate).
- **OQ-005**: Should Periphery enumerate the Linux `gnss` class (transport 1f) as devices
  at all? These are receivers with no serial port, so they cannot be reached by the
  `OfCategory(Ports)` query and `Periphery.Serial` cannot open them; supporting them means
  a Linux-provider enumeration (ADR-0057) plus a `FromStream` path straight to `/dev/gnss0`
  that bypasses `Periphery.Serial` entirely. Lean: not for v1 — no consumer has one, and
  the codec's transport-agnostic core means adding it later costs nothing structurally.
  Explicitly **not** a signal for the `Ports` enricher (see §2).
- **OQ-006**: How is the Windows dual-node case (one dongle → a `Ports` entry plus a
  Sensor-class entry) presented? Options: leave them as two unrelated `DeviceInfo`
  entries; or link them via the ADR-0078 topology forest, where both descend from the same
  USB device. Lean: leave them unlinked for v1 and revisit once topology is on `main` —
  the link is real but nothing consumes it yet.
- **OQ-007**: When a correction path (RTCM 3.x / SPARTN in, per NEG-004) is eventually
  wanted, does it belong here or in a separate package? NMEA-out and RTCM-in are different
  protocols sharing one link, so a bidirectional `NmeaReader` would under-name it. Lean: a
  sibling `Periphery.Serial.Rtcm` over the same `ISerialPort`, composed by the consumer;
  not v1.

---

## Relationship to Prior ADRs

| ADR | Relationship |
|---|---|
| ADR-0005 | Property-change events — established GPS is streaming, not snapshot data (the premise here). |
| ADR-0024 | Extension package pattern — the `Periphery.{Domain}.{Sub}` sub-package rule that makes `Periphery.Serial.Nmea` legal without a spoke-to-spoke dependency; the timestamp rule deviated from in §3. |
| ADR-0026 | Enricher I/O boundary — the GPS enricher is metadata-only (no handle, no device I/O). |
| ADR-0057 | Linux extension backends — where `gnss`-class enumeration would live if OQ-005 is ever answered yes. |
| ADR-0078 | Device topology as a rooted forest — the mechanism that could link the Windows dual-node case (OQ-006). |
| ADR-0028 | `Periphery.Serial`, original design — **superseded by ADR-0062**. Cited here only for history; do not build against it. |
| ADR-0062 | `Periphery.Serial` as it now stands — the backend-provider model, the `ISerialPort` seam, and the retained `PipeReader` surface this builds on; sequences behind it. |
| ADR-0047 | Device tags — the `{Gps}` capability tag and the anti-speculation rule for its constant. |
| ADR-0051 | **Governing principle** — "Category = subsystem, Tags = capability"; GPS is its first ratified instance. This ADR is an application of ADR-0051. |
