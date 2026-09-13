using System.IO.Pipelines;

namespace Periphery.Bootloader.Stm32.Serial.Tests;

/// <summary>
/// The runtime behaviour the simulated sync tests rest on, asserted directly so that a change in it
/// fails here, by name, instead of as a confusing failure inside the handshake tests.
/// </summary>
/// <remarks>
/// Those tests advance a fake clock whenever the handshake is not finished, which is only right if
/// nothing runnable is left at that moment. That holds because a reply written into an inline pipe
/// runs the programmer's awaiting code on the writer's call stack, through several nested awaits,
/// before the write returns. .NET does that only when no <see cref="SynchronizationContext"/> is
/// current, which is why they run inside <c>Task.Run</c>.
/// </remarks>
public class SimulationPreconditionTests
{
    private static Pipe InlinePipe() =>
        new(
            new PipeOptions(
                readerScheduler: PipeScheduler.Inline,
                writerScheduler: PipeScheduler.Inline,
                useSynchronizationContext: false
            )
        );

    [Fact]
    public Task AReplyWrittenIntoAnInlinePipe_RunsANestedAwaitingReaderBeforeTheWriteReturns() =>
        Task.Run(async () =>
        {
            Assert.Null(SynchronizationContext.Current);

            var pipe = InlinePipe();
            using var cts = new CancellationTokenSource();
            bool continued = false;
            var reading = OuterAsync(pipe.Reader, cts.Token, () => continued = true);

            await pipe.Writer.WriteAsync(new byte[] { 0x79 });

            Assert.True(
                continued,
                "the reader's nested await did not continue inline, so the simulated sync tests would advance the clock past pending work"
            );
            await reading;
        });

    private static async Task OuterAsync(PipeReader reader, CancellationToken ct, Action after)
    {
        _ = await InnerAsync(reader, ct).ConfigureAwait(false);
        after();
    }

    private static async Task<byte> InnerAsync(PipeReader reader, CancellationToken ct)
    {
        var result = await reader.ReadAsync(ct).ConfigureAwait(false);
        byte value = result.Buffer.FirstSpan[0];
        reader.AdvanceTo(result.Buffer.GetPosition(1));
        return value;
    }
}
