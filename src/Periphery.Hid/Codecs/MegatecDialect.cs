// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Collections.Immutable;

namespace Periphery.Hid.Codecs;

/// <summary>
/// One status dialect in the Megatec Qx family: the status-inquiry
/// <see cref="Verb"/> to send and the <see cref="ResponsePrefix"/> character its
/// reply begins with. Immutable data — the codec's claim-and-bind handshake
/// decides which dialect a given device actually speaks (see
/// <see cref="MegatecQxCodec"/>).
/// </summary>
/// <remarks>
/// Modelled as a reference type, not a <c>record struct</c>, on purpose: the
/// dialects are a fixed set of shared singletons (<see cref="Q1"/>,
/// <see cref="QS"/>), and a struct holding a <c>static readonly
/// ImmutableArray&lt;MegatecDialect&gt;</c> of <em>itself</em> trips a value-type
/// layout cycle in the loader (<see cref="System.Collections.Immutable.ImmutableArray{T}"/>
/// is itself a value type). A record class keeps value equality without that
/// hazard.
/// </remarks>
/// <remarks>
/// <para>
/// The Megatec family shares one response <em>shape</em> (the
/// <c>(MMM.M NNN.N …</c> line that <see cref="MegatecStatus"/> parses) across
/// several different status <em>verbs</em>: <c>Q1</c> is the Megatec-spec status
/// inquiry, <c>QS</c> is the Voltronic variant, and the same Cypress 0665
/// silicon ships firmware that answers one or the other (or both). VID:PID
/// <b>cannot</b> distinguish them — the dialect must be probed at runtime,
/// exactly as NUT's <c>nutdrv_qx</c> subdrivers each <c>claim()</c> a device
/// before binding to it.
/// </para>
/// <para>
/// All dialects sharing this response shape collapse into this codec because the
/// only thing that differs is the verb. Reserve a sibling codec (its own
/// <see cref="IHidUpsCodec"/>) for a dialect whose response <em>format</em>
/// diverges, not merely its verb.
/// </para>
/// </remarks>
internal sealed record MegatecDialect(string Verb, char ResponsePrefix)
{
    /// <summary>Megatec-spec status inquiry. <c>Q1\r</c> → <c>(…</c>.</summary>
    public static readonly MegatecDialect Q1 = new("Q1", '(');

    /// <summary>Voltronic status inquiry. <c>QS\r</c> → <c>(…</c>.</summary>
    public static readonly MegatecDialect QS = new("QS", '(');

    /// <summary>
    /// Ordered set of dialects probed during claim-and-bind detection.
    /// </summary>
    /// <remarks>
    /// The dialects are <b>peers, not a hierarchy</b> — the order here is only
    /// the probe sequence, not a claim that one is canonical. In particular, do
    /// <b>not</b> privilege <c>Q1</c>: the May-2026 "Q1 validated against this
    /// WayTech" reading in ADR-0048 was very likely a multicast-input artifact —
    /// the vendor monitor's <c>QS</c> reply seen on the shared HID input pipe and
    /// misattributed to our <c>Q1</c> write. Extend with new verbs (e.g.
    /// Voltronic <c>D</c>) as hardware surfaces them.
    /// </remarks>
    public static readonly ImmutableArray<MegatecDialect> Candidates = [Q1, QS];
}
