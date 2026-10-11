// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Periphery.Serial.Browser.Core;

namespace Periphery.Serial.Browser;

/// <summary>
/// The calls <see cref="BrowserSerialPort"/> makes on one granted Web Serial <c>SerialPort</c>. In a
/// browser each forwards to the package's JS module; tests supply a fake.
/// </summary>
/// <remarks>
/// A rejected promise faults the task with <see cref="WebSerialRejectionException"/>. A method that
/// takes data copies it before it returns.
/// </remarks>
internal interface IWebSerialPort
{
    /// <summary><c>open(options)</c>, with the options as <c>SerialOptions</c> JSON.</summary>
    Task OpenAsync(string optionsJson);

    /// <summary>
    /// Reads up to <paramref name="destination"/>'s length from the port's readable stream. Returns
    /// the count, or 0 when the stream has ended because the port closed. A line error such as a
    /// parity error loses its bytes and the read goes on.
    /// </summary>
    Task<int> ReadAsync(Memory<byte> destination);

    /// <summary>Writes all of <paramref name="data"/> to the port's writable stream.</summary>
    Task WriteAsync(ReadOnlyMemory<byte> data);

    /// <summary><c>setSignals(signals)</c>, with the signals as <c>SerialOutputSignals</c> JSON.</summary>
    Task SetSignalsAsync(string signalsJson);

    /// <summary>
    /// Aborts a pending write, which then fails, cancels a pending read, which then returns 0,
    /// releases the streams, and calls <c>close()</c>. Neither pending call has to finish first.
    /// </summary>
    Task CloseAsync();
}

/// <summary>A Web Serial promise rejected.</summary>
internal sealed class WebSerialRejectionException : Exception
{
    public WebSerialRejectionException(WebSerialRejection rejection)
        : base($"{rejection.Name}: {rejection.Message}")
    {
        Rejection = rejection;
    }

    public WebSerialRejection Rejection { get; }
}
