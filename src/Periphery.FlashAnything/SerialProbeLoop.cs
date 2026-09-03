// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Threading;
using System.Threading.Tasks;
using Periphery.Bootloader;

namespace Periphery.FlashAnything;

/// <summary>
/// Probes one bound bridge on a cadence and reports what it finds, folding each cycle through the
/// pure <see cref="ProbeRowPolicy"/>.
/// </summary>
/// <remarks>
/// <para>
/// This exists because hotplug cannot see the arrival that matters (autoflash adr.md Decision 9).
/// A board dropped onto pogo pins changes nothing the OS reports: the bridge is on the fixture and
/// stays enumerated, so the only way to learn a part is there is to ask it.
/// </para>
/// <para>
/// It is not a second discovery path. The watcher still finds the bridge; this resolves what is
/// behind one the operator already bound, which is why it takes a resolver rather than enumerating
/// anything itself.
/// </para>
/// </remarks>
internal sealed class SerialProbeLoop
{
    private readonly BridgeIdentity _bridge;
    private readonly Func<BridgeIdentity, DeviceInfo?> _resolve;
    private readonly IBootloaderProvider _provider;
    private readonly Action<ProbeRowAction> _report;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeSpan _cadence;
    private readonly TimeSpan _stalledCadence;

    /// <summary>Creates a loop for one bound bridge.</summary>
    /// <param name="bridge">The bridge the operator bound.</param>
    /// <param name="resolve">Finds the bridge's current device, or null when it is no longer present.</param>
    /// <param name="provider">The probe: opening a programmer <i>is</i> the AN3155 handshake.</param>
    /// <param name="report">Receives every action the policy produces.</param>
    /// <param name="delay">Waits between cycles. Injected so tests can drive the cadence without sleeping, and assert what was asked for.</param>
    /// <param name="cadence">Interval between probes while the row is live.</param>
    /// <param name="stalledCadence">Interval after the row has been silent long enough to stall.</param>
    public SerialProbeLoop(
        BridgeIdentity bridge,
        Func<BridgeIdentity, DeviceInfo?> resolve,
        IBootloaderProvider provider,
        Action<ProbeRowAction> report,
        Func<TimeSpan, CancellationToken, Task> delay,
        TimeSpan cadence,
        TimeSpan stalledCadence)
    {
        ArgumentNullException.ThrowIfNull(resolve);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(delay);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(cadence, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(stalledCadence, cadence);

        _bridge = bridge;
        _resolve = resolve;
        _provider = provider;
        _report = report;
        _delay = delay;
        _cadence = cadence;
        _stalledCadence = stalledCadence;
    }

    /// <summary>The row's state after the last cycle. For tests and for rendering.</summary>
    public ProbeRowState State { get; private set; } = ProbeRowState.Initial;

    /// <summary>
    /// Probes until cancelled, or until the bridge faults. Cancellation is the operator disarming,
    /// and it is the stop: an armed fixture sitting empty is the normal resting state of this
    /// feature, so the loop slows down rather than giving up.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var outcome = await ProbeOnceAsync(ct).ConfigureAwait(false);

            var (next, action) = ProbeRowPolicy.Advance(State, outcome);
            State = next;

            if (action is not ProbeRowAction.None)
                _report(action);

            // A faulted bridge is not something probing harder can fix — the fixture is unplugged
            // or the port is unusable, and adr.md Decision 8 breaks the bind on disconnect rather
            // than resuming if something matching comes back.
            if (action is ProbeRowAction.Faulted)
                return;

            try
            {
                await _delay(next.Stalled ? _stalledCadence : _cadence, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task<ProbeOutcome> ProbeOnceAsync(CancellationToken ct)
    {
        // The bridge itself going away is a fault, not silence: there is no port left to be quiet
        // on. Everything else — a fixture with no board, a seated part that will not answer — comes
        // back through the provider as a failure to open, and is silence.
        if (_resolve(_bridge) is not { } device)
            return new ProbeOutcome.TransportFailed($"the bound bridge {_bridge} is no longer present.");

        IFirmwareProgrammer? programmer = null;
        try
        {
            // Opening is the probe. For AN3155 the handshake happens inside OpenAsync, so a
            // programmer coming back at all means a bootloader answered.
            programmer = await _provider.OpenAsync(device, ct).ConfigureAwait(false);
            var identity = await programmer.IdentifyAsync(ct).ConfigureAwait(false);
            return new ProbeOutcome.Occupied(identity);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (BootloaderException)
        {
            // The documented way a provider reports that the device did not cooperate: nothing
            // answered the handshake, the port would not open, the part is not in its bootloader.
            // All of those are silence, and the row decides whether a run of them means anything.
            return ProbeOutcome.NoResponse.Instance;
        }
        catch (Exception ex)
        {
            // Anything outside the provider contract is a defect, not a quiet fixture. Folding it
            // into NoResponse would back the cadence off and probe forever while never reporting
            // that something is wrong — so it faults the row and the operator hears about it.
            return new ProbeOutcome.TransportFailed(
                $"probing {device.PortName?.Value ?? device.Id.Value} threw {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            // Open per cycle (adr.md Decision 11). The port goes free between probes; the handle is
            // held only across the probe-to-flash hand-off, which arrives with the service wiring.
            await SafeDisposeAsync(programmer).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Closes the probe's programmer without letting a teardown failure escape.
    /// </summary>
    /// <remarks>
    /// A throw from here runs after the probe result is computed but before it is returned, so it
    /// would bypass the policy entirely and propagate out of <see cref="RunAsync"/> — killing an
    /// armed row over a failed close, and losing the detection or removal that cycle had already
    /// established. Swallowing is safe rather than lazy: if the port really is broken, the next
    /// cycle fails to open it and the row observes that through the normal path.
    /// </remarks>
    private static async ValueTask SafeDisposeAsync(IFirmwareProgrammer? programmer)
    {
        if (programmer is null)
            return;

        try
        {
            await programmer.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // Deliberately ignored; see above.
        }
    }
}
