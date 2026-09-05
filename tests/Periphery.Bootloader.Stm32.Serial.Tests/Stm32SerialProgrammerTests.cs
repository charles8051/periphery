namespace Periphery.Bootloader.Stm32.Serial.Tests;

/// <summary>
/// Drives the whole shell against <see cref="FakeStm32Bootloader"/> — a modelled AN3155 device on
/// an in-memory pipe. No port, no RJCP, no hardware.
/// </summary>
public class Stm32SerialProgrammerTests
{
    private const uint FlashBase = 0x08000000;

    private static readonly DeviceInfo Device = new()
    {
        Id = "stm32-uart",
        Name = "USB-SERIAL CH340",
        PortName = new SerialPortName("COM7"),
    };

    private static byte[] Pattern(int length)
    {
        var bytes = new byte[length];
        for (int i = 0; i < length; i++) bytes[i] = (byte)(i * 7 + 1);
        return bytes;
    }

    private static FirmwarePayload Payload(uint address, byte[] data) =>
        FirmwarePayload.FromImage(FirmwareImage.FromBytes(address, data), FirmwareFormat.RawBinary);

    [Fact]
    public async Task Flash_writes_the_image_and_the_read_back_verify_passes()
    {
        await using var device = new FakeStm32Bootloader();
        await using var programmer = new Stm32SerialProgrammer(Device, device);

        var data = Pattern(600);
        var result = await programmer.FlashAsync(Payload(FlashBase, data), FlashOptions.Default);

        Assert.True(result.Success, result.Error);
        Assert.True(result.Verified);
        Assert.Equal(600, result.BytesWritten);
        Assert.True(device.Read(FlashBase, 600).SequenceEqual(data));
    }

    [Fact]
    public async Task Flash_erases_every_page_up_to_the_end_of_the_image()
    {
        await using var device = new FakeStm32Bootloader();
        await using var programmer = new Stm32SerialProgrammer(Device, device);

        await programmer.FlashAsync(Payload(FlashBase, Pattern(3000)), FlashOptions.Default);

        Assert.Equal(new[] { 2 }, device.ErasedPageCounts); // 3000 bytes over 2 KiB pages
    }

    [Fact]
    public async Task Flash_reports_progress_through_to_done()
    {
        await using var device = new FakeStm32Bootloader();
        await using var programmer = new Stm32SerialProgrammer(Device, device);

        // Not Progress<T>: it posts to the captured context, so the assertions would race the
        // callbacks. This sink runs on the reporting thread.
        var phases = new List<FlashPhase>();
        var sink = new SynchronousProgress(p => phases.Add(p.Phase));

        var result = await programmer.FlashAsync(Payload(FlashBase, Pattern(300)), FlashOptions.Default, sink);

        Assert.True(result.Success, result.Error);
        Assert.Contains(FlashPhase.Erasing, phases);
        Assert.Contains(FlashPhase.Writing, phases);
        Assert.Contains(FlashPhase.Verifying, phases);
        Assert.Contains(FlashPhase.Leaving, phases);
        Assert.Equal(FlashPhase.Done, phases[^1]);
    }

    [Fact]
    public async Task Flash_fails_with_the_mismatch_address_when_a_written_byte_does_not_read_back()
    {
        await using var device = new FakeStm32Bootloader();
        device.CorruptOnWrite[FlashBase + 100] = 0xAA;

        await using var programmer = new Stm32SerialProgrammer(Device, device);
        var result = await programmer.FlashAsync(Payload(FlashBase, Pattern(300)), FlashOptions.Default);

        Assert.False(result.Success);
        Assert.Contains("verify FAILED at 0x08000064", result.Error);
    }

    [Fact]
    public async Task Flash_skipping_verify_does_not_catch_a_corrupt_write()
    {
        // The counterpart to the test above: without Verify the flash reports success, which is
        // what makes Verify worth defaulting to on.
        await using var device = new FakeStm32Bootloader();
        device.CorruptOnWrite[FlashBase + 100] = 0xAA;

        await using var programmer = new Stm32SerialProgrammer(Device, device);
        var result = await programmer.FlashAsync(
            Payload(FlashBase, Pattern(300)), FlashOptions.Default with { Verify = false });

        Assert.True(result.Success, result.Error);
        Assert.False(result.Verified);
    }

    [Fact]
    public async Task Flash_refuses_a_packaged_blob_before_touching_the_device()
    {
        await using var device = new FakeStm32Bootloader();
        await using var programmer = new Stm32SerialProgrammer(Device, device);

        var blob = FirmwarePayload.FromBlob(new byte[] { 0x24, 0x00 }, FirmwareFormat.Efm8BootRecords);
        var result = await programmer.FlashAsync(blob, FlashOptions.Default);

        Assert.False(result.Success);
        Assert.Contains("cannot flash", result.Error);
        Assert.Empty(device.ErasedPageCounts);
    }

    [Fact]
    public async Task Flash_mass_erases_with_the_special_code_rather_than_a_page_list()
    {
        // AN3155 3.7's 0xFFFF is one command with no page list. It used to be unreachable, so the
        // shell refused EraseMode.Mass outright rather than quietly doing a page erase instead.
        await using var device = new FakeStm32Bootloader();
        await using var programmer = new Stm32SerialProgrammer(Device, device);

        var result = await programmer.FlashAsync(
            Payload(FlashBase, Pattern(16)), FlashOptions.Default with { Erase = EraseMode.Mass });

        Assert.True(result.Success, result.Error);
        Assert.Equal(1, device.MassErases);
        Assert.Empty(device.ErasedPageCounts);
    }

    [Fact]
    public async Task Flash_erases_only_the_pages_the_image_covers_unless_mass_is_asked_for()
    {
        // The counterpart to the test above: Auto must not become a mass erase now that one is
        // available. Erasing flash the caller did not ask about is not an upgrade.
        await using var device = new FakeStm32Bootloader();
        await using var programmer = new Stm32SerialProgrammer(Device, device);

        var result = await programmer.FlashAsync(
            Payload(FlashBase, Pattern(16)), FlashOptions.Default with { Erase = EraseMode.Auto });

        Assert.True(result.Success, result.Error);
        Assert.Equal(0, device.MassErases);
        Assert.Equal(new[] { 1 }, device.ErasedPageCounts);
    }

    [Fact]
    public async Task Flash_succeeds_when_stale_bytes_are_already_sitting_on_the_line()
    {
        // A stray ACK is the dangerous case: SendReceivePerfectMatch scans the whole accumulated
        // buffer, so an undrained one satisfies the next command's match before the device has
        // answered, and every reply after that is off by one frame.
        await using var device = new FakeStm32Bootloader();
        await device.InjectNoiseAsync(FakeStm32Bootloader.Ack, FakeStm32Bootloader.Nack, 0x00);

        await using var programmer = new Stm32SerialProgrammer(Device, device);
        var data = Pattern(300);
        var result = await programmer.FlashAsync(Payload(FlashBase, data), FlashOptions.Default);

        Assert.True(result.Success, result.Error);
        Assert.True(device.Read(FlashBase, 300).SequenceEqual(data));
    }

    [Fact]
    public async Task Identify_reads_the_product_id_and_protocol_version()
    {
        // Also covers the drain: Get leaves its trailing ACK buffered, and Get ID reads a fixed
        // five bytes, so an undrained pipe shifts the id by one byte.
        await using var device = new FakeStm32Bootloader { ProductId = 0x0413, ProtocolVersion = 0x31 };
        await using var programmer = new Stm32SerialProgrammer(Device, device);

        var identity = await programmer.IdentifyAsync();

        Assert.Equal("STM32", identity.Family);
        Assert.Equal("0x413", identity.Chip);          // the client's own GetId returns the trailing ACK
        Assert.Equal("3.1", identity.BootloaderVersion);
        Assert.Contains("WriteMemory", identity.SupportedCommands);
        Assert.Contains("ExtendedEraseMemory", identity.SupportedCommands);
    }

    [Fact]
    public async Task Accepted_formats_are_the_memory_image_kinds()
    {
        await using var device = new FakeStm32Bootloader();
        await using var programmer = new Stm32SerialProgrammer(Device, device);

        Assert.Equal(
            new[] { FirmwareFormat.IntelHex, FirmwareFormat.RawBinary, FirmwareFormat.Elf },
            programmer.AcceptedFormats);
    }

    [Fact]
    public async Task Flash_writes_each_segment_at_its_own_address()
    {
        await using var device = new FakeStm32Bootloader();
        await using var programmer = new Stm32SerialProgrammer(Device, device);

        var low = Pattern(16);
        var high = Pattern(32);
        var image = FirmwareImage.FromSegments(new[]
        {
            new FirmwareSegment(FlashBase, low),
            new FirmwareSegment(FlashBase + 0x1000, high),
        });

        var result = await programmer.FlashAsync(
            FirmwarePayload.FromImage(image, FirmwareFormat.IntelHex), FlashOptions.Default);

        Assert.True(result.Success, result.Error);
        Assert.True(device.Read(FlashBase, 16).SequenceEqual(low));
        Assert.True(device.Read(FlashBase + 0x1000, 32).SequenceEqual(high));
    }

    private sealed class SynchronousProgress(Action<FlashProgress> report) : IProgress<FlashProgress>
    {
        public void Report(FlashProgress value) => report(value);
    }
}
