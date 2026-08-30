using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging.Abstractions;
using Periphery.Camera.Internal;
using Periphery.Camera.Testing;
using Periphery.Camera.Tests.Fakes;

namespace Periphery.Camera.Tests;

/// <summary>
/// Verifies that the canonical camera metrics emit through
/// <see cref="System.Diagnostics.Metrics.Meter"/> at the expected
/// increment sites — frames produced by the session, drops counted
/// by the pool, lease/return tracked across the round-trip.
/// </summary>
[Collection("Camera")]
public sealed class CameraDiagnosticsTests
{
    private const string MeterName = "Periphery.Camera";

    [Fact]
    public async Task FramesProduced_Counter_IncrementsOnEveryDeliveredFrame()
    {
        var produced = 0L;
        using var listener = StartListener<long>(
            "periphery.camera.frames_produced", v => Interlocked.Add(ref produced, v));

        var backend = new InMemoryCameraBackend();
        await using var session = await TestHelpers.CreateSessionWithBackend(backend);
        await session.StartCaptureAsync();

        const int target = 5;
        for (int i = 0; i < target; i++)
        {
            using var frame = await session.ReadFrameAsync();
        }

        Assert.Equal(target, produced);
    }

    [Fact]
    public async Task FramesDropped_Counter_IncrementsOnEveryEviction()
    {
        var dropped = 0L;
        using var listener = StartListener<long>(
            "periphery.camera.frames_dropped", v => Interlocked.Add(ref dropped, v));

        // A queue of one that the consumer never reads: each new frame evicts
        // the one before it, so frames 1 to 7 drop and frame 8 is left queued.
        // MaxFrames then parks the producer, so the count settles instead of
        // racing.
        var backend = new InMemoryCameraBackend { MaxFrames = 8 };
        await using var session = await TestHelpers.CreateSessionWithBackend(
            backend,
            options: new CameraSessionOptions(
                BufferCount: 1,
                ExhaustionPolicy: BufferExhaustionPolicy.LatestWins,
                QueueDepth: 1));

        await session.StartCaptureAsync();
        await backend.ReadHangReached.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(7, Interlocked.Read(ref dropped));

        using var held = await session.ReadFrameAsync();
        await session.StopCaptureAsync();
    }

    [Fact]
    public async Task ProducerStalls_Counter_FiresWhenTheProducerParks()
    {
        var stalls = 0L;
        using var listener = StartListener<long>(
            "periphery.camera.producer_stalls", v => Interlocked.Add(ref stalls, v));

        var durations = new List<double>();
        using var durationListener = StartListener<double>(
            "periphery.camera.producer_stall_ms",
            v => { lock (durations) durations.Add(v); });

        // Frame 1 fills the queue of one; frame 2 finds it full and, under
        // StallProducer, parks there instead of evicting.
        var backend = new InMemoryCameraBackend { MaxFrames = 2 };
        await using var session = await TestHelpers.CreateSessionWithBackend(
            backend,
            options: new CameraSessionOptions(
                BufferCount: 3,
                ExhaustionPolicy: BufferExhaustionPolicy.StallProducer,
                QueueDepth: 1));

        await session.StartCaptureAsync();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (Interlocked.Read(ref stalls) == 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the producer to park.");
            await Task.Delay(10);
        }

        // Reading frame 1 frees the slot, which ends the stall and records how
        // long it held the producer off the driver.
        using var frame = await session.ReadFrameAsync();
        while (true)
        {
            lock (durations)
            {
                if (durations.Count > 0) break;
            }
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the stall duration.");
            await Task.Delay(10);
        }

        lock (durations)
            Assert.All(durations, ms => Assert.True(ms > 0, $"Stall duration was {ms} ms."));

        await session.StopCaptureAsync();
    }

    [Fact]
    public async Task OutstandingLeases_UpDownCounter_FiresOnLeaseAndReturn()
    {
        var positives = 0;
        var negatives = 0;
        using var listener = StartListener<int>(
            "periphery.camera.outstanding_leases",
            v =>
            {
                if (v > 0) Interlocked.Increment(ref positives);
                else if (v < 0) Interlocked.Increment(ref negatives);
            });

        var backend = new InMemoryCameraBackend();
        await using var session = await TestHelpers.CreateSessionWithBackend(backend);
        await session.StartCaptureAsync();

        // Lease and dispose a frame — at least one +1 and one -1 must fire.
        // (The producer races ahead with BufferCount=3, so net outstanding is
        // not deterministic; we verify the *signal* not the exact count.)
        var frame = await session.ReadFrameAsync();
        frame.Dispose();

        await session.StopCaptureAsync();

        Assert.True(positives > 0, $"Expected at least one lease (+1) event; got {positives}.");
        Assert.True(negatives > 0, $"Expected at least one return (-1) event; got {negatives}.");
    }

    [Fact]
    public void Meter_HasExpectedNameAndVersion()
    {
        Assert.Equal("Periphery.Camera", CameraDiagnostics.Meter.Name);
        Assert.False(string.IsNullOrEmpty(CameraDiagnostics.Meter.Version));
    }

    /// <summary>
    /// Subscribes to one named instrument on the Periphery.Camera meter and
    /// invokes <paramref name="onMeasurement"/> for every recorded value.
    /// Disposing the returned listener stops the subscription.
    /// </summary>
    private static MeterListener StartListener<T>(string instrumentName, Action<T> onMeasurement)
        where T : struct
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == MeterName && instrument.Name == instrumentName)
                    l.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<T>((_, value, _, _) => onMeasurement(value));
        listener.Start();
        return listener;
    }
}
