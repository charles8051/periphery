// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Collections.Generic;

namespace Periphery;

/// <summary>
/// Per-platform OS enumeration tokens under which a tag-emitting enricher's
/// candidate devices appear. Lets a tag-only query — e.g.
/// <c>Devices.Enumerate().WithTag("Gps")</c> with no <c>OfCategory</c> —
/// narrow the OS-level enumeration to the relevant subsystems instead of
/// scanning every device, once a provider consults
/// <see cref="DeviceEnrichers.ScopeForTags(IReadOnlySet{string})"/> (ADR-0051 §5).
/// </summary>
/// <remarks>
/// <para>The tokens are the same routing strings the platform category maps
/// already use: Windows SetupAPI class GUIDs (<c>WindowsCategoryMap</c>),
/// Linux udev subsystem names (<c>LinuxCategoryMap</c>), and macOS IOKit class
/// names (<c>MacOSCategoryMap</c>). A cross-platform enricher declares all
/// three from a single assembly — they are string constants, not platform API
/// calls, the same shape <c>DeviceCategoryRegistry</c> uses for extension
/// categories (ADR-0025).</para>
/// <para>An empty list on a platform means "no scope hint there": the
/// enricher's candidate devices are already enumerated under some existing
/// category, so a tag query is not narrowed on this enricher's account.</para>
/// </remarks>
public sealed record EnricherScope(
    IReadOnlyList<string> WindowsClassGuids,
    IReadOnlyList<string> LinuxSubsystems,
    IReadOnlyList<string> MacOSClasses)
{
    /// <summary>
    /// A scope that contributes no enumeration narrowing on any platform. The
    /// default for a tag-emitting enricher that only annotates devices already
    /// surfaced by an existing category.
    /// </summary>
    public static readonly EnricherScope None = new([], [], []);

    /// <summary>True when this scope carries no tokens on any platform.</summary>
    public bool IsEmpty =>
        WindowsClassGuids.Count == 0 &&
        LinuxSubsystems.Count == 0 &&
        MacOSClasses.Count == 0;
}
