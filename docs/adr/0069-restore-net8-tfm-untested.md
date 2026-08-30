---
title: "ADR-0069: Restore the net8.0 TFM, publish it untested, and say so"
status: "Accepted"
date: "2026-07-24"
authors: "@charles8051"
tags: ["architecture", "decision", "packaging", "tfm", "ci", "versioning"]
supersedes: "ADR-0067"
superseded_by: ""
---

# ADR-0069: Restore the net8.0 TFM, publish it untested, and say so

## Status

**Accepted** — supersedes [ADR-0067](0067-single-target-net10.md) (single-target net10.0).

## Context

ADR-0067 dropped `net8.0` from every published library, on the reasoning that the
TFM had become a fiction: since `#148` retargeted the test projects to `net10.0`,
**no test had executed against the `net8.0` assets being published**. It stated the
rule *"a TFM we publish is a TFM the test sweep runs"* and single-targeted `net10.0`.

That reasoning was sound about the *risk*. It was incomplete about the *cost*.
Dropping a published TFM is a breaking change, which forces a major version bump —
and the only known consumers (frame-flow, the kiosk consumer, the fleet consumer) were
already on `net10.0`, so the bump bought nothing for the very consumers it was
justified by. Reach for a future `net8.0` consumer was surrendered to remove a
theoretical risk that no observed defect had ever exercised.

## Decision

### D1. `net8.0` is restored on every published library

All 17 `src/` projects return to `<TargetFrameworks>net8.0;net10.0</TargetFrameworks>`.
The benchmarks project stays `net10.0`: it is not published, and its previous
`net8.0`-only pin was a genuine bug ADR-0067 fixed in passing.

### D2. `net8.0` is deliberately **not** added to CI

The test projects remain `net10.0`-only and the CI matrix is unchanged. Adding a
`net8.0` test leg would mean either a second runtime in the Linux SDK container or a
second job, for a target no consumer currently uses.

### D3. The resulting risk is named here rather than left implicit

**The `net8.0` assets we publish are compiled but never executed by any test.** That
is exactly the condition ADR-0067 was written to end, reinstated knowingly. What
could slip through: runtime-behaviour differences the compiler cannot see —
`TimeProvider`/`Task` scheduling, `LibraryImport`/marshalling shape, BCL behaviour
changes between .NET 8 and .NET 10, and anything guarded by a
`NET8_0_OR_GREATER`-style conditional (the repo currently has none, verified).

The honest framing: **`net8.0` is offered on a best-effort basis.** It compiles, and
the pure logic is target-independent, but it carries no test evidence. A consumer
who adopts `net8.0` in earnest should be told to run their own suite against it, and
that is the trigger to revisit D2.

### D4. Revisit when a real `net8.0` consumer appears

If any consumer actually pins `net8.0`, D2 stops being defensible and a test leg
should be added at that point — the cost is then justified by a real dependant
rather than a hypothetical one.

## Consequences

### Positive

- No major version bump is forced by a TFM drop; this release stays a minor bump
  as far as target frameworks are concerned.
- `net8.0` consumers remain addressable without a future re-add.

### Negative / trade-offs

- **A published target framework has zero test coverage.** ADR-0067's core objection
  stands and is simply accepted rather than answered.
- CI cannot catch a net8-specific regression; the first report would come from a
  consumer.
- Build and pack time roughly double for the affected projects.

## Alternatives considered

- **Keep single-targeting (status quo, ADR-0067).** Rejected: it forces a major
  version for no benefit to any current consumer.
- **Restore `net8.0` *and* add a CI leg.** The rigorous option, and the one to take
  the moment D4's trigger fires. Rejected for now as cost without a dependant —
  it needs a second runtime in the Linux SDK container or a separate job.
- **Restore `net8.0` silently.** Rejected outright: ADR-0067 is a written decision,
  and leaving it in force while the code does the opposite is worse than either
  choice on its own. That is the whole reason this ADR exists rather than a commit
  that quietly edits the csproj files.
