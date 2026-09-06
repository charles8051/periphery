// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.CompilerServices;
using Periphery;

namespace Periphery.Treehopper.Control.Tests;

/// <summary>
/// Pins which lifecycle edge each half of the hotplug handling binds to (ADR-0088):
/// presence carries the inventory, activity carries the board handle.
///
/// <list type="table">
/// <item><term><c>Appeared</c></term><description>BoardDiscovered — inventory in</description></item>
/// <item><term><c>Activated</c></term><description>version read, session reconcile — I/O in</description></item>
/// <item><term><c>Deactivated</c></term><description>close the session — I/O out</description></item>
/// <item><term><c>Disappeared</c></term><description>BoardRemoved — inventory out</description></item>
/// </list>
///
/// <para>The version read is the observable proxy for "did this handler open the board".
/// On the real path the open is swallowed on failure and emits nothing, which is why the
/// service takes an injectable reader — without it a test could assert the presence half
/// and prove nothing about the I/O half.</para>
/// </summary>
public class HotplugEdgeBindingTests
{
    private static readonly DeviceId BoardId = new(@"USB\VID_10C4&PID_8A7E\BOARD1");

    private static DeviceInfo Board(bool isActive) => new()
    {
        Id = BoardId,
        Name = "Treehopper",
        Category = DeviceCategory.Usb,
        VendorId = new HardwareId(0x10C4),
        ProductId = new HardwareId(0x8A7E),
        IsActive = isActive,
    };

    private sealed class EmptyProvider : IDeviceProvider
    {
        public async IAsyncEnumerable<DeviceInfo> EnumerateAsync(
            DeviceFilter filter, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class ManualMonitor : IDeviceMonitorProvider
    {
        public event EventHandler<DeviceChangeEventArgs>? DeviceAppeared;
        public event EventHandler<DeviceChangeEventArgs>? DeviceDisappeared;
        public event EventHandler<DeviceChangeEventArgs>? DeviceActivated;
        public event EventHandler<DeviceChangeEventArgs>? DeviceDeactivated;
        public event EventHandler<DeviceModificationEventArgs>? DevicePropertyChanged;

        public Task StartAsync(DeviceFilter filter, CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void Appeared(DeviceInfo d) => DeviceAppeared?.Invoke(this, new DeviceChangeEventArgs(d));
        public void Activated(DeviceInfo d) => DeviceActivated?.Invoke(this, new DeviceChangeEventArgs(d));
        public void Deactivated(DeviceInfo d) => DeviceDeactivated?.Invoke(this, new DeviceChangeEventArgs(d));
        public void Disappeared(DeviceInfo d) => DeviceDisappeared?.Invoke(this, new DeviceChangeEventArgs(d));

        public void Unused() => DevicePropertyChanged?.Invoke(this, null!);
    }

    private sealed class Harness : IAsyncDisposable
    {
        public ManualMonitor Monitor { get; } = new();
        public TreehopperControlService Service { get; }
        public List<DeviceId> VersionReads { get; } = [];
        public bool StillPresent { get; set; }

        public Harness()
        {
            var monitor = Monitor;
            Service = new TreehopperControlService(
                options: null,
                watcherFactory: () => new DeviceWatcher(new EmptyProvider(), monitor),
                readVersion: (id, _) => { VersionReads.Add(id); return Task.FromResult<int?>(42); },
                isStillPresent: (_, _) => Task.FromResult(StillPresent),
                // Hermetic: without this the harness would pick up whatever boards are
                // plugged into the machine running the suite.
                enumerateBoards: _ => Task.FromResult<IReadOnlyList<DeviceInfo>>([]));
        }

        public ValueTask DisposeAsync() => Service.DisposeAsync();
    }

    /// <summary>
    /// Waits for a condition the handlers produce, rather than sleeping a fixed interval.
    /// The handlers are fire-and-forget behind a gate, so a fixed delay is a guess: on a
    /// busy or paused runner the gate may not have run yet, and the assertion then reads
    /// stale state and fails for reasons unrelated to the behaviour under test.
    /// </summary>
    private static async Task WaitForAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(5);
        }

        Assert.Fail($"timed out waiting for {what}");
    }

    /// <summary>
    /// Waits for the handlers to have drained when the expectation is that NOTHING
    /// happens. There is no condition to wait on, so this yields the gate a bounded number
    /// of times instead - enough for a queued handler to have run and been observed.
    /// </summary>
    private static async Task SettleAsync()
    {
        for (int i = 0; i < 20; i++) await Task.Delay(5);
    }

    [Fact]
    public async Task Appeared_ListsTheBoardWithoutOpeningIt()
    {
        await using var h = new Harness();
        await h.Service.StartAsync();

        h.Monitor.Appeared(Board(isActive: false));
        await WaitForAsync(() => h.Service.State.Find(BoardId) is not null, "the board to be listed");
        await SettleAsync();

        Assert.NotNull(h.Service.State.Find(BoardId));
        Assert.Empty(h.VersionReads);
    }

    [Fact]
    public async Task Activated_ReadsTheVersion()
    {
        await using var h = new Harness();
        await h.Service.StartAsync();

        h.Monitor.Activated(Board(isActive: true));
        await WaitForAsync(() => h.VersionReads.Count > 0, "the version read");

        Assert.NotNull(h.Service.State.Find(BoardId));
        Assert.Equal([BoardId], h.VersionReads);
    }

    /// <summary>
    /// The whole point of the split: a board that enters the tree without starting is
    /// listed and never opened, and starting it later is what triggers the read.
    /// </summary>
    [Fact]
    public async Task AppearedThenActivated_ReadsTheVersionExactlyOnce()
    {
        await using var h = new Harness();
        await h.Service.StartAsync();

        h.Monitor.Appeared(Board(isActive: false));
        await WaitForAsync(() => h.Service.State.Find(BoardId) is not null, "the board to be listed");
        await SettleAsync();
        Assert.Empty(h.VersionReads);

        h.Monitor.Activated(Board(isActive: true));
        await WaitForAsync(() => h.VersionReads.Count > 0, "the version read");

        Assert.Equal([BoardId], h.VersionReads);
    }

    /// <summary>
    /// Inventory out. The absence re-verify still guards it, so a board that is still
    /// there — the transient drop while it re-enumerates through the bootloader — stays.
    /// </summary>
    [Fact]
    public async Task Disappeared_RemovesTheBoardOnlyWhenItIsReallyGone()
    {
        await using var h = new Harness();
        await h.Service.StartAsync();

        h.Monitor.Appeared(Board(isActive: false));
        h.Monitor.Activated(Board(isActive: true));
        await WaitForAsync(() => h.VersionReads.Count > 0, "the board to be activated");

        h.StillPresent = true;              // mid-re-enumeration
        h.Monitor.Disappeared(Board(isActive: false));
        await SettleAsync();
        Assert.NotNull(h.Service.State.Find(BoardId));

        h.StillPresent = false;             // actually unplugged
        h.Monitor.Disappeared(Board(isActive: false));
        await WaitForAsync(() => h.Service.State.Find(BoardId) is null, "the board to be removed");
    }

    /// <summary>
    /// Activity out does not touch the inventory. A device that stops without leaving the
    /// tree — the Bluetooth-out-of-range shape, and a driver restart for USB — keeps its
    /// place in the list.
    ///
    /// <para><b>This does not exercise the session close.</b> Live streaming is off by
    /// default, so no session is ever opened, and <c>OnDeactivated</c> finds
    /// <c>_session</c> null and returns — the assertion below would pass with the
    /// handler deleted. Covering the close needs a seam for opening a session, which
    /// would mean standing in for <c>TreehopperBoard</c> itself. That is a larger change
    /// than this PR, and asserting on inventory alone while implying otherwise would be
    /// worse than saying so.</para>
    /// </summary>
    [Fact]
    public async Task Deactivated_KeepsTheBoardInInventory()
    {
        await using var h = new Harness();
        await h.Service.StartAsync();

        h.Monitor.Appeared(Board(isActive: false));
        h.Monitor.Activated(Board(isActive: true));
        await WaitForAsync(() => h.VersionReads.Count > 0, "the board to be activated");

        h.Monitor.Deactivated(Board(isActive: false));
        await SettleAsync();

        Assert.NotNull(h.Service.State.Find(BoardId));
    }
}
