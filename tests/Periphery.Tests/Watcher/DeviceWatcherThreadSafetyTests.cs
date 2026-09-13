namespace Periphery.Tests;

/// <summary>
/// Thread-safety tests for <see cref="DeviceWatcher"/> lifecycle operations.
/// All tests use fake providers — no OS APIs required.
/// </summary>
public class DeviceWatcherThreadSafetyTests
{
    private static DeviceWatcher FakeWatcher()
        => new(FakeDeviceProvider.Empty(), new FakeDeviceMonitorProvider());

    // ── Concurrent StartAsync ──────────────────────────────────────────

    [Fact]
    public async Task ConcurrentStartAsync_OnlyOneSucceeds()
    {
        await using var watcher = FakeWatcher();

        var tasks = Enumerable.Range(0, 10)
            .Select(_ => Task.Run(async () =>
            {
                try
                {
                    await watcher.StartAsync();
                    return true;
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            }))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        Assert.Equal(1,  results.Count(r => r));
        Assert.Equal(9, results.Count(r => !r));
    }

    [Fact]
    public async Task ConcurrentStartAndDispose_NoDeadlock()
    {
        // One start holds the lifecycle lock inside the provider while four more starts and a
        // dispose queue behind it, in that order. The lock serves waiters in the order they
        // queued, so the losing starts run before the dispose. A start queued behind the dispose
        // is #262.
        var monitor = new FakeDeviceMonitorProvider();
        var holdStart = new TaskCompletionSource();
        monitor.HoldStartUntil = holdStart.Task;
        var watcher = new DeviceWatcher(FakeDeviceProvider.Empty(), monitor);

        var winner = watcher.StartAsync();
        await monitor.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var losers = Enumerable.Range(0, 4).Select(_ => watcher.StartAsync()).ToArray();
        var dispose = watcher.DisposeAsync().AsTask();
        holdStart.SetResult();

        await winner.WaitAsync(TimeSpan.FromSeconds(10));
        foreach (var loser in losers)
            await Assert.ThrowsAsync<InvalidOperationException>(() => loser.WaitAsync(TimeSpan.FromSeconds(10)));
        await dispose.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, monitor.StartAttempts);
        Assert.Equal(1, monitor.DisposeCount);
    }

    [Fact]
    public async Task StressTest_ManyWatchers_ConcurrentOperations()
    {
        var watchers = Enumerable.Range(0, 3)
            .Select(_ => FakeWatcher())
            .ToArray();

        try
        {
            await Task.WhenAll(watchers.Select(w => w.StartAsync()));
        }
        finally
        {
            await Task.WhenAll(watchers.Select(w => w.DisposeAsync().AsTask()));
        }
    }

    [Fact]
    public async Task RapidCreateStartDisposeCycles_NoRaceConditions()
    {
        for (int i = 0; i < 20; i++)
        {
            await using var watcher = FakeWatcher();
            await watcher.StartAsync();
        }
    }
}
