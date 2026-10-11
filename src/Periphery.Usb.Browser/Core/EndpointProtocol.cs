// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Usb.Browser.Core;

/// <summary>The shell's token for one call into the backend: one read, write or control transfer.</summary>
internal readonly record struct CallerId(int Value);

/// <summary>An endpoint machine's token for one WebUSB transfer it asked the shell to issue.</summary>
internal readonly record struct TransferId(int Value);

/// <summary>How a WebUSB transfer settled, as the shell reports it to an endpoint machine.</summary>
internal abstract record TransferResult
{
    private TransferResult() { }

    /// <summary>
    /// The transfer completed. <paramref name="Count"/> is the bytes moved. For an IN transfer,
    /// <paramref name="Data"/> holds them; for an OUT transfer it is empty.
    /// </summary>
    internal sealed record Ok(int Count, ReadOnlyMemory<byte> Data) : TransferResult
    {
        /// <summary>A completed IN transfer that returned <paramref name="data"/>.</summary>
        public static Ok Read(ReadOnlyMemory<byte> data) => new(data.Length, data);

        /// <summary>A completed OUT transfer that wrote <paramref name="count"/> bytes.</summary>
        public static Ok Written(int count) => new(count, ReadOnlyMemory<byte>.Empty);
    }

    /// <summary>
    /// The transfer failed. The machines never inspect <paramref name="Error"/>; they route it to
    /// a caller or hold it.
    /// </summary>
    internal sealed record Fault(Exception Error) : TransferResult;
}

/// <summary>An event the shell feeds to an endpoint machine.</summary>
internal abstract record EndpointInput
{
    private EndpointInput() { }

    /// <summary>
    /// A caller asks for a transfer. <paramref name="Length"/> is the caller's buffer length: the
    /// most bytes it can take for a read, or the bytes it sends for a write.
    /// </summary>
    internal sealed record Requested(CallerId Caller, int Length) : EndpointInput;

    /// <summary>The WebUSB transfer <paramref name="Transfer"/> settled.</summary>
    internal sealed record Settled(TransferId Transfer, TransferResult Result) : EndpointInput;

    /// <summary>
    /// The caller's token fired, from a cancellation or a deadline. The machine does not tell the
    /// two apart; <see cref="UsbDevice"/> does.
    /// </summary>
    internal sealed record Cancelled(CallerId Caller) : EndpointInput;

    /// <summary>The backend is being disposed.</summary>
    internal sealed record Disposed : EndpointInput
    {
        public static Disposed Instance { get; } = new();
    }
}

/// <summary>An instruction an endpoint machine gives the shell.</summary>
internal abstract record EndpointOutput
{
    private EndpointOutput() { }

    /// <summary>
    /// Start a WebUSB transfer of <paramref name="Length"/> bytes for <paramref name="Caller"/>, and
    /// report how it settles as <see cref="EndpointInput.Settled"/> with <paramref name="Transfer"/>.
    /// </summary>
    /// <remarks>
    /// On an ordered endpoint the transfer carries <paramref name="Caller"/>'s request. On an IN
    /// endpoint a later caller may take it over, so its result is routed by
    /// <paramref name="Transfer"/>, never by <paramref name="Caller"/>.
    /// </remarks>
    internal sealed record Issue(TransferId Transfer, CallerId Caller, int Length) : EndpointOutput;

    /// <summary>Complete the caller with <paramref name="Result"/>.</summary>
    internal sealed record Complete(CallerId Caller, TransferResult.Ok Result) : EndpointOutput;

    /// <summary>Fail the caller with <paramref name="Error"/>, a transfer's fault.</summary>
    internal sealed record Fail(CallerId Caller, Exception Error) : EndpointOutput;

    /// <summary>Complete the caller as cancelled. Its transfer, if one was issued, is abandoned.</summary>
    internal sealed record Cancel(CallerId Caller) : EndpointOutput;

    /// <summary>Fail the caller because the backend was disposed.</summary>
    internal sealed record FailDisposed(CallerId Caller) : EndpointOutput;

    /// <summary>
    /// Fail the caller because another caller is already using the endpoint. <see cref="UsbDevice"/>
    /// allows one transfer per endpoint at a time, so this means a caller bypassed it.
    /// </summary>
    internal sealed record RejectConcurrent(CallerId Caller) : EndpointOutput;
}
