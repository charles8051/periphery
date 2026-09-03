namespace Periphery.FlashAnything.Tests;

/// <summary>
/// Arming a probe family binds the bridges behind the named ports (adr.md Decision 8), and
/// discovery attaches the bridge it found a probe target behind.
/// </summary>
public class ProbeArmBindingTests
{
    private const string Family = "STM32 UART (AN3155)";

    private static DeviceInfo Bridge(string port = "COM7", string? serial = "92EA014C") => new()
    {
        Id = new DeviceId($"USB-CP210X-{port}"),
        Name = "Silicon Labs CP210x USB to UART Bridge",
        VendorId = new HardwareId(0x10C4),
        ProductId = new HardwareId(0xEA60),
        SerialNumber = serial,
        LocationPath = $"PCIROOT(0)#USB({port})",
        PortName = new SerialPortName(port),
    };

    private static BootloaderRegistry Registry()
    {
        var registry = new BootloaderRegistry();
        registry.Register(new FakeBootloaderProvider(
            Family,
            d => d.PortName is not null,
            d => new FakeFirmwareProgrammer(d),
            IdentificationMode.Probe));
        return registry;
    }

    private static async Task<string> TempBinAsync()
    {
        string path = Path.Combine(Path.GetTempPath(), $"probe-arm-{Guid.NewGuid():N}.bin");
        await File.WriteAllBytesAsync(path, new byte[64]);
        return path;
    }

    private static async Task WaitUntil(FlashAnythingService svc, Func<AppState, bool> until)
    {
        for (int i = 0; i < 200 && !until(svc.State); i++) await Task.Delay(10);
        Assert.True(until(svc.State), "condition not reached");
    }

    [Fact]
    public async Task Discovery_attaches_the_bridge_a_probe_target_sits_behind()
    {
        var monitor = new FakeMonitor();
        await using var svc = new FlashAnythingService(Registry(), FakeDevices.Watcher(monitor));
        await svc.RefreshAsync();

        monitor.Plug(Bridge());
        await WaitUntil(svc, s => s.Targets.Length == 1);

        var target = svc.State.Targets[0];
        Assert.Equal(IdentificationMode.Probe, target.Identification);
        Assert.NotNull(target.Bridge);
        Assert.Equal("92EA014C", target.Bridge!.Value.SerialNumber);
    }

    [Fact]
    public async Task Arming_a_named_port_binds_the_bridge_behind_it()
    {
        var monitor = new FakeMonitor();
        await using var svc = new FlashAnythingService(Registry(), FakeDevices.Watcher(monitor));
        await svc.RefreshAsync();
        monitor.Plug(Bridge());
        await WaitUntil(svc, s => s.Targets.Length == 1);

        string fw = await TempBinAsync();
        try
        {
            await svc.LoadFirmwareAsync(fw);
            await svc.DispatchAsync(new AppIntent.ArmAutoflash(
                Family, FlashOptions.Default, [new SerialPortName("COM7")]));

            var armed = svc.State.Autoflash;
            Assert.NotNull(armed);
            var bound = Assert.Single(armed!.Bridges);
            Assert.Equal("92EA014C", bound.SerialNumber);
        }
        finally { File.Delete(fw); }
    }

    [Fact]
    public async Task Arming_a_port_with_nothing_on_it_is_refused()
    {
        // The operator named a bench that is not there. Arming anyway would leave a fixture
        // apparently armed and permanently idle.
        var monitor = new FakeMonitor();
        await using var svc = new FlashAnythingService(Registry(), FakeDevices.Watcher(monitor));
        await svc.RefreshAsync();

        string fw = await TempBinAsync();
        try
        {
            await svc.LoadFirmwareAsync(fw);
            await svc.DispatchAsync(new AppIntent.ArmAutoflash(
                Family, FlashOptions.Default, [new SerialPortName("COM99")]));

            Assert.Null(svc.State.Autoflash);
            Assert.Contains("COM99", svc.State.FirmwareError);
        }
        finally { File.Delete(fw); }
    }

    [Fact]
    public async Task A_partial_bind_is_refused_rather_than_arming_for_a_subset()
    {
        // The operator named two fixtures and one is absent. Arming for the half that resolved
        // while reporting success is how a bench gets left unattended and unflashed.
        var monitor = new FakeMonitor();
        await using var svc = new FlashAnythingService(Registry(), FakeDevices.Watcher(monitor));
        await svc.RefreshAsync();
        monitor.Plug(Bridge());
        await WaitUntil(svc, s => s.Targets.Length == 1);

        string fw = await TempBinAsync();
        try
        {
            await svc.LoadFirmwareAsync(fw);
            await svc.DispatchAsync(new AppIntent.ArmAutoflash(
                Family, FlashOptions.Default, [new SerialPortName("COM7"), new SerialPortName("COM8")]));

            Assert.Null(svc.State.Autoflash);
        }
        finally { File.Delete(fw); }
    }

    [Fact]
    public async Task Arming_a_bridge_that_cannot_be_identified_is_refused()
    {
        // VID/PID names a model, not a device. With neither a serial nor a port there is nothing
        // to tell this bridge from another of the same kind, so the arm fails rather than binding
        // something ambiguous.
        var monitor = new FakeMonitor();
        await using var svc = new FlashAnythingService(Registry(), FakeDevices.Watcher(monitor));
        await svc.RefreshAsync();
        monitor.Plug(Bridge() with { SerialNumber = null, LocationPath = null });
        await WaitUntil(svc, s => s.Targets.Length == 1);

        string fw = await TempBinAsync();
        try
        {
            await svc.LoadFirmwareAsync(fw);
            await svc.DispatchAsync(new AppIntent.ArmAutoflash(
                Family, FlashOptions.Default, [new SerialPortName("COM7")]));

            Assert.Null(svc.State.Autoflash);
            Assert.Contains("cannot be told apart", svc.State.FirmwareError);
        }
        finally { File.Delete(fw); }
    }

    [Fact]
    public async Task A_failed_arm_disarms_rather_than_leaving_the_previous_session_running()
    {
        // The worst of both outcomes: the operator is told the arm failed while the old family,
        // options and bindings keep flashing whatever appears.
        var monitor = new FakeMonitor();
        await using var svc = new FlashAnythingService(Registry(), FakeDevices.Watcher(monitor));
        await svc.RefreshAsync();
        monitor.Plug(Bridge());
        await WaitUntil(svc, s => s.Targets.Length == 1);

        string fw = await TempBinAsync();
        try
        {
            await svc.LoadFirmwareAsync(fw);
            await svc.DispatchAsync(new AppIntent.ArmAutoflash(
                Family, FlashOptions.Default, [new SerialPortName("COM7")]));
            Assert.NotNull(svc.State.Autoflash);

            await svc.DispatchAsync(new AppIntent.ArmAutoflash(
                Family, FlashOptions.Default, [new SerialPortName("COM99")]));

            Assert.Null(svc.State.Autoflash);
        }
        finally { File.Delete(fw); }
    }

    [Fact]
    public async Task A_failed_arm_keeps_the_loaded_firmware()
    {
        // A port that is absent says nothing about the image. Discarding it would be a second
        // failure caused by the first, and the operator would have to reload to try again.
        var monitor = new FakeMonitor();
        await using var svc = new FlashAnythingService(Registry(), FakeDevices.Watcher(monitor));
        await svc.RefreshAsync();

        string fw = await TempBinAsync();
        try
        {
            await svc.LoadFirmwareAsync(fw);
            await svc.DispatchAsync(new AppIntent.ArmAutoflash(
                Family, FlashOptions.Default, [new SerialPortName("COM99")]));

            Assert.NotNull(svc.State.Firmware);
            Assert.Contains("COM99", svc.State.FirmwareError);
        }
        finally { File.Delete(fw); }
    }

    [Fact]
    public async Task Arming_a_probe_family_with_no_ports_is_refused()
    {
        // Not dangerous — the policy's scope check fails closed, and such a session would skip
        // every target. It is useless, which is its own hazard: an operator who armed a fixture and
        // walked away would return to a bench that flashed nothing and never said why.
        var monitor = new FakeMonitor();
        await using var svc = new FlashAnythingService(Registry(), FakeDevices.Watcher(monitor));
        await svc.RefreshAsync();

        string fw = await TempBinAsync();
        try
        {
            await svc.LoadFirmwareAsync(fw);
            await svc.DispatchAsync(new AppIntent.ArmAutoflash(Family, FlashOptions.Default));

            Assert.Null(svc.State.Autoflash);
            Assert.Contains("without naming a port", svc.State.FirmwareError);
        }
        finally { File.Delete(fw); }
    }

    [Fact]
    public async Task Arming_a_passive_family_still_needs_no_ports()
    {
        var registry = new BootloaderRegistry();
        registry.Register(new FakeBootloaderProvider(
            "EFM8 USB-HID", d => d.PortName is null, d => new FakeFirmwareProgrammer(d)));

        var monitor = new FakeMonitor();
        await using var svc = new FlashAnythingService(registry, FakeDevices.Watcher(monitor));
        await svc.RefreshAsync();

        string fw = await TempBinAsync();
        try
        {
            await svc.LoadFirmwareAsync(fw);
            await svc.DispatchAsync(new AppIntent.ArmAutoflash("EFM8 USB-HID", FlashOptions.Default));

            Assert.NotNull(svc.State.Autoflash);
            Assert.Empty(svc.State.Autoflash!.Bridges);
        }
        finally { File.Delete(fw); }
    }
}
