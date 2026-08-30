// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;

namespace Periphery;

/// <summary>
/// Default <see cref="IRecoveryPolicy"/>: exponential backoff between retries,
/// capped at <paramref name="maxDelay"/>, with an optional give-up after
/// <paramref name="maxAttempts"/> consecutive failures. <b>Never resets</b> —
/// reset is a fault-keyed escalation a consumer policy opts into (ADR-0060); this
/// default preserves the pre-ADR-0060 retry-or-give-up behavior verbatim.
/// </summary>
/// <param name="baseDelay">Delay before the first retry; doubled each attempt.</param>
/// <param name="maxDelay">Upper bound on the per-attempt delay.</param>
/// <param name="maxAttempts">
/// Maximum consecutive failures before giving up (the policy returns
/// <see cref="RecoveryDirective.GiveUp"/>), or <see langword="null"/> to retry forever.
/// </param>
public sealed class ExponentialBackoffRecoveryPolicy(
    TimeSpan baseDelay, TimeSpan maxDelay, int? maxAttempts = null) : IRecoveryPolicy
{
    /// <summary>
    /// Reproduces the legacy 1→2→4→5 s (capped) curve, unbounded — the same
    /// cadence as the hardcoded backoff that predated ADR-0055. Used as the
    /// <see cref="DeviceProxyBase{TDevice,TException}"/> default so existing
    /// proxies behave exactly as before until a consumer injects a richer policy.
    /// </summary>
    public static readonly IRecoveryPolicy Default =
        new ExponentialBackoffRecoveryPolicy(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));

    /// <inheritdoc/>
    public RecoveryDirective Decide(RecoveryContext context)
    {
        if (maxAttempts is { } max && context.Attempt > max)
            return new RecoveryDirective.GiveUp();           // give up

        // baseDelay * 2^(attempt-1), clamped at maxDelay. Compute the factor in
        // double and saturate before the multiply so a large Attempt can't
        // overflow TimeSpan — the curve flattens at maxDelay long before then.
        var factor = Math.Pow(2, context.Attempt - 1);
        var capFactor = maxDelay.Ticks / (double)Math.Max(baseDelay.Ticks, 1);

        TimeSpan delay;
        if (double.IsInfinity(factor) || factor >= capFactor)
            delay = maxDelay;
        else
        {
            var exp = baseDelay * factor;
            delay = exp < maxDelay ? exp : maxDelay;
        }

        return new RecoveryDirective.Retry(delay);
    }
}
