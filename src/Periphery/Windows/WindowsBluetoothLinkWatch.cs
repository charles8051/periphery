// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Periphery.Windows;

/// <summary>
/// Thin imperative shell that listens for Bluetooth link changes on every local radio and
/// hands each decoded change to a caller-supplied callback (issue #286).
///
/// <para><b>Why a separate registration.</b> cfgmgr32's devnode stream raises nothing when an
/// already-paired device connects or disconnects, although the link devnode's status changes.
/// The Bluetooth driver does push it: <c>GUID_BLUETOOTH_HCI_EVENT</c>, delivered as a
/// <c>CM_NOTIFY_ACTION_DEVICECUSTOMEVENT</c> to a <c>CM_NOTIFY_FILTER_TYPE_DEVICEHANDLE</c>
/// registration on the radio. Measured on a BR/EDR peripheral: one event per connect and per
/// disconnect (docs/explorations/bluetooth-os-apis-2026-09.md, Liveness → Windows).</para>
///
/// <para><b>No handle is held.</b> Each radio is opened with <c>FILE_READ_ATTRIBUTES</c> only
/// for the <c>CM_Register_Notification</c> call and closed as soon as it returns, which
/// <c>CM_NOTIFY_FILTER</c> documents as allowed. Nothing here can block the radio being
/// disabled or removed. A radio's registration ends with the radio; a class-scoped
/// <c>GUID_BTHPORT_DEVICE_INTERFACE</c> registration picks up radios that arrive later.</para>
///
/// <para><b>Never fails the watcher.</b> Every failure is logged and leaves the provider with
/// the behaviour it had before this class existed: no Bluetooth link edges.</para>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsBluetoothLinkWatch : IDisposable
{
    private static readonly ILogger<WindowsBluetoothLinkWatch> _logger =
        PeripheryLoggerFactory.CreateLogger<WindowsBluetoothLinkWatch>();

    private const int CR_SUCCESS = 0;

    // Callback context is an id into this map rather than a GCHandle. A registration made
    // on a thread-pool thread can race Dispose, and a callback that finds no entry is simply
    // dropped, where a freed GCHandle would fault inside an [UnmanagedCallersOnly] frame.
    private static readonly ConcurrentDictionary<nint, WindowsBluetoothLinkWatch> s_instances = new();
    private static long s_nextId;

    private readonly Action<BluetoothLinkChange> _onLinkChange;
    private readonly nint _id;

    private readonly object _lock = new();
    private readonly Dictionary<string, DevNodeHelper.CmNotifyHandle> _radios = new(StringComparer.OrdinalIgnoreCase);
    private DevNodeHelper.CmNotifyHandle? _radioArrivals;
    private bool _disposed;

    public WindowsBluetoothLinkWatch(Action<BluetoothLinkChange> onLinkChange)
    {
        _onLinkChange = onLinkChange;
        _id = (nint)Interlocked.Increment(ref s_nextId);
        s_instances[_id] = this;
    }

    /// <summary>
    /// Registers for radio arrivals, then for link events on every radio present now. The
    /// arrival registration comes first so a radio that appears in between is seen by at
    /// least one of the two; <see cref="RegisterRadio"/> ignores the second sighting.
    /// </summary>
    public unsafe void Start()
    {
        try
        {
            var filter = new DevNodeHelper.CM_NOTIFY_FILTER
            {
                cbSize = Marshal.SizeOf<DevNodeHelper.CM_NOTIFY_FILTER>(),
                FilterType = DevNodeHelper.CM_NOTIFY_FILTER_TYPE_DEVICEINTERFACE,
                ClassGuid = BluetoothRadio.GUID_BTHPORT_DEVICE_INTERFACE,
            };
            int cr = DevNodeHelper.CM_Register_Notification(ref filter, _id, &NotificationShim, out nint raw);
            if (cr == CR_SUCCESS)
            {
                // Same keep-or-release rule as RegisterRadio: a Dispose that ran while the
                // call was in flight found nothing to release, so this one is ours to drop.
                var arrivals = new DevNodeHelper.CmNotifyHandle(raw);
                bool keep;
                lock (_lock)
                {
                    keep = !_disposed;
                    if (keep) _radioArrivals = arrivals;
                }
                if (!keep)
                {
                    arrivals.Dispose();
                    return;
                }
            }
            else
            {
                _logger.LogWarning("Bluetooth radio arrival registration failed (CONFIGRET 0x{Result:X}); radios added later get no link events.", cr);
            }

            foreach (string path in BluetoothRadio.InterfacePaths())
                RegisterRadio(path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Bluetooth link watch failed to start; no Bluetooth link events will be raised.");
        }
    }

    public void Dispose()
    {
        var handles = new List<DevNodeHelper.CmNotifyHandle>();
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            if (_radioArrivals is not null) handles.Add(_radioArrivals);
            handles.AddRange(_radios.Values);
            _radioArrivals = null;
            _radios.Clear();
        }

        // Outside the lock: unregistering waits for in-flight callbacks, and a callback
        // may be waiting on the lock.
        foreach (var handle in handles)
            handle.Dispose();

        s_instances.TryRemove(_id, out _);
    }

    private unsafe void RegisterRadio(string interfacePath)
    {
        lock (_lock)
        {
            if (_disposed || _radios.ContainsKey(interfacePath)) return;
        }

        nint radio = BluetoothRadio.Open(interfacePath);
        if (radio == BluetoothRadio.INVALID_HANDLE_VALUE)
        {
            _logger.LogWarning("Could not open a Bluetooth radio for link events (Win32 error {Error}).", Marshal.GetLastPInvokeError());
            return;
        }

        int cr;
        nint raw;
        try
        {
            var filter = new DevNodeHelper.CM_NOTIFY_FILTER
            {
                cbSize = Marshal.SizeOf<DevNodeHelper.CM_NOTIFY_FILTER>(),
                FilterType = DevNodeHelper.CM_NOTIFY_FILTER_TYPE_DEVICEHANDLE,
                hTarget = radio,
            };
            cr = DevNodeHelper.CM_Register_Notification(ref filter, _id, &NotificationShim, out raw);
        }
        finally
        {
            // The handle only has to outlive the registration call.
            BluetoothRadio.Close(radio);
        }

        if (cr != CR_SUCCESS)
        {
            _logger.LogWarning("Bluetooth radio link registration failed (CONFIGRET 0x{Result:X}).", cr);
            return;
        }

        var registration = new DevNodeHelper.CmNotifyHandle(raw);
        bool keep;
        lock (_lock)
        {
            keep = !_disposed && _radios.TryAdd(interfacePath, registration);
        }

        if (keep)
            _logger.LogDebug("Listening for Bluetooth link events on a radio.");
        else
            registration.Dispose();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int NotificationShim(nint hNotify, nint context, int action, nint eventData, int eventDataSize)
    {
        if (s_instances.TryGetValue(context, out var self))
            self.OnNotification(hNotify, action, eventData, eventDataSize);
        return 0; // ERROR_SUCCESS. Nothing is held open, so a query-remove is never vetoed.
    }

    private unsafe void OnNotification(nint hNotify, int action, nint eventData, int eventDataSize)
    {
        try
        {
            switch (action)
            {
                case DevNodeHelper.CM_NOTIFY_ACTION_DEVICECUSTOMEVENT:
                    if (eventData != 0
                        && BluetoothLinkEvents.TryDecode(new ReadOnlySpan<byte>((void*)eventData, eventDataSize), out var change))
                    {
                        _onLinkChange(change);
                    }
                    break;

                case DevNodeHelper.CM_NOTIFY_ACTION_DEVICEINTERFACEARRIVAL:
                    // Opening and registering is I/O; keep it off the notification thread.
                    if (ReadSymbolicLink(eventData, eventDataSize) is { } path)
                        ThreadPool.QueueUserWorkItem(static s => s.self.RegisterRadio(s.path), (self: this, path), preferLocal: false);
                    break;

                case DevNodeHelper.CM_NOTIFY_ACTION_DEVICEREMOVEPENDING:
                case DevNodeHelper.CM_NOTIFY_ACTION_DEVICEREMOVECOMPLETE:
                    ForgetRadio(hNotify);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error handling a Bluetooth link notification (action {Action}).", action);
        }
    }

    // The radio is going away and its registration with it. CM_Unregister_Notification must
    // not run on the notification thread, so the handle is released from the thread pool.
    private void ForgetRadio(nint hNotify)
    {
        DevNodeHelper.CmNotifyHandle? gone = null;
        lock (_lock)
        {
            string? path = null;
            foreach (var (candidate, handle) in _radios)
            {
                if (handle.DangerousGetHandle() == hNotify)
                {
                    path = candidate;
                    break;
                }
            }
            if (path is not null)
                _radios.Remove(path, out gone);
        }

        if (gone is not null)
            ThreadPool.QueueUserWorkItem(static h => h.Dispose(), gone, preferLocal: false);
    }

    // CM_NOTIFY_EVENT_DATA for an interface event: FilterType @0, Reserved @4,
    // u.DeviceInterface { GUID ClassGuid @8; WCHAR SymbolicLink[] @24 }.
    private static string? ReadSymbolicLink(nint eventData, int eventDataSize)
    {
        const int symbolicLinkOffset = 24;
        if (eventData == 0 || eventDataSize < symbolicLinkOffset + 2) return null;
        return Marshal.PtrToStringUni(eventData + symbolicLinkOffset);
    }
}
