using System.Collections.Immutable;

namespace Periphery.FlashAnything.Tests;

/// <summary>
/// The fixture loop (adr.md Decision 10): one flash per bound bridge per armed session by default,
/// with a succession of boards opt-in behind <c>--repeat</c>.
/// </summary>
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
        public int ConcurrentFlashes;
        public int MaxConcurrentFlashes;
        public string Name => Family;
        public IdentificationMode Identification => IdentificationMode.Probe;
        public bool CanHandle(DeviceInfo device) => device.PortName is not null;

        public Task<IFirmwareProgrammer> OpenAsync(DeviceInfo device, CancellationToken ct = default) =>
            Answers
                ? Task.FromResult<IFirmwareProgrammer>(new CountingProgrammer(device, this))
                : throw new BootloaderException("nothing answered the sync byte");
    }

    /// <summary>Records overlapping flashes, so a double-enqueue cannot pass unnoticed.</summary>
    private sealed class CountingProgrammer(DeviceInfo device, SwitchableProvider owner) : IFirmwareProgrammer
    {
        public DeviceInfo Device { get; } = device;
        public ImmutableArray<FirmwareFormat> AcceptedFormats { get; } =
            ImmutableArray.Create(FirmwareFormat.IntelHex, FirmwareFormat.RawBinary, FirmwareFormat.Elf);

        public Task<DeviceIdentity> IdentifyAsync(CancellationToken ct = default) =>
            Task.FromResult(DeviceIdentity.Unknown("STM32"));

        public async Task<FlashResult> FlashAsync(
            FirmwarePayload payload, FlashOptions options, IProgress<FlashProgress>? progress = null,
            CancellationToken ct = default)
        {
            int now = Interlocked.Increment(ref owner.ConcurrentFlashes);
            int seen = Volatile.Read(ref owner.MaxConcurrentFlashes);
            while (now > seen)
            {
                int prior = Interlocked.CompareExchange(ref owner.MaxConcurrentFlashes, now, seen);
                if (prior == seen) break;
                seen = prior;
            }

            try
            {
                await Task.Delay(20, ct);
                return FlashResult.Ok(payload.ByteLength, verified: true);
            }
            finally { Interlocked.Decrement(ref owner.ConcurrentFlashes); }
        }

        public Task LeaveAsync(CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static async Task<string> TempBinAsync()
    {
        string path = Path.Combine(Path.GetTempPath(), $"probe-repeat-{Guid.NewGuid():N}.bin");
        await File.WriteAllBytesAsync(path, new byte[64]);
        return path;
    }

    private static async Task WaitUntil(Func<bool> until, string what)
    {
        for (int i = 0; i < 500 && !until(); i++) await Task.Delay(10);
        Assert.True(until(), what);
    }

    private static (FlashAnythingService Svc, SwitchableProvider Provider, FakeMonitor Monitor) Build()
    {
        var provider = new SwitchableProvider();
        var registry = new BootloaderRegistry();
        registry.Register(provider);
        var monitor = new FakeMonitor();
        var svc = new FlashAnythingService(registry, FakeDevices.Watcher(monitor))
        {
            ProbeCadence = TimeSpan.FromMilliseconds(1),
            StalledProbeCadence = TimeSpan.FromMilliseconds(1),
        };
        return (svc, provider, monitor);
    }

    private static async Task<FlashAnythingService> ArmedAsync(
        (FlashAnythingService Svc, SwitchableProvider Provider, FakeMonitor Monitor) rig,
        string firmware, RepeatMode repeat)
    {
        await rig.Svc.RefreshAsync();
        rig.Monitor.Plug(Bridge());
        await WaitUntil(() => rig.Svc.State.Targets.Length == 1, "bridge surfaced");
        await rig.Svc.LoadFirmwareAsync(firmware);
        await rig.Svc.DispatchAsync(new AppIntent.ArmAutoflash(
            Family, FlashOptions.Default, [new SerialPortName("COM7")], repeat));
        return rig.Svc;
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

            rig.Provider.Answers = true;
            await WaitUntil(() => svc.State.AutoflashTally.Flashed >= 1, "first board flashed");

            // The board leaves and another arrives.
            rig.Provider.Answers = false;
            await Task.Delay(60);
            rig.Provider.Answers = true;
            await Task.Delay(120);

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

            rig.Provider.Answers = true;
            await WaitUntil(() => svc.State.AutoflashTally.Flashed >= 1, "first board flashed");

            rig.Provider.Answers = false;
            await Task.Delay(80);            // long enough to retract the row
            rig.Provider.Answers = true;

            await WaitUntil(() => svc.State.AutoflashTally.Flashed >= 2, "second board flashed");
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

            rig.Provider.Answers = true;
            await WaitUntil(() => svc.State.AutoflashTally.Flashed >= 1, "board flashed");
            await Task.Delay(150);

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
    public async Task Repeat_never_leaves_two_flashes_outstanding_on_one_fixture()
    {
        // A successful flash causes the silence that retracts the row: LeaveAfterFlash jumps the
        // part and it stops answering. So a Removed routinely arrives while that flash is still
        // running, and reopening then would let the next Detected enqueue the same row again — a
        // second outstanding flash on a fixture that reports one DeviceId for every board.
        var rig = Build();
        await using var svc = rig.Svc;
        string fw = await TempBinAsync();
        try
        {
            await ArmedAsync(rig, fw, RepeatMode.Silence);

            // Get one flash on the board first, so the assertion below cannot pass by nothing
            // having happened — that made it flaky, because the flapping alone does not guarantee
            // a flash starts.
            rig.Provider.Answers = true;
            await WaitUntil(() => Volatile.Read(ref rig.Provider.MaxConcurrentFlashes) >= 1, "a flash started");

            // Then flap the fixture hard: every answer can start another, every silence can retract.
            for (int i = 0; i < 40; i++)
            {
                rig.Provider.Answers = i % 2 == 0;
                await Task.Delay(5);
            }
            rig.Provider.Answers = false;
            await Task.Delay(100);

            // The invariant: however many flashes the flapping produced, no two ever overlapped on
            // this fixture. Counting audit lines would prove nothing — that is a tautology.
            Assert.Equal(1, Volatile.Read(ref rig.Provider.MaxConcurrentFlashes));
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
