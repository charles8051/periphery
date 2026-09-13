using Periphery.Testing;

namespace Periphery.FlashAnything.Tests;

/// <summary>
/// The probe loop's lifecycle where it meets the rest of the service: which arms start loops, what
/// a failed arm leaves behind, and the exclusion between probing and flashing one fixture.
/// </summary>
public class ProbeLoopLifecycleTests
{
    private const string Probe = "STM32 UART (AN3155)";
    private const string Passive = "EFM8 USB-HID";

    private static DeviceInfo Bridge(string port = "COM7") => new()
    {
        Id = new DeviceId($"USB-CP210X-{port}"),
        Name = "CP210x",
        VendorId = new HardwareId(0x10C4),
        ProductId = new HardwareId(0xEA60),
        SerialNumber = $"SN-{port}",
        LocationPath = $"PCIROOT(0)#USB({port})",
        PortName = new SerialPortName(port),
    };

    /// <summary>Counts opens, and can hold one open so a flash and a probe can be raced.</summary>
    private sealed class CountingProvider(string name, IdentificationMode mode) : IBootloaderProvider
    {
        public int Opens;
        public int Concurrent;
        public int MaxConcurrent;
        public volatile bool Answers;
        public string Name => name;
        public IdentificationMode Identification => mode;
        public bool CanHandle(DeviceInfo device) => device.PortName is not null;

        /// <summary>The open, counted from 1, that waits inside the port until released. 0 holds none.</summary>
        public int HoldOpen { get; init; }
        public TaskCompletionSource HeldOpenEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseHeldOpen { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IFirmwareProgrammer> OpenAsync(DeviceInfo device, CancellationToken ct = default)
        {
            int open = Interlocked.Increment(ref Opens);
            int now = Interlocked.Increment(ref Concurrent);
            InterlockedMax(ref MaxConcurrent, now);
            try
            {
                if (open == HoldOpen)
                {
                    HeldOpenEntered.TrySetResult();
                    await ReleaseHeldOpen.Task.WaitAsync(ct);
                }
                if (!Answers) throw new BootloaderException("nothing answered the sync byte");
                return new FakeFirmwareProgrammer(device);
            }
            finally { Interlocked.Decrement(ref Concurrent); }
        }

        private static void InterlockedMax(ref int target, int value)
        {
            int seen = Volatile.Read(ref target);
            while (value > seen)
            {
                int prior = Interlocked.CompareExchange(ref target, value, seen);
                if (prior == seen) return;
                seen = prior;
            }
        }
    }

    private sealed record Rig(
        FlashAnythingService Svc, CountingProvider Provider, FakeMonitor Monitor,
        TimerSignalingFakeTimeProvider Time, ProbeStepper Probes);

    private static async Task<string> TempBinAsync()
    {
        string path = Path.Combine(Path.GetTempPath(), $"probe-life-{Guid.NewGuid():N}.bin");
        await File.WriteAllBytesAsync(path, new byte[64]);
        return path;
    }

    private static Rig Build(IdentificationMode mode = IdentificationMode.Probe, int holdOpen = 0)
    {
        var provider = new CountingProvider(mode == IdentificationMode.Probe ? Probe : Passive, mode)
        {
            HoldOpen = holdOpen,
        };
        var registry = new BootloaderRegistry();
        registry.Register(provider);
        var monitor = new FakeMonitor();
        var time = new TimerSignalingFakeTimeProvider();
        var svc = new FlashAnythingService(registry, FakeDevices.Watcher(monitor)) { TimeProvider = time };
        return new Rig(svc, provider, monitor, time, new ProbeStepper(time));
    }

    private static async Task SurfaceBridgeAsync(Rig rig, string firmware)
    {
        await rig.Svc.RefreshAsync();
        rig.Monitor.Plug(Bridge());
        await ServiceWait.UntilAsync(rig.Svc, s => s.Targets.Length == 1);
        await rig.Svc.LoadFirmwareAsync(firmware);
    }

    [Fact]
    public async Task A_failed_re_arm_stops_the_running_probe_loops()
    {
        // The comment said every refusal disarms; the early returns did not stop the loops, so an
        // invalid re-arm left the old fixture being probed under a session that no longer existed.
        var rig = Build();
        await using var _ = rig.Svc;
        string fw = await TempBinAsync();
        try
        {
            await SurfaceBridgeAsync(rig, fw);
            await rig.Svc.DispatchAsync(new AppIntent.ArmAutoflash(Probe, FlashOptions.Default, [new SerialPortName("COM7")]));
            await rig.Probes.ParkedAsync();
            await rig.Probes.StepAsync(2);

            await rig.Svc.DispatchAsync(new AppIntent.ArmAutoflash(Probe, FlashOptions.Default, [new SerialPortName("COM99")]));

            // The refusal waits for the loops it stops, and a stopped loop's cadence timer goes with
            // it, so nothing is left that could probe COM7 again.
            Assert.Null(rig.Svc.State.Autoflash);
            Assert.False(rig.Time.AdvanceToNextPendingTimer(), "a probe loop was still waiting to run");
            Assert.Equal(3, Volatile.Read(ref rig.Provider.Opens));
        }
        finally { File.Delete(fw); }
    }

    [Fact]
    public async Task A_passive_family_never_gets_a_probe_loop()
    {
        // A loop here would poke passive targets while detection ownership suppressed the watcher
        // events they are actually identified by — the family would stop working for nothing.
        // Loops start on the thread pool and say nothing until they probe, so what is asserted is
        // the arm's decision not to start one, made before the dispatch returns.
        var rig = Build(IdentificationMode.Passive);
        await using var _ = rig.Svc;
        string fw = await TempBinAsync();
        try
        {
            await SurfaceBridgeAsync(rig, fw);
            await rig.Svc.DispatchAsync(new AppIntent.ArmAutoflash(Passive, FlashOptions.Default, [new SerialPortName("COM7")]));

            Assert.NotNull(rig.Svc.State.Autoflash);
            Assert.Equal(0, ServiceInternals.ProbeLoopCount(rig.Svc));
        }
        finally { File.Delete(fw); }
    }

    [Fact]
    public async Task An_unprobed_bridge_present_at_arm_time_is_not_flashed()
    {
        // A probe row exists as soon as the watcher sees the bridge, before anything has asked what
        // is behind it. Flashing it at arm time would act on a fixture that might be empty.
        var rig = Build();
        await using var _ = rig.Svc;
        string fw = await TempBinAsync();
        try
        {
            await SurfaceBridgeAsync(rig, fw);
            await rig.Svc.DispatchAsync(new AppIntent.ArmAutoflash(Probe, FlashOptions.Default, [new SerialPortName("COM7")]));
            await rig.Probes.ParkedAsync();

            // A board answers. Had the arm queued the unprobed row, that row would already be marked
            // flashed, and this detection would be skipped as a repeat inside the cycle.
            rig.Provider.Answers = true;
            await rig.Probes.StepAsync();
            Assert.Equal(0, rig.Svc.State.AutoflashTally.Skipped);

            await ServiceWait.UntilAsync(rig.Svc, s => s.AutoflashTally.Total >= 1);
            Assert.Equal(1, rig.Svc.State.AutoflashTally.Flashed);
            Assert.Equal(1, rig.Svc.State.AutoflashTally.Total);
        }
        finally { File.Delete(fw); }
    }

    [Fact]
    public async Task Probing_and_flashing_one_fixture_never_open_the_port_at_once()
    {
        // A serial port is an exclusive open. Without the bridge gate the loop keeps probing on its
        // cadence while a worker flashes the same fixture, and either open can lose.
        //
        // Open 1 is the probe that finds the board; open 2 is the flash, held inside the port.
        var rig = Build(holdOpen: 2);
        await using var _ = rig.Svc;
        string fw = await TempBinAsync();
        try
        {
            await SurfaceBridgeAsync(rig, fw);
            rig.Provider.Answers = true;
            await rig.Svc.DispatchAsync(new AppIntent.ArmAutoflash(Probe, FlashOptions.Default, [new SerialPortName("COM7")]));

            await rig.Probes.ParkedAsync();
            await rig.Provider.HeldOpenEntered.Task.WaitAsync(ServiceWait.Bound);

            // Wake the loop while the flash holds the port. It continues inside the advance until it
            // has to wait, so by now it has either reached the port or stopped at the bridge gate.
            await rig.Probes.StartNextCycleInlineAsync();
            Assert.Equal(1, Volatile.Read(ref rig.Provider.MaxConcurrent));

            rig.Provider.ReleaseHeldOpen.TrySetResult();
            await ServiceWait.UntilAsync(rig.Svc, s => s.AutoflashTally.Flashed >= 1);
            await rig.Probes.ParkedAsync();   // the waiting probe ran once the flash let go

            Assert.Equal(1, Volatile.Read(ref rig.Provider.MaxConcurrent));
            Assert.Equal(3, Volatile.Read(ref rig.Provider.Opens));
        }
        finally
        {
            rig.Provider.ReleaseHeldOpen.TrySetResult();
            File.Delete(fw);
        }
    }
}
