namespace Periphery.Serial.Tests;

using Step = FakeSyncSerialStream.Step;

/// <summary>
/// Exercises <see cref="BclSerialDuplexPipe"/>'s synchronous read pump against a
/// <see cref="FakeSyncSerialStream"/>. The subject is almost entirely the exception classifier:
/// the BCL backend's loop tick arrives as an exception, so the pump has to tell a tick apart
/// from a dead port without hardware to ask.
/// </summary>
public class BclSerialDuplexPipeTests
{
    /// <summary><c>ERROR_TIMEOUT</c> (1460), the HResult .NET 7 gives a timed-out read.</summary>
    private const int ErrorTimeoutHResult = unchecked((int)0x800705B4);

    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(20);

    private static CancellationToken Token(int ms = 5000) =>
        new CancellationTokenSource(ms).Token;

    private static async Task<byte[]> ReadOnce(BclSerialDuplexPipe pipe)
    {
        var read = await pipe.Input.ReadAsync(Token());
        var bytes = System.Buffers.BuffersExtensions.ToArray(read.Buffer);
        pipe.Input.AdvanceTo(read.Buffer.End);
        return bytes;
    }

    [Fact]
    public async Task ReadPump_TimeoutException_IsALoopTickAndNotAFailure()
    {
        // .NET 6 and earlier, and .NET 8 if the dotnet/runtime#80079 fix landed.
        var stream = new FakeSyncSerialStream(
            Step.Throw(new TimeoutException("The operation has timed out.")),
            Step.Throw(new TimeoutException("The operation has timed out.")),
            Step.Data(0x01, 0x02));

        await using var pipe = new BclSerialDuplexPipe(stream, Tick);

        // Reaching the bytes at all proves the two timeouts neither faulted the pipe nor
        // stopped the pump.
        Assert.Equal(new byte[] { 0x01, 0x02 }, await ReadOnce(pipe));
    }

    [Fact]
    public async Task ReadPump_IOExceptionCarryingErrorTimeout_IsALoopTickAndNotAFailure()
    {
        // .NET 7 turned a timed-out read into this. Same meaning, different type.
        var stream = new FakeSyncSerialStream(
            Step.Throw(new IOException("The operation has timed out.", ErrorTimeoutHResult)),
            Step.Data(0x03));

        await using var pipe = new BclSerialDuplexPipe(stream, Tick);

        Assert.Equal(new byte[] { 0x03 }, await ReadOnce(pipe));
    }

    [Fact]
    public async Task ReadPump_IOExceptionWithAnyOtherHResult_FaultsTheReader()
    {
        // The dangerous direction. A classifier widened to a bare IOException would swallow
        // this, spin forever on a dead port, and hang the consumer.
        var failure = new IOException("The device is not connected.", unchecked((int)0x8007048F));
        var stream = new FakeSyncSerialStream(Step.Throw(failure));

        await using var pipe = new BclSerialDuplexPipe(stream, Tick);

        var thrown = await Assert.ThrowsAsync<IOException>(() => pipe.Input.ReadAsync(Token()).AsTask());
        Assert.Same(failure, thrown);
    }

    [Fact]
    public async Task ReadPump_PortFailsMidSession_PropagatesOriginalExceptionToReader()
    {
        var failure = new UnauthorizedAccessException("Access to the port is denied.");
        var stream = new FakeSyncSerialStream(Step.Data(0x01, 0x02, 0x03));

        await using var pipe = new BclSerialDuplexPipe(stream, Tick);

        // The bytes that arrived before the failure still come through. Injected rather than
        // scripted, so the failure cannot overtake this read.
        Assert.Equal(new byte[] { 0x01, 0x02, 0x03 }, await ReadOnce(pipe));

        stream.Fail(failure);

        var thrown = await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => pipe.Input.ReadAsync(Token()).AsTask());
        Assert.Same(failure, thrown);
    }

    [Fact]
    public async Task ReadPump_OperationCanceled_IsAFailureBecauseTheReadNeverCarriesOurToken()
    {
        // The synchronous read is never handed the pump's token, so cancellation cannot
        // originate from our shutdown. Anything claiming to be cancelled came from elsewhere
        // and is a failure, unlike the RJCP pump where it is the shutdown path.
        var failure = new OperationCanceledException("The read was aborted by the driver.");
        var stream = new FakeSyncSerialStream(Step.Throw(failure));

        await using var pipe = new BclSerialDuplexPipe(stream, Tick);

        var thrown = await Assert.ThrowsAsync<OperationCanceledException>(
            () => pipe.Input.ReadAsync(Token()).AsTask());
        Assert.Same(failure, thrown);
    }

    [Fact]
    public async Task Dispose_OnAnIdlePort_CompletesCleanlyWithinTheJoinBudget()
    {
        var stream = new FakeSyncSerialStream(Tick);
        var pipe = new BclSerialDuplexPipe(stream, Tick);

        // Let the pump get into its tick loop rather than catching it before the first read.
        while (stream.ReadCount < 2) await Task.Delay(5, Token());

        await pipe.DisposeAsync();

        var read = await pipe.Input.ReadAsync(Token());

        Assert.True(read.IsCompleted);
        Assert.Equal(0, read.Buffer.Length);
    }

    [Fact]
    public async Task DisposeAsync_CalledTwice_IsIdempotent()
    {
        var stream = new FakeSyncSerialStream(Tick);
        var pipe = new BclSerialDuplexPipe(stream, Tick);

        while (stream.ReadCount < 2) await Task.Delay(5, Token());

        await pipe.DisposeAsync();
        // Must not throw ObjectDisposedException from re-cancelling an already-disposed _cts.
        await pipe.DisposeAsync();
    }

    [Fact]
    public async Task Dispose_StopsThePump()
    {
        var stream = new FakeSyncSerialStream(Tick);
        var pipe = new BclSerialDuplexPipe(stream, Tick);

        while (stream.ReadCount < 2) await Task.Delay(5, Token());
        await pipe.DisposeAsync();

        var afterDispose = stream.ReadCount;
        await Task.Delay(Tick + Tick + Tick, Token());

        // At most the one read that was already in flight when the token tripped.
        Assert.True(stream.ReadCount <= afterDispose + 1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]      // SerialPort.InfiniteTimeout
    [InlineData(-250)]
    public void Constructor_RejectsANonPositiveReadTick(int milliseconds)
    {
        using var port = new System.IO.Ports.SerialPort("COM255");

        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => new BclSerialDuplexPipe(port, TimeSpan.FromMilliseconds(milliseconds)));
        Assert.Equal("readTick", ex.ParamName);
    }

    [Fact]
    public void Constructor_RejectsANullPort()
    {
        var ex = Assert.Throws<ArgumentNullException>(() => new BclSerialDuplexPipe(null!));
        Assert.Equal("serialPort", ex.ParamName);
    }
}
