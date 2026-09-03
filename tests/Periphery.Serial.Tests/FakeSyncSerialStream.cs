namespace Periphery.Serial.Tests;

/// <summary>
/// A <see cref="Stream"/> stand-in for an open <c>System.IO.Ports</c> serial port, used to drive
/// <see cref="BclSerialDuplexPipe"/>'s synchronous read pump without hardware.
/// </summary>
/// <remarks>
/// <para>
/// The BCL pump calls the blocking <see cref="Read(byte[], int, int)"/>, not <c>ReadAsync</c>,
/// so an async-driven fake cannot exercise it. Scripted steps are played in order: a step either
/// hands back bytes or throws. Once they run out the stream behaves like an idle port — each
/// read waits briefly and then throws <see cref="TimeoutException"/>, the way an expired
/// <c>ReadTimeout</c> does.
/// </para>
/// <para>
/// That idle behaviour matters. It means a test never has to cancel a blocked read, which is
/// precisely the thing the real backend cannot do.
/// </para>
/// </remarks>
internal sealed class FakeSyncSerialStream : Stream
{
    /// <summary>One scripted read: bytes to return, or an exception to throw.</summary>
    internal sealed record Step(byte[]? Bytes, Exception? Throws)
    {
        public static Step Data(params byte[] bytes) => new(bytes, null);
        public static Step Throw(Exception exception) => new(null, exception);
    }

    private readonly Queue<Step> _steps;
    private readonly TimeSpan _idleTick;

    /// <summary>Never set. Gives the idle read something real to wait on.</summary>
    private readonly ManualResetEventSlim _idle = new(initialState: false);

    private int _reads;

    /// <summary>How many times <see cref="Read(byte[], int, int)"/> has been called.</summary>
    public int ReadCount => Volatile.Read(ref _reads);

    /// <summary>
    /// Make a later read throw <paramref name="exception"/>. Use this rather than a scripted
    /// step when the test needs to consume earlier bytes first: a pipe completed with a failure
    /// throws on read even when it still holds unread data, so a scripted failure can overtake
    /// the consumer and hide the bytes before it.
    /// </summary>
    public void Fail(Exception exception)
    {
        lock (_steps) _steps.Enqueue(Step.Throw(exception));
    }

    public FakeSyncSerialStream(params Step[] steps)
        : this(TimeSpan.FromMilliseconds(20), steps)
    {
    }

    public FakeSyncSerialStream(TimeSpan idleTick, params Step[] steps)
    {
        _idleTick = idleTick;
        _steps = new Queue<Step>(steps);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        Interlocked.Increment(ref _reads);

        Step? step;
        lock (_steps)
        {
            step = _steps.Count > 0 ? _steps.Dequeue() : null;
        }

        if (step is null)
        {
            // Out of script: an idle port whose ReadTimeout keeps expiring.
            _idle.Wait(_idleTick);
            throw new TimeoutException("The operation has timed out.");
        }

        if (step.Throws is not null) throw step.Throws;

        var bytes = step.Bytes!;
        bytes.CopyTo(buffer.AsSpan(offset, count));
        return bytes.Length;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _idle.Dispose();
        base.Dispose(disposing);
    }

    // PipeWriter.Create requires a writable stream; the tests never assert on what is
    // written, so the write side just swallows bytes.
    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;

    public override void Write(byte[] buffer, int offset, int count) { }
    public override void Flush() { }

    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
