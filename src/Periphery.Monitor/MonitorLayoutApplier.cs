// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Periphery.Monitor;

/// <summary>Desired state for one monitor; a null axis means "leave as-is".</summary>
/// <remarks>
/// <para>
/// <see cref="Mode"/> is expressed in the monitor's <b>native (unrotated,
/// source) frame</b> — the same frame the read model reports as
/// <see cref="MonitorLayoutEntry.CurrentMode"/> (#137 / ADR-0064), so the
/// applier's satisfied-check compares like-for-like. Set a portrait panel with
/// <see cref="Orientation"/> = Portrait and <see cref="Mode"/> = its native
/// <c>1920x1080</c> (NOT the transposed <c>1080x1920</c>): rotation is a
/// separate axis and does not transpose the mode. The virtual-desktop footprint
/// the panel then occupies is derived from the native mode plus the rotation and
/// reported as <see cref="MonitorLayoutEntry.DesktopSize"/>.
/// </para>
/// <para>
/// <see cref="Position"/> and <see cref="IsPrimary"/> are <b>backend
/// capabilities</b> that a platform may not support (ADR-0064). Both are
/// Windows-CCD-backed today: setting <see cref="Position"/> writes a global
/// virtual-desktop coordinate, and <see cref="IsPrimary"/> = <c>true</c> is
/// realized by translating every source so the chosen monitor lands at the
/// desktop origin. Neither operation has a portable analog — Wayland clients
/// cannot set output position at all, and X11 sets primary via a RandR flag
/// with no coordinate translation — so a non-Windows applier is expected to
/// reject or ignore an unsupported axis explicitly rather than emulate it.
/// </para>
/// </remarks>
public sealed record MonitorConfiguration(
    DeviceId DeviceId,
    DisplayMode? Mode = null,
    MonitorOrientation? Orientation = null,
    DisplayPosition? Position = null,
    bool? IsPrimary = null);

/// <summary>How <see cref="MonitorLayoutApplier.ApplyAsync"/> concluded.</summary>
public enum MonitorLayoutApplyOutcome
{
    /// <summary>The topology already matched the desired state; nothing was touched.</summary>
    AlreadySatisfied,

    /// <summary>The change validated and was applied (and persisted, unless opted out).</summary>
    Applied,
}

/// <summary>The outcome plus the post-call topology snapshot.</summary>
public sealed record MonitorLayoutApplyResult(
    MonitorLayoutApplyOutcome Outcome,
    MonitorLayout Layout);

public sealed record MonitorLayoutApplyOptions
{
    /// <summary>
    /// Persist the applied topology to the CCD database
    /// (<c>SDC_SAVE_TO_DATABASE</c>) so it survives logon cycles and reboots.
    /// Defaults to true — the surface's audience is posture convergence.
    /// </summary>
    public bool Persist { get; init; } = true;
}

/// <summary>
/// The privileged display-topology apply surface (ADR-0059): given desired
/// per-monitor state, validates and applies it as one
/// <c>SetDisplayConfig</c> transaction. Deliberately a separate entry point
/// from the <see cref="MonitorLayout"/> read model — the read/apply trust
/// boundary is visible at the call site.
/// </summary>
public static class MonitorLayoutApplier
{
    /// <summary>
    /// Converges the topology toward <paramref name="desired"/>.
    /// Idempotent: when the current layout already satisfies every requested
    /// axis, returns <see cref="MonitorLayoutApplyOutcome.AlreadySatisfied"/>
    /// without touching the OS — the convergence-loop common case.
    /// </summary>
    /// <exception cref="MonitorDeviceNotFoundException">
    /// A <see cref="MonitorConfiguration.DeviceId"/> is not in this session's
    /// active topology — including the "no active display paths at all" case
    /// (headless, non-interactive session, or LTSC zero-paths; ADR-0059 D4).
    /// </exception>
    /// <exception cref="MonitorLayoutRejectedException">
    /// <c>SetDisplayConfig(SDC_VALIDATE)</c> rejected the requested topology;
    /// carries the CCD return code. Nothing was applied.
    /// </exception>
    public static Task<MonitorLayoutApplyResult> ApplyAsync(
        IReadOnlyList<MonitorConfiguration> desired,
        MonitorLayoutApplyOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(desired);
        if (desired.Count == 0)
            throw new ArgumentException("At least one MonitorConfiguration is required.", nameof(desired));
        if (desired.Count(d => d.IsPrimary == true) > 1)
            throw new ArgumentException("At most one monitor can be designated primary.", nameof(desired));
        ct.ThrowIfCancellationRequested();

        if (OperatingSystem.IsWindows())
            return Task.Run(() => Windows.CcdLayoutApplier.Apply(
                desired, options ?? new MonitorLayoutApplyOptions()), ct);

        throw new PlatformNotSupportedException(
            "MonitorLayoutApplier is not yet implemented on this platform (ADR-0059; "
            + "the Linux story is gated on a pinned session model, ADR-0058 D9).");
    }
}

/// <summary>
/// Pure desired-vs-current comparison — the idempotence core of
/// <see cref="MonitorLayoutApplier"/>, exhaustively unit-testable.
/// </summary>
internal static class LayoutDiff
{
    /// <summary>
    /// True when every requested axis of every configuration already holds
    /// in <paramref name="current"/>. Unknown device IDs report "not
    /// satisfied" (the applier turns them into a typed not-found).
    /// </summary>
    internal static bool IsSatisfiedBy(
        MonitorLayout current, IReadOnlyList<MonitorConfiguration> desired)
    {
        foreach (var config in desired)
        {
            MonitorLayoutEntry? entry = null;
            foreach (var candidate in current.Monitors)
            {
                // DeviceId compares OrdinalIgnoreCase by construction — the two
                // sides of this match derive the id from different Windows APIs
                // and do not agree in case (issue #190).
                if (candidate.DeviceId == config.DeviceId)
                {
                    entry = candidate;
                    break;
                }
            }
            if (entry is null)
                return false;

            if (config.Mode is { } mode && entry.CurrentMode != mode)
                return false;
            if (config.Orientation is { } orientation && entry.Orientation != orientation)
                return false;
            if (config.Position is { } position && entry.Position != position)
                return false;
            if (config.IsPrimary is { } isPrimary && entry.IsPrimary != isPrimary)
                return false;
        }
        return true;
    }

    /// <summary>
    /// Final virtual-desktop positions for the whole topology: explicit
    /// positions win, then a primary designation translates everything so
    /// the chosen monitor lands at the origin (CCD defines primary as the
    /// monitor at (0,0)).
    /// </summary>
    internal static Dictionary<DeviceId, DisplayPosition> ResolvePositions(
        MonitorLayout current, IReadOnlyList<MonitorConfiguration> desired)
    {
        // No explicit comparer: DeviceId's own Equals/GetHashCode are
        // OrdinalIgnoreCase, so the dictionary is case-correct by construction.
        var positions = current.Monitors.ToDictionary(m => m.DeviceId, m => m.Position);

        foreach (var config in desired)
        {
            if (config.Position is { } position)
                positions[config.DeviceId] = position;
        }

        var primary = desired.FirstOrDefault(d => d.IsPrimary == true);
        if (primary is not null && positions.TryGetValue(primary.DeviceId, out var origin)
            && origin != new DisplayPosition(0, 0))
        {
            foreach (var key in positions.Keys.ToList())
                positions[key] = new DisplayPosition(
                    positions[key].X - origin.X, positions[key].Y - origin.Y);
        }

        return positions;
    }
}
