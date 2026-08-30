// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Monitor;

/// <summary>
/// The kind of video output a monitor is attached through, defined by
/// <b>semantic value</b> (internal panel, HDMI, DisplayPort, an indirect/virtual
/// display, …) — not by any one platform's native output-technology encoding.
/// This is a platform-neutral contract type: consumers reason about it by name,
/// and each backend owns the translation from its OS representation (ADR-0064,
/// ADR-0070).
/// </summary>
/// <remarks>
/// <para>
/// The numeric values are a stable, opaque serialization contract; they are
/// <i>not</i> defined as, and must not be assumed equal to, any platform's
/// native value. On Windows the CCD read path maps through
/// <c>Periphery.Monitor.Windows.CcdOutputTechnology</c>, so the enum's ordinals
/// are not load-bearing at the OS boundary; a future backend maps from its own
/// encoding (Linux DRM connector type, an X11/RandR output name) without
/// touching these values.
/// </para>
/// <para>
/// <b>Indirect displays are two members, not one</b>
/// (<see cref="IndirectWired"/> and <see cref="IndirectVirtual"/>), and the
/// distinction is load-bearing. Both are driven by a software display driver
/// rather than a direct GPU output, but only one implies there is no physical
/// panel. Collapsing them here would manufacture a false "this screen is
/// virtual" for DisplayLink adapters and USB-C docks, which drive real glass
/// through the same indirect-display path. This contract reports what the
/// platform reports and leaves the collapse — if a consumer wants one — to the
/// consumer, which is the only place the right answer is knowable.
/// </para>
/// <para>
/// This is a <b>read-only, descriptive</b> value: unlike
/// <see cref="MonitorOrientation"/>, it has no apply-side counterpart — Windows
/// exposes no way to <i>set</i> a monitor's output technology, so there is no
/// contract-to-native reverse mapping. Output technologies this contract does
/// not model (S-Video, composite/component, LVDS, SDI, Miracast, …) surface as
/// <see cref="Other"/> rather than gaining a member speculatively; a backend
/// that needs to distinguish one is a contract extension, not a silent
/// reinterpretation of these values.
/// </para>
/// </remarks>
public enum MonitorOutputTechnology
{
    /// <summary>
    /// An output technology this contract does not model, or one the backend
    /// could not classify (an unmapped native value, or Windows'
    /// <c>_FORCE_UINT32</c>). Never assume a specific connector from this.
    /// </summary>
    Other = 0,

    /// <summary>An internal panel — a built-in laptop / all-in-one display.</summary>
    Internal = 1,

    /// <summary>HD-15 / VGA (analogue).</summary>
    Vga = 2,

    /// <summary>Digital Visual Interface (DVI).</summary>
    Dvi = 3,

    /// <summary>High-Definition Multimedia Interface (HDMI).</summary>
    Hdmi = 4,

    /// <summary>DisplayPort over an external connector.</summary>
    DisplayPortExternal = 5,

    /// <summary>Embedded DisplayPort (eDP) — an internally wired panel.</summary>
    DisplayPortEmbedded = 6,

    /// <summary>
    /// An indirect display driven over a wire — a display presented by a software
    /// display driver rather than a direct GPU output, but terminating in a real
    /// physical panel. DisplayLink adapters and USB-C / Thunderbolt docks report
    /// this.
    /// <para><b>This is not "the screen is virtual", and it is not even a
    /// reliable hint.</b> Four virtualization mechanisms have been measured and
    /// <b>none reaches this member</b> (ADR-0072 D4, issue #205): a synthetic
    /// IddCx display (ge9's <c>IddSampleDriver</c>) reports plain
    /// <see cref="Hdmi"/>; a Remote Desktop session display and a QEMU VM
    /// display both report <see cref="Other"/>; VNC adds no display at all.
    /// Meanwhile a DisplayLink or dock-attached <i>real</i> panel is the one
    /// population plausibly reporting this value. Output technology answers
    /// "how is this attached", never "is there real glass". A consumer needing
    /// that answer must derive it from panel identity (the EDID vendor/product)
    /// or its own deployment inventory.</para>
    /// </summary>
    IndirectWired = 7,

    /// <summary>
    /// Windows reported <c>INDIRECT_VIRTUAL</c> — an indirect display declaring
    /// it has no physical output. Unlike <see cref="IndirectWired"/>, the native
    /// value does imply there is no panel.
    /// <para><b>Do not expect the obvious sources to produce it.</b> Earlier
    /// revisions of this doc named "a Remote Desktop session display or a VM
    /// display" as the examples; both are measured wrong — a live RDP session
    /// display and a QEMU VM display each report <see cref="Other"/> (ADR-0072
    /// D4, issue #205). Nothing measured so far reaches this member.</para>
    /// </summary>
    IndirectVirtual = 8,
}
