// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Periphery.Linux;

/// <summary>
/// Linux implementation of <see cref="IDeviceMonitorProvider"/> using libudev
/// <c>udev_monitor</c> for real-time device event notifications.
/// <para>
/// Subscribes to the <c>"udev"</c> netlink source so events have already been
/// processed through udev rules — device names, symlinks, and synthesised
/// attributes are fully resolved when the callback fires.
/// </para>
/// <para>Polls the monitor file descriptor on a dedicated
/// <see cref="TaskCreationOptions.LongRunning"/> task. No native callback or
/// <see cref="System.Runtime.InteropServices.GCHandle"/> is needed (unlike Windows).</para>
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class LinuxDeviceMonitorProvider : IDeviceMonitorProvider
{
    private static readonly ILogger<LinuxDeviceMonitorProvider> _logger =
        PeripheryLoggerFactory.CreateLogger<LinuxDeviceMonitorProvider>();

    private int _started; // 0 = unstarted, 1 = started (Interlocked)

    private IntPtr _udev;
    private IntPtr _monitor;
    private int _monitorFd;

    private CancellationTokenSource? _monitorCts;
    private Task? _monitorTask;

    private readonly object _cacheLock = new();
    // Keyed by DeviceId so the cache cannot drift from DeviceId's equality contract.
    // Defense in depth, NOT a fix for an observed failure here: on Linux DeviceInfo.Id is
    // the udev syspath (LinuxDeviceProvider sets Id = syspath), which is lowercase and
    // stable, so the StringComparer.ORDINAL this replaces was not producing the split-cache
    // bug #231 describes. The point is that the invariant now rides on the key type rather
    // than on a comparer argument each of the three providers has to remember — the Windows
    // provider (whose ids DO flip case, and where the bug IS live) held it in a comparer,
    // and neither Linux nor macOS picked it up when they were written. If the Linux id
    // scheme ever changes to something case-bearing, this cache is already correct.
    private readonly Dictionary<DeviceId, DeviceInfo> _lastKnownDevices = new();

    public event EventHandler<DeviceChangeEventArgs>? DeviceAppeared;
    public event EventHandler<DeviceChangeEventArgs>? DeviceDisappeared;
    public event EventHandler<DeviceChangeEventArgs>? DeviceActivated;
    public event EventHandler<DeviceChangeEventArgs>? DeviceDeactivated;
    public event EventHandler<DeviceModificationEventArgs>? DevicePropertyChanged;

    public Task StartAsync(DeviceFilter filter, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
            throw new InvalidOperationException(
                "StartAsync has already been called. Dispose and create a new monitor to restart.");

        _logger.LogInformation("Starting device monitor via libudev udev_monitor");

        try
        {
            _udev = UdevInterop.udev_new();
            if (_udev == IntPtr.Zero)
                throw new DeviceProviderException("Failed to create udev context.");

            _monitor = UdevInterop.MonitorNewFromNetlink(_udev);
            if (_monitor == IntPtr.Zero)
                throw new DeviceProviderException("Failed to create udev monitor.");

            // Add subsystem filters if a specific category is requested
            string[] subsystems = filter.Category.HasValue && filter.Category.Value != DeviceCategory.All
                ? LinuxCategoryMap.GetSubsystems(filter.Category.Value)
                : [];

            foreach (var subsystem in subsystems)
                UdevInterop.MonitorFilterAddMatchSubsystem(_monitor, subsystem);

            int enableResult = UdevInterop.udev_monitor_enable_receiving(_monitor);
            if (enableResult < 0)
                throw new DeviceProviderException(
                    $"udev_monitor_enable_receiving failed with error code {enableResult}.");

            _monitorFd = UdevInterop.udev_monitor_get_fd(_monitor);
            if (_monitorFd < 0)
                throw new DeviceProviderException(
                    $"udev_monitor_get_fd returned invalid fd {_monitorFd}.");

            // Seed the cache with the current device snapshot
            SeedCache();

            _monitorCts = new CancellationTokenSource();
            // Schedule with CancellationToken.None, NOT _monitorCts.Token. Passing the
            // monitor token as the StartNew creation token makes the TPL cancel the task
            // *before it runs the delegate* whenever that token is already signalled —
            // transitioning it straight to the Canceled state without the loop ever
            // running. A fast StartAsync -> DisposeAsync (every lifecycle path, including
            // the contract tests) cancels the token before the dedicated LongRunning
            // thread has started, so DisposeAsync would then await a Canceled task and
            // surface a spurious TaskCanceledException. Cancellation is the loop's job:
            // MonitorLoopAsync observes _monitorCts.Token via its while-condition (and
            // catches OperationCanceledException) and returns cleanly, so the delegate
            // must always be allowed to start.
            _monitorTask = Task.Factory.StartNew(
                () => MonitorLoopAsync(_monitorCts.Token),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default).Unwrap();

            _logger.LogInformation("Device monitor started; polling udev_monitor fd={Fd}", _monitorFd);
            return Task.CompletedTask;
        }
        catch (DllNotFoundException ex)
        {
            CleanupHandles();
            throw new DeviceProviderException(
                "libudev.so.1 is not available on this system. " +
                "Install libudev to use device monitoring.",
                ex);
        }
        catch (DeviceProviderException)
        {
            CleanupHandles();
            throw;
        }
        catch (Exception ex)
        {
            CleanupHandles();
            _logger.LogError(ex, "Failed to start device monitor");
            throw new DeviceProviderException($"Failed to start device monitor: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Seeds the <c>_lastKnownDevices</c> cache with a full enumeration snapshot
    /// so that property-change detection works from the first event.
    /// </summary>
    private void SeedCache()
    {
        var enumerate = UdevInterop.udev_enumerate_new(_udev);
        if (enumerate == IntPtr.Zero) return;

        try
        {
            UdevInterop.udev_enumerate_scan_devices(enumerate);
            var entry = UdevInterop.udev_enumerate_get_list_entry(enumerate);

            lock (_cacheLock)
            {
                while (entry != IntPtr.Zero)
                {
                    var syspathPtr = UdevInterop.udev_list_entry_get_name(entry);
                    var syspath = UdevInterop.PtrToString(syspathPtr);

                    if (syspath is not null)
                    {
                        var dev = UdevInterop.DeviceNewFromSyspath(_udev, syspath);
                        if (dev != IntPtr.Zero)
                        {
                            try
                            {
                                var info = LinuxDeviceProvider.ToDeviceInfo(dev, syspath);
                                if (info is not null)
                                    _lastKnownDevices[info.Id] = info;
                            }
                            catch
                            {
                                // Skip unreadable devices during cache seeding
                            }
                            finally
                            {
                                UdevInterop.udev_device_unref(dev);
                            }
                        }
                    }

                    entry = UdevInterop.udev_list_entry_get_next(entry);
                }
            }

            _logger.LogDebug("Cache seeded with {Count} devices", _lastKnownDevices.Count);
        }
        finally
        {
            UdevInterop.udev_enumerate_unref(enumerate);
        }
    }

    /// <summary>
    /// Long-running polling loop that reads udev monitor events and dispatches them.
    /// </summary>
    private async Task MonitorLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Poll the fd with 100ms timeout to allow cancellation checks
                if (!UdevInterop.PollFdReadable(_monitorFd, timeoutMs: 100))
                    continue;

                var dev = UdevInterop.udev_monitor_receive_device(_monitor);
                if (dev == IntPtr.Zero) continue;

                try
                {
                    DispatchAction(dev);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error dispatching udev event");
                    System.Diagnostics.Debug.WriteLine($"Error dispatching udev event: {ex.Message}");
                }
                finally
                {
                    UdevInterop.udev_device_unref(dev);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown — token cancelled by DisposeAsync
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>
    /// Routes a udev event to the appropriate handler based on the action string.
    /// </summary>
    private void DispatchAction(IntPtr dev)
    {
        var actionPtr = UdevInterop.udev_device_get_action(dev);
        var action = UdevInterop.PtrToString(actionPtr);

        var syspathPtr = UdevInterop.udev_device_get_syspath(dev);
        var syspath = UdevInterop.PtrToString(syspathPtr);

        if (action is null || syspath is null) return;

        switch (action)
        {
            case "add":
                HandleAdd(dev, syspath);
                break;
            case "remove":
                HandleRemove(syspath);
                break;
            case "bind":
                HandleBind(dev, syspath);
                break;
            case "unbind":
                HandleUnbind(syspath);
                break;
            case "change":
                HandleChange(dev, syspath);
                break;
        }
    }

    private void HandleAdd(IntPtr dev, string syspath)
    {
        var device = LinuxDeviceProvider.ToDeviceInfo(dev, syspath);
        if (device is null) return;

        lock (_cacheLock)
            _lastKnownDevices[device.Id] = device;

        _logger.LogDebug("Device appeared: {DeviceId} ({DeviceName})", device.Id, device.Name ?? "(unnamed)");
        DeviceAppeared?.Invoke(this, new DeviceChangeEventArgs(device));

        if (device.IsActive)
        {
            _logger.LogDebug("Device activated: {DeviceId} ({DeviceName})", device.Id, device.Name ?? "(unnamed)");
            DeviceActivated?.Invoke(this, new DeviceChangeEventArgs(device));
        }
    }

    private void HandleRemove(string syspath)
    {
        DeviceInfo? cached;
        lock (_cacheLock)
            _lastKnownDevices.Remove(syspath, out cached);

        var device = cached ?? new DeviceInfo { Id = syspath };

        _logger.LogDebug("Device disappeared: {DeviceId} ({DeviceName})", device.Id, device.Name ?? "(unnamed)");
        DeviceDisappeared?.Invoke(this, new DeviceChangeEventArgs(device));
    }

    private void HandleBind(IntPtr dev, string syspath)
    {
        var device = LinuxDeviceProvider.ToDeviceInfo(dev, syspath);
        if (device is null) return;

        lock (_cacheLock)
            _lastKnownDevices[device.Id] = device;

        _logger.LogDebug("Device activated (bind): {DeviceId} ({DeviceName})", device.Id, device.Name ?? "(unnamed)");
        DeviceActivated?.Invoke(this, new DeviceChangeEventArgs(device));
    }

    private void HandleUnbind(string syspath)
    {
        DeviceInfo? cached;
        lock (_cacheLock)
            _lastKnownDevices.TryGetValue(syspath, out cached);

        var device = cached ?? new DeviceInfo { Id = syspath };

        _logger.LogDebug("Device deactivated (unbind): {DeviceId}", device.Id);
        DeviceDeactivated?.Invoke(this, new DeviceChangeEventArgs(device));
    }

    private void HandleChange(IntPtr dev, string syspath)
    {
        var current = LinuxDeviceProvider.ToDeviceInfo(dev, syspath);
        if (current is null) return;

        DeviceInfo? previous;
        lock (_cacheLock)
        {
            _lastKnownDevices.TryGetValue(syspath, out previous);
            _lastKnownDevices[current.Id] = current;
        }

        if (previous is null) return;

        var changed = DeviceInfoDiff.Compute(previous, current);
        if (changed.Count == 0) return;

        // Fire connect/disconnect for soft state transitions
        if (changed.Contains(nameof(DeviceInfo.IsActive)))
        {
            if (current.IsActive)
            {
                _logger.LogDebug("Device activated (soft): {DeviceId}", current.Id);
                DeviceActivated?.Invoke(this, new DeviceChangeEventArgs(current));
            }
            else
            {
                _logger.LogDebug("Device deactivated (soft): {DeviceId}", current.Id);
                DeviceDeactivated?.Invoke(this, new DeviceChangeEventArgs(current));
            }
        }

        DevicePropertyChanged?.Invoke(this, new DeviceModificationEventArgs(previous, current));
    }

    private void CleanupHandles()
    {
        if (_monitor != IntPtr.Zero)
        {
            UdevInterop.udev_monitor_unref(_monitor);
            _monitor = IntPtr.Zero;
        }
        if (_udev != IntPtr.Zero)
        {
            UdevInterop.udev_unref(_udev);
            _udev = IntPtr.Zero;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _logger.LogInformation("Stopping device monitor");

        // Cancel and await the polling task before releasing handles
        if (_monitorCts is not null)
        {
            await _monitorCts.CancelAsync().ConfigureAwait(false);
            await (_monitorTask ?? Task.CompletedTask).ConfigureAwait(false);
            _monitorCts.Dispose();
            _monitorCts = null;
        }

        // Release udev handles in reverse registration order
        CleanupHandles();

        _logger.LogInformation("Device monitor stopped.");
    }
}
