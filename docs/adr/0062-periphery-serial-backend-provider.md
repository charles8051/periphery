---
title: "ADR-0062: Periphery.Serial — backend-provider model (BCL / RJCP), superseding the single native implementation"
status: "Proposed"
status_note: "Not implemented - there is no `Periphery.Serial` package. It does NOT block the AN3155 serial bootloader: `Periphery.Bootloader.Stm32.Serial` was built and tested without it, and merged 2026-09-02 (#153, `6fd0e9d`). See the Amendment."
date: "2026-06-16"
authors: "@charles8051"
tags: ["architecture", "decision", "serial", "extension", "uart", "backend-provider", "rjcp", "system-io-ports", "periphery-serial"]
supersedes: "ADR-0028"
superseded_by: ""
---

# ADR-0062: Periphery.Serial — backend-provider model (BCL / RJCP), superseding the single native implementation

## Status

> Number `0062` is provisional until merge. **Supersedes [ADR-0028](0028-periphery-serial-extension.md)**
> (Periphery.Serial as a single from-scratch native P/Invoke implementation). The
> discovery/IO boundary, the `PipeReader`/`PipeWriter` surface, custom-baud
> requirement, and `SerialPortOptions` shape from ADR-0028 are **retained**; what
> changes is *how the IO is implemented* — swappable backends instead of one
> native stack.

## Context

[ADR-0028](0028-periphery-serial-extension.md) decided to build `Periphery.Serial`
as a single, from-scratch **native P/Invoke** serial stack (Windows OVERLAPPED,
Linux `epoll`/`termios2`, macOS `kqueue`/`IOSSIOSPEED`), explicitly rejecting
both `System.IO.Ports.SerialPort` (documented deadlocks, `IsOpen` lies,
unreliable `GetPortNames`) and `RJCP.SerialPortStream` (third-party dep vs the
zero-dependency constraint).

Two things have changed the calculus:

1. **The native stack is the expensive, bug-prone part.** ADR-0028's own NEG-001
   flags the Windows OVERLAPPED path (correct cancellation + `NativeOverlapped`
   lifetime) as "the most implementation-complex piece," and NEG-002 notes E2E
   testing needs virtual COM pairs on every platform. That's a large, high-risk
   investment to *re-derive* correct serial IO that mature libraries already
   provide.

2. **The first real consumer is firmware flashing** ([ADR-0061](0061-firmware-flashing-platform.md)),
   where serial robustness and **custom/high baud rates** (ESP8266 74880;
   ESP32 230400/460800/921600; STM32 AN3155) are load-bearing. `System.IO.Ports`
   custom-baud support is the flakiest part of the BCL cross-platform;
   `RJCP.SerialPortStream` handles it cleanly and is battle-tested. Many
   consumers will *want* RJCP regardless of what we build.

ADR-0028 framed this as a binary "build native vs. take a bad dep." The better
answer is **neither exclusively**: provide a thin abstraction and let the
consumer pick a backend.

## Decision

Deliver `Periphery.Serial` as a **backend-provider** model: a transport-free
abstraction plus swappable backend packages. Consumers (and the
`Periphery.Bootloader.*` serial clients) depend on the abstraction; they select a
backend at composition time.

```
Periphery.Serial          abstraction — ISerialPort + SerialPortOptions + enums + exception
  ├─ Periphery.Serial.Bcl    System.IO.Ports backend   (no third-party dep; BCL caveats)
  ├─ Periphery.Serial.RJCP   RJCP.SerialPortStream      (third-party; robust; recommended)
  └─ Periphery.Serial.Native [deferred] the ADR-0028 native P/Invoke stack
```

### DEC-001 — Backend-provider, not a single implementation (supersedes ADR-0028)

`Periphery.Serial` is an **abstraction package**; the IO lives in backend
sub-packages. This replaces ADR-0028's single-native-impl decision. The benefits:
much less load-bearing native code now, a robust default via a mature library,
a zero-third-party-dep option still available, and a fake-backend testing seam
for consumers.

### DEC-002 — The abstraction is a small `ISerialPort` interface in `Periphery.Serial`

```csharp
namespace Periphery.Serial;

public interface ISerialPort : IAsyncDisposable
{
    DeviceInfo DeviceInfo { get; }
    SerialPortOptions Options { get; }
    PipeReader Reader { get; }          // ADR-0028's pipe surface, retained
    PipeWriter Writer { get; }
    SerialPinStates PinStates { get; }
    Task SetDtrAsync(bool value, CancellationToken ct = default);
    Task SetRtsAsync(bool value, CancellationToken ct = default);
}
```

`SerialPortOptions` (baud incl. custom, data/stop/parity, flow control,
timeouts), the `SerialPinStates` flags, and `SerialPortException :
DeviceEnumerationException` live here too — verbatim from ADR-0028. Each backend
exposes a static `OpenAsync(DeviceInfo, SerialPortOptions, CancellationToken) →
ISerialPort`.

This is a deliberate, [ADR-0024](0024-extension-package-pattern.md)-sanctioned
deviation from the canonical *sealed-class* Layer-1 primitive (NEG-001 permits an
`I{Domain}Port` interface where backends must be swapped). The Layer-2 lifecycle
manager (`SerialPortHandle`) wraps an `ISerialPort` from the chosen backend.

### DEC-003 — Ship two backends; RJCP is the recommended default

- **`Periphery.Serial.Bcl`** — over `System.IO.Ports` (a Microsoft package, **no
  third-party dep**). Honest about ADR-0028's documented BCL limitations
  (disconnect handling, custom baud on Linux/macOS). The "I can't take a
  third-party dep, Windows, standard baud" option.
- **`Periphery.Serial.RJCP`** — over `RJCP.SerialPortStream` (third-party,
  battle-tested, correct custom baud). **The recommended backend**, and the one
  the shipped `periphery flash` tool bundles, because the firmware-flashing use
  case needs robustness + custom/high baud.

### DEC-004 — The native P/Invoke backend (ADR-0028's design) is deferred

`Periphery.Serial.Native` — ADR-0028's from-scratch native stack — is the only
option that is *both* zero-third-party-dep *and* robust, but it carries
ADR-0028's full NEG-001/002 cost. Deferred (possibly indefinitely): build it only
if a consumer needs zero-third-party-dep **and** robustness **and** the BCL
backend's bugs bite. ADR-0028's platform tables (`kernel32` OVERLAPPED, `termios2`,
`IOSSIOSPEED`) remain the design reference if/when it is built.

### DEC-005 — Third-party isolation: the rule is preserved, not broken

[ADR-0024](0024-extension-package-pattern.md)'s "no third-party runtime deps"
intent is *don't force third-party supply-chain surface on consumers*. The
backend model preserves that: the **abstraction**, the **`.Bcl` backend**, and
**every consumer that doesn't choose RJCP** stay third-party-free. `RJCP` is
quarantined in `Periphery.Serial.RJCP`, an **explicit opt-in** — the same
isolation pattern as ADR-0024's platform sub-packages (WinRT behind a guarded
sub-package) and [ADR-0061](0061-firmware-flashing-platform.md) DEC-006
(first-party `call-and-response` confined to the serial bootloader client). The
one genuinely new thing vs. those is that RJCP is *third-party* — acceptable
because it is isolated, opt-in, and clearly labelled.

## Consequences

### Positive

- **Far less load-bearing native code now** — no from-scratch OVERLAPPED/epoll/kqueue stack on the critical path.
- **Robust default** (RJCP) with correct custom/high baud for the flashing use case, *and* a zero-third-party-dep option (`.Bcl`).
- **Testable** — consumers fake `ISerialPort`.
- **Choice respected** — orgs forbidding third-party deps use `.Bcl`; everyone else uses `.RJCP`.

### Negative

- **An interface seam** (`ISerialPort`) instead of a sealed concrete type — a small, sanctioned deviation from the ADR-0024 canonical shape.
- **`.Bcl` ships ADR-0028's known `System.IO.Ports` limitations** — must be documented; RJCP is the steer for production.
- **A third-party dep enters the ecosystem** (isolated, opt-in) — the genuine, accepted relaxation.

## Alternatives considered

- **Single native implementation (ADR-0028 as written).** Superseded — highest-effort, highest-risk path to re-derive correct serial IO that RJCP already provides; deferred to `Periphery.Serial.Native` (DEC-004).
- **RJCP-only (no BCL).** Rejected — drops the zero-third-party-dep option some consumers require.
- **BCL-only.** Rejected — `System.IO.Ports`' deadlock/`IsOpen`/custom-baud problems make it inadequate for production flashing (ADR-0028's whole rationale).
- **Wrap `System.IO.Ports.BaseStream` and call it done.** Rejected for the same reasons (ADR-0028 ALT-001/002): inherits the BCL defects.

## Affected files (planned)

- `src/Periphery.Serial/` — `ISerialPort.cs`, `SerialPortOptions.cs`, `SerialPinStates.cs`, `SerialPortException.cs`, `SerialPortHandle.cs` (Layer 2 over `ISerialPort`).
- `src/Periphery.Serial.Bcl/` — `BclSerialPort.cs` (System.IO.Ports).
- `src/Periphery.Serial.RJCP/` — `RjcpSerialPort.cs` (RJCP.SerialPortStream).
- *(deferred)* `src/Periphery.Serial.Native/` — ADR-0028's native backend.
- `tests/Periphery.Serial.Tests/` — abstraction + a fake `ISerialPort`; backend tests gated on a virtual COM pair (com0com / socat).

## Related ADRs

- [ADR-0028 — Periphery.Serial (single native impl)](0028-periphery-serial-extension.md) — **superseded by this ADR**; its boundary, pipe surface, options, and native platform tables are retained as the `.Native` reference.
- [ADR-0024 — Extension package pattern](0024-extension-package-pattern.md) — the sealed-class Layer-1 (here deviated to `ISerialPort`, sanctioned) and the no-third-party-deps rule (preserved via opt-in isolation, DEC-005).
- [ADR-0061 — Firmware-flashing platform](0061-firmware-flashing-platform.md) — the first consumer; `Periphery.Bootloader.Stm32.Serial` / `.Esp32.Serial` depend on the `Periphery.Serial` abstraction.

---

## Amendment (2026-09-02): Periphery owns the port, and `ISerialPort` is a pipe

Two things happened after this ADR was written.

1. **A serial bootloader flasher was built without `Periphery.Serial`.**
   `Periphery.Bootloader.Stm32.Serial` implements AN3155 against
   `CallAndResponse.Transport.Serial` and its `RJCP.SerialPortStream`. Discovery came from
   `DeviceInfo.PortName`, which lives in Periphery core and never needed this package. Merged
   2026-09-02 as [#153](https://github.com/charles8051/periphery/pull/153) (`6fd0e9d`): 42 tests
   against an in-memory AN3155 device emulator, an AOT publish with zero trim warnings, and no
   flash verified against real hardware. What it establishes is reachability of the transport,
   which is exactly the claim at issue here.
2. **`call-and-response` is splitting its serial transport into RJCP and BCL backends**
   (in flight at the time of writing). That is the same backend-provider axis DEC-003 defines
   here, one layer down.

The question is whether Periphery should mirror that split. It should not — and not because
Periphery should defer to it. Because a serial transport does not belong in a framing library at
all, and the two libraries do not need to know about each other.

---

### 1. A serial port is an exclusive open, so exactly one layer may own it

This constraint is absent from the original ADR and it is the decisive one.

A COM port cannot be opened twice. An application that flashes a target *and* uses a serial
peripheral through Periphery would, under a mirrored split, hold `Periphery.Serial.RJCP` and
`call-and-response`'s RJCP backend in the same process — two wrappers over the same
`SerialPortStream`, contending for one handle.

Owning a device handle is Periphery's job. `call-and-response` is a framing library: it turns a
byte stream into frames. It has no model of a device, no discovery, and no lifecycle, and its core
references no device API of any kind. The port belongs on the Periphery side of that line.

### 2. Two backend axes over the same two libraries can disagree

`Periphery.Serial.Bcl` alongside `call-and-response`'s RJCP backend pulls RJCP into the process
regardless. That is not a partial erosion of DEC-003's zero-third-party-dep guarantee but the whole
of it: a consumer who must forbid third-party deps would have to police two package families to get
a promise this ADR offers as a property of one.

### 3. DEC-002 amended — `ISerialPort` implements `IDuplexPipe`, and that removes the dependency edge

DEC-002 already gives the abstraction the pipe surface:

```csharp
PipeReader Reader { get; }
PipeWriter Writer { get; }
```

`System.IO.Pipelines.IDuplexPipe` is exactly `{ PipeReader Input; PipeWriter Output; }`. Declaring
the interface as

```csharp
public interface ISerialPort : IDuplexPipe, IAsyncDisposable
```

costs two lines. `Reader`/`Writer` are retained as the domain-readable names; `Input`/`Output` are
the interface implementation.

**The payoff is not convenience, it is the absence of a dependency.** `IDuplexPipe` is a BCL type.
Periphery produces one; `call-and-response` consumes one; **neither package references the other**.
No adapter, no second open, and no direction of dependency to argue about. A bootloader package
takes an `ISerialPort` from whichever backend the application composed and hands it straight to a
`Transceiver`.

This also settles a question [ADR-0061](0061-firmware-flashing-platform.md) DEC-006 left implicit.
DEC-006 asks for "a thin `SerialDuplexPipe : IDuplexPipe` over `Periphery.Serial`'s pipe surface
(~10 lines)". There is nothing to write.

### 4. DEC-003 affirmed as written — two backends, and they live here

`Periphery.Serial.Bcl` and `Periphery.Serial.RJCP` are built as DEC-003 specifies. What changes is
the recommendation to `call-and-response`: **it should ship no serial transport package.**

The code is not so much moving as merging. `CallAndResponse.Transport.Serial` is one file of 132
lines, and its substance is a background read pump on a dedicated thread — needed because
`PipeReader.Create(stream)` forwards its own cancellation token into every `ReadAsync`, so a
consumer timeout cancels the *serial read* rather than the pipe read. `RjcpSerialPort` needs that
same pump for that same reason. If both packages exist it is written twice, in two repositories,
and the RJCP dependency is declared twice.

**What this costs.** A standalone `call-and-response` consumer — Modbus over a serial port, no
Periphery — loses a package they have today. Mitigated in section 5, and it is a real cost, not a
free simplification.

### 5. `call-and-response` keeps a `StreamDuplexPipe` in its core

Not a Periphery decision, but the recommendation this amendment is paired with: put a
`StreamDuplexPipe` in the `CallAndResponse` core package, BCL-only, with the same pump. Any
`Stream` becomes a transport — TCP, named pipes, `SerialPort.BaseStream`.

That is the library's own reasoning. Its ADR-0015 DEC-007 says a transport earns a package only
when the adaptation is non-trivial — "a background pump, a framing quirk, a vendor SDK that is not
stream-shaped" — and POS-003 claims transports become nearly free. A core `StreamDuplexPipe` makes
that true, and leaves `call-and-response` with **zero third-party dependencies in every package**.

The RJCP-specific part is the part that is not stream-generic, and it is the part that comes here.

### 6. DEC-005 stands as written

DEC-005 says RJCP is "quarantined in `Periphery.Serial.RJCP`, an explicit opt-in." Under this
amendment that is exactly what happens, and Periphery enforces it rather than inheriting it. The
isolation claim needs no weakening.

### 7. `Periphery.Serial` becomes required, and the migration must not re-block what already works

The original ADR treats `Periphery.Serial` as the enabler for serial flashing. Point 1 above shows
the IO is reachable without it. This amendment nevertheless makes the package **required for the
target design** — it is where the port, the pump, and the third-party isolation live.

That creates a hazard worth naming. The `status_note` used to claim this ADR blocks the AN3155
serial bootloader; it did not, and the branch above is the proof. Adopting this amendment must not
turn that false claim into a true one by fiat. `Periphery.Bootloader.Stm32.Serial` ships as it is,
against `CallAndResponse.Transport.Serial`, and migrates to `ISerialPort` **when
`Periphery.Serial` exists** — not before, and the migration is a constructor-argument change
because the programmer already takes an `IDuplexPipe`.

The ESP32 feature spec's path-A row carries the same overstatement, reading "Transport reachable:
**No.** No `Periphery.Serial` at all." Path A is reachable today at the cost of the RJCP
dependency. That table belongs to
[the ESP32 spec](../feature-specs/firmware-flashing/esp32/spec.md) and is corrected there.

### 8. Out of scope, and not yet measured

**Out of scope.** `CallAndResponse.Transport.BleNordicUart` is not covered by this reasoning. It is
40 lines, it is a pipe pair the caller drives from both ends rather than a device handle, and
Periphery's Bluetooth state is poll-only on Windows. Moving it by symmetry with serial would be
a decision made for the wrong reason.

**Measured (2026-09-02): the backend choice costs 3x on a request-response protocol, and
DEC-003 picks the slow one.**

Identical AN3155 round trips to an STM32G431 behind a CP210x, 115200 8E1, 50 iterations each,
raw port to raw port with no pipe or framing layer in between:

| Backend | Get (15-byte reply) | Get ID (5-byte reply) |
|---|---|---|
| `RJCP.SerialPortStream` | 30.75 ms | 30.71 ms |
| BCL `System.IO.Ports.SerialPort` | 3.90 ms | 3.07 ms |

The shape matters more than the ratio. The BCL times track reply size — 0.83 ms for 10 extra
bytes against 0.95 ms of actual wire time — so it is paying for the bytes. RJCP's do not move
at all, because a flat ~30 ms per exchange swamps them. That is a fixed wait, not throughput.

End to end, flashing the same 52 KB image through the same `Stm32SerialProgrammer` and changing
only the `IDuplexPipe` beneath it:

| Transport | Flash + verify |
|---|---|
| RJCP (`CallAndResponse.Transport.Serial`) | **47.1 s** |
| BCL `SerialPort.BaseStream` via `PipeReader.Create` | **14.6 s** |

AN3155 is round-trip bound — Write Memory is three exchanges per 256 bytes and the cap is 256,
so a 52 KB flash with verify is about 1,230 exchanges. At ~27 ms of avoidable overhead each,
that is the whole difference, and the measured 32.5 s saved matches the predicted ~33 s.

**This bears directly on DEC-003**, which recommends RJCP as the default. That recommendation
was made on portability and custom-baud grounds with no latency measurement behind it, and for
the flashing workloads this repo exists to serve it is the wrong default by 3x. It may still be
right where custom baud rates or non-Windows behaviour dominate. It should not be described as
the default without qualifying the workload.

**Caveats.** One board, one bridge, one driver (Silicon Labs `silabser` 6.7.3.350), Windows
only. FTDI and CH340 bridges may differ. The micro-benchmark isolates the port layer fairly;
the end-to-end comparison also swaps the pipe adapter, so attribution rests on the
micro-benchmark, which the end-to-end number then corroborates. The cause inside RJCP is not
established — its `COMMTIMEOUTS` use (`ReadIntervalTimeout`, `ReadTotalTimeoutConstant`) is the
obvious suspect and is unconfirmed.

**Still not measured.**

- **Control-line changes while a read pump is in flight.** Setting `DtrEnable`, `RtsEnable`, or
  `BaudRate` on an open port while a background `ReadAsync` is outstanding. The ESP32 reset dance
  needs exactly this, on every serial path. These are separate ioctls from a read and are expected
  to work; that expectation is untested, and it sets the shape of `ISerialPort`'s DTR/RTS methods.

  *One data point (2026-09-02, STM32G431 behind a CP210x).* Setting `DtrEnable`/`RtsEnable` after
  open, in both states, changed nothing about whether the part answered — that board wires neither
  line to NRST or BOOT0, so it says nothing about a board that does. It did surface a **backend
  difference worth knowing before `ISerialPort` fixes its open semantics**: `RJCP.SerialPortStream`
  opens with `DTR=True RTS=True`, and the BCL `SerialPort` opens with both false. On a board using
  the common auto-reset circuit, those two backends would do visibly different things at open —
  under DEC-003 a consumer could switch backend and silently change whether their part gets reset.

  So this is no longer only an unmeasured item — it is a hole in the contract. **DEC-002 exposes
  only post-open `SetDtrAsync`/`SetRtsAsync`, and `SerialPortOptions` names no initial state**, so
  a caller cannot express "open without touching the lines" and cannot correct them until after
  the transition has already happened. On a board that drives NRST or BOOT0 from DTR/RTS, the
  reset happens during `Open`, before any code of the caller's runs.

  **`SerialPortOptions` should carry the initial DTR and RTS state, both defaulting to
  deasserted, and both backends must establish it as part of open.** Deasserted because that is
  the state that does not drive an auto-reset circuit: a port that opens asserted holds a part in
  reset, or resets it on close, and the caller never gets a say. Where a backend cannot open
  without a transient — and this needs checking on each one rather than assuming — the ADR should
  say so plainly instead of implying a guarantee the platform will not give.
- **Whether `SetDtrAsync`/`SetRtsAsync` should be async at all.** DEC-002 declares them `Task`-returning.
  The underlying operations are synchronous property writes in both `System.IO.Ports` and
  `RJCP.SerialPortStream`. Worth revisiting when the backends are written.

### 9. A continuous probe loop is a second claim on the port; §1 settled which layer owns it, not for how long

Added 2026-09-02, alongside the [autoflash ADR](../feature-specs/firmware-flashing/autoflash/adr.md)
amendment that admits probe-identified targets to autoflash on bridges the operator bound at
arm time.

That amendment introduces something §1 did not consider: a loop that opens a port, sends the
AN3155 sync byte, and closes — on a cadence, for as long as autoflash stays armed. §1 settled
*which layer* owns a port. It did not settle **for how long**, and a probe loop makes that the
live question.

Two shapes, and `ISerialPort`'s lifecycle has to support whichever wins:

- **Open per cycle.** The port is held only for the length of one probe, so other tools on the
  bench can use it between cycles and a crashed session leaves nothing claimed. It pays an
  open/close per cycle, and it has a race: the part can reset between the probe that decided to
  flash and the open that flashes it, so the flash lands on a device state nobody inspected.
- **Hold for the armed session.** One open, reused by the probes and by the flash that follows,
  which removes that race outright. It also makes an armed session exclusive over the port for
  its whole duration — the strongest form of §1's claim, and the one most likely to surprise an
  operator who still has a terminal open on that port.

**Settled as open-per-cycle** ([autoflash adr.md](../feature-specs/firmware-flashing/autoflash/adr.md)
Decision 11), with the refinement that dissolves the choice: the race is not a reason to hold the
port *across* cycles, only a reason not to close it *within* one. A cycle that decides to flash
keeps its handle and flashes on it. The port is free between cycles, and a long-lived exclusive
claim over a shared bench never has to be justified.

What that asks of `ISerialPort` is the weaker requirement, and the useful one: open and dispose
must be cheap and repeatable at a ~1 Hz cadence, and a dispose must actually release the handle
rather than leave it claimed for the next open. No session-scoped ownership, no lease, no sharing
model. If reopen turns out to be too slow or too flaky on a real bridge, holding is still
reachable behind the same policy — the reverse would not have been.

It also adds to §8's unmeasured list. **How fast a bridge can be reopened** — CH340 and CP210x
drivers do not all release a handle promptly on close, and a probe cadence in the hundreds of
milliseconds is exactly where a slow release shows up as a spurious "no device."
