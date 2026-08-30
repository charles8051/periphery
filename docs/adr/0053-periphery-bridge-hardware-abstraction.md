---
title: "ADR-0053: Hardware-agnostic bridge — moved out of Periphery"
status: "Superseded"
date: "2026-06-02"
authors: "@charles8051 (direction)"
tags: ["architecture", "decision", "treehopper", "bridge", "moved", "tombstone"]
supersedes: ""
superseded_by: ""
---

# ADR-0053: Hardware-agnostic bridge — moved out of Periphery

## Status

**Superseded / moved.** This ADR's ambition was moved out of Periphery. The
substrate project it moved to is private and not something a public reader can
follow, so the ambition is parked here rather than relocated — but the decision that concerns Periphery, the
lane split below, is unaffected and still holds. The full original draft
explored a hardware-agnostic USB→peripheral bridge with capability
negotiation, a pure codec seam, an IO-agnostic transport seam, PBP firmware,
and embedded-hal portability.

## What changed and why

The ADR conflated two things that belong in two different places:

1. **A cross-boundary, zero-copy, refcounted, backpressure-aware buffer/transport
   *substrate*** (the "holy grail" — IO-agnostic transport, buffer-ownership
   inversion, PCIe/shared-memory edges, memory domains). That is **not a
   Periphery concern**, and it left. It went to a separate substrate project
   built in isolation with no Periphery dependency (ADR-0045 holds). That
   project is private, so a public reader cannot follow it; the conclusion that
   the ambition does not belong here is what survives, and it is the part that
   mattered.

2. **A general-purpose USB→peripheral device SDK.** This *stays in Periphery* and
   is the buildable, near-term path: **`Periphery.Treehopper`** on the current
   **EFM8 firmware**, per **[ADR-0039](0039-periphery-treehopper.md)** (the clean
   rebuild) and **[ADR-0052](0052-periphery-treehopper-pure-core.md)** (the pure
   core). It is general-purpose, not the multi-embodiment grand abstraction.

## Resulting lane split

- **Periphery** → `Periphery.Treehopper`, EFM8, general-purpose. ADR-0039/0052
  stand as written. **OQ-004 resolved:** ADR-0039 is **not** superseded — it is
  the Treehopper lane, maintained for general use.
- **Elsewhere** → the cross-boundary buffer substrate. The grand
  multi-embodiment device bridge — capability negotiation, the PBP wire
  protocol, codecs/embodiments, the firmware/`embedded-hal` portability story —
  is **parked**, to be designed if a substrate and real cross-boundary hardware
  ever exist together.
- **Relationship** is deliberately **deferred**: a future bridge consumer *may*
  use Periphery as a backend (device enumeration / USB transfer) behind a
  transport seam, but nothing depends on Periphery today and Periphery depends
  on nothing.

## Why not just delete this file

ADRs are a decision trail. This tombstone records *that* the ambition moved and
the lane split it produced — so a reader following a link to ADR-0053 lands on
the reasoning instead of a 404. The substantive design is in this file's earlier
revisions.

## Cross-references

- [ADR-0039](0039-periphery-treehopper.md) / [ADR-0052](0052-periphery-treehopper-pure-core.md) — the general-purpose EFM8 Treehopper SDK that stays here.
- [ADR-0045](0045-substrate-independence-from-crossbar.md) — Periphery takes no substrate dependency; unchanged by this move.
