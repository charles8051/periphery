// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;

namespace Periphery.Monitor;

/// <summary>
/// A zero-handle snapshot of the whole monitor topology (ADR-0059): one
/// entry per active display with its identity, live mode, preferred mode,
/// rotation, virtual-desktop position, primary flag, and the modes the OS
/// will accept. Pure data — the apply surface is the separate
/// <see cref="MonitorLayoutApplier"/>, by design (the read/apply trust split).
/// </summary>
/// <remarks>
/// An <b>empty</b> layout has two causes that demand opposite responses —
/// genuinely no displays, or a session that cannot see them — so branch on
/// <see cref="Availability"/>, <b>not</b> on <c>Monitors.IsEmpty</c>. Treating
/// "empty" as "headless" is wrong in a Windows service or over SSH, where the
/// process runs in session 0 and the topology is invisible regardless of what
/// is attached (issue #207).
/// </remarks>
/// <param name="Monitors">One entry per active display path.</param>
/// <param name="Availability">
/// What this snapshot means — in particular, why it is empty when it is empty.
/// See <see cref="MonitorLayoutAvailability"/>.
/// </param>
public sealed record MonitorLayout(
    ImmutableArray<MonitorLayoutEntry> Monitors,
    MonitorLayoutAvailability Availability)
{
    /// <summary>Reads the current topology. Never opens a device handle.</summary>
    public static Task<MonitorLayout> ReadAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (OperatingSystem.IsWindows())
            return Task.Run(() => Windows.CcdLayout.Read().Layout, ct);

        throw new PlatformNotSupportedException(
            "MonitorLayout is not yet implemented on this platform. The Linux "
            + "story is gated on a pinned session model (ADR-0058 D9 / ADR-0059).");
    }

    /// <summary>
    /// The single monitor Windows designates primary
    /// (<c>MONITORINFOF_PRIMARY</c>) — normally the one owning the desktop
    /// origin (0,0), but derived from the real primary signal rather than
    /// position so clone/mirror mode and a virtual display parked at the origin
    /// do not yield a false or duplicate primary (issue #138). <c>null</c> when
    /// the layout is empty.
    /// </summary>
    public MonitorLayoutEntry? Primary
    {
        get
        {
            foreach (var entry in Monitors)
                if (entry.IsPrimary)
                    return entry;
            return null;
        }
    }
}

/// <summary>One monitor's place in the topology.</summary>
/// <remarks>
/// <b>Two frames, pinned:</b> the panel plane and the desktop plane are
/// deliberately separate fields so no consumer has to re-derive one from the
/// other (the ambiguity behind the "-312 vs -56" position bug and a transposed
/// remote-view crop). <see cref="CurrentMode"/>, <see cref="PreferredMode"/>,
/// and <see cref="SupportedModes"/> are the panel's <b>native/unrotated</b>
/// pixels — the frame the mode-set and drift-comparison APIs speak.
/// <see cref="DesktopSize"/> is the width×height the monitor occupies on the
/// <b>virtual desktop</b> after <see cref="Orientation"/> is applied — the
/// frame layout/position math speaks. For a landscape monitor the two coincide;
/// for a portrait one they are transposed.
/// </remarks>
/// <param name="DeviceId">
/// The PnP instance ID — the same identity core enumeration surfaces as
/// <see cref="Periphery.DeviceInfo.Id"/>, so layout entries join to
/// <c>DeviceInfo</c> and to <see cref="MonitorDevice.OpenAsync"/> directly.
/// <para>Typed as <see cref="Periphery.DeviceId"/> rather than <c>string</c>
/// precisely so that join is correct by construction: the two sides derive the
/// id from different Windows APIs — core from
/// <c>CM_Get_Device_Interface_Property(DEVPKEY_Device_InstanceId)</c>, this
/// reader by transforming the device-interface path — and Windows does not
/// guarantee they agree in <b>case</b>. They routinely do not. <c>DeviceId</c>
/// compares and hashes <c>OrdinalIgnoreCase</c>, so <c>==</c> and dictionary
/// lookups against <see cref="Periphery.DeviceInfo.Id"/> work; a raw
/// <c>string</c> compared ordinally would silently fail to match (issue #190).</para>
/// </param>
/// <param name="CurrentMode">
/// The mode driving the panel right now, in the panel's <b>native (unrotated,
/// source) frame</b> — read straight from the CCD source mode, so a
/// portrait-rotated 1280x720 panel reports <c>1280x720</c> here, not
/// <c>720x1280</c>. This is the same frame as <paramref name="PreferredMode"/>
/// and <paramref name="SupportedModes"/>, so drift detection (current vs
/// preferred) and mode-set validation compare like-for-like. For the size the
/// panel occupies on the virtual desktop, read <see cref="DesktopSize"/>
/// instead — do not swap this by hand.
/// </param>
/// <param name="PreferredMode">
/// The panel's preferred (native) mode, when reported — same native frame as
/// <paramref name="CurrentMode"/>.
/// </param>
/// <param name="IsPrimary">
/// Whether this monitor is the topology's primary. This is an <b>explicit
/// modeled flag</b>, not the derived predicate "position == (0,0)" (ADR-0064).
/// Today it is populated from the Windows-CCD invariant that primary sits at
/// the virtual-desktop origin, but the contract does not bind primary to the
/// origin: a non-Windows backend sets this from its own signal — on X11 the
/// dedicated RandR "primary output" flag, which is independent of coordinates.
/// A backend with no primary concept (a headless or single-output Wayland
/// session) may report every entry <c>false</c>.
/// </param>
/// <param name="Orientation">
/// Rotation as the platform-neutral <see cref="MonitorOrientation"/> semantic
/// value; each backend maps it from its native encoding (see that type).
/// </param>
/// <param name="OutputTechnology">
/// The kind of video output the monitor is attached through, as the
/// platform-neutral <see cref="MonitorOutputTechnology"/> semantic value; each
/// backend maps it from its native encoding (see that type). This is a
/// <b>descriptive, read-only</b> attribute — it drives no apply decision and has
/// no counterpart on <see cref="MonitorConfiguration"/>. It is the only place
/// the topology reports how a screen is attached at all: neither the
/// <see cref="DeviceId"/> nor an EDID serial distinguishes an indirect display
/// from a panel on a real port. Note that
/// <see cref="MonitorOutputTechnology.IndirectWired"/> alone does <b>not</b>
/// mean "virtual" — DisplayLink adapters and USB-C docks drive real panels
/// through it (see that type). On Windows it is
/// read from the same <c>GET_TARGET_NAME</c> query that yields
/// <see cref="DeviceId"/> and <see cref="FriendlyName"/>, so it costs no extra
/// interop call. A backend that cannot classify the output reports
/// <see cref="MonitorOutputTechnology.Other"/>.
/// </param>
/// <param name="Position">
/// The monitor's origin on a global virtual desktop. This models a
/// <b>backend capability</b> (Windows CCD exposes a single global desktop
/// coordinate space) that a platform may not have: Wayland has no global
/// output-coordinate origin a client can read or rely on, so a future Wayland
/// backend may surface a compositor-relative value or none. Treat cross-monitor
/// geometry as advisory unless the backend documents a global origin.
/// </param>
public sealed record MonitorLayoutEntry(
    DeviceId DeviceId,
    string? FriendlyName,
    bool IsPrimary,
    DisplayMode CurrentMode,
    DisplayMode? PreferredMode,
    MonitorOrientation Orientation,
    MonitorOutputTechnology OutputTechnology,
    DisplayPosition Position,
    ImmutableArray<DisplayMode> SupportedModes)
{
    /// <summary>
    /// What the panel <b>claims to be</b> — its EDID vendor and product code
    /// (e.g. <c>ACR0507</c>). <see langword="null"/> when the backend could not
    /// decode one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Evidence, not a verdict</b> (ADR-0073). This is the cheapest signal that
    /// separates a synthetic display from a real one — an
    /// <c>IddSampleDriver</c> rig reports <c>LNX0000</c> — but only because that
    /// driver's author chose that EDID. Match it as a <b>fingerprint</b> against a
    /// list you maintain; it is not a fact the OS verified, and Periphery
    /// deliberately does not turn it into an <c>IsVirtual</c> boolean.
    /// </para>
    /// <para>
    /// Deliberately an init-only property rather than a positional parameter: it
    /// arrives free from the <c>GET_TARGET_NAME</c> query already issued for
    /// <see cref="DeviceId"/>, and adding it positionally would have broken every
    /// consumer's construction sites for a third time in one day for a field they
    /// need not construct. The asymmetry is a churn trade, recorded so it is not
    /// mistaken for an accident.
    /// </para>
    /// </remarks>
    public MonitorPanelIdentity? PanelId { get; init; }

    /// <summary>
    /// The width×height the monitor occupies on the virtual desktop — the
    /// panel's native <see cref="CurrentMode"/> after <see cref="Orientation"/>
    /// is applied (a landscape/portrait rotation transposes it). This is the
    /// frame layout and <see cref="Position"/> math live in: a consumer laying
    /// out the desktop reads this directly instead of re-deriving it from the
    /// mode and the rotation. Derived, never stored — it cannot drift from
    /// <see cref="CurrentMode"/> / <see cref="Orientation"/>.
    /// </summary>
    public DisplaySize DesktopSize
    {
        get
        {
            (int width, int height) = OrientationMath.Reframe(
                CurrentMode.Width, CurrentMode.Height,
                MonitorOrientation.Landscape, Orientation);
            return new DisplaySize(width, height);
        }
    }
}
