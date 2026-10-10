// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.IO.Ports;
using Periphery.Cli.Serial.Core;
using Periphery.Serial;

namespace Periphery.Cli.Serial;

/// <summary>
/// One open port: the <see cref="SerialPort"/> and the duplex pipe reading it.
/// Reads go through <see cref="Pipe"/>. Writes go straight to the port, synchronously,
/// so <see cref="SerialPort.WriteTimeout"/> bounds a write that flow control holds back.
/// </summary>
internal sealed class SerialSession : IAsyncDisposable
{
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(2);

    private readonly SerialPort _port;

    private SerialSession(SerialPort port, BclSerialDuplexPipe pipe)
    {
        _port = port;
        Pipe = pipe;
    }

    internal string PortName => _port.PortName;

    internal BclSerialDuplexPipe Pipe { get; }

    internal static SerialSession Open(string portName, SerialLineSettings line, bool dtr, bool rts)
    {
        var port = new SerialPort(portName, line.BaudRate, line.Parity, line.DataBits, line.StopBits)
        {
            Handshake = line.Handshake,
            WriteTimeout = (int)WriteTimeout.TotalMilliseconds,
            DtrEnable = dtr,
        };

        // The driver owns RTS under hardware flow control and rejects a manual setting.
        if (line.Handshake is not (Handshake.RequestToSend or Handshake.RequestToSendXOnXOff))
            port.RtsEnable = rts;

        try
        {
            port.Open();
            return new SerialSession(port, new BclSerialDuplexPipe(port));
        }
        catch
        {
            port.Dispose();
            throw;
        }
    }

    /// <exception cref="TimeoutException">Flow control held the write back past the write timeout.</exception>
    /// <exception cref="IOException">The port failed.</exception>
    /// <exception cref="InvalidOperationException">The port is closed.</exception>
    internal void Write(byte[] bytes) => _port.Write(bytes, 0, bytes.Length);

    public async ValueTask DisposeAsync()
    {
        await Pipe.DisposeAsync().ConfigureAwait(false);
        try
        {
            _port.Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Closing a port whose adapter was unplugged can fail. There is nothing left to release.
        }
    }
}
