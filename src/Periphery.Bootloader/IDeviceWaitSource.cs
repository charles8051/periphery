// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Periphery.Bootloader;

/// <summary>
/// A per-wait source of candidate appeared/disappeared events for one <see cref="DeviceFilter"/>,
/// with a startup snapshot of those already present. <see cref="BootloaderEntryOrchestrator"/>
/// consumes this instead of owning a <see cref="DeviceWatcher"/>, so it can <b>ride a caller's
/// existing discovery</b> (e.g. FlashAnything's <see cref="MultiDeviceTracker"/>) rather than
/// starting a second, uncoordinated watcher. Standalone callers get the default
/// <see cref="DeviceWatcherWaitSource"/> (a fresh watcher per wait).
/// </summary>
/// <remarks>
/// The orchestrator subscribes, then <see cref="StartAsync"/>, then arms its pure correlation core;
/// so <see cref="StartAsync"/> must fire <see cref="Appeared"/> for every already-present candidate
/// (the snapshot) <b>before it returns</b>, so the debounce baseline is complete on arm.
/// </remarks>
public interface IDeviceWaitSource : IAsyncDisposable
{
    /// <summary>A candidate matching the filter became present (active).</summary>
    event Action<DeviceInfo>? Appeared;

    /// <summary>A candidate left the bus (by device id). A source that cannot observe departures may never raise this.</summary>
    event Action<string>? Disappeared;

    /// <summary>Begin observing. Fires <see cref="Appeared"/> for every already-present candidate (the snapshot) before returning.</summary>
    Task StartAsync(CancellationToken ct);
}

/// <summary>
/// The default <see cref="IDeviceWaitSource"/>: a fresh <see cref="DeviceWatcher"/> filtered to the
/// candidates, mapping its <c>Activated</c> to <see cref="Appeared"/> and
/// <c>Deactivated</c>/<c>Disappeared</c> to <see cref="Disappeared"/>. Used by standalone callers
/// (e.g. <c>TreehopperFirmwareUpdate</c>) that have no shared discovery to ride.
/// </summary>
public sealed class DeviceWatcherWaitSource : IDeviceWaitSource
{
    private readonly DeviceWatcher _watcher;

    /// <inheritdoc/>
    public event Action<DeviceInfo>? Appeared;

    /// <inheritdoc/>
    public event Action<string>? Disappeared;

    /// <summary>Creates a wait source over a fresh watcher filtered to candidates matching <paramref name="filter"/>.</summary>
    public DeviceWatcherWaitSource(DeviceFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        // .Where(filter.Matches) keeps the event stream to candidates that already match the
        // bootloader/app identity — the recognition + safety gate, enforced upstream of the core.
        _watcher = Devices.Watch().Where(filter.Matches);
        _watcher.Activated += (_, e) => Appeared?.Invoke(e.Device);
        _watcher.Deactivated += (_, e) => Disappeared?.Invoke(e.Device.Id);
        _watcher.Disappeared += (_, e) => Disappeared?.Invoke(e.Device.Id);
    }

    /// <inheritdoc/>
    // The watcher fires Activated for every already-present candidate (the snapshot) before
    // StartAsync returns, so the baseline is complete on return.
    public Task StartAsync(CancellationToken ct) => _watcher.StartAsync(ct);

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => _watcher.DisposeAsync();
}
