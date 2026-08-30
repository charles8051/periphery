// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery;

/// <summary>
/// A three-valued answer to a relational question: <see cref="Yes"/>, <see cref="No"/>, or
/// <see cref="Unknown"/> when the inputs do not allow the question to be answered at all.
/// </summary>
/// <remarks>
/// <para><b>Why not <c>bool</c>.</b> A bare <c>bool</c> fuses "no" with "cannot see", and the
/// two are opposite facts. <c>PortPath.SharesRootPortWith</c> returning <c>false</c> for a
/// device whose location path never parsed would tell the caller that two boards are on
/// <i>different</i> root ports, when the truth is that nothing was measured — a confident wrong
/// answer produced by a missing measurement (ADR-0079 D7).</para>
/// <para><b><see cref="Unknown"/> is ordinal 0 on purpose.</b> The default must be the honest
/// answer rather than the negative one — the same posture <c>MonitorLayoutAvailability.NotMeasured</c>
/// takes under ADR-0073 D4. The members are deliberately <i>not</i> ordered as a ladder;
/// nothing may compare them with <c>&lt;</c> or <c>&gt;</c>.</para>
/// <para>This type is shared, not per-consumer: ADR-0078 D8 independently arrived at the same
/// shape for <c>SameContainer</c> and consumes this declaration rather than spelling a second
/// one (ADR-0079 D1).</para>
/// <para>Neither this enum nor a <c>TryGet</c> gate can stop a caller who flattens on purpose —
/// <c>x.SharesRootPortWith(y) != Tri.Yes</c> collapses <see cref="Unknown"/> into <see cref="No"/>.
/// What the shape buys is that the collapse has to be <i>written down at the call site</i>, where
/// review and <c>grep</c> can see it. That is the whole of the guarantee (ADR-0079 D7).</para>
/// </remarks>
public enum Tri
{
    /// <summary>The question cannot be answered from the inputs given. Never read this as "no".</summary>
    Unknown = 0,

    /// <summary>Answered, and the answer is no.</summary>
    No,

    /// <summary>Answered, and the answer is yes.</summary>
    Yes,
}
