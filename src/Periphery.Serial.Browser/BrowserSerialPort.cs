// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.IO.Pipelines;
using Periphery.Serial.Browser.Core;

namespace Periphery.Serial.Browser;

/// <summary>
/// A serial port the user granted through the browser's chooser, read and written as an
/// <see cref="IDuplexPipe"/> (ADR-0092 D4).
/// </summary>
/// <remarks>
/// <para>It has no id: Web Serial reports nothing that survives a replug. Hold the object for as
/// long as the port is in use.</para>
/// <para>Two await loops move the bytes, one per direction, on the page's one thread. The pipes
/// continue their readers inline, so a reader sees bytes as soon as the browser hands them over.</para>
/// </remarks>
public sealed class BrowserSerialPort : IAsyncDisposable
{
    private static readonly PipeOptions InlinePipes =
        new(readerScheduler: PipeScheduler.Inline, writerScheduler: PipeScheduler.Inline, useSynchronizationContext: false);

    private const int ReadChunk = 4096;

    private readonly IWebSerialPort _port;
    private Pipe? _input;
    private Pipe? _output;
    private IDuplexPipe? _pipe;
    private Task? _inPump;
    private Task? _outPump;
    private bool _opening;
    private bool _disposed;

    internal BrowserSerialPort(IWebSerialPort port, ushort? usbVendorId, ushort? usbProductId)
    {
        _port = port;
        UsbVendorId = usbVendorId;
        UsbProductId = usbProductId;
    }

    /// <summary>The USB vendor ID of the port's adapter, or <see langword="null"/> for a port that is not USB, such as Bluetooth RFCOMM.</summary>
    public ushort? UsbVendorId { get; }

    /// <summary>The USB product ID of the port's adapter, or <see langword="null"/> for a port that is not USB.</summary>
    public ushort? UsbProductId { get; }

    /// <summary>Whether <see cref="OpenAsync"/> has completed and the port has not been disposed.</summary>
    public bool IsOpen => _pipe is not null && !_disposed;

    /// <summary>
    /// The port's bytes: <see cref="IDuplexPipe.Input"/> receives, <see cref="IDuplexPipe.Output"/>
    /// sends. Input completes when the port closes, and faults with <see cref="IOException"/> when
    /// the browser loses it. A write the browser fails surfaces as that <see cref="IOException"/>
    /// on the next write to Output.
    /// </summary>
    /// <exception cref="InvalidOperationException">The port has not been opened.</exception>
    public IDuplexPipe Pipe => _pipe ?? throw new InvalidOperationException("Open the port with OpenAsync before using its pipe.");

    /// <summary>Opens the port with <paramref name="options"/> and starts moving bytes.</summary>
    /// <exception cref="ArgumentOutOfRangeException">An option is outside what Web Serial accepts.</exception>
    /// <exception cref="InvalidOperationException">The port is already open, or another page or program holds it.</exception>
    /// <exception cref="IOException">The browser could not open the port.</exception>
    public async Task OpenAsync(SerialPortOpenOptions options, CancellationToken ct = default)
    {
        string json = WebSerialJson.WriteOptions(options);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_pipe is not null || _opening)
            throw new InvalidOperationException("The port is already open.");
        ct.ThrowIfCancellationRequested();

        _opening = true;
        try
        {
            await CallAsync(() => _port.OpenAsync(json), "open").ConfigureAwait(false);

            // Web Serial cannot cancel open(). A cancellation that arrived while it ran closes the
            // port again rather than leave it open behind the caller.
            if (ct.IsCancellationRequested || _disposed)
            {
                await CloseQuietlyAsync().ConfigureAwait(false);
                ObjectDisposedException.ThrowIf(_disposed, this);
                ct.ThrowIfCancellationRequested();
            }

            _input = new Pipe(InlinePipes);
            _output = new Pipe(InlinePipes);
            _pipe = new DuplexPipe(_input.Reader, _output.Writer);
            _inPump = PumpInAsync(_input.Writer);
            _outPump = PumpOutAsync(_output.Reader);
        }
        finally
        {
            _opening = false;
        }
    }

    /// <summary>
    /// Sets the output control lines. A signal left <see langword="null"/> is unchanged. DTR and RTS
    /// are how a USB-serial adapter drives an STM32's BOOT0 and NRST for bootloader entry.
    /// </summary>
    /// <exception cref="ArgumentException">No signal is given.</exception>
    /// <exception cref="InvalidOperationException">The port has not been opened.</exception>
    public async Task SetSignalsAsync(
        bool? dataTerminalReady = null, bool? requestToSend = null, bool? breakSignal = null, CancellationToken ct = default)
    {
        string json = WebSerialJson.WriteSignals(dataTerminalReady, requestToSend, breakSignal);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_pipe is null)
            throw new InvalidOperationException("Open the port with OpenAsync before setting its signals.");
        ct.ThrowIfCancellationRequested();

        await CallAsync(() => _port.SetSignalsAsync(json), "setSignals").ConfigureAwait(false);
    }

    private async Task PumpInAsync(PipeWriter writer)
    {
        try
        {
            while (true)
            {
                var memory = writer.GetMemory(ReadChunk);
                int read = await CallAsync(() => _port.ReadAsync(memory), "read").ConfigureAwait(false);
                if (read == 0)
                    break;

                writer.Advance(read);
                var flushed = await writer.FlushAsync().ConfigureAwait(false);
                if (flushed.IsCompleted)
                    break;
            }

            await writer.CompleteAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            await writer.CompleteAsync(e).ConfigureAwait(false);
        }
    }

    private async Task PumpOutAsync(PipeReader reader)
    {
        try
        {
            while (true)
            {
                var result = await reader.ReadAsync().ConfigureAwait(false);
                if (result.IsCanceled)
                    break;

                foreach (var segment in result.Buffer)
                    await CallAsync(() => _port.WriteAsync(segment), "write").ConfigureAwait(false);

                reader.AdvanceTo(result.Buffer.End);
                if (result.IsCompleted)
                    break;
            }

            await reader.CompleteAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            await reader.CompleteAsync(e).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Stops sending, closes the port, and completes <see cref="IDuplexPipe.Input"/> once the
    /// pending read ends. Bytes written to <see cref="IDuplexPipe.Output"/> and not yet sent are
    /// dropped.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_pipe is null)
            return;

        // An idle send loop stops here. One inside a write cannot be stopped from .NET: under
        // hardware flow control the write may never finish, so close comes before any wait.
        _output!.Reader.CancelPendingRead();

        // Closing aborts a write in flight and cancels the pending read, which ends both loops.
        await CloseQuietlyAsync().ConfigureAwait(false);

        await _outPump!.ConfigureAwait(false);
        await _inPump!.ConfigureAwait(false);
        await _output.Writer.CompleteAsync().ConfigureAwait(false);
    }

    private async Task CloseQuietlyAsync()
    {
        try
        {
            await _port.CloseAsync().ConfigureAwait(false);
        }
        catch (WebSerialRejectionException)
        {
            // A port whose device has gone rejects close(); there is nothing left to release.
        }
    }

    private static async Task CallAsync(Func<Task> call, string operation)
    {
        try
        {
            await call().ConfigureAwait(false);
        }
        catch (WebSerialRejectionException e)
        {
            throw Map(e, operation);
        }
    }

    private static async Task<T> CallAsync<T>(Func<Task<T>> call, string operation)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (WebSerialRejectionException e)
        {
            throw Map(e, operation);
        }
    }

    /// <summary>
    /// <c>InvalidStateError</c> means the port is in the wrong state for the call, such as already
    /// open or held by another page; everything else is the port or its device failing.
    /// </summary>
    internal static Exception Map(WebSerialRejectionException e, string operation)
        => e.Rejection.Name == "InvalidStateError"
            ? new InvalidOperationException($"Web Serial refused {operation}: {e.Rejection.Message}", e)
            : new IOException($"Web Serial {operation} failed ({e.Rejection.Name}): {e.Rejection.Message}", e);

    private sealed class DuplexPipe(PipeReader input, PipeWriter output) : IDuplexPipe
    {
        public PipeReader Input { get; } = input;
        public PipeWriter Output { get; } = output;
    }
}
