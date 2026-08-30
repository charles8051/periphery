using System;
using System.Collections.Generic;

namespace Periphery.Tests;

/// <summary>
/// Direct, hardware-free unit tests for the two recovery decisions that ADR-0055
/// / ADR-0060 always stated were pure but were previously only exercisable through
/// the full async proxy: the <see cref="ExponentialBackoffRecoveryPolicy"/> backoff
/// curve (Finding 1.2 — the policy is now a synchronous total function) and the
/// <see cref="ResetEscalation"/> admissibility step (Finding 1.3 — the reset
/// escalation decision is now a value split out of the gate/reset/reopen IO).
/// Both are tested as values: same input -> same output, no clock, no IO, no await.
/// </summary>
public class RecoveryDecisionTests
{
    private static DeviceInfo Device =>
        new()
        {
            Id = "USB\\VID_0001&PID_0002\\1",
            Name = "Test Device",
            Category = DeviceCategory.Usb,
            BusType = BusType.USB,
            VendorId = new HardwareId(0x0001),
            ProductId = new HardwareId(0x0002),
        };

    private static ResetStrategy UsbCycle =>
        new(ResetKind.UsbPortCycle, ResetBlastRadius.Self, ReEnumerates: true);

    private static ResetStrategy PnpDisableEnable =>
        new(ResetKind.PnpDisableEnable, ResetBlastRadius.Self, ReEnumerates: false);

    private static RecoveryContext Context(
        int attempt = 1,
        int resetCount = 0,
        IReadOnlyList<ResetStrategy>? availableResets = null) =>
        new(attempt, resetCount, null, Device, availableResets ?? []);

    // ── ExponentialBackoffRecoveryPolicy.Decide — the backoff curve per attempt ──

    [Theory]
    [InlineData(1, 1)]    // baseDelay
    [InlineData(2, 2)]    // doubled
    [InlineData(3, 4)]    // doubled again
    [InlineData(4, 5)]    // 8s clamped to maxDelay 5s
    [InlineData(5, 5)]    // stays clamped
    [InlineData(10, 5)]
    [InlineData(40, 5)]   // far past the clamp; no overflow
    public void Backoff_PerAttempt_DoublesThenClampsAtMaxDelay(int attempt, int expectedSeconds)
    {
        var policy = new ExponentialBackoffRecoveryPolicy(
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));

        var directive = policy.Decide(Context(attempt));

        var retry = Assert.IsType<RecoveryDirective.Retry>(directive);
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), retry.Delay);
    }

    [Fact]
    public void Backoff_HugeAttempt_SaturatesToMaxDelay_NoOverflow()
    {
        // 2^(int.MaxValue-1) overflows to +Infinity in double; the policy must
        // saturate to maxDelay rather than throw or produce a negative TimeSpan.
        var policy = new ExponentialBackoffRecoveryPolicy(
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));

        var retry = Assert.IsType<RecoveryDirective.Retry>(policy.Decide(Context(int.MaxValue)));
        Assert.Equal(TimeSpan.FromSeconds(5), retry.Delay);
    }

    [Fact]
    public void Backoff_Unbounded_NeverGivesUp()
    {
        // The default (no maxAttempts) retries forever — the legacy retry-forever curve.
        var policy = ExponentialBackoffRecoveryPolicy.Default;
        Assert.IsType<RecoveryDirective.Retry>(policy.Decide(Context(attempt: 100_000)));
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(3, false)]
    [InlineData(4, true)]    // attempt > maxAttempts(3) -> give up
    [InlineData(99, true)]
    public void Backoff_Bounded_GivesUpPastMaxAttempts(int attempt, bool expectGiveUp)
    {
        var policy = new ExponentialBackoffRecoveryPolicy(
            TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1), maxAttempts: 3);

        var directive = policy.Decide(Context(attempt));

        if (expectGiveUp)
            Assert.IsType<RecoveryDirective.GiveUp>(directive);
        else
            Assert.IsType<RecoveryDirective.Retry>(directive);
    }

    [Fact]
    public void Backoff_IsPure_SameInputSameOutput_IgnoresBudgetAndFault()
    {
        // The curve is a function of Attempt alone; ResetCount / LastFault do not move it.
        var policy = ExponentialBackoffRecoveryPolicy.Default;

        var a = (RecoveryDirective.Retry)policy.Decide(
            new RecoveryContext(3, ResetCount: 0, LastFault: null, Device, []));
        var b = (RecoveryDirective.Retry)policy.Decide(
            new RecoveryContext(3, ResetCount: 7, LastFault: new InvalidOperationException("x"), Device, []));

        Assert.Equal(a.Delay, b.Delay);
        Assert.Equal(TimeSpan.FromSeconds(4), a.Delay);
    }

    // ── ResetEscalation.Decide — the admissibility choice per RecoveryContext ──

    [Fact]
    public void Escalation_RequestedStrategyAdvertised_ExecutesThatStrategy()
    {
        var ctx = Context(availableResets: [UsbCycle, PnpDisableEnable]);

        var decision = ResetEscalation.Decide(ctx, new RecoveryDirective.Reset(UsbCycle));

        var exec = Assert.IsType<EscalationDecision.ExecuteDecision>(decision);
        Assert.Equal(UsbCycle, exec.Strategy);
    }

    [Fact]
    public void Escalation_SecondAdvertisedStrategy_IsAlsoAdmissible()
    {
        // The policy may escalate to a harder rung; any strategy the device advertises
        // is admissible, not only the gentlest.
        var ctx = Context(availableResets: [UsbCycle, PnpDisableEnable]);

        var decision = ResetEscalation.Decide(ctx, new RecoveryDirective.Reset(PnpDisableEnable));

        var exec = Assert.IsType<EscalationDecision.ExecuteDecision>(decision);
        Assert.Equal(PnpDisableEnable, exec.Strategy);
    }

    [Fact]
    public void Escalation_EmptyAvailableResets_Concedes()
    {
        // No advertised strategies => the proxy must not reset, even if a misbehaving
        // policy asked for one.
        var ctx = Context(availableResets: []);

        var decision = ResetEscalation.Decide(ctx, new RecoveryDirective.Reset(UsbCycle));

        Assert.IsType<EscalationDecision.ConcedeDecision>(decision);
        Assert.Same(EscalationDecision.Concede, decision);
    }

    [Fact]
    public void Escalation_StrategyNotInAdvertisedSet_Concedes()
    {
        // The device advertises only the gentle port-cycle; a policy that asks for the
        // unsupported PnP rung is inadmissible -> concede rather than execute it.
        var ctx = Context(availableResets: [UsbCycle]);

        var decision = ResetEscalation.Decide(ctx, new RecoveryDirective.Reset(PnpDisableEnable));

        Assert.IsType<EscalationDecision.ConcedeDecision>(decision);
    }

    [Fact]
    public void Escalation_IsPure_SameInputSameOutput()
    {
        var ctx = Context(attempt: 2, resetCount: 1, availableResets: [UsbCycle]);
        var requested = new RecoveryDirective.Reset(UsbCycle);

        var first = ResetEscalation.Decide(ctx, requested);
        var second = ResetEscalation.Decide(ctx, requested);

        var a = Assert.IsType<EscalationDecision.ExecuteDecision>(first);
        var b = Assert.IsType<EscalationDecision.ExecuteDecision>(second);
        Assert.Equal(a.Strategy, b.Strategy);
    }

    [Fact]
    public void Escalation_NullRequested_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => ResetEscalation.Decide(Context(availableResets: [UsbCycle]), null!));
    }
}
