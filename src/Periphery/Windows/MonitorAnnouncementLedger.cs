// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.Generic;
using System.Runtime.Versioning;

namespace Periphery.Windows;

/// <summary>
/// Records, per monitor instance id, whether the provider has already raised the
/// appearance event that lets a consumer's tracker resolve the device — the
/// ordering precondition ADR-0066 Decision 2a exists to guarantee.
///
/// <para><b>Why this replaces a lock.</b> The invariant needed is
/// <i>"a <c>DevicePropertyChanged</c> for a monitor must never precede the
/// <c>DeviceAppeared</c>/<c>DeviceActivated</c> that makes it applicable"</i>
/// (the tracker drops a property change for an unresolved device, and the cache
/// would then diff to nothing and never re-emit — issue #149). The first
/// implementation enforced it by holding a provider-wide gate across the raising
/// of those events. That is mutual exclusion standing in for ordering, and it put
/// an internal lock around synchronous consumer callbacks, which could stall the
/// OS <c>WM_DISPLAYCHANGE</c> broadcast that the sink's own pump thread has to
/// service (issue #153). This ledger states the precondition as data instead, so
/// every event can be raised with no lock held.</para>
///
/// <para><b>Not thread-safe by design.</b> The provider guards every call with its
/// cache lock, which is the same lock that guards the snapshot the eligibility
/// answer is about — asking "is this monitor announced?" and acting on the answer
/// have to be one atomic step, so a second lock here would only add a nesting
/// order to get wrong. Nothing in this type calls out, so the provider's lock is
/// never held across foreign code.</para>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class MonitorAnnouncementLedger
{
    // Monitors whose appearance has been raised at least once.
    private readonly HashSet<string> _announced = new(StringComparer.OrdinalIgnoreCase);

    // Publishes currently in flight, by id. A single plug can publish the same
    // monitor twice concurrently — the interface-arrival and instance-started
    // notifications both fire, on different cfgmgr32 callback threads — so this
    // is a depth, not a flag: the monitor is only eligible again once the LAST
    // in-flight publish has raised its events.
    private readonly Dictionary<string, int> _publishing = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Marks a monitor as already announced without a publish — for the
    /// <c>StartAsync</c> cache seed, whose devices are announced to consumers by
    /// the watcher's own startup snapshot (which runs the enrichment pipeline)
    /// rather than by a provider event.
    /// </summary>
    internal void MarkAnnounced(string id) => _announced.Add(id);

    /// <summary>
    /// A publish (cache write + appearance events) has started for this monitor.
    /// It is not refresh-eligible again until the matching
    /// <see cref="EndPublish"/>.
    /// </summary>
    internal void BeginPublish(string id) =>
        _publishing[id] = _publishing.TryGetValue(id, out int depth) ? depth + 1 : 1;

    /// <summary>
    /// The publish finished — its appearance events have been raised, so the
    /// monitor is announced and (once no other publish is in flight) eligible for
    /// a refresh delta again.
    /// <para>A publish for a monitor that was <see cref="Forget"/>ten while it was
    /// in flight — removed concurrently — is deliberately <b>not</b> re-announced:
    /// a removed monitor must not be resurrected into the eligible set.</para>
    /// </summary>
    internal void EndPublish(string id)
    {
        if (!_publishing.TryGetValue(id, out int depth))
            return;

        if (depth <= 1)
            _publishing.Remove(id);
        else
            _publishing[id] = depth - 1;

        _announced.Add(id);
    }

    /// <summary>
    /// Whether a <c>DevicePropertyChanged</c> delta for this monitor can be
    /// written back and raised right now: true only once its appearance has been
    /// raised and no publish is mid-flight.
    ///
    /// <para>A monitor that is <b>not</b> eligible is skipped entirely — neither
    /// written back nor raised. Skipping the write-back is the load-bearing half:
    /// enriching the cache without emitting would leave the next refresh diffing
    /// to nothing, which is precisely how the enrichment used to get lost. The
    /// publish that made it ineligible always requests a refresh once it is done,
    /// so a skipped monitor is re-driven rather than dropped.</para>
    /// </summary>
    internal bool IsRefreshEligible(string id) =>
        _announced.Contains(id) && !_publishing.ContainsKey(id);

    /// <summary>Drops all state for a removed monitor.</summary>
    internal void Forget(string id)
    {
        _announced.Remove(id);
        _publishing.Remove(id);
    }
}
