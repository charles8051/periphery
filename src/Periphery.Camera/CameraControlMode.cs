// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Camera;

/// <summary>
/// Whether a camera control is currently being driven by the device itself or
/// held at a value someone set.
/// </summary>
/// <remarks>
/// <para>
/// <b>A semantic value, not a platform encoding.</b> The two backends express
/// this in unrelated ways and neither shape is the contract:
/// </para>
/// <list type="bullet">
/// <item><b>Media Foundation</b> carries it as a flag on the control itself —
///   <c>VideoProcAmp_Flags_Auto</c> / <c>_Manual</c> returned alongside the value
///   by <c>IAMVideoProcAmp::Get</c>.</item>
/// <item><b>V4L2</b> has no such flag. Automatic behaviour is a <i>separate
///   control</i> — <c>V4L2_CID_EXPOSURE_AUTO</c>, <c>V4L2_CID_AUTO_WHITE_BALANCE</c>
///   — and its sense is not even consistent between them: white balance is a
///   boolean where 1 means automatic, while exposure is an enumeration where
///   <c>V4L2_EXPOSURE_AUTO</c> is <b>0</b> and <c>V4L2_EXPOSURE_MANUAL</c> is 1.</item>
/// </list>
/// <para>
/// A contract shaped like either one would force the other backend to lie, so
/// each maps its own signal onto these values and the mapping lives with the
/// backend. This follows ADR-0064's stance for the monitor value contract:
/// define the neutral meaning while the shapes still differ, rather than
/// discovering later that a "neutral" type encoded one platform's model.
/// </para>
/// </remarks>
public enum CameraControlMode
{
    /// <summary>
    /// The backend cannot say. A <b>named gap</b>, not a synonym for manual.
    /// </summary>
    /// <remarks>
    /// Reported when the device exposes a readable value but nothing that says
    /// how it is being driven — for instance a Media Foundation driver that
    /// returns neither flag. Consumers that need certainty must treat this as
    /// unknown rather than assuming the safer-sounding answer; a caller that
    /// read <see cref="Manual"/> here would believe a value is pinned when it
    /// may be drifting.
    /// </remarks>
    Unknown = 0,

    /// <summary>The control is held at a value someone set; the device is not adjusting it.</summary>
    Manual = 1,

    /// <summary>The device is adjusting the control itself.</summary>
    Automatic = 2,
}
