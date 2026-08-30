// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace Periphery.Usb.Linux;

/// <summary>
/// Linux implementation of <see cref="IUsbBackend"/> over <c>libusb-1.0</c>.
/// Opens the usbfs node (<c>/dev/bus/usb/BBB/DDD</c>) resolved from the
/// device's sysfs identity, wraps it with <c>libusb_wrap_sys_device</c>, and
/// runs control / bulk / interrupt transfers through libusb's
/// <b>asynchronous</b> API: each transfer completes on a per-backend event
/// thread, with <c>libusb_cancel_transfer</c> aborting an in-flight transfer
/// when the caller's <see cref="CancellationToken"/> fires — the same
/// completion-driven shape as the Windows backend's IOCP overlapped I/O.
/// </summary>
/// <remarks>
/// Kernel class drivers (usbhid, usb-storage, cdc-acm, …) frequently hold the
/// interfaces of exactly the devices people point raw USB at, so the backend
/// enables <c>libusb_set_auto_detach_kernel_driver</c> — claiming an
/// interface detaches its kernel driver and releasing reattaches it. This is
/// the Linux analogue of binding WinUSB on Windows, minus the .inf ceremony.
/// <para>
/// <see cref="DisposeAsync"/> drains before it releases: it cancels every submitted
/// transfer and waits for libusb to hand each one back — with the event pump still
/// running, since that is the only thing that can deliver a cancellation — before
/// closing the handle or exiting the context, both of which are undefined with
/// transfers pending (#263 item 2).
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
internal sealed class LibUsbBackend : IUsbBackend
{
    private readonly IntPtr _context;
    private readonly IntPtr _handle;
    private readonly int _fd;
    private readonly string _deviceId;
    private readonly Thread _eventThread;
    private readonly Dictionary<byte, UsbTransferType> _endpointTypes;
    private readonly object _claimLock = new();
    private readonly HashSet<byte> _claimed = [];
    private volatile bool _disposed;   // written under _lifetimeGate
    private int _shutdown; // 0 = running, 1 = stop the event pump.

    // Teardown must WAIT for in-flight transfers, not merely start it unwinding
    // (#263 item 2, Linux half — the Windows half landed in #269).
    //
    // _lifetimeGate makes "submit a transfer" and "begin disposing" mutually exclusive.
    // SubmitAsync holds it across the whole alloc-fill-publish-submit window, not just
    // the registration, so a transfer is either refused because teardown began, or it is
    // in _live *and already submitted* and will be cancelled and waited for. There is no
    // window where it is neither. _quiesced is created by DisposeAsync and signalled by
    // the last transfer to leave.
    //
    // Division of labour between the callback and the continuation, both deliberate:
    // the callback frees the native transfer (the only point at which "callback still
    // running" and "transfer being freed" cannot be two different threads), and the
    // continuation deregisters. Deregistering from the continuation keeps the event thread
    // out of _lifetimeGate entirely, so holding that gate across libusb_submit_transfer
    // cannot deadlock against a callback mid-dispatch. Together they put the drain's
    // guarantee in the right place: when the wait completes, every libusb_free_transfer has
    // already run, which is precisely the precondition libusb_close and libusb_exit need.
    //
    // LOCK ORDER is one-way: _lifetimeGate may be held while taking a PendingTransfer.Gate,
    // never the reverse.
    private static readonly TimeSpan QuiesceTimeout = TimeSpan.FromSeconds(2);
    private readonly object _lifetimeGate = new();
    private readonly HashSet<PendingTransfer> _live = new();
    private TaskCompletionSource? _quiesced;
    private Task? _disposal;   // guarded by _lifetimeGate — the single in-progress teardown

    private LibUsbBackend(
        IntPtr context,
        IntPtr handle,
        int fd,
        string deviceId,
        UsbDeviceDescriptor deviceDescriptor,
        UsbConfigurationDescriptor configuration)
    {
        _context = context;
        _handle = handle;
        _fd = fd;
        _deviceId = deviceId;
        DeviceDescriptor = deviceDescriptor;
        Configuration = configuration;

        _endpointTypes = new Dictionary<byte, UsbTransferType>();
        foreach (var iface in configuration.Interfaces)
            foreach (var ep in iface.Endpoints)
                _endpointTypes[ep.EndpointAddress] = ep.TransferType;

        _eventThread = new Thread(EventLoop)
        {
            IsBackground = true,
            Name = "periphery-libusb-events",
        };
        _eventThread.Start();
    }

    public UsbDeviceDescriptor DeviceDescriptor { get; }

    public UsbConfigurationDescriptor Configuration { get; }

    // -----------------------------------------------------------------------
    // Open
    // -----------------------------------------------------------------------

    internal static LibUsbBackend Open(string deviceId)
    {
        string devNode = ResolveDevNode(deviceId);

        int fd = LibUsbInterop.OpenFd(devNode, LibUsbInterop.O_RDWR | LibUsbInterop.O_CLOEXEC);
        if (fd < 0)
        {
            int errno = Marshal.GetLastPInvokeError();
            var inner = new IOException($"open('{devNode}') failed. errno: {errno}");
            throw errno switch
            {
                LibUsbInterop.EACCES or LibUsbInterop.EPERM =>
                    new UsbAccessDeniedException(
                        $"Access denied opening USB device '{deviceId}' ({devNode}). "
                        + "The calling user lacks read/write permission on the usbfs node — "
                        + "add a udev rule (e.g. TAG+=\"uaccess\" or MODE=\"0660\", GROUP=\"plugdev\") "
                        + "or run with elevated privileges.",
                        inner, deviceId),
                LibUsbInterop.ENOENT or LibUsbInterop.ENODEV or LibUsbInterop.ENXIO =>
                    new UsbDeviceNotFoundException(
                        $"USB device '{deviceId}' was not found at {devNode}. "
                        + "It may have been unplugged between enumeration and open.",
                        inner, deviceId),
                _ =>
                    new UsbException(
                        $"Failed to open USB device '{deviceId}' ({devNode}). errno: {errno}",
                        inner, deviceId),
            };
        }

        IntPtr context = IntPtr.Zero;
        IntPtr handle = IntPtr.Zero;
        try
        {
            int rc = LibUsbInterop.Init(out context);
            if (rc != LibUsbInterop.LIBUSB_SUCCESS)
                throw new UsbException(
                    $"libusb_init failed: {LibUsbInterop.ErrorName(rc)}.",
                    new IOException($"libusb_init returned {rc}."), deviceId);

            rc = LibUsbInterop.WrapSysDevice(context, fd, out handle);
            if (rc != LibUsbInterop.LIBUSB_SUCCESS)
                throw MapError(rc, deviceId,
                    $"libusb_wrap_sys_device failed for '{devNode}'");

            // Linux analogue of the WinUSB driver binding: claiming an
            // interface temporarily detaches its kernel class driver.
            _ = LibUsbInterop.SetAutoDetachKernelDriver(handle, 1);

            IntPtr device = LibUsbInterop.GetDevice(handle);

            // afterOpen from here on. The boundary is the HANDLE, not the method: once
            // libusb_wrap_sys_device has returned one, a device that vanishes during the
            // descriptor reads left a device we already had (#272 review turn 3).
            rc = LibUsbInterop.GetDeviceDescriptor(device, out var rawDescriptor);
            if (rc != LibUsbInterop.LIBUSB_SUCCESS)
                throw MapError(rc, deviceId, "reading the device descriptor", afterOpen: true);

            var configuration = ReadConfiguration(device, deviceId);

            // Mirror WinUsb_Initialize, which implicitly claims the first
            // interface: Treehopper and single-function vendor devices expect
            // an open device to be transfer-ready without an explicit claim.
            if (configuration.Interfaces.Length > 0)
            {
                byte first = configuration.Interfaces[0].InterfaceNumber;
                rc = LibUsbInterop.ClaimInterface(handle, first);
                if (rc != LibUsbInterop.LIBUSB_SUCCESS)
                    throw MapError(rc, deviceId,
                        $"claiming interface {first} at open", afterOpen: true);
            }

            var backend = new LibUsbBackend(
                context, handle, fd, deviceId,
                LibUsbDescriptors.ToDeviceDescriptor(rawDescriptor), configuration);

            if (configuration.Interfaces.Length > 0)
                backend._claimed.Add(configuration.Interfaces[0].InterfaceNumber);

            return backend;
        }
        catch
        {
            if (handle != IntPtr.Zero) LibUsbInterop.Close(handle);
            if (context != IntPtr.Zero) LibUsbInterop.Exit(context);
            _ = LibUsbInterop.CloseFd(fd);
            throw;
        }
    }

    private static unsafe UsbConfigurationDescriptor ReadConfiguration(IntPtr device, string deviceId)
    {
        // Prefer the active configuration; fall back to the first defined one
        // for devices enumerated but not yet configured.
        int rc = LibUsbInterop.GetActiveConfigDescriptor(device, out IntPtr configPtr);
        if (rc != LibUsbInterop.LIBUSB_SUCCESS)
            rc = LibUsbInterop.GetConfigDescriptor(device, 0, out configPtr);
        if (rc != LibUsbInterop.LIBUSB_SUCCESS)
            // Always reached with a live handle — Open calls this only after wrapping.
            throw MapError(rc, deviceId, "reading the configuration descriptor", afterOpen: true);

        try
        {
            var config = *(LibUsbInterop.ConfigDescriptor*)configPtr;

            var interfaces = ImmutableArray.CreateBuilder<UsbInterfaceDescriptor>(config.NumInterfaces);
            var ifaceArray = (LibUsbInterop.Interface*)config.Interfaces;
            for (int i = 0; i < config.NumInterfaces; i++)
            {
                var iface = ifaceArray[i];
                if (iface.NumAltSetting <= 0) continue;

                // Alternate setting 0 — the setting a freshly-configured
                // device runs; parity with the Windows backend's
                // WinUsb_QueryInterfaceSettings(0) view.
                var alt = *(LibUsbInterop.InterfaceDescriptor*)iface.AltSetting;

                var endpoints = ImmutableArray.CreateBuilder<UsbEndpointDescriptor>(alt.NumEndpoints);
                var epArray = (LibUsbInterop.EndpointDescriptor*)alt.Endpoint;
                for (int e = 0; e < alt.NumEndpoints; e++)
                {
                    var ep = epArray[e];
                    endpoints.Add(new UsbEndpointDescriptor
                    {
                        EndpointAddress = ep.EndpointAddress,
                        TransferType = (UsbTransferType)(ep.BmAttributes & 0x3),
                        MaxPacketSize = ep.MaxPacketSize,
                        Interval = ep.Interval,
                    });
                }

                interfaces.Add(new UsbInterfaceDescriptor
                {
                    InterfaceNumber = alt.InterfaceNumber,
                    AlternateSetting = alt.AlternateSetting,
                    InterfaceClass = alt.InterfaceClass,
                    InterfaceSubClass = alt.InterfaceSubClass,
                    InterfaceProtocol = alt.InterfaceProtocol,
                    Endpoints = endpoints.ToImmutable(),
                });
            }

            return new UsbConfigurationDescriptor
            {
                ConfigurationValue = config.ConfigurationValue,
                MaxPowerMilliamps = config.MaxPower * 2,
                Interfaces = interfaces.ToImmutable(),
            };
        }
        finally
        {
            LibUsbInterop.FreeConfigDescriptor(configPtr);
        }
    }

    // -----------------------------------------------------------------------
    // Interface claim
    // -----------------------------------------------------------------------

    public void ClaimInterface(byte interfaceNumber)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_claimLock)
        {
            if (_claimed.Contains(interfaceNumber)) return;
            int rc = LibUsbInterop.ClaimInterface(_handle, interfaceNumber);
            if (rc != LibUsbInterop.LIBUSB_SUCCESS)
                throw MapError(rc, _deviceId, $"claiming interface {interfaceNumber}", afterOpen: true);
            _claimed.Add(interfaceNumber);
        }
    }

    public void ReleaseInterface(byte interfaceNumber)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_claimLock)
        {
            if (!_claimed.Remove(interfaceNumber)) return;
            int rc = LibUsbInterop.ReleaseInterface(_handle, interfaceNumber);
            if (rc is not (LibUsbInterop.LIBUSB_SUCCESS or LibUsbInterop.LIBUSB_ERROR_NO_DEVICE))
                throw MapError(rc, _deviceId, $"releasing interface {interfaceNumber}", afterOpen: true);
        }
    }

    // -----------------------------------------------------------------------
    // Transfers
    // -----------------------------------------------------------------------

    public async Task<int> ControlTransferAsync(UsbControlSetup setup, Memory<byte> buffer, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // A control transfer's native buffer is the 8-byte setup packet
        // followed by the data stage.
        int dataLength = buffer.Length;
        IntPtr native = Marshal.AllocHGlobal(LibUsbInterop.LIBUSB_CONTROL_SETUP_SIZE + dataLength);
        try
        {
            unsafe
            {
                var p = (byte*)native;
                p[0] = setup.RequestType;
                p[1] = setup.Request;
                BinaryPrimitives.WriteUInt16LittleEndian(new Span<byte>(p + 2, 2), setup.Value);
                BinaryPrimitives.WriteUInt16LittleEndian(new Span<byte>(p + 4, 2), setup.Index);
                BinaryPrimitives.WriteUInt16LittleEndian(new Span<byte>(p + 6, 2), (ushort)dataLength);

                if (setup.Direction == UsbTransferDirection.HostToDevice && dataLength > 0)
                    buffer.Span.CopyTo(new Span<byte>(p + LibUsbInterop.LIBUSB_CONTROL_SETUP_SIZE, dataLength));
            }

            int actual = await SubmitAsync(
                endpoint: 0,
                type: LibUsbInterop.LIBUSB_TRANSFER_TYPE_CONTROL,
                native,
                LibUsbInterop.LIBUSB_CONTROL_SETUP_SIZE + dataLength,
                ct).ConfigureAwait(false);

            if (setup.Direction == UsbTransferDirection.DeviceToHost && actual > 0)
            {
                unsafe
                {
                    new ReadOnlySpan<byte>(
                        (byte*)native + LibUsbInterop.LIBUSB_CONTROL_SETUP_SIZE,
                        Math.Min(actual, dataLength)).CopyTo(buffer.Span);
                }
            }

            return actual;
        }
        finally
        {
            Marshal.FreeHGlobal(native);
        }
    }

    public async Task<int> BulkReadAsync(byte endpointAddress, Memory<byte> buffer, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        IntPtr native = Marshal.AllocHGlobal(buffer.Length);
        try
        {
            int actual = await SubmitAsync(
                endpointAddress, PipeTransferType(endpointAddress), native, buffer.Length, ct)
                .ConfigureAwait(false);

            unsafe
            {
                new ReadOnlySpan<byte>((void*)native, Math.Min(actual, buffer.Length))
                    .CopyTo(buffer.Span);
            }
            return actual;
        }
        finally
        {
            Marshal.FreeHGlobal(native);
        }
    }

    public async Task<int> BulkWriteAsync(byte endpointAddress, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        IntPtr native = Marshal.AllocHGlobal(Math.Max(data.Length, 1));
        try
        {
            unsafe
            {
                data.Span.CopyTo(new Span<byte>((void*)native, data.Length));
            }
            return await SubmitAsync(
                endpointAddress, PipeTransferType(endpointAddress), native, data.Length, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            Marshal.FreeHGlobal(native);
        }
    }

    /// <summary>
    /// The bulk read/write surface also serves interrupt endpoints (parity
    /// with WinUSB's ReadPipe/WritePipe); pick the URB type from the
    /// endpoint's descriptor so libusb submits the correct kind.
    /// </summary>
    private byte PipeTransferType(byte endpointAddress) =>
        _endpointTypes.TryGetValue(endpointAddress, out var type) && type == UsbTransferType.Interrupt
            ? LibUsbInterop.LIBUSB_TRANSFER_TYPE_INTERRUPT
            : LibUsbInterop.LIBUSB_TRANSFER_TYPE_BULK;

    /// <summary>Tracks one in-flight native transfer through to its callback.</summary>
    private sealed class PendingTransfer
    {
        /// <summary>Guards <see cref="Transfer"/> so a cancel can never race a free.</summary>
        public readonly object Gate = new();

        public readonly TaskCompletionSource<(int Status, int ActualLength)> Completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The live <c>libusb_transfer</c>, or zero once freed.</summary>
        public IntPtr Transfer;
    }

    private async Task<int> SubmitAsync(byte endpoint, byte type, IntPtr nativeBuffer, int length, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var pending = new PendingTransfer();

        // Registration spans the WHOLE alloc-fill-publish-submit window, not just the add.
        // Registering and then submitting outside the gate would leave a transfer that
        // DisposeAsync can snapshot while Transfer is still zero, skip in its cancellation
        // pass, and give up on — after which this thread submits it against a handle
        // teardown is about to close.
        //
        // The hold is short: libusb_submit_transfer queues the URB and returns, it does not
        // wait for completion. It also cannot deadlock, because the event thread never takes
        // this gate — the callback frees the transfer and completes the TCS, both under
        // pending.Gate at most, and the continuation that deregisters runs on a pool thread
        // (the TCS is RunContinuationsAsynchronously).
        //
        // NOTE the two exits from this window. A submitted transfer is freed by its callback;
        // one that never got submitted gets no callback, so the finally below is its only
        // release, and it frees the GCHandle and the native transfer itself.
        lock (_lifetimeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _live.Add(pending);

            IntPtr transfer = IntPtr.Zero;
            GCHandle gcHandle = default;
            bool submitted = false;
            try
            {
                transfer = LibUsbInterop.AllocTransfer(0);
                if (transfer == IntPtr.Zero)
                    throw new UsbException("libusb_alloc_transfer failed (out of memory).", _deviceId);

                gcHandle = GCHandle.Alloc(pending);

                unsafe
                {
                    var t = (LibUsbInterop.Transfer*)transfer;
                    t->DevHandle = _handle;
                    t->Flags = 0;
                    t->Endpoint = endpoint;
                    t->Type = type;
                    t->Timeout = 0; // Deadlines live in UsbDevice's transfer-timeout funnel.
                    t->Status = 0;
                    t->Length = length;
                    t->ActualLength = 0;
                    t->Callback = (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnTransferCompleted;
                    t->UserData = GCHandle.ToIntPtr(gcHandle);
                    t->Buffer = nativeBuffer;
                    t->NumIsoPackets = 0;
                }

                // Published BEFORE submission, so the moment libusb owns this transfer it is
                // already cancellable by DisposeAsync and by the token registration below.
                lock (pending.Gate)
                    pending.Transfer = transfer;

                int rc = LibUsbInterop.SubmitTransfer(transfer);
                if (rc != LibUsbInterop.LIBUSB_SUCCESS)
                    throw MapError(
                        rc, _deviceId, $"a transfer on endpoint 0x{endpoint:X2}", afterOpen: true);

                submitted = true;
            }
            finally
            {
                // Reached because the submit failed or because setup threw — libusb_alloc_transfer
                // can return null and GCHandle.Alloc can throw. Either way no callback is coming,
                // so this is the only chance to free and to leave _live. Miss it and the transfer
                // sits in _live forever: every later dispose burns the full QuiesceTimeout and
                // then declines to release anything.
                if (!submitted)
                {
                    lock (pending.Gate)
                        pending.Transfer = IntPtr.Zero;

                    if (gcHandle.IsAllocated) gcHandle.Free();
                    if (transfer != IntPtr.Zero) LibUsbInterop.FreeTransfer(transfer);

                    // Reentrant on _lifetimeGate, which Monitor allows; going through
                    // Deregister keeps the quiesce signal it may owe in one place.
                    Deregister(pending);
                }
            }
        }

        // Cancellation aborts the in-flight URB; the event thread then runs
        // the completion callback with LIBUSB_TRANSFER_CANCELLED. Cancelling
        // an already-completed (but not yet freed) transfer is a harmless
        // LIBUSB_ERROR_NOT_FOUND.
        CancellationTokenRegistration reg = ct.CanBeCanceled
            ? ct.Register(static state =>
            {
                var p = (PendingTransfer)state!;
                lock (p.Gate)
                {
                    if (p.Transfer != IntPtr.Zero)
                        _ = LibUsbInterop.CancelTransfer(p.Transfer);
                }
            }, pending)
            : default;

        (int status, int actualLength) result;
        try
        {
            result = await pending.Completion.Task.ConfigureAwait(false);
        }
        finally
        {
            // The native transfer is already gone — OnTransferCompleted freed it under
            // pending.Gate before completing the TCS. Cancel-vs-free is mutually exclusive
            // through that same gate, so disposing the registration is no longer an
            // ordering requirement, just tidiness.
            await reg.DisposeAsync().ConfigureAwait(false);

            // Leave _live only now, with the native transfer already freed. That is what
            // makes the drain's completion mean "libusb holds nothing of ours", which is
            // the precondition libusb_close and libusb_exit actually need. Deregistering
            // from here rather than from the callback also keeps the event thread out of
            // _lifetimeGate, which is what makes holding that gate across
            // libusb_submit_transfer safe.
            Deregister(pending);
        }

        // NO_DEVICE already said "the device has been disconnected" here and still threw a
        // generic UsbTransferException — the information was present and the type discarded it
        // (#260).
        return result.status == LibUsbInterop.LIBUSB_TRANSFER_COMPLETED
            ? result.actualLength
            : result.status == LibUsbInterop.LIBUSB_TRANSFER_CANCELLED
                ? throw new OperationCanceledException(ct)
                : throw ClassifyTransferStatus(result.status, endpoint, _deviceId);
    }

    /// <summary>
    /// Drops a finished transfer from the live set and, if teardown is waiting on the
    /// last one, releases it.
    /// </summary>
    private void Deregister(PendingTransfer pending)
    {
        lock (_lifetimeGate)
        {
            if (_live.Remove(pending) && _disposed && _live.Count == 0)
                _quiesced?.TrySetResult();
        }
    }

    [UnmanagedCallersOnly]
    private static unsafe void OnTransferCompleted(IntPtr transfer)
    {
        var t = (LibUsbInterop.Transfer*)transfer;
        var gcHandle = GCHandle.FromIntPtr(t->UserData);
        var pending = (PendingTransfer)gcHandle.Target!;
        gcHandle.Free();

        // Read the outcome off the struct BEFORE freeing it.
        int status = t->Status;
        int actualLength = t->ActualLength;

        // Freed HERE, inside the callback — not from the awaiting continuation.
        //
        // libusb explicitly permits freeing a transfer from within its own callback: it
        // copies everything it still needs (the flags, the device handle) into locals
        // before dispatching, and after the callback returns it touches the struct only
        // if LIBUSB_TRANSFER_FREE_TRANSFER is set, which this backend never sets. Freeing
        // from another thread has no such guarantee — signalling the TCS only queues the
        // continuation, it does not establish that this callback (or libusb's dispatch
        // around it) has finished with the struct, so the continuation could free it out
        // from under both. Doing it here removes the cross-thread free entirely.
        //
        // Under the gate, and zeroed first, so a CancelTransfer from teardown or from the
        // token registration cannot be looking at the pointer as it goes.
        lock (pending.Gate)
        {
            pending.Transfer = IntPtr.Zero;
            LibUsbInterop.FreeTransfer(transfer);
        }

        // Only now is the awaiting caller released — so by the time anything downstream
        // runs, the free has already happened.
        pending.Completion.TrySetResult((status, actualLength));
    }

    // -----------------------------------------------------------------------
    // Event pump
    // -----------------------------------------------------------------------

    private unsafe void EventLoop()
    {
        // One pump per backend: the dedicated context only ever carries this
        // device's transfers. The 1 s slice is a liveness backstop; shutdown
        // is normally instant via libusb_interrupt_event_handler.
        var slice = new LibUsbInterop.Timeval { Seconds = 1, Microseconds = 0 };
        while (Volatile.Read(ref _shutdown) == 0)
        {
            _ = LibUsbInterop.HandleEventsTimeoutCompleted(_context, &slice, null);
        }
    }

    // -----------------------------------------------------------------------
    // Disposal
    // -----------------------------------------------------------------------

    public ValueTask DisposeAsync()
    {
        PendingTransfer[] outstanding;
        Task quiesced;
        TaskCompletionSource disposal;

        lock (_lifetimeGate)
        {
            // Later callers await the FIRST caller's teardown instead of racing past it.
            if (_disposal is not null)
                return new ValueTask(_disposal);

            _disposed = true;   // from here, SubmitAsync refuses to register

            // Every transfer still in _live has been submitted and not yet freed: SubmitAsync
            // publishes Transfer and submits under this same lock, and only removes itself
            // once libusb_free_transfer has run.
            outstanding = _live.ToArray();
            if (outstanding.Length == 0)
            {
                quiesced = Task.CompletedTask;
            }
            else
            {
                _quiesced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                quiesced = _quiesced.Task;
            }

            disposal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _disposal = disposal.Task;
        }

        // Task.Run, not a bare call. With no live transfers the quiesce wait completes
        // synchronously, and the teardown that follows it blocks: _eventThread.Join can sit
        // for up to QuiesceTimeout. Running it inline would put that block on the stack of
        // whoever awaited DisposeAsync — a UI or request thread, for an API whose whole
        // point is not to do that.
        _ = Task.Run(() => QuiesceThenReleaseAsync(outstanding, quiesced, disposal));
        return new ValueTask(disposal.Task);
    }

    /// <summary>
    /// Cancels <paramref name="outstanding"/>, waits (boundedly) for libusb to hand every one
    /// of them back, and only then releases the interfaces, the event pump, the device handle,
    /// the context, and the fd. Runs exactly once; every <see cref="DisposeAsync"/> caller
    /// observes it through <paramref name="disposal"/>.
    /// </summary>
    private async Task QuiesceThenReleaseAsync(
        PendingTransfer[] outstanding, Task quiesced, TaskCompletionSource disposal)
    {
        try
        {
            // Cancel each submitted URB. The completion callback then fires with
            // LIBUSB_TRANSFER_CANCELLED — which only happens because the event pump is still
            // running. Stopping it first, as this method used to, is what stranded the
            // awaiting task forever.
            foreach (var pending in outstanding)
            {
                lock (pending.Gate)
                {
                    if (pending.Transfer != IntPtr.Zero)
                        _ = LibUsbInterop.CancelTransfer(pending.Transfer);
                }
            }

            // THE POINT OF THIS METHOD. libusb_close is documented as undefined behaviour with
            // transfers still pending, and libusb_exit tears down the context those transfers
            // live in. The previous code did neither wait nor cancel: it set _shutdown, joined
            // the event thread, and closed — after which no libusb_handle_events would ever run
            // again, so the callback could not fire, PendingTransfer.Completion never completed,
            // and the caller awaiting that transfer hung for the life of the process. (The
            // transfer watchdog in UsbDevice does not rescue it: that timeout cancels the token,
            // and delivering a cancellation is exactly what needs the pump.)
            bool drained = quiesced.IsCompleted
                || await Task.WhenAny(quiesced, Task.Delay(QuiesceTimeout)).ConfigureAwait(false) == quiesced;

            if (!drained)
            {
                // Release NOTHING — and above all, leave the event pump running.
                //
                // libusb_cancel_transfer only *requests* cancellation; reaching this bound means
                // at least one URB is, as far as anything here can tell, still live, and libusb
                // still owns its transfer struct and its buffer. Closing the handle or exiting
                // the context under that is the undefined behaviour this change exists to
                // remove, and stopping the pump would guarantee the stranded caller never
                // unwinds. Leaving everything alive is the one option that keeps the late
                // completion able to arrive and free itself.
                //
                // The cost is a context, a device handle, an fd and a background pump thread per
                // stranded teardown, for the life of the process. The counter makes the choice
                // visible rather than silent, and it should read flat zero.
                UsbMeters.TeardownNotQuiescedTotal.Add(1);
                disposal.TrySetResult();
                return;
            }

            // Drained: libusb holds nothing of ours, so the ordinary reverse-open teardown is
            // safe. Release claims first (reattaches kernel drivers) — legal now precisely
            // because no transfer is outstanding on those endpoints.
            lock (_claimLock)
            {
                foreach (byte iface in _claimed)
                    _ = LibUsbInterop.ReleaseInterface(_handle, iface);
                _claimed.Clear();
            }

            Volatile.Write(ref _shutdown, 1);
            LibUsbInterop.InterruptEventHandler(_context);

            // Bounded, for the same reason every other wait on this path is: a teardown that
            // hangs forever takes the reconnect path down with it. A pump that will not exit
            // leaves the context in use, so this path releases nothing either.
            if (!_eventThread.Join(QuiesceTimeout))
            {
                UsbMeters.TeardownNotQuiescedTotal.Add(1);
                disposal.TrySetResult();
                return;
            }

            LibUsbInterop.Close(_handle);
            LibUsbInterop.Exit(_context);
            _ = LibUsbInterop.CloseFd(_fd);

            disposal.TrySetResult();
        }
        catch (Exception ex)
        {
            // Nothing awaits this task directly — callers observe it through `disposal` — so a
            // fault has to be handed over rather than left to the unobserved-exception path.
            disposal.TrySetException(ex);
        }
    }

    // -----------------------------------------------------------------------
    // Identity resolution
    // -----------------------------------------------------------------------

    /// <summary>
    /// Resolves an enumeration identity into the openable usbfs node.
    /// Periphery's Linux provider surfaces sysfs paths as
    /// <see cref="Periphery.DeviceInfo.Id"/>; the path may name the USB
    /// device itself or one of its interfaces (<c>1-4:1.0</c>), so the walk
    /// ascends until a directory carrying <c>busnum</c>/<c>devnum</c>
    /// attributes appears. Paths already under <c>/dev/bus/usb/</c> pass
    /// through unchanged.
    /// </summary>
    internal static string ResolveDevNode(string deviceId)
    {
        if (deviceId.StartsWith("/dev/bus/usb/", StringComparison.Ordinal))
            return deviceId;

        // Parity with Windows, where an unresolvable identity surfaces as
        // device-not-found out of the open call rather than a generic error.
        if (!deviceId.StartsWith("/sys/", StringComparison.Ordinal))
            throw new UsbDeviceNotFoundException(
                $"USB device '{deviceId}' was not found — the identity is neither a "
                + "sysfs path nor a /dev/bus/usb/BBB/DDD node.",
                new IOException($"Unrecognized USB device identity: {deviceId}"), deviceId);

        if (!Directory.Exists(deviceId))
            throw new UsbDeviceNotFoundException(
                $"USB device '{deviceId}' was not found. "
                + "It may have been unplugged between enumeration and open.",
                new IOException($"sysfs path does not exist: {deviceId}"), deviceId);

        string? current = deviceId.TrimEnd('/');
        for (int depth = 0; depth < 8 && current is not null; depth++)
        {
            string busnumPath = current + "/busnum";
            string devnumPath = current + "/devnum";
            if (File.Exists(busnumPath) && File.Exists(devnumPath)
                && TryReadSysfsInt(busnumPath, out int busnum)
                && TryReadSysfsInt(devnumPath, out int devnum))
            {
                return string.Create(CultureInfo.InvariantCulture,
                    $"/dev/bus/usb/{busnum:D3}/{devnum:D3}");
            }

            current = Path.GetDirectoryName(current)?.Replace('\\', '/');
            if (current is null || current.Length <= "/sys".Length)
                break;
        }

        throw new UsbException(
            $"Could not resolve a usbfs node for '{deviceId}' — no ancestor carries "
            + "busnum/devnum attributes. The identity may not be a USB device.", deviceId);
    }

    private static bool TryReadSysfsInt(string path, out int value)
    {
        try
        {
            value = int.Parse(File.ReadAllText(path).Trim(), CultureInfo.InvariantCulture);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            value = 0;
            return false;
        }
    }

    // -----------------------------------------------------------------------
    // Error mapping
    // -----------------------------------------------------------------------

    /// <param name="afterOpen">
    /// Whether a working handle had already been established. It decides what
    /// <c>LIBUSB_ERROR_NO_DEVICE</c> means: before the handle exists the device was never
    /// there (<see cref="UsbDeviceNotFoundException"/>); after it, one we owned left the bus
    /// (<see cref="UsbDeviceRemovedException"/>).
    /// <para>
    /// The boundary is <c>libusb_wrap_sys_device</c> returning a handle, NOT the end of
    /// <see cref="Open"/> — the descriptor and configuration reads run after it, and a device
    /// that vanishes during those left one we already had.
    /// </para>
    /// </param>
    /// <summary>
    /// Classifies a completed-but-failed libusb transfer. libusb reports removal and stall as
    /// distinct statuses, so unlike Win32 there is nothing ambiguous to resolve here.
    /// </summary>
    /// <remarks>
    /// Lives on the Linux backend, beside <see cref="MapError"/>, rather than in a shared
    /// platform-neutral classifier: the mapping is libusb's semantics, and a neutral type that
    /// reaches into <c>LibUsbInterop</c> for them inverts the layering — it even needed a
    /// CA1416 suppression to say so out loud (#272 review turn 3). Both backends still produce
    /// the same exception vocabulary, which is what actually needed to be shared.
    /// <para>
    /// Internal rather than private because it is pure, and therefore the one part of this
    /// class that can be tested without libusb.
    /// </para>
    /// </remarks>
    internal static UsbException ClassifyTransferStatus(int status, byte endpoint, string? deviceId)
    {
        var inner = new IOException($"libusb transfer status {status}.");

        return status switch
        {
            LibUsbInterop.LIBUSB_TRANSFER_NO_DEVICE =>
                new UsbDeviceRemovedException(
                    $"Transfer on endpoint 0x{endpoint:X2} failed — the device left the USB bus "
                    + "mid-transfer (LIBUSB_TRANSFER_NO_DEVICE).", inner, deviceId),

            LibUsbInterop.LIBUSB_TRANSFER_STALL =>
                new UsbTransferException(
                    $"Transfer on endpoint 0x{endpoint:X2} stalled — the device rejected the "
                    + "request (LIBUSB_TRANSFER_STALL). The device is still on the bus.",
                    inner, deviceId),

            LibUsbInterop.LIBUSB_TRANSFER_OVERFLOW =>
                new UsbTransferException(
                    $"Transfer on endpoint 0x{endpoint:X2} overflowed the supplied buffer "
                    + "(LIBUSB_TRANSFER_OVERFLOW).", inner, deviceId),

            _ => new UsbTransferException(
                $"Transfer on endpoint 0x{endpoint:X2} failed (libusb status {status}).",
                inner, deviceId),
        };
    }

    /// <remarks>
    /// Internal rather than private so the phase judgement can be tested without libusb: it is
    /// pure, and it is the only part of this class that is (#272 review turn 1).
    /// </remarks>
    internal static UsbException MapError(int rc, string deviceId, string operation, bool afterOpen = false)
    {
        var inner = new IOException($"{operation}: {LibUsbInterop.ErrorName(rc)} ({rc}).");
        return rc switch
        {
            LibUsbInterop.LIBUSB_ERROR_ACCESS or LibUsbInterop.LIBUSB_ERROR_BUSY =>
                new UsbAccessDeniedException(
                    $"Access denied: {operation} for '{deviceId}' "
                    + $"({LibUsbInterop.ErrorName(rc)}). Another driver or process may hold "
                    + "the interface, or the usbfs node permissions are insufficient.",
                    inner, deviceId),
            // NOT a removal. MapError serves the OPEN path too — libusb_wrap_sys_device, the
            // descriptor reads, ReadConfiguration — where NO_DEVICE means the device was not
            // there to begin with. Post-open callers that can distinguish say so themselves by
            // passing afterOpen (#272 review turn 1).
            LibUsbInterop.LIBUSB_ERROR_NO_DEVICE when !afterOpen =>
                new UsbDeviceNotFoundException(
                    $"USB device '{deviceId}' is gone — it was unplugged before the handle "
                    + "was established.", inner, deviceId),

            LibUsbInterop.LIBUSB_ERROR_NO_DEVICE =>
                new UsbDeviceRemovedException(
                    $"USB device '{deviceId}' left the USB bus during {operation}.",
                    inner, deviceId),
            _ =>
                new UsbException(
                    $"{operation} for '{deviceId}': {LibUsbInterop.ErrorName(rc)}.",
                    inner, deviceId),
        };
    }
}
