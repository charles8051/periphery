namespace Periphery.Bootloader.Stm32.Serial.Tests;

public class Stm32SerialBootloaderProviderTests
{
    [Fact]
    public void CanHandle_claims_any_device_that_has_a_serial_port_name()
    {
        var provider = new Stm32SerialBootloaderProvider();

        Assert.Equal("STM32 UART (AN3155)", provider.Name);
        Assert.True(provider.CanHandle(new DeviceInfo { Id = "ch340", PortName = new SerialPortName("COM7") }));
        Assert.True(provider.CanHandle(new DeviceInfo { Id = "ftdi", PortName = new SerialPortName("/dev/ttyUSB0") }));
        Assert.False(provider.CanHandle(new DeviceInfo { Id = "no-port" }));
        Assert.False(provider.CanHandle(new DeviceInfo
        {
            Id = "stm32-dfu", VendorId = new HardwareId(0x0483), ProductId = new HardwareId(0xDF11),
        }));
    }

    [Fact]
    public void Identification_is_probe_so_the_provider_is_never_autoflashed()
    {
        // The bridge's VID/PID names the CH340 / FTDI, never the STM32 behind it, so identity
        // needs the AN3155 handshake. Probe keeps it out of unattended autoflash.
        Assert.Equal(IdentificationMode.Probe, new Stm32SerialBootloaderProvider().Identification);
    }

    [Fact]
    public async Task OpenAsync_fails_cleanly_when_the_device_has_no_port_name()
    {
        var provider = new Stm32SerialBootloaderProvider();

        var ex = await Assert.ThrowsAsync<Stm32SerialException>(
            () => provider.OpenAsync(new DeviceInfo { Id = "no-port", Name = "Nothing" }));

        Assert.Contains("no serial port name", ex.Message);
    }

    [Fact]
    public void A_vid_pid_provider_registered_first_wins_over_this_one()
    {
        // Registration order is load-bearing because CanHandle is broad. Matches the comment on
        // the provider and the ordering in the FlashAnything CLI composition.
        var registry = new BootloaderRegistry();
        registry.Register(new StubProvider());
        registry.Register(new Stm32SerialBootloaderProvider());

        var device = new DeviceInfo
        {
            Id = "shared",
            VendorId = new HardwareId(0x1234),
            PortName = new SerialPortName("COM7"),
        };

        Assert.Equal("stub", registry.Match(device)?.Name);
    }

    private sealed class StubProvider : IBootloaderProvider
    {
        public string Name => "stub";
        public bool CanHandle(DeviceInfo device) => device.VendorId == new HardwareId(0x1234);
        public Task<IFirmwareProgrammer> OpenAsync(DeviceInfo device, CancellationToken ct = default)
            => throw new NotSupportedException();
        public IdentificationMode Identification => IdentificationMode.Passive;
    }
}
