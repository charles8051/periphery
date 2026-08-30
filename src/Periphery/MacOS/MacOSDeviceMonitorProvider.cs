// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Periphery.MacOS;

/// <summary>
/// macOS implementation of <see cref="IDeviceMonitorProvider"/> using
/// <c>IONotificationPort</c> + <c>dispatch_get_global_queue</c> (GCD) for
/// kernel-level device notifications and a <see cref="PeriodicTimer"/>
/// background scan for soft state changes and property mutations.
/// <para>Two notification types are registered after <see cref="StartAsync"/>:</para>
/// <list type="bullet">
/// <item><c>kIOMatchedNotification</c> — device appeared → <see cref="DeviceAppeared"/>
///   (+ <see cref="DeviceActivated"/> if active).</item>
/// <item><c>kIOTerminatedNotification</c> — device removed →
///   <see cref="DeviceDisappeared"/> (+ <see cref="DeviceDeactivated"/> if was active).</item>
/// </list>
/// <para>A background scan loop re-enumerates all devices every
/// <see cref="PropertyScanInterval"/> and fires <see cref="DeviceActivated"/>,
/// <see cref="DeviceDeactivated"/>, or <see cref="DevicePropertyChanged"/> for
/// soft state transitions and property mutations.</para>
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class MacOSDeviceMonitorProvider : IDeviceMonitorProvider
{
    private static readonly ILogger<MacOSDeviceMonitorProvider> _logger =
        PeripheryLoggerFactory.CreateLogger<MacOSDeviceMonitorProvider>();

    private readonly TimeSpan _propertyScanInterval;

    private GCHandle _selfHandle;
    private IntPtr _notifyPort;
    private int _started; // 0 = unstarted, 1 = started (Interlocked)
    private DeviceFilter? _filter;

    // Notification iterators that must be kept alive for the notification registration lifetime
    private readonly List<uint> _notificationIterators = [];

    private CancellationTokenSource? _scanCts;
    private Task? _scanTask;
    private readonly object _cacheLock = new();
    // Keyed by DeviceId for consistency with the other two providers. On macOS
    // DeviceInfo.Id is an IOKit registry entry id rendered as DECIMAL DIGITS
    // (MacOSDeviceProvider: Id = entryId.ToString()), so casing is meaningless here and
    // the default ordinal comparer this replaces was never wrong. See the note in
    // LinuxDeviceMonitorProvider for why all three are keyed by the type anyway.
    private readonly Dictionary<DeviceId, DeviceInfo> _lastKnownDevices = new();

    /// <summary>Interval between property-scan loop ticks. Default: 2 seconds.</summary>
    public TimeSpan PropertyScanInterval => _propertyScanInterval;

    public event EventHandler<DeviceChangeEventArgs>? DeviceAppeared;
    public event EventHandler<DeviceChangeEventArgs>? DeviceDisappeared;
    public event EventHandler<DeviceChangeEventArgs>? DeviceActivated;
    public event EventHandler<DeviceChangeEventArgs>? DeviceDeactivated;
    public event EventHandler<DeviceModificationEventArgs>? DevicePropertyChanged;

    public MacOSDeviceMonitorProvider(TimeSpan? propertyScanInterval = null)
    {
        _propertyScanInterval = propertyScanInterval ?? TimeSpan.FromSeconds(2);
    }

    public unsafe Task StartAsync(DeviceFilter filter, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
            throw new InvalidOperationException(
                "StartAsync has already been called. Dispose and create a new monitor to restart.");

        _filter = filter;

        _logger.LogInformation("Starting device monitor via IOKit notifications");

        try
        {
            _selfHandle = GCHandle.Alloc(this);

            _notifyPort = IOKitInterop.IONotificationPortCreate(IOKitInterop.kIOMasterPortDefault);
            if (_notifyPort == IntPtr.Zero)
                throw new DeviceProviderException("IONotificationPortCreate returned null.");

            // Set the notification port to dispatch on a global GCD queue (no NSRunLoop needed)
            IntPtr queue = IOKitInterop.dispatch_get_global_queue(0 /* QOS_CLASS_DEFAULT */, 0);
            IOKitInterop.IONotificationPortSetDispatchQueue(_notifyPort, queue);

            // Register for arrivals and removals for each IOKit class
            string[] ioKitClasses = MacOSCategoryMap.GetIOKitClasses(
                _filter.Category.HasValue && _filter.Category.Value != DeviceCategory.All
                    ? _filter.Category.Value
                    : null);

            foreach (var ioKitClass in ioKitClasses)
            {
                // Arrival notification
                IntPtr arrivedDict = IOKitInterop.IOServiceMatching(ioKitClass);
                if (arrivedDict != IntPtr.Zero)
                {
                    int kr = IOKitInterop.IOServiceAddMatchingNotification(
                        _notifyPort,
                        IOKitInterop.kIOMatchedNotification,
                        arrivedDict,
                        &MatchedNotificationShim,
                        GCHandle.ToIntPtr(_selfHandle),
                        out uint arrivedIter);

                    if (kr == IOKitInterop.kIOReturnSuccess)
                    {
                        _notificationIterators.Add(arrivedIter);
                        // Drain the initial set without firing events
                        DrainIterator(arrivedIter, ioKitClass, fireEvents: false);
                    }
                    else
                    {
                        _logger.LogWarning(
                            "IOServiceAddMatchingNotification (matched) failed for {Class}: kr=0x{KernReturn:X8}",
                            ioKitClass, kr);
                    }
                }

                // Removal notification
                IntPtr removedDict = IOKitInterop.IOServiceMatching(ioKitClass);
                if (removedDict != IntPtr.Zero)
                {
                    int kr = IOKitInterop.IOServiceAddMatchingNotification(
                        _notifyPort,
                        IOKitInterop.kIOTerminatedNotification,
                        removedDict,
                        &TerminatedNotificationShim,
                        GCHandle.ToIntPtr(_selfHandle),
                        out uint removedIter);

                    if (kr == IOKitInterop.kIOReturnSuccess)
                    {
                        _notificationIterators.Add(removedIter);
                        // Drain the initial set without firing events
                        DrainIterator(removedIter, ioKitClass, fireEvents: false);
                    }
                    else
                    {
                        _logger.LogWarning(
                            "IOServiceAddMatchingNotification (terminated) failed for {Class}: kr=0x{KernReturn:X8}",
                            ioKitClass, kr);
                    }
                }
            }

            // Seed the property-scan cache with the current device snapshot
            SeedCache(ioKitClasses);

            _scanCts = new CancellationTokenSource();
            _scanTask = Task.Run(() => ScanLoopAsync(_scanCts.Token));

            _logger.LogInformation(
                "Device notifications registered; scan loop started (interval: {Interval} s).",
                _propertyScanInterval.TotalSeconds);
            return Task.CompletedTask;
        }
        catch (DeviceProviderException)
        {
            CleanupAfterFailedStart();
            throw;
        }
        catch (Exception ex)
        {
            CleanupAfterFailedStart();
            _logger.LogError(ex, "Failed to start device monitor");
            throw new DeviceProviderException($"Failed to start device monitor: {ex.Message}", ex);
        }
    }

    private void CleanupAfterFailedStart()
    {
        foreach (var iter in _notificationIterators)
            IOKitInterop.IOObjectRelease(iter);
        _notificationIterators.Clear();

        if (_notifyPort != IntPtr.Zero)
        {
            IOKitInterop.IONotificationPortDestroy(_notifyPort);
            _notifyPort = IntPtr.Zero;
        }

        if (_selfHandle.IsAllocated)
            _selfHandle.Free();
    }

    // ── AOT-safe static callback shims ─────────────────────────────────

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void MatchedNotificationShim(IntPtr refCon, uint iterator)
    {
        var self = (MacOSDeviceMonitorProvider)GCHandle.FromIntPtr(refCon).Target!;
        self.OnDeviceMatched(iterator);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void TerminatedNotificationShim(IntPtr refCon, uint iterator)
    {
        var self = (MacOSDeviceMonitorProvider)GCHandle.FromIntPtr(refCon).Target!;
        self.OnDeviceTerminated(iterator);
    }

    // ── Notification handlers ──────────────────────────────────────────

    private void OnDeviceMatched(uint iterator)
    {
        uint service;
        while ((service = IOKitInterop.IOIteratorNext(iterator)) != 0)
        {
            try
            {
                string? ioKitClass = IOKitInterop.GetIOObjectClassName(service);
                DeviceInfo? device = MacOSDeviceProvider.TryBuildDeviceInfo(
                    service, ioKitClass ?? "Unknown");
                if (device is null) continue;

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
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error processing device arrival notification");
            }
            finally
            {
                IOKitInterop.IOObjectRelease(service);
            }
        }
    }

    private void OnDeviceTerminated(uint iterator)
    {
        uint service;
        while ((service = IOKitInterop.IOIteratorNext(iterator)) != 0)
        {
            try
            {
                // Read the entry ID before the service is fully gone
                IOKitInterop.IORegistryEntryGetRegistryEntryID(service, out ulong entryId);
                string deviceId = entryId.ToString();

                DeviceInfo? cached;
                lock (_cacheLock)
                    _lastKnownDevices.Remove(deviceId, out cached);

                DeviceInfo device = cached ?? new DeviceInfo { Id = deviceId };

                if (cached is { IsActive: true })
                {
                    _logger.LogDebug("Device disconnected: {DeviceId}", device.Id);
                    DeviceDeactivated?.Invoke(this, new DeviceChangeEventArgs(device));
                }

                _logger.LogDebug("Device disappeared: {DeviceId}", device.Id);
                DeviceDisappeared?.Invoke(this, new DeviceChangeEventArgs(device));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error processing device removal notification");
            }
            finally
            {
                IOKitInterop.IOObjectRelease(service);
            }
        }
    }

    /// <summary>
    /// Drains an IOKit iterator, optionally firing events for each entry.
    /// Must be called after <c>IOServiceAddMatchingNotification</c> to arm the notification.
    /// </summary>
    private void DrainIterator(uint iterator, string ioKitClass, bool fireEvents)
    {
        uint service;
        while ((service = IOKitInterop.IOIteratorNext(iterator)) != 0)
        {
            try
            {
                if (fireEvents)
                {
                    DeviceInfo? device = MacOSDeviceProvider.TryBuildDeviceInfo(service, ioKitClass);
                    if (device is not null)
                    {
                        lock (_cacheLock)
                            _lastKnownDevices[device.Id] = device;

                        DeviceAppeared?.Invoke(this, new DeviceChangeEventArgs(device));
                        if (device.IsActive)
                            DeviceActivated?.Invoke(this, new DeviceChangeEventArgs(device));
                    }
                }
            }
            finally
            {
                IOKitInterop.IOObjectRelease(service);
            }
        }
    }

    /// <summary>
    /// Seeds the last-known-devices cache by enumerating all matching services.
    /// </summary>
    private void SeedCache(string[] ioKitClasses)
    {
        var seenEntryIds = new HashSet<ulong>();

        lock (_cacheLock)
        {
            foreach (var ioKitClass in ioKitClasses)
            {
                IntPtr matchingDict = IOKitInterop.IOServiceMatching(ioKitClass);
                if (matchingDict == IntPtr.Zero) continue;

                int kr = IOKitInterop.IOServiceGetMatchingServices(
                    IOKitInterop.kIOMasterPortDefault, matchingDict, out uint iterator);
                if (kr != IOKitInterop.kIOReturnSuccess) continue;

                try
                {
                    uint service;
                    while ((service = IOKitInterop.IOIteratorNext(iterator)) != 0)
                    {
                        try
                        {
                            IOKitInterop.IORegistryEntryGetRegistryEntryID(service, out ulong entryId);
                            if (!seenEntryIds.Add(entryId)) continue;

                            DeviceInfo? device = MacOSDeviceProvider.TryBuildDeviceInfo(service, ioKitClass);
                            if (device is not null)
                                _lastKnownDevices[device.Id] = device;
                        }
                        catch { /* skip unreadable devices */ }
                        finally
                        {
                            IOKitInterop.IOObjectRelease(service);
                        }
                    }
                }
                finally
                {
                    IOKitInterop.IOObjectRelease(iterator);
                }
            }
        }
    }

    // ── Property scan loop ─────────────────────────────────────────────

    private async Task ScanLoopAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(_propertyScanInterval);
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    ScanForChanges();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Scan loop iteration failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown — token cancelled by DisposeAsync
        }
    }

    private void ScanForChanges()
    {
        string[] ioKitClasses = MacOSCategoryMap.GetIOKitClasses(
            _filter?.Category is { } cat && cat != DeviceCategory.All ? cat : null);

        var seenEntryIds = new HashSet<ulong>();

        foreach (var ioKitClass in ioKitClasses)
        {
            IntPtr matchingDict = IOKitInterop.IOServiceMatching(ioKitClass);
            if (matchingDict == IntPtr.Zero) continue;

            int kr = IOKitInterop.IOServiceGetMatchingServices(
                IOKitInterop.kIOMasterPortDefault, matchingDict, out uint iterator);
            if (kr != IOKitInterop.kIOReturnSuccess) continue;

            try
            {
                uint service;
                while ((service = IOKitInterop.IOIteratorNext(iterator)) != 0)
                {
                    try
                    {
                        IOKitInterop.IORegistryEntryGetRegistryEntryID(service, out ulong entryId);
                        if (!seenEntryIds.Add(entryId)) continue;

                        string deviceId = entryId.ToString();

                        DeviceInfo? previous;
                        lock (_cacheLock)
                            _lastKnownDevices.TryGetValue(deviceId, out previous);

                        if (previous is null) continue; // New device — notification callback handles it

                        DeviceInfo current;
                        try { current = MacOSDeviceProvider.ToDeviceInfo(service, ioKitClass); }
                        catch { continue; }

                        IReadOnlySet<string> changed = DeviceInfoDiff.Compute(previous, current);
                        if (changed.Count == 0) continue;

                        lock (_cacheLock)
                            _lastKnownDevices[deviceId] = current;

                        if (changed.Contains(nameof(DeviceInfo.IsActive)))
                        {
                            if (current.IsActive)
                            {
                                _logger.LogDebug("Device activated (soft): {DeviceId}", deviceId);
                                DeviceActivated?.Invoke(this, new DeviceChangeEventArgs(current));
                            }
                            else
                            {
                                _logger.LogDebug("Device deactivated (soft): {DeviceId}", deviceId);
                                DeviceDeactivated?.Invoke(this, new DeviceChangeEventArgs(current));
                            }
                        }

                        DevicePropertyChanged?.Invoke(this, new DeviceModificationEventArgs(previous, current));
                    }
                    finally
                    {
                        IOKitInterop.IOObjectRelease(service);
                    }
                }
            }
            finally
            {
                IOKitInterop.IOObjectRelease(iterator);
            }
        }
    }

    // ── Disposal ───────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        _logger.LogInformation("Stopping device monitor, cancelling scan loop and releasing IOKit handles");

        if (_scanCts is not null)
        {
            await _scanCts.CancelAsync().ConfigureAwait(false);
            await (_scanTask ?? Task.CompletedTask).ConfigureAwait(false);
            _scanCts.Dispose();
            _scanCts = null;
        }

        foreach (var iter in _notificationIterators)
            IOKitInterop.IOObjectRelease(iter);
        _notificationIterators.Clear();

        if (_notifyPort != IntPtr.Zero)
        {
            IOKitInterop.IONotificationPortDestroy(_notifyPort);
            _notifyPort = IntPtr.Zero;
        }

        if (_selfHandle.IsAllocated)
            _selfHandle.Free();

        _logger.LogInformation("Device monitor stopped.");
    }
}
