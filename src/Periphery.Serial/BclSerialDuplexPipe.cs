// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.IO.Pipelines;
using System.IO.Ports;

namespace Periphery.Serial;

/// <summary>
/// An <see cref="IDuplexPipe"/> backed by an already-open <see cref="SerialPort"/>. The caller
/// owns the serial port lifecycle (open/close/dispose).
/// </summary>
/// <remarks>
/// <para>
/// The read pump is synchronous, on a dedicated thread, and that is not a style choice — it is
/// what a live board on the bench proved necessary. An earlier, naive implementation built this
/// pipe with <c>PipeReader.Create(port.BaseStream)</c>, which issues <c>ReadAsync</c> under the
/// hood; against a part that stayed genuinely silent, that read never returned, no matter what
/// timeout the caller configured, and the connection attempt hung indefinitely. On Windows
/// <c>SerialPort.BaseStream.ReadAsync</c> honours neither cancellation nor a timeout: it ignores
/// the <see cref="CancellationToken"/> (dotnet/runtime#30850), and
/// <see cref="SerialPort.ReadTimeout"/> is documented as not affecting <c>BeginRead</c>, which is
/// what <c>ReadAsync</c> is built on. A read issued that way returns only when bytes arrive or
/// the port closes.
/// </para>
/// <para>
/// The synchronous <see cref="Stream.Read(byte[], int, int)"/> does honour
/// <see cref="SerialPort.ReadTimeout"/>, through <c>SetCommTimeouts</c> at driver level. That
/// timeout is this pump's loop tick, and being driver-enforced it is not subject to the ~15.6ms
/// .NET timer resolution that would cripple a polled design. <c>DataReceived</c> and
/// <c>BytesToRead</c> are not used; both are unreliable in ways this library cannot work around.
/// </para>
/// <para>
/// Cancellation is therefore observed at the top of the loop rather than inside a read.
/// Consumers do not wait on the read — they wait on <see cref="Input"/>, and cancelling that is
/// immediate. Only <see cref="DisposeAsync"/> sees the tick, and it bounds its wait rather than
/// blocking on a read in flight.
/// </para>
/// <para>
/// Ported from <c>call-and-response</c> (commit <c>bb95838</c>, branch
/// <c>claude/bcl-serial-transport</c>, same author) — that branch predates the decision
/// (ADR-0062) to keep the serial backend split inside Periphery rather than the framing library,
/// and is not expected to land there.
/// </para>
/// </remarks>
public sealed class BclSerialDuplexPipe : IDuplexPipe, IAsyncDisposable
{
    /// <summary>
    /// The default loop tick. Nothing awaits the pump, so a longer tick costs no latency —
    /// <c>Read</c> still returns on the first byte available — and a shorter one only
    /// multiplies caught exceptions.
    /// </summary>
    public static readonly TimeSpan DefaultReadTick = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// <c>ERROR_TIMEOUT</c> (1460). .NET 7 changed a timed-out read from
    /// <see cref="TimeoutException"/> to <see cref="IOException"/> carrying this HResult
    /// (dotnet/runtime#80079), and a fix was targeted at 8.0.0. Both forms stay recognised,
    /// because identical source can meet either depending on the runtime it is rolled forward to.
    /// </summary>
    private const int ErrorTimeoutHResult = unchecked((int)0x800705B4);

    private readonly Pipe _rxPipe = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pumpTask;
    private readonly TimeSpan _joinTimeout;
    private readonly SerialPort? _port;
    private readonly int _originalReadTimeout;

    /// <inheritdoc />
    public PipeReader Input => _rxPipe.Reader;

    /// <inheritdoc />
    public PipeWriter Output { get; }

    /// <param name="serialPort">An open port. This pipe does not open, close, or dispose it.</param>
    /// <param name="readTick">
    /// How long a read waits for the first byte before coming back empty so the pump can check
    /// for cancellation. Defaults to <see cref="DefaultReadTick"/>. This pipe writes it to
    /// <see cref="SerialPort.ReadTimeout"/> and owns that property for its lifetime, restoring
    /// the previous value on disposal when the pump stops in time.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="readTick"/> is <see cref="SerialPort.InfiniteTimeout"/> or is not positive.
    /// An infinite tick would leave a disposed pipe's pump parked on the port handle, where it
    /// would steal bytes from any later pipe built over the same port.
    /// </exception>
    public BclSerialDuplexPipe(SerialPort serialPort, TimeSpan? readTick = null)
    {
        ArgumentNullException.ThrowIfNull(serialPort);
        var tick = Validate(readTick ?? DefaultReadTick);

        _port = serialPort;
        _originalReadTimeout = serialPort.ReadTimeout;

        // Before the pump starts, not after. A pump that gets one read in under the
        // framework's InfiniteTimeout default would park on it and never come back.
        serialPort.ReadTimeout = (int)tick.TotalMilliseconds;

        var stream = serialPort.BaseStream;

        // One tick to notice cancellation, one for the read already in flight when it arrived.
        _joinTimeout = tick + tick;
        Output = PipeWriter.Create(stream, new StreamPipeWriterOptions(leaveOpen: true));
        _pumpTask = StartPump(stream, _rxPipe.Writer, _cts.Token);
    }

    /// <summary>
    /// Test seam. The pump needs nothing beyond <see cref="Stream"/>, so the unit tests can
    /// drive it with a stream that times out and fails on demand instead of with real hardware.
    /// The public surface stays pinned to <see cref="SerialPort"/>.
    /// </summary>
    internal BclSerialDuplexPipe(Stream stream, TimeSpan readTick)
    {
        ArgumentNullException.ThrowIfNull(stream);

        _joinTimeout = readTick + readTick;
        Output = PipeWriter.Create(stream, new StreamPipeWriterOptions(leaveOpen: true));
        _pumpTask = StartPump(stream, _rxPipe.Writer, _cts.Token);
    }

    private static Task StartPump(Stream stream, PipeWriter writer, CancellationToken token) =>
        Task.Factory.StartNew(
            () => SerialReadPump.RunAsync(
                    writer,
                    // Synchronous on purpose: the async path honours neither the token nor
                    // ReadTimeout on Windows. Blocking is what the dedicated thread is for.
                    (buffer, _) => new ValueTask<int>(stream.Read(buffer, 0, buffer.Length)),
                    Classify,
                    token)
                .GetAwaiter().GetResult(),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

    private static TimeSpan Validate(TimeSpan readTick)
    {
        if (readTick <= TimeSpan.Zero || readTick.TotalMilliseconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(readTick),
                readTick,
                "The read tick must be a positive number of milliseconds. SerialPort.InfiniteTimeout " +
                "is not valid here: a pump that never times out cannot observe cancellation, so a " +
                "disposed pipe would leave a thread parked on the port handle.");
        }

        return readTick;
    }

    /// <summary>
    /// A timed-out read is this pump's loop tick, not a port failure. Anything else is a
    /// failure the consumer has to see.
    /// </summary>
    /// <remarks>
    /// Deliberately narrow, and the two mistakes are not symmetric. Missing a timeout form
    /// faults the pipe on an idle port, which announces itself the first time anyone runs it.
    /// Widening to a bare <see cref="IOException"/> mistakes a dead port for a tick: the pump
    /// spins, the pipe never faults, and the consumer hangs until its own token fires.
    /// </remarks>
    private static ReadDisposition Classify(Exception exception) =>
        exception is TimeoutException ||
        (exception is IOException io && io.HResult == ErrorTimeoutHResult)
            ? ReadDisposition.Benign
            : ReadDisposition.Failure;

    private readonly object _disposeLock = new();
    private Task? _disposeTask;

    /// <summary>
    /// Signals the background pump to stop and waits for it to actually finish before handing
    /// the port back to the caller. Does not close or dispose the underlying
    /// <see cref="SerialPort"/>. Safe to call more than once — every call after the first awaits
    /// the same cleanup rather than repeating it.
    /// </summary>
    /// <remarks>
    /// A synchronous read in flight is not cancellable, and the alternative — closing the port
    /// to unblock it — belongs to the caller and is the close-to-unblock pattern this library
    /// removed. The common case still returns within two read ticks, since that is what the
    /// pump's own <see cref="SerialPort.ReadTimeout"/> bounds it to; when a read runs longer than
    /// that, this keeps waiting rather than restoring <see cref="SerialPort.ReadTimeout"/> and
    /// returning while the pump still owns the port — a caller that reconfigures or closes the
    /// port believing disposal is complete would otherwise race it. Consumers are unaffected
    /// either way: they wait on <see cref="Input"/>, which cancels immediately.
    /// </remarks>
    public ValueTask DisposeAsync()
    {
        lock (_disposeLock)
        {
            _disposeTask ??= DisposeAsyncCore();
        }

        return new ValueTask(_disposeTask);
    }

    private async Task DisposeAsyncCore()
    {
        _cts.Cancel();

        try
        {
            await _pumpTask.WaitAsync(_joinTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // A read was still in flight past the fast path. Keep waiting for it to
            // actually finish rather than abandoning the port mid-tick — see the remarks
            // above.
            try
            {
                await _pumpTask.ConfigureAwait(false);
            }
            catch
            {
                // Already reported to the consumer through the pipe's completion.
            }
        }
        catch
        {
            // The pump faulted; already reported to the consumer through the pipe's
            // completion. Disposal still has to restore the port and release _cts.
        }

        // The pump is now provably out of Read; SetCommTimeouts on a port with I/O in
        // flight is not something to do on the way out.
        if (_port is not null)
        {
            try
            {
                _port.ReadTimeout = _originalReadTimeout;
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or ObjectDisposedException)
            {
                // The caller closed or disposed the port first. Nothing left to restore.
            }
        }

        _cts.Dispose();
    }
}
