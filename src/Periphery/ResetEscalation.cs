// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;

namespace Periphery;

/// <summary>
/// Pure, total escalation step for the reset rung of the recovery seam (ADR-0060
/// Decision 3, ADR-0052 functional core). Given a <see cref="RecoveryContext"/>
/// (attempt / reset budget / available strategies) and the
/// <see cref="RecoveryDirective.Reset"/> an <see cref="IRecoveryPolicy"/> chose,
/// it decides — <b>as a value</b> — whether that reset is admissible to execute
/// or whether recovery must concede. The effectful half (consult the safety gate,
/// perform the reset IO, drive the self-reopen poll) lives in the proxy shell's
/// <c>ExecuteResetAsync</c>; this owns only the decision.
/// </summary>
/// <remarks>
/// <para>The split exists because <c>TryResetAndReopenAsync</c> previously fused
/// the escalation <em>decision</em> (which strategy, whether the budget is spent)
/// with the gate / reset / reopen IO and the state transitions, so the only way to
/// exercise the ladder was through the full async proxy with fakes. The inputs the
/// decision needs — <see cref="ResetStrategyMap"/>'s output, carried on
/// <see cref="RecoveryContext.AvailableResets"/>, and <see cref="RecoveryContext.ResetCount"/> —
/// are already pure values, so the decision can be too: a total function with no IO,
/// no clock, and no <see cref="System.Threading.Tasks.Task"/>, exhaustively
/// unit-testable with hand-built <see cref="RecoveryContext"/> values.</para>
/// <para><b>Budget remains the policy's call, not this step's.</b> Whether the
/// reset budget (<see cref="RecoveryContext.ResetCount"/>) is spent is expressed by
/// the policy returning <see cref="RecoveryDirective.GiveUp"/> instead of
/// <see cref="RecoveryDirective.Reset"/> — that decision never reaches this step.
/// This step is the proxy's own <em>admissibility</em> guard on a chosen reset: it
/// concedes only when the chosen strategy is not one the device actually advertises
/// (<see cref="RecoveryContext.AvailableResets"/>), which the proxy must never
/// execute regardless of what the policy asked for. Keeping that guard pure means a
/// misbehaving policy can be rejected as a value, not via an exception deep inside
/// the IO body.</para>
/// </remarks>
public static class ResetEscalation
{
    /// <summary>
    /// Decide whether the <paramref name="requested"/> reset is admissible for
    /// <paramref name="context"/>. Returns <see cref="EscalationDecision.Execute"/>
    /// with the validated strategy when the device advertises it (it is one of
    /// <see cref="RecoveryContext.AvailableResets"/>), and
    /// <see cref="EscalationDecision.Concede"/> otherwise — the proxy must not run a
    /// reset the device never offered.
    /// </summary>
    public static EscalationDecision Decide(RecoveryContext context, RecoveryDirective.Reset requested)
    {
        ArgumentNullException.ThrowIfNull(requested);

        // The proxy only ever advertises a reset the device declared resettable for.
        // A strategy the policy invented (not in AvailableResets) is inadmissible —
        // concede rather than execute an unsupported reset.
        foreach (var available in context.AvailableResets)
        {
            if (available == requested.Strategy)
                return EscalationDecision.Execute(requested.Strategy);
        }

        return EscalationDecision.Concede;
    }
}

/// <summary>
/// The outcome of the pure <see cref="ResetEscalation.Decide"/> step: either run a
/// validated <see cref="ResetStrategy"/>, or concede (no admissible reset). A closed
/// hierarchy — only the two nested cases below can derive.
/// </summary>
public abstract record EscalationDecision
{
    private EscalationDecision() { }

    /// <summary>The single shared "no admissible reset; concede" value.</summary>
    public static readonly EscalationDecision Concede = new ConcedeDecision();

    /// <summary>Run <paramref name="strategy"/> (validated against the device's advertised set).</summary>
    public static EscalationDecision Execute(ResetStrategy strategy) => new ExecuteDecision(strategy);

    /// <summary>The shell should execute <see cref="Strategy"/> via its effectful reset path.</summary>
    public sealed record ExecuteDecision(ResetStrategy Strategy) : EscalationDecision;

    /// <summary>The shell should not reset; recovery concedes (loop re-decides / gives up).</summary>
    public sealed record ConcedeDecision : EscalationDecision;
}
