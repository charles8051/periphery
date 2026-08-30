// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.Generic;
using System.Runtime.Versioning;

namespace Periphery.Windows;

/// <summary>
/// Pure helpers for the Windows monitor DisplayConfig-enrichment refresh path
/// (issue #149). Both functions are total value transforms with no IO, no OS
/// calls, and no mutable state — the imperative shell
/// (<see cref="WindowsDeviceMonitorProvider"/>) owns the cache, the enricher,
/// and the event raising; this class owns only the value logic, so it is unit
/// testable without any display hardware.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsMonitorEnrichment
{
    /// <summary>
    /// Carries the DisplayConfig-tier fields forward from a prior cached
    /// snapshot onto a freshly-built arrival payload, for Monitor-category
    /// devices only, filling <b>only</b> fields the arrival left null.
    ///
    /// <para>The hotplug arrival path (<c>TryBuildDeviceInfo</c>) never runs the
    /// DisplayConfig enricher, so a re-appearance of a monitor that a prior
    /// <c>WM_DISPLAYCHANGE</c> refresh already enriched would otherwise emit a
    /// <c>DeviceAppeared</c>/<c>DeviceActivated</c> payload with
    /// <see cref="DeviceInfo.MonitorName"/> / <see cref="DeviceInfo.DisplayResolution"/> /
    /// <see cref="DeviceInfo.DisplayBounds"/> / connector all null, clobbering the
    /// known values. Merging here — in the provider shell, before either event is
    /// raised — means both the appeared and activated payloads carry the
    /// enrichment, so no downstream transition can drop it.</para>
    ///
    /// <para>Because the arrival path never measures these fields, an arrival
    /// null always means "unmeasured", never "measured-empty" — so filling from
    /// the prior can never mask a legitimate clear. Every field the arrival
    /// <i>does</i> supply still wins.</para>
    ///
    /// <para>Covers <b>every</b> monitor-tier field on <see cref="DeviceInfo"/>,
    /// not just the ones <c>WindowsDisplayConfigEnricher</c> populates today. The
    /// arrival path measures none of them, so carrying all of them forward is
    /// equally safe and means a future enricher (HDR luminance, DPI, physical
    /// size) does not silently reopen #149 for its field.
    /// <c>WindowsMonitorEnrichmentTests</c> pins this set against the
    /// <see cref="DeviceInfo"/> property list by reflection, so adding a monitor
    /// field there fails the test until it is carried here.</para>
    /// </summary>
    internal static DeviceInfo MergeArrival(DeviceInfo arrival, DeviceInfo prior)
    {
        if (arrival.Category != DeviceCategory.Monitor)
            return arrival;

        return arrival with
        {
            MonitorName                  = arrival.MonitorName                  ?? prior.MonitorName,
            DisplayResolution            = arrival.DisplayResolution            ?? prior.DisplayResolution,
            DisplayBounds                = arrival.DisplayBounds                ?? prior.DisplayBounds,
            DisplayOrientation           = arrival.DisplayOrientation           ?? prior.DisplayOrientation,
            DisplayPhysicalConnector     = arrival.DisplayPhysicalConnector     ?? prior.DisplayPhysicalConnector,
            DisplayConnectionKind        = arrival.DisplayConnectionKind        ?? prior.DisplayConnectionKind,
            DisplayUsageKind             = arrival.DisplayUsageKind             ?? prior.DisplayUsageKind,
            DisplayPhysicalSizeInInches  = arrival.DisplayPhysicalSizeInInches  ?? prior.DisplayPhysicalSizeInInches,
            DisplayDpi                   = arrival.DisplayDpi                   ?? prior.DisplayDpi,
            DisplayMaxLuminanceInNits    = arrival.DisplayMaxLuminanceInNits    ?? prior.DisplayMaxLuminanceInNits,
            DisplayMaxAvgLuminanceInNits = arrival.DisplayMaxAvgLuminanceInNits ?? prior.DisplayMaxAvgLuminanceInNits,
            DisplayMinLuminanceInNits    = arrival.DisplayMinLuminanceInNits    ?? prior.DisplayMinLuminanceInNits,
        };
    }

    /// <summary>
    /// Given the cached device snapshots and an <paramref name="enrich"/>
    /// function (the DisplayConfig enricher's <c>Enrich</c>), returns the
    /// (previous, enriched) pairs for Monitor-category devices whose enriched
    /// snapshot actually differs from the cached one. Pure: the caller decides
    /// what to do with the deltas (write back, raise events) under its own lock.
    /// </summary>
    internal static IReadOnlyList<(DeviceInfo Previous, DeviceInfo Current)> ComputeDeltas(
        IEnumerable<DeviceInfo> cached,
        Func<DeviceInfo, DeviceInfo> enrich)
    {
        var deltas = new List<(DeviceInfo, DeviceInfo)>();
        foreach (var previous in cached)
        {
            if (previous.Category != DeviceCategory.Monitor)
                continue;

            DeviceInfo current = enrich(previous);
            if (DeviceInfoDiff.Compute(previous, current).Count > 0)
                deltas.Add((previous, current));
        }
        return deltas;
    }
}
