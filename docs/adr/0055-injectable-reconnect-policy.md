---
title: "ADR-0055: Injectable reconnect policy for DeviceProxyBase"
status: "Accepted"
date: "2026-06-11"
authors: "@charles8051 (design)"
tags: ["architecture", "decision"]
supersedes: ""
superseded_by: ""
---

# ADR-0055: Injectable reconnect policy for DeviceProxyBase

**Tracks:** `DeviceProxyBase<TDevice,TException>`, `DeviceTracker`; (cross-repo) a downstream kiosk consumer's `TreehopperLedStripRenderer`
**Related:** ADR-0046 (runtime tracker reconfigure), ADR-0049 (DeviceTracker cooperative observers)

> **Number provisional.** Per this repo's convention the ADR number is assigned at merge; renumber if `0055` is taken by a parallel branch.

> **Amendment (2026-06-11).** Initial landing wired the seam into
> `DeviceProxyBase<TDevice,TException>` (and thus `DeviceProxy<TDevice>`, which
> derives from it), but the **non-generic `DeviceProxy`** — a standalone third
> hand-copy of the reconnect loop on a hardcoded backoff — was left as a
> follow-up. That fold is now **done**: the non-generic `DeviceProxy` derives from
> `DeviceProxyBase<DeviceProxy.Sentinel, Exception>` (an inert `IAsyncDisposable`
> sentinel carries the `DeviceInfo` to the closure hooks; the `onActivated` gate
> runs in the overridden `OpenDeviceAsync` so its throw still raises `OpenFailed`).
> It inherits the injectable policy, `State`/`GaveUp`/`LastOpenFault`, and
> re-enumeration reset; its duplicate `s_reconnectBackoff`/`ReconnectAsync`/
> `RunWorkerAsync`/`RequestReconnect` are deleted. The reconnect loop now lives in
> exactly one place. See ADR-0027 amendment #3.
>
> **Amendment (2026-06-11, #2).** The seam is now **forwarded through the session-host
> layer**. `DeviceSessionHost<TSession>` wraps `DeviceProxy<SessionLease<TSession>>`
> and previously built that inner proxy with no policy, silently pinning every
> session host to `ExponentialBackoffReconnectPolicy.Default` (retry forever) with
> no way to surface `GaveUp`. The reconnect policy is a **device-level** concept —
> it governs the underlying device handle's reopen cadence — and the session host
> is plumbing, so the fix is a *forward*, not a new "session retry policy": both
> `DeviceSessionHost` factories (`StartAsync`/`ForDeviceAsync`/`Create`) and both
> `MultiDeviceSessionHost` factories (`StartAsync`/`Create`) gain an optional
> `IReconnectPolicy? reconnectPolicy = null` that flows down to the inner
> `DeviceProxy.Create(...)` (per-device, fanned out in the multi-host). Default
> `null` preserves byte-for-byte prior behavior. Two things are surfaced **outward**
> so the session-host cohort feeds the same health evaluator as the
> `DeviceProxy`-direct cohort: a `DeviceSessionHost.ConnectionState` property
> projecting the inner proxy's `State` (`Disconnected` before the handle attaches),
> and a new **terminal** `HostStatus` discriminant `SessionGaveUp<TSession>(LastError,
> Attempt)` — distinct from the transient `SessionUnavailable` — driven by observing
> the inner proxy's `PropertyChanged(State)` for `ConnectionState.GaveUp` (the
> give-up transition fires from the proxy's background reconnect loop, not from any
> open/close/tracker edge the host already watched, so the host now subscribes to
> that `PropertyChanged`). `SessionGaveUp` deliberately omits the `DeviceInfo` the
> transient states carry: the give-up is reported off an async state change where a
> non-null snapshot is not reliably threadable, the device remains reachable via
> `host.DeviceInfo`, and consumers switch on the discriminant / `ConnectionState`.
> This closes the same false-healthy gap reported by a downstream kiosk consumer, for the session
> cohort that the base seam closed for the direct cohort. That consumer's wiring onto
> this is the follow-up, gated on a Periphery release.

---

## Context

`DeviceProxyBase<TDevice,TException>` is Periphery's shared "track a device, open a
session, supervise it, reconnect on loss" base. Camera, mechanism (Phidget),
barcode, and HID sessions all derive from it. It owns the reconnect lifecycle:
open (`OpenDeviceAsync` + `OnActivatedAsync`) → run `WhileOpenAsync` until a
non-cancellation fault → close → reconnect. The reconnect cadence is a
**hardcoded static array** (`ReconnectAsync`, `DeviceProxyBase.cs`):

```csharp
private static readonly TimeSpan[] s_reconnectBackoff =
    [ 1.Seconds(), 2.Seconds(), 4.Seconds(), 5.Seconds() ];   // clamps at the last entry

// ReconnectAsync: loop while not disposed && !IsOpen && tracker.IsActive
await Task.Delay(s_reconnectBackoff[Math.Min(attempt, s_reconnectBackoff.Length - 1)], ct);
```

Three gaps fall out of that shape:

1. **Backoff is hardcoded and uniform.** A 30 Hz LED strip, a UPS polled every
   10 s, a camera, and a barcode scanner all get the same 1/2/4/5 s schedule.
   A consumer cannot tune it without forking the base.
2. **No give-up; no terminal state.** The loop retries **forever** at the 5 s cap
   for as long as the OS still enumerates the device. A *present-but-unopenable*
   device — e.g. a kiosk Treehopper LED board whose EFM8 SPI FIFO wedges
   ([#93](https://github.com/charles8051/periphery/issues/93)): Windows enumerates it, but every `OpenAsync` reconcile
   times out on a wedged bulk endpoint until a physical power-cycle — is
   re-attempted every 5 s indefinitely. There is no way to express "stop after N
   and stay disconnected until the device re-enumerates."
3. **No observability of the failing-to-open state.** `IsOpen` is a bare bool;
   there is no "connecting / repeatedly-failing / gave-up" signal a health probe
   can read. This is exactly the false-healthy gap that downstream report
   raised: an enumerated-but-unopenable device reads healthy because presence and
   openability are conflated.

Because of (2) and (3), the kiosk's `TreehopperLedStripRenderer` **bypasses
`DeviceProxyBase` entirely** and hand-rolls its own reconnect state machine
(`MaxOpenRetries = 5`, exponential backoff `min(15 s, 2^n)`, a
give-up-until-tracker-event budget, plus a pessimistic open-timeout guard). So
there are **two divergent reconnect implementations** — Periphery's
(retry forever) and the kiosk's (give up) — and the decision that matters most
(*when to stop*) lives in the wrong layer, duplicated.

The obvious reach is [Polly](https://github.com/App-vNext/Polly). But retry count,
backoff curve, and circuit-breaking are **policy**, and policy is a consumer
concern: a CLI wants fail-fast, the kiosk wants give-up-until-replug, a server
might retry hard. Baking a Polly dependency — and one policy — into Periphery
core would impose one app's choice on every consumer and couple the library to
Polly's version line. It also cuts against the "separate state / IO /
timing" preference: reconnect timing is the imperative shell, not the device-IO
mechanism. Periphery already draws this line correctly elsewhere (it owns the
per-transfer watchdog — "don't hang" — but not retry policy).

## Decision

Replace the hardcoded `s_reconnectBackoff` with a **BCL-only reconnect-policy
seam** that `DeviceProxyBase` consults. Periphery defines the contract and ships
a sane default; **consumers inject the policy** (Polly-backed or otherwise). No
third-party type crosses the Periphery boundary.

### 1. `IReconnectPolicy` (new, `Periphery` namespace, no third-party deps)

```csharp
namespace Periphery;

/// <summary>
/// Decides the cadence of reconnect attempts after a device session fails to
/// open or drops. Injected into <see cref="DeviceProxyBase{TDevice,TException}"/>;
/// the library owns the worker lifecycle, the policy owns only timing + give-up.
/// </summary>
public interface IReconnectPolicy
{
    /// <summary>
    /// Called after the Nth consecutive failure. Return the delay before the next
    /// attempt, or <see langword="null"/> to STOP retrying — the proxy transitions
    /// to <see cref="ConnectionState.GaveUp"/> and stays there until the device
    /// re-enumerates (which resets <see cref="ReconnectContext.Attempt"/> to 1).
    /// Must honor <paramref name="ct"/>.
    /// </summary>
    ValueTask<TimeSpan?> NextDelayAsync(ReconnectContext context, CancellationToken ct);
}

/// <summary>Inputs to a reconnect decision. A pure value; same input → same decision.</summary>
public readonly record struct ReconnectContext(
    int Attempt,             // 1-based consecutive-failure count; resets to 1 on re-enumeration
    Exception? LastFault,    // fault that closed a live session, or null for a plain open-failure
    DeviceInfo Device);      // the still-enumerated device we are failing to open
```

### 2. Default policy — preserves today's behavior unless a consumer opts in

```csharp
public sealed class ExponentialBackoffReconnectPolicy(
    TimeSpan baseDelay, TimeSpan maxDelay, int? maxAttempts = null) : IReconnectPolicy
{
    /// Reproduces the legacy 1→2→4→5 s (capped) curve, unbounded — same as s_reconnectBackoff.
    public static readonly IReconnectPolicy Default =
        new ExponentialBackoffReconnectPolicy(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));

    public ValueTask<TimeSpan?> NextDelayAsync(ReconnectContext ctx, CancellationToken ct)
    {
        if (maxAttempts is { } max && ctx.Attempt > max)
            return new((TimeSpan?)null);                                   // give up
        var exp = baseDelay * Math.Pow(2, ctx.Attempt - 1);
        return new(exp < maxDelay ? exp : maxDelay);
    }
}
```

The base class defaults to `ExponentialBackoffReconnectPolicy.Default`, so
existing derived proxies behave as before (retry forever) until a consumer
passes a bounded policy.

### 3. `DeviceProxyBase` consults the policy and gains a terminal state

`ReconnectAsync` replaces the array index with `policy.NextDelayAsync(...)`; a
`null` return ends the loop in `ConnectionState.GaveUp` instead of re-arming. A
tracker re-enumeration (`OnTrackerStateChanged` active) clears `GaveUp`, resets
the attempt counter, and re-opens — the existing reset point, made explicit.

### 4. Observable connection state (feeds the downstream false-healthy report)

```csharp
public enum ConnectionState { Disconnected, Connecting, Open, GaveUp }

public ConnectionState State { get; private set; }   // raises PropertyChanged (already INotifyPropertyChanged)
public Exception? LastOpenFault { get; private set; }
```

`GaveUp` is the "enumerated but unopenable" signal a health probe maps to
`Degraded`/`Unhealthy` — closing the `#160` gap for free, regardless of which
policy is injected. `IsOpen` becomes `State == Open` (keep it as a convenience).

### 5. Polly stays entirely consumer-side

A consumer that wants jitter / decorrelated backoff / a circuit breaker
implements `IReconnectPolicy` over Polly in *their* assembly. Periphery
references no Polly and pins no Polly version.

### 6. Retire the kiosk's bespoke reconnect (follow-up)

Once the seam lands, that consumer's `TreehopperLedStripRenderer` migrates its
`MaxOpenRetries`/backoff/give-up onto `DeviceProxyBase` + an injected
(bounded, exponential) policy, collapsing the two implementations into one. Its
pessimistic open-timeout guard is orthogonal — see Consequences.

## Implementation sketch

`DeviceProxyBase` — the reconnect loop, before/after:

```csharp
// + injected; defaults to the built-in so nothing regresses.
private readonly IReconnectPolicy _reconnectPolicy;   // ctor param ?? ExponentialBackoffReconnectPolicy.Default

private async Task ReconnectAsync()
{
    try
    {
        int attempt = 0;
        while (!_disposed && !IsOpen && _tracker.IsActive)
        {
            var deviceInfo = _tracker.Device;
            if (deviceInfo is null) return;
            attempt++;

            TimeSpan? delay;
            try
            {
                delay = await _reconnectPolicy.NextDelayAsync(
                    new ReconnectContext(attempt, _lastFault, deviceInfo),
                    _disposeCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }

            if (delay is null)                       // policy gave up
            {
                SetState(ConnectionState.GaveUp);    // -> health Unhealthy; wait for re-enumeration
                return;
            }

            SetState(ConnectionState.Connecting);
            try { await Task.Delay(delay.Value, _disposeCts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            if (_disposed || IsOpen || !_tracker.IsActive) return;
            if (await TryOpenDeviceAsync(deviceInfo, requestReconnectOnFailure: false).ConfigureAwait(false))
                return;                              // success -> TryOpen sets State = Open
        }
    }
    finally
    {
        Interlocked.Exchange(ref _reconnectInProgress, 0);
        // Only re-arm if we didn't deliberately give up.
        if (!_disposed && !IsOpen && _tracker.IsActive && State != ConnectionState.GaveUp)
            RequestReconnect();
    }
}

// OnTrackerStateChanged(active): reset on re-enumeration so a power-cycled/replugged
// device gets a fresh budget.
//   State == GaveUp -> SetState(Disconnected); _lastFault = null; then TryOpenDeviceAsync(device).
```

Consumer side — Polly lives **here**, not in Periphery:

```csharp
// Bounded give-up + exponential backoff + jitter, expressed however the app likes.
// (Use Polly's Backoff.DecorrelatedJitterBackoffV2 / a breaker's CircuitState, or hand-roll.)
sealed class KioskReconnectPolicy : IReconnectPolicy
{
    public ValueTask<TimeSpan?> NextDelayAsync(ReconnectContext ctx, CancellationToken ct)
    {
        if (ctx.Attempt > 5) return new((TimeSpan?)null);                  // -> GaveUp -> #160 Unhealthy
        var secs = Math.Min(15, Math.Pow(2, ctx.Attempt - 1));
        var jitter = Random.Shared.Next(0, 250);
        return new(TimeSpan.FromSeconds(secs) + TimeSpan.FromMilliseconds(jitter));
    }
}

// registration: new TreehopperProxy(tracker, reconnectPolicy: new KioskReconnectPolicy());
```

## Alternatives considered

- **(A) Hard Polly dependency in `Periphery` core, applying a default policy.**
  Rejected: imposes one app's policy on all consumers, couples the library to
  Polly's major-version line, and fuses timing/policy into device-IO mechanism.
- **(B) A separate `Periphery.Resilience` package built on Polly.** Viable and
  compatible with this seam (it would just ship a Polly-backed `IReconnectPolicy`
  + DI helpers), but not required and deferred. The seam delivers the win without
  it; add it only if a ready-made Polly adapter earns its keep.
- **(C) Inject an *executor* delegate** (`Func<Func<CancellationToken,Task>, …>`)
  and let the consumer wrap the whole open+run cycle in a Polly pipeline.
  Rejected as the primary shape: it inverts control of the worker loop, ceding
  cancellation/disposal and — critically — the *reset-on-re-enumeration* timing to
  the executor. The delay-seam keeps `DeviceProxyBase` owning the lifecycle and
  only delegates the timing decision, which is the smaller, safer change.

## Consequences

- **+ Per-device-class tunability.** Each proxy can carry a policy that fits its
  cadence and failure modes.
- **+ Expressible give-up + terminal state.** Fixes infinite-retry against a
  present-but-wedged device; the proxy can stop and stay stopped until replug.
- **+ Observable `State`/`LastOpenFault`.** Hands that report a real
  session-openability signal essentially for free.
- **+ Polly opt-in with zero library dependency** and no version coupling.
- **+ One reconnect implementation.** The kiosk's bespoke state machine folds
  back onto the shared base; so does the non-generic `DeviceProxy`, which used to
  carry its own copy of the loop (now derives from `DeviceProxyBase` — see the
  2026-06-11 amendment above and ADR-0027 amendment #3).
- **− New surface:** an interface, an options type, a state enum, a ctor param.
  Acceptable under Periphery's no-external-consumers / breaking-changes-fine
  stance; no compat shims.
- **− Migration:** the downstream Treehopper renderer must move onto the seam
  (breaking; fine per stance). Tracked as the follow-up to those downstream reports.
- **Out of scope — per-attempt open timeout.** This seam governs *between-attempt*
  backoff and give-up, not the *duration* of a single open. The un-cancellable
  native-open guard (the kiosk renderer's `Task.WhenAny` wall-clock timeout) stays
  a derived-class concern, or becomes a separate `OpenTimeout` option in a
  follow-up. Don't conflate the two — a slow-but-progressing open is not a
  reconnect decision.
