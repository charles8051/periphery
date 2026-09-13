using System.Diagnostics.Metrics;
using Microsoft.Extensions.Time.Testing;

namespace Periphery.Camera.Tests.Fakes;

/// <summary>
/// A <see cref="FakeTimeProvider"/> that reports each timestamp read. It moves only when a test
/// advances it; the report tells the test that the code under test has taken its reading.
/// </summary>
internal sealed class TimestampReportingFakeTimeProvider : FakeTimeProvider
{
    /// <summary>Raised on the reading thread, after the reading is taken.</summary>
    public event Action? TimestampRead;

    public override long GetTimestamp()
    {
        long now = base.GetTimestamp();
        TimestampRead?.Invoke();
        return now;
    }

    /// <summary>
    /// Completes once a producer on this clock has entered a stall and read the clock for its
    /// start. Entering a stall counts it on <c>periphery.camera.producer_stalls</c> and then reads
    /// the clock, and nothing else in the session reads it in between, so advancing the clock after
    /// this completes lengthens the stall by exactly that much.
    /// </summary>
    /// <remarks>
    /// The stall instrument moves only under <see cref="BufferExhaustionPolicy.StallProducer"/>,
    /// which only tests in the serial <c>Camera</c> collection use, so no other test's session can
    /// set it off.
    /// </remarks>
    public MeterListener ProducerParked(out Task parkedTask)
    {
        int stalled = 0;
        var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == "Periphery.Camera"
                    && instrument.Name == "periphery.camera.producer_stalls")
                    l.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<long>((_, _, _, _) => Volatile.Write(ref stalled, 1));
        listener.Start();
        TimestampRead += () =>
        {
            if (Volatile.Read(ref stalled) == 1)
                parked.TrySetResult();
        };
        parkedTask = parked.Task;
        return listener;
    }
}
