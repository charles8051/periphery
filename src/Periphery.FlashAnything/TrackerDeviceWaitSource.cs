// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Threading;
using System.Threading.Tasks;
using Periphery.Bootloader;

namespace Periphery.FlashAnything;

/// <summary>
/// An <see cref="IDeviceWaitSource"/> backed by the service's <see cref="MultiDeviceTracker"/>, so a
/// <see cref="BootloaderEntryOrchestrator"/> running inside FlashAnything <b>rides the same discovery
/// the service already runs</b> instead of starting a second, uncoordinated watcher (ADR-0063 slice 4).
/// This also gets the tracker's event-coalescing on the bootloader wait for free.
/// </summary>
/// <remarks>
/// <para><see cref="MultiDeviceTracker.Subscribe"/> delivers the current child states (the snapshot)
/// synchronously, then live updates — exactly the snapshot-then-live contract the orchestrator's arm
/// step needs.</para>
/// <para><b>Departures are not raised here.</b> The tracker's absent state carries no
/// <see cref="DeviceInfo"/> to identify which candidate left; but the app-mode bootloader wait arms
/// with an empty debounce baseline (no bootloader is present before the reboot), so a disappearance is
/// never consulted. The standalone <see cref="DeviceWatcherWaitSource"/> still observes departures.</para>
/// </remarks>
internal sealed class TrackerDeviceWaitSource : IDeviceWaitSource
{
    private readonly MultiDeviceTracker _tracker;
    private readonly DeviceFilter _filter;
    private IDisposable? _subscription;

    /// <inheritdoc/>
    public event Action<DeviceInfo>? Appeared;

#pragma warning disable CS0067 // never raised — see the class remarks (departures aren't observed; the baseline is empty)
    /// <inheritdoc/>
    public event Action<string>? Disappeared;
#pragma warning restore CS0067

    public TrackerDeviceWaitSource(MultiDeviceTracker tracker, DeviceFilter filter)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(filter);
        _tracker = tracker;
        _filter = filter;
    }

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken ct)
    {
        // Subscribe synchronously replays the current children (the snapshot), then streams live
        // updates — so the pre-existing baseline is complete before this returns.
        _subscription = _tracker.Subscribe(new Observer(this));
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        _subscription?.Dispose();
        return ValueTask.CompletedTask;
    }

    private sealed class Observer(TrackerDeviceWaitSource owner) : IObserver<DeviceTrackerState>
    {
        public void OnNext(DeviceTrackerState state)
        {
            if (state.Device is { } device
                && state.ActivityStatus != DeviceActivityStatus.Absent
                && owner._filter.Matches(device))
                owner.Appeared?.Invoke(device);
        }

        public void OnCompleted() { }
        public void OnError(Exception error) { }
    }
}
