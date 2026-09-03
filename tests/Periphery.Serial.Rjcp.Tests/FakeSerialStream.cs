namespace Periphery.Serial.Rjcp.Tests;

/// <summary>
/// A <see cref="Stream"/> stand-in for an open serial port, used to drive
/// <see cref="SerialDuplexPipe"/>'s read pump without hardware.
/// <para>
/// Reads hand back the scripted chunks in order. Once they run out the read parks — like a
/// quiet port with nothing to say — until either <see cref="Fail"/> makes it throw or the
/// pump's token is cancelled.
/// </para>
/// </summary>
internal sealed class FakeSerialStream : Stream
{
    private readonly Queue<byte[]> _chunks;
    private readonly TaskCompletionSource<Exception> _failure =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _parked =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public FakeSerialStream(params byte[][] chunks) => _chunks = new Queue<byte[]>(chunks);

    /// <summary>
    /// Stands in for a driver that aborts a read for reasons of its own. When set, a
    /// cancelled read throws this instead of an exception carrying the pump's token, so a
    /// test can put a foreign cancellation and the pump's shutdown in the same instant.
    /// </summary>
    public Exception? FailOnCancellation { get; init; }

    /// <summary>
    /// Completes once a read has run out of scripted chunks and parked. Await it before
    /// cancelling, so the pump is inside a read rather than still at its loop guard.
    /// </summary>
    public Task Parked => _parked.Task;

    /// <summary>Make the pending (or next) read throw <paramref name="exception"/>.</summary>
    public void Fail(Exception exception) => _failure.TrySetResult(exception);

    public override async Task<int> ReadAsync(
        byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_chunks.Count > 0)
        {
            var chunk = _chunks.Dequeue();
            chunk.CopyTo(buffer.AsSpan(offset, count));
            return chunk.Length;
        }

        Exception failure;
        _parked.TrySetResult();
        try
        {
            failure = await _failure.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (FailOnCancellation is not null)
        {
            throw FailOnCancellation;
        }

        throw failure;
    }

    // PipeWriter.Create requires a writable stream; the tests never assert on what is
    // written, so the write side just swallows bytes.
    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;

    public override void Write(byte[] buffer, int offset, int count) { }
    public override void Flush() { }

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
