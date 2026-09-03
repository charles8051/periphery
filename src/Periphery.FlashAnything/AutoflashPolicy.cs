// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Periphery.Bootloader;

namespace Periphery.FlashAnything;

/// <summary>
/// The armed autoflash configuration: which family/provider to auto-flash, and with what options.
/// Bound when the operator arms autoflash; <see langword="null"/> in <see cref="AppState.Autoflash"/>
/// means disarmed. <see cref="Family"/> is matched against <see cref="FlashTargetView.ProviderName"/>.
/// </summary>
public sealed record AutoflashConfig(string Family, FlashOptions Options)
{
    /// <summary>
    /// The USB-serial bridges this arm is bound to, for probe-identified families. Empty for
    /// passive families, which identify themselves and need no binding; required for probe
    /// families, where it is the operator's consent to probe those fixtures and nothing else
    /// (adr.md Decision 8).
    /// </summary>
    public ImmutableHashSet<BridgeIdentity> Bridges { get; init; } = ImmutableHashSet<BridgeIdentity>.Empty;
}

/// <summary>What autoflash should do for one detected target. A closed union.</summary>
public abstract record AutoflashAction
{
    private protected AutoflashAction() { }

    /// <summary>Flash this target now.</summary>
    public sealed record Flash : AutoflashAction
    {
        public static readonly Flash Instance = new();
    }

    /// <summary>Do not flash; <paramref name="Reason"/> says why (surfaced as a skip, never silent).</summary>
    public sealed record Skip(string Reason) : AutoflashAction;
}

/// <summary>
/// The pure, total autoflash decision (ADR-0052): given the armed config, a detected target, and the
/// device ids already flashed this armed session, decide whether to flash it. Same inputs -> same
/// decision; no IO, no clock. This is the safety-critical heart of autoflash — it is what stops the
/// wrong thing being flashed unattended — and is exhaustively unit-testable as a decision table.
/// </summary>
public static class AutoflashPolicy
{
    /// <summary>Decide what autoflash should do for <paramref name="detected"/>.</summary>
    public static AutoflashAction Decide(
        AutoflashConfig armed,
        FlashTargetView detected,
        // Typed DeviceId, not string: the idempotence check below is the only thing stopping a
        // device being flashed twice per armed session, and a raw HashSet<string> makes that
        // guarantee depend on the caller remembering to pass an OrdinalIgnoreCase comparer —
        // which a returning device in different casing (issue #231) then defeats silently.
        IReadOnlySet<DeviceId> alreadyFlashed)
    {
        ArgumentNullException.ThrowIfNull(armed);
        ArgumentNullException.ThrowIfNull(detected);
        ArgumentNullException.ThrowIfNull(alreadyFlashed);

        // 1. Only the armed family/provider — arming for STM32 must not flash an EFM8 that appears.
        if (!string.Equals(detected.ProviderName, armed.Family, StringComparison.Ordinal))
            return new AutoflashAction.Skip($"not the armed family ('{detected.ProviderName}' != '{armed.Family}')");

        // 2. A passively-identified target says what it is without being touched, so it needs no
        //    binding. A probe-identified one does not: its bridge's VID/PID names the bridge, never
        //    the part behind it, and establishing that part means sending it protocol bytes. The
        //    operator supplies the consent a VID/PID cannot, by binding the bridge at arm time —
        //    so probing is scoped to what they bound, and nothing else (adr.md Decision 8).
        if (detected.Identification != IdentificationMode.Passive
            && (detected.Bridge is not { } bridge || !armed.Bridges.Contains(bridge)))
            return new AutoflashAction.Skip(
                detected.Bridge is null
                    ? $"probe-identified ({detected.Identification}) and its bridge could not be identified"
                    : $"probe-identified ({detected.Identification}) and not on a bound bridge");

        // 3. Idempotent — each physical device is flashed at most once per armed session.
        if (alreadyFlashed.Contains(detected.Id))
            return new AutoflashAction.Skip("already flashed this session");

        return AutoflashAction.Flash.Instance;
    }
}
