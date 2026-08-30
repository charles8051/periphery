// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Monitor;

/// <summary>
/// Why a <see cref="MonitorLayout"/> holds the entries it does — specifically,
/// what an <b>empty</b> layout means. An empty topology has two completely
/// different causes that demand opposite responses, and without this value a
/// caller cannot tell them apart (issue #207).
/// </summary>
/// <remarks>
/// <para>
/// This is a platform-neutral contract value in the sense of ADR-0064: it names
/// the <i>situation</i>, not the OS mechanism that produced it. Windows'
/// session-0 isolation is the case that motivated it, but a Linux backend with
/// no seat assigned, or a Wayland client with no compositor connection, is the
/// same situation and reports the same member.
/// </para>
/// <para>
/// Callers that previously branched on <c>Monitors.IsEmpty</c> should branch on
/// this instead. "Empty" alone was never safe to treat as "nothing attached".
/// </para>
/// </remarks>
public enum MonitorLayoutAvailability
{
    /// <summary>
    /// <b>No read was performed.</b> There is no backend on this platform, or the
    /// caller is constructing a layout it never queried for. Nothing is claimed
    /// about the hardware.
    /// <para>This is the <b>zero value</b> deliberately: a default-constructed or
    /// zero-initialized <see cref="MonitorLayoutAvailability"/> must assert the
    /// least, not the most. It is also the value a non-Windows fallback should
    /// use — <see cref="MonitorLayout.ReadAsync"/> throws
    /// <see cref="System.PlatformNotSupportedException"/> off Windows, so a
    /// consumer substituting an empty layout there has measured nothing.</para>
    /// <para>Added because both downstream consumers, independently, had to pick
    /// a member they documented as wrong for exactly this case: the enum modelled
    /// the two <i>outcomes</i> of a read and not the <i>absence</i> of one
    /// (issue #210). It carries the same posture ADR-0068 set for rotation —
    /// unmeasured is its own state, never a negative result.</para>
    /// </summary>
    NotMeasured = 0,

    /// <summary>
    /// The topology was read and holds <b>at least one</b> entry. This member is
    /// never used for an empty layout — an empty read is always
    /// <see cref="NoActiveDisplays"/> or
    /// <see cref="NotVisibleFromThisSession"/>.
    /// </summary>
    Available = 1,

    /// <summary>
    /// The query succeeded from a context that <i>would</i> have seen displays,
    /// and there genuinely are none: a headless machine, every output disabled,
    /// or the Win10 IoT/LTSC zero-paths behaviour (ADR-0044). Treating this as
    /// "nothing to do" is correct.
    /// </summary>
    NoActiveDisplays = 2,

    /// <summary>
    /// Displays may well be attached and working, but <b>this process cannot see
    /// them</b>, because display configuration is scoped to a session and this
    /// process is not in one that owns a desktop.
    /// <para>On Windows this is session 0 — where every Windows <b>service</b>
    /// runs, and where an OpenSSH command shell lands. The layout is empty for a
    /// reason that has nothing to do with the hardware, so treating it as
    /// "headless" is wrong: the correct response is to re-run the read from the
    /// interactive session (ADR-0058 OQ-004, ADR-0059 D4).</para>
    /// <para>The same isolation is why the ADR-0066 <c>WM_DISPLAYCHANGE</c>
    /// refresh hook is inert in session 0.</para>
    /// </summary>
    NotVisibleFromThisSession = 3,
}
