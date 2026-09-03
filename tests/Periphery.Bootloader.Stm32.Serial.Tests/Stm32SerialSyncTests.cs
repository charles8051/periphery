using System.IO.Pipelines;

namespace Periphery.Bootloader.Stm32.Serial.Tests;

/// <summary>
/// The AN3155 handshake, against a device that models what real silicon does with a sync byte.
/// <para>
/// These exist because the original <c>SyncAsync</c> was wrong and nothing could catch it. It was
/// private and reachable only through <c>OpenAsync</c>, which needs a real port, so no pipe test
/// touched it — and <see cref="FakeStm32Bootloader"/> answered a second sync byte with an
/// immediate NACK, which is the same misreading of section 3.1 the shell had. Code and emulator
/// agreed with each other and both disagreed with the part. Found on an STM32G431 (PID 0x468,
/// bootloader 3.1) on 2026-09-02: the first flash attempt against hardware timed out.
/// </para>
/// </summary>
public class Stm32SerialSyncTests
{
    private static readonly DeviceInfo Device = new()
    {
        Id = "stm32-uart",
        PortName = new SerialPortName("COM7"),
    };

    // Short: the already-synced path is proven by a sync-byte timeout, and it is paid every time.
    private static readonly Stm32SerialOptions Quick = Stm32SerialOptions.Default with
    {
        SyncTimeout = TimeSpan.FromMilliseconds(250),
        CommandTimeout = TimeSpan.FromMilliseconds(250),
    };

    [Fact]
    public async Task Sync_succeeds_on_a_part_that_has_not_synced_since_reset()
    {
        // The easy case, and the only one that ever worked: 0x7F drives autobaud, part ACKs.
        await using var device = new FakeStm32Bootloader();
        await using var programmer = new Stm32SerialProgrammer(Device, device, Quick);

        await programmer.SyncAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Sync_succeeds_on_a_part_that_is_already_in_its_command_loop()
    {
        // The case that failed on hardware. The part is synced, so it takes 0x7F as an opcode and
        // says nothing, waiting for the complement. The old shell timed out here and reported the
        // part missing — on a part that was answering Get and Get ID perfectly.
        await using var device = new FakeStm32Bootloader { StartSynced = true };
        await using var programmer = new Stm32SerialProgrammer(Device, device, Quick);

        await programmer.SyncAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Sync_leaves_an_already_synced_part_on_a_clean_command_boundary()
    {
        // The repair, and the reason completing the frame matters more than reporting success:
        // the pending opcode has to be consumed, or the next command's first byte completes it
        // instead and every reply after that is off by a frame.
        await using var device = new FakeStm32Bootloader { StartSynced = true, ProductId = 0x0468 };
        await using var programmer = new Stm32SerialProgrammer(Device, device, Quick);

        await programmer.SyncAsync(CancellationToken.None);
        var identity = await programmer.IdentifyAsync();

        Assert.Equal("0x468", identity.Chip);
        Assert.Equal("3.1", identity.BootloaderVersion);
    }

    [Fact]
    public async Task Sync_fails_when_nothing_answers_either_byte()
    {
        // A dead line: not in the bootloader, wrong port, or RX/TX swapped. Silence to the sync
        // byte AND to the completed frame is the only thing that may fail.
        var pipe = new SilentPipe();
        await using var programmer = new Stm32SerialProgrammer(Device, pipe, Quick);

        var ex = await Assert.ThrowsAsync<Stm32SerialException>(
            () => programmer.SyncAsync(CancellationToken.None));

        Assert.Contains("did not answer Get", ex.Message);
    }

    [Fact]
    public async Task Sync_survives_an_ACK_that_lands_after_the_deadline()
    {
        // The race the first fix had. A fresh part whose ACK is merely late looks exactly like a
        // synced part holding our byte — and the two need opposite repairs. Inferring from the
        // next single byte got this wrong in the dangerous direction: it reported success while a
        // byte sat pending, and the next command desynchronised. Proving the boundary with Get
        // makes the distinction unnecessary.
        await using var device = new FakeStm32Bootloader
        {
            SyncAckDelay = TimeSpan.FromMilliseconds(400),   // vs the 250 ms sync deadline below
        };
        await using var programmer = new Stm32SerialProgrammer(Device, device, Quick);

        await programmer.SyncAsync(CancellationToken.None);

        // The proof that matters is not that Sync returned — it is that the session is usable.
        var identity = await programmer.IdentifyAsync();
        Assert.Equal("3.1", identity.BootloaderVersion);
    }

    [Fact]
    public async Task Sync_absorbs_bytes_that_trickle_in_after_the_first_drain()
    {
        // Waiting a fixed interval and then draining once assumes the line is quiet by then, and
        // nothing in AN3155 promises that. A reply delayed between its own bytes puts its head in
        // front of that drain and its tail behind it, and the tail is then read as the answer to
        // whatever goes out next. Draining until a whole window passes with nothing arriving is
        // evidence of a quiet line; an elapsed interval is only an assumption.
        await using var device = new FakeStm32Bootloader { StartSynced = true, ProductId = 0x0468 };
        await using var programmer = new Stm32SerialProgrammer(Device, device, Stm32SerialOptions.Default with
        {
            SyncTimeout = TimeSpan.FromMilliseconds(200),
            CommandTimeout = TimeSpan.FromMilliseconds(500),
            SyncSettle = TimeSpan.FromMilliseconds(100),
            SyncSettleBudget = TimeSpan.FromSeconds(3),
        });

        // Dribble stale bytes across several settle windows while the handshake is running.
        using var trickling = new CancellationTokenSource();
        var trickle = Task.Run(async () =>
        {
            // Start after the sync byte's own deadline has passed, so these are bytes arriving
            // during recovery rather than an answer to the sync byte itself.
            await Task.Delay(260, trickling.Token);
            for (int i = 0; i < 5 && !trickling.IsCancellationRequested; i++)
            {
                await device.InjectNoiseAsync(0x00);
                await Task.Delay(60, trickling.Token);
            }
        });

        await programmer.SyncAsync(CancellationToken.None);
        trickling.Cancel();
        try { await trickle; } catch (OperationCanceledException) { }

        // What matters is that the session is usable afterwards, not that Sync returned.
        var identity = await programmer.IdentifyAsync();
        Assert.Equal("0x468", identity.Chip);
    }

    [Fact]
    public async Task Sync_reports_a_transport_failure_rather_than_calling_it_silence()
    {
        // A closed port or a pulled cable is not a quiet part, and the two need opposite
        // handling: silence is what recovery exists for, a dead transport is not recoverable and
        // must reach the caller as itself. Treating every exception as silence sent recovery
        // bytes into a dead stream and then blamed the part for being missing.
        await using var pipe = new DeadPipe();
        await using var programmer = new Stm32SerialProgrammer(Device, pipe, Quick);

        var ex = await Assert.ThrowsAsync<Stm32SerialException>(
            () => programmer.SyncAsync(CancellationToken.None));

        Assert.DoesNotContain("did not answer Get", ex.Message);
    }

    [Fact]
    public async Task Sync_fails_on_a_junk_answer()
    {
        // Something is talking, but it is not an AN3155 bootloader at this baud. Note this needs a
        // device that *answers* junk: bytes already sitting on the line when we open are drained
        // by WithTimeout, deliberately, so pre-existing noise never reaches the sync read.
        await using var pipe = new AnsweringPipe(0x5A);
        await using var programmer = new Stm32SerialProgrammer(Device, pipe, Quick);

        var ex = await Assert.ThrowsAsync<Stm32SerialException>(
            () => programmer.SyncAsync(CancellationToken.None));

        Assert.Contains("0x5A", ex.Message);
    }

    /// <summary>A pipe that answers one fixed byte to every byte written — a talkative non-target.</summary>
    private sealed class AnsweringPipe : IDuplexPipe, IAsyncDisposable
    {
        private readonly Pipe _in = new();
        private readonly Pipe _out = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        public AnsweringPipe(byte answer) => _loop = Task.Run(async () =>
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var read = await _out.Reader.ReadAsync(_cts.Token);
                    if (read.Buffer.Length > 0)
                    {
                        for (long i = 0; i < read.Buffer.Length; i++)
                        {
                            _in.Writer.GetSpan(1)[0] = answer;
                            _in.Writer.Advance(1);
                        }
                        await _in.Writer.FlushAsync(_cts.Token);
                    }
                    _out.Reader.AdvanceTo(read.Buffer.End);
                    if (read.IsCompleted) break;
                }
            }
            catch (OperationCanceledException) { }
        });

        public PipeReader Input => _in.Reader;
        public PipeWriter Output => _out.Writer;

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try { await _loop; } catch (OperationCanceledException) { }
            _cts.Dispose();
        }
    }

    /// <summary>A pipe whose far end is gone — reads complete immediately, as on a closed port.</summary>
    private sealed class DeadPipe : IDuplexPipe, IAsyncDisposable
    {
        private readonly Pipe _in = new();
        private readonly Pipe _out = new();

        public DeadPipe() => _in.Writer.Complete();

        public PipeReader Input => _in.Reader;
        public PipeWriter Output => _out.Writer;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>A pipe that accepts writes and never answers.</summary>
    private sealed class SilentPipe : IDuplexPipe
    {
        private readonly Pipe _in = new();
        private readonly Pipe _out = new();
        public PipeReader Input => _in.Reader;
        public PipeWriter Output => _out.Writer;
    }
}
