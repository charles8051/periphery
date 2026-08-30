---
title: "ADR-0052: Pure-core structure for Periphery.Treehopper — separating state, IO, and timing"
status: "Accepted"
status_note: "Shipped, and now this repository's reference pattern for the functional-core / imperative-shell split."
date: "2026-06-02"
authors: "@charles8051"
tags: ["architecture", "decision", "treehopper", "usb", "io", "immutability", "functional-core", "composition", "testing"]
supersedes: ""
superseded_by: ""
---

# ADR-0052: Pure-core structure for Periphery.Treehopper — separating state, IO, and timing

## Status

> **Complements — does not supersede — [ADR-0039](0039-periphery-treehopper.md).**
> ADR-0039 decides *that* we rebuild Treehopper on Periphery.Usb and fixes the
> outer **shell** (async-only, no setter-IO, pin/peripheral leases,
> `IAsyncEnumerable<PinUpdate>`, `DeviceSessionHost` reconnect). This ADR
> decides *how the layer underneath that shell is structured* — the part
> ADR-0039 leaves as "internals" — and pins down the v2 `Treehopper.Libraries`
> shape before anyone writes it. Nothing here reopens an ADR-0039 decision;
> it's all additive internal structure plus a constraint on v2.

## Context

ADR-0039 audits the original [treehopper-sdk](https://github.com/treehopper-electronics/treehopper-sdk)
and groups its problems into "async hygiene", "type/lifecycle", and "USB
layer". ADR-0039's shell decisions kill the async-hygiene bucket cleanly. But
reading the actual source (`NET/API/Treehopper/`), the audit's deepest and
most repeated failure is narrower and more specific than "async hygiene":

> **State, IO, and timing are fused into the same objects.** A value you
> set is also a USB transfer is also a blocking wait. There is no layer that
> is *just* state, and no layer that is *just* the wire.

Three load-bearing examples, first-hand from the current SDK:

**1. A pin's mode is a cached field, a USB transfer, and a thread-blocking
wait — all in one setter** (`Pin.cs`, `Mode` setter):

```csharp
set {
    _mode = value;                              // (a) mutate cached state
    switch (_mode) {
        case PinMode.AnalogInput:
            if (TreehopperUsb.Settings.PropertyWritesReturnImmediately)
                MakeAnalogInAsync().Forget();           // (b) fire-and-forget USB IO, errors swallowed
            else
                Task.Run(MakeAnalogInAsync).Wait();     // (b') ...or sync-over-async deadlock hazard
            break;
        // ...
    }
}
```

`_mode` is mutated here *and again* inside `MakeAnalogInAsync()`. The global
`Settings.PropertyWritesReturnImmediately` flag picks which of two wrong
behaviours you get. There is no way to express "this is the desired mode" as a
value without immediately committing it to hardware on the calling thread.

**2. Pin reads are unsynchronised mutable fields written from the USB thread,
and "await a change" races on a replaced `TaskCompletionSource`** (`Pin.cs`):

```csharp
private TaskCompletionSource<int> _adcValueSignal = new();      // four of these per pin
private int _adcValue;                                          // mutated from the USB callback thread

public Task<int> AwaitAdcValueChangeAsync() {
    _adcValueSignal = new TaskCompletionSource<int>();          // REPLACES the field every call
    return _adcValueSignal.Task;                                // → two waiters race, the first orphans
}

internal virtual void UpdateValue(byte highByte, byte lowByte) {// called on the USB callback thread
    if (Mode == PinMode.DigitalInput) { _digitalValue = highByte > 0; /* raise events */ }
    else if (Mode == PinMode.AnalogInput) { _adcValue = (highByte << 8) | lowByte; /* raise events */ }
}
```

`UpdateValue` reads `Mode` (which the app thread can be mutating per example 1)
and writes `_adcValue`/`_digitalValue` with no lock, while `AdcValue`/
`DigitalValue` getters read them on the app thread. The board's pin-report
handler (`TreehopperUsb.cs`) does the same at the board level:

```csharp
private void Connection_PinEventDataReceived(byte[] pinStateBuffer) {
    _pinUpdateReportReceived.TrySetResult(false);              // shared TCS, replaced elsewhere → race
    var i = 1;
    foreach (var pin in Pins)
        pin.UpdateValue(pinStateBuffer[i++], pinStateBuffer[i++]);  // decode + mutate, fused, on USB thread
}
```

The *decode* of the wire buffer and the *mutation* of cached pin state are the
same loop, on the USB thread.

**3. The APA102 LED driver reads mutable colour fields *while* serialising the
frame, and writes them under `.Wait()`** (`Treehopper.Libraries/Displays/Apa102.cs`):

```csharp
public class Led { internal float red, green, blue; /* ... */
    public void SetRgb(float red, float green, float blue) {
        this.red = red; this.green = green; this.blue = blue;   // mutate shared fields
        if (driver.AutoFlush) driver.FlushAsync().Wait();       // sync-over-async
    }
}
public async Task FlushAsync(bool force = false) {
    foreach (var led in Leds) {
        bytes.Add(...(led.blue / 255.0)...);                    // read mutable fields to build the frame...
        bytes.Add(...(led.green / 255.0)...);
        bytes.Add(...(led.red / 255.0)...);
    }
    await spi.SendReceiveAsync(chunk, ...);                     // ...then await the SPI transfer
}
```

If any thread calls `SetRgb` while `FlushAsync` is mid-serialisation, the frame
tears — there is no snapshot. This is the **exact** problem the
The kiosk consumer's LED subsystem already solved (and shipped) with an immutable
frame + a pure `Next()` + a separate flusher (`LedAnimation` / `LedStripEngine`
/ `SequenceAnimation`) — state without a clock, separating calculation from timing.

### Why Periphery is the right host for the fix

Periphery already leans the right way, so this is "follow the grain", not "import a foreign idea":

- **Immutable snapshots** — `DeviceInfo` is a `sealed record`, never mutated; changes are diffed, not patched.
- **Channel-backed streams** — `CameraSession` exposes `IAsyncEnumerable<LeasedCameraFrame>` off a bounded channel, decoupling producer from consumer. The exact shape a pin-report stream wants.
- **Closed-union state** — ADR-0031's `DeviceResolution` (`NoMatch` / `UniqueMatch` / `AmbiguousMatch` / `MatchedButDisconnected`) is already the pattern-match-friendly union style this ADR uses for commands and reports.
- **Reconcile-style lifecycle** — `DeviceProxyBase`/`DeviceSessionHost` already model "drive actual toward desired" for device *presence*; we extend the same instinct to device *configuration*.
- **No-baggage stance** — Periphery has no external consumers and prefers "make it right" over "make it gradual." ADR-0038/0039 are both still *Proposed* and `src/Periphery.Usb/` does not exist yet, so **this costs nothing to adopt now and would be expensive to retrofit later.**

## Decision

Structure Periphery.Treehopper as three layers with a hard rule at each
boundary: **the core is values and total functions; the shell owns IO, the
clock, and cancellation; composition happens over streams.** This is
"functional core, imperative shell" applied to a USB protocol.

```
┌─ Pure core ───────────────────────────────────────────────┐
│  Command / BoardReport / BoardConfig  (immutable, closed)  │   no IO, no clock,
│  TreehopperWire.Encode / .Decode / Plan   (total fns)      │   no threads, no Task
└────────────────────────────────────────────────────────────┘
            ▲ bytes / values                       │ bytes / values
┌─ Imperative shell ─────────────────────────────────────────┐
│  IUsbBulkChannel (Periphery.Usb) · the reconcile loop ·     │   owns IO, clock,
│  the pin-report producer · the LED flusher · leases         │   cancellation, leases
└────────────────────────────────────────────────────────────┘
            ▲ IAsyncEnumerable<BoardReport> / Command[]
┌─ Composition ──────────────────────────────────────────────┐
│  device drivers as pure transforms over the two streams     │   substrate-friendly
└────────────────────────────────────────────────────────────┘
```

### DEC-001 — The wire protocol is a pure, total codec over closed-union values

ADR-0039 already isolates a `TreehopperWireProtocol` "testable against a fake
`IUsbBulkChannel`." Sharpen that into a **pure, total** function pair over
closed-union values — no channel reference, no `Task`, no clock, no mutable
buffers retained:

```csharp
public abstract record Command {                         // closed union → exhaustive Encode
    public sealed record ConfigurePin(byte Pin, PinMode Mode, AdcReferenceLevel Vref) : Command;
    public sealed record WriteDigital(byte Pin, bool High) : Command;
    public sealed record ConfigureI2c(int SpeedKhz) : Command;
    public sealed record I2cTransaction(byte Address, ReadOnlyMemory<byte> Tx, int ReadLen) : Command;
    public sealed record ConfigureSpi(SpiMode Mode, double SpeedMhz) : Command;
    public sealed record SpiTransaction(ReadOnlyMemory<byte> Tx, byte ChipSelect, SpiBurst Burst) : Command;
    public sealed record SetLed(bool On) : Command;
    public sealed record Reboot() : Command;
    // ... one variant per DeviceCommands byte (17 total)
    private Command();
}

internal static class TreehopperWire {
    // value → bytes. The transducer's δ. Pure: same input, same bytes, no side effects.
    public static int Encode(in Command cmd, Span<byte> dst);
    // bytes → immutable snapshot. Pure: a Moore-machine output decode.
    public static BoardReport DecodeReport(ReadOnlySpan<byte> pinStateBuffer, long sequence);
}
```

The `DeviceCommands` byte values and packet shapes are preserved verbatim
(ADR-0039 constraint). The only thing that touches hardware is the
`IUsbBulkChannel` from Periphery.Usb — it is the **interpreter** that ships the
bytes `Encode` produced and feeds bytes to `Decode`. The codec never sees it.

*Payoff:* the entire protocol is **round-trip property-testable with zero
hardware** (`Decode(EncodeAndLoopback(cmd)) == expected`), which is exactly the
gap `docs/patterns/wire-level-testing.md` calls out as unreachable by lifecycle
testing. Today the encode logic is scattered across `Pin.SendCommandAsync`
(builds a 6-byte packet inline), `HardwareSpi`, `HardwareI2c`, and
`TreehopperUsb`; centralising it as a pure function makes it the single,
testable point of firmware coupling.

### DEC-002 — Board observable state is an immutable snapshot stream, not cached mutable fields

Model everything the board *reports* as one immutable value, streamed
(channel-backed, exactly like `CameraSession`). Do **not** keep mutable
`_digitalValue` / `_adcValue` fields that the USB thread and the app thread
both touch.

```csharp
public sealed record BoardReport(long Sequence, ImmutableArray<PinSnapshot> Pins);
public readonly record struct PinSnapshot(PinMode Mode, bool Digital, int Adc);

public sealed class TreehopperBoard {
    public IAsyncEnumerable<BoardReport> Reports { get; }   // bounded channel, one producer
    // ADR-0039's PinUpdates stream is a projection of this:
    public IAsyncEnumerable<PinUpdate> PinUpdates => Reports.SelectMany(Diff);
}
```

- "Current pin value" becomes a **projection of the latest `BoardReport`**, never a field two threads race on. Dissolves the unguarded `UpdateValue` read/write race.
- "Await a change" becomes `await board.Reports.FirstAsync(r => r.Pins[7].Digital, ct)` — a normal stream operation. Dissolves the four `TaskCompletionSource`-replacement races (`AwaitDigitalValueChangeAsync` et al.) and the board-level `_pinUpdateReportReceived` race **by construction** — there is no shared mutable TCS to replace.
- The producer's job shrinks to: read bytes off the bulk-IN endpoint → `TreehopperWire.DecodeReport(...)` (pure) → publish to the channel. Decode and IO are no longer the same loop.

This is the coalgebra/`unfold` view: the board is an `S → (BoardReport, S)` stream; consumers observe it; the producer is the only writer.

### DEC-003 — Desired configuration is an immutable value; reconciliation is a pure planner + an effectful applier

This is the piece ADR-0039 leaves implicit. Separate *what the board should be*
(a value) from *how/when we get it there* (the interpreter):

```csharp
public sealed record BoardConfig(
    ImmutableDictionary<byte, PinConfig> Pins,
    PeripheralSet Peripherals);

// PURE: diff desired against the last report, emit the minimal command list. No IO.
internal static IReadOnlyList<Command> Plan(BoardConfig desired, BoardReport actual);

// EFFECTFUL: the interpreter. Encodes each planned command and ships it.
public async Task ReconcileAsync(BoardConfig desired, CancellationToken ct);
```

Two compounding wins:

- **Reconnect is free and uniform.** On reconnect, reconcile `desired` against a *blank* actual state → re-applies the full configuration with **no bespoke re-init code path**. This is precisely the renderer-owned `TreehopperLedStripRenderer.ReconcileAsync` pattern the kiosk consumer already validated in production (and which the original SDK lacks entirely — once a board is gone, the instance is dead). It also mirrors what `DeviceProxyBase`/`DeviceSessionHost` already do for device *presence*, one level up.
- **A pin handle (ADR-0039's lease) becomes a thin edit to the desired-config value**, applied by the same reconcile path — not a bespoke setter that does its own IO. The lease is the imperative-shell boundary (acquire/release the hardware resource); the config inside it is a pure value.

> **Invariant — the actual/`applied` baseline is per-connection, and must reset to
> blank on every (re)connection.** The Treehopper EFM8 protocol is **open-loop for
> configuration: there is no read-back command.** The firmware's only host-bound
> messages are the pin-value report (change-driven readings) and transaction
> responses — never register/config state. So the host's `applied` mirror (peripheral
> enables, rates, pin modes, PWM duty, LED) can be *re-asserted* but never *verified*.
> `ConfigureDevice` is a genuine full firmware reset (`Treehopper_Init`: all
> peripherals disabled, all pins high-impedance), which is exactly why "reconcile
> `desired` against a *blank* actual" is correct on reconnect — **provided the actual
> baseline is reset to blank.** The failure mode this rules out: preserving a non-null
> `applied` across a connection boundary (to "avoid re-sending everything") deltas
> against a board that silently reset to blank, skipping commands the metal lost —
> the classic "host thinks I²C is on, the metal reset it off" divergence. Rule:
> `desired` config may be durable across reconnects; `applied` (belief about the
> metal) may not. The only safe resync is `ConfigureDevice` + full re-apply
> (`TreehopperBoard.ResyncAsync` exposes this for the rare live-divergence case).
> The proper long-term fix is a firmware patch adding a config read-back so the host
> can verify rather than assume — deferred.

### DEC-004 — Timing lives in the shell, never in core state

No core value and no codec function may `sleep`, read a clock, or own a timer.
Cadence — PWM phase, ADC poll interval, input debounce, LED flush rate — lives
in a **driver that owns the clock and ticks a pure phase forward**:

```csharp
// PURE: phase state + a total step function (no clock inside).
public readonly record struct SoftPwmPhase(double DutyCycle, double Position) {
    public SoftPwmPhase Next(double dtSeconds) => /* advance position, fold over period */;
}
// SHELL: the driver owns Task.Delay / the tick and applies frames via the channel.
```

This is `BreatheAnimation.Next()` (pure) + `LedStripEngine` (owns `Task.Delay`)
from the kiosk, generalised. It dissolves the original `SoftPwmManager` hazard
(timing tangled with a mutable pin dictionary, `Pin.Mode = SoftPwm` mutating
hidden manager state through a property-as-IO setter).

### DEC-005 — The v2 `Treehopper.Libraries` LED driver IS the immutable-frame model

ADR-0039 defers `Treehopper.Libraries` (incl. APA102) to v2 and notes it'll sit
on the new I²C/SPI surface "unchanged in spirit." **Constrain that spirit:** do
**not** reproduce the mutable `Led.red/green/blue` + `FlushAsync`-reads-them
race shown above. Build it as the kiosk's already-shipped model:

```csharp
public sealed record LedFrame(ImmutableArray<Rgb> Pixels);    // immutable snapshot
public abstract record LedAnimation { public abstract LedAnimation Next(); /* pure */ }
//   the kiosk's closed union: Solid / Blink / Chase / Comet / Breathe / Rainbow /
//   Heartbeat / Sequence — Sequence composes the others on a timeline, for free.

// SHELL: owns the SPI lease + the tick; snapshots the frame, then transfers it.
internal sealed class Apa102Flusher {
    public Task PushAsync(LedFrame frame, SpiLease spi, CancellationToken ct);  // serialise a value, no shared mutation
}
```

`SetRgb(...).Wait()` and torn frames become unrepresentable: the flusher
serialises an immutable `LedFrame` value; producers build the next frame as a
new value. The kiosk's `TreehopperLedStripRenderer` can then sit *on*
Periphery.Treehopper instead of raw Treehopper SPI, closing the loop.

### DEC-006 — Device drivers compose as pure transforms over the two streams

A higher-level driver (sensor, port-expander, LED strip) is a pair of pure
functions plus the shell's streams:

```
device-intent ──(pure encode)──▶ Command[] ──▶ [shell: bulk-OUT]
[shell: BoardReport stream] ──▶ BoardReport ──(pure decode)──▶ device-state
```

That's the operator-node shape (`f(input) → output` over an edge), and it lines up with ADR-0042
(substrate integration): an LED-frame source can be a graph pipeline whose
sink is the SPI flusher, so the *same* substrate that carries camera frames can
carry LED frames. Drivers stay testable as plain functions over recorded
streams.

## Rationale

- **It attacks the actual root cause.** The audit's three buckets share one
  root: fused state/IO/timing. Fixing the async shell (ADR-0039) without
  separating the core leaves the door open to re-growing the same races inside
  the new internals — most obviously in the v2 library port, which is where the
  APA102 race lives today.
- **It's already proven in a shipped consumer.** The kiosk's LED subsystem
  (immutable `LedAnimation`, pure `Next()`, `SequenceAnimation`, renderer-owned
  `ReconcileAsync`) is the same design, shipped and tested headlessly without
  hardware. This ADR generalises a known-good local result rather than betting
  on a new one.
- **It makes the testing tiers in ADR-0039 cheaper and deeper.** A pure codec
  is exhaustively unit-testable; an immutable report stream is replayable from a
  recording; reconcile is a pure diff you can assert on. The hardware tiers
  (lifecycle, wire-level) then only need to cover what genuinely needs
  hardware.
- **It follows Periphery's grain.** Immutable `DeviceInfo`, channel-backed
  `CameraSession`, closed-union `DeviceResolution`, reconcile-style lifecycle —
  every primitive this ADR leans on already exists in the codebase.
- **The moment is free.** `src/Periphery.Usb/` isn't written; ADR-0038/0039 are
  *Proposed*. Adopting this now is a structuring choice, not a refactor.

## How this maps to the ADR-0039 audit

| Original SDK pain point (source) | Dissolved by |
|---|---|
| `Pin.Mode` / `DigitalValue` setters do USB IO + `Forget()`/`.Wait()` (`Pin.cs`) | DEC-001 (codec) + DEC-003 (config is a value; reconcile is the IO) |
| Global `Settings.PropertyWritesReturnImmediately` flag | DEC-003 — no setter commits IO, so there's nothing to flag |
| `Await*ValueChangeAsync` replaces a shared `TaskCompletionSource` (`Pin.cs`) | DEC-002 — `Reports.FirstAsync(...)`; no shared TCS exists |
| `UpdateValue` mutates pin state on the USB thread, unlocked (`Pin.cs`) | DEC-002 — decode produces an immutable `BoardReport`; no cross-thread field writes |
| `Connection_PinEventDataReceived` fuses decode + mutation (`TreehopperUsb.cs`) | DEC-001 + DEC-002 — pure decode → channel publish |
| `SoftPwmManager` timing tangled with mutable pin dict | DEC-004 — pure phase + shell-owned tick |
| `Apa102.Led.red/green/blue` read during `FlushAsync`; `SetRgb().Wait()` | DEC-005 — immutable `LedFrame`, snapshot-then-transfer |
| No reconnect; dead instance after removal | DEC-003 (reconcile from blank) + ADR-0039's `DeviceSessionHost` |

## Alternatives considered

- **Leave it as "internals" (ADR-0039 as written).** The leases + async surface
  alone are a huge improvement and one could let the internal structure emerge.
  Rejected as the *default* because the highest-value, hardest-to-retrofit
  decision (immutable report stream vs. mutable cached fields; config-as-value
  vs. setter-IO) is an internal one, and the v2 library port will re-introduce
  the APA102 race unless the model is stated up front. Cheap to state now,
  expensive to reverse after Pin/peripheral classes exist.
- **A reactive/INPC state model on the board (mirror the old SDK, but cleaned
  up).** Rejected — `INotifyPropertyChanged`-per-pin is what ADR-0039 already
  removes; per-property change events over mutable fields are the source of the
  cross-thread races, not a fix for them.
- **Full event-sourcing / persisted command log for the board.** Overkill. The
  board is not a durable aggregate; an immutable *latest snapshot* stream plus a
  desired-config value is the right amount of structure. We borrow the
  pure-transition idea, not the persistence machinery.
- **Make `Encode`/`Decode` methods on a stateful `TreehopperWireProtocol`
  object (ADR-0039's literal phrasing).** Acceptable, but a stateless static
  (or pure instance with no fields) is strictly more testable and removes any
  temptation to cache wire state. Keep it pure.

## Consequences

### What we gain

- A protocol layer that is **deterministic and unit-testable to the byte with
  no board** — the cheapest test tier, covering the encode/mode-bit/clock-
  divider class of bugs.
- Board state that is **race-free by construction** (no shared mutable pin
  fields, no replaceable TCS) and **replayable** (record the `BoardReport`
  stream, re-run consumers offline).
- **Reconnect with no special-case re-init** — reconcile the desired-config
  value against a blank report.
- A **v2 LED library that can't tear frames**, reusing the kiosk's shipped
  design, with composition (`SequenceAnimation`-style) for free.
- A **substrate-friendly composition story** (DEC-006) consistent with ADR-0042.

### What we accept

- **A few more types** in the core (`Command`/`BoardReport`/`BoardConfig`
  unions, `Plan`) than a setter-driven design. They are small, immutable, and
  the source of the testability — net win, but real surface.
- **A reconcile/diff step** instead of direct setters. One indirection; pays for
  itself at the first reconnect and the first concurrency bug avoided.
- **Projection cost** for "current pin value" (read latest snapshot vs. read a
  field). Negligible; the latest report is one volatile reference.

### What we constrain

- **Core purity is a hard rule.** `Command`/`BoardReport`/`BoardConfig` and
  `Encode`/`Decode`/`Plan` take no `CancellationToken`, touch no clock, hold no
  `IUsbBulkChannel`, start no `Task`. If a function needs any of those, it
  belongs in the shell. CI/code-review enforce this boundary.
- **The pin-report producer is the only writer** of board state. Consumers read
  the stream; nothing mutates a shared pin field.
- **The v2 `Treehopper.Libraries` LED/display drivers** must use the immutable-
  frame + flusher model (DEC-005). No mutable per-pixel fields read during a
  transfer.
- **No timing in core** (DEC-004). Drivers own their clocks.

## Affected files (planned)

Adjusts/extends ADR-0039's "Affected files":

- `src/Periphery.Treehopper/Wire/Command.cs` — closed-union command values.
- `src/Periphery.Treehopper/Wire/BoardReport.cs`, `PinSnapshot.cs` — immutable report snapshot.
- `src/Periphery.Treehopper/Wire/TreehopperWire.cs` — **pure** `Encode`/`Decode`/`Plan` (replaces the stateful-codec phrasing in ADR-0039).
- `src/Periphery.Treehopper/BoardConfig.cs` — desired-configuration value.
- `src/Periphery.Treehopper/TreehopperBoard.cs` — shell: owns `IUsbBulkChannel`, the report producer, `ReconcileAsync`. (ADR-0039)
- `tests/Periphery.Treehopper.Tests/WireRoundTripTests.cs` — pure codec property tests (no hardware).
- `tests/Periphery.Treehopper.Tests/ReconcilePlanTests.cs` — `Plan(desired, actual)` is a pure assertion target.
- *(v2)* `src/Periphery.Treehopper.Libraries/Displays/Apa102.cs` — `LedFrame` + flusher, per DEC-005.

## Testing

The split changes the *shape* of ADR-0039's three test tiers, mostly by making
the bottom tier carry far more:

- **Pure (new, biggest tier, per-PR, no hardware):** round-trip every `Command`
  through `Encode`; decode golden `pinStateBuffer`s into expected
  `BoardReport`s; assert `Plan(desired, actual)` emits the minimal command set;
  tick `LedAnimation`/`SoftPwmPhase` and assert frames. This is what
  `docs/patterns/wire-level-testing.md` wanted but couldn't get without a logic
  analyzer — for the encode/decode half, the analyzer is now only needed to
  confirm the *bytes match real firmware*, not to find logic bugs.
- **Lifecycle / reconnect (ADR-0039, per-PR Linux):** the `authorized` sysfs
  toggle still drives real add/remove; `ReconcileAsync`-from-blank is the code
  under test.
- **Wire-level (ADR-0039, nightly rig):** now a thin confirmation that the pure
  codec's bytes are firmware-accurate, not a logic hunt.

## Related ADRs

- [ADR-0039 — Periphery.Treehopper](0039-periphery-treehopper.md) — **complemented** by this ADR (the shell; this is the core).
- [ADR-0038 — Periphery.Usb](0038-periphery-usb.md) — provides the `IUsbBulkChannel` / `UsbDeviceProxy` that is the interpreter for DEC-001.
- [ADR-0031 — Modern composable API shapes](0031-modern-composable-api-shapes-for-periphery.md) — the closed-union (`DeviceResolution`) and `IAsyncEnumerable` precedents this ADR reuses.
- [ADR-0035 — Periphery.Camera](0035-periphery-camera.md) — `CameraSession`'s channel-backed stream is the model for the `BoardReport` stream (DEC-002).
- [ADR-0042 — substrate integration](0042-periphery-crossbar-substrate-integration.md) — the composition target for DEC-006.
- [ADR-0030 — Application-level reconnect](0030-application-level-reconnect.md) / [ADR-0032 — Device session host](0032-device-session-host.md) — presence-level reconcile that DEC-003 mirrors at the configuration level.
