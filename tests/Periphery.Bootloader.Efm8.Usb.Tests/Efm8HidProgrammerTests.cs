using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Periphery.Firmware;
using Xunit;

namespace Periphery.Bootloader.Efm8.Usb.Tests;

/// <summary>
/// Shell tests for the EFM8 <see cref="IFirmwareProgrammer"/> over the fake transport: it replays a
/// boot-record <see cref="FirmwarePayload"/>, maps the upload outcome/progress onto the platform
/// contract, and gates out any non-EFM8 format. No hardware.
/// </summary>
public class Efm8HidProgrammerTests
{
    private static DeviceInfo Device() => new()
    {
        Id = "efm8-boot",
        VendorId = new HardwareId(0x10C4),
        ProductId = new HardwareId(0xEAC9),
    };

    private static byte[] WellFormedStream() => BootRecordBuilder.Stream(
        BootRecordBuilder.Frame(0x31, 0xA5, 0xF1, 0x00),
        BootRecordBuilder.Frame(0x33, BootRecordBuilder.Bytes(8)),
        BootRecordBuilder.Frame(0x36, 0x00, 0x00)); // last record = run app

    private static FirmwarePayload Payload(byte[] blob) =>
        FirmwarePayload.FromBlob(blob, FirmwareFormat.Efm8BootRecords);

    [Fact]
    public void AcceptedFormats_IsEfm8BootRecordsOnly()
    {
        var prog = Efm8HidProgrammer.CreateForTest(Device(), new FakeEfm8Transport());
        Assert.Equal(new[] { FirmwareFormat.Efm8BootRecords }, prog.AcceptedFormats);
    }

    [Fact]
    public async Task IdentifyAsync_ReportsEfm8Family()
    {
        var prog = Efm8HidProgrammer.CreateForTest(Device(), new FakeEfm8Transport());
        var identity = await prog.IdentifyAsync();
        Assert.Equal("EFM8", identity.Family);
        Assert.Equal(Efm8Protocol.OutputReportSize, identity.TransferSize);
    }

    [Fact]
    public async Task FlashAsync_WellFormedBlob_AllAcked_Succeeds()
    {
        var blob = WellFormedStream();
        var transport = new FakeEfm8Transport(); // acks every record
        var prog = Efm8HidProgrammer.CreateForTest(Device(), transport);

        var result = await prog.FlashAsync(Payload(blob), FlashOptions.Default);

        Assert.True(result.Success);
        Assert.Equal(blob.Length, result.BytesWritten);
        Assert.False(result.Verified); // acked per-record, but no read-back
        Assert.Equal(3, transport.ReadCount);
    }

    [Fact]
    public async Task FlashAsync_ReportsWriteProgressAndDone()
    {
        var transport = new FakeEfm8Transport();
        var prog = Efm8HidProgrammer.CreateForTest(Device(), transport);
        var phases = new List<FlashPhase>();
        var progress = new SyncProgress<FlashProgress>(p => phases.Add(p.Phase));

        var result = await prog.FlashAsync(Payload(WellFormedStream()), FlashOptions.Default, progress);

        Assert.True(result.Success);
        Assert.Contains(FlashPhase.Writing, phases);
        Assert.Equal(FlashPhase.Done, phases[^1]);
    }

    [Fact]
    public async Task FlashAsync_UploadStopsOnNak_ReturnsFailure()
    {
        // Ack record 0, then a CRC-error reply (0x42) on record 1.
        var transport = FakeEfm8Transport.AckThen(ackCount: 1, thenReply: 0x42);
        var prog = Efm8HidProgrammer.CreateForTest(Device(), transport);

        var result = await prog.FlashAsync(Payload(WellFormedStream()), FlashOptions.Default);

        Assert.False(result.Success);
        Assert.Equal(0, result.BytesWritten);
        Assert.Contains("EFM8 upload failed", result.Error);
    }

    [Fact]
    public async Task FlashAsync_RejectsNonEfm8Format_BeforeAnyWrite()
    {
        var transport = new FakeEfm8Transport();
        var prog = Efm8HidProgrammer.CreateForTest(Device(), transport);
        // A Kind-1 memory image is not what an EFM8 flasher takes.
        var memoryImage = FirmwarePayload.FromImage(FirmwareImage.FromBytes(0x0000, new byte[16]), FirmwareFormat.RawBinary);

        var result = await prog.FlashAsync(memoryImage, FlashOptions.Default);

        Assert.False(result.Success);
        Assert.Contains("boot records", result.Error);
        Assert.Empty(transport.Writes); // gated before the transport is touched
    }

    [Fact]
    public async Task FlashAsync_MalformedBlob_ReturnsFailure_NoWrites()
    {
        var transport = new FakeEfm8Transport();
        var prog = Efm8HidProgrammer.CreateForTest(Device(), transport);
        var malformed = Payload(new byte[] { 0x24, 0x0A, 0x33, 0x00, 0x00 }); // length 10 overruns

        var result = await prog.FlashAsync(malformed, FlashOptions.Default);

        Assert.False(result.Success);
        Assert.Empty(transport.Writes); // the uploader parses before writing a byte
    }

    [Fact]
    public async Task LeaveAsync_IsNoOp()
    {
        var prog = Efm8HidProgrammer.CreateForTest(Device(), new FakeEfm8Transport());
        await prog.LeaveAsync(); // completes without touching the transport
    }

    private sealed class SyncProgress<T>(Action<T> onReport) : IProgress<T>
    {
        public void Report(T value) => onReport(value);
    }
}
