// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;

namespace Periphery;

/// <summary>
/// Pure, total classification of whether an enumerated device's OS-reported
/// status represents a <em>genuine, resettable fault</em> — the signal the
/// faulted-node recovery trigger keys on (ADR-0060 Decision 11). A device that
/// enumerates but never reaches <see cref="DeviceActivityStatus.Active"/> sits in
/// the tracker's <see cref="DeviceActivityStatus.Present"/> bucket, the SAME
/// bucket as a perfectly healthy paired-but-out-of-range Bluetooth device, so the
/// proxy cannot reset blindly on "present but not active". This classifier draws
/// the line: only a real fault is a reset candidate.
/// </summary>
/// <remarks>
/// <para>Kept pure and platform-agnostic per the functional-core / imperative-shell
/// split (ADR-0052): the classifier is a total function of
/// (<see cref="DeviceStatus"/>, problem code) with no IO, no clock, and no mutable
/// state, exhaustively unit-testable with hand-built <see cref="DeviceInfo"/>
/// values. The imperative shell (<see cref="DeviceProxyBase{TDevice,TException}"/>)
/// owns the settle-window clock, the <see cref="IDeviceReset"/> call, and the
/// tracker subscription.</para>
/// <para><b>Cross-platform contract.</b> The trigger is keyed on the cross-platform
/// <see cref="DeviceStatus.Error"/> (which Windows, Linux, and macOS all set); the
/// Windows-only <c>RawStatus</c> problem code is used <em>only to refine / exclude</em>
/// (never auto-enable a user/policy-disabled node, never touch a node the OS says
/// has no problem). A non-Windows device that carries no problem code falls back to
/// <see cref="DeviceStatus.Error"/> as the signal and <see cref="DeviceStatus.Disabled"/>
/// as hands-off.</para>
/// </remarks>
public static class DeviceFaultClassifier
{
    // ── Windows cfgmgr32 CM_PROB_* problem codes (subset; see WellKnownProperties.RawStatus) ──

    /// <summary>CM_PROB_NONE — the OS reports no problem. Authoritative "not a fault".</summary>
    public const int CmProbNone = 0;

    /// <summary>CM_PROB_FAILED_START (10) — the device failed to start. Resettable.</summary>
    public const int CmProbFailedStart = 10;

    /// <summary>CM_PROB_DISABLED (22) — disabled by user / policy. NEVER auto-enable.</summary>
    public const int CmProbDisabled = 22;

    /// <summary>CM_PROB_FAILED_POST_START (21) — pending removal / failed post-start. Resettable.</summary>
    public const int CmProbFailedPostStart = 21;

    /// <summary>CM_PROB_FAILED_DRIVER_ENTRY (31) — the driver failed to load. Resettable.</summary>
    public const int CmProbFailedDriverEntry = 31;

    /// <summary>CM_PROB_DEVICE_RESET_FAILED / device-reported problem (43). Resettable.</summary>
    public const int CmProbDeviceReportedProblem = 43;

    /// <summary>
    /// <see langword="true"/> when <paramref name="device"/> reports a genuine,
    /// resettable OS-level fault — a node the disable/enable or port-cycle reset
    /// rungs may be able to clear. Reads <see cref="DeviceInfo.Status"/> and the
    /// optional Windows <c>RawStatus</c> problem code from
    /// <see cref="DeviceInfo.Properties"/>.
    /// </summary>
    public static bool IsResettableFault(DeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return IsResettableFault(device.Status, ReadProblemCode(device));
    }

    /// <summary>
    /// The pure core: maps a (<paramref name="status"/>, <paramref name="problemCode"/>)
    /// pair to whether it is a resettable fault. <paramref name="problemCode"/> is
    /// <see langword="null"/> on platforms / devices that expose no cfgmgr32-style
    /// problem code (Linux, macOS, or a Windows node whose code could not be read).
    /// </summary>
    /// <remarks>
    /// Rules, in order (refine / exclude semantics — <see cref="DeviceStatus.Error"/>
    /// is the trigger, the problem code only narrows it):
    /// <list type="number">
    /// <item><see cref="DeviceStatus.Disabled"/> ⇒ <see langword="false"/>: an
    /// intentional user/policy state, never something to fight.</item>
    /// <item>problem code <see cref="CmProbDisabled"/> (22) ⇒ <see langword="false"/>:
    /// the Windows-granular form of the same hands-off rule, even if a provider mapped
    /// the coarse status differently.</item>
    /// <item>problem code <see cref="CmProbNone"/> (0) ⇒ <see langword="false"/>: the
    /// OS says there is no problem; that is authoritative over a stale coarse status.</item>
    /// <item>otherwise ⇒ <see cref="DeviceStatus.Error"/>: the cross-platform fault
    /// signal. Any non-zero, non-disabled problem code (10, 21, 31, 43, ...) maps to
    /// <see cref="DeviceStatus.Error"/> on Windows and is treated as resettable.</item>
    /// </list>
    /// </remarks>
    public static bool IsResettableFault(DeviceStatus status, int? problemCode)
    {
        // (1) Intentionally disabled by user / policy — hands-off on every platform.
        if (status == DeviceStatus.Disabled)
            return false;

        // (2) Windows-granular disabled — never auto-enable a user/policy-disabled node.
        if (problemCode == CmProbDisabled)
            return false;

        // (3) The OS reports no problem — not a fault, regardless of a stale status.
        if (problemCode == CmProbNone)
            return false;

        // (4) The cross-platform fault signal. Healthy-present (OK / Unknown with no
        // problem code — e.g. a Bluetooth device paired but out of range) is left alone.
        return status == DeviceStatus.Error;
    }

    /// <summary>
    /// Reads the Windows cfgmgr32 problem code from
    /// <see cref="DeviceInfo.Properties"/> under <see cref="WellKnownProperties.RawStatus"/>,
    /// or <see langword="null"/> when absent or not an <see cref="int"/> (non-Windows
    /// platforms never populate it).
    /// </summary>
    public static int? ReadProblemCode(DeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return device.Properties.TryGetValue(WellKnownProperties.RawStatus, out var raw) && raw is int code
            ? code
            : null;
    }
}
