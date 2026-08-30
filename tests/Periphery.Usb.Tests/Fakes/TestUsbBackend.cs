using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;

namespace Periphery.Usb.Tests.Fakes;

/// <summary>
/// In-memory <see cref="IUsbBackend"/> for exercising <see cref="UsbDevice"/>'s
/// public surface without real hardware. Records the last claim/control call and
/// can be told how many bytes the next bulk read should yield (to test slicing).
/// </summary>
internal sealed class TestUsbBackend : IUsbBackend
{
    public int DisposeCount { get; private set; }
    public byte? LastClaimedInterface { get; private set; }
    public byte? LastReleasedInterface { get; private set; }
    public UsbControlSetup LastControlSetup { get; private set; }

    /// <summary>When set, the next bulk read returns exactly this many bytes (clamped to the buffer).</summary>
    public int? NextReadByteCount { get; set; }

    /// <summary>Filler byte written into read / control buffers.</summary>
    public byte Fill { get; set; } = 0xAB;

    public UsbDeviceDescriptor DeviceDescriptor { get; init; } = new()
    {
        UsbVersion = 0x0200,
        DeviceClass = 0x00,
        MaxPacketSize0 = 64,
        VendorId = new HardwareId(0x10C4),
        ProductId = new HardwareId(0x8A7E),
        ConfigurationCount = 1,
    };

    public UsbConfigurationDescriptor Configuration { get; init; } = new()
    {
        ConfigurationValue = 1,
        MaxPowerMilliamps = 100,
        Interfaces = ImmutableArray<UsbInterfaceDescriptor>.Empty,
    };

    /// <summary>
    /// When true, every transfer blocks until its <see cref="CancellationToken"/> fires —
    /// simulating a wedged endpoint, to exercise the transfer watchdog.
    /// </summary>
    public bool BlockUntilCancelled { get; set; }

    public void ClaimInterface(byte interfaceNumber) => LastClaimedInterface = interfaceNumber;

    public void ReleaseInterface(byte interfaceNumber) => LastReleasedInterface = interfaceNumber;

    public async Task<int> ControlTransferAsync(UsbControlSetup setup, Memory<byte> buffer, CancellationToken ct)
    {
        LastControlSetup = setup;
        if (BlockUntilCancelled)
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
        buffer.Span.Fill(Fill); // simulate an IN data stage filling the buffer
        return buffer.Length;
    }

    public async Task<int> BulkReadAsync(byte endpointAddress, Memory<byte> buffer, CancellationToken ct)
    {
        if (BlockUntilCancelled)
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
        int n = Math.Min(NextReadByteCount ?? buffer.Length, buffer.Length);
        buffer.Span[..n].Fill(Fill);
        return n;
    }

    public async Task<int> BulkWriteAsync(byte endpointAddress, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        if (BlockUntilCancelled)
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
        return data.Length;
    }

    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        return ValueTask.CompletedTask;
    }
}
