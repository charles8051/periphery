// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.Immutable;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Periphery.Usb.Windows;

/// <summary>
/// Windows implementation of <see cref="IUsbBackend"/> over <c>winusb.dll</c>.
/// Opens the device-interface handle (overlapped), initialises WinUSB (which
/// claims the first interface), reads the device + configuration descriptors,
/// and runs control / bulk transfers as <b>true overlapped (asynchronous) I/O</b>:
/// the handle is bound to the thread-pool I/O completion port and each transfer
/// completes on an IOCP callback, with <c>CancelIoEx</c> aborting an in-flight
/// transfer when the caller's <see cref="CancellationToken"/> fires.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WinUsbBackend : IUsbBackend
{
    // Win32 error codes used for exception classification.
    private const int ERROR_FILE_NOT_FOUND = 2;
    private const int ERROR_PATH_NOT_FOUND = 3;
    private const int ERROR_ACCESS_DENIED = 5;
    private const int ERROR_DEVICE_NOT_CONNECTED = 1167;
    private const int ERROR_BAD_COMMAND = 22;
    private const int ERROR_GEN_FAILURE = 31;
    private const int ERROR_NO_SUCH_DEVICE = 433;

    /// <summary>
    /// How long <see cref="DisposeAsync"/> waits for aborted transfers to report back
    /// before giving up on them. Generous: a CancelIoEx'd transfer completes in
    /// microseconds, so reaching this bound means a packet is not coming at all.
    /// </summary>
    private static readonly TimeSpan QuiesceTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Issues a single overlapped WinUSB transfer over the pinned buffer.</summary>
    private delegate bool WinUsbOverlappedCall(nint buffer, uint length, nint overlapped);

    private readonly SafeFileHandle _deviceHandle;
    private readonly ThreadPoolBoundHandle _boundHandle;
    private readonly string _devicePath;
    private nint _winUsbHandle;

    // Teardown must WAIT for in-flight I/O, not merely start it unwinding (#263 item 2).
    //
    // _lifetimeGate makes "issue a new transfer" and "begin disposing" mutually
    // exclusive. RunOverlappedAsync holds it across the WHOLE allocate-publish-issue
    // window, not just the registration, so a transfer is either refused because
    // teardown began, or it is in _live *and already native* and will be cancelled and
    // waited for. There is no window where it is neither. _live holds every transfer
    // whose IOCP callback has not yet run; _quiesced is created by DisposeAsync and
    // signalled by the last callback to leave.
    //
    // LOCK ORDER is one-way: _lifetimeGate may be held while taking an IoState.Gate,
    // never the reverse. The issuing path relies on that — it takes state.Gate inside
    // _lifetimeGate to publish the overlapped pointer and to undo a failed setup. The
    // completion callback is the path that could invert it, so it takes state.Gate and
    // _lifetimeGate strictly one after the other, never nested: it always releases
    // state.Gate without waiting on anything, so a holder of _lifetimeGate can never be
    // blocked behind it.
    private readonly object _lifetimeGate = new();
    private readonly HashSet<IoState> _live = new();
    private TaskCompletionSource? _quiesced;
    private bool _disposed;    // guarded by _lifetimeGate
    private Task? _disposal;   // guarded by _lifetimeGate — the single in-progress teardown

    private WinUsbBackend(
        SafeFileHandle deviceHandle,
        ThreadPoolBoundHandle boundHandle,
        nint winUsbHandle,
        string devicePath,
        UsbDeviceDescriptor deviceDescriptor,
        UsbConfigurationDescriptor configuration)
    {
        _deviceHandle = deviceHandle;
        _boundHandle = boundHandle;
        _winUsbHandle = winUsbHandle;
        _devicePath = devicePath;
        DeviceDescriptor = deviceDescriptor;
        Configuration = configuration;
    }

    public UsbDeviceDescriptor DeviceDescriptor { get; }

    public UsbConfigurationDescriptor Configuration { get; }

    /// <summary>
    /// Opens the WinUSB device-interface identified by <paramref name="deviceId"/>
    /// (a SetupAPI device-instance ID, or an already-resolved <c>\\?\</c> path).
    /// </summary>
    internal static WinUsbBackend Open(string deviceId)
    {
        var devicePath = ResolveInterfacePath(deviceId);

        var deviceHandle = WinUsbInterop.CreateFile(
            devicePath,
            WinUsbInterop.GENERIC_READ | WinUsbInterop.GENERIC_WRITE,
            WinUsbInterop.FILE_SHARE_READ | WinUsbInterop.FILE_SHARE_WRITE,
            nint.Zero,
            WinUsbInterop.OPEN_EXISTING,
            WinUsbInterop.FILE_FLAG_OVERLAPPED,
            nint.Zero);

        if (deviceHandle.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            deviceHandle.Dispose();
            throw MapOpenError(error, deviceId, devicePath);
        }

        if (!WinUsbInterop.WinUsb_Initialize(deviceHandle, out nint winUsbHandle))
        {
            int error = Marshal.GetLastPInvokeError();
            deviceHandle.Dispose();
            var inner = new IOException($"WinUsb_Initialize failed for '{devicePath}'. Win32 error: {error}.");
            throw error == ERROR_ACCESS_DENIED
                ? new UsbAccessDeniedException(
                    "WinUsb_Initialize was denied — the device is not bound to the WinUSB driver " +
                    "(it may be claimed by a class driver, or need a WinUSB .inf / Zadig binding).",
                    inner, deviceId)
                : new UsbException($"WinUsb_Initialize failed for '{deviceId}'. Win32 error: {error}.", inner, deviceId);
        }

        ThreadPoolBoundHandle? boundHandle = null;
        try
        {
            // Bind the file handle to the thread-pool IOCP so overlapped transfers
            // complete on a pool thread without a dedicated waiter.
            boundHandle = ThreadPoolBoundHandle.BindHandle(deviceHandle);

            var deviceDescriptor = ReadDeviceDescriptor(winUsbHandle, deviceId);
            var configuration = ReadConfiguration(winUsbHandle, deviceId);
            return new WinUsbBackend(deviceHandle, boundHandle, winUsbHandle, devicePath, deviceDescriptor, configuration);
        }
        catch
        {
            boundHandle?.Dispose();
            WinUsbInterop.WinUsb_Free(winUsbHandle);
            deviceHandle.Dispose();
            throw;
        }
    }

    public void ClaimInterface(byte interfaceNumber)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // WinUsb_Initialize implicitly claims the first interface. Additional
        // interfaces (WinUsb_GetAssociatedInterface) are a follow-up.
        if (interfaceNumber != 0)
            throw new NotSupportedException(
                $"This WinUSB spike supports interface 0 only (claimed implicitly at open); " +
                $"interface {interfaceNumber} requires WinUsb_GetAssociatedInterface, which is not yet wired up.");
    }

    public void ReleaseInterface(byte interfaceNumber)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // Interface 0 is released on dispose (WinUsb_Free); nothing to do here in the spike.
    }

    public async Task<int> ControlTransferAsync(UsbControlSetup setup, Memory<byte> buffer, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var packet = new WinUsbInterop.WINUSB_SETUP_PACKET
        {
            RequestType = setup.RequestType,
            Request = setup.Request,
            Value = setup.Value,
            Index = setup.Index,
            Length = (ushort)buffer.Length,
        };

        var scratch = new byte[buffer.Length];
        buffer.Span.CopyTo(scratch); // OUT (host→device) data stage

        int transferred = await RunOverlappedAsync(
            scratch,
            (buf, len, ov) => WinUsbInterop.WinUsb_ControlTransfer(_winUsbHandle, packet, buf, len, out _, ov),
            "control transfer",
            $"bmRequestType=0x{setup.RequestType:X2} bRequest=0x{setup.Request:X2}",
            ct).ConfigureAwait(false);

        new ReadOnlySpan<byte>(scratch, 0, transferred).CopyTo(buffer.Span); // IN (device→host) data stage
        return transferred;
    }

    public async Task<int> BulkReadAsync(byte endpointAddress, Memory<byte> buffer, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var scratch = new byte[buffer.Length];
        int transferred = await RunOverlappedAsync(
            scratch,
            (buf, len, ov) => WinUsbInterop.WinUsb_ReadPipe(_winUsbHandle, endpointAddress, buf, len, out _, ov),
            "bulk read",
            $"endpoint=0x{endpointAddress:X2}",
            ct).ConfigureAwait(false);

        new ReadOnlySpan<byte>(scratch, 0, transferred).CopyTo(buffer.Span);
        return transferred;
    }

    public Task<int> BulkWriteAsync(byte endpointAddress, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var scratch = data.ToArray();
        return RunOverlappedAsync(
            scratch,
            (buf, len, ov) => WinUsbInterop.WinUsb_WritePipe(_winUsbHandle, endpointAddress, buf, len, out _, ov),
            "bulk write",
            $"endpoint=0x{endpointAddress:X2}",
            ct);
    }

    public ValueTask DisposeAsync()
    {
        IoState[] outstanding;
        Task quiesced;
        TaskCompletionSource disposal;

        lock (_lifetimeGate)
        {
            // Later callers await the FIRST caller's teardown instead of racing past it.
            // Returning on _disposed alone would let a second caller's await complete
            // while the first is still inside the quiesce wait with nothing yet freed —
            // weaker than the synchronous dispose this replaced, which held every caller
            // until the handles were actually gone.
            if (_disposal is not null)
                return new ValueTask(_disposal);

            _disposed = true;   // from here, RunOverlappedAsync refuses to register

            // Every state still in _live is already native: the issuing path publishes
            // POverlapped and issues the transfer while holding this same lock, so this
            // snapshot cannot catch a half-set-up transfer that the cancellation pass
            // would then skip.
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

        _ = QuiesceThenReleaseAsync(outstanding, quiesced, disposal);
        return new ValueTask(disposal.Task);
    }

    /// <summary>
    /// Aborts <paramref name="outstanding"/>, waits (boundedly) for their IOCP callbacks to
    /// report back, and only then releases the native resources those callbacks touch. Runs
    /// exactly once per backend; every <see cref="DisposeAsync"/> caller observes it through
    /// <paramref name="disposal"/>.
    /// </summary>
    private async Task QuiesceThenReleaseAsync(
        IoState[] outstanding, Task quiesced, TaskCompletionSource disposal)
    {
        try
        {
            // Abort each pending transfer. Deliberately OUTSIDE _lifetimeGate: nothing here
            // needs it, and the completion callback takes it to deregister, so leaving it free
            // keeps a callback that fires mid-cancel from queueing behind us for no reason.
            foreach (var state in outstanding)
            {
                lock (state.Gate)
                {
                    if (state.POverlapped != nint.Zero)
                        WinUsbInterop.CancelIoEx(state.Handle, state.POverlapped);
                }
            }

            // THE POINT OF THIS METHOD. Every aborted transfer still has an IOCP completion
            // packet coming, and that callback dereferences _boundHandle to free its
            // NativeOverlapped. The previous code freed the WinUSB handle and disposed the
            // bound handle immediately, on the strength of a comment asserting the callbacks
            // "then complete ... before the bound handle is torn down" — an ordering nothing
            // established. Awaiting it is what makes that sentence true.
            bool drained = quiesced.IsCompleted
                || await Task.WhenAny(quiesced, Task.Delay(QuiesceTimeout)).ConfigureAwait(false) == quiesced;

            // The wait is BOUNDED, and on expiry this method releases NOTHING — not the
            // WinUSB interface handle, not the device handle, not the bound handle.
            //
            // CancelIoEx only *requests* cancellation. It does not promise the operation has
            // stopped, and it does not promise its completion packet has been delivered. So
            // reaching this bound means at least one transfer is, as far as anything here can
            // tell, still live — and a live WinUSB transfer touches all three: the driver may
            // still be working through the interface handle, the I/O manager still owns an IRP
            // against the device handle, and the completion callback still dereferences the
            // bound handle to free its NativeOverlapped. Releasing any one of them is the
            // use-after-free this change exists to remove; there is no subset that is safe.
            //
            // An unbounded wait is not the alternative. It would trade the use-after-free for
            // a teardown that hangs forever if a completion packet never arrives (a driver
            // that loses one, a CancelIoEx that does not take), which takes the whole
            // reconnect path down with it. So we do proceed — and proceed by letting go of
            // nothing. The cost is one pinned allocation and two native handles per stranded
            // transfer, for the life of the process, against an access violation on a
            // thread-pool thread. The counter makes the choice visible rather than silent.
            if (!drained)
            {
                // Leaving the SafeFileHandle undisposed is not enough on its own: its
                // finalizer would close the OS handle later anyway, at a moment even less
                // predictable than this one. SetHandleAsInvalid drops ownership and
                // suppresses finalization, so the handle is leaked deliberately rather than
                // closed behind our back.
                _deviceHandle.SetHandleAsInvalid();

                UsbMeters.TeardownNotQuiescedTotal.Add(1);
                disposal.TrySetResult();
                return;
            }

            // Drained: every transfer has reported back, so nothing native is outstanding and
            // the ordinary teardown order applies (WinUsb_Free before closing the handle it
            // was initialised from, bound handle last).
            if (_winUsbHandle != nint.Zero)
            {
                WinUsbInterop.WinUsb_Free(_winUsbHandle);
                _winUsbHandle = nint.Zero;
            }

            _deviceHandle.Dispose();
            _boundHandle.Dispose();

            disposal.TrySetResult();
        }
        catch (Exception ex)
        {
            // Nothing awaits this task directly — callers observe it through `disposal` —
            // so a fault has to be handed over rather than left to the unobserved-exception
            // path that #259 was about.
            disposal.TrySetException(ex);
        }
    }

    // -----------------------------------------------------------------------
    // Overlapped transfer core
    // -----------------------------------------------------------------------

    /// <summary>
    /// Coordinates one overlapped transfer end-to-end: pins <paramref name="buffer"/>,
    /// issues <paramref name="call"/>, and completes the returned task from the IOCP
    /// callback. <see cref="WinUsbInterop.CancelIoEx"/> is registered against
    /// <paramref name="ct"/>; the per-transfer <c>IoState.Gate</c> makes cancel-vs-complete
    /// mutually exclusive so the <c>NativeOverlapped</c> is never used after it is freed,
    /// and <c>_lifetimeGate</c> is held across allocation, publication, and issuance so
    /// teardown cannot free the handles this call is about to use.
    /// </summary>
    private unsafe Task<int> RunOverlappedAsync(
        byte[] buffer, WinUsbOverlappedCall call, string op, string detail, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return Task.FromCanceled<int>(ct);

        var state = new IoState(_deviceHandle);

        // Registration spans the WHOLE allocate-publish-issue window, not just the add.
        //
        // Holding _lifetimeGate across `_live.Add` alone left a transfer registered but not
        // yet native, and DisposeAsync would then snapshot it with POverlapped still zero,
        // skip it in the cancellation pass, wait out QuiesceTimeout on a transfer nobody had
        // cancelled, and free _winUsbHandle / _deviceHandle — after which this thread would
        // resume and allocate and issue against all three. Covering issuance too makes the
        // snapshot mean what the drain assumes: everything in _live is already native, and
        // therefore already cancellable.
        //
        // The hold is short and cannot deadlock. WinUsb_ReadPipe / WinUsb_WritePipe on an
        // overlapped handle return ERROR_IO_PENDING immediately, and completion arrives on a
        // thread-pool thread through the IOCP (nothing here sets
        // SKIP_COMPLETION_PORT_ON_SUCCESS), never inline on this one. A callback that fires
        // while we still hold the gate simply waits to deregister — it never waits on us for
        // anything else, and it releases state.Gate before asking for _lifetimeGate, so the
        // one-way lock order holds.
        lock (_lifetimeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _live.Add(state);

            NativeOverlapped* pOverlapped = null;
            bool issued = false;
            try
            {
                pOverlapped = _boundHandle.AllocateNativeOverlapped(
                    (uint errorCode, uint numBytes, NativeOverlapped* np) =>
                    {
                        lock (state.Gate)
                        {
                            state.POverlapped = nint.Zero; // bar any pending CancelIoEx from touching it
                            _boundHandle.FreeNativeOverlapped(np);
                        }

                        // Sequential with the block above, never nested inside it. The lock
                        // order is _lifetimeGate → IoState.Gate; taking them the other way
                        // round here — while the issuing thread holds _lifetimeGate and wants
                        // this state.Gate — would be the classic inversion.
                        Deregister(state);

                        switch (errorCode)
                        {
                            case 0:
                                state.Tcs.TrySetResult((int)numBytes);
                                break;
                            case WinUsbInterop.ERROR_OPERATION_ABORTED:
                                state.Tcs.TrySetCanceled();
                                break;
                            default:
                                state.Tcs.TrySetException(TransferError(op, detail, (int)errorCode));
                                break;
                        }
                    },
                    state: null,
                    pinData: buffer);

                lock (state.Gate)
                    state.POverlapped = (nint)pOverlapped;

                bool ok;
                int err = 0;
                fixed (byte* pBuffer = buffer)
                {
                    ok = call((nint)pBuffer, (uint)buffer.Length, (nint)pOverlapped);
                    if (!ok) err = Marshal.GetLastPInvokeError();
                }

                if (!ok && err != WinUsbInterop.ERROR_IO_PENDING)
                    throw TransferError(op, detail, err);

                issued = true;
            }
            finally
            {
                // Reached either because the call failed synchronously or because setup threw
                // — AllocateNativeOverlapped and the interop delegate can both throw rather
                // than return false. Either way no completion packet is coming, so this is the
                // only chance to free the overlapped and leave _live. Miss it and the state
                // sits in _live forever: every later DisposeAsync burns the full
                // QuiesceTimeout waiting on a transfer that will never report back, and then
                // leaks the bound handle on the timeout path.
                if (!issued)
                {
                    lock (state.Gate)
                    {
                        if (state.POverlapped != nint.Zero)
                        {
                            state.POverlapped = nint.Zero;
                            _boundHandle.FreeNativeOverlapped(pOverlapped);
                        }
                    }

                    // Reentrant on _lifetimeGate, which Monitor allows; going through
                    // Deregister keeps "leave the live set" — and the quiesce signal it may
                    // owe — in one place.
                    Deregister(state);
                }
            }
        }

        // Pending (or inline-completed; the completion packet still fires). Wire up
        // cancellation; the cancel callback no-ops once POverlapped has been cleared.
        if (ct.CanBeCanceled)
        {
            var reg = ct.Register(static s =>
            {
                var st = (IoState)s!;
                lock (st.Gate)
                {
                    if (st.POverlapped != nint.Zero)
                        WinUsbInterop.CancelIoEx(st.Handle, st.POverlapped);
                }
            }, state);

            return AwaitAndUnregisterAsync(state.Tcs.Task, reg);
        }

        return state.Tcs.Task;
    }

    private static async Task<int> AwaitAndUnregisterAsync(Task<int> task, CancellationTokenRegistration reg)
    {
        try { return await task.ConfigureAwait(false); }
        finally { reg.Dispose(); }
    }

    /// <summary>
    /// Drops a finished transfer from the live set and, if teardown is waiting on the
    /// last one, releases it.
    /// </summary>
    private void Deregister(IoState state)
    {
        lock (_lifetimeGate)
        {
            if (_live.Remove(state) && _disposed && _live.Count == 0)
                _quiesced?.TrySetResult();
        }
    }

    /// <summary>Per-transfer coordination state shared between the issuing thread,
    /// the IOCP completion callback, and the cancellation callback.</summary>
    private sealed class IoState(SafeFileHandle handle)
    {
        public readonly object Gate = new();
        public readonly SafeFileHandle Handle = handle;
        public readonly TaskCompletionSource<int> Tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The live NativeOverlapped pointer, or zero once completed / freed.</summary>
        public nint POverlapped;
    }

    // -----------------------------------------------------------------------
    // Descriptor reads (synchronous — performed once at open)
    // -----------------------------------------------------------------------

    private static UsbDeviceDescriptor ReadDeviceDescriptor(nint winUsbHandle, string deviceId)
    {
        // Shell: fetch the raw descriptor bytes. The byte-level decode lives in
        // the pure UsbDescriptors.ParseDeviceDescriptor (golden-tested off-device).
        var buffer = new byte[UsbDescriptors.DeviceDescriptorLength];
        if (!WinUsbInterop.WinUsb_GetDescriptor(
                winUsbHandle, WinUsbInterop.USB_DEVICE_DESCRIPTOR_TYPE, 0, 0, buffer, (uint)buffer.Length, out uint len)
            || len < UsbDescriptors.DeviceDescriptorLength)
        {
            int error = Marshal.GetLastPInvokeError();
            throw new UsbException(
                $"Failed to read the USB device descriptor for '{deviceId}'. Win32 error: {error}.",
                new IOException($"WinUsb_GetDescriptor(DEVICE) returned {len} bytes, Win32 error {error}."),
                deviceId);
        }

        return UsbDescriptors.ParseDeviceDescriptor(buffer);
    }

    private static UsbConfigurationDescriptor ReadConfiguration(nint winUsbHandle, string deviceId)
    {
        byte configurationValue = 0;
        int maxPowerMilliamps = 0;

        // Shell: fetch the 9-byte header; the scalar-field decode is pure
        // (UsbDescriptors.ParseConfigurationHeader). Interface/endpoint
        // enumeration still comes from the WinUSB query surface below.
        var header = new byte[UsbDescriptors.ConfigurationHeaderLength];
        if (WinUsbInterop.WinUsb_GetDescriptor(
                winUsbHandle, WinUsbInterop.USB_CONFIGURATION_DESCRIPTOR_TYPE, 0, 0, header, (uint)header.Length, out uint hlen)
            && hlen >= UsbDescriptors.ConfigurationHeaderLength)
        {
            var parsed = UsbDescriptors.ParseConfigurationHeader(header);
            configurationValue = parsed.ConfigurationValue;
            maxPowerMilliamps = parsed.MaxPowerMilliamps;
        }

        var interfaces = ImmutableArray<UsbInterfaceDescriptor>.Empty;
        if (WinUsbInterop.WinUsb_QueryInterfaceSettings(winUsbHandle, 0, out var iface))
        {
            var endpoints = ImmutableArray.CreateBuilder<UsbEndpointDescriptor>(iface.bNumEndpoints);
            for (byte pipeIndex = 0; pipeIndex < iface.bNumEndpoints; pipeIndex++)
            {
                if (!WinUsbInterop.WinUsb_QueryPipe(winUsbHandle, 0, pipeIndex, out var pipe))
                    continue;

                endpoints.Add(new UsbEndpointDescriptor
                {
                    EndpointAddress = pipe.PipeId,
                    TransferType = (UsbTransferType)pipe.PipeType,
                    MaxPacketSize = pipe.MaximumPacketSize,
                    Interval = pipe.Interval,
                });
            }

            interfaces = ImmutableArray.Create(new UsbInterfaceDescriptor
            {
                InterfaceNumber = iface.bInterfaceNumber,
                AlternateSetting = iface.bAlternateSetting,
                InterfaceClass = iface.bInterfaceClass,
                InterfaceSubClass = iface.bInterfaceSubClass,
                InterfaceProtocol = iface.bInterfaceProtocol,
                Endpoints = endpoints.ToImmutable(),
            });
        }

        return new UsbConfigurationDescriptor
        {
            ConfigurationValue = configurationValue,
            MaxPowerMilliamps = maxPowerMilliamps,
            Interfaces = interfaces,
        };
    }

    // -----------------------------------------------------------------------
    // Path resolution + error mapping
    // -----------------------------------------------------------------------

    /// <summary>
    /// Resolves a SetupAPI device-instance ID to the device-interface path that
    /// <c>CreateFile</c> needs. Pass-through if already an interface path. Mirrors
    /// <c>Periphery.Hid</c>'s resolver, keyed by
    /// <see cref="WinUsbInterop.GUID_DEVINTERFACE_USB_DEVICE"/>.
    /// </summary>
    private static string ResolveInterfacePath(string input)
    {
        if (input.StartsWith(@"\\?\", StringComparison.Ordinal)
            || input.StartsWith(@"\\.\", StringComparison.Ordinal))
            return input;

        var guid = WinUsbInterop.GUID_DEVINTERFACE_USB_DEVICE;

        int sizeResult = WinUsbInterop.CM_Get_Device_Interface_List_Size(
            out uint lenChars, in guid, input, WinUsbInterop.CM_GET_DEVICE_INTERFACE_LIST_PRESENT);
        if (sizeResult != WinUsbInterop.CR_SUCCESS || lenChars <= 1)
            return input; // nothing to resolve — let CreateFile produce the diagnostic

        var buffer = new char[lenChars];
        int listResult = WinUsbInterop.CM_Get_Device_Interface_List(
            in guid, input, buffer, lenChars, WinUsbInterop.CM_GET_DEVICE_INTERFACE_LIST_PRESENT);
        if (listResult != WinUsbInterop.CR_SUCCESS)
            return input;

        int firstNull = Array.IndexOf(buffer, '\0');
        return firstNull <= 0 ? input : new string(buffer, 0, firstNull);
    }

    private static UsbException MapOpenError(int error, string deviceId, string devicePath)
    {
        var inner = new IOException($"CreateFile failed for '{devicePath}'. Win32 error: {error}.");
        return error switch
        {
            ERROR_ACCESS_DENIED =>
                new UsbAccessDeniedException(
                    $"Access denied opening USB device '{deviceId}'. It may be owned by another " +
                    "process or bound to a non-WinUSB driver.", inner, deviceId),
            ERROR_FILE_NOT_FOUND or ERROR_PATH_NOT_FOUND or ERROR_DEVICE_NOT_CONNECTED =>
                new UsbDeviceNotFoundException(
                    $"USB device '{deviceId}' was not found — it may have been disconnected.", inner, deviceId),
            _ =>
                new UsbException($"Failed to open USB device '{deviceId}'. Win32 error: {error}.", inner, deviceId),
        };
    }

    private UsbException TransferError(string operation, string detail, int error) =>
        ClassifyTransferError(error, operation, detail, _devicePath);

    /// <summary>
    /// Classifies a failed WinUSB transfer. <paramref name="operation"/> is a human phrase
    /// ("bulk read"), <paramref name="detail"/> the endpoint context.
    /// </summary>
    /// <remarks>
    /// The message this replaced hedged three ways: "may have been disconnected, the endpoint
    /// stalled, or the transfer was cancelled". The third was never possible — the completion
    /// callback routes ERROR_OPERATION_ABORTED to cancellation and never reaches here — and the
    /// first two were left for the reader to sort out. Both cost an hour in #260.
    /// <para>
    /// Internal and static because it is pure: the rest of this class needs a device-interface
    /// path and a live WinUSB handle and cannot be unit tested, and the judgement can.
    /// </para>
    /// </remarks>
    internal static UsbException ClassifyTransferError(
        int error, string operation, string detail, string? deviceId)
    {
        var inner = new IOException($"WinUSB {operation} Win32 error {error} ({Win32Name(error)}).");
        string what = $"WinUSB {operation} failed ({detail}). {Win32Name(error)} ({error}).";

        return error switch
        {
            // The device is gone. Both codes mean the device object is no longer there, in any
            // call context, so there is nothing to hedge about.
            ERROR_NO_SUCH_DEVICE or ERROR_DEVICE_NOT_CONNECTED =>
                new UsbDeviceRemovedException(
                    $"{what} The device left the USB bus mid-transfer.", inner, deviceId),

            // NOT a removal on this path, whatever it means at open. WinUsb_ReadPipe /
            // WinUsb_WritePipe report ERROR_FILE_NOT_FOUND when the *pipe* is not found — an
            // endpoint address that is not on the claimed interface — so treating it as
            // evidence the device left would send a caller off to wait for a re-enumeration
            // that is never coming, over what is really a wrong endpoint (#272 review turn 5).
            // MapOpenError still reads these as not-found, correctly: there they come from
            // CreateFile on a device path.
            ERROR_FILE_NOT_FOUND or ERROR_PATH_NOT_FOUND =>
                new UsbTransferException(
                    $"{what} WinUSB has no pipe with that address on the claimed interface — "
                    + "check the endpoint against the configuration descriptor. This is not "
                    + "evidence that the device left the bus.",
                    inner, deviceId),

            // Driven by the predicate rather than repeating its code list, so the two cannot
            // disagree about which codes are ambiguous (#272 review turn 3).
            _ when IsAmbiguousTransferError(error) =>
                new UsbTransferException(
                    $"{what} The device stopped servicing the endpoint — most often a surprise "
                    + "removal, otherwise a stalled pipe.",
                    inner, deviceId),

            _ => new UsbTransferException(what, inner, deviceId),
        };
    }

    /// <summary>
    /// The Win32 codes that a surprise removal and a stalled pipe both produce, and which
    /// therefore cannot be classified from the code alone.
    /// </summary>
    /// <remarks>
    /// Distinguishing them is host-diagnostic work a caller may want to do, and the exception
    /// message deliberately does not carry instructions for it:
    /// <list type="bullet">
    /// <item>Periphery's own device tracker logs a real PnP transition — a disconnected /
    /// connected pair for the device — on a removal and nothing on a stall.</item>
    /// <item><c>DEVPKEY_Device_LastArrivalDate</c> is per-device and needs no log retention, so
    /// it survives where an event log has rolled.</item>
    /// <item>The <c>Kernel-PnP/Configuration</c> log does <b>not</b> settle it: it does not
    /// record re-arrival of an already-installed device, and reading its silence as "no
    /// re-enumeration" is the inference that sent #260 an hour down a firmware hypothesis.</item>
    /// </list>
    /// </remarks>
    internal static bool IsAmbiguousTransferError(int error) =>
        error is ERROR_BAD_COMMAND or ERROR_GEN_FAILURE;

    /// <summary>
    /// The symbolic name for a Win32 code, so a log line reads <c>ERROR_GEN_FAILURE (31)</c>
    /// rather than <c>Win32 error: 31</c> and the reader does not have to go looking.
    /// </summary>
    private static string Win32Name(int error) => error switch
    {
        ERROR_FILE_NOT_FOUND => "ERROR_FILE_NOT_FOUND",
        ERROR_PATH_NOT_FOUND => "ERROR_PATH_NOT_FOUND",
        ERROR_BAD_COMMAND => "ERROR_BAD_COMMAND",
        ERROR_GEN_FAILURE => "ERROR_GEN_FAILURE",
        ERROR_NO_SUCH_DEVICE => "ERROR_NO_SUCH_DEVICE",
        ERROR_DEVICE_NOT_CONNECTED => "ERROR_DEVICE_NOT_CONNECTED",
        _ => "Win32 error",
    };
}
