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
        var watcher = FakeWatcher();

        var tasks = new List<Task>();

        for (int i = 0; i < 5; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                try { await watcher.StartAsync(); }
                catch (InvalidOperationException) { }
            }));
        }

        tasks.Add(Task.Run(async () =>
        {
            await Task.Delay(5);
            await watcher.DisposeAsync();
        }));

        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));
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
            await Task.Delay(20);
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
