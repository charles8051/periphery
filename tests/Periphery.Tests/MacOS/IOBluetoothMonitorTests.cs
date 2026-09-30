using System.Collections.Immutable;
using Periphery.MacOS.Bluetooth;
using Periphery.MacOS.Bluetooth.Core;
using Periphery.Testing;
using Periphery.Tests.Linux;

namespace Periphery.Tests.MacOS;

/// <summary>
/// The bond watch shell (ADR-0093 D4) on a fake clock. Each poll is awaited through the monitor's
/// own <c>Polled</c> signal, after the test advances past a timer the monitor armed.
/// </summary>
public class IOBluetoothMonitorTests
{
    private static readonly IOBluetoothBond Mouse = new("c1-d2-e3-f4-a5-b6", "iClever Mouse 5.0", Connected: false, ClassOfDevice: 0);
    private const string MouseId = "iobluetooth:C1:D2:E3:F4:A5:B6";

    private sealed class Harness : IAsyncDisposable
    {
        private readonly Queue<ImmutableArray<DeviceInfo>?> _snapshots = new();
        private TaskCompletionSource _nextPoll = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TimerSignalingFakeTimeProvider Time { get; } = new();
        public List<(IOBluetoothEdgeKind Kind, string Id)> Edges { get; } = new();
        public IOBluetoothMonitor Monitor { get; }
        public Action<IOBluetoothEdge>? OnEdge { get; set; }

        public Harness(params ImmutableArray<DeviceInfo>?[] snapshots)
        {
            foreach (var s in snapshots)
                _snapshots.Enqueue(s);
            Monitor = new IOBluetoothMonitor(
                () => _snapshots.Count > 1 ? _snapshots.Dequeue() : _snapshots.Peek(),
                Time,
                new RecordingLogger(),
                edge =>
                {
                    lock (Edges)
                        Edges.Add((edge.Kind, edge.Device.Id.Value));
                    OnEdge?.Invoke(edge);
                });
            Monitor.Polled += () => Interlocked.Exchange(ref _nextPoll, new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
        }

        /// <summary>Waits for the monitor to arm its poll timer, fires it, and waits for that poll to finish.</summary>
        public async Task PollAsync()
        {
            var polled = Volatile.Read(ref _nextPoll).Task;
            Assert.Equal(IOBluetoothMonitor.PollInterval, await Time.NextTimerArmedAsync());
            Time.Advance(IOBluetoothMonitor.PollInterval);
            await polled;
        }

        public ValueTask DisposeAsync() => Monitor.DisposeAsync();
    }

    private static ImmutableArray<DeviceInfo> Bonds(params IOBluetoothBond[] bonds) => IOBluetoothInventory.Map(bonds);

    [Fact]
    public async Task Start_SeedsSilently_ThenAPollRaisesTheConnection()
    {
        await using var harness = new Harness(Bonds(Mouse), Bonds(Mouse with { Connected = true }));

        harness.Monitor.Start();
        Assert.Contains((DeviceId)MouseId, harness.Monitor.Held.Keys);
        Assert.Empty(harness.Edges);

        await harness.PollAsync();

        Assert.Equal(new[] { (IOBluetoothEdgeKind.Activated, MouseId), (IOBluetoothEdgeKind.PropertyChanged, MouseId) }, harness.Edges);
    }

    [Fact]
    public async Task APermissionGrantedLater_AnnouncesTheBonds()
    {
        await using var harness = new Harness(null, Bonds(Mouse));

        harness.Monitor.Start();
        Assert.Empty(harness.Monitor.Held);

        await harness.PollAsync();

        Assert.Equal(new[] { (IOBluetoothEdgeKind.Appeared, MouseId) }, harness.Edges);
    }

    [Fact]
    public async Task AnUnavailablePoll_KeepsTheBonds_AndRaisesNothing()
    {
        await using var harness = new Harness(Bonds(Mouse), null);

        harness.Monitor.Start();
        await harness.PollAsync();

        Assert.Empty(harness.Edges);
        Assert.Contains((DeviceId)MouseId, harness.Monitor.Held.Keys);
    }

    [Fact]
    public async Task AThrowingHandler_DoesNotStopThePolling()
    {
        await using var harness = new Harness(Bonds(), Bonds(Mouse), Bonds());
        harness.OnEdge = _ => throw new InvalidOperationException("handler");

        harness.Monitor.Start();
        await harness.PollAsync();
        await harness.PollAsync();

        Assert.Equal(new[] { (IOBluetoothEdgeKind.Appeared, MouseId), (IOBluetoothEdgeKind.Disappeared, MouseId) }, harness.Edges);
    }
}
