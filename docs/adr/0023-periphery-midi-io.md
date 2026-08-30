---
title: "ADR-0023: Periphery.Midi — Cross-Platform MIDI I/O Extension"
status: "Proposed"
status_note: "Not implemented - there is no `Periphery.Midi` package."
date: "2026-07-14"
authors: "@charles8051 (design)"
tags: ["architecture", "decision", "midi", "midi2", "ump", "extension", "api-design", "periphery-midi", "i/o", "alsa", "coremidi", "winmm"]
supersedes: ""
superseded_by: ""
---

# ADR-0023: Periphery.Midi — Cross-Platform MIDI I/O Extension

## Context

ADR-0021 added `DeviceCategory.Midi` to the core library and explicitly deferred all
MIDI I/O to a dedicated `Periphery.Midi` package. That ADR identified four structural
problems that make MIDI I/O substantially harder than `Periphery.Hid` (ADR-0020). This
ADR identifies a fifth that is the most significant of all:

1. Timestamping is first-class and correctness-critical.
2. MIDI is a variable-length streaming protocol, not a fixed-size report API.
3. The three platform APIs are not analogous abstractions — they are genuinely different
   architectural models.
4. The industry is mid-transition from MIDI 1.0 to MIDI 2.0 / UMP.
5. **The .NET Garbage Collector is non-deterministic and cannot be excluded from managed
   threads — including callback threads — without an explicit architectural boundary.**

This ADR works through each of these problems, commits to a design, and specifies the
`Periphery.Midi` API surface.

---

### Problem 1 — Timestamp normalisation

A MIDI message without a high-resolution timestamp is musically unusable at any tempo
above approximately 60 BPM. At 120 BPM a sixteenth note lasts 125 ms; audible jitter
in a live recording begins well below 10 ms. Clock normalisation is therefore a
correctness constraint, not a quality-of-life feature.

The four platform timestamp sources are incompatible in origin, unit, and resolution:

| Platform | Timestamp source | Units | Resolution | Origin |
|---|---|---|---|---|
| Windows WinMM | `midiInProc` `dwParam2` | Milliseconds | ~1–15 ms | `timeGetTime` since `midiInStart` |
| Windows MIDI 2.0 | UMP packet timestamp | 100 ns | < 1 µs | `QueryPerformanceCounter` epoch |
| Linux ALSA seq | `snd_seq_real_time_t` | Seconds + nanoseconds | < 1 µs | `CLOCK_MONOTONIC` |
| macOS CoreMIDI | `MIDITimeStamp` host ticks | `mach_absolute_time` ticks | < 1 µs | Boot time |

**Resolution gap on WinMM is fundamental, not fixable.** WinMM timestamps are integers
in milliseconds. Even with `timeBeginPeriod(1)` to improve the multimedia timer
resolution, WinMM timestamps are limited to approximately 1 ms granularity, and
`timeBeginPeriod` is a system-wide setting that is inappropriate for a library to
modify. This is a known and accepted limitation of WinMM.

**Normalisation strategy:** `Periphery.Midi` normalises all timestamps to a
`TimeSpan` offset from the moment the port was opened, using `Stopwatch` as the
normalisation reference. At port-open time the backend captures `Stopwatch.GetTimestamp()`
alongside the platform's own reference point. Subsequent platform timestamps are
converted to `Stopwatch`-relative ticks, then to `TimeSpan`. This approach:

- Requires no system-wide timer changes.
- Is coherent across multiple ports opened in the same process (they share the same
  `Stopwatch` epoch).
- Preserves the full sub-millisecond precision of ALSA, CoreMIDI, and Windows MIDI 2.0
  where available.
- Honestly degrades to ~1 ms granularity on WinMM without hiding it from callers.

An absolute `DateTimeOffset` is intentionally **not** provided. Correlating high-resolution
monotonic timestamps with wall clock time introduces platform-specific drift and is not
necessary for the primary use case (sequencing, recording, live performance).

---

### Problem 2 — Variable-length streaming protocol

MIDI 1.0 on the wire is a stream of status bytes and data bytes with no framing
other than the high bit of status bytes. The three message forms are:

- **System real-time** — 1 byte (`0xF8`–`0xFF`). No data. May appear mid-SysEx.
- **Short channel and system** — 2 or 3 bytes. Status byte + 1 or 2 data bytes.
- **SysEx** — `0xF0` followed by arbitrary data bytes, terminated by `0xF7`. May be
  hundreds or thousands of bytes. Manufacturer-specific.

**Running status** further complicates parsing: a device may omit the status byte on
consecutive messages of the same type, requiring the parser to carry state. Real-time
messages interrupt SysEx without resetting the SysEx state machine.

Platform APIs differ in how much parsing they do before delivering events:

| Platform | Delivered form |
|---|---|
| Windows WinMM | Short messages packed into a `DWORD`; SysEx via `MIDIHDR` buffer chain |
| Windows MIDI 2.0 | UMP packets (already framed; no running status) |
| Linux ALSA rawmidi | Raw byte stream; parser required in user space |
| Linux ALSA seq | `snd_seq_event_t` structs (already decoded, running status resolved) |
| macOS CoreMIDI | `MIDIPacketList` of `MIDIPacket`s; SysEx reassembled across packets |

**`Periphery.Midi` will use the highest-level OS API available on each platform** to
avoid re-implementing the byte-stream parser. Specifically: ALSA seq (not rawmidi)
on Linux; `MIDIPacketList` reassembly via CoreMIDI on macOS; WinMM `MIDIHDR` callbacks
on Windows. The platform backend is responsible for delivering complete, parsed messages
to the internal `Channel<MidiMessage>` — callers never see a partial or running-status
message.

SysEx messages are delivered as a single `MidiMessage` with `Kind == SysEx` and a
`ReadOnlyMemory<byte>` payload. Because SysEx payloads can be large, they are allocated
from a shared `MemoryPool<byte>` managed by the port; the memory is valid for the
duration of the `await foreach` iteration but should not be retained beyond the
consuming iteration step without copying.

---

### Problem 3 — Platform API fragmentation

The four API surfaces differ not just in their message delivery model but in their
fundamental architectural concepts.

#### Windows WinMM (MIDI 1.0)

`winmm.dll` exposes MIDI 1.0 as two families of functions:

- **Input:** `midiInOpen`, `midiInStart`, `midiInStop`, `midiInClose`. A
  callback function (`MIDIINPROC`) is registered at open time and fires on a
  dedicated OS thread for each short message (`MIM_DATA`) or completed SysEx
  buffer (`MIM_LONGDATA`).
- **Output:** `midiOutOpen`, `midiOutShortMsg`, `midiOutLongMsg`, `midiOutClose`.

WinMM is always available on Windows Vista and later. It is single-client per device —
only one process can open a given MIDI device for input or output at a time.

#### Windows MIDI 2.0 (`Windows.Devices.Midi2`)

Available on Windows 11 22H2 (build 22621) and later. This is a WinRT API in the
`Windows.Devices.Midi2` namespace. It provides:

- A MIDI 2.0 service (`MidiService`) with system-wide routing, virtual ports,
  and loopback endpoints.
- Multi-client: multiple applications can open the same endpoint simultaneously.
- High-resolution timestamps (100 ns, QPC-based).
- Backward compatibility: MIDI 1.0 devices are exposed as UMP endpoints with
  automatic MIDI 1.0 ↔ UMP translation.
- `MidiSession.Open()` → `MidiInputEndpointConnection` / `MidiOutputEndpointConnection`.
- UMP messages delivered via `MessageReceived` event or `IAsyncEnumerable`.

The WinRT dependency creates the same TFM coupling concern addressed in ADR-0015
through ADR-0018. Specifically, WinRT types are only available with the
`Microsoft.Windows.CsWinRT` / `Microsoft.Windows.SDK.Contracts` packages and
the `[SupportedOSPlatform("windows10.0.22621.0")]` guard. The Windows MIDI 2.0
backend is therefore an **optional upgrade path**: present only when the OS
supports it, with WinMM as the always-available fallback.

#### Linux ALSA

ALSA exposes MIDI through two interfaces:

- **rawmidi** (`/dev/snd/midiCxDy`) — raw byte stream. No running-status resolution,
  no timestamping, no routing. Simplest but lowest-level.
- **seq** (`/dev/snd/seq`) — the ALSA sequencer. Delivers `snd_seq_event_t` structs with
  `snd_seq_real_time_t` timestamps, resolved running status, multi-client access, and
  software routing via `aconnect`. This is the preferred interface.

The ALSA seq API is a C API accessed via P/Invoke to `libasound.so.2`. Key operations:
`snd_seq_open`, `snd_seq_set_client_name`, `snd_seq_create_simple_port`,
`snd_seq_connect_from` / `snd_seq_connect_to`, `snd_seq_event_input` (blocking),
`snd_seq_nonblock` + `poll` for non-blocking read.

ALSA UMP (Universal MIDI Packet) support was added in kernel 6.5 and ALSA lib 1.2.10.
It exposes UMP endpoints alongside legacy seq ports, accessible via the same
`snd_seq` API family. Periphery.Midi targets ALSA seq for MIDI 1.0 baseline and
detects UMP endpoint capability at runtime for MIDI 2.0.

#### macOS CoreMIDI

CoreMIDI (`CoreMIDI.framework`) is the most architecturally distinct platform. It is
a system-wide MIDI routing graph managed by the `MIDIServer` daemon:

- **`MIDIClientRef`** — a process-level registration with the MIDI server.
- **`MIDIPortRef`** — an input or output port on the client.
- **`MIDIEndpointRef`** — a source (input) or destination (output) in the routing graph.
  Devices expose endpoints; virtual endpoints can be created by any client.
- **`MIDIInputPortCreate`** with a `MIDIReadProc` callback (MIDI 1.0) or
  `MIDIInputPortCreateWithProtocol` + `MIDIReceiveBlock` (MIDI 2.0, macOS 13+).
- **`MIDIPortConnectSource`** — routes events from an endpoint into the port's callback.

The CoreMIDI callback delivers `MIDIPacketList` (MIDI 1.0) or `MIDIEventList` (MIDI 2.0).
`MIDIPacketList` may split a SysEx message across multiple `MIDIPacket`s; the backend
reassembles these before posting to the internal channel.

Timestamps on CoreMIDI are `MIDITimeStamp` values (typedef `UInt64`), which are
`mach_absolute_time` ticks. Converting to nanoseconds requires `mach_timebase_info` to
obtain the numer/denom ratio. The Periphery.Midi macOS backend captures the
`mach_absolute_time` tick at port-open time and converts all subsequent timestamps
to `Stopwatch`-relative `TimeSpan` via the same tick-to-ns ratio.

---

### Problem 4 — MIDI 1.0 vs MIDI 2.0

MIDI 2.0 introduced Universal MIDI Packets (UMP), per-note controllers, 32-bit resolution
for pitch bend and velocity, and a bidirectional capability negotiation protocol (MIDI-CI).
It is backward compatible by design: MIDI 2.0 endpoints can communicate with MIDI 1.0
devices via automatic translation in the OS driver.

**OS support matrix:**

| Platform | MIDI 1.0 | MIDI 2.0 (UMP) | Minimum requirement |
|---|---|---|---|
| Windows WinMM | ✅ Always | ❌ | Windows Vista |
| Windows MIDI 2.0 | ✅ (via translation) | ✅ | Windows 11 22H2 |
| Linux ALSA seq | ✅ Always | ✅ (kernel ≥ 6.5) | libasound ≥ 1.2.10 |
| macOS CoreMIDI | ✅ Always | ✅ (macOS ≥ 13) | — |

MIDI 2.0 hardware adoption is growing but not yet ubiquitous. As of 2026, a library that
targets MIDI 2.0 exclusively would be unusable with the majority of deployed hardware.
A library that targets MIDI 1.0 exclusively leaves value on the table for users with
MIDI 2.0 devices on supported OS versions.

**Decision: MIDI 1.0 is the universal baseline; MIDI 2.0 is an opt-in upgrade path.**
`MidiInputPort` and `MidiOutputPort` default to `MidiProtocol.Midi1`. Callers who pass
`MidiProtocol.Midi2` receive `UniversalMidiPacket` events and must handle UMP framing
themselves. On platforms or OS versions that do not support MIDI 2.0, the port
degrades to `MidiProtocol.Midi1` and the negotiated protocol is reflected in the
`MidiInputPort.Protocol` / `MidiOutputPort.Protocol` properties after open.

---

### Problem 5 — The Garbage Collector

This is the elephant in the room. The AoT constraint above (and the JIT warmup problem
it addresses) has a correct, complete solution: publish with `PublishAot=true`. The GC
does not.

#### What the GC does to managed threads

The .NET garbage collector periodically suspends managed threads to scan object roots,
compact the heap, and reclaim unreachable memory. Under the concurrent background GC
used in .NET 5+, most work is done concurrently, but **stop-the-world phases are
unavoidable**: the GC must briefly freeze all managed threads to complete the initial
mark and final mark phases of each collection cycle.

| Collection tier | Typical stop-the-world pause |
|---|---|
| Gen0 / Gen1 | 0.1 – 2 ms |
| Gen2 (background) | 1 – 10 ms |
| Gen2 full blocking / LOH compaction | 10 – 100+ ms |

These pauses occur when **any** managed thread in the process triggers a collection —
not just the thread that is allocating. A callback thread that never allocates a single
object is still subject to GC pauses caused by background tasks, logging, the async
state machine infrastructure, or any other managed code running concurrently.

#### NativeAOT does not help

NativeAOT compiles all managed code to native machine code ahead of time, eliminating
the JIT. It does not remove the GC. The NativeAOT runtime ships its own GC (derived
from the CoreCLR GC) with the same stop-the-world model. A callback thread in a
`PublishAot=true` application is still a managed thread and is still subject to GC
pauses. AoT publication solves Problem AoT; it does not solve Problem 5.

#### Why this specifically breaks MIDI timestamp accuracy

Problem 1 established that the timestamp must be captured in the native callback with
sub-millisecond accuracy. If the callback executes on a managed thread, the GC can
pause it between the OS firing the callback and the library capturing
`Stopwatch.GetTimestamp()`. The captured timestamp then reflects a time slightly later
than the actual event, introducing the same jitter the timestamp strategy was designed
to eliminate — and doing so silently, with no error, in a pattern that is impossible to
reproduce deterministically.

Every managed-code MIDI library that routes callbacks through managed threads has this
problem. It is the primary reason professional real-time audio and MIDI software is
written in C and C++.

#### The correct architectural boundary

The fix is to split the callback path at the managed/unmanaged boundary:

```
╔══════════════════════════════════════════════════════════╗
║  GC-FREE ZONE  (unmanaged thread or pinned-only access)  ║
║                                                          ║
║  Native OS callback  ([UnmanagedCallersOnly] static)     ║
║    1. Capture Stopwatch.GetTimestamp() immediately        ║
║    2. Pack status + data bytes + ticks into               ║
║       MidiRingEntry  (blittable struct, no GC refs)       ║
║    3. MidiRingBuffer.TryWriteUnsafe(ref entry)            ║
║       ← lock-free CAS into GCHandle-pinned array         ║
╚══════════════════════════════════════════════════════════╝
                          │
                  (ring buffer drain)
                          │
╔══════════════════════════════════════════════════════════╗
║  MANAGED ZONE  (GC-visible; timing non-critical here)    ║
║                                                          ║
║  Dedicated drain thread                                  ║
║    1. SpinWait → MidiRingBuffer.TryRead(out entry)       ║
║    2. Reconstruct MidiMessage from blittable entry        ║
║       Short msgs: inline struct — zero allocation         ║
║       SysEx:      rent from MemoryPool<byte>              ║
║    3. channel.TryWrite(message)                          ║
╚══════════════════════════════════════════════════════════╝
```

The timestamp is captured **before** the ring buffer write, entirely within the
GC-free zone. Even if the drain thread is paused for a multi-millisecond GC collection,
the timestamp already recorded in the ring entry accurately reflects when the hardware
event occurred. The consumer sees a correct timestamp on a slightly delayed delivery —
which is the correct tradeoff for any non-hard-real-time system.

#### The `MidiRingBuffer` — design constraints

`MidiRingBuffer` is the seam between the two zones. Its constraints are strict:

- **Pre-allocated at `OpenAsync` time.** The ring buffer array is fixed-size and never
  resized. No allocation occurs after the port is open.
- **Backed by a GCHandle-pinned array.** The callback receives a raw `nint` pointer to
  the pinned array entries, not a managed object reference. The GC cannot move the
  array; the pointer is stable for the lifetime of the port.
- **Blittable entries only.** `MidiRingEntry` is a `[StructLayout(LayoutKind.Sequential)]`
  struct containing only primitive types: `byte Status`, `byte Data1`, `byte Data2`,
  `byte Flags`, `long TimestampTicks`. No managed object references. The GC does not
  scan it.
- **Lock-free write via CAS on the head index.** The write path is a single
  compare-and-swap on a 32-bit integer. No locks, no allocations, no managed calls.
- **Power-of-two capacity** for efficient index wrapping via bitwise AND.
- **SysEx is separated.** Variable-length SysEx messages cannot fit in a fixed-size
  ring entry. SysEx is never timing-critical (it carries patch dumps, not performance
  events), so it is delivered through a separate pre-allocated byte staging buffer
  with relaxed timing requirements. The ring entry for a SysEx chunk contains only
  a staging-buffer offset and byte count.

```csharp
[StructLayout(LayoutKind.Sequential)]
internal struct MidiRingEntry
{
    public byte  Status;         // MIDI status byte
    public byte  Data1;          // data byte 1 (0 if not applicable)
    public byte  Data2;          // data byte 2 (0 if not applicable)
    public byte  Flags;          // bit 0: IsSysExChunk; bit 1: IsSysExEnd
    public int   SysExOffset;    // byte offset into staging buffer (IsSysExChunk only)
    public int   SysExLength;    // byte count in this chunk   (IsSysExChunk only)
    public long  TimestampTicks; // Stopwatch.GetTimestamp() at capture time
}
```

---

### Constraint — Ahead-of-Time (AoT) Compilation ⚠️ HIGH PRIORITY

AoT compatibility is a **first-class correctness requirement** for `Periphery.Midi`,
not a deployment convenience. The reason is physical: JIT compilation and real-time
MIDI input share a process, and they are mutually hostile.

When the CLR JIT-compiles a method for the first time, it stalls the calling thread
for anywhere from a few microseconds (trivial methods) to several milliseconds (complex
generics, first-time interface dispatch, first-time delegate stub generation). The MIDI
callback threads managed by WinMM, CoreMIDI, and ALSA seq are real-time or
near-real-time OS threads. A JIT stall on that thread produces a timestamp gap that is
indistinguishable to the application from actual MIDI input jitter. At 120 BPM with
100 µs resolution, a 2 ms JIT stall misrepresents an event's musical position by nearly
one sixty-fourth note.

This is not a theoretical concern. It is a documented failure mode in managed-code
audio and MIDI software, and one of the primary reasons game engines and DAW plugin
hosts avoid the JIT entirely on their real-time threads.

**AoT eliminates the problem at the root.** A `PublishAot=true` application has no JIT;
all methods are pre-compiled to native code before first call. The callback path runs
with fully predictable latency from first invocation.

#### What AoT requires from the implementation

Every component in the GC-free zone (native callback → `MidiRingBuffer.TryWriteUnsafe`)
must be free of runtime-generated code. The managed drain zone (ring buffer →
`Channel<T>.TryWrite`) is not on the timing-critical path and is not subject to this
constraint, but it must still compile and run correctly under NativeAOT. The concrete
requirements are:

| Requirement | Why |
|---|---|
| All P/Invoke must use `[LibraryImport]` (source-generated) | `[DllImport]` with non-blittable parameters requires the JIT-only runtime marshalling layer, which does not exist under NativeAOT |
| Native callbacks must be `[UnmanagedCallersOnly]` static methods | A regular `delegate` passed to native code requires the JIT to generate a native-to-managed stub at runtime; `[UnmanagedCallersOnly]` emits the stub at compile time |
| Windows MIDI 2.0 / WinRT types must be registered via `[GeneratedWinRTExposedExternalType]` | The CsWinRT CCW factory trimming problem documented in ADR-0016 applies directly here; all .NET types passed across the WinRT ABI boundary must be statically rooted |
| No reflection-based dispatch on the callback path | `Type.GetMethod`, `MethodInfo.Invoke`, `Activator.CreateInstance` are not available under NativeAOT trim unless explicitly rooted |
| Blittable types in P/Invoke signatures where possible | Non-blittable types require marshalling code that the trimmer may remove |

#### Cold-path warming as a secondary mitigation

The GC-free zone (callback → ring buffer write) is entirely `[UnmanagedCallersOnly]`
code compiled to native stubs — there is nothing to warm there even under JIT. The
managed drain zone (ring buffer → parse → `channel.TryWrite`) does benefit from
pre-warming under JIT builds, since a JIT stall in the drain path increases delivery
latency (though not timestamp accuracy, because timestamps were captured earlier in the
GC-free zone).

For callers who cannot use `PublishAot=true`, `MidiInputPort.OpenAsync` should
pre-warm the drain path by posting a synthetic zero-entry through the full parse and
channel-write path before returning. This JIT-compiles all drain-zone methods before
live events arrive, removing the worst-case first-message delivery stall.

Pre-warming is a best-effort fallback. AoT publication is the authoritative solution.
Neither addresses GC pauses on the drain thread — that is accepted and by design: GC
pauses affect delivery latency, not timestamp accuracy.

---

## Decision

### Two-port API shape

MIDI I/O is port-oriented, not device-oriented. A single USB MIDI device may expose
multiple independent input and output ports (e.g. a multi-port MIDI interface, or a
keyboard that exposes a MIDI DIN port and a USB port separately). Each port is
enumerated as a distinct `DeviceInfo` entry with `DeviceCategory.Midi`.

`Periphery.Midi` exposes two I/O primitives that together form the **Layer 1** surface
in the ADR-0024 extension package pattern. `MidiDeviceProxy` is the **Layer 2**
lifecycle manager. There is no Layer 3 enrichment because MIDI port metadata
(`MidiPortDirection`) is already a typed `init` property on `DeviceInfo` (ADR-0021).

`Periphery.Midi` exposes two I/O primitives: `MidiInputPort` and `MidiOutputPort`.

### `MidiInputPort` — the Layer 1 input I/O primitive

```csharp
public sealed class MidiInputPort : IAsyncDisposable
{
    // Discovery context
    public DeviceInfo DeviceInfo { get; }

    // The protocol actually negotiated at open time.
    // May differ from the requested protocol if the device or OS doesn't support MIDI 2.0.
    public MidiProtocol Protocol { get; }

    // MIDI 1.0 stream — available when Protocol == Midi1
    public IAsyncEnumerable<MidiMessage> Messages { get; }

    // MIDI 2.0 stream — available when Protocol == Midi2
    public IAsyncEnumerable<UniversalMidiPacket> Packets { get; }

    // Factory — bridge from enumeration to I/O
    // Throws MidiPortException if the port cannot be opened (exclusive access, etc.)
    public static Task<MidiInputPort> OpenAsync(
        DeviceInfo device,
        MidiPortOptions? options = null,
        CancellationToken ct = default);
}
```

### `MidiOutputPort` — the Layer 1 output I/O primitive

```csharp
public sealed class MidiOutputPort : IAsyncDisposable
{
    public DeviceInfo DeviceInfo { get; }
    public MidiProtocol Protocol { get; }

    // MIDI 1.0 output
    public Task SendAsync(MidiMessage message, CancellationToken ct = default);
    public Task SendSysExAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default);

    // MIDI 2.0 output
    public Task SendAsync(UniversalMidiPacket packet, CancellationToken ct = default);

    // Scheduled output — send a message at a specific time relative to port-open epoch.
    // scheduledAt uses the same Stopwatch-relative TimeSpan origin as MidiMessage.Timestamp,
    // so recorded input timestamps can be fed directly back as output dispatch times.
    public Task SendAsync(MidiMessage message, TimeSpan scheduledAt, CancellationToken ct = default);

    // Batch scheduled output — enqueue an ordered sequence for dispatch.
    // Entries must be sorted by ScheduledAt ascending; the implementation does not sort.
    public Task SendSequenceAsync(
        IEnumerable<(TimeSpan ScheduledAt, MidiMessage Message)> sequence,
        CancellationToken ct = default);

    public static Task<MidiOutputPort> OpenAsync(
        DeviceInfo device,
        MidiPortOptions? options = null,
        CancellationToken ct = default);
}

public sealed class MidiPortOptions
{
    /// Requested protocol. Actual protocol may be lower if not supported.
    public MidiProtocol PreferredProtocol { get; init; } = MidiProtocol.Midi1;

    /// Input message channel capacity before oldest messages are dropped.
    /// Default: 512. Increase for high-density clock / SysEx scenarios.
    public int BufferCapacity { get; init; } = 512;
}

public enum MidiProtocol { Midi1, Midi2 }
```

### Scheduled output

Three of the four platform backends expose **native scheduled dispatch** — the OS
acceptsa future timestamp and fires the message at the right time independently of any
managed thread activity. The fourth (WinMM) has no such capability and requires a
software scheduler.

| Backend | Scheduled dispatch mechanism | Notes |
|---|---|---|
| Windows MIDI 2.0 | `MidiOutputEndpointConnection` accepts future `MidiMessage` timestamps | OS-managed; sub-100 µs precision on Win11 22H2+ |
| ALSA seq | `SND_SEQ_TIME_STAMP_REAL` + `SND_SEQ_TIME_MODE_ABS` on `snd_seq_event_t` | ALSA kernel scheduler fires at the requested `CLOCK_MONOTONIC` time |
| CoreMIDI | Future `MIDITimeStamp` in `MIDIPacketList` passed to `MIDISend` | OS dispatches at the requested `mach_absolute_time`; common for software synthesizers |
| WinMM | **No native support** — software scheduler required | See WinMM software scheduler below |

For Windows MIDI 2.0, ALSA, and CoreMIDI, `SendAsync(message, scheduledAt)` translates
the `TimeSpan` epoch offset back to the platform timestamp domain and passes it directly
to the OS. The GC is irrelevant for dispatch on these backends: the OS owns the clock.

#### WinMM software scheduler

WinMM's `midiOutShortMsg` is fire-and-forget with no scheduling argument. Achieving
precise timed output on WinMM requires a **dedicated GC-free scheduler thread** using
the high-resolution waitable timer introduced in Windows 10 version 2004
(build 19041).

The output side mirrors the input ring buffer with an inverted pattern:

```
MidiOutputPort.SendAsync(message, scheduledAt)
    │
    ├─ Translate TimeSpan → absolute Stopwatch ticks (DispatchTicks)
    ├─ Pack into MidiScheduledEntry (blittable)
    └─ Enqueue into OutputRingBuffer (pinned, sorted by DispatchTicks)

    ┌─ GC-FREE ZONE ────────────────────────────────────────────────────────────┐
    │  Scheduler thread (THREAD_PRIORITY_TIME_CRITICAL)                          │
    │    while (running):                                                        │
    │      peek head entry → DispatchTicks - QPC.Now                            │
    │        > ~500 µs  → CREATE_WAITABLE_TIMER_HIGH_RESOLUTION sleep           │
    │        50–500 µs  → Thread.SpinWait (busy-wait, avoids timer overhead)    │
    │        ≤ 0        → midiOutShortMsg(hOut, packed3bytes) — fire!           │
    └───────────────────────────────────────────────────────────────────────────┘

MidiOutputPort.DisposeAsync()
    └─ Signal scheduler thread → join
           → GCHandle.Free (unpin output ring buffer)
```

The blittable entry struct that crosses the GC boundary:

```csharp
[StructLayout(LayoutKind.Sequential)]
internal struct MidiScheduledEntry
{
    public byte  Status;         // MIDI status byte
    public byte  Data1;          // First data byte (0 if not applicable)
    public byte  Data2;          // Second data byte (0 if not applicable)
    public byte  Pad;            // Alignment padding
    public long  DispatchTicks;  // Stopwatch.GetTimestamp() absolute target tick
}
```

The `DispatchTicks` field uses the same `Stopwatch.GetTimestamp()` epoch as
`MidiMessage.Timestamp` on input, which means a recorded input timestamp can be
passed directly to `SendAsync` as a `scheduledAt` value with no conversion —
essential for MIDI playback and loop-back scenarios.

`CREATE_WAITABLE_TIMER_HIGH_RESOLUTION` (flag `0x00000002`) is available from
Windows 10 2004 (build 19041) onwards. On earlier Windows the scheduler falls back to
`CreateWaitableTimer` (no high-resolution flag), which degrades timer accuracy to the
multimedia timer period (~1 ms by default, ~0.5 ms with `timeBeginPeriod(1)`).
See NEG-008.

### `MidiMessage` — MIDI 1.0 value type

Short messages and SysEx are represented by a single value type to avoid allocation
on every message. SysEx data is accessed as `ReadOnlyMemory<byte>` rented from the
port's internal `MemoryPool<byte>` and is valid only for the duration of the consuming
iteration step.

```csharp
public readonly struct MidiMessage
{
    // Parsed message kind — enables switch expressions without bit manipulation
    public MidiMessageKind Kind { get; }

    // MIDI channel (0-15) for channel messages; 0 for system messages
    public byte Channel { get; }

    // Raw status byte
    public byte Status { get; }

    // Data bytes for short messages (0 if not applicable)
    public byte Data1 { get; }
    public byte Data2 { get; }

    // SysEx payload — non-empty only when Kind == SysEx
    // Valid for the duration of the enclosing await foreach iteration step only
    public ReadOnlyMemory<byte> SysExData { get; }

    // Time since MidiInputPort.OpenAsync completed
    public TimeSpan Timestamp { get; }

    // Static factories for common outgoing messages
    public static MidiMessage NoteOn(byte channel, byte note, byte velocity);
    public static MidiMessage NoteOff(byte channel, byte note, byte velocity);
    public static MidiMessage ControlChange(byte channel, byte controller, byte value);
    public static MidiMessage ProgramChange(byte channel, byte program);
    public static MidiMessage PitchBend(byte channel, ushort value);
    public static MidiMessage Clock();
    public static MidiMessage Start();
    public static MidiMessage Stop();
    public static MidiMessage Continue();
    public static MidiMessage SysEx(ReadOnlySpan<byte> data);
}

public enum MidiMessageKind
{
    // Channel voice
    NoteOff, NoteOn, PolyKeyPressure, ControlChange,
    ProgramChange, ChannelPressure, PitchBend,
    // System common
    SysEx, TimeCodeQuarterFrame, SongPositionPointer,
    SongSelect, TuneRequest,
    // System real-time
    Clock, Start, Continue, Stop, ActiveSensing, SystemReset,
    // Unknown / unparseable
    Unknown,
}
```

### `UniversalMidiPacket` — MIDI 2.0 value type

```csharp
public readonly struct UniversalMidiPacket
{
    // MT field (bits 31-28 of word 0) — determines word count (1-4 words)
    public UmpMessageType MessageType { get; }

    // 1-4 32-bit words. Length is determined by MessageType.
    public ReadOnlyMemory<uint> Words { get; }

    // Time since MidiInputPort.OpenAsync completed
    public TimeSpan Timestamp { get; }

    // Attempt to read this packet as a MIDI 1.0 channel voice message (MT = 0x2)
    public bool TryAsMidi1Message(out MidiMessage message);
}

public enum UmpMessageType
{
    Utility         = 0x0,  // 1 word
    SystemAndMidi1  = 0x1,  // 1 word
    Midi1ChannelVoice = 0x2, // 1 word
    DataAndSysEx64  = 0x3,  // 2 words
    Midi2ChannelVoice = 0x4, // 2 words
    DataAndSysEx128 = 0x5,  // 4 words
    // 0x6–0xF reserved / vendor-specific
}
```

### `MidiDeviceProxy` — the Layer 2 lifecycle manager

Follows the same `DeviceTracker` composition pattern as `HidDeviceProxy` (ADR-0020)
and `UsbDeviceProxy` (ADR-0019), and is the canonical Layer 2 shape defined in
ADR-0024:

```csharp
public sealed class MidiDeviceProxy : INotifyPropertyChanged, IAsyncDisposable
{
    public MidiDeviceProxy(DeviceTracker tracker,
        MidiPortOptions? options = null);

    public bool IsConnected { get; }
    public DeviceInfo? DeviceInfo { get; }

    // Non-null while connected; null after disconnect
    public MidiInputPort? InputPort { get; }
    public MidiOutputPort? OutputPort { get; }

    // True if the enumerated DeviceInfo exposes both input and output ports.
    // When false, InputPort or OutputPort (but not both) will be non-null on connect.
    public bool IsBidirectional { get; }

    public event EventHandler<MidiInputPort>? InputPortOpened;
    public event EventHandler<MidiOutputPort>? OutputPortOpened;
    public event EventHandler? PortClosed;
    public event PropertyChangedEventHandler? PropertyChanged;
}
```

### Call-site shapes

```csharp
// Read MIDI 1.0 input from the first available MIDI device
var device = await Devices.Enumerate()
    .OfCategory(DeviceCategory.Midi)
    .FirstOrDefaultAsync();

await using var port = await MidiInputPort.OpenAsync(device);

await foreach (var msg in port.Messages.WithCancellation(cts.Token))
{
    if (msg.Kind == MidiMessageKind.NoteOn)
        Console.WriteLine($"Ch {msg.Channel}  Note {msg.Data1}  Vel {msg.Data2}  " +
                          $"t={msg.Timestamp.TotalMilliseconds:F3} ms");
}

// Send a MIDI message
await using var outPort = await MidiOutputPort.OpenAsync(device);
await outPort.SendAsync(MidiMessage.NoteOn(0, 60, 100));
await Task.Delay(500);
await outPort.SendAsync(MidiMessage.NoteOff(0, 60, 0));

// Request MIDI 2.0 where available
await using var port = await MidiInputPort.OpenAsync(device,
    new MidiPortOptions { PreferredProtocol = MidiProtocol.Midi2 });

Console.WriteLine($"Negotiated protocol: {port.Protocol}");

if (port.Protocol == MidiProtocol.Midi2)
{
    await foreach (var pkt in port.Packets.WithCancellation(cts.Token))
        ProcessUmp(pkt);
}
else
{
    await foreach (var msg in port.Messages.WithCancellation(cts.Token))
        ProcessMidi1(msg);
}

// Lifecycle-managed: reconnect-resilient
var tracker = new DeviceTracker("Arturia KeyLab",
    new DeviceProfile(f => f.OfCategory(DeviceCategory.Midi)
                             .ByManufacturer("Arturia")));

await using var handle = new MidiDeviceProxy(tracker);

handle.InputPortOpened += async (_, inputPort) =>
{
    await foreach (var msg in inputPort.Messages.WithCancellation(cts.Token))
        OnMidiMessage(msg);
};

await using var watcher = Devices.Watch().AddTrackers(tracker);
await watcher.StartAsync();
```

### Platform backends

| Platform | Backend | MIDI 1.0 | MIDI 2.0 | Notes |
|---|---|---|---|---|
| Windows (legacy) | WinMM (`winmm.dll`) | ✅ | ❌ | Always available. `midiInOpen` / `midiOutOpen`. Short messages via `MIDIINPROC` `MIM_DATA`; SysEx via `MIDIHDR` buffer chain. ~1 ms timestamp resolution. |
| Windows (modern) | `Windows.Devices.Midi2` | ✅ via UMP translation | ✅ | Win11 22H2+ only. Multi-client. `MidiSession` + `MidiInputEndpointConnection`. Detected at runtime; WinMM is fallback. WinRT TFM guard required (see ADR-0015–0018). |
| Linux | ALSA seq (`libasound.so.2`) | ✅ | ✅ kernel ≥ 6.5 | `snd_seq_open` + `snd_seq_connect_from/to`. Delivers `snd_seq_event_t` structs. Nanosecond timestamps. UMP port detection via `snd_ump_*` APIs where available. |
| macOS | CoreMIDI (`CoreMIDI.framework`) | ✅ | ✅ macOS 13+ | `MIDIClientCreate` + `MIDIInputPortCreate`. `MIDIPacketList` reassembly for SysEx. MIDI 2.0 via `MIDIInputPortCreateWithProtocol` on macOS 13+. `mach_absolute_time` timestamps converted via `mach_timebase_info`. |

### Windows dual-stack detection

```
MidiInputPort.OpenAsync(deviceInfo)
    │
    ├─ OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621) ?
    │       └─ YES → Windows.Devices.Midi2 backend
    │              Prefer MidiProtocol.Midi2; expose MIDI 1.0 via UMP translation
    │
    └─ NO  → WinMM backend
             MidiProtocol.Midi1 only
             Timestamp resolution degraded to ~1 ms — surfaced in MidiMessage.Timestamp precision
```

The Windows MIDI 2.0 backend is compiled in a separate internal assembly
(`Periphery.Midi.Windows.Midi2`) gated by `[SupportedOSPlatform("windows10.0.22621.0")]`,
following the pattern established in ADR-0016 for WinRT AOT/CCW registration. This
keeps the WinRT dependency isolated and avoids TFM pollution in the main
`Periphery.Midi` package.

### Internal architecture

The two-zone design mandated by Problem 5 splits every platform backend into an
unmanaged capture stage and a managed drain stage.

```
MidiInputPort.OpenAsync(deviceInfo, options)
    │
    ├─ Platform probe → IMidiInputBackend implementation selected
    │
    ├─ Pre-allocate MidiRingBuffer
    │       power-of-2 capacity (default: 1024 entries)
    │       GCHandle.Alloc(entries, GCHandleType.Pinned) → stable nint for callbacks
    │
    ├─ Allocate Channel<MidiMessage>(options.BufferCapacity)   (MIDI 1.0)
    │        OR Channel<UniversalMidiPacket>(options.BufferCapacity)  (MIDI 2.0)
    │
    ├─ Start managed drain thread
    │       SpinWait → MidiRingBuffer.TryRead → parse → channel.TryWrite
    │
    ├─ Backend.StartAsync(ringBuffer.PinnedPointer)
    │
    │   ┌─ GC-FREE ZONE ─────────────────────────────────────────────────────┐
    │   │  [UnmanagedCallersOnly] native callback (real-time OS thread)       │
    │   │    Windows WinMM:   MIDIINPROC  → capture ticks → ring write        │
    │   │    Windows MIDI2:   UMP callback→ capture ticks → ring write        │
    │   │    Linux ALSA:      poll thread → capture ticks → ring write        │
    │   │    macOS CoreMIDI:  MIDIReadProc→ capture ticks → ring write        │
    │   └────────────────────────────────────────────────────────────────────┘
    │                    │
    │              (ring buffer)
    │                    │
    │   ┌─ MANAGED ZONE ─────────────────────────────────────────────────────┐
    │   │  Drain thread (GC-visible; timing non-critical)                     │
    │   │    TryRead → reconstruct MidiMessage (short: inline, SysEx: pool)  │
    │   │    channel.TryWrite(message)                                        │
    │   └────────────────────────────────────────────────────────────────────┘
    │
    └─ Expose channel.Reader as IAsyncEnumerable<MidiMessage> via ReadAllAsync

MidiInputPort.DisposeAsync()
    └─ Backend.StopAsync
           → signal drain thread → join
           → complete channel
           → GCHandle.Free (unpin ring buffer)
           → release MemoryPool rentals
```

### AoT implementation requirements

The following patterns are **mandatory** across all platform backends. Any deviation
breaks `PublishAot=true` consumers and violates the constraint established above.

```csharp
// ✅ REQUIRED: source-generated P/Invoke
[LibraryImport("winmm.dll")]
internal static partial int midiInOpen(
    out nint lphMidiIn, uint uDeviceID,
    nint dwCallback, nint dwInstance, uint dwFlags);

// ✅ REQUIRED: UnmanagedCallersOnly for native callbacks
// dwInstance carries a raw nint to the GCHandle-pinned MidiRingEntry array.
// No managed object is touched — the entire body is in the GC-free zone.
[UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
private static unsafe void MidiInProc(
    nint hMidiIn, uint wMsg, nint dwInstance, nint dwParam1, nint dwParam2)
{
    if (wMsg != MIM_DATA) return;
    // Timestamp captured first — before any other work — to maximise accuracy.
    var entry = new MidiRingEntry
    {
        Status         = (byte)(dwParam1 & 0xFF),
        Data1          = (byte)((dwParam1 >> 8)  & 0xFF),
        Data2          = (byte)((dwParam1 >> 16) & 0xFF),
        TimestampTicks = Stopwatch.GetTimestamp(),
    };
    // dwInstance is a raw pointer into the pinned ring buffer array — no GC barrier.
    ((MidiRingBuffer*)dwInstance)->TryWriteUnsafe(ref entry);
}

// ✅ REQUIRED for Windows MIDI 2.0: root WinRT CCW factories (see ADR-0016)
[assembly: GeneratedWinRTExposedExternalType(typeof(MidiMessageReceivedEventArgs))]

// ❌ FORBIDDEN: DllImport with non-blittable parameters
[DllImport("winmm.dll")]  // ← marshals string params at runtime; fails under NativeAOT
internal static extern int midiInGetDevCaps(uint uDeviceID, ref MIDIINCAPS lpMidiInCaps, uint cbMidiInCaps);

// ❌ FORBIDDEN: delegate passed directly to native code as callback
var callback = new MidiInProc(OnMidiIn);  // ← JIT generates stub; fails under NativeAOT
midiInOpen(out handle, deviceId, Marshal.GetFunctionPointerForDelegate(callback), ...);
```

The `Periphery.Midi` CI pipeline must include at least one `PublishAot=true` publish
step as a gate. A library that compiles under JIT but fails under AoT publication is a
regression, and that regression is silent without an AoT publish in the test matrix.

---

## Relationship to ADR-0024 and ADR-0025

**ADR-0024** (Extension Package Pattern) formalised the Layer 1 / Layer 2 / Layer 3
architecture. `Periphery.Midi` maps to it as follows:

- **Layer 1:** `MidiInputPort` and `MidiOutputPort` — both follow the
  `static OpenAsync(DeviceInfo)` + `IAsyncDisposable` shape. The dual-port model is a
  domain-valid extension of the single-port shape: MIDI ports are inherently directional.
- **Layer 2:** `MidiDeviceProxy` — composes `DeviceTracker` via `StateChanged`,
  implements `INotifyPropertyChanged`, `IAsyncDisposable`.
- **Layer 3:** No enricher required. `MidiPortDirection` is already a typed `init`
  property on `DeviceInfo` (ADR-0021), populated during enumeration by the core
  Windows/Linux/macOS providers. The ADR-0024 promotion rule (scalar enumeration-time
  value → typed `init` property on `DeviceInfo`) was applied when ADR-0021 was
  corrected.

The two-zone GC architecture (unmanaged capture zone → `MidiRingBuffer` → managed
drain zone) and the AoT + `[UnmanagedCallersOnly]` requirements in this ADR were
identified as patterns applicable to any timing-critical extension package, and are
captured in ADR-0024 §3d.

**ADR-0025** (Extensible `DeviceCategory`) is not directly exercised by `Periphery.Midi`
in v1 — `DeviceCategory.Midi` already exists in the core library. If multi-port
devices warrant finer-grained categories (e.g. `DeviceCategory.MidiInterface`,
`DeviceCategory.MidiSynthesizer`) in a future revision, they are registered via
`[ModuleInitializer]` + `DeviceCategoryRegistry` without core library changes.

---

## Consequences

### Positive

- **POS-001**: MIDI 1.0 works on every supported OS version with no prerequisites.
  WinMM, ALSA, and CoreMIDI are inbox APIs available on all target platforms.
- **POS-002**: MIDI 2.0 support is additive. Callers on capable platforms get higher
  resolution and richer messages; callers on older platforms get the same MIDI 1.0
  API without any code changes.
- **POS-003**: `IAsyncEnumerable<MidiMessage>` is idiomatic for the Periphery async-first
  convention and integrates with `await foreach`, `System.Linq.Async`, and `Channel<T>`
  backpressure naturally.
- **POS-004**: The port-oriented API honestly models how MIDI devices are enumerated by
  the OS. A multi-port interface has its ports independently addressable, matching
  the `DeviceInfo` entries that `DeviceCategory.Midi` enumeration produces.
- **POS-005**: `MidiMessage` as a value type with static factory methods eliminates
  allocation on the hot path (note streams at 500+ events/second are common in live
  performance).
- **POS-006**: The `MidiDeviceProxy` pattern reuses `DeviceTracker` composition, giving
  reconnect resilience and profile-ordered device resolution with no new infrastructure.
- **POS-007**: The AoT-first implementation strategy makes `Periphery.Midi` usable in
  fully native-compiled applications — game engines, embedded controllers, live
  performance software, and DAW hosts — without JIT warmup jitter on the callback path.
  Consumers who publish with `PublishAot=true` get deterministic callback latency from
  first invocation.
- **POS-008**: The GC-free / managed two-zone architecture correctly separates timing
  accuracy from delivery latency. Timestamps are captured in the GC-free zone before
  any managed code runs, so they accurately reflect the hardware event time regardless
  of subsequent GC pauses on the drain thread. A consumer paused mid-GC-collection
  sees messages delivered slightly late but timestamped correctly — which is the right
  tradeoff for every non-hard-real-time use case: recording, sequencing, and DAW
  integration.

### Negative

- **NEG-001**: **WinMM timestamp resolution is ~1 ms.** There is no user-space workaround
  on older Windows. Callers who need sub-millisecond timestamp accuracy on Windows must
  run on Win11 22H2+ to get the Windows MIDI 2.0 backend. This limitation should be
  surfaced in documentation and optionally in a property on `MidiInputPort`
  (`TimestampResolution: TimeSpan`).
- **NEG-002**: **WinMM is single-client.** Only one process can open a given WinMM
  device at a time. `MidiInputPort.OpenAsync` will throw `MidiPortException` if another
  process holds the port open. Windows MIDI 2.0 is multi-client and does not have this
  restriction.
- **NEG-003**: **SysEx memory lifetime is restricted.** `MidiMessage.SysExData` is a
  pool-rented `ReadOnlyMemory<byte>` valid only for one iteration step. Callers who
  retain a reference beyond the `await foreach` body and access the memory later will
  observe corrupt or recycled data. The contract must be documented clearly with a
  warning in XML doc comments and an `InvalidOperationException` guard if feasible.
- **NEG-004**: **macOS CoreMIDI routing graph is not exposed.** CoreMIDI's virtual ports,
  system-wide routing, and network MIDI capabilities are not surfaced in v1. Callers who
  need to create virtual ports or interconnect applications via the CoreMIDI graph must
  use P/Invoke directly.
- **NEG-005**: **MIDI-CI (capability inquiry) for MIDI 2.0 negotiation is out of scope.**
  MIDI-CI is the bidirectional protocol by which MIDI 2.0 devices negotiate capabilities.
  The `PreferredProtocol` option in `MidiPortOptions` bypasses MIDI-CI and relies on the
  OS driver performing negotiation transparently (Windows MIDI 2.0 and macOS 13+
  do this). A future ADR should address explicit MIDI-CI support if the use case arises.
- **NEG-006**: **ALSA `libasound` is a P/Invoke dependency.** Unlike the WinMM and
  CoreMIDI backends, which use OS-resident DLLs that need no deployment steps, the
  Linux backend requires `libasound.so.2` to be present. On modern distributions this
  is universally available, but it is a runtime dependency that must be documented.
- **NEG-007**: **Windows MIDI 2.0 AoT CCW registration adds build-time complexity.**
  The `[GeneratedWinRTExposedExternalType]` attributes required to root WinRT CCW
  factories under NativeAOT trimming (documented in ADR-0016) must be maintained
  in the `Periphery.Midi.Windows.Midi2` assembly for every .NET type that crosses
  the WinRT ABI boundary. Adding a new `Windows.Devices.Midi2` API call without the
  corresponding attribute registration produces a silent failure at runtime under AoT
  that does not manifest in JIT builds. The `PublishAot=true` CI gate (see AoT
  implementation requirements above) is the primary safeguard against this class of
  regression.
- **NEG-008**: **WinMM scheduled output requires Windows 10 2004+ (build 19041).**
  `CREATE_WAITABLE_TIMER_HIGH_RESOLUTION` — the mechanism used by the WinMM software
  scheduler to achieve sub-millisecond sleep precision — was introduced in Windows 10
  version 2004 (build 19041, May 2020). On earlier Windows (unlikely in practice but
  possible in enterprise environments), the scheduler silently falls back to the
  standard `CreateWaitableTimer`, which is subject to the system timer period and
  cannot achieve better than ~1 ms wake precision. Callers who need deterministic
  sub-millisecond scheduled output on Windows must either target Win10 2004+ or
  upgrade to the Windows MIDI 2.0 backend (Win11 22H2+), which uses the OS scheduler.

---

## Alternatives Considered

### A — Target MIDI 2.0 only

Rejected. MIDI 2.0 hardware is not ubiquitous. A MIDI 2.0-only library would be
unusable with the majority of deployed MIDI controllers, synthesizers, and DAW
interfaces as of 2026. The MIDI Association's own position is that MIDI 2.0 devices
must remain compatible with MIDI 1.0 hosts, confirming that MIDI 1.0 remains the
interoperability baseline for the foreseeable future.

### B — Use a raw byte stream API (`IAsyncEnumerable<byte>`)

Considered for simplicity. Rejected because it forces every caller to implement the
MIDI 1.0 parser, including running-status resolution, SysEx framing, and real-time
message interleaving. These are non-trivial with real edge cases. Providing a parsed
`MidiMessage` value type is a better tradeoff: the parsing complexity is contained
once in the library, and callers work with structured values.

### C — Use ALSA rawmidi instead of ALSA seq on Linux

rawmidi (`/dev/snd/midiCxDy`) is simpler to open but delivers a raw byte stream with
no timestamps and no running-status resolution. This would require a full MIDI parser
in user space (see alternative B). ALSA seq delivers pre-parsed `snd_seq_event_t`
structs with nanosecond timestamps and handles multi-client access natively. Seq is the
correct interface for a library targeting correctness and cross-platform timestamp
coherence.

### D — Wrap an existing .NET MIDI library (`NAudio`, `RtMidi.Net`, `managed-midi`)

Considered as a way to avoid re-implementing MIDI parsing and platform backends.
Rejected for two reasons: (1) all three are MIDI 1.0-only and have no roadmap for
MIDI 2.0 / UMP; (2) they introduce third-party runtime dependencies, violating the
zero-dependency constraint established in ARCHITECTURE.md §1. `Periphery.Midi` must
be self-contained.

### E — Single `MidiPort` type for both input and output

A combined `MidiPort` that exposes both `Messages` (input) and `SendAsync` (output)
was considered for simplicity. Rejected because MIDI ports are fundamentally
directional: the OS enumerates input and output endpoints separately, and many physical
devices have only input or only output ports. A combined type would have null-ish
properties for the inactive direction and would misrepresent the underlying model.
`MidiDeviceProxy.IsBidirectional` provides the convenience signal for devices that
expose both.

### F — Route native callbacks directly into `Channel<T>` (no ring buffer)

The naive architecture — `[UnmanagedCallersOnly]` callback writes directly to
`Channel<T>` on the OS callback thread — appears to satisfy the AoT constraint
(no JIT, no delegate stubs) while being far simpler to implement. It was the original
architecture in this ADR before Problem 5 was identified.

Rejected for two reasons:

1. **GC pause corrupts timestamps.** `Channel<T>` is a managed object. Writing to it
   from an `[UnmanagedCallersOnly]` method requires transitioning into managed context,
   which re-exposes the thread to the GC. If a GC pause occurs between the OS firing
   the callback and the library capturing `Stopwatch.GetTimestamp()`, the timestamp
   is wrong. This is not hypothetical — Gen0 collections fire frequently under any
   realistic load, and a 1–2 ms pause at the wrong moment is indistinguishable from
   hardware jitter in a recorded MIDI stream.

2. **`[UnmanagedCallersOnly]` and managed heap access.** Calling managed code (including
   `Channel<T>.TryWrite`) from an `[UnmanagedCallersOnly]` method requires an explicit
   managed-to-unmanaged transition boundary. Under NativeAOT this is possible via
   `[UnmanagedCallersOnly]` with `GCUnmanagedToManagedTransition`, but it re-exposes
   the thread to GC suspension for the duration of the managed call — exactly the
   window we need to keep GC-free.

The ring buffer solves both problems by keeping the timestamp capture and the write
entirely within blittable, pinned, GC-invisible memory. The managed `Channel<T>` write
happens later, on the drain thread, where timing is irrelevant.

---

## Open Questions

- **OQ-001**: Should `MidiInputPort` expose a `TimestampResolution` property reflecting
  the actual precision of `MidiMessage.Timestamp` on the current platform/backend? This
  would allow callers to decide programmatically whether the timestamp precision is
  sufficient for their use case (e.g. reject WinMM's ~1 ms on a latency-sensitive path).

- **OQ-002**: Should SysEx memory be caller-owned (copied into caller-provided
  `Memory<byte>`) rather than pool-rented? A copying model eliminates the lifetime hazard
  in NEG-003 at the cost of allocation on every SysEx message. For most use cases
  (patch dumps, occasional SysEx) the allocation cost is negligible; for high-frequency
  SysEx the pool-rent model is worth the complexity. The right default needs validation
  against real-world SysEx patterns.

- **OQ-003**: Should `MidiOutputPort` expose `SendBatchAsync(IEnumerable<MidiMessage>)`
  for sending a burst of messages with minimal round-trip overhead? WinMM and ALSA seq
  both support buffered output; batching could improve throughput for sequencer playback
  scenarios.

- **OQ-004**: Should `Periphery.Midi` expose virtual port creation (`CreateVirtualInputPort`,
  `CreateVirtualOutputPort`) in v1? CoreMIDI and ALSA seq both support virtual ports
  natively. Virtual ports are essential for software synthesizers and inter-application
  MIDI routing. WinMM does not support them, but Windows MIDI 2.0 does. Platform parity
  is achievable for Windows and Linux/macOS but not for older Windows.

- **OQ-005**: Should MIDI clock synchronisation utilities (tempo tracking, beat detection
  from `MidiMessageKind.Clock` events) be included in `Periphery.Midi` or deferred to a
  higher-level `Periphery.Midi.Sync` package? The raw `Clock` messages are available
  through the standard stream; interpretation is value-add that may warrant separation.

---

## Use-Case Analysis

This section maps concrete consumer scenarios to the architectural decisions above,
calling out where the design is well-suited, where constraints apply, and what the
caller must account for.

> **Column key**
> - **Input path**: How incoming MIDI is captured.
> - **Output path**: How outgoing MIDI is dispatched.
> - **Timestamp accuracy**: Quality of `MidiMessage.Timestamp` (reflects hardware event time).
> - **Delivery latency**: Delay from hardware event to consumer `await foreach` iteration.
> - **GC impact**: Whether GC pauses can affect correctness or perceived quality.
> - **WinMM constraint**: Specific limitation when running on the WinMM backend.
> - **Verdict**: Overall suitability of the `Periphery.Midi` design for this scenario.

### Scenario table

| Use case | Input path | Output path | Timestamp accuracy | Delivery latency | GC impact | WinMM constraint | Verdict |
|---|---|---|---|---|---|---|---|
| **MIDI recording** | `MidiInputPort.Messages` via ring buffer | None | ✅ High — timestamp captured in GC-free zone before any managed code runs; accurate regardless of GC pauses on drain thread | Low to moderate — drain thread adds one ring-read cycle; bounded `Channel<T>` ensures no unbounded growth | ✅ None on timestamp correctness; GC only delays delivery, not accuracy | ⚠️ Timestamp resolution ~1 ms; sub-millisecond timing distinctions (e.g. fast ornaments at 200 BPM) are not representable | ✅ Well-suited. Timestamps are accurate within platform resolution. Document WinMM ~1 ms ceiling via `TimestampResolution` (OQ-001). |
| **Live performance / MIDI synth** | `MidiInputPort.Messages` via ring buffer | Audio engine (out-of-scope) consumes `MidiMessage` from `IAsyncEnumerable` | ✅ High — timestamp reflects true hardware event time | ⚠️ Moderate — `await foreach` on drain thread adds one `Channel<T>` round-trip; total path is: hardware → ring write → drain read → channel write → consumer await. Typical: < 1 ms. GC pause on consumer thread can add 1–5 ms stall | ⚠️ GC pause on consumer thread adds jitter to the input→audio path. Callers can mitigate with `GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency` | ⚠️ WinMM is single-client; no multi-app monitoring. Timestamp resolution ~1 ms (audible on fast passages) | ⚠️ Usable for most live scenarios. For hard-real-time audio synthesis, callers should prefer Windows MIDI 2.0 / ASIO out-of-process path and consider `SustainedLowLatency` GC mode. |
| **MIDI file / sequence playback** | None (messages read from file/memory) | `MidiOutputPort.SendSequenceAsync` or `SendAsync(msg, scheduledAt)` | N/A — no live input | ✅ Low and consistent on ALSA/CoreMIDI/MIDI 2.0 (OS scheduler); ⚠️ WinMM uses `CREATE_WAITABLE_TIMER_HIGH_RESOLUTION` software scheduler on GC-free thread — good precision on Win10 2004+ | ✅ GC-free scheduler thread on WinMM; OS scheduler on other platforms — GC cannot perturb dispatch timing | ⚠️ Win10 2004+ required for high-resolution timer. Falls back to ~1 ms timer period on older Windows | ✅ Well-suited. `DispatchTicks` uses the same epoch as `MidiMessage.Timestamp`, enabling direct replay of recorded sequences. `SendSequenceAsync` batches the entire sequence in one call. |
| **MIDI routing / real-time processing** | `MidiInputPort.Messages` | `MidiOutputPort.SendAsync` (immediate) | ✅ Timestamps preserved across the transform; output fired immediately without rescheduling | ⚠️ Combined latency: input ring-drain + consumer processing + output `midiOutShortMsg`. Total typically 1–3 ms. | ⚠️ GC pause on the routing loop thread adds jitter to the through-put path. For deterministic routing, run the loop in `SustainedLowLatency` GC mode. | ⚠️ WinMM single-client blocks a second process from opening the same device simultaneously; virtual port routing is not available on WinMM (see OQ-004). | ⚠️ Functional for most routing. Not a replacement for kernel-mode virtual MIDI cables. `IsBidirectional` on `MidiDeviceProxy` simplifies open/close for combined I/O devices. |
| **Controller mapping / remapping** | `MidiInputPort.Messages` — intercept CC/note events | `MidiOutputPort.SendAsync` to a different port | ✅ Timestamps carried through; remapped messages can be sent with original timestamp preserved as metadata | Low — single transform step; no scheduling overhead | Same as MIDI routing above | WinMM single-client: cannot read from a port already held by another application (e.g. a DAW). Upgrade to MIDI 2.0 (multi-client) resolves this. | ✅ Well-suited for standalone controller mapping tools. Multi-client limitation on WinMM is the primary constraint in practice. |
| **DAW / sequencer host bridge** | `MidiInputPort.Messages` or `MidiInputPort.Packets` (MIDI 2.0) | `MidiOutputPort.SendAsync` + `SendSequenceAsync` for track playback | ✅ Timestamps accurate; `TimeSpan` epoch aligns with any `Stopwatch`-based DAW transport clock | ⚠️ Managed `IAsyncEnumerable` delivery is not hard-real-time; final audio rendering must happen downstream of `Periphery.Midi` | ⚠️ DAW hosts typically run with `GCLatencyMode.SustainedLowLatency`; `Periphery.Midi` is compatible with this mode | ⚠️ WinMM single-client prevents opening a port the DAW host already holds; Windows MIDI 2.0 multi-client is the correct target for DAW integration on Windows | ⚠️ Suitable as an interop bridge layer. Not intended to replace the native MIDI stack inside a DAW engine. AoT compatibility (POS-007) makes it viable in native DAW plugin hosts. |
| **IoT / embedded MIDI controller** | `MidiInputPort.Messages` for command input | `MidiOutputPort.SendAsync` for feedback / response | ✅ High accuracy; ring buffer architecture is lightweight enough for constrained environments | Low — bounded ring + `Channel<T>` adds minimal memory overhead | ✅ NativeAOT eliminates JIT warmup; GC-free callback zone means the first message is timestamped as accurately as the hundredth | WinMM is unlikely on IoT targets; ALSA seq is the typical backend on Linux-based embedded systems | ✅ Well-suited. NativeAOT + `[UnmanagedCallersOnly]` design was motivated partly by this class of consumer. |
| **MIDI 2.0 device testing / conformance** | `MidiInputPort.Packets` (`IAsyncEnumerable<UniversalMidiPacket>`) | `MidiOutputPort.SendAsync(UniversalMidiPacket)` | ✅ 100 ns resolution on Windows MIDI 2.0 and CoreMIDI; nanosecond on ALSA | Low | ✅ MIDI 2.0 backends (Windows MIDI 2.0, ALSA UMP, CoreMIDI 2.0) do not use the WinMM ring buffer path at all; GC exposure is limited to drain thread | WinMM does not support MIDI 2.0; UMP is unavailable on WinMM backend | ✅ Well-suited on supported platforms. `port.Protocol` reports negotiated protocol; callers can assert MIDI 2.0 negotiation succeeded before running conformance tests. |

### Cross-cutting constraints summary

The following constraints apply across **all** use cases and should be communicated
prominently in `Periphery.Midi` documentation:

1. **WinMM timestamp resolution is ~1 ms** — not fixable in user space. Callers needing
   sub-millisecond timestamps on Windows must run on Win11 22H2+ (Windows MIDI 2.0
   backend). The `TimestampResolution` property (OQ-001) provides a programmatic signal.

2. **WinMM is single-client** — only one process may hold a WinMM port open at a time.
   Windows MIDI 2.0 is multi-client. Applications that must co-exist with a DAW or
   virtual MIDI router should prefer or require the MIDI 2.0 backend.

3. **SysEx memory lifetime is restricted** — `MidiMessage.SysExData` is pool-rented and
   valid only for the current `await foreach` iteration step. Copy if retention is needed.

4. **Delivery latency ≠ timestamp inaccuracy** — GC pauses delay delivery (the consumer
   sees the message later) but do not corrupt the recorded timestamp (captured in the
   GC-free zone). This distinction is critical for recording use cases: messages arrive
   with correct timestamps even if they arrive in a burst after a GC pause.

5. **Scheduled output on WinMM requires Win10 2004+** (build 19041) for
   `CREATE_WAITABLE_TIMER_HIGH_RESOLUTION`. On earlier Windows the software scheduler
   falls back to ~1 ms timer granularity (NEG-008).

6. **`Periphery.Midi` is not hard-real-time** — the managed `IAsyncEnumerable<T>`
   delivery boundary means jitter is possible on the consumer side under GC pressure.
   Hard-real-time audio synthesis (sub-ms, bounded jitter guarantees) requires a
   kernel-mode or native audio plugin architecture that is out of scope for a managed
   library. `Periphery.Midi` targets the 99% of use cases where < 5 ms delivery jitter
   is acceptable and timestamp accuracy is the primary correctness requirement.
