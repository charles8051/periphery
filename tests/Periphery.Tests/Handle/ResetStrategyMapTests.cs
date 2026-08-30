namespace Periphery.Tests;

/// <summary>
/// Pure-core tests for <see cref="ResetStrategyMap"/> (ADR-0060): transport
/// markers on a <see cref="DeviceInfo"/> in, conceivable reset strategies out,
/// no hardware.
/// </summary>
public class ResetStrategyMapTests
{
    private static DeviceInfo Dev(
        string id = "ROOT\\X",
        BusType bus = BusType.Unknown,
        string? subsystem = null,
        string? ioService = null) => new()
    {
        Id = id,
        BusType = bus,
        Subsystem = subsystem,
        IOServiceClass = ioService,
    };

    [Fact]
    public void Usb_AdvertisesPortCycleThenDisableEnable_GentlestFirst()
    {
        var s = ResetStrategyMap.ForTransport(Dev("USB\\VID_10C4&PID_8A7E\\1", BusType.USB));

        Assert.Equal(2, s.Count);
        Assert.Equal(ResetKind.UsbPortCycle, s[0].Kind);
        Assert.True(s[0].ReEnumerates);                 // a port-cycle re-enumerates
        Assert.Equal(ResetKind.PnpDisableEnable, s[1].Kind);
        // Hardware-measured, not inferred (periphery #251): across disable/enable cycles a watcher
        // filtered to the device's own port + serial saw no edge at all, and the device never left
        // enumeration. Flipping this to true buys no wait — see ResetKind.PnpDisableEnable.
        Assert.False(s[1].ReEnumerates);
        Assert.True(s[0].Kind < s[1].Kind);             // ascending force
    }

    [Fact]
    public void UsbInstanceIdPrefix_AloneMarksUsbBacked()
        => Assert.True(ResetStrategyMap.IsUsbBacked(Dev("USB\\VID_1234&PID_5678\\1")));

    [Fact]
    public void LinuxUsbSubsystem_MarksUsbBacked()
        => Assert.True(ResetStrategyMap.IsUsbBacked(Dev(subsystem: "usb")));

    [Fact]
    public void MacOsIOUsbServiceClass_MarksUsbBacked()
        => Assert.True(ResetStrategyMap.IsUsbBacked(Dev(ioService: "IOUSBHostDevice")));

    [Theory]
    [InlineData(BusType.Bluetooth)]
    [InlineData(BusType.PCI)]
    [InlineData(BusType.Software)]
    [InlineData(BusType.HID)]   // bare HID, no USB id/subsystem: the shell's ancestor walk handles real HID-over-USB
    public void NonUsbTransports_AdvertiseNothing(BusType bus)
        => Assert.Empty(ResetStrategyMap.ForTransport(Dev("ROOT\\X", bus)));

    [Fact]
    public void UsbStrategies_IsTheSharedTable()
    {
        Assert.Equal(ResetStrategyMap.UsbStrategies, ResetStrategyMap.ForTransport(Dev(bus: BusType.USB)));
        Assert.Equal(2, ResetStrategyMap.UsbStrategies.Count);
    }
}
