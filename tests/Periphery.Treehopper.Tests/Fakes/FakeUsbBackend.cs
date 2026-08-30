using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Periphery.Usb;

namespace Periphery.Treehopper.Tests.Fakes;

/// <summary>
/// In-memory <see cref="IUsbBackend"/> that records bulk writes (per endpoint) and
/// replays a queue of canned bulk-read responses, so the Treehopper board's command
/// framing and response parsing can be asserted without hardware.
/// </summary>
internal sealed class FakeUsbBackend : IUsbBackend
{
    /// <summary>Every bulk write, in order, as <c>(endpoint, payload)</c>.</summary>
    public List<(byte Endpoint, byte[] Data)> Writes { get; } = new();

    /// <summary>Canned responses returned by successive bulk reads (FIFO).</summary>
    public Queue<byte[]> ReadResponses { get; } = new();

    public UsbDeviceDescriptor DeviceDescriptor { get; } = new()
    {
        UsbVersion = 0x0200,
        DeviceClass = 0xFF,
        MaxPacketSize0 = 64,
        VendorId = new HardwareId(0x10C4),
        ProductId = new HardwareId(0x8A7E),
    };

    public UsbConfigurationDescriptor Configuration { get; } = new()
    {
        ConfigurationValue = 1,
        MaxPowerMilliamps = 100,
    };

    public void ClaimInterface(byte interfaceNumber) { }

    public void ReleaseInterface(byte interfaceNumber) { }

    public Task<int> ControlTransferAsync(UsbControlSetup setup, Memory<byte> buffer, CancellationToken ct)
        => Task.FromResult(buffer.Length);

    /// <summary>
    /// Optional hook awaited inside every bulk read, before the canned response is
    /// dequeued. Null by default, so tests that do not set it are unaffected. The mirror of
    /// <see cref="OnBulkWrite"/>: lets a test fault or park a read — which is the half of a
    /// transaction that can strand a reply on the device.
    /// </summary>
    public Func<CancellationToken, Task>? OnBulkRead { get; set; }

    public async Task<int> BulkReadAsync(byte endpointAddress, Memory<byte> buffer, CancellationToken ct)
    {
        if (OnBulkRead is { } hook)
            await hook(ct).ConfigureAwait(false);

        if (ReadResponses.Count == 0)
            return 0;

        var response = ReadResponses.Dequeue();
        int n = Math.Min(response.Length, buffer.Length);
        response.AsSpan(0, n).CopyTo(buffer.Span);
        return n;
    }

    /// <summary>
    /// Optional hook awaited inside every bulk write, before it completes. Null by
    /// default, so tests that do not set it are unaffected. Lets a test park a caller
    /// inside the board's coms lock without this fake owning a gate protocol of its own —
    /// the waiting primitive belongs to the test.
    /// </summary>
    public Func<CancellationToken, Task>? OnBulkWrite { get; set; }

    /// <summary>Whether <see cref="DisposeAsync"/> has run.</summary>
    public bool Disposed { get; private set; }

    public async Task<int> BulkWriteAsync(byte endpointAddress, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        Writes.Add((endpointAddress, data.ToArray()));

        if (OnBulkWrite is { } hook)
            await hook(ct).ConfigureAwait(false);

        return data.Length;
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}
