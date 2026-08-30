// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.InteropServices;

namespace Periphery.Windows;

/// <summary>
/// Device-reset verbs (ADR-0060), kept in a second partial so the reset
/// mechanism sits beside the enumeration / notification P/Invokes it is a
/// sibling of, without bloating the primary file. All calls are cfgmgr32 —
/// no new dependency, AOT-safe via <c>[LibraryImport]</c>.
/// </summary>
internal static unsafe partial class DevNodeHelper
{
    // ── Reset flags ────────────────────────────────────────────────────
    // CM_Query_And_Remove_SubTree: remove without auto-restarting; we drive the
    // re-enumeration of the parent ourselves so the device comes back cleanly.
    private const int CM_REMOVE_NO_RESTART        = 0x00000002;
    // CM_Reenumerate_DevNode: block until the re-enumeration completes.
    private const int CM_REENUMERATE_SYNCHRONOUS  = 0x00000001;

    // ── Native imports ─────────────────────────────────────────────────

    [LibraryImport("cfgmgr32.dll")]
    private static partial int CM_Disable_DevNode(int dnDevInst, int ulFlags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial int CM_Enable_DevNode(int dnDevInst, int ulFlags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial int CM_Get_Parent(out int pdnDevInst, int dnDevInst, int ulFlags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Query_And_Remove_SubTreeW")]
    private static partial int CM_Query_And_Remove_SubTree(
        int dnAncestor, out int pVetoType, nint pszVetoName, int ulNameLength, int ulFlags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial int CM_Reenumerate_DevNode(int dnDevInst, int ulFlags);

    // ── Wrappers ───────────────────────────────────────────────────────

    /// <summary>
    /// Returns the parent devnode of <paramref name="devInst"/>, or <c>null</c>
    /// if it has none (a root) or the call fails.
    /// </summary>
    internal static int? GetParent(int devInst)
        => CM_Get_Parent(out int parent, devInst, 0) == CR_SUCCESS ? parent : null;

    /// <summary>
    /// Disables the devnode (stops the driver; the instance stays enumerated).
    /// <c>true</c> on success. Pair with <see cref="EnableDevNode"/> — never
    /// leave a devnode disabled.
    /// </summary>
    internal static bool DisableDevNode(int devInst)
        => CM_Disable_DevNode(devInst, 0) == CR_SUCCESS;

    /// <summary>Re-enables a previously disabled devnode. <c>true</c> on success.</summary>
    internal static bool EnableDevNode(int devInst)
        => CM_Enable_DevNode(devInst, 0) == CR_SUCCESS;

    /// <summary>
    /// <see langword="true"/> when <paramref name="instanceId"/>'s node is located,
    /// <b>started</b> (<c>DN_STARTED</c>, not merely enabled), not flagged disconnected,
    /// and reports <c>CM_PROB_NONE</c> — i.e. the driver stack has finished (re)loading and
    /// the device is ready to be opened.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Re-locates by instance id on every call rather than taking a cached
    /// <c>devInst</c>, because the handle a caller captured before a reset is not
    /// guaranteed to still address the node afterwards.
    /// </para>
    /// <para>
    /// <b>Measured against ground truth</b> (periphery #251, 4 disable/enable cycles on
    /// a Treehopper): this predicate goes true 14.9–16.2 ms <em>before</em> the WinUSB
    /// interface actually opens, and one call costs ~0.07 ms. It is therefore cheap
    /// enough to poll tightly and slightly optimistic — callers that need the interface
    /// itself should keep whatever small settle margin they already apply.
    /// </para>
    /// </remarks>
    internal static bool IsDevNodeReady(string instanceId)
    {
        if (LocateDevNode(instanceId) is not { } devInst)
            return false;

        // ONE status read, not a composition of IsDeviceConnected + GetProblemCode. Those
        // issue a CM_Get_DevNode_Status each, so the started bits and the problem code can
        // describe two different instants — and a node mid-restart is exactly when they
        // disagree, which is the only moment this predicate is asked about.
        if (CM_Get_DevNode_Status(out int status, out int problem, devInst, 0) != CR_SUCCESS)
            return false;

        return (status & DN_STARTED) != 0               // started, not merely enabled
            && (status & DN_DEVICE_DISCONNECTED) == 0   // and still on the bus
            && problem == CM_PROB_NONE;                 // and the OS reports nothing wrong
    }

    /// <summary>cfgmgr32 <c>CM_PROB_NONE</c> — the OS reports no problem with the node.</summary>
    private const int CM_PROB_NONE = 0;

    /// <summary>
    /// Removes the device subtree rooted at <paramref name="devInst"/> (without
    /// auto-restart), so a subsequent <see cref="ReenumerateDevNode"/> of the
    /// parent forces a full re-enumeration. <c>true</c> if the remove was not
    /// vetoed (e.g. by an open handle elsewhere).
    /// </summary>
    internal static bool QueryAndRemoveSubTree(int devInst)
        => CM_Query_And_Remove_SubTree(devInst, out _, nint.Zero, 0, CM_REMOVE_NO_RESTART) == CR_SUCCESS;

    /// <summary>
    /// Synchronously re-enumerates the devnode (use the parent after a
    /// <see cref="QueryAndRemoveSubTree"/> to bring the device back).
    /// <c>true</c> on success.
    /// </summary>
    internal static bool ReenumerateDevNode(int devInst)
        => CM_Reenumerate_DevNode(devInst, CM_REENUMERATE_SYNCHRONOUS) == CR_SUCCESS;
}
