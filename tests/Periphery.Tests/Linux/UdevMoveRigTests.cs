using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;

namespace Periphery.Tests.Linux;

/// <summary>
/// A real udev <c>move</c> (#304). Renaming a network interface moves its device, and udev reports
/// <c>move</c> with the old path in <c>DEVPATH_OLD</c>. Renaming needs <c>CAP_NET_ADMIN</c>, so
/// these run only with <c>PERIPHERY_LINUX_DEVICE_TESTS=1</c> and <c>PERIPHERY_LINUX_SUDO_TESTS=1</c>,
/// as a user with passwordless <c>sudo</c>. When enabled, a failed <c>sudo</c> is a failure, never a
/// skip (ADR-0089). The test adds and renames an interface, so it runs alone: a Network
/// enumeration running beside it sees the interface come and go.
/// </summary>
[Collection(NetworkMutationCollection.Name)]
public class UdevMoveRigTests
{
    private static bool Enabled =>
        OperatingSystem.IsLinux()
        && Environment.GetEnvironmentVariable("PERIPHERY_LINUX_DEVICE_TESTS") == "1"
        && Environment.GetEnvironmentVariable("PERIPHERY_LINUX_SUDO_TESTS") == "1";

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RenamedInterface_DisappearsUnderItsOldId_ThenAppearsUnderItsNewOne()
    {
        if (!Enabled) return;

        // The test deletes only the interface it created. `ip link add` and the rename both fail on a
        // name already in use, so neither can take over someone else's interface.
        string tag = Convert.ToHexString(RandomNumberGenerator.GetBytes(3)).ToLowerInvariant();
        string before = $"pmv{tag}a", after = $"pmv{tag}b";
        string oldId = $"/sys/devices/virtual/net/{before}", newId = $"/sys/devices/virtual/net/{after}";

        var (added, addOutput) = await SudoAsync("ip", "link", "add", before, "type", "dummy");
        Assert.True(added == 0, $"Could not create {before}: {addOutput}");
        string owned = before;

        try
        {
            // Let udev finish the add, so its edge cannot land after the watcher's enumeration.
            await RunAsync("udevadm", "settle");

            var edges = new ConcurrentQueue<string>();
            var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            await using var watcher = Devices.Watch().OfCategory(DeviceCategory.Network);
            watcher.Disappeared += (_, e) => edges.Enqueue($"Disappeared {e.Device.Id.Value}");
            watcher.Appeared += (_, e) =>
            {
                edges.Enqueue($"Appeared {e.Device.Id.Value}");
                if (e.Device.Id.Value == newId)
                    arrived.TrySetResult();
            };
            await watcher.StartAsync();

            var (renamed, renameOutput) = await SudoAsync("ip", "link", "set", before, "name", after);
            Assert.True(renamed == 0, $"Could not rename {before}: {renameOutput}");
            owned = after;

            // A safety net that bounds a failure; the Appeared edge is the signal (ADR-0089 D5).
            using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                await arrived.Task.WaitAsync(safety.Token);
            }
            catch (OperationCanceledException)
            {
                Assert.Fail($"No Appeared for {newId}. Edges: {string.Join("; ", edges)}");
            }

            // The first edge is the watcher's start-up snapshot, which shows it held the old id.
            var ours = edges.Where(e => e.EndsWith(oldId, StringComparison.Ordinal) || e.EndsWith(newId, StringComparison.Ordinal));
            Assert.Equal(new[] { $"Appeared {oldId}", $"Disappeared {oldId}", $"Appeared {newId}" }, ours);

            // Positive control: the kernel holds the interface under its new name only.
            var (shown, _) = await RunAsync("ip", "link", "show", after);
            Assert.Equal(0, shown);
            Assert.False(Directory.Exists(oldId));
        }
        finally
        {
            await SudoAsync("ip", "link", "del", owned);
        }
    }

    private static Task<(int ExitCode, string Output)> SudoAsync(params string[] args) => RunAsync("sudo", ["-n", .. args]);

    private static async Task<(int ExitCode, string Output)> RunAsync(string program, params string[] args)
    {
        var start = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args)
            start.ArgumentList.Add(arg);

        using var process = Process.Start(start)!;
        // A safety net, not an assertion (ADR-0089 D5).
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var stdout = process.StandardOutput.ReadToEndAsync(cts.Token);
        var stderr = process.StandardError.ReadToEndAsync(cts.Token);
        await process.WaitForExitAsync(cts.Token);
        return (process.ExitCode, await stdout + await stderr);
    }
}

/// <summary>Tests that add, rename or remove network interfaces. They run with nothing beside them.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class NetworkMutationCollection
{
    public const string Name = "Linux network mutation";
}
