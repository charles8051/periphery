// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Periphery.Treehopper.Wire;

namespace Periphery.Treehopper;

/// <summary>
/// A single Treehopper I/O pin. Obtain via <see cref="TreehopperBoard.Pins"/>
/// and configure it with <see cref="ConfigureAsync"/>, which returns a
/// <see cref="PinHandle"/> lease.
/// </summary>
/// <remarks>
/// In the pure-core model (ADR-0052 DEC-003), configuring a pin is a change to
/// the board's desired config, applied via <see cref="TreehopperBoard.ReconcileWithAsync"/>.
/// Pin reads are done by consuming <see cref="TreehopperBoard.Reports"/> — there
/// are no one-shot read methods here.
/// </remarks>
public sealed class Pin
{
    internal Pin(TreehopperBoard board, int number)
    {
        Board = board;
        Number = number;
    }

    /// <summary>The board this pin belongs to.</summary>
    internal TreehopperBoard Board { get; }

    /// <summary>This pin's number (0–19).</summary>
    public int Number { get; }

    /// <summary>
    /// Configures the pin's electrical mode and returns a lease. Disposing the
    /// lease releases the pin back to a high-impedance digital input.
    /// </summary>
    public Task<PinHandle> ConfigureAsync(PinMode mode, CancellationToken ct = default)
        => ConfigureAsync(mode, AdcReferenceLevel.Vref_3V3, ct);

    /// <summary>
    /// Configures the pin's electrical mode with an explicit ADC reference (applied only
    /// for <see cref="PinMode.AnalogInput"/>) and returns a lease. Disposing the lease
    /// releases the pin back to a high-impedance digital input.
    /// </summary>
    public async Task<PinHandle> ConfigureAsync(
        PinMode mode, AdcReferenceLevel reference, CancellationToken ct = default)
    {
        if (mode == PinMode.Reserved)
            throw new ArgumentOutOfRangeException(nameof(mode), "Cannot configure a pin as Reserved.");

        await Board.ReconcileWithAsync(
            cfg => cfg with
            {
                Pins = cfg.Pins.SetItem((byte)Number, new PinConfig(mode, Reference: reference))
            }, ct).ConfigureAwait(false);

        return new PinHandle(this, reference);
    }

    /// <summary>
    /// Configures this pin as a soft-PWM output (~60.94 Hz, available on any pin) and
    /// returns a lease. Disposing the lease releases the pin back to a high-impedance
    /// digital input.
    /// </summary>
    /// <param name="dutyCycle">Initial duty cycle (0.0–1.0).</param>
    /// <param name="ct">A token to cancel the operation.</param>
    public async Task<SoftPwmHandle> ConfigureSoftPwmAsync(double dutyCycle = 0, CancellationToken ct = default)
    {
        if (dutyCycle is < 0.0 or > 1.0)
            throw new ArgumentOutOfRangeException(
                nameof(dutyCycle), dutyCycle, "Duty cycle must be in the range [0.0, 1.0].");

        await Board.SetSoftPwmAsync(
            (byte)Number, TreehopperWire.SoftPwmTicksFromDuty(dutyCycle), ct).ConfigureAwait(false);

        return new SoftPwmHandle(this);
    }
}

/// <summary>
/// A lease on a configured <see cref="Pin"/>. Disposing the lease releases the
/// pin back to a high-impedance digital input.
/// </summary>
public sealed class PinHandle : IAsyncDisposable
{
    private readonly Pin _pin;
    private readonly AdcReferenceLevel _reference;
    private bool _disposed;

    internal PinHandle(Pin pin, AdcReferenceLevel reference = AdcReferenceLevel.Vref_3V3)
    {
        _pin = pin;
        _reference = reference;
    }

    /// <summary>The pin this lease controls.</summary>
    public Pin Pin => _pin;

    /// <summary>
    /// Drives a push-pull output pin high or low. Updates the desired config
    /// and reconciles. Throws if the pin is not in push-pull output mode.
    /// </summary>
    public Task WriteAsync(bool high, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _pin.Board.ReconcileWithAsync(
            cfg => cfg with
            {
                Pins = cfg.Pins.SetItem(
                    (byte)_pin.Number,
                    new PinConfig(PinMode.PushPullOutput, high))
            }, ct);
    }

    // ── Reads (projections over the report stream — ADR-0052 DEC-002) ───

    /// <summary>
    /// Reads this pin's current <see cref="PinSnapshot"/> — the board's latest
    /// known state for the pin, waiting for the first report if none has arrived
    /// yet. Because the firmware emits reports only on change, this returns the
    /// last-seen value rather than forcing a fresh sample.
    /// </summary>
    public async Task<PinSnapshot> ReadAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var report = await _pin.Board.ReadReportAsync(ct).ConfigureAwait(false);
        return report.Pins[_pin.Number];
    }

    /// <summary>Reads this pin's current digital level.</summary>
    public async Task<bool> ReadDigitalAsync(CancellationToken ct = default)
        => (await ReadAsync(ct).ConfigureAwait(false)).Digital;

    /// <summary>Reads this pin's current raw 12-bit ADC sample (0–4092).</summary>
    public async Task<int> ReadAnalogAsync(CancellationToken ct = default)
        => (await ReadAsync(ct).ConfigureAwait(false)).Adc;

    /// <summary>
    /// Reads this pin's current analog voltage, scaled by the ADC reference the pin was
    /// configured with via
    /// <see cref="Pin.ConfigureAsync(PinMode, AdcReferenceLevel, CancellationToken)"/>.
    /// </summary>
    public async Task<double> ReadVoltageAsync(CancellationToken ct = default)
        => (await ReadAsync(ct).ConfigureAwait(false)).AnalogVoltage(_reference.ReferenceVoltage());

    /// <summary>Reads this pin's current analog voltage against an explicit reference voltage.</summary>
    public async Task<double> ReadVoltageAsync(double referenceVoltage, CancellationToken ct = default)
        => (await ReadAsync(ct).ConfigureAwait(false)).AnalogVoltage(referenceVoltage);

    /// <summary>
    /// Streams this pin's <see cref="PinSnapshot"/> as it changes — a per-pin
    /// projection of <see cref="TreehopperBoard.Reports"/>, filtered to this pin
    /// and de-duplicated (only distinct consecutive values are yielded). The first
    /// element is the current value (replayed on subscribe), then each change.
    /// </summary>
    public async IAsyncEnumerable<PinSnapshot> WatchAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        PinSnapshot? last = null;
        await foreach (var report in _pin.Board.Reports.WithCancellation(ct).ConfigureAwait(false))
        {
            var snap = report.Pins[_pin.Number];
            if (last is null || !snap.Equals(last.Value))
            {
                last = snap;
                yield return snap;
            }
        }
    }

    /// <summary>Releases the pin back to a high-impedance digital input.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            await _pin.Board.ReconcileWithAsync(
                cfg => cfg with { Pins = cfg.Pins.Remove((byte)_pin.Number) },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch { /* best-effort teardown */ }
    }
}
