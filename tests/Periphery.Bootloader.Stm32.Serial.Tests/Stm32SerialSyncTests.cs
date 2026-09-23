using System.IO.Pipelines;
using Periphery.Testing;

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

    // Short: the already-synced path is proven by a sync-byte timeout.
    private static readonly Stm32SerialOptions Quick = Stm32SerialOptions.Default with
    {
        SyncTimeout = TimeSpan.FromMilliseconds(250),
        CommandTimeout = TimeSpan.FromMilliseconds(250),
    };

    [Fact]
    public Task Sync_succeeds_on_a_part_that_has_not_synced_since_reset() => Simulation.RunAsync(async time =>
    {
        // The easy case, and the only one that ever worked: 0x7F drives autobaud, part ACKs.
        await using var device = new FakeStm32Bootloader(timeProvider: time);
        await using var programmer = new Stm32SerialProgrammer(Device, device, Quick with { TimeProvider = time });

        await Simulation.DriveAsync(time, programmer.SyncAsync(CancellationToken.None));
    });

    [Fact]
    public Task Sync_succeeds_on_a_part_that_is_already_in_its_command_loop() => Simulation.RunAsync(async time =>
    {
        // The case that failed on hardware. The part is synced, so it takes 0x7F as an opcode and
        // says nothing, waiting for the complement. The old shell timed out here and reported the
        // part missing — on a part that was answering Get and Get ID perfectly.
        await using var device = new FakeStm32Bootloader(timeProvider: time) { StartSynced = true };
        await using var programmer = new Stm32SerialProgrammer(Device, device, Quick with { TimeProvider = time });

        await Simulation.DriveAsync(time, programmer.SyncAsync(CancellationToken.None));
    });

    [Fact]
    public Task Sync_leaves_an_already_synced_part_on_a_clean_command_boundary() => Simulation.RunAsync(async time =>
    {
        // The repair, and the reason completing the frame matters more than reporting success:
        // the pending opcode has to be consumed, or the next command's first byte completes it
        // instead and every reply after that is off by a frame.
        await using var device = new FakeStm32Bootloader(timeProvider: time) { StartSynced = true, ProductId = 0x0468 };
        await using var programmer = new Stm32SerialProgrammer(Device, device, Quick with { TimeProvider = time });

        await Simulation.DriveAsync(time, programmer.SyncAsync(CancellationToken.None));
        var identity = await Simulation.DriveAsync(time, programmer.IdentifyAsync());

        Assert.Equal("0x468", identity.Chip);
        Assert.Equal("3.1", identity.BootloaderVersion);
    });

    [Fact]
    public Task Sync_fails_when_nothing_answers_either_byte() => Simulation.RunAsync(async time =>
    {
        // A dead line: not in the bootloader, wrong port, or RX/TX swapped. Silence to the sync
        // byte AND to the completed frame is the only thing that may fail.
        var pipe = new SilentPipe();
        await using var programmer = new Stm32SerialProgrammer(Device, pipe, Quick with { TimeProvider = time });

        var ex = await Assert.ThrowsAsync<Stm32SerialException>(
            () => Simulation.DriveAsync(time, programmer.SyncAsync(CancellationToken.None)));

        Assert.Contains("did not answer Get", ex.Message);
    });

    [Fact]
    public async Task Sync_survives_an_ACK_that_lands_after_the_deadline()
    {
        // The race the first fix had. A fresh part whose ACK is merely late looks exactly like a
        // synced part holding our byte — and the two need opposite repairs. Inferring from the
        // next single byte got this wrong in the dangerous direction: it reported success while a
        // byte sat pending, and the next command desynchronised. Proving the boundary with Get
        // makes the distinction unnecessary.
        await Simulation.RunAsync(async time =>
        {
            await using var device = new FakeStm32Bootloader(timeProvider: time)
            {
                SyncAckDelay = TimeSpan.FromMilliseconds(400),   // vs the 250 ms sync deadline below
            };
            await using var programmer = new Stm32SerialProgrammer(Device, device, Quick with { TimeProvider = time });

            await Simulation.DriveAsync(time, programmer.SyncAsync(CancellationToken.None));

            // The scenario, not an accident of scheduling: the sync deadline was armed on this clock
            // before the part began holding its ACK back, so the ACK really was late.
            Assert.Equal([Quick.SyncTimeout, TimeSpan.FromMilliseconds(400)], time.ArmedDueTimes.Take(2));

            // The proof that matters is not that Sync returned — it is that the session is usable.
            var identify = programmer.IdentifyAsync();
            await Simulation.DriveAsync(time, identify);
            Assert.Equal("3.1", (await identify).BootloaderVersion);
        });
    }

    [Fact]
    public async Task Sync_absorbs_bytes_that_trickle_in_after_the_first_drain()
    {
        // Waiting a fixed interval and then draining once assumes the line is quiet by then, and
        // nothing in AN3155 promises that. A reply delayed between its own bytes puts its head in
        // front of that drain and its tail behind it, and the tail is then read as the answer to
        // whatever goes out next. Draining until a whole window passes with nothing arriving is
        // evidence of a quiet line; an elapsed interval is only an assumption.
        await Simulation.RunAsync(async time =>
        {
            await using var device = new FakeStm32Bootloader(timeProvider: time) { StartSynced = true, ProductId = 0x0468 };
            await using var programmer = new Stm32SerialProgrammer(Device, device, Stm32SerialOptions.Default with
            {
                SyncTimeout = TimeSpan.FromMilliseconds(200),
                CommandTimeout = TimeSpan.FromMilliseconds(500),
                SyncSettle = TimeSpan.FromMilliseconds(100),
                SyncSettleBudget = TimeSpan.FromSeconds(3),
                TimeProvider = time,
            });

            // Dribble stale bytes across several settle windows while the handshake is running, on the
            // same clock, so where each byte lands relative to the windows is fixed rather than a race.
            using var trickling = new CancellationTokenSource();
            var trickle = TrickleAsync();
            async Task TrickleAsync()
            {
                // Start after the sync byte's own deadline has passed, so these are bytes arriving
                // during recovery rather than an answer to the sync byte itself.
                await Task.Delay(TimeSpan.FromMilliseconds(260), time, trickling.Token);
                for (int i = 0; i < 5; i++)
                {
                    await device.InjectNoiseAsync(0x00);
                    await Task.Delay(TimeSpan.FromMilliseconds(60), time, trickling.Token);
                }
            }

            await Simulation.DriveAsync(time, programmer.SyncAsync(CancellationToken.None));
            trickling.Cancel();
            try { await trickle; } catch (OperationCanceledException) { }

            // The boundary was proved with Get, once, rather than inferred from whatever byte arrived.
            // This fake answers instantly, so trickled bytes never land inside a reply and cannot
            // show whether the settle drained until quiet; the chattering-line test below does.
            Assert.Equal(1, device.GetsAnswered);

            // What matters is that the session is usable afterwards, not that Sync returned.
            var identify = programmer.IdentifyAsync();
            await Simulation.DriveAsync(time, identify);
            Assert.Equal("0x468", (await identify).Chip);
        });
    }

    [Fact]
    public async Task Sync_refuses_to_transmit_into_a_line_that_never_falls_quiet()
    {
        // Draining until idle only helps if idle ever happens. When the budget runs out with
        // bytes still arriving, the old code transmitted anyway — which is precisely the
        // interleaving the settle exists to prevent, and no answer coming back could then be
        // attributed to what we sent. Refusing is the only honest option; the error names the
        // likely causes rather than blaming the part for not answering.
        await Simulation.RunAsync(async time =>
        {
            await using var pipe = new ChatteringPipe(time, silenceAfterFirstWrite: TimeSpan.FromMilliseconds(250));
            await using var programmer = new Stm32SerialProgrammer(Device, pipe, Stm32SerialOptions.Default with
            {
                SyncTimeout = TimeSpan.FromMilliseconds(200),
                CommandTimeout = TimeSpan.FromMilliseconds(200),
                SyncSettle = TimeSpan.FromMilliseconds(100),
                SyncSettleBudget = TimeSpan.FromMilliseconds(700),
                TimeProvider = time,
            });

            // Which correct failure surfaces depends on when noise lands relative to the sync deadline:
            // noise reaching the sync read is a junk answer, noise arriving during recovery is a line
            // that never fell quiet. On a real clock that was a race, and pinning the message made a
            // loaded CI runner fail a passing implementation. On this clock the chatter starts 50 ms
            // after the 200 ms sync deadline on every run, so the failure is the settle giving up, and
            // the message can say so.
            var ex = await Assert.ThrowsAsync<Stm32SerialException>(
                () => Simulation.DriveAsync(time, programmer.SyncAsync(CancellationToken.None)));
            Assert.Contains("never fell quiet", ex.Message);
        });
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
    public Task Sync_fails_on_a_junk_answer() => Simulation.RunAsync(async time =>
    {
        // Something is talking, but it is not an AN3155 bootloader at this baud. Note this needs a
        // device that *answers* junk: bytes already sitting on the line when we open are drained
        // by WithTimeout, deliberately, so pre-existing noise never reaches the sync read.
        await using var pipe = new AnsweringPipe(0x5A);
        await using var programmer = new Stm32SerialProgrammer(Device, pipe, Quick with { TimeProvider = time });

        var ex = await Assert.ThrowsAsync<Stm32SerialException>(
            () => Simulation.DriveAsync(time, programmer.SyncAsync(CancellationToken.None)));

        Assert.Contains("0x5A", ex.Message);
    });

    /// <summary>A pipe that answers one fixed byte to every byte written — a talkative non-target.</summary>
    private sealed class AnsweringPipe : IDuplexPipe, IAsyncDisposable
    {
        private readonly Pipe _in = new(Simulation.InlinePipes);
        private readonly Pipe _out = new(Simulation.InlinePipes);
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        // Started on the calling thread, like FakeStm32Bootloader on a clock: with inline pipes each
        // answer is written before the write that prompted it returns.
        public AnsweringPipe(byte answer) => _loop = AnswerAsync(answer);

        private async Task AnswerAsync(byte answer)
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
        }

        public PipeReader Input => _in.Reader;
        public PipeWriter Output => _out.Writer;

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try { await _loop; } catch (OperationCanceledException) { }
            _cts.Dispose();
        }
    }

    /// <summary>
    /// Stays silent until the sync byte has been sent and its deadline has passed, then transmits
    /// every 10 ms — a device talking unprompted, a wrong baud rate turning noise into bytes, or
    /// another program on the same port.
    /// <para>
    /// The chatter is anchored to the first byte written, and paced on the test's fake clock with
    /// inline pipes, so every 100 ms settle window sees bytes on every run. Pacing it on the real
    /// clock is what failed on a loaded CI runner.
    /// </para>
    /// </summary>
    private sealed class ChatteringPipe : IDuplexPipe, IAsyncDisposable
    {
        private static readonly PipeOptions Inline =
            new(readerScheduler: PipeScheduler.Inline, writerScheduler: PipeScheduler.Inline, useSynchronizationContext: false);

        private readonly Pipe _in = new(Inline);
        private readonly Pipe _out = new(Inline);
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        public ChatteringPipe(TimeProvider time, TimeSpan silenceAfterFirstWrite) =>
            _loop = ChatterAsync(time, silenceAfterFirstWrite);

        private async Task ChatterAsync(TimeProvider time, TimeSpan silenceAfterFirstWrite)
        {
            try
            {
                // Wait for the sync byte itself, then out-wait its deadline.
                var first = await _out.Reader.ReadAsync(_cts.Token);
                _out.Reader.AdvanceTo(first.Buffer.End);
                await Task.Delay(silenceAfterFirstWrite, time, _cts.Token);

                while (!_cts.IsCancellationRequested)
                {
                    _in.Writer.GetSpan(1)[0] = 0x5A;
                    _in.Writer.Advance(1);
                    await _in.Writer.FlushAsync(_cts.Token);
                    await Task.Delay(TimeSpan.FromMilliseconds(10), time, _cts.Token);
                }
            }
            catch (OperationCanceledException) { }
        }

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
        private readonly Pipe _in = new(Simulation.InlinePipes);
        private readonly Pipe _out = new(Simulation.InlinePipes);
        public PipeReader Input => _in.Reader;
        public PipeWriter Output => _out.Writer;
    }
}
