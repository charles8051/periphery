using System.Collections.Immutable;

namespace Periphery.FlashAnything.Tests;

/// <summary>
/// The probe loop (adr.md Decision 9). The delay is injected, so the cadence is driven by the test
/// rather than by a clock — nothing here sleeps, and the requested interval is itself assertable.
/// </summary>
public class SerialProbeLoopTests
{
    private static readonly DeviceIdentity G431 = new(
        Family: "STM32", Chip: "0x468", BootloaderVersion: "3.1",
        TransferSize: 256, Regions: ImmutableArray<MemoryRegion>.Empty,
        SupportedCommands: ImmutableArray<string>.Empty);

    private static readonly TimeSpan Cadence = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Stalled = TimeSpan.FromSeconds(10);

    private static DeviceInfo BridgeDevice => new()
    {
        Id = new DeviceId("USB-BRIDGE"),
        Name = "CP210x",
        VendorId = new HardwareId(0x10C4),
        ProductId = new HardwareId(0xEA60),
        SerialNumber = "92EA014C",
        LocationPath = "PCIROOT(0)#USB(1)",
        PortName = new SerialPortName("COM7"),
    };

    private static BridgeIdentity Identity()
    {
        Assert.True(BridgeIdentity.TryFrom(BridgeDevice, out var id, out _));
        return id;
    }

    /// <summary>A provider whose every open answers or stays silent as the test dictates.</summary>
    private sealed class ScriptedProvider(
        Queue<bool> answers, Exception? throwInstead = null, bool throwOnDispose = false) : IBootloaderProvider
    {
        public int Opens { get; private set; }
        public string Name => "STM32 UART (AN3155)";
        public IdentificationMode Identification => IdentificationMode.Probe;
        public bool CanHandle(DeviceInfo device) => true;

        public Task<IFirmwareProgrammer> OpenAsync(DeviceInfo device, CancellationToken ct = default)
        {
            Opens++;
            if (throwInstead is not null) throw throwInstead;
            bool answered = answers.Count > 0 && answers.Dequeue();
            return answered
                ? Task.FromResult<IFirmwareProgrammer>(
                    throwOnDispose ? new ThrowsOnDispose(device, G431) : new FakeFirmwareProgrammer(device, identity: G431))
                : throw new BootloaderException("no valid answer to the AN3155 sync byte");
        }
    }

    /// <summary>A programmer whose close fails — a port yanked between probe and teardown.</summary>
    private sealed class ThrowsOnDispose(DeviceInfo device, DeviceIdentity identity) : IFirmwareProgrammer
    {
        public DeviceInfo Device { get; } = device;
        public ImmutableArray<FirmwareFormat> AcceptedFormats { get; } =
            ImmutableArray.Create(FirmwareFormat.IntelHex, FirmwareFormat.RawBinary);
        public Task<DeviceIdentity> IdentifyAsync(CancellationToken ct = default) => Task.FromResult(identity);
        public Task<FlashResult> FlashAsync(
            FirmwarePayload payload, FlashOptions options, IProgress<FlashProgress>? progress = null,
            CancellationToken ct = default) => Task.FromResult(FlashResult.Ok(0, verified: true));
        public Task LeaveAsync(CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => throw new IOException("the port went away while closing");
    }

    private sealed record Run(
        List<ProbeRowAction> Actions, List<TimeSpan> Waits, ScriptedProvider Provider, SerialProbeLoop Loop);

    private static async Task<Run> RunAsync(
        IEnumerable<bool> answers, int cycles, Func<BridgeIdentity, DeviceInfo?>? resolve = null,
        Exception? providerThrows = null, bool throwOnDispose = false)
    {
        var actions = new List<ProbeRowAction>();
        var waits = new List<TimeSpan>();
        var provider = new ScriptedProvider(new Queue<bool>(answers), providerThrows, throwOnDispose);
        using var cts = new CancellationTokenSource();

        // The delay is where the loop yields, so it is also where the test decides how far to let
        // it run: record the interval, and cancel once enough cycles have happened.
        Task Delay(TimeSpan d, CancellationToken _)
        {
            waits.Add(d);
            if (waits.Count >= cycles) cts.Cancel();
            return Task.CompletedTask;
        }

        var loop = new SerialProbeLoop(
            Identity(), resolve ?? (_ => BridgeDevice), provider, actions.Add, Delay, Cadence, Stalled);
        await loop.RunAsync(cts.Token);
        return new Run(actions, waits, provider, loop);
    }

    [Fact]
    public async Task A_board_that_answers_is_reported_once()
    {
        var run = await RunAsync(new[] { true, true, true }, cycles: 3);

        Assert.Single(run.Actions);
        Assert.Equal(G431, Assert.IsType<ProbeRowAction.Detected>(run.Actions[0]).Identity);
        Assert.True(run.Loop.State.Occupied);
    }

    [Fact]
    public async Task An_empty_fixture_reports_nothing_at_all()
    {
        // The normal resting state of an armed fixture. It must not emit removals for a board that
        // was never there, however long it sits.
        var run = await RunAsync(Enumerable.Repeat(false, 8), cycles: 8);

        Assert.Empty(run.Actions);
        Assert.Equal(8, run.Provider.Opens);
    }

    [Fact]
    public async Task A_board_lifted_out_is_reported_gone_after_the_silence_run()
    {
        var answers = new[] { true }.Concat(Enumerable.Repeat(false, ProbeRowPolicy.SilencesBeforeRemoved));

        var run = await RunAsync(answers, cycles: ProbeRowPolicy.SilencesBeforeRemoved + 1);

        Assert.IsType<ProbeRowAction.Detected>(run.Actions[0]);
        Assert.IsType<ProbeRowAction.Removed>(run.Actions[^1]);
    }

    [Fact]
    public async Task A_brief_gap_does_not_retract_the_row()
    {
        // One quiet cycle between answers is routine and must not make the row flicker.
        var run = await RunAsync(new[] { true, false, true, true }, cycles: 4);

        Assert.Single(run.Actions);
        Assert.IsType<ProbeRowAction.Detected>(run.Actions[0]);
    }

    [Fact]
    public async Task The_bridge_disappearing_faults_the_loop_and_stops_it()
    {
        // adr.md Decision 8 breaks the bind on disconnect: the fixture is unplugged, and the loop
        // does not resume if something matching comes back. There is no port left to be quiet on,
        // so this is a fault rather than silence.
        var run = await RunAsync(new[] { true }, cycles: 3, resolve: _ => null);

        Assert.Contains("no longer present", Assert.IsType<ProbeRowAction.Faulted>(run.Actions[^1]).Message);
        Assert.Equal(0, run.Provider.Opens);   // never tried to open a port that is not there
        Assert.Empty(run.Waits);               // and stopped rather than waiting to try again
    }

    [Fact]
    public async Task Probing_slows_down_once_the_row_stalls()
    {
        var run = await RunAsync(
            Enumerable.Repeat(false, ProbeRowPolicy.SilencesBeforeBackoff + 1),
            cycles: ProbeRowPolicy.SilencesBeforeBackoff + 1);

        // The cadence itself changes, which is the point: the byte rate to whatever is attached
        // falls. Asserting the requested interval rather than a flag is what makes that observable.
        Assert.All(run.Waits.Take(ProbeRowPolicy.SilencesBeforeBackoff - 1), w => Assert.Equal(Cadence, w));
        Assert.Equal(Stalled, run.Waits[^1]);
        Assert.True(run.Loop.State.Stalled);
    }

    [Fact]
    public async Task A_stalled_fixture_keeps_probing()
    {
        // Backing off is not stopping. An armed fixture is still armed, and a board dropped in
        // after a long wait must still be found.
        var answers = Enumerable.Repeat(false, ProbeRowPolicy.SilencesBeforeBackoff + 1).Append(true);

        var run = await RunAsync(answers, cycles: ProbeRowPolicy.SilencesBeforeBackoff + 2);

        Assert.IsType<ProbeRowAction.Detected>(run.Actions[^1]);
        Assert.Equal(Cadence, run.Waits[^1]);   // and the cadence recovers with it
    }

    [Fact]
    public async Task A_provider_defect_faults_the_row_rather_than_reading_as_silence()
    {
        // Folding an unexpected exception into NoResponse would back the cadence off and probe
        // forever while never reporting that anything is wrong. Only the provider's documented
        // failure — BootloaderException — means the device did not cooperate.
        var run = await RunAsync(new[] { true }, cycles: 3,
            providerThrows: new InvalidOperationException("provider bug"));

        var faulted = Assert.IsType<ProbeRowAction.Faulted>(run.Actions[^1]);
        Assert.Contains("InvalidOperationException", faulted.Message);
        Assert.Contains("provider bug", faulted.Message);
    }

    [Fact]
    public async Task A_failed_close_does_not_kill_the_row_or_lose_the_detection()
    {
        // Disposal runs after the probe result is computed but before it is returned, so a throw
        // there would bypass the policy entirely and propagate out of RunAsync — killing an armed
        // row over a failed close and losing the detection that cycle had already established.
        var run = await RunAsync(new[] { true, true }, cycles: 2, throwOnDispose: true);

        Assert.Equal(G431, Assert.IsType<ProbeRowAction.Detected>(run.Actions[0]).Identity);
        Assert.Equal(2, run.Provider.Opens);   // and it kept probing
    }
}
