// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Periphery.Hid;

namespace Periphery.Bootloader.Efm8.Usb;

/// <summary>
/// The hardened "open an EFM8 bootloader device, replay a verify-only record stream, then
/// unconditionally attempt to leave" operation — independent confirmation of a device's current
/// flash content, without a reflash (periphery#246: <see cref="Efm8HidProgrammer.FlashAsync"/>'s own
/// embedded check is not proof a write landed). Generic to any EFM8-based device family: both
/// <c>Periphery.Treehopper.Firmware</c>'s standalone <c>VerifyFromFileAsync</c> and
/// <c>TreehopperBootloaderEntry</c>'s <see cref="IBootloaderEntry.VerifyAsync"/> implementation
/// (driving <see cref="BootloaderEntryOrchestrator.RunWithVerificationAsync{TResult}"/>'s automatic
/// post-flash confirmation) call this rather than each hand-rolling the same recovery hardening.
/// </summary>
/// <remarks>
/// This is the imperative shell (ADR-0052) over <see cref="Efm8BootloaderUploader"/>'s pure
/// record-replay core: it owns the HID handle, the open-retry timing, and the leave-transfer's
/// deliberately uncancelled recovery attempts.
/// </remarks>
public static class Efm8VerifyOperation
{
    // How many times to retry the initial HidDevice.OpenAsync before giving up. Each attempt is
    // separated by OpenRetryDelay. Hardware-observed (periphery#246 follow-up): shortly after the
    // bootloader arrives, Windows can still be finishing registration of the HID child interface's
    // symbolic link, and a CreateFile issued in that window fails outright with no transport ever
    // created — meaning there would be nothing left to send a leave-transfer over and no way to
    // recover the board from this call at all. A real flash's much longer duration (100+ records vs.
    // a verify-only stream's handful) evidently outlasts that window far more often, which is almost
    // certainly why this was never observed on the flash path before periphery#246 went looking.
    private const int OpenRetryAttempts = 5;
    private static readonly TimeSpan OpenRetryDelay = TimeSpan.FromMilliseconds(150);

    /// <summary>
    /// Opens <paramref name="bootloaderDevice"/>, replays <paramref name="verifyRecords"/> (a
    /// Setup-and-Verify-only stream — see <see cref="Efm8BootRecordGenerator.VerifyOnly"/> and
    /// <see cref="Efm8BootRecordGenerator.VerifyOnlyFromBlob"/>), and <b>always</b> follows up with a
    /// separate RunAppOnly transfer — on the success path, on a verify exception, AND (via a further
    /// independent open attempt) when the open itself never succeeded at all.
    /// <see cref="Efm8BootloaderUploader"/> stops at the first non-Acknowledge reply, so without an
    /// unconditional leave, a genuine mismatch (a non-Acknowledge Verify reply) would strand the
    /// board in the bootloader — the one outcome this exists to report on, not to cause. Every leave
    /// attempt runs on its own <see cref="CancellationToken.None"/>, deliberately independent of
    /// <paramref name="ct"/>: a cancellation is often exactly why recovery is needed, and passing an
    /// already-cancelled token to the leave transfer would throw immediately, defeating
    /// "unconditional" at the one moment it matters. Each leave attempt is still bounded
    /// (<see cref="Efm8BootloaderUploader"/>'s own per-reply deadline applies regardless of the
    /// token), so ignoring cancellation here cannot hang the process.
    /// </summary>
    /// <exception cref="Efm8BootloaderException">
    /// The device could not be opened after <see cref="OpenRetryAttempts"/> attempts (message notes
    /// whether a final recovery attempt got it back to its application), or the verify upload itself
    /// threw (message notes whether the follow-up leave attempt succeeded — there is no independent
    /// "did the app come back" signal on this path, since the caller's own post-flash app-wait never
    /// runs if this method throws).
    /// </exception>
    public static async Task<Efm8UploadResult> RunAsync(
        DeviceInfo bootloaderDevice, byte[] verifyRecords, CancellationToken ct)
    {
        HidDevice? hid = null;
        Exception? lastOpenFailure = null;
        OperationCanceledException? openCancellation = null;
        try
        {
            for (int attempt = 1; attempt <= OpenRetryAttempts && hid is null; attempt++)
            {
                try
                {
                    hid = await HidDevice.OpenAsync(bootloaderDevice, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Catch every ordinary failure, including the FINAL attempt's - the "should we
                    // still retry" decision (delay and loop again) is separate from "should we catch
                    // this at all." Folding both into one `when` guard keyed on
                    // `attempt < OpenRetryAttempts` would let the single most common case (the last
                    // attempt exhausting the budget with an ordinary exception) escape this whole
                    // method uncaught, bypassing the last-resort recovery below entirely.
                    lastOpenFailure = ex;
                    if (attempt < OpenRetryAttempts)
                        await Task.Delay(OpenRetryDelay, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException oce)
        {
            // Caught here (rather than left to propagate) so the last-resort leave attempt below
            // still runs before this is rethrown - a cancellation mid-open is exactly the case
            // where no transport exists yet and the board may already be sitting in the bootloader.
            openCancellation = oce;
        }

        if (hid is null)
        {
            bool lastResortRecovered = await TryLastResortLeaveAsync(bootloaderDevice).ConfigureAwait(false);
            if (openCancellation is not null)
            {
                // Preserve cancellation as cancellation - callers distinguish
                // OperationCanceledException from an ordinary failure, and a last-resort leave
                // attempt having also been tried doesn't change what this outcome actually is.
                ExceptionDispatchInfo.Capture(openCancellation).Throw();
            }
            throw new Efm8BootloaderException(
                lastResortRecovered
                    ? $"Could not open the bootloader device for a verify check after {OpenRetryAttempts} attempts, "
                      + "but a final recovery attempt succeeded in sending it back to its application - check "
                      + "with 'list' to confirm before assuming it needs manual recovery."
                    : $"Could not open the bootloader device for a verify check after {OpenRetryAttempts} attempts, "
                      + "and a final recovery attempt could not reach it either. The board is likely still "
                      + "sitting in the EFM8 bootloader - check with 'list', and recover with a targeted "
                      + "'flash' or a physical replug if needed.",
                lastOpenFailure!);
        }

        try
        {
            var transport = new HidEfm8Transport(hid);
            // Tracks the ONE leave attempt's actual outcome across both the success path and the
            // catch blocks below, so a cancellation observed just after a successful leave (via
            // ct.ThrowIfCancellationRequested()) reuses that result instead of sending a second,
            // redundant RunAppOnly transfer to a board that has already left the bootloader.
            bool? leaveConfirmed = null;
            try
            {
                var result = await Efm8BootloaderUploader.UploadAsync(
                    transport, verifyRecords, Efm8FlashConfirmation.ConfirmEraseAndReflash, ct: ct).ConfigureAwait(false);
                leaveConfirmed = await TryLeaveAsync(transport).ConfigureAwait(false);
                // The uncancelled leave attempt above must run either way, but a cancellation
                // requested during/after the verify upload must still surface once it has - a
                // caller must not see this as a clean, successful result just because the record
                // loop happened to finish before the cancellation was observed.
                ct.ThrowIfCancellationRequested();
                return result;
            }
            catch (OperationCanceledException oce)
            {
                leaveConfirmed ??= await TryLeaveAsync(transport).ConfigureAwait(false);
                // Cancellation must stay cancellation for callers (e.g. a CLI's --all loop) to tell
                // it apart from an ordinary failure, so the type can't change - but a caller that
                // inspects Data can still learn whether the board was actually recovered before this
                // propagates. oce is the same instance `throw;` rethrows, so mutating it here is
                // visible on the exception the caller receives.
                oce.Data["LeaveConfirmed"] = leaveConfirmed.Value;
                throw;
            }
            catch (Exception verifyFault)
            {
                leaveConfirmed ??= await TryLeaveAsync(transport).ConfigureAwait(false);
                throw new Efm8BootloaderException(
                    leaveConfirmed.Value
                        ? "The verify check failed, but the board was returned to its application - see the "
                          + "inner exception for the verification failure."
                        : "The verify check failed, and the attempt to return the board to its application also "
                          + "failed; it is likely still sitting in the EFM8 bootloader. See the inner exception "
                          + "for the verification failure.",
                    verifyFault);
            }
        }
        finally
        {
            // A finally block's own exception replaces whatever was propagating from the try above -
            // an ordinary C# hazard that would otherwise let a disposal failure mask the real verify
            // result or exception (and its recovery diagnostics) that this whole method exists to
            // report faithfully. Best-effort, matching TryLeaveAsync/TryLastResortLeaveAsync's own
            // philosophy: the handle not disposing cleanly is a leak worth knowing about separately,
            // never a reason to overwrite the actual outcome.
            try
            {
                await hid.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Swallowed deliberately - see remarks above.
            }
        }
    }

    // Sends the RunAppOnly leave-transfer over an already-open transport. Best-effort and
    // non-throwing by design: every caller already has its own outcome (a result or an exception) to
    // report, and a leave failure must inform that outcome, never replace or mask it. Returns
    // whether the RunApp record was actually acknowledged - UploadAsync reports a rejected or
    // stalled reply as a non-throwing Efm8UploadResult with Success == false, not an exception, so
    // "didn't throw" is not the same question as "did it work." Getting this wrong would report a
    // board still sitting in the bootloader as successfully returned to its application.
    private static async Task<bool> TryLeaveAsync(HidEfm8Transport transport)
    {
        try
        {
            var result = await Efm8BootloaderUploader.UploadAsync(
                transport, Efm8BootRecordGenerator.RunAppOnly(), Efm8FlashConfirmation.ConfirmEraseAndReflash,
                ct: CancellationToken.None).ConfigureAwait(false);
            return result.Success;
        }
        catch
        {
            return false;
        }
    }

    // The last-resort recovery path when the open retry budget is exhausted (or cancelled) and no
    // transport was ever created: one further, independent open attempt on CancellationToken.None,
    // purely to send the leave-transfer. Never throws - a failure here is just "no, that didn't work
    // either," which the caller already reports via lastOpenFailure.
    private static async Task<bool> TryLastResortLeaveAsync(DeviceInfo bootloaderDevice)
    {
        try
        {
            var hid = await HidDevice.OpenAsync(bootloaderDevice, CancellationToken.None).ConfigureAwait(false);
            try
            {
                return await TryLeaveAsync(new HidEfm8Transport(hid)).ConfigureAwait(false);
            }
            finally
            {
                await hid.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch
        {
            return false;
        }
    }
}
