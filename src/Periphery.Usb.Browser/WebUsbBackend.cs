// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Collections.Immutable;
using Periphery.Usb.Browser.Core;

namespace Periphery.Usb.Browser;

/// <summary>
/// <see cref="IUsbBackend"/> over one WebUSB device (ADR-0092 D6, D7). The shell around the pure
/// endpoint machines: it owns the pending promises and the callers' tasks, feeds each machine its
/// inputs, and carries out the outputs.
/// </summary>
/// <remarks>
/// <para>Interface 0 is claimed at open, as on WinUSB. Its bulk and interrupt IN endpoints follow
/// <see cref="InEndpoint"/>; its OUT endpoints and the control endpoint follow
/// <see cref="OrderedEndpoint"/>. A transfer on any other endpoint is refused before it is
/// issued, so a transfer's <c>NotFoundError</c> can only mean the device left or its grant was
/// revoked (<see cref="WebUsbOutcomes"/>).</para>
/// <para>Cancelling a call, or its deadline expiring in <see cref="UsbDevice"/>, completes the
/// call at once and leaves the WebUSB transfer running, because WebUSB cannot cancel one. The
/// endpoint machine decides where that transfer's result goes.</para>
/// <para>Nothing here blocks or starts a thread. In a browser every continuation runs on the one
/// thread; the lock only matters to tests that drive the backend from several.</para>
/// </remarks>
internal sealed class WebUsbBackend : IUsbBackend
{
    private const byte ControlKey = 0x00;

    private readonly IWebUsbDevice _device;
    private readonly string _deviceId;
    private readonly object _lock = new();
    private readonly Dictionary<byte, InEndpointState> _in = new();
    private readonly Dictionary<byte, OrderedEndpointState> _ordered = new();
    private readonly Dictionary<CallerId, PendingCall> _calls = new();
    private readonly HashSet<byte> _inEndpoints = new();
    private readonly HashSet<byte> _outEndpoints = new();
    private int _callersSeen;
    private bool _disposed;

    private WebUsbBackend(
        IWebUsbDevice device, string deviceId,
        UsbDeviceDescriptor deviceDescriptor, UsbConfigurationDescriptor configuration)
    {
        _device = device;
        _deviceId = deviceId;
        DeviceDescriptor = deviceDescriptor;
        Configuration = configuration;

        _ordered[ControlKey] = OrderedEndpointState.Idle;
        var interface0 = configuration.Interfaces.FirstOrDefault(i => i.InterfaceNumber == 0);
        foreach (var endpoint in interface0?.Endpoints ?? ImmutableArray<UsbEndpointDescriptor>.Empty)
        {
            if (endpoint.TransferType is not (UsbTransferType.Bulk or UsbTransferType.Interrupt))
                continue;

            // Bits 0-10 are the packet size; bits 11-12 count extra transactions per microframe.
            int packetSize = endpoint.MaxPacketSize & 0x7FF;
            if (endpoint.Direction == UsbTransferDirection.DeviceToHost && packetSize > 0)
            {
                _in[endpoint.EndpointAddress] = new InEndpointState(packetSize);
                _inEndpoints.Add(endpoint.EndpointAddress);
            }
            else if (endpoint.Direction == UsbTransferDirection.HostToDevice)
            {
                _ordered[endpoint.EndpointAddress] = OrderedEndpointState.Idle;
                _outEndpoints.Add(endpoint.EndpointAddress);
            }
        }
    }

    public UsbDeviceDescriptor DeviceDescriptor { get; }

    public UsbConfigurationDescriptor Configuration { get; }

    // ── Open ───────────────────────────────────────────────────────────

    /// <summary>
    /// Opens <paramref name="device"/>, selects its first configuration when it has none active,
    /// claims interface 0, and reads its device and configuration descriptors with
    /// <c>GET_DESCRIPTOR</c>. On any failure after <c>open()</c> the device is closed again.
    /// </summary>
    public static async Task<WebUsbBackend> OpenAsync(IWebUsbDevice device, string deviceId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(device);
        ct.ThrowIfCancellationRequested();

        await Call(device.OpenAsync, WebUsbPhase.Open, deviceId).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();

            var values = device.ConfigurationValues;
            if (values.Count == 0)
                throw new UsbException($"USB device '{deviceId}' reports no configurations.", deviceId);

            // On Windows the OS configures the device, and selectConfiguration is not implemented
            // there, so a device with none active fails to open on Windows (ADR-0092 D6).
            byte active = device.ActiveConfigurationValue ?? values[0];
            if (device.ActiveConfigurationValue is null)
                await Call(() => device.SelectConfigurationAsync(active), WebUsbPhase.Open, deviceId).ConfigureAwait(false);

            await Call(() => device.ClaimInterfaceAsync(0), WebUsbPhase.Claim, deviceId).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            var deviceBytes = await GetDescriptorAsync(
                device, deviceId, DescriptorDevice, 0, UsbDescriptors.DeviceDescriptorLength).ConfigureAwait(false);

            int configIndex = Math.Max(0, IndexOf(values, active));
            var header = await GetDescriptorAsync(
                device, deviceId, DescriptorConfiguration, configIndex, UsbDescriptors.ConfigurationHeaderLength).ConfigureAwait(false);
            int totalLength = header[2] | (header[3] << 8);
            var configBytes = totalLength > header.Length
                ? await GetDescriptorAsync(device, deviceId, DescriptorConfiguration, configIndex, totalLength).ConfigureAwait(false)
                : header;

            return new WebUsbBackend(
                device, deviceId,
                UsbDescriptors.ParseDeviceDescriptor(deviceBytes),
                UsbDescriptors.ParseConfiguration(configBytes));
        }
        catch
        {
            await CloseQuietlyAsync(device).ConfigureAwait(false);
            throw;
        }
    }

    private const byte DescriptorDevice = 0x01;
    private const byte DescriptorConfiguration = 0x02;

    private static async Task<byte[]> GetDescriptorAsync(
        IWebUsbDevice device, string deviceId, byte type, int index, int length)
    {
        var setup = new UsbControlSetup
        {
            RequestType = 0x80, // device-to-host, standard, device
            Request = 0x06,     // GET_DESCRIPTOR
            Value = (ushort)((type << 8) | index),
            Index = 0,
        };

        WebUsbInResult result;
        try
        {
            result = await device.ControlTransferInAsync(setup, length).ConfigureAwait(false);
        }
        catch (WebUsbRejectionException e)
        {
            throw WebUsbOutcomes.FromRejection(e.Rejection, WebUsbPhase.Open, ControlKey, deviceId);
        }

        var verdict = WebUsbOutcomes.FromStatus(result.Status, ControlKey, deviceId);
        if (!verdict.IsOk)
            throw verdict.Error!;

        int minimum = type == DescriptorDevice ? UsbDescriptors.DeviceDescriptorLength : UsbDescriptors.ConfigurationHeaderLength;
        if (result.Data.Length < minimum)
            throw new UsbException(
                $"USB device '{deviceId}' returned {result.Data.Length} bytes for descriptor type {type}; " +
                $"at least {minimum} were expected.", deviceId);

        return result.Data.ToArray();
    }

    private static async Task Call(Func<Task> call, WebUsbPhase phase, string deviceId)
    {
        try
        {
            await call().ConfigureAwait(false);
        }
        catch (WebUsbRejectionException e)
        {
            throw WebUsbOutcomes.FromRejection(e.Rejection, phase, ControlKey, deviceId);
        }
    }

    private static int IndexOf(IReadOnlyList<byte> values, byte value)
    {
        for (int i = 0; i < values.Count; i++)
        {
            if (values[i] == value)
                return i;
        }
        return -1;
    }

    // ── Interface claim ────────────────────────────────────────────────

    public void ClaimInterface(byte interfaceNumber)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (interfaceNumber != 0)
            throw new NotSupportedException(
                $"The WebUSB backend supports interface 0 only, which is claimed at open; interface {interfaceNumber} is not.");
    }

    public void ReleaseInterface(byte interfaceNumber)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // Interface 0 is released when the device is closed.
    }

    // ── Transfers ──────────────────────────────────────────────────────

    public Task<int> ControlTransferAsync(UsbControlSetup setup, Memory<byte> buffer, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var call = setup.Direction == UsbTransferDirection.DeviceToHost
            ? new PendingCall(ControlKey, ct) { Setup = setup, IsRead = true, ReadBuffer = buffer }
            : new PendingCall(ControlKey, ct) { Setup = setup, WriteData = buffer };
        return Submit(call, buffer.Length);
    }

    public Task<int> BulkReadAsync(byte endpointAddress, Memory<byte> buffer, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_inEndpoints.Contains(endpointAddress))
            throw NotAnEndpoint(endpointAddress, "IN");
        return Submit(new PendingCall(endpointAddress, ct) { IsRead = true, ReadBuffer = buffer }, buffer.Length);
    }

    public Task<int> BulkWriteAsync(byte endpointAddress, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_outEndpoints.Contains(endpointAddress))
            throw NotAnEndpoint(endpointAddress, "OUT");
        return Submit(new PendingCall(endpointAddress, ct) { WriteData = data }, data.Length);
    }

    private UsbException NotAnEndpoint(byte endpointAddress, string direction)
        => new($"Endpoint 0x{endpointAddress:X2} is not a bulk or interrupt {direction} endpoint of interface 0 " +
               $"on USB device '{_deviceId}'.", _deviceId);

    private Task<int> Submit(PendingCall call, int length)
    {
        if (call.Token.IsCancellationRequested)
            return Task.FromCanceled<int>(call.Token);

        CallerId caller;
        lock (_lock)
        {
            caller = new CallerId(++_callersSeen);
            _calls[caller] = call;
        }

        Feed(call.Key, new EndpointInput.Requested(caller, length));

        if (call.Token.CanBeCanceled)
        {
            var registration = call.Token.Register(() => Feed(call.Key, new EndpointInput.Cancelled(caller)));
            bool pending;
            lock (_lock)
            {
                pending = _calls.ContainsKey(caller);
                if (pending)
                    call.Registration = registration;
            }
            if (!pending)
                registration.Unregister();
        }

        return call.Completion.Task;
    }

    private void Feed(byte key, EndpointInput input)
    {
        ImmutableArray<EndpointOutput> outputs;
        lock (_lock)
        {
            if (_inEndpoints.Contains(key))
            {
                (var inState, outputs) = InEndpoint.Step(_in[key], input);
                _in[key] = inState;
            }
            else
            {
                (var orderedState, outputs) = OrderedEndpoint.Step(_ordered[key], input);
                _ordered[key] = orderedState;
            }
        }

        Apply(key, outputs);
    }

    private void Apply(byte key, ImmutableArray<EndpointOutput> outputs)
    {
        foreach (var output in outputs)
        {
            switch (output)
            {
                case EndpointOutput.Issue issue:
                    StartTransfer(key, issue);
                    break;

                case EndpointOutput.Complete complete:
                    Finish(complete.Caller, call =>
                    {
                        if (!call.IsRead)
                        {
                            call.Completion.TrySetResult(complete.Result.Count);
                            return;
                        }

                        var data = complete.Result.Data;
                        int count = Math.Min(data.Length, call.ReadBuffer.Length);
                        data[..count].CopyTo(call.ReadBuffer);
                        call.Completion.TrySetResult(count);
                    });
                    break;

                case EndpointOutput.Fail fail:
                    Finish(fail.Caller, call => call.Completion.TrySetException(fail.Error));
                    break;

                case EndpointOutput.Cancel cancel:
                    Finish(cancel.Caller, call => call.Completion.TrySetCanceled(call.Token));
                    break;

                case EndpointOutput.FailDisposed disposed:
                    Finish(disposed.Caller, call => call.Completion.TrySetException(
                        new ObjectDisposedException(nameof(UsbDevice), $"USB device '{_deviceId}' was closed.")));
                    break;

                case EndpointOutput.RejectConcurrent rejected:
                    Finish(rejected.Caller, call => call.Completion.TrySetException(new InvalidOperationException(
                        $"Another transfer is already in progress on endpoint 0x{key:X2}. UsbDevice runs one per endpoint.")));
                    break;
            }
        }
    }

    private void Finish(CallerId caller, Action<PendingCall> complete)
    {
        PendingCall? call;
        lock (_lock)
        {
            if (!_calls.Remove(caller, out call))
                return;
        }
        call.Registration.Unregister();
        complete(call);
    }

    private void StartTransfer(byte key, EndpointOutput.Issue issue)
    {
        // The request's payload is read now, while its caller is certainly still pending. An IN
        // transfer needs only its length, and a later caller may take it over.
        PendingCall? call;
        lock (_lock)
            _calls.TryGetValue(issue.Caller, out call);

        Task<TransferResult> transfer = key switch
        {
            ControlKey when call?.Setup is { } setup => ControlAsync(setup, call),
            _ when _inEndpoints.Contains(key) => InAsync(key, issue.Length),
            _ when call is not null => OutAsync(key, call.WriteData),
            _ => Task.FromResult<TransferResult>(new TransferResult.Fault(
                new InvalidOperationException($"Transfer {issue.Transfer.Value} on endpoint 0x{key:X2} has no caller."))),
        };

        _ = SettleAsync(key, issue.Transfer, transfer);
    }

    private async Task SettleAsync(byte key, TransferId transfer, Task<TransferResult> pending)
    {
        TransferResult result;
        try
        {
            result = await pending.ConfigureAwait(false);
        }
        catch (WebUsbRejectionException e)
        {
            result = new TransferResult.Fault(WebUsbOutcomes.FromRejection(e.Rejection, WebUsbPhase.Transfer, key, _deviceId));
        }
        catch (Exception e)
        {
            result = new TransferResult.Fault(e);
        }

        Feed(key, new EndpointInput.Settled(transfer, result));
    }

    private async Task<TransferResult> InAsync(byte endpointAddress, int length)
    {
        var result = await _device.TransferInAsync((byte)(endpointAddress & 0x0F), length).ConfigureAwait(false);
        return await JudgeAsync(endpointAddress, result.Status, TransferResult.Ok.Read(result.Data)).ConfigureAwait(false);
    }

    private async Task<TransferResult> OutAsync(byte endpointAddress, ReadOnlyMemory<byte> data)
    {
        var result = await _device.TransferOutAsync((byte)(endpointAddress & 0x0F), data).ConfigureAwait(false);
        return await JudgeAsync(endpointAddress, result.Status, TransferResult.Ok.Written(result.BytesWritten)).ConfigureAwait(false);
    }

    private async Task<TransferResult> ControlAsync(UsbControlSetup setup, PendingCall call)
    {
        if (setup.Direction == UsbTransferDirection.DeviceToHost)
        {
            var result = await _device.ControlTransferInAsync(setup, call.ReadBuffer.Length).ConfigureAwait(false);
            return await JudgeAsync(ControlKey, result.Status, TransferResult.Ok.Read(result.Data)).ConfigureAwait(false);
        }

        var written = await _device.ControlTransferOutAsync(setup, call.WriteData).ConfigureAwait(false);
        return await JudgeAsync(ControlKey, written.Status, TransferResult.Ok.Written(written.BytesWritten)).ConfigureAwait(false);
    }

    /// <summary>
    /// Turns a resolved status into a result. A stall on a bulk or interrupt endpoint is cleared
    /// before the fault is reported, so the next transfer on it can run.
    /// </summary>
    private async Task<TransferResult> JudgeAsync(byte endpointAddress, string status, TransferResult.Ok ok)
    {
        var verdict = WebUsbOutcomes.FromStatus(status, endpointAddress, _deviceId);
        if (verdict.IsOk)
            return ok;

        if (verdict.ClearHaltFirst)
        {
            var direction = (endpointAddress & 0x80) != 0
                ? UsbTransferDirection.DeviceToHost
                : UsbTransferDirection.HostToDevice;
            try
            {
                await _device.ClearHaltAsync(direction, (byte)(endpointAddress & 0x0F)).ConfigureAwait(false);
            }
            catch (WebUsbRejectionException)
            {
                // The stall is what the caller needs to hear about; a failed clear leaves the
                // endpoint halted, and the next transfer will say so.
            }
        }

        return new TransferResult.Fault(verdict.Error!);
    }

    // ── Disposal ───────────────────────────────────────────────────────

    /// <summary>
    /// Fails every waiting caller, discards held bytes and faults, then closes the device (ADR-0092
    /// D7 rule 10). Transfers still pending reject with <c>AbortError</c>, and their results are
    /// ignored.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        var pending = new List<(byte Key, ImmutableArray<EndpointOutput> Outputs)>();
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;

            foreach (var key in _in.Keys.ToArray())
            {
                (var state, var outputs) = InEndpoint.Step(_in[key], EndpointInput.Disposed.Instance);
                _in[key] = state;
                pending.Add((key, outputs));
            }

            foreach (var key in _ordered.Keys.ToArray())
            {
                (var state, var outputs) = OrderedEndpoint.Step(_ordered[key], EndpointInput.Disposed.Instance);
                _ordered[key] = state;
                pending.Add((key, outputs));
            }
        }

        foreach (var (key, outputs) in pending)
            Apply(key, outputs);

        await CloseQuietlyAsync(_device).ConfigureAwait(false);
    }

    private static async Task CloseQuietlyAsync(IWebUsbDevice device)
    {
        try
        {
            await device.CloseAsync().ConfigureAwait(false);
        }
        catch (WebUsbRejectionException)
        {
            // Closing a device that already left rejects; there is nothing left to release.
        }
    }

    private sealed class PendingCall(byte key, CancellationToken token)
    {
        public byte Key { get; } = key;
        public CancellationToken Token { get; } = token;
        public TaskCompletionSource<int> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public UsbControlSetup? Setup { get; init; }
        public bool IsRead { get; init; }
        public Memory<byte> ReadBuffer { get; init; }
        public ReadOnlyMemory<byte> WriteData { get; init; }
        public CancellationTokenRegistration Registration { get; set; }
    }
}
