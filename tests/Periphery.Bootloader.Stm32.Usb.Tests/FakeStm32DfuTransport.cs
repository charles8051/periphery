using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Periphery.Bootloader.Stm32.Usb.Tests;

/// <summary>
/// A scripted <see cref="IStm32DfuTransport"/>: enqueue the GETSTATUS responses the device
/// would give, record the downloads, and drive <see cref="Stm32DfuProgrammer"/> with no
/// hardware. Poll timeouts are zero so tests don't actually wait.
/// </summary>
internal sealed class FakeStm32DfuTransport : IStm32DfuTransport
{
    private readonly Queue<DfuStatus> _statuses = new();

    public List<(ushort Block, byte[] Data)> Downloads { get; } = new();
    public int ClearStatusCalls { get; private set; }
    public int AbortCalls { get; private set; }
    public byte[] UploadResponse { get; set; } = Array.Empty<byte>();

    /// <summary>Enqueue the next GETSTATUS response.</summary>
    public FakeStm32DfuTransport Status(DfuStatusCode code, DfuState state)
    {
        _statuses.Enqueue(new DfuStatus(code, TimeSpan.Zero, state, 0));
        return this;
    }

    /// <summary>Enqueue an OK GETSTATUS response in the given state.</summary>
    public FakeStm32DfuTransport Ok(DfuState state) => Status(DfuStatusCode.Ok, state);

    public Task<int> DownloadAsync(ushort blockNum, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        Downloads.Add((blockNum, data.ToArray()));
        return Task.FromResult(data.Length);
    }

    public Task<int> UploadAsync(ushort blockNum, Memory<byte> buffer, CancellationToken ct)
    {
        int n = Math.Min(UploadResponse.Length, buffer.Length);
        UploadResponse.AsSpan(0, n).CopyTo(buffer.Span);
        return Task.FromResult(n);
    }

    public Task<DfuStatus> GetStatusAsync(CancellationToken ct)
        => Task.FromResult(_statuses.Count > 0
            ? _statuses.Dequeue()
            : new DfuStatus(DfuStatusCode.Ok, TimeSpan.Zero, DfuState.DfuIdle, 0));

    public Task ClearStatusAsync(CancellationToken ct) { ClearStatusCalls++; return Task.CompletedTask; }
    public Task<DfuState> GetStateAsync(CancellationToken ct) => Task.FromResult(DfuState.DfuIdle);
    public Task AbortAsync(CancellationToken ct) { AbortCalls++; return Task.CompletedTask; }
}
