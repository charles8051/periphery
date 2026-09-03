// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.IO.Pipelines;

namespace Periphery.Serial;

/// <summary>
/// What the read pump should do about an exception a read threw.
/// </summary>
/// <remarks>
/// The BCL and RJCP backends disagree completely about this, which is why it is a parameter of
/// <see cref="SerialReadPump"/> rather than something the pump decides.
/// </remarks>
internal enum ReadDisposition
{
    /// <summary>
    /// Not an error. Keep reading. The <c>System.IO.Ports</c> backend's loop tick is an expired
    /// <c>ReadTimeout</c>, which arrives as an exception several times a second on an idle port.
    /// The RJCP backend has no benign exception at all.
    /// </summary>
    Benign,

    /// <summary>
    /// A deliberate shutdown. Stop and complete the pipe cleanly, the way an ordinary end of
    /// stream would.
    /// </summary>
    Shutdown,

    /// <summary>
    /// The port died under us. Stop and complete the pipe with this exception so the consumer
    /// sees the real cause.
    /// </summary>
    Failure,
}

/// <summary>
/// The read loop both serial backends share: pull bytes off a stream, write them into a
/// <see cref="Pipe"/>, and complete that pipe in a way that tells the consumer why it ended.
/// </summary>
/// <remarks>
/// <para>
/// Everything backend-specific is a parameter. The two implementations differ in how a read is
/// issued, in how an exception from that read is classified, and in how disposal joins the pump
/// — nothing else. <see cref="Periphery.Serial.BclSerialDuplexPipe"/> blocks a dedicated thread
/// in the synchronous <c>Read</c>, because a real board proved <c>ReadAsync</c> cannot be trusted
/// to time out or cancel on Windows. <c>Periphery.Serial.Rjcp.SerialDuplexPipe</c> awaits
/// <c>ReadAsync</c> directly, because RJCP's own implementation does not have that problem.
/// </para>
/// <para>
/// Ported from <c>call-and-response</c> (commit <c>bb95838</c>, branch
/// <c>claude/bcl-serial-transport</c>, same author) — that branch predates the decision
/// (ADR-0062) to keep the serial backend split inside Periphery rather than the framing library,
/// and is not expected to land there.
/// </para>
/// </remarks>
internal static class SerialReadPump
{
    private const int BufferSize = 512;

    /// <param name="writer">The pipe to fill. The pump completes it and nothing else does.</param>
    /// <param name="readAsync">
    /// Issues one read. The RJCP backend awaits <c>Stream.ReadAsync</c> with the token; the
    /// <c>System.IO.Ports</c> backend blocks in the synchronous <c>Read</c> and ignores the
    /// token, because on Windows the async path honours neither cancellation nor
    /// <c>ReadTimeout</c>.
    /// </param>
    /// <param name="classify">
    /// Decides what an exception from <paramref name="readAsync"/> means. Write it narrow: a
    /// predicate one clause too wide turns a dead port into an indefinite hang, because the pump
    /// keeps reading and the consumer never learns the port is gone. When in doubt return
    /// <see cref="ReadDisposition.Failure"/> and let the exception through.
    /// </param>
    /// <param name="token">Cancelled by disposal to ask the pump to stop.</param>
    internal static async Task RunAsync(
        PipeWriter writer,
        Func<byte[], CancellationToken, ValueTask<int>> readAsync,
        Func<Exception, ReadDisposition> classify,
        CancellationToken token)
    {
        var readBuffer = new byte[BufferSize];

        // Non-null once the port has failed. Handed to Complete so the reader sees the real
        // cause instead of an end of stream indistinguishable from a clean close.
        Exception? failure = null;

        try
        {
            while (!token.IsCancellationRequested)
            {
                int bytesRead;
                try
                {
                    bytesRead = await readAsync(readBuffer, token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    var disposition = classify(ex);

                    // A benign exception is the loop tick, not an event. Nothing is written
                    // and nothing is recorded; go straight back to reading.
                    if (disposition == ReadDisposition.Benign) continue;

                    if (disposition == ReadDisposition.Failure) failure = ex;
                    break;
                }

                if (bytesRead == 0) break;

                readBuffer.AsSpan(0, bytesRead).CopyTo(writer.GetSpan(bytesRead));
                writer.Advance(bytesRead);

                var flush = await writer.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                if (flush.IsCompleted || flush.IsCanceled) break;
            }
        }
        finally
        {
            writer.Complete(failure);
        }
    }
}
