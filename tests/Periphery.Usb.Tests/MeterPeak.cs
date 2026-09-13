using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Periphery.Usb.Tests;

/// <summary>
/// Tracks the peak (and current) value of a <c>Periphery.Usb</c> UpDownCounter while a
/// probe runs, and lets a test wait for a counter to reach a value rather than sleeping.
/// </summary>
/// <remarks>
/// Shared across the assembly rather than nested in one test class: the same barrier
/// problem shows up wherever a test needs to know a transfer has actually reached the
/// backend. Safe to read because <c>AssemblyInfo.cs</c> serialises this assembly — the
/// Periphery.Usb Meter is process-wide, so parallel classes would contaminate each other's
/// measurements.
/// </remarks>
internal sealed class MeterPeak : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly Dictionary<string, int> _current = new();
    private readonly Dictionary<string, int> _peak = new();
    private readonly List<(string Instrument, int Value, TaskCompletionSource Reached)> _waiters = new();
    private readonly object _gate = new();

    public MeterPeak(params string[] instruments)
    {
        _listener.InstrumentPublished = (inst, l) =>
        {
            if (inst.Meter.Name == "Periphery.Usb" && instruments.Contains(inst.Name))
                l.EnableMeasurementEvents(inst);
        };
        _listener.SetMeasurementEventCallback<int>((inst, value, _, _) => Record(inst.Name, value));
        // The monotonic counters (transfers_total, teardown_not_quiesced_total) are Counter<long>.
        _listener.SetMeasurementEventCallback<long>((inst, value, _, _) => Record(inst.Name, checked((int)value)));
        _listener.Start();
    }

    private void Record(string instrument, int delta)
    {
        lock (_gate)
        {
            int now = _current.GetValueOrDefault(instrument) + delta;
            _current[instrument] = now;
            _peak[instrument] = Math.Max(_peak.GetValueOrDefault(instrument), now);
            _waiters.RemoveAll(w => w.Instrument == instrument && w.Value == now && w.Reached.TrySetResult());
        }
    }

    public int Peak(string instrument)
    {
        lock (_gate) return _peak.GetValueOrDefault(instrument);
    }

    public int Current(string instrument)
    {
        lock (_gate) return _current.GetValueOrDefault(instrument);
    }

    /// <summary>
    /// Waits until <paramref name="instrument"/> reads exactly <paramref name="value"/>.
    /// The production counters move as each caller enters the gate, so a test that
    /// sleeps and hopes is sampling a race; this waits for the steady state it means
    /// to assert on, and fails loudly if it never arrives.
    /// </summary>
    /// <remarks>
    /// Released by the measurement that brings the counter to <paramref name="value"/>, so it
    /// matches the first time the value is reached. Callers wait at a point after which the
    /// counter only moves toward it. <paramref name="timeout"/> only bounds a failure.
    /// </remarks>
    public async Task WaitForAsync(string instrument, int value, TimeSpan timeout)
    {
        Task reached;
        lock (_gate)
        {
            if (_current.GetValueOrDefault(instrument) == value)
                return;

            var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((instrument, value, waiter));
            reached = waiter.Task;
        }

        try
        {
            await reached.WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Assert.Fail(
                $"{instrument} never reached {value} within {timeout.TotalMilliseconds:F0} ms " +
                $"(last read {Current(instrument)}).");
        }
    }

    public void Dispose() => _listener.Dispose();
}
