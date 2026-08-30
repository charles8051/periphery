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
    private readonly object _gate = new();

    public MeterPeak(params string[] instruments)
    {
        _listener.InstrumentPublished = (inst, l) =>
        {
            if (inst.Meter.Name == "Periphery.Usb" && instruments.Contains(inst.Name))
                l.EnableMeasurementEvents(inst);
        };
        _listener.SetMeasurementEventCallback<int>((inst, value, _, _) =>
        {
            lock (_gate)
            {
                int now = _current.GetValueOrDefault(inst.Name) + value;
                _current[inst.Name] = now;
                _peak[inst.Name] = Math.Max(_peak.GetValueOrDefault(inst.Name), now);
            }
        });
        _listener.Start();
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
    public async Task WaitForAsync(string instrument, int value, TimeSpan timeout)
    {
        long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            if (Current(instrument) == value)
                return;
            await Task.Delay(10).ConfigureAwait(false);
        }

        Assert.Fail(
            $"{instrument} never reached {value} within {timeout.TotalMilliseconds:F0} ms " +
            $"(last read {Current(instrument)}).");
    }

    public void Dispose() => _listener.Dispose();
}
