namespace Periphery.FlashAnything.Tests;

/// <summary>
/// Detection ownership and probe-loop lifecycle (adr.md Decision 9). While a probe family is armed
/// on a bridge, the loop is the only thing that may report a target present — it is the only thing
/// that has actually asked.
/// </summary>
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
        public bool Answers { get; set; }
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

    private static async Task<string> TempBinAsync()
    {
        string path = Path.Combine(Path.GetTempPath(), $"probe-own-{Guid.NewGuid():N}.bin");
        await File.WriteAllBytesAsync(path, new byte[64]);
        return path;
    }

    private static async Task WaitUntil(Func<bool> until, string what)
    {
        for (int i = 0; i < 300 && !until(); i++) await Task.Delay(10);
        Assert.True(until(), what);
    }

    private static (FlashAnythingService Svc, ProbeProvider Provider, FakeMonitor Monitor) Build()
    {
        var provider = new ProbeProvider();
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
    public async Task Arming_starts_probing_the_bound_bridge()
    {
        var (svc, provider, monitor) = Build();
        await using var _ = svc;
        await svc.RefreshAsync();
        monitor.Plug(Bridge());
        await WaitUntil(() => svc.State.Targets.Length == 1, "bridge surfaced");

        string fw = await TempBinAsync();
        try
        {
            await svc.LoadFirmwareAsync(fw);
            await svc.DispatchAsync(new AppIntent.ArmAutoflash(
                Family, FlashOptions.Default, [new SerialPortName("COM7")]));

            await WaitUntil(() => Volatile.Read(ref provider.Opens) > 2, "loop probed repeatedly");
        }
        finally { File.Delete(fw); }
    }

    [Fact]
    public async Task Disarming_stops_the_probing()
    {
        var (svc, provider, monitor) = Build();
        await using var _ = svc;
        await svc.RefreshAsync();
        monitor.Plug(Bridge());
        await WaitUntil(() => svc.State.Targets.Length == 1, "bridge surfaced");

        string fw = await TempBinAsync();
        try
        {
            await svc.LoadFirmwareAsync(fw);
            await svc.DispatchAsync(new AppIntent.ArmAutoflash(
                Family, FlashOptions.Default, [new SerialPortName("COM7")]));
            await WaitUntil(() => Volatile.Read(ref provider.Opens) > 2, "probing started");

            await svc.DispatchAsync(new AppIntent.DisarmAutoflash());
            await Task.Delay(30);
            int after = Volatile.Read(ref provider.Opens);
            await Task.Delay(60);

            // Disarm is the stop, and it is immediate.
            Assert.InRange(Volatile.Read(ref provider.Opens) - after, 0, 1);
        }
        finally { File.Delete(fw); }
    }

    [Fact]
    public async Task While_armed_the_watcher_does_not_also_report_the_target()
    {
        // Two detections for one physical target is the hazard: MaybeAutoflash fires on the first,
        // so the watcher's could dispatch a flash before the probe had established there is an
        // STM32 there at all.
        var (svc, provider, monitor) = Build();
        await using var _ = svc;
        await svc.RefreshAsync();
        monitor.Plug(Bridge());
        await WaitUntil(() => svc.State.Targets.Length == 1, "bridge surfaced");

        string fw = await TempBinAsync();
        try
        {
            await svc.LoadFirmwareAsync(fw);
            await svc.DispatchAsync(new AppIntent.ArmAutoflash(
                Family, FlashOptions.Default, [new SerialPortName("COM7")]));
            await WaitUntil(() => Volatile.Read(ref provider.Opens) > 2, "probing started");

            // Re-announce the same bridge, as a re-enumeration would.
            monitor.Plug(Bridge());
            await Task.Delay(40);

            Assert.Single(svc.State.Targets);
        }
        finally { File.Delete(fw); }
    }

    [Fact]
    public async Task A_board_answering_on_a_bound_bridge_becomes_a_target_with_its_identity()
    {
        var (svc, provider, monitor) = Build();
        await using var _ = svc;
        await svc.RefreshAsync();
        monitor.Plug(Bridge());
        await WaitUntil(() => svc.State.Targets.Length == 1, "bridge surfaced");

        string fw = await TempBinAsync();
        try
        {
            await svc.LoadFirmwareAsync(fw);
            await svc.DispatchAsync(new AppIntent.ArmAutoflash(
                Family, FlashOptions.Default, [new SerialPortName("COM7")]));
            await WaitUntil(() => Volatile.Read(ref provider.Opens) > 1, "probing started");

            provider.Answers = true;

            await WaitUntil(() => svc.State.Targets.Length == 1 && svc.State.Targets[0].Identity is not null,
                "probe reported the target with its identity");
        }
        finally { File.Delete(fw); }
    }
}
