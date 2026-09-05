// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Periphery.Bootloader;

public static partial class BootloaderEntryOrchestrator
{
    /// <summary>
    /// Flashes, then independently confirms the write in a genuinely separate, later bootloader
    /// session — retrying the whole flash when that confirmation reports a mismatch (periphery#246:
    /// the EFM8 factory bootloader has been observed to acknowledge a write it had not actually
    /// committed, on a family whose <see cref="IFirmwareProgrammer"/> has no in-session read-back to
    /// catch it). Built entirely from two calls to the existing, unmodified <see cref="RunAsync{TResult}"/>
    /// — one running <paramref name="flash"/>, one running <paramref name="verify"/> — so neither call
    /// site's careful correlation/recovery/safety-gate logic is duplicated or re-implemented here.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Opt-in, not automatic for every family.</b> A family with no independent verify capability
    /// (e.g. STM32 DFU, which already does a genuine in-session read-back per block via
    /// <see cref="FlashOptions.Verify"/>) should never pay for a second, unnecessary reboot-into-
    /// bootloader round-trip. Callers only reach for this method when <see cref="IBootloaderEntry.CanVerify"/>
    /// is <see langword="true"/> — see <c>FlashAnythingService.RebootAndFlashApplicationAsync</c> for
    /// the call site that makes that decision before any device interaction begins.
    /// </para>
    /// <para>
    /// <b>Correlation between rounds uses the returned device, never a stale snapshot.</b> Each
    /// round's own <see cref="BootloaderEntryResult{TResult}.ApplicationDevice"/> — the fresh
    /// discovery snapshot the orchestrator itself just correlated — feeds the next round's
    /// <paramref name="applicationDevice"/> parameter. Re-using the original, pre-flash snapshot
    /// across a retry would risk re-entering against a device id that changed case, or worse,
    /// silently adopting the wrong physical board (ADR-0063 DEC-005).
    /// </para>
    /// </remarks>
    /// <param name="entry">The device-specific mode switch, shared by both rounds.</param>
    /// <param name="applicationDevice">The discovery snapshot of the device in application mode, before the first flash.</param>
    /// <param name="flash">Opens and flashes the correlated bootloader device. May run more than once (a retry re-flashes).</param>
    /// <param name="verify">
    /// Opens the correlated bootloader device and independently confirms the just-flashed content,
    /// returning whether it matched. Invoked once per attempt, only after that attempt's flash
    /// succeeded and the application was confirmed to have returned.
    /// </param>
    /// <param name="flashSucceeded">Decides, from a flash attempt's result, whether it succeeded at all (a hard failure skips verify and ends the run).</param>
    /// <param name="options">
    /// Timeouts, correlation mode, and recovery — shared by both rounds. <see cref="BootloaderEntryOptions.ApplicationFilter"/>
    /// is required to run this loop safely; when left <see langword="null"/>, a filter is derived from
    /// <paramref name="applicationDevice"/>'s own identity — its USB port <em>and</em> serial, falling
    /// back to its USB vendor/product id only when it exposes no such identity. A supplied filter is
    /// used verbatim; supply one that admits <b>only the board being flashed</b>, because it decides
    /// which board the verify round re-enters and which board a retry re-flashes. A model-wide filter
    /// adopts a sibling of the same model that is already on the bus (periphery#173) — see
    /// <c>WithApplicationFilter</c>.
    /// </param>
    /// <param name="maxAttempts">How many flash-then-verify cycles to try before giving up on a persistent mismatch. Must be at least 1.</param>
    /// <param name="phase">
    /// Reported for the flash round only. The verify round runs silently on this channel — a caller
    /// mapping <see cref="BootloaderEntryPhase.Entering"/> to "a fresh attempt has started" (as
    /// <c>FlashAnythingService</c> does, resetting a target's displayed result) would otherwise see
    /// a second Entering right after the flash's own success is reported, with nothing to replace it
    /// — the verify round confirms an already-flashed device, it does not start over.
    /// </param>
    /// <param name="waitSource">Device discovery source, as <see cref="RunAsync{TResult}"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="options"/> has no <see cref="BootloaderEntryOptions.ApplicationFilter"/> and
    /// <paramref name="applicationDevice"/> exposes no <see cref="DeviceInfo.VendorId"/> to derive
    /// one from.
    /// </exception>
    public static async Task<VerifiedFlashResult<TResult>> RunWithVerificationAsync<TResult>(
        IBootloaderEntry entry,
        DeviceInfo applicationDevice,
        Func<DeviceInfo, CancellationToken, Task<TResult>> flash,
        Func<DeviceInfo, CancellationToken, Task<bool>> verify,
        Func<TResult, bool> flashSucceeded,
        BootloaderEntryOptions? options = null,
        int maxAttempts = 3,
        IProgress<BootloaderEntryPhase>? phase = null,
        Func<DeviceFilter, IDeviceWaitSource>? waitSource = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(applicationDevice);
        ArgumentNullException.ThrowIfNull(flash);
        ArgumentNullException.ThrowIfNull(verify);
        ArgumentNullException.ThrowIfNull(flashSucceeded);
        if (maxAttempts < 1)
            throw new ArgumentOutOfRangeException(nameof(maxAttempts), "must attempt at least once.");

        var effectiveOptions = WithApplicationFilter(options ?? new BootloaderEntryOptions(), applicationDevice);

        var device = applicationDevice;
        BootloaderEntryResult<TResult>? lastFlash = null;
        BootloaderEntryResult<bool>? lastVerify = null;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            lastFlash = await RunAsync(entry, device, flash, effectiveOptions, flashSucceeded, phase, waitSource, ct)
                .ConfigureAwait(false);

            if (!flashSucceeded(lastFlash.FlashResult) || lastFlash.ApplicationDevice is not { } returned)
                // Either the flash itself failed, or the application never confirmed returning -
                // either way there is nothing safe to independently verify against. Re-entering
                // without proof we hold the device that just came back risks correlating the wrong
                // physical board (ADR-0063 DEC-005) rather than the one this run is responsible for.
                return new VerifiedFlashResult<TResult>(lastFlash.FlashResult, lastFlash.ApplicationReturned, Verified: false, attempt);

            // Deliberately phase: null here, not the caller's phase - a caller driving UI from
            // BootloaderEntryPhase (e.g. FlashAnythingService's Entering -> AppEvent.EnteringBootloader)
            // treats a fresh Entering as the start of a NEW attempt and resets that target's displayed
            // result message accordingly. The verify round re-enters the SAME already-flashed device
            // to confirm it, not to start over; reporting its Entering/WaitingForBootloader through the
            // same phase sink would silently clobber the flash's own just-reported success message
            // with no replacement (verify has no message of its own to put there).
            lastVerify = await RunAsync(
                entry, returned, flash: verify, effectiveOptions, flashSucceeded: static _ => true,
                phase: null, waitSource, ct).ConfigureAwait(false);

            // Verified requires BOTH the content check AND confirmed proof the application actually
            // came back afterward. Efm8VerifyOperation's own leave-transfer (RunAppOnly) can itself be
            // rejected or time out even when the content check matched - reporting Verified: true from
            // the content answer alone would let a board that is still sitting in the bootloader be
            // reported as a healthy, confirmed flash.
            if (lastVerify.FlashResult && lastVerify.ApplicationDevice is not null)
                return new VerifiedFlashResult<TResult>(lastFlash.FlashResult, ApplicationReturned: true, Verified: true, attempt);

            if (lastVerify.ApplicationDevice is not { } confirmedForRetry)
                // The verify round's own app-wait did not confirm a return - whether because of a
                // content mismatch that also failed to leave, or a leave that failed outright. Either
                // way there is no fresh, confirmed device to safely re-enter with: retrying against
                // the stale pre-verify snapshot could re-enter a device that is not actually back, or
                // (given only a USB-id filter, not a true identity) a different physical board
                // entirely. Stop rather than retry on an unproven snapshot.
                return new VerifiedFlashResult<TResult>(lastFlash.FlashResult, lastVerify.ApplicationReturned, Verified: false, attempt);

            // Mismatch, but the verify round's own leave confirmed the app is genuinely back - safe
            // to retry the whole flash against this freshly re-correlated device.
            device = confirmedForRetry;
        }

        return new VerifiedFlashResult<TResult>(lastFlash!.FlashResult, lastVerify!.ApplicationReturned, Verified: false, maxAttempts);
    }

    // The filter the post-flash application wait uses to decide which board came back - and therefore
    // which board the verify round re-enters, and which board a retry re-flashes.
    //
    // IT MUST PIN AN IDENTITY, NOT A MODEL. That wait accepts a PRE-EXISTING match (it is a liveness
    // check, DeviceWaitState.Collecting(FirstAppearance, debouncePreExisting: false)), so a filter
    // built from VID/PID alone is satisfied the instant it arms by any same-model board sitting on
    // the bus - a sibling nobody asked to touch. periphery#173: with two Treehoppers connected, the
    // wait handed back the OTHER board, the verify round rebooted it and checked ITS flash against
    // the image just written to the first, and the mismatch was reported as a failed flash on a
    // board that had in fact flashed correctly (an independent `verify` run afterwards said MATCH).
    // A mismatch that DOES confirm a return is worse than a false FAILED: the retry re-flashes the
    // adopted board.
    //
    // So prefer IdentityFilterFor - the same port-AND-serial conjunction the recovery path already
    // uses, for the same reason, with the same "both or nothing" rule. Its remarks are the full
    // argument for why either half alone is not proof of sameness.
    //
    // The VID/PID fallback remains for a device that exposes no such identity, because the
    // alternative there is refusing to flash at all on a platform that reports no port. It carries
    // the original hazard, and it is the narrowest place left to carry it: one board of a model on
    // the bus is the case it is still correct for.
    private static BootloaderEntryOptions WithApplicationFilter(BootloaderEntryOptions options, DeviceInfo applicationDevice)
    {
        if (options.ApplicationFilter is not null)
            return options;
        if (IdentityFilterFor(applicationDevice) is { } identity)
            return options with { ApplicationFilter = identity };
        if (applicationDevice.VendorId is not { } vid)
            throw new ArgumentException(
                "RunWithVerificationAsync must be able to recognize the application device again "
                + "after each round-trip, but neither options.ApplicationFilter nor "
                + $"applicationDevice.VendorId is set to derive one from ('{applicationDevice.Id}').",
                nameof(applicationDevice));
        return options with { ApplicationFilter = new DeviceFilter().WithUsbId(vid, applicationDevice.ProductId) };
    }
}
