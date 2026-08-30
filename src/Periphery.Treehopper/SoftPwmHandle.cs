// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Threading;
using System.Threading.Tasks;
using Periphery.Treehopper.Wire;

namespace Periphery.Treehopper;

/// <summary>
/// A lease on a pin driven as a soft-PWM output. Obtain via
/// <see cref="Pin.ConfigureSoftPwmAsync"/>; disposing it releases the pin back to a
/// high-impedance input.
/// </summary>
/// <remarks>
/// Soft-PWM works on any pin at a fixed ~60.94 Hz with 16-bit resolution — well suited
/// to hobby servos and other low-frequency, jitter-tolerant loads. The board drives
/// every active soft-PWM pin from one timer, so each duty/pulse-width change re-sends
/// the whole soft-PWM set (handled transparently by the reconcile planner).
/// </remarks>
public sealed class SoftPwmHandle : IAsyncDisposable
{
    private readonly Pin _pin;
    private bool _disposed;

    internal SoftPwmHandle(Pin pin) => _pin = pin;

    /// <summary>The pin this lease controls.</summary>
    public Pin Pin => _pin;

    /// <summary>Sets the duty cycle (0.0–1.0).</summary>
    public Task SetDutyCycleAsync(double dutyCycle, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (dutyCycle is < 0.0 or > 1.0)
            throw new ArgumentOutOfRangeException(
                nameof(dutyCycle), dutyCycle, "Duty cycle must be in the range [0.0, 1.0].");
        return _pin.Board.SetSoftPwmAsync(
            (byte)_pin.Number, TreehopperWire.SoftPwmTicksFromDuty(dutyCycle), ct);
    }

    /// <summary>
    /// Sets the pulse width in microseconds (0 … ≈16383.75 µs, in 0.25 µs steps) — the
    /// natural API for driving hobby servos.
    /// </summary>
    public Task SetPulseWidthAsync(double microseconds, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (microseconds < 0)
            throw new ArgumentOutOfRangeException(
                nameof(microseconds), microseconds, "Pulse width must be non-negative.");
        return _pin.Board.SetSoftPwmAsync(
            (byte)_pin.Number, TreehopperWire.SoftPwmTicksFromPulseWidth(microseconds), ct);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            await _pin.Board.ClearSoftPwmAsync((byte)_pin.Number, CancellationToken.None).ConfigureAwait(false);
        }
        catch { /* best-effort teardown */ }
    }
}
