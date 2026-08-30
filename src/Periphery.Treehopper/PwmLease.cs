// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Threading;
using System.Threading.Tasks;
using Periphery.Treehopper.Wire;

namespace Periphery.Treehopper;

/// <summary>
/// The active hardware-PWM peripheral on a Treehopper board (up to three channels,
/// shared frequency). Obtain via <see cref="TreehopperBoard.UsePwmAsync"/>;
/// disposing it disables all channels.
/// </summary>
/// <remarks>
/// Each change reconciles the full PWM state via
/// <see cref="TreehopperBoard.ReconcileWithAsync"/>. Channels enable cumulatively
/// (firmware constraint): enabling Pwm3 also activates Pwm1 and Pwm2 at their
/// current duty cycles (default 0%).
/// </remarks>
public sealed class PwmLease : IAsyncDisposable
{
    private readonly TreehopperBoard _board;
    private readonly double[] _duty = new double[3]; // [0]=pin7, [1]=pin8, [2]=pin9
    private PwmFrequency _frequency;
    private byte _enableMode;
    private bool _disposed;

    internal PwmLease(TreehopperBoard board, PwmFrequency frequency)
    {
        _board = board;
        _frequency = frequency;
    }

    /// <summary>
    /// Sends the initial PWM config (frequency set; no channels enabled yet).
    /// Called by <see cref="TreehopperBoard.UsePwmAsync"/> immediately after
    /// constructing the lease.
    /// </summary>
    internal Task InitializeAsync(CancellationToken ct) => PushAsync(ct);

    /// <summary>
    /// Sets a channel's duty cycle (0.0–1.0), automatically enabling it (and any
    /// lower-numbered channels) if not already enabled.
    /// </summary>
    public async Task SetDutyCycleAsync(
        PwmChannel channel, double dutyCycle, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (dutyCycle is < 0.0 or > 1.0)
            throw new ArgumentOutOfRangeException(
                nameof(dutyCycle), dutyCycle, "Duty cycle must be in the range [0.0, 1.0].");

        int index = (int)channel;
        _duty[index] = dutyCycle;
        if (index + 1 > _enableMode)
            _enableMode = (byte)(index + 1); // cumulative enable ladder

        await PushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Changes the base frequency shared by all channels.</summary>
    public async Task SetFrequencyAsync(PwmFrequency frequency, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _frequency = frequency;
        await PushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The PWM period in microseconds at the current frequency. A pulse width set via
    /// <see cref="SetPulseWidthAsync"/> ranges from 0 to this value.
    /// </summary>
    public double PeriodMicroseconds => 1_000_000.0 / FrequencyHz(_frequency);

    /// <summary>
    /// Sets a channel's output as a pulse width in microseconds
    /// (0 … <see cref="PeriodMicroseconds"/>), automatically enabling it (and any
    /// lower-numbered channels). A convenience over <see cref="SetDutyCycleAsync"/> —
    /// the firmware is always driven by duty cycle.
    /// </summary>
    public Task SetPulseWidthAsync(PwmChannel channel, double microseconds, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        double period = PeriodMicroseconds;
        if (microseconds < 0 || microseconds > period)
            throw new ArgumentOutOfRangeException(
                nameof(microseconds), microseconds,
                $"Pulse width must be in the range [0, {period:0.##}] µs at the current frequency.");
        return SetDutyCycleAsync(channel, microseconds / period, ct);
    }

    private static int FrequencyHz(PwmFrequency frequency) => frequency switch
    {
        PwmFrequency.Freq61Hz  => 61,
        PwmFrequency.Freq183Hz => 183,
        PwmFrequency.Freq732Hz => 732,
        _                      => 732,
    };

    private Task PushAsync(CancellationToken ct)
    {
        var pwmCfg = new PwmConfig(_frequency, _enableMode, _duty[0], _duty[1], _duty[2]);
        return _board.ReconcileWithAsync(cfg => cfg with { Pwm = pwmCfg }, ct);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            await _board.ReconcileWithAsync(cfg => cfg with { Pwm = null }, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch { /* best-effort teardown */ }
    }
}
