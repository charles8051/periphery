using Periphery.Testing;

namespace Periphery.FlashAnything.Tests;

/// <summary>
/// The fixture loop (adr.md Decision 10): one flash per bound bridge per armed session by default,
/// with a succession of boards opt-in behind <c>--repeat</c>.
/// </summary>
/// <remarks>
/// The probe loop runs on a fake clock and is stepped one cycle at a time, so "the board leaves" is
/// exactly <see cref="ProbeRowPolicy.SilencesBeforeRemoved"/> silent cycles rather than a guess at how
/// many a real cadence fits into a delay.
/// </remarks>
public class ProbeRepeatTests
{
    private const string Family = "STM32 UART (AN3155)";

    private static DeviceInfo Bridge() => new()
    {
        Id = new DeviceId("USB-CP210X-COM7"),
        Name = "CP210x",
        VendorId = new HardwareId(0x10C4),
        ProductId = new HardwareId(0xEA60),
        SerialNumber = "92EA014C",
        LocationPath = "PCIROOT(0)#USB(1)",
        PortName = new SerialPortName("COM7"),
    };

    /// <summary>A probe provider whose answer the test flips to simulate boards coming and going.</summary>
    private sealed class SwitchableProvider : IBootloaderProvider
    {
        public volatile bool Answers;
        public string Name => Family;
        public IdentificationMode Identification => IdentificationMode.Probe;
        public bool CanHandle(DeviceInfo device) => device.PortName is not null;

        public Task<IFirmwareProgrammer> OpenAsync(DeviceInfo device, CancellationToken ct = default) =>
            Answers
                ? Task.FromResult<IFirmwareProgrammer>(new FakeFirmwareProgrammer(device))
                : throw new BootloaderException("nothing answered the sync byte");
    }

    private sealed record Rig(
        FlashAnythingService Svc, SwitchableProvider Provider, FakeMonitor Monitor, ProbeStepper Probes);

    private static async Task<string> TempBinAsync()
    {
        string path = Path.Combine(Path.GetTempPath(), $"probe-repeat-{Guid.NewGuid():N}.bin");
        await File.WriteAllBytesAsync(path, new byte[64]);
        return path;
    }

    private static Rig Build()
    {
        var provider = new SwitchableProvider();
        var registry = new BootloaderRegistry();
        registry.Register(provider);
        var monitor = new FakeMonitor();
        var time = new TimerSignalingFakeTimeProvider();
        var svc = new FlashAnythingService(registry, FakeDevices.Watcher(monitor)) { TimeProvider = time };
        return new Rig(svc, provider, monitor, new ProbeStepper(time));
    }

    // Arms on COM7 and waits for the loop's first probe, which finds nothing.
    private static async Task ArmedAsync(Rig rig, string firmware, RepeatMode repeat)
    {
        await rig.Svc.RefreshAsync();
        rig.Monitor.Plug(Bridge());
        await ServiceWait.UntilAsync(rig.Svc, s => s.Targets.Length == 1);
        await rig.Svc.LoadFirmwareAsync(firmware);
        await rig.Svc.DispatchAsync(new AppIntent.ArmAutoflash(
            Family, FlashOptions.Default, [new SerialPortName("COM7")], repeat));
        await rig.Probes.ParkedAsync();
    }

    // A board arrives and is flashed.
    private static async Task FlashFirstBoardAsync(Rig rig)
    {
        rig.Provider.Answers = true;
        await rig.Probes.StepAsync();
        await ServiceWait.UntilAsync(rig.Svc, s => s.AutoflashTally.Flashed >= 1);
    }

    // The board leaves: the loop stays silent until the row is retracted.
    private static async Task BoardLeavesAsync(Rig rig)
    {
        rig.Provider.Answers = false;
        await rig.Probes.StepAsync(ProbeRowPolicy.SilencesBeforeRemoved);
        Assert.Empty(rig.Svc.State.Targets);
    }

    [Fact]
    public async Task Without_repeat_a_fixture_flashes_one_board_and_stops()
    {
        // Decision 5's guarantee, unchanged. A fixture produces the same DeviceId for every board,
        // so the already-flashed set is what stops the second one.
        var rig = Build();
        await using var svc = rig.Svc;
        string fw = await TempBinAsync();
        try
        {
            await ArmedAsync(rig, fw, RepeatMode.None);
            await FlashFirstBoardAsync(rig);
            await BoardLeavesAsync(rig);

            // Another board arrives. The cycle that detects it also decides whether to flash it,
            // before it waits for the next cycle.
            rig.Provider.Answers = true;
            await rig.Probes.StepAsync();

            Assert.Equal(1, svc.State.AutoflashTally.Skipped);
            Assert.Equal(1, svc.State.AutoflashTally.Flashed);
        }
        finally { File.Delete(fw); }
    }

    [Fact]
    public async Task With_repeat_the_fixture_flashes_the_next_board_after_the_row_is_retracted()
    {
        var rig = Build();
        await using var svc = rig.Svc;
        string fw = await TempBinAsync();
        try
        {
            await ArmedAsync(rig, fw, RepeatMode.Silence);
            await FlashFirstBoardAsync(rig);
            await BoardLeavesAsync(rig);

            rig.Provider.Answers = true;
            await rig.Probes.StepAsync();
            Assert.Equal(0, svc.State.AutoflashTally.Skipped);   // queued, not refused

            await ServiceWait.UntilAsync(svc, s => s.AutoflashTally.Flashed >= 2);
        }
        finally { File.Delete(fw); }
    }

    [Fact]
    public async Task With_repeat_a_board_that_never_leaves_is_not_flashed_twice()
    {
        // The gate reopens on departure, not on time. A board that keeps answering keeps its row,
        // so nothing re-arms.
        var rig = Build();
        await using var svc = rig.Svc;
        string fw = await TempBinAsync();
        try
        {
            await ArmedAsync(rig, fw, RepeatMode.Silence);
            await FlashFirstBoardAsync(rig);

            // Every way to a second flash starts with the service reporting something. Each cycle
            // reports before it waits, so over these cycles it reported nothing at all.
            int reported = 0;
            svc.StateChanged += _ => Interlocked.Increment(ref reported);
            await rig.Probes.StepAsync(ProbeRowPolicy.SilencesBeforeRemoved + 2);

            Assert.Equal(0, Volatile.Read(ref reported));
            Assert.Equal(1, svc.State.AutoflashTally.Flashed);
        }
        finally { File.Delete(fw); }
    }

    [Fact]
    public async Task A_repeating_session_counts_flashes_rather_than_boards()
    {
        // Silence cannot tell a board that left from one that reset while seated, so the tally must
        // not be worded as a count of distinct boards.
        var rig = Build();
        await using var svc = rig.Svc;
        string fw = await TempBinAsync();
        try
        {
            await ArmedAsync(rig, fw, RepeatMode.Silence);
            Assert.False(svc.State.AutoflashTally.CountsDistinctBoards);
        }
        finally { File.Delete(fw); }
    }

    [Fact]
    public async Task A_non_repeating_session_can_claim_distinct_boards()
    {
        var rig = Build();
        await using var svc = rig.Svc;
        string fw = await TempBinAsync();
        try
        {
            await ArmedAsync(rig, fw, RepeatMode.None);
            Assert.True(svc.State.AutoflashTally.CountsDistinctBoards);
        }
        finally { File.Delete(fw); }
    }

    [Fact]
    public void The_audit_numbers_repeated_flashes_on_one_row()
    {
        // "flashed COM7" three times says nothing about which board each was. A position in the
        // sequence is what can honestly be produced for a fixture.
        var id = new DeviceId("COM7");

        var tally = AutoflashTally.Empty
            .With(AutoflashOutcomeKind.Flashed, id, null)
            .With(AutoflashOutcomeKind.Flashed, id, null)
            .With(AutoflashOutcomeKind.Flashed, id, null);

        Assert.Equal(3, tally.Flashed);
        Assert.Equal("flashed COM7", tally.Audit[0]);
        Assert.Equal("flashed COM7 #2", tally.Audit[1]);
        Assert.Equal("flashed COM7 #3", tally.Audit[2]);
    }

    [Fact]
    public void Separate_rows_are_numbered_separately()
    {
        var tally = AutoflashTally.Empty
            .With(AutoflashOutcomeKind.Flashed, new DeviceId("COM7"), null)
            .With(AutoflashOutcomeKind.Flashed, new DeviceId("COM9"), null)
            .With(AutoflashOutcomeKind.Flashed, new DeviceId("COM7"), null);

        Assert.Equal("flashed COM7", tally.Audit[0]);
        Assert.Equal("flashed COM9", tally.Audit[1]);
        Assert.Equal("flashed COM7 #2", tally.Audit[2]);
    }
}
