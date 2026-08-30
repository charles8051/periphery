// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Periphery.Windows;

/// <summary>
/// Windows <see cref="IDeviceReset"/> mechanism (ADR-0060), built on the cfgmgr32
/// reset verbs in <see cref="DevNodeHelper"/>. The imperative shell: it owns the
/// devnode tree, the platform reset calls, the inter-reset settle timing, and the
/// per-hub coalescing of shared-hub port cycles (Decision 4). No open handle is
/// required — a <see cref="ConnectionState.GaveUp"/> device has none.
/// </summary>
/// <remarks>
/// <b>Elevation:</b> <c>CM_Disable_DevNode</c> / <c>CM_Enable_DevNode</c> generally require the
/// host process to be elevated; a non-elevated caller gets <see cref="ResetOutcome.Failed"/>
/// (logged) and the reset does not run.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class WindowsDeviceReset : IDeviceReset
{
    private static readonly ILogger<WindowsDeviceReset> _logger =
        PeripheryLoggerFactory.CreateLogger<WindowsDeviceReset>();

    // Settle window between disable and enable / remove and re-enumerate: give the
    // driver stack time to tear down before it is rebuilt.
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(750);

    // How long to wait, after CM_Enable_DevNode, for the node to actually come back
    // started and problem-free — see DisableEnableAsync.
    //
    // 2s is a deliberate compromise, and the ceiling matters as much as the floor:
    //   - Floor: the field failure (#251) was a reload outlasting a 750ms blind delay, so
    //     the bound has to clear that with room. This is ~2.7x it, and ~20,000x the
    //     measured healthy-path latency (the node is typically started before
    //     CM_Enable_DevNode even returns, ~0.05-0.17ms).
    //   - Ceiling: this wait is not free when a host has several boards. Treehopper is
    //     an EFM8 no-serial family (every unit's bootloader shares 0x10C4:0xEAC9), so the
    //     reboot -> correlate -> flash window is gated one board at a time — a stall here
    //     blocks that host's *other* boards, and any consumer racing the boards at startup
    //     loses on a margin measured in seconds. An earlier 10s here — chosen only for
    //     symmetry with DeviceProxyBase.ResetReopenTimeout, not from any measurement of
    //     reload duration — put ~30s on a three-board host.
    // Raise this only against a measured restart distribution, never on intuition.
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(2);

    // ~0.07 ms per probe (periphery #251), so a tight interval is essentially free and
    // keeps the common case — the node is back before CM_Enable_DevNode even returns —
    // at a single probe with no added latency.
    private static readonly TimeSpan ReadyPollInterval = TimeSpan.FromMilliseconds(25);

    // A SharedHub cycle of one hub that just happened folds in rather than
    // thrashing the hub when several of its children recover at once.
    private const long HubCoalesceWindowMs = 3_000;

    // Bounds the ancestor walk that resolves a bridged child to its USB node.
    private const int MaxAncestorWalk = 16;

    private readonly object _coalesceLock = new();
    private readonly Dictionary<int, long> _recentHubCycles = new();   // parent devInst -> Environment.TickCount64

    /// <inheritdoc/>
    public IReadOnlyList<ResetStrategy> StrategiesFor(DeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);

        var direct = ResetStrategyMap.ForTransport(device);
        if (direct.Count > 0)
            return direct;

        // Bridged device (HID-as-COM, USB-serial child) whose own snapshot is not
        // USB-marked: if it sits under a USB ancestor, it is still USB-resettable.
        return TryResolveUsbAncestor(device, out _)
            ? ResetStrategyMap.UsbStrategies
            : [];
    }

    /// <inheritdoc/>
    public async ValueTask<ResetOutcome> ResetAsync(
        DeviceInfo device, ResetStrategy strategy, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(device);

        return strategy.Kind switch
        {
            ResetKind.PnpDisableEnable => await DisableEnableAsync(device, ct).ConfigureAwait(false),
            ResetKind.UsbPortCycle     => await PortCycleAsync(device, ct).ConfigureAwait(false),
            // A device-specific soft reset is owned by a device extension, not core. That
            // holds for both soft rungs: the out-of-band one is a vendor request whose wire
            // contract only the device's own firmware defines (ADR-0075).
            ResetKind.SoftProtocol          => ResetOutcome.NotSupported,
            ResetKind.SoftProtocolOutOfBand => ResetOutcome.NotSupported,
            _                          => ResetOutcome.NotSupported,
        };
    }

    // CM_Disable_DevNode + settle + CM_Enable_DevNode on the device's own node —
    // the programmatic equivalent of the manual Disable/Enable that healed the
    // field wedge. Does NOT re-enumerate (ReEnumerates: false).
    //
    // Returns only once the node is back and started, because nothing downstream can
    // learn that for us (periphery #251). This rung produces NO watcher edge at all —
    // the devnode never leaves the tree, it only flips Disabled/CM_PROB_DISABLED ->
    // OK/CM_PROB_NONE — so the caller has no arrival to wait on and would otherwise be
    // reduced to a blind delay. That is exactly what failed in the field: the
    // bootloader orchestrator waited a flat 750 ms and retried, and on loaded
    // hardware the reload had not finished, so the retry's open threw
    // UsbDeviceNotFoundException and burned a recovery attempt against a healthy board.
    private async ValueTask<ResetOutcome> DisableEnableAsync(DeviceInfo device, CancellationToken ct)
    {
        int? devInst = DevNodeHelper.LocateDevNode(device.Id);
        if (devInst is null)
        {
            _logger.LogWarning("Reset (disable/enable): devnode not found for {Id}", device.Id);
            return ResetOutcome.NotSupported;
        }

        if (!DevNodeHelper.DisableDevNode(devInst.Value))
        {
            _logger.LogWarning(
                "Reset (disable/enable): CM_Disable_DevNode failed for {Id} (host process not elevated, or a driver refused). The reset did not run.",
                device.Id);
            return ResetOutcome.Failed;
        }

        _logger.LogInformation(
            "Reset (disable/enable): disabled {Id}, settling {Ms}ms", device.Id, SettleDelay.TotalMilliseconds);

        try { await Task.Delay(SettleDelay, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* re-enable anyway — never leave a devnode disabled */ }

        bool enabled = DevNodeHelper.EnableDevNode(devInst.Value);
        _logger.LogInformation(
            "Reset (disable/enable): re-enable {Id} -> {Result}", device.Id, enabled ? "ok" : "FAILED");
        if (!enabled)
            return ResetOutcome.Failed;

        // Cancellation propagates from here, unlike the settle above: that one is swallowed
        // to guarantee the devnode is never left disabled, and the enable has now happened,
        // so there is no invariant left to protect by finishing the wait.
        string id = device.Id.Value;
        var ready = await ReadinessPoll
            .UntilAsync(() => DevNodeHelper.IsDevNodeReady(id), ReadyTimeout, ReadyPollInterval, ct)
            .ConfigureAwait(false);

        if (ready is null)
        {
            // Failed, not Issued. ADR-0073's "Issued is not a health verdict" licenses
            // reporting Issued when the rung CANNOT observe the outcome — the EP0 rescue,
            // where a resetting device and a device that ignored the request fault
            // identically. That is absence of confirmation. This is the opposite: we
            // watched and positively observed the node not restart. Reporting Issued on
            // evidence of non-recovery would be the same over-claiming this whole change
            // exists to remove, one level up.
            _logger.LogWarning(
                "Reset (disable/enable): {Id} did not report started within {Timeout}s of re-enable; "
                + "reporting Failed. The node may be slow to restart or genuinely faulted.",
                device.Id, ReadyTimeout.TotalSeconds);
            return ResetOutcome.Failed;
        }

        _logger.LogInformation(
            "Reset (disable/enable): {Id} back and started {Ms}ms after re-enable",
            device.Id, ready.Value.TotalMilliseconds);
        return ResetOutcome.Issued;
    }

    // Force a real re-enumeration of the device subtree: remove it, then
    // re-enumerate its parent hub. Produces the DEVICEINSTANCEREMOVED -> STARTED
    // edges the watcher can observe (ReEnumerates: true).
    private async ValueTask<ResetOutcome> PortCycleAsync(DeviceInfo device, CancellationToken ct)
    {
        int target;
        if (ResetStrategyMap.IsUsbBacked(device) && DevNodeHelper.LocateDevNode(device.Id) is { } own)
            target = own;
        else if (TryResolveUsbAncestor(device, out int ancestor))
            target = ancestor;
        else
            return ResetOutcome.NotSupported;

        int? parent = DevNodeHelper.GetParent(target);
        if (parent is null)
            return ResetOutcome.Degraded;   // can't reach the hub to re-enumerate through

        // Coalesce: a sibling just cycled this hub — fold in rather than thrash it.
        if (RecentlyCycled(parent.Value))
        {
            _logger.LogInformation(
                "Reset (port-cycle): hub {Parent} cycled recently; folding in for {Id}", parent.Value, device.Id);
            return ResetOutcome.Issued;
        }

        if (!DevNodeHelper.QueryAndRemoveSubTree(target))
        {
            // Removal vetoed (an open handle elsewhere) — could not force re-enumeration.
            _logger.LogWarning("Reset (port-cycle): remove vetoed for {Id}; degraded", device.Id);
            return ResetOutcome.Degraded;
        }

        try { await Task.Delay(SettleDelay, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { }

        bool back = DevNodeHelper.ReenumerateDevNode(parent.Value);
        RecordCycle(parent.Value);
        _logger.LogInformation(
            "Reset (port-cycle): re-enumerated hub {Parent} for {Id} -> {Result}",
            parent.Value, device.Id, back ? "ok" : "FAILED");
        return back ? ResetOutcome.Issued : ResetOutcome.Degraded;
    }

    // Walk up the devnode tree to the first ancestor whose instance id is a USB
    // node — for bridged children (a USB-serial COM port, a HID-as-COM scanner).
    private static bool TryResolveUsbAncestor(DeviceInfo device, out int usbDevInst)
    {
        usbDevInst = 0;
        int? cur = DevNodeHelper.LocateDevNode(device.Id);

        for (int depth = 0; depth < MaxAncestorWalk && cur is not null; depth++)
        {
            string? id = DevNodeHelper.GetDeviceInstanceId(cur.Value);
            if (id is not null && id.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase))
            {
                usbDevInst = cur.Value;
                return true;
            }
            cur = DevNodeHelper.GetParent(cur.Value);
        }
        return false;
    }

    private bool RecentlyCycled(int parentDevInst)
    {
        long now = Environment.TickCount64;
        lock (_coalesceLock)
            return _recentHubCycles.TryGetValue(parentDevInst, out long when)
                && now - when < HubCoalesceWindowMs;
    }

    private void RecordCycle(int parentDevInst)
    {
        long now = Environment.TickCount64;
        lock (_coalesceLock)
            _recentHubCycles[parentDevInst] = now;
    }
}
