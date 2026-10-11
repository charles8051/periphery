using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Pipelines;

namespace Periphery.Serial.Browser.Tests.Fakes;

/// <summary>
/// A fake <see cref="IWebSerialPort"/> whose far end is <paramref name="device"/>: reads take what
/// the device writes to its <see cref="IDuplexPipe.Input"/>, and writes go to its
/// <see cref="IDuplexPipe.Output"/>. Closing ends a pending read, as cancelling a Web Serial reader
/// does.
/// </summary>
internal sealed class PipeWebSerialPort(IDuplexPipe device) : IWebSerialPort
{
    /// <summary>Every call, in order, as a short text.</summary>
    public ConcurrentQueue<string> Log { get; } = new();

    public WebSerialRejection? RejectOpen { get; set; }
    public WebSerialRejection? RejectReads { get; set; }
    public WebSerialRejection? RejectWrites { get; set; }

    /// <summary>Called while <c>open()</c> is in progress.</summary>
    public Action? OnOpen { get; set; }

    public Task OpenAsync(string optionsJson)
    {
        Log.Enqueue($"open {optionsJson}");
        OnOpen?.Invoke();
        return RejectOpen is { } r ? Task.FromException(new WebSerialRejectionException(r)) : Task.CompletedTask;
    }

    public async Task<int> ReadAsync(Memory<byte> destination)
    {
        if (RejectReads is { } r)
            throw new WebSerialRejectionException(r);

        var result = await device.Input.ReadAsync().ConfigureAwait(false);
        var buffer = result.Buffer;
        if (result.IsCanceled || (buffer.IsEmpty && result.IsCompleted))
        {
            device.Input.AdvanceTo(buffer.Start);
            return 0;
        }

        int count = (int)Math.Min(destination.Length, buffer.Length);
        buffer.Slice(0, count).CopyTo(destination.Span);
        device.Input.AdvanceTo(buffer.GetPosition(count));
        return count;
    }

    /// <summary>
    /// When set, every write stays pending until the port is closed, which aborts it, as a write
    /// held back by hardware flow control does.
    /// </summary>
    public bool BlockWrites { get; set; }

    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _writeBlocked = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes once a write is pending under <see cref="BlockWrites"/>.</summary>
    public Task WriteBlocked => _writeBlocked.Task;

    public async Task WriteAsync(ReadOnlyMemory<byte> data)
    {
        if (RejectWrites is { } r)
            throw new WebSerialRejectionException(r);
        Log.Enqueue($"write {data.Length}");

        if (BlockWrites)
        {
            _writeBlocked.TrySetResult();
            await _closed.Task.ConfigureAwait(false);
            throw new WebSerialRejectionException(new WebSerialRejection("AbortError", "The write was aborted."));
        }

        await device.Output.WriteAsync(data).ConfigureAwait(false);
    }

    public Task SetSignalsAsync(string signalsJson)
    {
        Log.Enqueue($"setSignals {signalsJson}");
        return Task.CompletedTask;
    }

    public Task CloseAsync()
    {
        Log.Enqueue("close");
        _closed.TrySetResult();
        device.Input.CancelPendingRead();
        return Task.CompletedTask;
    }
}

/// <summary>The far end of a <see cref="PipeWebSerialPort"/>, driven by a test as the device.</summary>
internal sealed class FakeLine : IDuplexPipe
{
    private readonly Pipe _toHost = new();
    private readonly Pipe _toDevice = new();

    public PipeReader Input => _toHost.Reader;
    public PipeWriter Output => _toDevice.Writer;

    /// <summary>What the device sends.</summary>
    public PipeWriter DeviceSends => _toHost.Writer;

    /// <summary>What the device receives.</summary>
    public PipeReader DeviceReceives => _toDevice.Reader;
}
