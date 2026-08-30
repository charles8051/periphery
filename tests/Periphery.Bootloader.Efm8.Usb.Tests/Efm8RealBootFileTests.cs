using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Periphery.Bootloader.Efm8.Usb.Tests;

/// <summary>
/// End-to-end exercise of a <b>real</b> <c>.efm8</c> file — produced by
/// <c>hex2boot.py</c> from a synthetic Intel HEX (see
/// <c>Assets/README.md</c>) — parsed and replayed against the fake transport. This
/// proves the parser and chunker handle genuine hex2boot output, not just hand-built
/// frames.
/// </summary>
public class Efm8RealBootFileTests
{
    private static byte[] LoadRealBootFile()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "synthetic.efm8");
        Assert.True(File.Exists(path), $"Missing test asset: {path}");
        return File.ReadAllBytes(path);
    }

    [Fact]
    public void RealEfm8File_ParsesIntoExpectedRecordSequence()
    {
        var records = Efm8Protocol.ParseRecords(LoadRealBootFile());

        // hex2boot -m ub1 -b 0 emits: Setup, Erase-with-data, Write, Verify,
        // failsafe reset-vector Write, RunApp.
        byte[] commands = records.Select(r => r.Command).ToArray();
        Assert.Equal(new byte[] { 0x31, 0x32, 0x33, 0x34, 0x33, 0x36 }, commands);

        // The erase-with-data record carries a 128-byte page -> 133-byte frame -> 3 chunks.
        var eraseFrame = records[1].Frame;
        Assert.Equal(133, eraseFrame.Length);
        Assert.Equal(3, Efm8Protocol.ChunkFrame(eraseFrame).Count);

        // The final record is RunApp (resets into the freshly written app).
        Assert.Equal(0x36, records[^1].Command);
    }

    [Fact]
    public async Task RealEfm8File_ReplayedAgainstAckingTransport_Succeeds()
    {
        var image = LoadRealBootFile();
        var records = Efm8Protocol.ParseRecords(image);
        var transport = new FakeEfm8Transport();

        var result = await Efm8BootloaderUploader.UploadAsync(
            transport, image, Efm8FlashConfirmation.ConfirmEraseAndReflash);

        Assert.True(result.Success);
        Assert.Equal(records.Length, result.RecordsSent);
        Assert.Equal(records.Length, transport.ReadCount);
        Assert.Equal(image.Length, result.TotalBytes);

        // Every byte written, concatenated, equals the original file — replay is verbatim.
        var rejoined = transport.Writes.SelectMany(w => w).ToArray();
        Assert.Equal(image, rejoined);
    }
}
