// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.Generic;
using System.Runtime.Versioning;

namespace Periphery.Monitor.Windows;

/// <summary>
/// The <c>SetDisplayConfig</c> half of ADR-0059: mutates the queried
/// path/mode arrays toward the desired per-monitor state and submits them as
/// one transaction — <c>SDC_VALIDATE</c> first (fail loudly, never blank the
/// panel on a bad request), then <c>SDC_APPLY</c> with optional
/// <c>SDC_SAVE_TO_DATABASE</c> persistence.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class CcdLayoutApplier
{
    internal static unsafe MonitorLayoutApplyResult Apply(
        IReadOnlyList<MonitorConfiguration> desired, MonitorLayoutApplyOptions options)
    {
        var topology = CcdLayout.Read();

        if (topology.Layout.Monitors.IsEmpty)
        {
            throw new MonitorDeviceNotFoundException(
                "No active display paths in this session — the machine is headless, this is a "
                + "non-interactive session (display configuration is session-local, ADR-0058 "
                + "OQ-004), or the OS reports zero paths (Win10 IoT/LTSC, ADR-0044). There is "
                + "nothing a topology apply can target; treat as no-display (ADR-0059 D4).");
        }

        foreach (var config in desired)
        {
            if (!topology.PathIndexByDeviceId.ContainsKey(config.DeviceId))
                throw new MonitorDeviceNotFoundException(
                    $"Monitor '{config.DeviceId}' is not in this session's active topology. "
                    + $"Active: {string.Join(", ", topology.PathIndexByDeviceId.Keys)}.",
                    config.DeviceId);
        }

        // Idempotence first — the convergence-loop common case never touches CCD.
        if (LayoutDiff.IsSatisfiedBy(topology.Layout, desired))
            return new MonitorLayoutApplyResult(
                MonitorLayoutApplyOutcome.AlreadySatisfied, topology.Layout);

        var paths = topology.Paths;
        var modes = topology.Modes;
        var finalPositions = LayoutDiff.ResolvePositions(topology.Layout, desired);

        // Per-monitor mode/orientation mutations.
        foreach (var config in desired)
        {
            int pathIdx = topology.PathIndexByDeviceId[config.DeviceId];
            uint srcIdx = paths[pathIdx].SourceInfo.ModeInfoIdx;
            if (srcIdx == MonitorInterop.DISPLAYCONFIG_PATH_MODE_IDX_INVALID)
                throw new MonitorLayoutRejectedException(
                    $"Monitor '{config.DeviceId}' has no source mode entry to mutate.", -1);

            MonitorLayoutEntry entry = Find(topology.Layout, config.DeviceId);

            if (config.Orientation is { } orientation)
            {
                // The CCD source mode is the panel's NATIVE (unrotated) frame and
                // stays native across a rotation (#137 / ADR-0064): a native
                // 1920x1080 surface rotated 90° displays as a 1080x1920 portrait
                // desktop, so the OS derives the on-desktop footprint from the
                // source mode PLUS this rotation field. We therefore set ONLY the
                // rotation and never transpose the source dimensions — the
                // pre-#137 "source is desktop-space, swap it" rule was the same
                // misconception the read model was corrected for.
                paths[pathIdx].TargetInfo.Rotation = CcdOrientation.ToCcdRotation(orientation);
            }

            if (config.Mode is { } mode)
            {
                // Mode is the native/source frame (matches CurrentMode), so the
                // source dimensions are set verbatim — no orientation swap.
                modes[srcIdx].SourceWidth = (uint)mode.Width;
                modes[srcIdx].SourceHeight = (uint)mode.Height;
                if (mode.RefreshRateHz > 0
                    && mode.RefreshRateHz != entry.CurrentMode.RefreshRateHz)
                {
                    paths[pathIdx].TargetInfo.RefreshRate = new MonitorInterop.DisplayConfigRational
                    {
                        Numerator = (uint)mode.RefreshRateHz,
                        Denominator = 1,
                    };

                    // The supplied config must stay complete: a strict
                    // SDC_VALIDATE rejects an invalidated target mode index
                    // outright (ERROR_GEN_FAILURE 31 on the bench). Scale the
                    // existing target video timing to the new vertical rate
                    // instead — hSync and pixel rate move linearly with
                    // vSync for an unchanged raster.
                    uint tgtIdx = paths[pathIdx].TargetInfo.ModeInfoIdx;
                    if (tgtIdx != MonitorInterop.DISPLAYCONFIG_PATH_MODE_IDX_INVALID
                        && tgtIdx < modes.Length
                        && modes[tgtIdx].TargetVSyncFreq.Denominator != 0
                        && modes[tgtIdx].TargetVSyncFreq.Numerator != 0)
                    {
                        double oldHz = modes[tgtIdx].TargetVSyncFreq.Numerator
                            / (double)modes[tgtIdx].TargetVSyncFreq.Denominator;
                        double scale = mode.RefreshRateHz / oldHz;

                        modes[tgtIdx].TargetVSyncFreq = new MonitorInterop.DisplayConfigRational
                        {
                            Numerator = (uint)mode.RefreshRateHz,
                            Denominator = 1,
                        };
                        if (modes[tgtIdx].TargetHSyncFreq.Denominator != 0)
                        {
                            modes[tgtIdx].TargetHSyncFreq = new MonitorInterop.DisplayConfigRational
                            {
                                Numerator = (uint)Math.Round(
                                    modes[tgtIdx].TargetHSyncFreq.Numerator * scale),
                                Denominator = modes[tgtIdx].TargetHSyncFreq.Denominator,
                            };
                        }
                        modes[tgtIdx].TargetPixelRate =
                            (ulong)Math.Round(modes[tgtIdx].TargetPixelRate * scale);
                    }
                }
            }
        }

        // Whole-topology positions (explicit positions + primary translation).
        foreach ((string deviceId, DisplayPosition position) in finalPositions)
        {
            if (!topology.PathIndexByDeviceId.TryGetValue(deviceId, out int pathIdx))
                continue;
            uint srcIdx = paths[pathIdx].SourceInfo.ModeInfoIdx;
            if (srcIdx == MonitorInterop.DISPLAYCONFIG_PATH_MODE_IDX_INVALID)
                continue;
            modes[srcIdx].SourcePositionX = position.X;
            modes[srcIdx].SourcePositionY = position.Y;
        }

        bool allowedChanges = false;
        fixed (MonitorInterop.DisplayConfigPathInfo* p = paths)
        fixed (MonitorInterop.DisplayConfigModeInfo* m = modes)
        {
            uint baseFlags = MonitorInterop.SDC_USE_SUPPLIED_DISPLAY_CONFIG;
            int rc = MonitorInterop.SetDisplayConfig(
                (uint)paths.Length, p, (uint)modes.Length, m,
                MonitorInterop.SDC_VALIDATE | baseFlags);

            if (rc != 0)
            {
                // A refresh change carries hand-scaled video timing that the
                // adapter may not match exactly (real rasters change blanking
                // between rates - bench-observed). Re-validate letting the OS
                // substitute its own known-good timing for the supplied
                // request; the post-verify below keeps the fail-loud
                // contract if the OS lands anywhere other than asked.
                rc = MonitorInterop.SetDisplayConfig(
                    (uint)paths.Length, p, (uint)modes.Length, m,
                    MonitorInterop.SDC_VALIDATE | baseFlags | MonitorInterop.SDC_ALLOW_CHANGES);
                if (rc != 0)
                    throw new MonitorLayoutRejectedException(
                        $"SetDisplayConfig(SDC_VALIDATE) rejected the requested topology "
                        + $"(return code {rc}). Nothing was applied — check the desired modes "
                        + "against each monitor's SupportedModes.", rc);
                allowedChanges = true;
            }

            uint applyFlags = MonitorInterop.SDC_APPLY | baseFlags;
            if (allowedChanges)
                applyFlags |= MonitorInterop.SDC_ALLOW_CHANGES;
            if (options.Persist)
                applyFlags |= MonitorInterop.SDC_SAVE_TO_DATABASE;

            rc = MonitorInterop.SetDisplayConfig(
                (uint)paths.Length, p, (uint)modes.Length, m, applyFlags);
            if (rc != 0)
                throw new MonitorLayoutRejectedException(
                    $"SetDisplayConfig(SDC_APPLY) failed after a clean validate "
                    + $"(return code {rc}).", rc);
        }

        var landed = CcdLayout.Read().Layout;

        // Post-verify: the requested axes must actually hold - mandatory
        // after ALLOW_CHANGES (the OS was free to adjust), cheap insurance
        // otherwise.
        if (!LayoutDiff.IsSatisfiedBy(landed, desired))
            throw new MonitorLayoutRejectedException(
                allowedChanges
                    ? "The OS accepted the request only with adjustments and landed on a "
                      + "different configuration than asked. The change was applied but "
                      + "does not satisfy the desired state — read the layout and decide."
                    : "SetDisplayConfig applied cleanly but the landed configuration does "
                      + "not satisfy the desired state.", 0);

        return new MonitorLayoutApplyResult(MonitorLayoutApplyOutcome.Applied, landed);
    }

    private static MonitorLayoutEntry Find(MonitorLayout layout, string deviceId)
    {
        foreach (var entry in layout.Monitors)
            if (string.Equals(entry.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
                return entry;
        throw new MonitorDeviceNotFoundException($"Monitor '{deviceId}' vanished mid-apply.", deviceId);
    }
}
