// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Periphery.Bootloader;

/// <summary>
/// The shared, watcher-driven orchestration for flashing an <em>app-mode</em> device: drive an
/// <see cref="IBootloaderEntry"/> through <b>enter → wait-for-expected-bootloader → safety-gate →
/// flash → (optional) wait-for-application</b> (ADR-0063 DEC-003). Generalizes the bespoke
/// reboot/poll/gate/poll glue every app-mode device used to hand-roll, so a new device is one small
/// <see cref="IBootloaderEntry"/> plus a reused flasher.
/// </summary>
/// <remarks>
/// <para>
/// This is the imperative shell (ADR-0052): it owns an injectable <see cref="IDeviceWaitSource"/>
/// (a fresh <see cref="DeviceWatcher"/> by default, or a caller's shared discovery — FlashAnything
/// passes one backed by its <see cref="MultiDeviceTracker"/>), the SDK calls inside
/// <see cref="IBootloaderEntry.EnterAsync"/>, the timeout clock, and the flash. All of the
/// wait/correlation decisions live in the pure <see cref="DeviceWaitState"/> core, advanced here by
/// device-appeared/disappeared events and a shell-owned timeout. It is <b>push, not poll</b> — it
/// listens to the source rather than busy-looping <c>Devices.Enumerate()</c> (DEC-003).
/// </para>
/// <para>
/// The flasher is supplied as a callback so this stays free of any specific flash protocol: slice 1
/// passes a callback that opens the EFM8 HID device and runs the existing
/// <c>Efm8BootloaderUploader</c>; a later slice routes through <see cref="IFirmwareProgrammer"/>
/// instead, with no change here.
/// </para>
/// </remarks>
public static partial class BootloaderEntryOrchestrator
{
    /// <summary>
    /// Reboots <paramref name="applicationDevice"/> into its bootloader via
    /// <paramref name="entry"/>, waits (watcher-driven) for the device to re-enumerate as
    /// <see cref="IBootloaderEntry.ExpectedBootloader"/>, applies the safety gate, invokes
    /// <paramref name="flash"/> against the bootloader device, and — when
    /// <see cref="BootloaderEntryOptions.ApplicationFilter"/> is set and the flash succeeded —
    /// waits for the application to re-appear.
    /// </summary>
    /// <typeparam name="TResult">Whatever <paramref name="flash"/> returns (e.g. an upload result).</typeparam>
    /// <param name="entry">The device-specific mode switch.</param>
    /// <param name="applicationDevice">The discovery snapshot of the device in application mode.</param>
    /// <param name="flash">
    /// Opens and flashes the correlated bootloader device. Invoked exactly once, only after the
    /// safety gate passes. Its handle to the device is the caller's to open and dispose.
    /// </param>
    /// <param name="options">Timeouts, correlation mode, and the optional application-return filter.</param>
    /// <param name="flashSucceeded">
    /// Decides, from the flash result, whether to wait for the application to return. Defaults to
    /// always-wait (when an application filter is set). Treehopper passes <c>r =&gt; r.Success</c> so a
    /// failed upload skips the wait — preserving the bespoke flow's behavior.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="BootloaderEntryException">
    /// The expected bootloader did not re-enumerate within the timeout, or the safety gate refused a
    /// device that did not match <see cref="IBootloaderEntry.ExpectedBootloader"/>.
    /// </exception>
    public static async Task<BootloaderEntryResult<TResult>> RunAsync<TResult>(
        IBootloaderEntry entry,
        DeviceInfo applicationDevice,
        Func<DeviceInfo, CancellationToken, Task<TResult>> flash,
        BootloaderEntryOptions? options = null,
        Func<TResult, bool>? flashSucceeded = null,
        IProgress<BootloaderEntryPhase>? phase = null,
        Func<DeviceFilter, IDeviceWaitSource>? waitSource = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(applicationDevice);
        ArgumentNullException.ThrowIfNull(flash);
        options ??= new BootloaderEntryOptions();
        // Default: a fresh DeviceWatcher per wait (standalone). FlashAnything passes a source backed
        // by its MultiDeviceTracker so the orchestration rides the same discovery (ADR-0063 slice 4).
        var createWaitSource = waitSource ?? (filter => new DeviceWatcherWaitSource(filter));

        string? expectedSerial = null;
        if (options.Correlation == DeviceCorrelationMode.BySerial)
        {
            expectedSerial = applicationDevice.SerialNumber;
            if (string.IsNullOrEmpty(expectedSerial))
                throw new BootloaderEntryException(
                    $"BySerial correlation needs a serial that survives the mode switch, but the "
                    + $"application device '{applicationDevice.Name ?? applicationDevice.Id}' exposes none.");
        }

        string? expectedLocationPath = null;
        if (options.Correlation == DeviceCorrelationMode.ByLocationPath)
        {
            // The physical USB port survives the reboot (the board does not move ports when it resets),
            // so the bootloader re-enumerates on the app device's LocationPath. Capturing it here makes
            // the failure explicit if the platform exposes no port for this device — far better than a
            // silent fall-through that could correlate the wrong board.
            expectedLocationPath = applicationDevice.LocationPath;
            // IsNullOrWhiteSpace, not IsNullOrEmpty: a whitespace-only path could never match a real
            // platform-supplied port, so reject it here with the explicit "exposes no LocationPath"
            // reason rather than letting it degrade to a confusing "did not re-enumerate" timeout.
            if (string.IsNullOrWhiteSpace(expectedLocationPath))
                throw new BootloaderEntryException(
                    $"ByLocationPath correlation needs a USB port that survives the mode switch, but the "
                    + $"application device '{applicationDevice.Name ?? applicationDevice.Id}' exposes no LocationPath. "
                    + $"This platform may not report a stable USB port; only Windows is hardware-verified for "
                    + $"port-invariance across the reboot.");
        }

        // 1. Wait for the bootloader (debounce pre-existing — a bootloader already on the bus is not
        //    ours), triggering EnterAsync after the watcher is armed so the re-enumeration is fresh.
        //    Surface the two app-mode phases the UI renders: Entering (sending the wake command) then
        //    WaitingForBootloader (the device is rebooting; we are watching for it).
        //
        //    When options.Recovery is set this is a LOOP (ADR-0076): a failed mode switch drives the
        //    ADR-0060 recovery seam — reset the device, wait for it to come back, retry — instead of
        //    ending the run. With Recovery null the loop runs exactly once and every failure path
        //    below behaves precisely as it did before recovery existed.
        var recovery = options.Recovery;
        DeviceInfo? bootloaderDevice = null;
        Exception? lastFault = null;
        int attempt = 0;
        int resetCount = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            attempt++;

            phase?.Report(BootloaderEntryPhase.Entering);
            try
            {
                bootloaderDevice = await WaitForDeviceAsync(
                    createWaitSource,
                    entry.ExpectedBootloader,
                    DeviceWaitState.Collecting(options.Correlation, debouncePreExisting: true, expectedSerial, expectedLocationPath),
                    options.BootloaderTimeout,
                    afterArm: async token =>
                    {
                        try
                        {
                            await entry.EnterAsync(applicationDevice, token).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (recovery is not null && !token.IsCancellationRequested)
                        {
                            // Tag it, so the catch below can recover from THIS and nothing else.
                            throw new EntryAttemptFailedException(ex);
                        }
                        phase?.Report(BootloaderEntryPhase.WaitingForBootloader);
                    },
                    ct).ConfigureAwait(false);
            }
            catch (EntryAttemptFailedException tagged)
            {
                // EnterAsync threw — the commonest wedged-device shape, because the mode switch (and
                // the open that precedes it) rides the very data path that is stuck.
                //
                // Deliberately narrow. An earlier revision caught everything thrown anywhere inside
                // WaitForDeviceAsync, which swept in failures that say nothing about the device at
                // all — a disposed IProgress consumer throwing from Report, a watcher that cannot be
                // created — and answered them by RESETTING THE BOARD. A host-side bug must never
                // disrupt hardware. Only a tagged entry failure is recoverable; everything else
                // propagates, and cancellation is excluded at the throw site above.
                lastFault = tagged.InnerException!;
                bootloaderDevice = null;
            }

            if (bootloaderDevice is not null)
                break;

            var timedOut = new BootloaderEntryException(
                $"The expected bootloader for '{entry.Name}' did not re-enumerate within "
                + $"{options.BootloaderTimeout.TotalSeconds:0.#}s after entering the bootloader. "
                + "The device may still be in the bootloader — re-enumerate and retry.");

            if (recovery is null)
            {
                // Original behaviour, byte for byte: a timeout is this exception, and an EnterAsync
                // throw never reached here in the first place.
                throw timedOut;
            }

            lastFault ??= timedOut;

            // The recovery decision is a pure function of a value (ADR-0052 / ADR-0060): the shell
            // gathers the inputs, the policy chooses, ResetEscalation independently validates that
            // the chosen rung is one the device actually advertises.
            var context = new RecoveryContext(
                Attempt: attempt,
                ResetCount: resetCount,
                LastFault: lastFault,
                Device: applicationDevice,
                AvailableResets: recovery.Reset.StrategiesFor(applicationDevice),
                Trigger: RecoveryTrigger.BootloaderEntryFailure);

            switch (recovery.EffectivePolicy.Decide(context))
            {
                case RecoveryDirective.Retry retry:
                    if (retry.Delay > TimeSpan.Zero)
                        await Task.Delay(retry.Delay, ct).ConfigureAwait(false);
                    continue;

                case RecoveryDirective.Reset requested:
                {
                    if (ResetEscalation.Decide(context, requested) is not EscalationDecision.ExecuteDecision exec)
                        throw Exhausted(entry, attempt, resetCount, lastFault,
                            "the recovery policy asked for a reset strategy the device does not advertise");

                    // Safety gate before any device-disrupting reset (ADR-0060 Decision 4). Unlike the
                    // proxy this aborts rather than deferring — see BootloaderEntryRecovery.SafetyGate.
                    if (recovery.SafetyGate is not null
                        && !await recovery.SafetyGate.CanResetAsync(applicationDevice, ct).ConfigureAwait(false))
                    {
                        throw Exhausted(entry, attempt, resetCount, lastFault,
                            $"a {exec.Strategy.Kind} reset was needed but the reset safety gate refused it; "
                            + "re-run the update when the device is idle");
                    }

                    phase?.Report(BootloaderEntryPhase.Recovering);
                    resetCount++;

                    // A rung that re-enumerates gives us a fresh appearance to wait for, and the
                    // DeviceInfo we hold is a snapshot that the reset invalidates (the id can even
                    // change case across re-enumeration — periphery #231), so re-acquire it. Arm
                    // first, reset inside afterArm: the pre-reset instance is then the debounce
                    // baseline and the post-reset one is a genuine fresh appearance.
                    //
                    // ADOPTING A DEVICE REQUIRES PROVING IT IS THE SAME PHYSICAL BOARD. VID/PID is
                    // not an identity — every board of a model shares it — so correlation must key
                    // on something invariant across the reset: the USB port, else the serial. There
                    // is deliberately NO FirstAppearance fallback here. That fallback is what #220
                    // already cost us once: FlashAnything flashes concurrently by default, so a
                    // sibling board re-enumerating inside our window is ordinary, and adopting it
                    // would point the retry — and therefore the flash — at the wrong hardware.
                    // Without an invariant identity we do not wait and do not adopt.
                    ResetOutcome outcome = ResetOutcome.Failed;
                    var identity = IdentityFilterFor(applicationDevice);

                    // IDENTITY LIVES IN THE FILTER. Reset first, let the board go, then look for a
                    // device on the same PHYSICAL PORT carrying the same SERIAL — see
                    // IdentityFilterFor for why it takes both, and what the pair still cannot tell
                    // apart. Nothing else is ever surfaced, so nothing else can be adopted.
                    //
                    // Deliberately NOT keyed on observing the disappearance. Two earlier revisions
                    // failed here, both instructively:
                    //   - DeviceWaitState's BySerial/ByLocationPath "correlate immediately" on an
                    //     already-present match, and our board IS present when the wait arms — it has
                    //     not been reset yet — so they returned the pre-reset snapshot without ever
                    //     waiting, silently defeating the refresh. Fakes with no snapshot hid it.
                    //   - FirstAppearance + debouncePreExisting needs the removal event to clear the
                    //     baseline, but the source filters removals through the same DeviceFilter and
                    //     a removal's DeviceInfo does not carry LocationPath, so Disappeared never
                    //     fired for an identity filter and the board's return was debounced away.
                    //     That one cost a 117 s hardware failure where the shipped path took 39 s.
                    // The settle-then-look shape depends on neither.
                    outcome = await recovery.Reset
                        .ResetAsync(applicationDevice, exec.Strategy, ct).ConfigureAwait(false);
                    await Task.Delay(SettleAfterReset, ct).ConfigureAwait(false);

                    if (exec.Strategy.ReEnumerates && identity is { } identityFilter)
                    {
                        var returned = await WaitForDeviceAsync(
                            createWaitSource,
                            identityFilter,
                            // debouncePreExisting: false — with an identity-pinned filter there is
                            // nothing to debounce against. A present match is our board whether it is
                            // the post-reset instance or (if the settle was short) the pre-reset one,
                            // and either is a safe thing to hold.
                            DeviceWaitState.Collecting(
                                DeviceCorrelationMode.FirstAppearance,
                                debouncePreExisting: false),
                            recovery.EffectiveReturnTimeout,
                            afterArm: null,
                            ct).ConfigureAwait(false);

                        // Null means it never came back inside the window. Retry anyway rather than
                        // abort: the next entry attempt fails fast and the policy escalates to a
                        // harder rung, which is more useful than stopping on a rung that may simply
                        // have been too gentle. Keeping the stale snapshot is safe precisely because
                        // it is OUR board's id — a stale id fails to open, it never resolves to a
                        // different board — whereas adopting an uncorrelated appearance would not be.
                        if (returned is not null)
                            applicationDevice = returned;
                    }

                    // ResetOutcome.Issued is NOT a confirmation for every rung — the EP0 rescue in
                    // particular cannot be confirmed from the transfer (ADR-0075), so we never treat
                    // it as proof and never treat its absence as fatal. A rung the platform could not
                    // run at all is simply a wasted attempt; the policy escalates on the next pass.
                    lastFault = new BootloaderEntryException(
                        $"{exec.Strategy.Kind} reset reported {outcome}, but '{entry.Name}' still did "
                        + "not enter its bootloader.", lastFault);
                    continue;
                }

                default:
                    throw Exhausted(entry, attempt, resetCount, lastFault, "the recovery policy gave up");
            }
        }

        // 2. Safety gate (ADR-0063 DEC-005). The watcher already filtered to ExpectedBootloader, so
        //    this is defense-in-depth: the device-specific code never gets to flash the wrong thing.
        if (!entry.ExpectedBootloader.Matches(bootloaderDevice))
            throw new BootloaderEntryException(
                $"Refusing to flash: the correlated device "
                + $"{Describe(bootloaderDevice)} does not match '{entry.Name}'s expected bootloader.");

        // 3. Flash (the reusable half).
        TResult flashResult = await flash(bootloaderDevice, ct).ConfigureAwait(false);

        // 4. Optionally wait for the application to come back. Liveness check, not a re-enumeration
        //    correlation, so it accepts a pre-existing match (behaviour of the original poll).
        bool applicationReturned = false;
        DeviceInfo? appDevice = null;
        bool wantAppWait = options.ApplicationFilter is not null
            && (flashSucceeded?.Invoke(flashResult) ?? true);
        if (wantAppWait)
        {
            appDevice = await WaitForDeviceAsync(
                createWaitSource,
                options.ApplicationFilter!,
                DeviceWaitState.Collecting(DeviceCorrelationMode.FirstAppearance, debouncePreExisting: false),
                options.ApplicationTimeout,
                afterArm: null,
                ct).ConfigureAwait(false);
            applicationReturned = appDevice is not null;
        }

        return new BootloaderEntryResult<TResult>(flashResult, applicationReturned, appDevice);
    }

    /// <summary>
    /// Source-driven wait: opens an <see cref="IDeviceWaitSource"/> over <paramref name="filter"/>
    /// (a fresh watcher, or a caller's shared discovery), feeds appeared/disappeared events into the
    /// pure <paramref name="initial"/> state, arms it (freezing the pre-existing baseline), runs the
    /// optional <paramref name="afterArm"/> trigger (the SDK reboot), then awaits a correlation or the
    /// shell-owned timeout. Returns the correlated device, or <c>null</c> on timeout.
    /// </summary>
    private static async Task<DeviceInfo?> WaitForDeviceAsync(
        Func<DeviceFilter, IDeviceWaitSource> createWaitSource,
        DeviceFilter filter,
        DeviceWaitState initial,
        TimeSpan timeout,
        Func<CancellationToken, Task>? afterArm,
        CancellationToken ct)
    {
        var gate = new object();
        var state = initial;
        var done = new TaskCompletionSource<DeviceInfo>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Advance(Func<DeviceWaitState, DeviceWaitState> transition)
        {
            lock (gate)
            {
                if (state.IsComplete) return;
                state = transition(state);
                if (state.Status == DeviceWaitStatus.Correlated)
                    done.TrySetResult(state.Correlated!);
            }
        }

        void OnAppeared(DeviceInfo device) => Advance(s => s.OnAppeared(device));
        void OnDisappeared(string id) => Advance(s => s.OnDisappeared(id));

        await using var source = createWaitSource(filter);
        source.Appeared += OnAppeared;
        source.Disappeared += OnDisappeared;

        // StartAsync fires Appeared for every already-present candidate (the snapshot) before it
        // returns, so by the time we arm, the pre-existing baseline is complete.
        await source.StartAsync(ct).ConfigureAwait(false);
        Advance(s => s.Arm());

        // Trigger the re-enumeration only after arming, so the device we just rebooted counts as a
        // fresh appearance rather than being folded into the debounce baseline.
        if (afterArm is not null)
            await afterArm(ct).ConfigureAwait(false);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            return await done.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            Advance(s => s.OnTimeout());
            return null;
        }
    }

    private static string Describe(DeviceInfo device)
        => $"{device.VendorId?.ToString() ?? "?"}:{device.ProductId?.ToString() ?? "?"}";

    /// <summary>
    /// How long to let a reset take effect before looking for the device again. Biases the look
    /// toward the post-reset instance rather than the one that has not dropped off the bus yet. A
    /// Treehopper is measured off the bus ~230 ms with real remove/arrive edges (periphery #232), so
    /// this clears that with margin while staying short enough not to pad the run.
    /// </summary>
    /// <remarks>
    /// <b>This is a bias, not a readiness check</b> — do not let it become one again. It used to be
    /// the only thing standing between a non-re-enumerating reset and the next
    /// <see cref="IBootloaderEntry.EnterAsync"/>, and periphery #251 is what that cost: on loaded
    /// loaded hardware the driver stack had not finished reloading after 750 ms, the retry's open threw
    /// <c>UsbDeviceNotFoundException</c>, and the wasted attempt eventually exhausted the recovery
    /// budget on boards that were perfectly healthy. Establishing that a device is back is the reset
    /// mechanism's job (<see cref="IDeviceReset.ResetAsync"/>), because only it knows what "back"
    /// means on that platform; this delay must never be tuned upward to compensate for a rung that
    /// does not.
    /// </remarks>
    private static readonly TimeSpan SettleAfterReset = TimeSpan.FromMilliseconds(750);

    /// <summary>
    /// A filter matching the application device itself, for the post-reset return wait. Built from
    /// its USB id rather than taken from <see cref="BootloaderEntryOptions.ApplicationFilter"/> so
    /// recovery works whether or not the caller configured that (it is optional and exists for a
    /// different purpose — the post-flash liveness check). Correlation still pins it to the original
    /// LocationPath where the platform reports one, so a sibling board on the same bus is never
    /// mistaken for ours. Returns <see langword="null"/> when the device exposes no USB id, in which
    /// case recovery cannot wait for a return and settles instead.
    /// </summary>
    /// <summary>
    /// A filter that admits <b>only</b> <paramref name="device"/> itself, for re-acquiring it after a
    /// re-enumerating reset. <see langword="null"/> when it carries no such identity, in which case
    /// recovery must not adopt anything.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The identity is the <b>physical USB port AND the serial</b> — a conjunction, never a choice
    /// between them. Each covers the other's hole:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// A board does not move ports when it reboots, so the port is invariant across the reset, and it
    /// excludes a same-serial board sitting somewhere else on the bus. <b>But a port identifies a
    /// slot, not its occupant</b> — a different board plugged into that port during the window would
    /// satisfy it.
    /// </description></item>
    /// <item><description>
    /// The serial excludes exactly that replacement. <b>But a serial alone is not unique</b> — many
    /// USB families ship one hardcoded across every unit, and uniqueness cannot be established across
    /// a window in which the reference device is absent, which is why an earlier revision's
    /// pre-reset uniqueness check had to be abandoned rather than tuned.
    /// </description></item>
    /// </list>
    /// <para>
    /// Together they leave only one indistinguishable case: an identical board, carrying an identical
    /// serial, physically swapped onto the same port inside the window. No invariant available to
    /// this layer separates that from the original, and the same is true of the
    /// <see cref="DeviceCorrelationMode.ByLocationPath"/> correlation ADR-0063 already ships for the
    /// bootloader itself.
    /// </para>
    /// <para>
    /// <b>Both are required, or there is no identity.</b> A device exposing only one of them does not
    /// get the weaker half as a fallback — that was the shape of two earlier revisions and of two
    /// separate review findings, because each half alone is not a proof of sameness but a proof of
    /// something adjacent to it ("a board with this serial exists", "a compatible board is in this
    /// slot"). Recovery then simply does not refresh: it still resets, settles and retries against
    /// the snapshot it holds, which is safe because a stale id fails to open rather than resolving to
    /// a <em>different</em> board. Losing the refresh is an optimisation; adopting the wrong board
    /// flashes it.
    /// </para>
    /// <para>
    /// The cost is small and the trade is deliberate: on a platform reporting no port, recovery
    /// simply does not refresh the snapshot. It still resets, settles, and retries — which is what
    /// actually recovers the board — and holding the stale snapshot is safe, because a stale id fails
    /// to open rather than resolving to a <em>different</em> board. Losing an optimisation beats
    /// keeping a path that can flash the wrong hardware.
    /// </para>
    /// <para>
    /// VID/PID alone is <b>not</b> an identity — every board of a model shares it — and this filter
    /// decides which physical board the retry, and then the flash, is aimed at. #220 is the standing
    /// evidence for what treating a same-VID/PID appearance as identity costs.
    /// </para>
    /// </remarks>
    private static DeviceFilter? IdentityFilterFor(DeviceInfo device)
    {
        if (device.VendorId is not { } vid)
            return null;

        // IsNullOrWhiteSpace, as the ByLocationPath guard above uses: a whitespace-only path could
        // never match a real platform-supplied port.
        if (device.LocationPath is not { } location || string.IsNullOrWhiteSpace(location))
            return null;

        // BOTH, or nothing. A port with no serial is not a weaker identity to fall back on, it is
        // an unproven one: it says a compatible board is in that slot, not that it is OUR board.
        if (device.SerialNumber is not { } serial || string.IsNullOrWhiteSpace(serial))
            return null;

        return new DeviceFilter()
            .WithUsbId(vid, device.ProductId)
            .Where(d => string.Equals(d.LocationPath, location, StringComparison.OrdinalIgnoreCase))
            .WithSerialNumber(serial);
    }


    /// <summary>
    /// Marks an <see cref="IBootloaderEntry.EnterAsync"/> failure as the one thing recovery may act
    /// on. Private and never surfaced: the recovery path unwraps it, and the no-recovery path never
    /// creates it, so callers only ever see the original exception.
    /// </summary>
    private sealed class EntryAttemptFailedException(Exception inner)
        : Exception(inner.Message, inner);

    /// <summary>
    /// The terminal error when recovery ran and could not get the device into its bootloader. Says
    /// what was tried, so the operator can tell "this device is beyond software recovery" from
    /// "recovery was never configured" — the two produce very different next actions.
    /// </summary>
    private static BootloaderEntryException Exhausted(
        IBootloaderEntry entry, int attempts, int resets, Exception? lastFault, string reason)
        => new(
            $"Could not put '{entry.Name}' into its bootloader after {attempts} attempt(s) and "
            + $"{resets} reset(s): {reason}. The device may need a physical power-cycle.",
            lastFault ?? new BootloaderEntryException(reason));
}
