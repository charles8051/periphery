// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace Periphery;

/// <summary>
/// Process-wide registry of <see cref="IDeviceEnricher"/> implementations
/// run against every <see cref="DeviceInfo"/> emitted by core enumeration.
/// Extension packages register their enrichers at module init
/// (mirroring <c>HidQuirks</c>'s baseline-registration pattern from
/// ADR-0048); the provider pipeline reads <see cref="Snapshot"/> per
/// enumeration and invokes each enricher whose
/// <see cref="IDeviceEnricher.CanEnrich"/> returns <c>true</c>.
/// </summary>
/// <remarks>
/// <para><b>Mutates process-wide state.</b> Same trade-off as
/// <c>HidQuirks</c> — tests that mutate the registry must coordinate
/// (use an xUnit <c>[Collection]</c> to serialise, and call
/// <see cref="Unregister"/> in tear-down). Production code typically
/// only calls <see cref="Register"/> once per assembly via a
/// <c>[ModuleInitializer]</c>.</para>
/// <para><b>Lock-free reads.</b> <see cref="Snapshot"/> returns the
/// current immutable array; the provider pipeline iterates the
/// snapshot without holding any lock. Writes serialise through a
/// private gate.</para>
/// </remarks>
public static class DeviceEnrichers
{
    private static ImmutableArray<IDeviceEnricher> _enrichers = ImmutableArray<IDeviceEnricher>.Empty;
    private static readonly object _gate = new();

    /// <summary>
    /// Registers <paramref name="enricher"/> so subsequent enumerations
    /// invoke it. Idempotent — re-registering the same instance is a
    /// no-op; equality is reference-based.
    /// </summary>
    public static void Register(IDeviceEnricher enricher)
    {
        ArgumentNullException.ThrowIfNull(enricher);
        lock (_gate)
        {
            if (_enrichers.Contains(enricher)) return;
            _enrichers = _enrichers.Add(enricher);
        }
    }

    /// <summary>
    /// Removes a previously-registered enricher. Returns <c>true</c>
    /// when the instance was present and removed; <c>false</c> when it
    /// wasn't registered. Reference-based.
    /// </summary>
    public static bool Unregister(IDeviceEnricher enricher)
    {
        ArgumentNullException.ThrowIfNull(enricher);
        lock (_gate)
        {
            var updated = _enrichers.Remove(enricher);
            if (updated.Length == _enrichers.Length) return false;
            _enrichers = updated;
            return true;
        }
    }

    /// <summary>
    /// Current snapshot of registered enrichers. Safe to iterate after
    /// the call returns — the returned array is immutable.
    /// </summary>
    public static ImmutableArray<IDeviceEnricher> Snapshot() => _enrichers;

    /// <summary>
    /// Merges the <see cref="EnricherScope"/> of every registered
    /// <see cref="ITagEmittingEnricher"/> that emits at least one tag in
    /// <paramref name="tags"/>. The result is the union of those enrichers'
    /// per-platform OS enumeration tokens — what a provider would add to an
    /// otherwise category-less query so a tag filter (for example
    /// <c>WithTag("Printer")</c>) scans only the relevant subsystems instead
    /// of every device (ADR-0051 §5).
    /// </summary>
    /// <remarks>
    /// Returns <see cref="EnricherScope.None"/> when <paramref name="tags"/> is
    /// empty or no registered enricher claims any of the tags. Lock-free —
    /// reads the same immutable snapshot <see cref="Snapshot"/> exposes. Landed
    /// inert: no provider consults it yet; activation lands per-category in the
    /// ADR-0051 demotion rollout.
    /// </remarks>
    /// <param name="tags">The capability tags a query filters on — typically
    /// <see cref="DeviceFilter.RelevantTags"/>.</param>
    public static EnricherScope ScopeForTags(IReadOnlySet<string> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        if (tags.Count == 0) return EnricherScope.None;

        HashSet<string>? windows = null;
        HashSet<string>? linux = null;
        HashSet<string>? macOS = null;

        var enrichers = _enrichers;
        for (int i = 0; i < enrichers.Length; i++)
        {
            if (enrichers[i] is not ITagEmittingEnricher tagger) continue;
            if (!EmitsAnyOf(tagger.EmitsTags, tags)) continue;

            var scope = tagger.Scope;
            Accumulate(ref windows, scope.WindowsClassGuids, StringComparer.OrdinalIgnoreCase);
            Accumulate(ref linux, scope.LinuxSubsystems, StringComparer.Ordinal);
            Accumulate(ref macOS, scope.MacOSClasses, StringComparer.Ordinal);
        }

        if (windows is null && linux is null && macOS is null)
            return EnricherScope.None;

        return new EnricherScope(
            windows is null ? [] : [.. windows],
            linux is null ? [] : [.. linux],
            macOS is null ? [] : [.. macOS]);
    }

    private static bool EmitsAnyOf(IReadOnlySet<string> emitted, IReadOnlySet<string> wanted)
    {
        // Probe membership against the larger set, iterate the smaller one.
        if (emitted.Count <= wanted.Count)
        {
            foreach (var tag in emitted)
                if (wanted.Contains(tag)) return true;
        }
        else
        {
            foreach (var tag in wanted)
                if (emitted.Contains(tag)) return true;
        }
        return false;
    }

    private static void Accumulate(ref HashSet<string>? set, IReadOnlyList<string> tokens, StringComparer comparer)
    {
        if (tokens.Count == 0) return;
        set ??= new HashSet<string>(comparer);
        for (int i = 0; i < tokens.Count; i++)
            set.Add(tokens[i]);
    }
}
