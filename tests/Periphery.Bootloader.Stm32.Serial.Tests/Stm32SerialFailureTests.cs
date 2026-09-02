namespace Periphery.Bootloader.Stm32.Serial.Tests;

/// <summary>
/// Regressions for the five review findings on the original package. Each one is a way the flasher
/// failed in a shape a caller could not handle, rather than a wrong byte on the wire.
/// </summary>
public class Stm32SerialFailureTests
{
    private const uint FlashBase = 0x08000000;

    private static readonly DeviceInfo Device = new()
    {
        Id = "stm32-uart",
        Name = "USB Serial Device",
        PortName = new SerialPortName("COM7"),
    };

    // Short deadlines: the refusal cases are proven by a timeout, and the default is 5 s.
    private static readonly Stm32SerialOptions Quick = Stm32SerialOptions.Default with
    {
        CommandTimeout = TimeSpan.FromMilliseconds(250),
        EraseTimeout = TimeSpan.FromMilliseconds(250),
    };

    private static FirmwarePayload Payload(uint address, int length) =>
        FirmwarePayload.FromImage(FirmwareImage.FromBytes(address, new byte[length]), FirmwareFormat.RawBinary);

    // ── Finding 1: a transport failure must reach the caller as a FlashResult ──

    [Fact]
    public async Task Flash_returns_a_failure_when_the_transport_closes_mid_flash()
    {
        // The cable-unplugged case. CallAndResponse raises TransceiverTransportException, which
        // derives straight from Exception — it used to escape FlashAsync entirely, so every caller
        // treating the contract as returning a FlashResult got an unhandled exception instead.
        await using var device = new FakeStm32Bootloader { DisconnectAfterCommands = 3 };
        await using var programmer = new Stm32SerialProgrammer(Device, device, Quick);

        var result = await programmer.FlashAsync(Payload(FlashBase, 2000), FlashOptions.Default);

        Assert.False(result.Success);
        Assert.Contains("transport closed mid-command", result.Error);
    }

    // ── Finding 2: an image outside flash must be refused, not truncated ──

    [Fact]
    public void Plan_refuses_an_image_whose_page_count_would_not_fit_the_wire_half_word()
    {
        // A vendor HEX carrying an option-bytes record near 0x1FFF7800 computes ~196601 pages.
        // The shell narrows the count to a ushort, so this used to wrap silently — and the nearest
        // wrap is 0, which erases one page and then writes into un-erased flash.
        var image = FirmwareImage.FromBytes(0x1FFF7800, new byte[16]);

        var ex = Assert.Throws<Stm32SerialException>(
            () => Stm32SerialPlan.Plan(image, Stm32SerialOptions.Default, FlashOptions.Default));

        Assert.Contains("more than the 65520", ex.Message);
    }

    [Fact]
    public async Task Flash_reports_an_out_of_range_image_as_a_failure_not_an_exception()
    {
        await using var device = new FakeStm32Bootloader();
        await using var programmer = new Stm32SerialProgrammer(Device, device, Quick);

        var result = await programmer.FlashAsync(Payload(0x1FFF7800, 16), FlashOptions.Default);

        Assert.False(result.Success);
        Assert.Contains("Extended Erase can address", result.Error);
        Assert.Empty(device.ErasedPageCounts);   // nothing was erased on the way to finding out
    }

    [Fact]
    public void Plan_accepts_an_image_at_the_page_count_limit()
    {
        // The boundary is inclusive: exactly MaxErasePages is fine.
        int pageSize = Stm32SerialOptions.Default.ErasePageSize;
        uint lastPageStart = FlashBase + (uint)((Stm32SerialPlan.MaxErasePages - 1) * pageSize);

        var steps = Stm32SerialPlan.Plan(
            FirmwareImage.FromBytes(lastPageStart, new byte[1]), Stm32SerialOptions.Default, FlashOptions.Default);

        var erase = Assert.IsType<Stm32SerialStep.ErasePages>(steps[0]);
        Assert.Equal(Stm32SerialPlan.MaxErasePages, erase.PageCount);
    }

    // ── Finding 3: protocol limits are enforced where the caller can see them ──

    [Theory]
    [InlineData(257)]
    [InlineData(512)]
    [InlineData(0)]
    [InlineData(-1)]
    public void WriteChunkSize_outside_the_AN3155_limit_is_rejected_at_construction(int size)
    {
        // It used to be accepted here and throw ArgumentException from inside the flash, out of
        // FlashAsync, uncaught.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Stm32SerialOptions.Default with { WriteChunkSize = size });
    }

    [Fact]
    public void The_other_options_validate_too()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Stm32SerialOptions.Default with { BaudRate = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => Stm32SerialOptions.Default with { ErasePageSize = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Stm32SerialOptions.Default with { CommandTimeout = TimeSpan.Zero });
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Stm32SerialOptions.Default with { EraseTimeout = TimeSpan.FromSeconds(-1) });
        Assert.Equal(256, Stm32SerialOptions.MaxTransferSize);
    }

    // ── Finding 4: a refused Go is a failure, not a successful leave ──

    [Fact]
    public async Task Flash_fails_when_the_bootloader_refuses_Go()
    {
        // The write and the verify both succeeded; only the jump was refused. Reporting success
        // here told the operator the application was running while the part sat in the bootloader.
        await using var device = new FakeStm32Bootloader { RefuseGo = true };
        await using var programmer = new Stm32SerialProgrammer(Device, device, Quick);

        var data = new byte[64];
        var result = await programmer.FlashAsync(
            FirmwarePayload.FromImage(FirmwareImage.FromBytes(FlashBase, data), FirmwareFormat.RawBinary),
            FlashOptions.Default);

        Assert.False(result.Success);
        Assert.Contains("refused Go", result.Error);

        // ...and the bytes did land, which is what the message promises.
        Assert.True(device.Read(FlashBase, 64).SequenceEqual(data));
    }

    [Fact]
    public async Task LeaveAsync_throws_when_the_bootloader_refuses_Go()
    {
        await using var device = new FakeStm32Bootloader { RefuseGo = true };
        await using var programmer = new Stm32SerialProgrammer(Device, device, Quick);

        var ex = await Assert.ThrowsAsync<Stm32SerialException>(() => programmer.LeaveAsync());

        Assert.Contains("refused Go", ex.Message);
    }

    [Fact]
    public async Task A_normal_flash_still_leaves_cleanly()
    {
        // The counterpart: splitting Go into its two round trips must not break the happy path.
        await using var device = new FakeStm32Bootloader();
        await using var programmer = new Stm32SerialProgrammer(Device, device, Quick);

        var result = await programmer.FlashAsync(Payload(FlashBase, 64), FlashOptions.Default);

        Assert.True(result.Success, result.Error);
    }

    // ── Finding 5: OpenAsync wraps a bad port configuration ──

    [Fact]
    public async Task OpenAsync_wraps_a_port_that_cannot_be_opened()
    {
        // COM255 does not exist. The failure must arrive as Stm32SerialException, per the
        // documented contract, rather than as whatever the serial backend happens to throw.
        var missing = new DeviceInfo { Id = "nope", PortName = new SerialPortName("COM255") };

        var ex = await Assert.ThrowsAsync<Stm32SerialException>(
            () => Stm32SerialProgrammer.OpenAsync(missing, Quick));

        Assert.Contains("could not open COM255", ex.Message);
    }
}
