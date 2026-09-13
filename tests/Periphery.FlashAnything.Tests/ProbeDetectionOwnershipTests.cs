using Periphery.Testing;

namespace Periphery.FlashAnything.Tests;

/// <summary>
/// Detection ownership and probe-loop lifecycle (adr.md Decision 9). While a probe family is armed
/// on a bridge, the loop is the only thing that may report a target present — it is the only thing
/// that has actually asked.
/// </summary>
/// <remarks>
/// The loop runs on a fake clock and the tests step it one cycle at a time, so a count of probes is
/// exact rather than a lower bound on however many a real cadence managed.
/// </remarks>
public class ProbeDetectionOwnershipTests
{
    private const string Family = "STM32 UART (AN3155)";

    private static DeviceInfo Bridge(string port = "COM7") => new()
    {
        Id = new DeviceId($"USB-CP210X-{port}"),
        Name = "CP210x",
        VendorId = new HardwareId(0x10C4),
        ProductId = new HardwareId(0xEA60),
        SerialNumber = "92EA014C",
        LocationPath = $"PCIROOT(0)#USB({port})",
        PortName = new SerialPortName(port),
    };

    /// <summary>A probe provider that answers, or does not, on demand.</summary>
    private sealed class ProbeProvider : IBootloaderProvider
    {
        public volatile bool Answers;
        public int Opens;
        public string Name => Family;
        public IdentificationMode Identification => IdentificationMode.Probe;
        public bool CanHandle(DeviceInfo device) => device.PortName is not null;

        public Task<IFirmwareProgrammer> OpenAsync(DeviceInfo device, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Opens);
            return Answers
                ? Task.FromResult<IFirmwareProgrammer>(new FakeFirmwareProgrammer(device))
                : throw new BootloaderException("nothing answered the sync byte");
        }
    }

    private sealed record Rig(
        FlashAnythingService Svc, ProbeProvider Provider, FakeMonitor Monitor,
        TimerSignalingFakeTimeProvider Time, ProbeStepper Probes);

    private static async Task<string> TempBinAsync()
    {
        string path = Path.Combine(Path.GetTempPath(), $"probe-own-{Guid.NewGuid():N}.bin");
        await File.WriteAllBytesAsync(path, new byte[64]);
        return path;
    }

    private static Rig Build()
    {
        var provider = new ProbeProvider();
        var registry = new BootloaderRegistry();
        registry.Register(provider);
        var monitor = new FakeMonitor();
        var time = new TimerSignalingFakeTimeProvider();
        var svc = new FlashAnythingService(registry, FakeDevices.Watcher(monitor)) { TimeProvider = time };
        return new Rig(svc, provider, monitor, time, new ProbeStepper(time));
    }

    // Surfaces the bridge, loads an image, arms on COM7, and waits for the loop's first probe.
    private static async Task ArmAsync(Rig rig, string firmware)
    {
        await rig.Svc.RefreshAsync();
        rig.Monitor.Plug(Bridge());
        await ServiceWait.UntilAsync(rig.Svc, s => s.Targets.Length == 1);
        await rig.Svc.LoadFirmwareAsync(firmware);
        await rig.Svc.DispatchAsync(new AppIntent.ArmAutoflash(
            Family, FlashOptions.Default, [new SerialPortName("COM7")]));
        await rig.Probes.ParkedAsync();
    }

    [Fact]
    public async Task Arming_starts_probing_the_bound_bridge()
    {
        var rig = Build();
        await using var _ = rig.Svc;
        string fw = await TempBinAsync();
        try
        {
            await ArmAsync(rig, fw);
            await rig.Probes.StepAsync(2);

            Assert.Equal(1, ServiceInternals.ProbeLoopCount(rig.Svc));
            Assert.Equal(3, Volatile.Read(ref rig.Provider.Opens));
        }
        finally { File.Delete(fw); }
    }

    [Fact]
    public async Task Disarming_stops_the_probing()
    {
        var rig = Build();
        await using var _ = rig.Svc;
        string fw = await TempBinAsync();
        try
        {
            await ArmAsync(rig, fw);
            await rig.Probes.StepAsync(2);

            // Disarm is the stop, and it is immediate: the dispatch waits for the loop to end, and
            // the loop's cadence timer ends with it, so nothing is left that could probe again.
            await rig.Svc.DispatchAsync(new AppIntent.DisarmAutoflash());

            Assert.False(rig.Time.AdvanceToNextPendingTimer(), "a probe loop was still waiting to run");
            Assert.Equal(3, Volatile.Read(ref rig.Provider.Opens));
        }
        finally { File.Delete(fw); }
    }

    [Fact]
    public async Task While_armed_the_watcher_does_not_also_report_the_target()
    {
        // Two detections for one physical target is the hazard: MaybeAutoflash fires on the first,
        // so the watcher's could dispatch a flash before the probe had established there is an
        // STM32 there at all.
        var rig = Build();
        await using var _ = rig.Svc;
        string fw = await TempBinAsync();
        try
        {
            await ArmAsync(rig, fw);

            // Re-enumerate the bridge. Its removal is reported, and its return is the watcher's to
            // report or not. The watcher reports synchronously, and the loop has not probed since,
            // so a target present now could only have come from the watcher.
            rig.Monitor.Unplug(Bridge());
            rig.Monitor.Plug(Bridge());

            Assert.Empty(rig.Svc.State.Targets);
        }
        finally { File.Delete(fw); }
    }

    [Fact]
    public async Task A_board_answering_on_a_bound_bridge_becomes_a_target_with_its_identity()
    {
        var rig = Build();
        await using var _ = rig.Svc;
        string fw = await TempBinAsync();
        try
        {
            await ArmAsync(rig, fw);

            // The cycle reports what it found before it waits for the next one.
            rig.Provider.Answers = true;
            await rig.Probes.StepAsync();

            var target = Assert.Single(rig.Svc.State.Targets);
            Assert.NotNull(target.Identity);
        }
        finally { File.Delete(fw); }
    }
}
