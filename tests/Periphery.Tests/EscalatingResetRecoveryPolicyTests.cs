using System;
using System.Collections.Generic;
using Xunit;

namespace Periphery.Tests;

/// <summary>
/// The pure escalation policy (ADR-0076): sanity retries, then one advertised rung per attempt
/// gentlest-first, then give up. No IO, no clock — every case is a plain value assertion.
/// </summary>
public class EscalatingResetRecoveryPolicyTests
{
    private static readonly ResetStrategy Soft =
        new(ResetKind.SoftProtocol, ResetBlastRadius.Self, ReEnumerates: true);
    private static readonly ResetStrategy OutOfBand =
        new(ResetKind.SoftProtocolOutOfBand, ResetBlastRadius.Self, ReEnumerates: true);
    private static readonly ResetStrategy PortCycle =
        new(ResetKind.UsbPortCycle, ResetBlastRadius.Self, ReEnumerates: true);

    private static readonly DeviceInfo Device = new() { Id = "d" };

    private static RecoveryContext At(int attempt, params ResetStrategy[] available) =>
        new(attempt, ResetCount: 0, LastFault: null, Device: Device,
            AvailableResets: available, Trigger: RecoveryTrigger.BootloaderEntryFailure);

    [Fact]
    public void Spends_one_sanity_retry_before_touching_the_ladder()
    {
        var policy = new EscalatingResetRecoveryPolicy();       // sanityRetries: 1

        var first = Assert.IsType<RecoveryDirective.Retry>(policy.Decide(At(1, Soft, OutOfBand)));
        Assert.Equal(TimeSpan.FromMilliseconds(500), first.Delay);
    }

    [Fact]
    public void Then_walks_every_rung_gentlest_first_and_gives_up_when_they_run_out()
    {
        var policy = new EscalatingResetRecoveryPolicy();
        var ladder = new[] { Soft, OutOfBand, PortCycle };

        var kinds = new List<ResetKind>();
        for (int attempt = 2; attempt <= 4; attempt++)
            kinds.Add(Assert.IsType<RecoveryDirective.Reset>(policy.Decide(At(attempt, ladder))).Strategy.Kind);

        Assert.Equal([ResetKind.SoftProtocol, ResetKind.SoftProtocolOutOfBand, ResetKind.UsbPortCycle], kinds);
        Assert.IsType<RecoveryDirective.GiveUp>(policy.Decide(At(5, ladder)));
    }

    [Fact]
    public void Gives_up_rather_than_naming_a_rung_the_device_never_advertised()
    {
        // The empty ladder is the first-class "not resettable" answer (IDeviceReset.StrategiesFor).
        var policy = new EscalatingResetRecoveryPolicy();
        Assert.IsType<RecoveryDirective.GiveUp>(policy.Decide(At(2)));
    }

    [Fact]
    public void Zero_sanity_retries_escalates_on_the_very_first_failure()
    {
        var policy = new EscalatingResetRecoveryPolicy(sanityRetries: 0);
        var directive = Assert.IsType<RecoveryDirective.Reset>(policy.Decide(At(1, Soft, OutOfBand)));
        Assert.Equal(ResetKind.SoftProtocol, directive.Strategy.Kind);
    }

    [Fact]
    public void Is_total_for_absurd_attempt_numbers_rather_than_throwing()
    {
        // Totality matters: the policy is called from a loop the shell owns, and a throw there
        // would surface as a device fault rather than a recovery decision.
        var policy = new EscalatingResetRecoveryPolicy();
        Assert.IsType<RecoveryDirective.GiveUp>(policy.Decide(At(int.MaxValue, Soft)));
        Assert.IsType<RecoveryDirective.Retry>(policy.Decide(At(0, Soft)));
        Assert.IsType<RecoveryDirective.Retry>(policy.Decide(At(-5, Soft)));
    }

    [Fact]
    public void The_default_instance_is_the_one_bootloader_entry_recovery_uses()
    {
        var directive = EscalatingResetRecoveryPolicy.Default.Decide(At(2, Soft, OutOfBand));
        Assert.Equal(ResetKind.SoftProtocol, Assert.IsType<RecoveryDirective.Reset>(directive).Strategy.Kind);
    }
}
