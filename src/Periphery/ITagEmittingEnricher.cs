// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Collections.Generic;

namespace Periphery;

/// <summary>
/// An <see cref="IDeviceEnricher"/> that annotates devices with one or more
/// capability tags (<see cref="DeviceInfo.Tags"/>, ADR-0047) and declares the
/// OS enumeration <see cref="Scope"/> under which its candidate devices
/// appear. The declared <see cref="EmitsTags"/> and <see cref="Scope"/> let a
/// tag-only query narrow OS-level enumeration to the relevant subsystems
/// (ADR-0051 §5) — see
/// <see cref="DeviceEnrichers.ScopeForTags(IReadOnlySet{string})"/>.
/// </summary>
/// <remarks>
/// <para>This is the <em>declarative</em> half of a tag enricher; the
/// behavioural half is the inherited
/// <see cref="IDeviceEnricher.CanEnrich(DeviceInfo)"/> /
/// <see cref="IDeviceEnricher.EnrichAsync(DeviceInfo, System.Threading.CancellationToken)"/>
/// pair, which still does the actual tagging. The two must agree: every tag a
/// concrete <c>EnrichAsync</c> can add should appear in
/// <see cref="EmitsTags"/>, and <see cref="Scope"/> must cover every OS
/// subsystem its taggable devices enumerate under, or a scoped tag query will
/// silently miss them.</para>
/// <para>An enricher that tags devices already surfaced by an existing
/// category (for example a HID-class UPS, enumerated under HID regardless)
/// still declares a <see cref="Scope"/> covering that category's subsystem so
/// a bare <c>WithTag(...)</c> query — one with no <c>OfCategory</c> — can find
/// it without scanning every device.</para>
/// </remarks>
public interface ITagEmittingEnricher : IDeviceEnricher
{
    /// <summary>
    /// The capability tags this enricher may add to a device. Used to decide
    /// whether the enricher is relevant to a tag query and to union its
    /// <see cref="Scope"/> into that query's OS enumeration. Should be the
    /// complete set of tags the enricher's <c>EnrichAsync</c> can emit.
    /// </summary>
    IReadOnlySet<string> EmitsTags { get; }

    /// <summary>
    /// The OS enumeration tokens under which this enricher's taggable devices
    /// appear, per platform. Defaults to <see cref="EnricherScope.None"/> for
    /// enrichers whose devices are already covered by an existing category's
    /// enumeration and therefore need no widening.
    /// </summary>
    EnricherScope Scope => EnricherScope.None;
}
