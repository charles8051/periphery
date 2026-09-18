---
title: "ADR-0090: A supplementary activity source reports levels into the watcher, and never touches presence"
status: "Proposed"
status_note: "No code. Written to discharge the dependency ADR-0085 D7 §2 creates and does not settle: core has no extension point through which a non-OS source can contribute activity edges. Tracked as issue #274."
date: "2026-09-18"
authors: "@charles8051"
tags: ["architecture", "decision", "device-watcher", "activity", "state-model", "bluetooth", "extension-package", "adr-0087"]
supersedes: ""
superseded_by: ""
depends_on: ["0004-two-level-device-state-model.md", "0052-periphery-treehopper-pure-core.md", "0054-windows-property-freshness-events-over-polling.md", "0073-observations-not-verdicts.md", "0085-the-32feet-binding-is-two-integration-packages.md", "0087-reconcile-activity-at-the-watcher-boundary.md", "0088-open-handles-on-activity-not-presence.md"]
---

# ADR-0090: A supplementary activity source reports levels into the watcher, and never touches presence

> Number `0090` is provisional until merge (the next free number after ADR-0089),
> per this repo's "assign the number at merge" convention.
>
> **Amends [ADR-0087](0087-reconcile-activity-at-the-watcher-boundary.md) D1** within a declared
> scope. D1 otherwise stands unchanged.

**Tracks:** `DeviceWatcher`, `IDeviceMonitorProvider`, `DeviceActivityStatus`, and any extension
package that can observe a device the OS cannot.

---

## Status

**Proposed.** No code.

---

## Context

- **CTX-001**: [ADR-0004](0004-two-level-device-state-model.md) gives two orthogonal axes.
  *Present* means the OS has an entry for this device. *Active* means the driver is started and
  the device is usable. [ADR-0088](0088-open-handles-on-activity-not-presence.md) D1 binds all I/O
  to the activity axis.

- **CTX-002**: On Windows the activity axis has one working direction.
  [ADR-0054](0054-windows-property-freshness-events-over-polling.md) established that cfgmgr32
  pushes no soft driver-stop signal, so `DeviceDeactivated` is never raised there (issue #15).
  ADR-0088 D4 states the consequence: a handle opened on activity is torn down only by physical
  removal, and a device that stops without leaving the tree produces no close edge at all.

- **CTX-003**: Bluetooth is the case where that is the normal course of events rather than an edge
  case. A peripheral going out of range stops without leaving the device tree.
  [ADR-0085](0085-the-32feet-binding-is-two-integration-packages.md) Context §1 measured that a
  poll over `BluetoothDeviceInfo.Connected` agrees with `DeviceInfo.IsActive` in both directions,
  and its Open Questions record the cost as p50 0.3 ms, p95 0.9 ms for one paired device at a 2 s
  cadence. The signal exists and is cheap. It has nowhere to go.

- **CTX-004**: ADR-0085 D7 §2 names the gap and declines to close it. Without a seam an
  integration package can expose the poll only as a consumer-driven `Task<bool>`, which reaches
  neither `DeviceTracker`, nor `DeviceProxyBase`'s reopen loop, nor the public `Activated` /
  `Deactivated` events. That ADR states plainly that the benefit "should not be claimed as
  delivered on the strength of this ADR alone."

- **CTX-005**: [ADR-0087](0087-reconcile-activity-at-the-watcher-boundary.md) D1 makes the watcher
  the authority on activity and reconciles a contradicting presence payload once, in
  `OnProviderAppeared`, against `_knownConnectedIds`. A poll-sourced edge delivered beside the
  watcher arrives second and loses. So whatever carries this signal has to reach consumers
  *through* the watcher.

- **CTX-006**: The existing seam nearly fits and then does not.
  [`IDeviceMonitorProvider`](../../src/Periphery/IDeviceProvider.cs) already declares exactly the
  four lifecycle edges plus `DevicePropertyChanged`, and `DeviceWatcher` already accepts an
  injected instance. But both of its constructors take **one** monitor provider, and
  `DeviceProviderFactory.GetMonitorProvider()` returns exactly one per OS. Admitting a second
  implementation of that interface admits a second source of *presence* edges, which is a much
  larger change than the one this ADR is for.

- **CTX-007**: A poll is not an edge. It samples a level at an instant and cannot know whether it
  witnessed a transition. `_knownConnectedIds` is the watcher's current belief and is already the
  arbiter at every lifecycle handler, under a lock, at eighteen sites in
  [`DeviceWatcher.cs`](../../src/Periphery/DeviceWatcher.cs).

- **CTX-008**: [ADR-0073](0073-observations-not-verdicts.md) already settled the general form of
  this problem in another domain: a component that can see something reports what it saw, and the
  library renders the verdict in one place rather than letting each observer assert one.

---

## Decision

### D1 — A new narrow interface, `IDeviceActivitySource`, not a second `IDeviceMonitorProvider`

```csharp
public interface IDeviceActivitySource : IAsyncDisposable
{
    Task StartAsync(CancellationToken ct = default);

    event EventHandler<DeviceActivityObservedEventArgs>? ActivityObserved;
}

public sealed class DeviceActivityObservedEventArgs : EventArgs
{
    public DeviceId Id { get; }
    public bool IsActive { get; }
    public DateTimeOffset ObservedAt { get; }
}
```

The axis confinement is the property that keeps this change bounded, so it is encoded in the type
rather than left to a convention a future implementer can read past. There is no presence edge to
raise because the interface declares none.

`DevicePropertyChanged` is out of scope for the same reason: a source that can rewrite arbitrary
properties is an enricher, and [ADR-0026](0026-enricher-io-boundary.md) already owns that.

### D2 — A source reports a level. The watcher derives the edge.

`IsActive` is a sampled state, not a transition. The watcher converts it by comparing against
`_knownConnectedIds` under the existing lock, exactly as `OnProviderActivated` and
`OnProviderDeactivated` already do:

- observed `true`, id not in the set → add, raise `Activated`
- observed `false`, id in the set → remove, raise `Deactivated`
- otherwise → no event

Three things fall out of this rather than needing to be designed. Repeated identical polls raise
nothing, because the set already holds the belief. Two sources polling at different cadences cannot
both claim the same edge. And a source needs no memory of what it reported last, which is what
makes a correct source small enough to be worth writing.

This is ADR-0073's shape: the source reports the observation, the watcher renders the verdict.

### D3 — Scoped authority, not global precedence

A source is registered with a `DeviceFilter` naming the devices it speaks for. Inside that scope
its most recent observation becomes the watcher's belief, and may contradict the OS. Outside it,
the observation is dropped.

This is the amendment to ADR-0087 D1, and it is deliberately the smallest one that works. D1 stays
true globally: the watcher is still the single authority, still the only place a verdict is
rendered, still the owner of `_knownConnectedIds`. What changes is that inside a declared scope it
will accept a supplementary observation over the platform provider's.

Global precedence in either direction is wrong. "OS always wins" makes the seam useless on the
platform that needs it, because CTX-002 says the OS never reports the close edge there. "Source
always wins" lets a stale poll tear down a working handle anywhere in the tree. Scoping bounds the
blast radius to the transport whose owner asked for it.

### D4 — Registration is explicit and additive, and core ships no source

Sources are supplied at watcher construction. Core ships no implementation and registers none by
default, so a `DeviceWatcher` built as it is today behaves byte-for-byte as it does today. That is
the property that makes this shippable ahead of any consumer.

### D5 — A source can never create presence, and the mechanism enforces it

An observation is keyed by `DeviceId`, which exists only for a device already in the tree. The
watcher resolves that id against `_deviceCache` — maintained on every lifecycle edge per ADR-0087
D3 — to build the `DeviceInfo` its events carry. An observation naming an id the cache does not
hold is dropped.

So a source cannot announce a device, cannot keep a removed device alive, and cannot reorder
against the startup walk, because it contributes nothing the walk reconciles. ADR-0087 D2's
ordering and ADR-0004's cascade rule are untouched.

### D6 — Cadence belongs to the source

Core owns no timer, sets no interval, and never polls on a source's behalf.
[ADR-0052](0052-periphery-treehopper-pure-core.md) puts clocks in the shell, and a source *is* a
shell. `StartAsync` and `DisposeAsync` bound its lifetime; what happens between them is its own
business. Core's only obligation is to stop consuming after disposal.

---

## Consequences

### Good

- ADR-0085 D7 §2's undischarged dependency is discharged. A 32feet poll reaches `DeviceTracker`,
  `DeviceProxyBase`'s reopen loop, and the public events, instead of stopping at a `Task<bool>`.
- Windows gains a close edge for Bluetooth for the first time. ADR-0088 D4's "no close edge at all"
  stops being unconditional for the one transport where it bites hardest.
- Default behaviour is unchanged. No source registered means no new code path taken.
- A correct source is small: no edge detection, no dedup, no memory of prior state.

### Bad

- A wrong source can tear down a healthy handle inside its declared scope. D3 bounds the blast
  radius; it does not eliminate it, and no design that lets a poll overrule the OS can.
- ADR-0087 D1 becomes conditional. "The watcher is the authority" is still true, but a reader now
  has to carry "and it arbitrates between sources inside declared scopes" with it.
- One more public interface on a core that ADR-0024 keeps deliberately small.

### Neutral

- No change for a browser/WASM backend
  ([exploration](../explorations/browser-wasm-device-backend-2026-09.md)). That is a full platform
  provider implementing both existing interfaces, not a supplementary source: its grant-scoped
  enumeration is still enumeration, and its `connect` / `disconnect` edges arrive through
  `IDeviceMonitorProvider` like any other provider's.
- No change to `IDeviceProvider`, `DeviceTracker`, `DeviceProxyBase`, or any enricher.

---

## Alternatives considered

**A second `IDeviceMonitorProvider` behind a fan-in composite.** The interface already carries the
right two edges and needs no new type. Rejected because it also carries `Appeared`, `Disappeared`
and `DevicePropertyChanged`, so admitting a second implementation admits a second enumeration
authority by construction. That pulls the startup walk ordering (ADR-0087 D2), `_deviceCache`
maintenance (D3), and ADR-0004's cascade rule into scope on three platforms, to deliver a signal
that only ever needed one axis.

**An enricher (ADR-0026).** Enrichers decorate a `DeviceInfo` during enumeration and enrichment.
They raise no edges and run at the wrong time. An enricher cannot tell a consumer that a device
went out of range ten seconds after it was enumerated.

**A cooperative observer at the tracker (ADR-0049).** Rejected by ADR-0087 D1: an edge that reaches
a tracker without passing through the watcher arrives after the watcher's own contradicting one and
loses. It would also have to be re-implemented per tracker type.

**Leave it as `Task<bool>` (status quo, ADR-0085 D7 §2).** This is what ships today if nothing is
done. It does not reach the two consumers that make the signal worth having.

---

## Open questions

- **Does a supplementary observation affect `EnumerateAsync`?** Enumeration does not pass through
  the watcher, so a snapshot's `IsActive` would still come from the platform provider while the
  watcher's belief differs. Either that divergence is documented as acceptable, or enumeration
  grows a reconcile step it does not have today.
- **Runtime registration.** D4 settles construction-time only. Whether a source can be added or
  removed while the watcher runs is deferred, because it interacts with the startup-walk ordering
  D5 currently sidesteps.
- **Staleness.** `ObservedAt` is carried but unused by D2. Should an observation expire, reverting
  the watcher to the platform provider's belief after some interval, or does a source that stops
  reporting simply leave the last value standing?
- **Overlapping scopes.** Two sources whose filters both match one device. Most-recent-wins is the
  obvious rule and nothing yet needs it; say so explicitly before a second source exists.
- **Does `MultiDeviceTracker` need anything?** Expected no, since it consumes watcher events like
  any other tracker, but unverified.

---

## Related

- [ADR-0085 — The 32feet binding is two integration packages](0085-the-32feet-binding-is-two-integration-packages.md) —
  D7 §2 creates this dependency and declines to discharge it.
- [ADR-0087 — Reconcile activity at the watcher boundary](0087-reconcile-activity-at-the-watcher-boundary.md) —
  D1 is what D3 amends; D2 and D3 are what D5 protects.
- [ADR-0088 — Open handles on activity, never on presence](0088-open-handles-on-activity-not-presence.md) —
  D4 states the missing close edge this seam exists to supply.
- [ADR-0054 — Windows property freshness events over polling](0054-windows-property-freshness-events-over-polling.md) —
  why the close edge is missing (issue #15).
- [ADR-0004 — Two-level device state model](0004-two-level-device-state-model.md) — the two axes,
  and the Bluetooth case that motivated splitting them.
- [ADR-0073 — Observations, not verdicts](0073-observations-not-verdicts.md) — the same shape,
  settled in another domain.
- [ADR-0026 — Enricher IO boundary](0026-enricher-io-boundary.md) — the seam for contributing
  *properties*, which this one deliberately is not.
- [Nordic DFU feature spec](../feature-specs/firmware-flashing/nordic-dfu/spec.md) — OQ-5 there is
  the first consumer that would need this.
- Issue [#274](https://github.com/charles8051/periphery/issues/274).
