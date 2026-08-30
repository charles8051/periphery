// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Threading;
using System.Threading.Tasks;

namespace Periphery;

/// <summary>
/// A narrow predicate the proxy consults before executing a
/// <see cref="RecoveryDirective.Reset"/>: is it safe to reset the
/// device right now? Injected into
/// <see cref="DeviceProxyBase{TDevice,TException}"/> (ADR-0060 Decision 4).
/// </summary>
/// <remarks>
/// One boolean dependency — DI of a <em>decision</em>, not a live feed of system
/// state. The consumer closes over whatever it cares about — an
/// "is a sale in progress?" predicate, say. The default, when none is
/// injected, is always-safe.
/// </remarks>
public interface IResetSafetyGate
{
    /// <summary>
    /// <see langword="true"/> if a reset of <paramref name="device"/> is safe to
    /// perform now. When <see langword="false"/>, the proxy defers the reset and
    /// re-decides shortly rather than disrupting an in-progress operation.
    /// </summary>
    ValueTask<bool> CanResetAsync(DeviceInfo device, CancellationToken ct);
}
