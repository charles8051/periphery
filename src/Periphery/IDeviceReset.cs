// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Periphery;

/// <summary>
/// Per-device reset capability: advertises which <see cref="ResetStrategy"/>s a
/// device can attempt, and executes one. Injected into
/// <see cref="DeviceProxyBase{TDevice,TException}"/> so the recovery seam can
/// escalate from retry to reset (ADR-0060).
/// </summary>
/// <remarks>
/// <para>This is the <b>mechanism</b> (the imperative shell): it owns the
/// devnode tree, the platform reset verbs, and any cross-device physical
/// coordination (e.g. coalescing concurrent hub-port cycles — ADR-0060
/// Decision 4). The <see cref="IRecoveryPolicy"/> decides <em>whether</em> to
/// reset; this performs it.</para>
/// <para><b>No open handle is required.</b> A device that has reached
/// <see cref="ConnectionState.GaveUp"/> has no live session, and the reset must
/// still work from the <see cref="DeviceInfo"/> snapshot alone.</para>
/// </remarks>
public interface IDeviceReset
{
    /// <summary>
    /// The reset strategies available for <paramref name="device"/>, gentlest
    /// first. An <b>empty list</b> is the first-class "not resettable" answer
    /// (PS/2, virtual, network devices). Advertisement means "can attempt";
    /// <see cref="ResetAsync"/> may still report
    /// <see cref="ResetOutcome.Degraded"/> / <see cref="ResetOutcome.NotSupported"/>
    /// at runtime.
    /// </summary>
    /// <remarks>
    /// Derived from the device's enumerator / bus-type and topology — no open
    /// required. For a bridged device (USB-serial terminal, HID-as-COM scanner)
    /// the platform layer walks to the resettable USB ancestor.
    /// </remarks>
    IReadOnlyList<ResetStrategy> StrategiesFor(DeviceInfo device);

    /// <summary>
    /// Execute <paramref name="strategy"/> against <paramref name="device"/>.
    /// </summary>
    /// <remarks>
    /// <b>Implementations should not return while the device is still mid-restart</b>
    /// when the platform can cheaply observe that. A caller has no better vantage
    /// point: for a strategy that does not re-enumerate there is no arrival event to
    /// wait on, so anything the implementation does not confirm degrades into a blind
    /// delay at the call site — which is the failure recorded in issue #251.
    /// <para>
    /// This is a best-effort duty, not a guarantee, and it does <b>not</b> promote
    /// <see cref="ResetOutcome.Issued"/> into a health verdict (ADR-0073). The dividing
    /// line is what the rung can observe. A rung that <em>cannot</em> confirm anything —
    /// the EP0 rescue, ADR-0075, where a resetting device and one that ignored the
    /// request fault identically — reports <see cref="ResetOutcome.Issued"/> without
    /// waiting: absence of confirmation. A rung that <em>watched</em> and saw the device
    /// fail to come back should report <see cref="ResetOutcome.Failed"/>: that is
    /// evidence of non-recovery, and reporting success on it is the same over-claiming
    /// this obligation exists to prevent.
    /// </para>
    /// </remarks>
    /// <param name="device">The enumeration snapshot identifying the device (may be unopen).</param>
    /// <param name="strategy">One of the strategies advertised by <see cref="StrategiesFor"/>.</param>
    /// <param name="ct">Cancelled when the owning proxy is disposed.</param>
    /// <returns>The outcome; <see cref="ResetOutcome.Issued"/> on success.</returns>
    ValueTask<ResetOutcome> ResetAsync(DeviceInfo device, ResetStrategy strategy, CancellationToken ct);
}
