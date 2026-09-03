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

    /// <summary>Counts opens, and can block inside one so a flash and a probe can be raced.</summary>
    private sealed class CountingProvider(string name, IdentificationMode mode) : IBootloaderProvider
    {
        public int Opens;
        public int Concurrent;
        public int MaxConcurrent;
        public bool Answers { get; set; }
        public string Name => name;
        public IdentificationMode Identification => mode;
        public bool CanHandle(DeviceInfo device) => device.PortName is not null;

        public async Task<IFirmwareProgrammer> OpenAsync(DeviceInfo device, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Opens);
            int now = Interlocked.Increment(ref Concurrent);
            InterlockedMax(ref MaxConcurrent, now);
            try
            {
                await Task.Delay(5, ct);
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

    private static async Task<string> TempBinAsync()
    {
        string path = Path.Combine(Path.GetTempPath(), $"probe-life-{Guid.NewGuid():N}.bin");
        await File.WriteAllBytesAsync(path, new byte[64]);
        return path;
    }

    private static async Task WaitUntil(Func<bool> until, string what)
    {
        for (int i = 0; i < 400 && !until(); i++) await Task.Delay(10);
        Assert.True(until(), what);
    }

    private static (FlashAnythingService Svc, CountingProvider Provider, FakeMonitor Monitor) Build(
        IdentificationMode mode = IdentificationMode.Probe, string? name = null)
    {
        var provider = new CountingProvider(name ?? (mode == IdentificationMode.Probe ? Probe : Passive), mode);
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

    [Fact]
    public async Task A_failed_re_arm_stops_the_running_probe_loops()
    {
        // The comment said every refusal disarms; the early returns did not stop the loops, so an
        // invalid re-arm left the old fixture being probed under a session that no longer existed.
        var (svc, provider, monitor) = Build();
        await using var _ = svc;
        await svc.RefreshAsync();
        monitor.Plug(Bridge());
        await WaitUntil(() => svc.State.Targets.Length == 1, "bridge surfaced");

        string fw = await TempBinAsync();
        try
        {
            await svc.LoadFirmwareAsync(fw);
            await svc.DispatchAsync(new AppIntent.ArmAutoflash(Probe, FlashOptions.Default, [new SerialPortName("COM7")]));
            await WaitUntil(() => Volatile.Read(ref provider.Opens) > 2, "probing started");

            await svc.DispatchAsync(new AppIntent.ArmAutoflash(Probe, FlashOptions.Default, [new SerialPortName("COM99")]));
            await Task.Delay(30);
            int after = Volatile.Read(ref provider.Opens);
            await Task.Delay(60);

            Assert.Null(svc.State.Autoflash);
            Assert.InRange(Volatile.Read(ref provider.Opens) - after, 0, 1);
        }
        finally { File.Delete(fw); }
    }

    [Fact]
    public async Task A_passive_family_never_gets_a_probe_loop()
    {
        // A loop here would poke passive targets while detection ownership suppressed the watcher
        // events they are actually identified by — the family would stop working for nothing.
        var (svc, provider, monitor) = Build(IdentificationMode.Passive);
        await using var _ = svc;
        await svc.RefreshAsync();
        monitor.Plug(Bridge());
        await WaitUntil(() => svc.State.Targets.Length == 1, "target surfaced");

        string fw = await TempBinAsync();
        try
        {
            await svc.LoadFirmwareAsync(fw);
            await svc.DispatchAsync(new AppIntent.ArmAutoflash(Passive, FlashOptions.Default, [new SerialPortName("COM7")]));
            await Task.Delay(60);

            // The only opens are the flash the passive path dispatched, not a probe cadence.
            Assert.InRange(Volatile.Read(ref provider.Opens), 0, 1);
        }
        finally { File.Delete(fw); }
    }

    [Fact]
    public async Task An_unprobed_bridge_present_at_arm_time_is_not_flashed()
    {
        // A probe row exists as soon as the watcher sees the bridge, before anything has asked what
        // is behind it. Flashing it at arm time would act on a fixture that might be empty.
        var (svc, provider, monitor) = Build();
        await using var _ = svc;
        await svc.RefreshAsync();
        monitor.Plug(Bridge());
        await WaitUntil(() => svc.State.Targets.Length == 1, "bridge surfaced");

        string fw = await TempBinAsync();
        try
        {
            await svc.LoadFirmwareAsync(fw);
            await svc.DispatchAsync(new AppIntent.ArmAutoflash(Probe, FlashOptions.Default, [new SerialPortName("COM7")]));
            await Task.Delay(60);

            Assert.Equal(0, svc.State.AutoflashTally.Flashed);
        }
        finally { File.Delete(fw); }
    }

    [Fact]
    public async Task Probing_and_flashing_one_fixture_never_open_the_port_at_once()
    {
        // A serial port is an exclusive open. Without the bridge gate the loop keeps probing on its
        // cadence while a worker flashes the same fixture, and either open can lose.
        var (svc, provider, monitor) = Build();
        await using var _ = svc;
        await svc.RefreshAsync();
        monitor.Plug(Bridge());
        await WaitUntil(() => svc.State.Targets.Length == 1, "bridge surfaced");

        string fw = await TempBinAsync();
        try
        {
            await svc.LoadFirmwareAsync(fw);
            provider.Answers = true;
            await svc.DispatchAsync(new AppIntent.ArmAutoflash(Probe, FlashOptions.Default, [new SerialPortName("COM7")]));

            await WaitUntil(() => svc.State.AutoflashTally.Flashed >= 1, "the probed board was flashed");
            await Task.Delay(50);

            Assert.Equal(1, Volatile.Read(ref provider.MaxConcurrent));
        }
        finally { File.Delete(fw); }
    }
}
