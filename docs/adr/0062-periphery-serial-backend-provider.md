---
title: "ADR-0062: Periphery.Serial — backend-provider model (BCL / RJCP), superseding the single native implementation"
status: "Proposed"
status_note: "Not implemented - there is no `Periphery.Serial` package. Blocks the AN3155 serial bootloader in [ADR-0061](0061-firmware-flashing-platform.md)."
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
