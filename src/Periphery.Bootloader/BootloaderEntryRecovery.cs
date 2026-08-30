// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;

namespace Periphery.Bootloader;

/// <summary>
/// Opt-in recovery for the mode switch (ADR-0076): when
/// <see cref="IBootloaderEntry.EnterAsync"/> cannot get the device into its bootloader,
/// drive the ADR-0060 recovery seam — reset the device, wait for it to come back, retry —
/// instead of failing the update outright.
/// </summary>
/// <remarks>
/// <para>
/// <b>The gap this closes.</b> The mode switch travels over the device's normal data path
/// (for a Treehopper, wire opcode <c>0x0D</c> over the peripheral-config endpoint, and an
/// open that reconciles over the same endpoint first). A device whose foreground has
/// stopped never drains that endpoint, so the command is delivered to precisely the thing
/// that is broken. Without recovery the updater's answer to a wedged board is "flash it",
/// and flashing it requires a board that works — the circularity ADR-0075 named. The reset
/// ladder breaks it: <c>SoftProtocolOutOfBand</c> reaches the device over EP0, which its
/// USB ISR services independently of the dead foreground, and the board reboots into a
/// healthy application that *can* hear <c>0x0D</c>.
/// </para>
/// <para>
/// <b>Opt-in, and off by default.</b> A reset is a board-disrupting side effect. An updater
/// that has not asked for one should not get one, so <see cref="BootloaderEntryOptions.Recovery"/>
/// defaults to <see langword="null"/> and the orchestrator behaves exactly as it did before
/// this existed: first failure is the failure.
/// </para>
/// </remarks>
/// <param name="Reset">
/// Supplies and executes the ladder. For a Treehopper this is
/// <c>TreehopperDeviceReset</c> wrapped around <see cref="DeviceReset.PlatformDefault"/>,
/// which advertises the two soft rungs ahead of the platform's USB rungs. The rungs
/// available are whatever <see cref="IDeviceReset.StrategiesFor"/> returns for the
/// application device — an empty list means recovery can only retry.
/// </param>
/// <param name="Policy">
/// Chooses the next step after each failed entry. <see langword="null"/> uses
/// <see cref="EscalatingResetRecoveryPolicy.Default"/> (one sanity retry, then one rung per
/// attempt, gentlest first). Note that <see cref="ExponentialBackoffRecoveryPolicy"/> is a
/// poor fit here — it never returns <see cref="RecoveryDirective.Reset"/>, so it would
/// retry the same wedged endpoint until its attempt budget ran out.
/// </param>
/// <param name="SafetyGate">
/// Consulted before every reset, exactly as <see cref="DeviceProxyBase{TDevice,TException}"/>
/// consults it (ADR-0060 Decision 4). <see langword="null"/> means always-safe.
/// <para>
/// <b>A refusal aborts the update; it does not defer.</b> This differs deliberately from the
/// proxy, which backs off and re-decides because it is a long-lived session that will get
/// another chance. An update is a bounded operation an operator started, so silently
/// waiting out a "not now" would either hang the run or, worse, reset the moment the gate
/// blinked open — mid-sale on a kiosk board is precisely the case the gate exists to
/// prevent. Failing with a clear reason lets the operator re-run it when the device is
/// idle, which is the outcome they actually want.
/// </para>
/// </param>
/// <param name="ReturnTimeout">
/// How long to wait for the application device to re-enumerate after a rung that declares
/// <see cref="ResetStrategy.ReEnumerates"/>. <see langword="null"/> means 15 seconds.
/// A rung that does not re-enumerate is followed by a short settle instead — there is no
/// appearance to wait for.
/// </param>
public sealed record BootloaderEntryRecovery(
    IDeviceReset Reset,
    IRecoveryPolicy? Policy = null,
    IResetSafetyGate? SafetyGate = null,
    TimeSpan? ReturnTimeout = null)
{
    /// <summary>The policy to use — <paramref name="Policy"/> or the escalating default.</summary>
    public IRecoveryPolicy EffectivePolicy => Policy ?? EscalatingResetRecoveryPolicy.Default;

    /// <summary>The application-return timeout to use — <paramref name="ReturnTimeout"/> or 15s.</summary>
    public TimeSpan EffectiveReturnTimeout => ReturnTimeout ?? TimeSpan.FromSeconds(15);
}
