// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.Generic;

namespace Periphery.Monitor.Windows;

/// <summary>
/// Pure primary-monitor selection for the CCD read (issue #138). Windows
/// always designates exactly one primary monitor, but a raw
/// <c>QueryDisplayConfig</c> read cannot be reduced to "the source at (0,0)":
/// in clone / duplicate mode several active paths share one source parked at
/// the origin, and an indirect (IddCx) virtual display sitting at the origin
/// is not necessarily the real primary. Deriving the flag from position alone
/// therefore reports two (or the wrong) primaries.
/// <para>
/// This factors the decision out of the interop shell into a total function
/// over already-parsed path facts — no handles, no clock — so it is
/// exhaustively unit-testable and, by returning a single index, cannot
/// structurally produce more than one primary.
/// </para>
/// </summary>
internal static class MonitorPrimary
{
    /// <summary>The subset of one active path's facts that primary selection needs.</summary>
    /// <param name="SourceGdiName">
    /// The path's GDI source name (<c>\\.\DISPLAY1</c>) — the <b>source</b>
    /// identity. Clone paths mirroring one desktop share a single source, so
    /// this both correlates a path to the authoritative GDI primary and groups
    /// duplicate paths together.
    /// </param>
    /// <param name="Position">The source's virtual-desktop origin.</param>
    internal readonly record struct PathFacts(string? SourceGdiName, DisplayPosition Position);

    /// <summary>
    /// Chooses the single primary among <paramref name="paths"/>, returning its
    /// index into that list, or <c>-1</c> when none qualifies.
    /// </summary>
    /// <param name="paths">Active-path facts, in enumeration order.</param>
    /// <param name="gdiPrimarySourceName">
    /// The GDI source name Windows reports as primary
    /// (<c>MONITORINFOF_PRIMARY</c>), read by the shell, or <see langword="null"/>
    /// when that read failed.
    /// </param>
    /// <remarks>
    /// Authoritative signal first: the primary is the path whose source is
    /// <paramref name="gdiPrimarySourceName"/>. Clone paths share that source,
    /// so the first-in-enumeration-order match wins — exactly one primary, and
    /// a virtual display idling at the origin is never mistaken for it.
    /// <para>
    /// Fallback when the GDI primary is unknown (enumeration failed, or matched
    /// no active path): the path at the desktop origin (0,0), again first-wins
    /// so a duplicated origin still yields one primary. This keeps the
    /// historical (0,0) definition as a floor while never returning two.
    /// </para>
    /// </remarks>
    internal static int SelectPrimaryIndex(
        IReadOnlyList<PathFacts> paths, string? gdiPrimarySourceName)
    {
        if (!string.IsNullOrEmpty(gdiPrimarySourceName))
        {
            for (int i = 0; i < paths.Count; i++)
                if (NameEquals(paths[i].SourceGdiName, gdiPrimarySourceName))
                    return i;
        }

        for (int i = 0; i < paths.Count; i++)
            if (paths[i].Position is { X: 0, Y: 0 })
                return i;

        return -1;
    }

    private static bool NameEquals(string? a, string? b) =>
        !string.IsNullOrEmpty(a) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
