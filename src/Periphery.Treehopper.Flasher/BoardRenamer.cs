// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Microsoft.Extensions.Logging;

namespace Periphery.Treehopper.Flasher;

/// <summary>
/// The imperative shell behind the <c>rename</c> verb: enumerate, open a board, write the name, and
/// reboot it. Every decision — what to write, which boards, whether it is legal — is
/// <see cref="BoardRename"/>'s; this only touches USB.
/// </summary>
/// <remarks>
/// Renaming is deliberately <em>not</em> routed through <c>FlashAnythingService</c>. A rename is not a
/// flash: it speaks the Treehopper application protocol to a board that must stay in application mode,
/// where the flasher's whole model (enter the bootloader, write an image, leave) does not apply. It
/// lives in this composition because it is Treehopper-specific, exactly like the curated registry.
/// </remarks>
public static class BoardRenamer
{
    /// <summary>Snapshots the connected Treehopper boards — the candidates a rename selects from.</summary>
    public static Task<IReadOnlyList<DeviceInfo>> DiscoverAsync(CancellationToken ct = default)
        => TreehopperBoard.EnumerateAsync(ct);

    /// <summary>
    /// Writes <paramref name="name"/> to one board's EEPROM, then (unless <paramref name="reboot"/> is
    /// false) reboots it. Returns once the write is acknowledged; throws if it is not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Writing the name and seeing the name are different things, and this only does the first.</b>
    /// The write is durable and verified by the board's acknowledgement. Whether any given host then
    /// <em>reports</em> the new name is a host-cache question this code cannot answer or fix:
    /// </para>
    /// <list type="bullet">
    /// <item>
    /// The reboot is wire opcode <c>0x0C</c>, and it <em>does</em> re-enumerate the board — the
    /// <c>reboot</c> diagnostic verb measures the board off the bus for roughly 200 ms. (An earlier
    /// revision of this note called <c>0x0C</c> an unreliable re-enumeration trigger. That was the
    /// verb's 500 ms poll missing the transient, not the board ignoring the reset.) It is still not
    /// load-bearing <em>here</em>, because re-enumerating and re-reading the name are different
    /// things — see the next point.
    /// </item>
    /// <item>
    /// On Windows the name a tool reads is <c>DEVPKEY_Device_FriendlyName</c>, which the hub driver
    /// writes from the USB <c>iProduct</c> string <em>when the device node is first created</em> and
    /// never refreshes. The node is keyed by serial, so it survives a reboot, a port cycle, a PnP
    /// disable/enable, and a physical replug alike — measured, not assumed. Only rebuilding the node
    /// (<c>pnputil /remove-device</c> then <c>/scan-devices</c>, elevated) re-reads <c>iProduct</c>.
    /// </item>
    /// </list>
    /// <para>
    /// An earlier revision watched for a drop-and-return here and reported "the new name is live".
    /// That was removed: on the platform it matters most for, observing a re-enumeration does not
    /// imply the host will report the new name, so the observation could only mislead.
    /// </para>
    /// </remarks>
    /// <param name="board">The board to rename, from <see cref="DiscoverAsync"/>.</param>
    /// <param name="name">The new device name; validate it with <see cref="BoardRename.ValidateName"/> first.</param>
    /// <param name="reboot">Reboot after writing. Best-effort, and never load-bearing for the write.</param>
    /// <param name="loggerFactory">Optional sink for the board's open/transaction trace.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    public static async Task RenameAsync(
        DeviceInfo board, string name, bool reboot = true,
        ILoggerFactory? loggerFactory = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(board);
        ArgumentNullException.ThrowIfNull(name);

        var handle = await TreehopperBoard.OpenAsync(board, ct, loggerFactory).ConfigureAwait(false);
        bool wrote = false;
        try
        {
            await handle.UpdateNameAsync(name, ct).ConfigureAwait(false);
            wrote = true;
            if (reboot)
                await handle.RebootAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            // The USB link drops as the board reboots, so a close fault is expected on the happy
            // path and must not turn a landed write into a reported failure. It is NOT expected when
            // the write itself faulted — there the close fault may be the root cause (a lost handle
            // surfacing as an EEPROM error), so surface it instead of swallowing it silently.
            try
            {
                await handle.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (wrote)
                    LogDisposeAfterWrite(loggerFactory?.CreateLogger(typeof(BoardRenamer)), ex);
                else
                    LogDisposeAfterFailedWrite(loggerFactory?.CreateLogger(typeof(BoardRenamer)), ex);
            }
        }
    }

    private static void LogDisposeAfterWrite(ILogger? logger, Exception ex) =>
        logger?.LogDebug(ex, "Closing the board after a successful name write faulted; expected as the USB link drops.");

    private static void LogDisposeAfterFailedWrite(ILogger? logger, Exception ex) =>
        logger?.LogWarning(ex, "Closing the board after a FAILED name write faulted; this may be the root cause.");
}
