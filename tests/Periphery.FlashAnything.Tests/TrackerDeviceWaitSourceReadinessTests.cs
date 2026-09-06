// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Periphery.FlashAnything;

namespace Periphery.FlashAnything.Tests;

/// <summary>
/// A bootloader wait is a <b>readiness</b> gate, not a presence gate. When it completes,
/// the caller immediately opens a USB or serial handle on the device. A devnode that is
/// in the OS device tree but whose driver has not started cannot be opened, and the
/// resulting failure is the one <c>BootloaderEntryOrchestrator</c> already documents from
/// issue #251 — a wasted attempt that eats the recovery budget on healthy hardware.
///
/// <para>ADR-0004 separates present from active precisely so a consumer can ask for the
/// one it needs. This source asked for neither: it fired on any non-<c>Absent</c> state,
/// which is <c>Present</c> or <c>Active</c>.</para>
///
/// <para><b>This is live today on Linux and macOS.</b> Both raise <c>DeviceAppeared</c> on
/// arrival before raising <c>DeviceActivated</c>, so a tracker reaches <c>Present</c> and
/// this gate opens early. Windows has been shielded only because its <c>DeviceAppeared</c>
/// never fires for a live arrival (issue #177) — so on Windows, gating on activity is
/// exactly the behaviour that ships today, made explicit and kept correct once that
/// provider bug is fixed.</para>
/// </summary>
public class TrackerDeviceWaitSourceReadinessTests
{
    private static DeviceInfo Device(bool isActive) => new()
    {
        Id = @"USB\VID_1234&PID_5678\BOOTLOADER",
        Name = "Bootloader Device",
        Category = DeviceCategory.Usb,
        IsActive = isActive,
    };

    /// <summary>Enumerates nothing, so the startup walk contributes no state.</summary>
    private sealed class EmptyProvider : IDeviceProvider
    {
        public async IAsyncEnumerable<DeviceInfo> EnumerateAsync(
            DeviceFilter filter,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    /// <summary>Raises the provider edges directly, so each is exercised on its own.</summary>
    private sealed class ManualMonitor : IDeviceMonitorProvider
    {
        public event EventHandler<DeviceChangeEventArgs>? DeviceAppeared;
        public event EventHandler<DeviceChangeEventArgs>? DeviceDisappeared;
        public event EventHandler<DeviceChangeEventArgs>? DeviceActivated;
        public event EventHandler<DeviceChangeEventArgs>? DeviceDeactivated;
        public event EventHandler<DeviceModificationEventArgs>? DevicePropertyChanged;

        public Task StartAsync(DeviceFilter filter, CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void RaiseAppeared(DeviceInfo d) => DeviceAppeared?.Invoke(this, new DeviceChangeEventArgs(d));
        public void RaiseActivated(DeviceInfo d) => DeviceActivated?.Invoke(this, new DeviceChangeEventArgs(d));

        // Declared by the interface; this fixture drives only the two arrival edges.
        public void Unused() { DeviceDisappeared?.Invoke(this, null!); DeviceDeactivated?.Invoke(this, null!); DevicePropertyChanged?.Invoke(this, null!); }
    }

    private static async Task<(TrackerDeviceWaitSource Source, ManualMonitor Monitor, DeviceWatcher Watcher, List<DeviceInfo> Seen)> BuildAsync()
    {
        var monitor = new ManualMonitor();
        var watcher = new DeviceWatcher(new EmptyProvider(), monitor);
        var tracker = watcher.AddMultiTracker(f => f.OfCategory(DeviceCategory.Usb), name: "Bootloader");
        await watcher.StartAsync();

        var source = new TrackerDeviceWaitSource(tracker, new DeviceFilter());
        var seen = new List<DeviceInfo>();
        source.Appeared += d => seen.Add(d);
        return (source, monitor, watcher, seen);
    }

    /// <summary>
    /// The failing case. A devnode has entered the tree but has not started, so the
    /// tracker is Present. Opening a handle now throws.
    /// </summary>
    [Fact]
    public async Task PresentButNotStarted_DoesNotCompleteTheWait()
    {
        var (source, monitor, watcher, seen) = await BuildAsync();
        await using var _ = watcher;
        await using var __ = source;
        await source.StartAsync(CancellationToken.None);

        monitor.RaiseAppeared(Device(isActive: false));

        Assert.Empty(seen);
    }

    /// <summary>
    /// Control. Once the driver has started, the wait completes — the gate must not be
    /// bought by making the source deaf.
    /// </summary>
    [Fact]
    public async Task Active_CompletesTheWait()
    {
        var (source, monitor, watcher, seen) = await BuildAsync();
        await using var _ = watcher;
        await using var __ = source;
        await source.StartAsync(CancellationToken.None);

        var device = Device(isActive: true);
        monitor.RaiseAppeared(device);
        monitor.RaiseActivated(device);

        Assert.Single(seen);
    }

    /// <summary>
    /// Control. The normal ordering — a device that appears inactive and then starts —
    /// completes exactly once, on the activation, not twice.
    /// </summary>
    [Fact]
    public async Task AppearedThenActivated_CompletesOnceOnActivation()
    {
        var (source, monitor, watcher, seen) = await BuildAsync();
        await using var _ = watcher;
        await using var __ = source;
        await source.StartAsync(CancellationToken.None);

        monitor.RaiseAppeared(Device(isActive: false));
        Assert.Empty(seen);

        monitor.RaiseActivated(Device(isActive: true));

        Assert.Single(seen);
    }
}
