using System.IO.Pipelines;

namespace Periphery.Bootloader.Stm32.Serial.Tests;

/// <summary>
/// The autobaud mis-lock: a part that is alive, correct, and answering every command — at a rate
/// ~12% below the one the host is driving, so none of it decodes.
/// </summary>
/// <remarks>
/// Found on a panel of STM32G431s on 2026-09-03. Boards would reach a state where flashing never
/// started, and a capture showed the part answering NACKs and a complete, well-formed Get reply
/// the whole time — every byte flagged with a parity or framing error, because it was transmitting
/// at 102400 while the host drove 115200.
/// <para>
/// The cause is ours. AN3155 §3.1 derives the bit period from the 0x7F sync byte, whose second
/// falling edge is 8 bit times after the first; the bootloader divides the span by 8. The
/// handshake's recovery path sends 0xFF, whose second falling edge is the parity bit at 9 bit
/// times, so a part that comes out of reset with one of those on the line locks to 8/9 of the
/// rate and stays there — autobaud happens once per reset. The old code reported that as "part
/// missing", which sent an operator looking for a wiring fault that was not there.
/// </para>
/// </remarks>
public class Stm32SerialMislockTests
{
    private static readonly DeviceInfo Device = new()
    {
        Id = "stm32-uart",
        PortName = new SerialPortName("COM7"),
    };

    private static readonly Stm32SerialOptions Quick = Stm32SerialOptions.Default with
    {
        SyncTimeout = TimeSpan.FromMilliseconds(50),
        CommandTimeout = TimeSpan.FromMilliseconds(50),
        SyncSettle = TimeSpan.FromMilliseconds(5),
        SyncSettleBudget = TimeSpan.FromMilliseconds(50),
    };

    /// <summary>115200 × 8/9, the rate a 0xFF-measured autobaud lands on.</summary>
    private const int MislockedBaud = 115200 * 8 / 9;

    [Fact]
    public void The_bit_slip_reproduces_the_captured_get_reply_exactly()
    {
        // The evidence the diagnosis rests on. Left: what an STM32G431 (bootloader 3.1) sends for
        // Get. Right: what the analyser — and the host's UART — recorded while the part was
        // locked to 102400 and everything else was reading at 115200. If this mapping is ever
        // wrong, the reasoning behind the whole mis-lock story is wrong with it.
        byte[] sent =
        {
            0x79, 0x0B, 0x31, 0x00, 0x01, 0x02, 0x11, 0x21, 0x31, 0x44, 0x63, 0x73, 0x82, 0x92, 0x79,
        };
        byte[] captured =
        {
            0xF1, 0x13, 0x61, 0x00, 0x01, 0x02, 0x21, 0x41, 0x61, 0x8C, 0xC3, 0xE3, 0x02, 0x22, 0xF1,
        };

        var misread = sent
            .Select(b => FakeStm32Bootloader.MisreadAtWrongBaud(b, MislockedBaud, 115200))
            .ToArray();

        Assert.Equal(captured, misread);
    }

    [Fact]
    public void A_nack_misreads_as_the_byte_that_kept_appearing_in_the_capture()
    {
        // Every isolated 0x3F in the capture was a NACK — the correct answer from an
        // already-synced part to a 0x7F, which is what made "it is responding" true all along.
        Assert.Equal(0x3F, FakeStm32Bootloader.MisreadAtWrongBaud(0x1F, MislockedBaud, 115200));
    }

    [Fact]
    public async Task Sync_reports_the_mislock_rather_than_a_missing_part()
    {
        await using var device = new FakeStm32Bootloader
        {
            StartSynced = true,
            MislockedBaudRate = MislockedBaud,
        };

        var ex = await Assert.ThrowsAsync<Stm32SerialException>(
            () => Stm32SerialProgrammer.ConnectAsync(
                Device, device, Quick, setBaudRate: b => device.HostBaudRate = b));

        Assert.Contains($"autobauded to {MislockedBaud}", ex.Message);
        Assert.Contains("reset the part", ex.Message);
    }

    [Fact]
    public async Task Sync_puts_the_port_back_on_the_intended_rate()
    {
        await using var device = new FakeStm32Bootloader
        {
            StartSynced = true,
            MislockedBaudRate = MislockedBaud,
        };

        await Assert.ThrowsAsync<Stm32SerialException>(
            () => Stm32SerialProgrammer.ConnectAsync(
                Device, device, Quick, setBaudRate: b => { device.BaudRatesSeen.Add(b); device.HostBaudRate = b; }));

        // It has to probe the suspect rate to prove anything, but the caller owns this port and
        // did not ask for it to be left retuned.
        Assert.Contains(MislockedBaud, device.BaudRatesSeen);
        Assert.Equal(Quick.BaudRate, device.HostBaudRate);
    }

    [Fact]
    public async Task Sync_reports_a_missing_part_when_nothing_answers_at_either_rate()
    {
        // The check must not turn every silence into a mis-lock diagnosis. A part that is simply
        // not there answers at neither rate, and the generic failure is the honest one.
        //
        // It must also not slow that case down. An empty fixture port is what a probe loop spends
        // almost all of its time looking at, and retuning to re-prove a rate nothing is listening
        // on would add seconds to every cycle for no information. A mis-locked part is never
        // silent — it answers, unintelligibly — so the absence of any byte at all is enough to
        // skip the whole check.
        var retunes = new List<int>();
        await using var device = new SilentDevice();

        var ex = await Assert.ThrowsAsync<Stm32SerialException>(
            () => Stm32SerialProgrammer.ConnectAsync(
                Device, device, Quick, setBaudRate: retunes.Add));

        Assert.Contains("no answer to the AN3155 sync byte", ex.Message);
        Assert.DoesNotContain("autobauded", ex.Message);
        Assert.Empty(retunes);
    }

    [Fact]
    public async Task Sync_reports_a_missing_part_when_the_caller_gave_no_way_to_retune()
    {
        // A caller that owns only a pipe cannot change the rate, so the check cannot run. Saying
        // so by falling back to the generic failure beats guessing.
        await using var device = new FakeStm32Bootloader
        {
            StartSynced = true,
            MislockedBaudRate = MislockedBaud,
        };

        var ex = await Assert.ThrowsAsync<Stm32SerialException>(
            () => Stm32SerialProgrammer.ConnectAsync(Device, device, Quick));

        Assert.Contains("no answer to the AN3155 sync byte", ex.Message);
    }

    [Fact]
    public async Task Garbled_bytes_on_the_line_do_not_pass_for_a_reply()
    {
        // The mis-locked part is not quiet — it is talking, and what arrives is the wreckage of a
        // real Get reply. None of it may be mistaken for an answer at the rate we are driving.
        await using var device = new FakeStm32Bootloader
        {
            StartSynced = true,
            MislockedBaudRate = MislockedBaud,
        };

        await device.InjectNoiseAsync(
            0xF1, 0x13, 0x61, 0x00, 0x01, 0x02, 0x21, 0x41, 0x61, 0x8C, 0xC3, 0xE3, 0x02, 0x22, 0xF1);

        var ex = await Assert.ThrowsAsync<Stm32SerialException>(
            () => Stm32SerialProgrammer.ConnectAsync(
                Device, device, Quick, setBaudRate: b => device.HostBaudRate = b));

        Assert.Contains("autobauded", ex.Message);
    }

    /// <summary>A port with nothing on the far end: reads never complete, writes go nowhere.</summary>
    private sealed class SilentDevice : IDuplexPipe, IAsyncDisposable
    {
        private readonly Pipe _toHost = new();
        private readonly Pipe _toDevice = new();

        public PipeReader Input => _toHost.Reader;
        public PipeWriter Output => _toDevice.Writer;

        public ValueTask DisposeAsync()
        {
            _toHost.Writer.Complete();
            _toDevice.Writer.Complete();
            return ValueTask.CompletedTask;
        }
    }
}
