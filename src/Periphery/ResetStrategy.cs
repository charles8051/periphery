// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery;

/// <summary>
/// One device-reset approach a device can attempt, advertised (gentlest first)
/// by <see cref="IDeviceReset.StrategiesFor"/> and selected by an
/// <see cref="IRecoveryPolicy"/> via <see cref="RecoveryDirective.Reset"/>.
/// A pure value — it describes <em>what</em> a reset would do, independent of
/// whether it succeeds at runtime (see ADR-0060 Decision 2).
/// </summary>
/// <param name="Kind">
/// The mechanism, in ascending force (<see cref="ResetKind.SoftProtocol"/> →
/// <see cref="ResetKind.SoftProtocolOutOfBand"/> → <see cref="ResetKind.UsbPortCycle"/>
/// → <see cref="ResetKind.PnpDisableEnable"/>).
/// </param>
/// <param name="Radius">
/// Whether executing this strategy can disturb sibling devices on the same hub
/// (a <see cref="ResetKind.UsbPortCycle"/> on a multi-port hub is
/// <see cref="ResetBlastRadius.SharedHub"/>).
/// </param>
/// <param name="ReEnumerates">
/// Whether the strategy produces the absent→present device-tree transition the
/// watcher can observe. The proxy re-opens on its own authority regardless
/// (ADR-0060 Decision 9); this only governs whether the watcher-wake fast-path
/// is available.
/// <para>
/// <b>This is a property of the individual strategy, not of its
/// <see cref="ResetKind"/>.</b> It is a constructor argument precisely because it
/// cannot be derived from the kind: a real <see cref="ResetKind.UsbPortCycle"/>
/// re-enumerates and <see cref="ResetKind.PnpDisableEnable"/> does not (the
/// instance stays in the tree), but a <see cref="ResetKind.SoftProtocol"/> reset
/// can go either way — it depends entirely on what the device's firmware does
/// with the command. Ask the strategy; do not infer from the kind.
/// </para>
/// </param>
public readonly record struct ResetStrategy(
    ResetKind Kind,
    ResetBlastRadius Radius,
    bool ReEnumerates);

/// <summary>Reset mechanisms, ordered by ascending force / disruption.</summary>
public enum ResetKind
{
    /// <summary>
    /// A device-specific soft reset issued over the open transport (a board
    /// reset command, an MF source reinit). Gentlest; supplied by a device
    /// extension (<c>Periphery.Treehopper</c>, <c>.Camera</c>), not by core.
    /// <para>
    /// <b>May or may not re-enumerate</b> — that is for the extension to declare
    /// per strategy, not for this kind to imply. A Treehopper board reset (wire
    /// opcode <c>0x0C</c>) <em>does</em>: measured on two boards, off the bus
    /// ~230 ms with real remove/arrive edges (periphery #232). An MF source
    /// reinit does not. Read <see cref="ResetStrategy.ReEnumerates"/>.
    /// </para>
    /// </summary>
    SoftProtocol,

    /// <summary>
    /// A device-specific reset delivered over a channel that survives a wedged
    /// primary transport — for a USB device, a vendor control request on EP0,
    /// which the device services from its USB ISR rather than from its
    /// foreground loop. Supplied by a device extension, never by core.
    /// <para>
    /// <b>Reachability, not force, is the point.</b> <see cref="SoftProtocol"/>
    /// is a <em>cooperative</em> reset: it travels over the normal data path and
    /// needs the device's foreground to still be draining it. When that
    /// foreground has stopped — the failure mode ADR-0060 exists for — the
    /// command is delivered to the very endpoint that is wedged and never
    /// arrives. This rung is the one that still gets through (ADR-0075).
    /// </para>
    /// <para>
    /// <b>Cannot be confirmed from the transfer.</b> A device resetting
    /// mid-request and a device whose firmware never implemented the request
    /// fault identically, so this rung reports <see cref="ResetOutcome.Issued"/>
    /// and never a verdict it cannot substantiate (ADR-0073). On firmware
    /// predating the handler it therefore reports <c>Issued</c> and does
    /// nothing; escalation to the next rung is what covers that.
    /// </para>
    /// </summary>
    SoftProtocolOutOfBand,

    /// <summary>
    /// Cycle the USB hub port the device is attached to (drops the data
    /// connection, and power if the hub supports per-port switching). Forces a
    /// real re-enumeration of the device subtree.
    /// </summary>
    UsbPortCycle,

    /// <summary>
    /// Disable then re-enable the devnode (PnP soft restart:
    /// <c>CM_Disable_DevNode</c> + <c>CM_Enable_DevNode</c>). Highest force, but
    /// the instance stays in the device tree — it does <b>not</b> re-enumerate,
    /// so the watcher sees no transition (ADR-0060 Decision 9).
    /// <para>
    /// <b>Measured, not assumed</b> (periphery #251). Across disable/enable cycles on
    /// real hardware a <see cref="DeviceWatcher"/> filtered to the device's own
    /// LocationPath + serial fired <b>no edge of any kind</b> — 0/5 trials — and the
    /// device never left enumeration; it only flipped
    /// <c>Disabled</c>/<c>CM_PROB_DISABLED</c> → <c>OK</c>/<c>CM_PROB_NONE</c>.
    /// </para>
    /// <para>
    /// <b>Do not "fix" this to <c>ReEnumerates: true</c>.</b> It looks like the way to
    /// buy the event-driven post-reset wait that
    /// <see cref="UsbPortCycle"/> gets, but there is no arrival to wait for: because
    /// the device stays enumerated throughout, an identity-filtered wait matches the
    /// <em>still-disabled</em> node from its own startup snapshot and returns
    /// immediately. The result is added latency and zero readiness guarantee. Readiness
    /// for this rung is established by polling the devnode instead — see
    /// <c>WindowsDeviceReset.DisableEnableAsync</c>.
    /// </para>
    /// </summary>
    PnpDisableEnable,
}

/// <summary>Whether executing a <see cref="ResetStrategy"/> can disturb siblings.</summary>
public enum ResetBlastRadius
{
    /// <summary>Affects only the target device.</summary>
    Self,

    /// <summary>
    /// May disturb sibling devices that share the same hub (e.g. a port-power
    /// cycle on a hub without per-port switching). The mechanism coalesces
    /// concurrent cycles of one hub (ADR-0060 Decision 4).
    /// </summary>
    SharedHub,
}

/// <summary>Result of attempting a <see cref="ResetStrategy"/>.</summary>
public enum ResetOutcome
{
    /// <summary>The reset was issued as requested.</summary>
    Issued,

    /// <summary>
    /// A weaker reset than requested was performed (e.g. a port-cycle that could
    /// not actually cut power because the hub lacks per-port switching, so it
    /// degraded to a soft data-line reset).
    /// </summary>
    Degraded,

    /// <summary>The reset was attempted but failed (e.g. the devnode rejected disable).</summary>
    Failed,

    /// <summary>
    /// The strategy is not supported for this device at runtime (e.g. the parent
    /// hub / port could not be resolved). Distinct from an empty
    /// <see cref="IDeviceReset.StrategiesFor"/>, which means "never advertised".
    /// </summary>
    NotSupported,
}
