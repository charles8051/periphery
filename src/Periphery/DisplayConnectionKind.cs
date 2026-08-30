// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Text.Json.Serialization;

namespace Periphery;

/// <summary>
/// The abstract connection method used to attach a monitor, independent of physical protocol.
/// <para>
/// Derived on Windows from the Win32 DisplayConfig (CCD)
/// <c>DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY</c> of the monitor's active path
/// (ADR-0018). The Linux and macOS providers do not populate it, so
/// <see cref="DeviceInfo.DisplayConnectionKind"/> — the nullable property that
/// carries this value — is <see langword="null"/> there. That
/// <see langword="null"/> means <b>unmeasured</b>, never "not virtual"
/// (ADR-0071).
/// </para>
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<DisplayConnectionKind>))]
public enum DisplayConnectionKind
{
    /// <summary>Connection kind could not be determined.</summary>
    Unknown = 0,

    /// <summary>
    /// Monitor is an internal display (e.g. built-in laptop or tablet panel).
    /// </summary>
    Internal,

    /// <summary>Monitor is connected via a physical cable.</summary>
    Wired,

    /// <summary>Monitor is connected wirelessly (e.g. Miracast, WiDi).</summary>
    Wireless,

    /// <summary>
    /// Windows reported <c>INDIRECT_VIRTUAL</c> — an indirect display declaring
    /// it has no physical output.
    /// <para><b>Do not read this as "the screen is not real", and do not expect
    /// the obvious sources to produce it.</b> Measured (issue #205): a Remote
    /// Desktop session display reports <see cref="Unknown"/>, a QEMU virtual
    /// machine display reports <see cref="Unknown"/>, and an IddCx virtual
    /// display (<c>IddSampleDriver</c>) reports <see cref="Wired"/> / HDMI.
    /// <b>Nothing measured so far produces this member.</b> Earlier revisions of
    /// this doc named RDP and VMs as examples; that was documentation-sourced and
    /// is now known to be wrong.</para>
    /// <para>A display driven by an indirect display driver where panel
    /// attachment is unknowable is <see cref="Indirect"/>, not this (ADR-0072).
    /// Neither member is a virtuality signal — see ADR-0072 Decision 4.</para>
    /// </summary>
    Virtual,

    /// <summary>
    /// Monitor is presented by an <b>indirect display driver</b> rather than a
    /// direct GPU output, and it is <b>not knowable at this layer whether a
    /// physical panel is attached</b>. DisplayLink adapters and USB-C /
    /// Thunderbolt docks drive real monitors through this path; so do purely
    /// synthetic rigs such as Windows' IddSampleDriver. Windows reports both as
    /// <c>DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INDIRECT_WIRED</c> and does not
    /// distinguish them.
    /// <para>Deliberately neither <see cref="Wired"/> nor <see cref="Virtual"/>:
    /// asserting either would be a guess. A consumer that must decide combines
    /// this with its own knowledge of the deployment (ADR-0072).</para>
    /// </summary>
    Indirect,
}
