// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Periphery;

/// <summary>
/// Combinators for <see cref="IResetSafetyGate"/>.
/// </summary>
public static class ResetSafetyGate
{
    /// <summary>
    /// A gate that permits a reset only when <b>every</b> supplied gate permits it. Nulls are
    /// skipped; an all-null or empty set yields <see langword="null"/> (no gate at all), and a
    /// single non-null gate is returned unwrapped rather than needlessly boxed.
    /// </summary>
    /// <remarks>
    /// Exists so that composing configuration can never <em>discard</em> a caller's gate. A safety
    /// gate is a veto, and the safe way to merge two vetoes is to honour both — silently dropping
    /// one (or letting the more specific one shadow the other) turns a caller's explicit refusal
    /// into a reset they did not sanction. Short-circuits on the first refusal, so a gate that is
    /// expensive to evaluate is not consulted once the answer is already no.
    /// </remarks>
    public static IResetSafetyGate? All(params IResetSafetyGate?[]? gates)
    {
        var present = gates?.Where(g => g is not null).Select(g => g!).ToArray() ?? [];
        return present.Length switch
        {
            0 => null,
            1 => present[0],
            _ => new AllGate(present),
        };
    }

    private sealed class AllGate(IReadOnlyList<IResetSafetyGate> gates) : IResetSafetyGate
    {
        public async ValueTask<bool> CanResetAsync(DeviceInfo device, CancellationToken ct)
        {
            foreach (var gate in gates)
            {
                if (!await gate.CanResetAsync(device, ct).ConfigureAwait(false))
                    return false;
            }
            return true;
        }
    }
}
