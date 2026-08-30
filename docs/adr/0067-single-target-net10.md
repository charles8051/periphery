---
title: "ADR-0067: Single-target net10.0 — drop the untested net8.0 TFM"
status: "Superseded"
date: "2026-07-24"
authors: "@charles8051"
tags: ["architecture", "decision", "packaging", "tfm", "ci", "testing"]
supersedes: ""
superseded_by: "ADR-0069"
---

# ADR-0067: Single-target net10.0 — drop the untested net8.0 TFM

## Status

> **Superseded by [ADR-0069](0069-restore-net8-tfm-untested.md) (2026-07-24).** `net8.0`
> has been restored on every published library and deliberately kept out of CI. The
> risk analysis below — that a published TFM with no test coverage is a fiction — is
> still accurate and is the reason ADR-0069 states the trade explicitly rather than
> making it silently; what changed is the judgement that forcing a major version bump
> to remove that risk cost more than the risk itself, given every current consumer was
> already on `net10.0`. Read this ADR for the reasoning, ADR-0069 for what is in force.

> Number `0067` is provisional until merge. Resolves [periphery#158](https://github.com/charles8051/periphery/issues/158).

## Context

Every packable library under `src/` multi-targeted `net8.0;net10.0`. That was
established incidentally, not deliberately: ADR-0018 removed the Windows-specific
TFMs (`net*-windows10.0.17763.0`) and, in doing so, left `Periphery.csproj` on
`net8.0;net10.0`; every library added since copied that line.

[#148](https://github.com/charles8051/periphery/pull/148) retargeted the remaining
12 test projects from `net8.0` to `net10.0`. That was the correct fix for CI — the
`mcr.microsoft.com/dotnet/sdk:10.0` container has no .NET 8 runtime, so every
`net8.0` testhost failed to launch — but it left the repo in a state where the
`net8.0` TFM we publish is **compile-only**:

- Not one test has executed against `net8.0` since `#148`.
- The release gate (`publish.yml` → `dotnet test … Periphery.slnx`) cannot catch
  a net8-specific regression: runtime-behaviour differences in `TimeProvider`,
  `Task` scheduling, `LibraryImport` marshalling, or BCL API behaviour would all
  ship unnoticed.
- `Periphery.Camera.Testing` (ADR-0065) shipped a `net8.0` asset on day one that
  has never been run.

"We build it, we don't run it, we ship it anyway" is a support claim we cannot
back. Periphery has no external consumers and no stability
commitment; an unverified target framework is exactly the baggage that stance
says not to carry.

### Consumer check

The only consumers are sibling repositories, all already 100% `net10.0`:

| Repo | Projects | TFM |
|---|---|---|
| `frame-flow` | 54 | `net10.0` |
| the kiosk consumer | 10 | `net10.0` |
| the fleet consumer | 206 | `net10.0` |

Periphery has no graph-substrate dependency — ADR-0045 removed it; only an
explanatory comment survives in `src/Periphery.Camera/Periphery.Camera.csproj`.

There is no wider consumer set to check: Periphery is **not published to
NuGet.org**. Packages go to a user-level local feed (`src/Directory.Build.props`)
and, on a release tag, to the private GitHub Packages feed. The repo carries no
stability commitment, and the README still says "NuGet package coming
soon".

So dropping `net8.0` breaks nothing that exists.

## Decision

**Every `src/` library targets `net10.0` only.** `<TargetFrameworks>net8.0;net10.0</TargetFrameworks>`
becomes `<TargetFramework>net10.0</TargetFramework>` (singular element) in all 17
multi-targeted projects. `benchmarks/Periphery.Camera.Benchmarks` — which was
pinned to `net8.0` alone and would no longer resolve its `ProjectReference`s —
moves to `net10.0` as well.

The rule going forward: **a TFM we publish is a TFM the test sweep runs.** If a
`net8.0` (or any additional) leg is ever wanted back, it comes back together with
test projects that multi-target it and a CI image that carries both runtimes —
option 2 in `#158`. Adding a publish target without a test leg is not a valid state.

### Consequences for language version

With `net8.0` gone the effective language version rises from C# 12 to C# 14. Two
comments attributed a code shape to "C# 12 / net8.0" — a `ref struct` local cannot
live across an `await`, so span comparisons are pulled into synchronous helpers
(`Stm32DfuProgrammer.VerifyChunk`, `MegatecWire`'s report loop). That restriction
still holds in C# 14; only the version attribution was stale, so the comments were
reworded and the code shape kept.

### Versioning

Package versions are derived by MinVer from git tags (root `Directory.Build.props`),
so there is no version field in any `.csproj` to bump and this change carries none.
Removing a published TFM is nonetheless a **breaking** change to the package's
asset set: the next release tag must be a MAJOR bump (`v2.x` → `v3.0.0`), not a
minor or patch. Recorded here because the tag, not this commit, is where that
decision gets made.

## Consequences

### Positive

- **POS-001**: Every published asset is covered by the test sweep. The release
  gate means what it claims.
- **POS-002**: Build and pack time roughly halves for the 17 multi-targeted
  libraries; the CI matrix loses a dimension it was no longer exercising.
- **POS-003**: C# 14 / .NET 10 BCL surface is available without a
  lowest-common-denominator check or `#if` guards. (The repo has zero
  TFM-conditional MSBuild and zero `#if NET*` directives, so nothing had to be
  untangled.)
- **POS-004**: The `net10.0` floor is stated once and honestly, rather than
  implied by an untested `net8.0` asset.

### Negative

- **NEG-001**: A consumer on .NET 8 can no longer reference Periphery. None
  exists today; a future one would need `net10.0` or a deliberate re-add under
  the rule above.
- **NEG-002**: .NET 10 is the current LTS, but Periphery now has no LTS-minus-one
  fallback. Accepted: every known consumer moves as one, and none lags.

## Alternatives Considered

### A — Restore a net8.0 test leg (`#158` option 2)

Multi-target the test projects again and give the Linux CI container both
runtimes (or run the net8 leg on a runner that has one). Rejected: it buys
verification of a TFM that no consumer uses, at the cost of a second CI leg, a
custom container image, and a permanent C# 12 ceiling on every `src/` project.
Pay that only when a real .NET 8 consumer appears.

### B — Leave it compile-only, document the caveat

Rejected outright. A published asset that no test has ever executed is not a
documentation problem.

### C — Drop net8.0 but keep the benchmarks project on it

Rejected: `benchmarks/Periphery.Camera.Benchmarks` `ProjectReference`s
`Periphery` and `Periphery.Camera`, so it cannot resolve them once those are
`net10.0`-only. Benchmarks should also measure the runtime consumers actually
use.

## Notes

ADR-0007 (niche-platform feasibility) writes its illustrative TFM lists against
the old `net8.0;net10.0` base (e.g. `net8.0;net10.0;net8.0-android;net8.0-ios`).
Those examples are historical; read the base as `net10.0` and the platform TFMs
as `net10.0-android` / `net10.0-ios` should that work ever be picked up.
Likewise, the `TargetFrameworks → net8.0;net10.0` row in ADR-0018's files-changed
table records what that change did at the time and is left intact.
