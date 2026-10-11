using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Periphery.Usb.Browser.Tests.Fakes;

/// <summary>
/// A scriptable <see cref="IWebUsbDevice"/>. Open, configuration and claim complete at once or
/// reject as scripted. <c>GET_DESCRIPTOR</c> is answered from <see cref="DeviceDescriptor"/> and
/// <see cref="ConfigurationDescriptor"/>. Every other transfer stays pending until the test
/// completes it through <see cref="NextTransferAsync"/>, as a device that has not answered yet.
/// </summary>
internal sealed class FakeWebUsbDevice : IWebUsbDevice
{
    /// <summary>A Treehopper's device descriptor: USB 2.0, <c>10C4:8A7E</c>, 64-byte endpoint 0.</summary>
    public static readonly byte[] TreehopperDevice =
        [0x12, 0x01, 0x00, 0x02, 0x00, 0x00, 0x00, 0x40, 0xC4, 0x10, 0x7E, 0x8A, 0x00, 0x01, 0x01, 0x02, 0x03, 0x01];

    /// <summary>A Treehopper's configuration descriptor, from its firmware's <c>descriptors.c</c>.</summary>
    public static readonly byte[] TreehopperConfiguration =
    [
        0x09, 0x02, 0x2E, 0x00, 0x01, 0x01, 0x00, 0xC0, 0x32,
        0x09, 0x04, 0x00, 0x00, 0x04, 0xFF, 0xFF, 0xFF, 0x00,
        0x07, 0x05, 0x81, 0x02, 0x40, 0x00, 0x01,
        0x07, 0x05, 0x82, 0x02, 0x40, 0x00, 0x01,
        0x07, 0x05, 0x01, 0x02, 0x40, 0x00, 0x01,
        0x07, 0x05, 0x02, 0x02, 0x40, 0x00, 0x01,
    ];

    private readonly Channel<PendingTransfer> _issued = Channel.CreateUnbounded<PendingTransfer>();
    private readonly ConcurrentDictionary<PendingTransfer, byte> _pending = new();

    public byte? ActiveConfigurationValue { get; set; } = 1;
    public IReadOnlyList<byte> ConfigurationValues { get; set; } = [1];
    public byte[] DeviceDescriptor { get; set; } = TreehopperDevice;
    public byte[] ConfigurationDescriptor { get; set; } = TreehopperConfiguration;

    public WebUsbRejection? RejectOpen { get; set; }
    public WebUsbRejection? RejectClaim { get; set; }

    /// <summary>Every call, in order, as a short text.</summary>
    public ConcurrentQueue<string> Log { get; } = new();

    /// <summary>How many transfers other than descriptor reads have been issued.</summary>
    public int TransfersIssued => _transfersIssued;
    private int _transfersIssued;

    /// <summary>Completes with the next transfer issued, including one issued before the call.</summary>
    public ValueTask<PendingTransfer> NextTransferAsync(CancellationToken ct = default) => _issued.Reader.ReadAsync(ct);

    /// <summary>Whether a transfer is waiting to be taken with <see cref="NextTransferAsync"/>.</summary>
    public bool HasUntakenTransfer => _issued.Reader.Count > 0;

    public Task OpenAsync()
    {
        Log.Enqueue("open");
        return RejectOpen is { } r ? Task.FromException(new WebUsbRejectionException(r)) : Task.CompletedTask;
    }

    public Task SelectConfigurationAsync(byte configurationValue)
    {
        Log.Enqueue($"selectConfiguration({configurationValue})");
        ActiveConfigurationValue = configurationValue;
        return Task.CompletedTask;
    }

    public Task ClaimInterfaceAsync(byte interfaceNumber)
    {
        Log.Enqueue($"claimInterface({interfaceNumber})");
        return RejectClaim is { } r ? Task.FromException(new WebUsbRejectionException(r)) : Task.CompletedTask;
    }

    /// <summary>Closes the device. Like Chromium, every pending transfer rejects with <c>AbortError</c>.</summary>
    public Task CloseAsync()
    {
        Log.Enqueue("close");
        foreach (var transfer in _pending.Keys)
            transfer.Reject("AbortError", "The transfer was cancelled.");
        return Task.CompletedTask;
    }

    public Task<WebUsbInResult> ControlTransferInAsync(UsbControlSetup setup, int length)
    {
        if (setup.Request == 0x06 && setup.RequestType == 0x80)
        {
            Log.Enqueue($"getDescriptor(0x{setup.Value:X4}, {length})");
            var source = (setup.Value >> 8) == 0x01 ? DeviceDescriptor : ConfigurationDescriptor;
            return Task.FromResult(new WebUsbInResult("ok", source.AsMemory(0, Math.Min(length, source.Length)).ToArray()));
        }

        Log.Enqueue($"controlTransferIn(0x{setup.Request:X2}, {length})");
        return Issue(new PendingTransfer(this, "controlTransferIn", 0, length, Array.Empty<byte>(), setup)).In!.Task;
    }

    public Task<WebUsbOutResult> ControlTransferOutAsync(UsbControlSetup setup, ReadOnlyMemory<byte> data)
    {
        Log.Enqueue($"controlTransferOut(0x{setup.Request:X2}, {data.Length})");
        return Issue(new PendingTransfer(this, "controlTransferOut", 0, data.Length, data.ToArray(), setup)).Out!.Task;
    }

    public Task<WebUsbInResult> TransferInAsync(byte endpointNumber, int length)
    {
        Log.Enqueue($"transferIn({endpointNumber}, {length})");
        return Issue(new PendingTransfer(this, "transferIn", endpointNumber, length, Array.Empty<byte>(), null)).In!.Task;
    }

    public Task<WebUsbOutResult> TransferOutAsync(byte endpointNumber, ReadOnlyMemory<byte> data)
    {
        Log.Enqueue($"transferOut({endpointNumber}, {data.Length})");
        return Issue(new PendingTransfer(this, "transferOut", endpointNumber, data.Length, data.ToArray(), null)).Out!.Task;
    }

    public Task ClearHaltAsync(UsbTransferDirection direction, byte endpointNumber)
    {
        Log.Enqueue($"clearHalt({(direction == UsbTransferDirection.DeviceToHost ? "in" : "out")}, {endpointNumber})");
        return Task.CompletedTask;
    }

    private PendingTransfer Issue(PendingTransfer transfer)
    {
        Interlocked.Increment(ref _transfersIssued);
        _pending[transfer] = 0;
        _issued.Writer.TryWrite(transfer);
        return transfer;
    }

    internal void Settled(PendingTransfer transfer) => _pending.TryRemove(transfer, out _);
}

/// <summary>A transfer the fake device has not answered yet.</summary>
internal sealed class PendingTransfer
{
    private readonly FakeWebUsbDevice _device;

    public PendingTransfer(FakeWebUsbDevice device, string kind, byte endpointNumber, int length, byte[] data, UsbControlSetup? setup)
    {
        _device = device;
        Kind = kind;
        EndpointNumber = endpointNumber;
        Length = length;
        Data = data;
        Setup = setup;
        if (kind.EndsWith("In", StringComparison.Ordinal))
            In = new(TaskCreationOptions.RunContinuationsAsynchronously);
        else
            Out = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public string Kind { get; }
    public byte EndpointNumber { get; }
    public int Length { get; }
    public byte[] Data { get; }
    public UsbControlSetup? Setup { get; }
    public TaskCompletionSource<WebUsbInResult>? In { get; }
    public TaskCompletionSource<WebUsbOutResult>? Out { get; }

    public void CompleteIn(byte[] data, string status = "ok")
    {
        _device.Settled(this);
        In!.TrySetResult(new WebUsbInResult(status, data));
    }

    public void CompleteOut(int bytesWritten, string status = "ok")
    {
        _device.Settled(this);
        Out!.TrySetResult(new WebUsbOutResult(status, bytesWritten));
    }

    public void Reject(string name, string message)
    {
        _device.Settled(this);
        var error = new WebUsbRejectionException(new WebUsbRejection(name, message));
        In?.TrySetException(error);
        Out?.TrySetException(error);
    }
}
