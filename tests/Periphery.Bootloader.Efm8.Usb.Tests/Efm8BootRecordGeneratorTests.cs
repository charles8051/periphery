using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Periphery.Bootloader.Efm8.Usb.Tests;

/// <summary>
/// Tests the in-house Intel-HEX -> boot-record generator (the hex2boot replacement).
/// The anchor is a <b>byte-for-byte</b> comparison against a real hex2boot-produced
/// <c>.efm8</c> for the same <c>.hex</c> — on a brick-capable path, "identical to the
/// reference tool" is the contract.
/// </summary>
public class Efm8BootRecordGeneratorTests
{
    private static string AssetPath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Assets", name);

    // ── The golden test: identical to hex2boot -m ub1 -b 0 ──────────────

    [Fact]
    public void FromIntelHex_Ub1_MatchesRealHex2bootOutput_ByteForByte()
    {
        string hex = File.ReadAllText(AssetPath("synthetic.hex"));
        byte[] expected = File.ReadAllBytes(AssetPath("synthetic.efm8"));

        byte[] actual = Efm8BootRecordGenerator.FromIntelHex(hex, Efm8BootOptions.Ub1);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void FromIntelHex_Ub1_RoundTripsThroughParserToExpectedCommands()
    {
        string hex = File.ReadAllText(AssetPath("synthetic.hex"));
        byte[] stream = Efm8BootRecordGenerator.FromIntelHex(hex, Efm8BootOptions.Ub1);

        var records = Efm8Protocol.ParseRecords(stream);
        // Setup, Erase-with-data, Write, Verify, failsafe reset-vector Write, RunApp.
        Assert.Equal(new byte[] { 0x31, 0x32, 0x33, 0x34, 0x33, 0x36 },
            records.Select(r => r.Command).ToArray());
    }

    [Fact]
    public async Task GeneratedStream_UploadsCleanlyAgainstAckingTransport()
    {
        // End-to-end: our generator's output feeds the uploader unchanged.
        string hex = File.ReadAllText(AssetPath("synthetic.hex"));
        byte[] stream = Efm8BootRecordGenerator.FromIntelHex(hex, Efm8BootOptions.Ub1);
        var transport = new FakeEfm8Transport();

        var result = await Efm8BootloaderUploader.UploadAsync(
            transport, stream, Efm8FlashConfirmation.ConfirmEraseAndReflash);

        Assert.True(result.Success);
        Assert.Equal(Efm8Protocol.ParseRecords(stream).Length, result.RecordsSent);
    }

    // ── CRC-16/XMODEM ───────────────────────────────────────────────────

    [Fact]
    public void Crc16Xmodem_KnownCheckVector()
    {
        // The canonical CRC-16/XMODEM check value for the ASCII string "123456789".
        byte[] data = "123456789"u8.ToArray();
        Assert.Equal((ushort)0x31C3, Efm8BootRecordGenerator.Crc16Xmodem(data));
    }

    [Fact]
    public void Crc16Xmodem_SeedChainsAcrossChunks()
    {
        byte[] whole = { 1, 2, 3, 4, 5, 6 };
        ushort once = Efm8BootRecordGenerator.Crc16Xmodem(whole);
        ushort chained = Efm8BootRecordGenerator.Crc16Xmodem(whole.AsSpan(3),
            Efm8BootRecordGenerator.Crc16Xmodem(whole.AsSpan(0, 3)));
        Assert.Equal(once, chained);
    }

    // ── Brick-safety: reset-vector written last ─────────────────────────

    private static IntelHexImage ImageFromBytes(int start, params byte[] data)
    {
        // Build a tiny image by emitting a single Intel HEX data record + EOF.
        int sum = data.Length + ((start >> 8) & 0xFF) + (start & 0xFF) + 0x00;
        foreach (byte b in data) sum += b;
        byte checksum = (byte)(-sum & 0xFF);
        string rec = $":{data.Length:X2}{start:X4}00{string.Concat(data.Select(b => b.ToString("X2")))}{checksum:X2}";
        return IntelHexImage.Parse(rec + "\n:00000001FF\n");
    }

    [Fact]
    public void Failsafe_HoldsResetVector_BlanksItInVerifyPass_AndWritesItLast()
    {
        // Reset vector 0x02 (LJMP opcode) — non-0xFF, so the failsafe applies.
        var image = ImageFromBytes(0, 0x02, 0x11, 0x22, 0x33);
        byte[] stream = Efm8BootRecordGenerator.FromImage(image, Efm8BootOptions.Ub1);
        var records = Efm8Protocol.ParseRecords(stream);

        // The erase-with-data record's first data byte (address 0) is blanked to 0xFF.
        var erase = records.First(r => r.Command == 0x32);
        Assert.Equal(0xFF, erase.Frame.Span[5]);   // $, len, cmd, addrHi, addrLo, data[0]

        // The penultimate record is a 1-byte Write of the REAL reset vector at addr 0,
        // and the last record is RunApp.
        Assert.Equal(0x36, records[^1].Command);
        var resetWrite = records[^2];
        Assert.Equal(0x33, resetWrite.Command);
        Assert.Equal(0x00, resetWrite.Frame.Span[3]); // addrHi
        Assert.Equal(0x00, resetWrite.Frame.Span[4]); // addrLo
        Assert.Equal(0x02, resetWrite.Frame.Span[5]); // the held-back reset vector
    }

    [Fact]
    public void Failsafe_NotApplied_WhenResetVectorAlreadyErased()
    {
        // Reset vector already 0xFF -> nothing to hold back, no extra reset Write.
        var image = ImageFromBytes(0, 0xFF, 0x11, 0x22, 0x33);
        var records = Efm8Protocol.ParseRecords(
            Efm8BootRecordGenerator.FromImage(image, Efm8BootOptions.Ub1));

        Assert.Equal(new byte[] { 0x31, 0x32, 0x34, 0x36 },
            records.Select(r => r.Command).ToArray());   // no second 0x33 reset-write
    }

    [Fact]
    public void Failsafe_NotApplied_ForNonZeroStartOrBank()
    {
        // start != 0 disables the failsafe even with a non-0xFF reset vector.
        var image = ImageFromBytes(0, 0x02, 0x11);
        var records = Efm8Protocol.ParseRecords(
            Efm8BootRecordGenerator.FromImage(image, Efm8BootOptions.Ub1 with { Start = 0x0000, Bank = 1 }));
        Assert.DoesNotContain(records, r => r.Command == 0x33 && r.DeclaredLength == 4); // no 1-byte reset write
    }

    // ── Safety defaults: no Lock; RunApp unless Wait ────────────────────

    [Fact]
    public void NeverEmitsLockRecord_ByDefault()
    {
        var records = Efm8Protocol.ParseRecords(
            Efm8BootRecordGenerator.FromImage(ImageFromBytes(0, 1, 2, 3), Efm8BootOptions.Ub1));
        Assert.DoesNotContain(records, r => r.Command == 0x35);
    }

    [Fact]
    public void Wait_OmitsTrailingRunApp()
    {
        var records = Efm8Protocol.ParseRecords(
            Efm8BootRecordGenerator.FromImage(ImageFromBytes(0, 1, 2, 3), Efm8BootOptions.Ub1 with { Wait = true }));
        Assert.DoesNotContain(records, r => r.Command == 0x36);
    }

    [Fact]
    public void Ids_EmitLeadingIdentifyRecords()
    {
        var records = Efm8Protocol.ParseRecords(
            Efm8BootRecordGenerator.FromImage(ImageFromBytes(0, 1, 2, 3),
                Efm8BootOptions.Ub1 with { Ids = [0x1234] }));
        Assert.Equal(0x30, records[0].Command);                 // Identify leads
        Assert.Equal(0x12, records[0].Frame.Span[3]);           // big-endian id
        Assert.Equal(0x34, records[0].Frame.Span[4]);
        Assert.Equal(0x31, records[1].Command);                 // then Setup
    }

    [Fact]
    public void Setup_CarriesFlashKeysAndBank()
    {
        var records = Efm8Protocol.ParseRecords(
            Efm8BootRecordGenerator.FromImage(ImageFromBytes(0, 1, 2, 3), Efm8BootOptions.Ub1));
        var setup = records.First(r => r.Command == 0x31);
        Assert.Equal(0xA5, setup.Frame.Span[3]);   // keys 0xA5F1
        Assert.Equal(0xF1, setup.Frame.Span[4]);
        Assert.Equal(0x00, setup.Frame.Span[5]);   // bank 0
    }

    // ── Region map: bootloader region is never targeted ─────────────────

    [Fact]
    public void EmptyImage_ProducesOnlySetupAndRunApp()
    {
        // No data anywhere -> no erase/write/verify; just the framing.
        var records = Efm8Protocol.ParseRecords(
            Efm8BootRecordGenerator.FromImage(IntelHexImage.Parse(":00000001FF\n"), Efm8BootOptions.Ub1));
        Assert.Equal(new byte[] { 0x31, 0x36 }, records.Select(r => r.Command).ToArray());
    }

    // ── VerifyOnly: read-only stream, no Erase/Write/RunApp, same CRC as a real flash ────

    [Fact]
    public void VerifyOnly_EmitsOnlySetupAndVerify_NoEraseWriteOrRunApp()
    {
        var image = ImageFromBytes(0, 1, 2, 3);
        var records = Efm8Protocol.ParseRecords(
            Efm8BootRecordGenerator.VerifyOnly(image, Efm8BootOptions.Ub1));

        Assert.Equal(new byte[] { 0x31, 0x34 }, records.Select(r => r.Command).ToArray());
    }

    [Fact]
    public void VerifyOnly_EmptyImage_Throws()
    {
        // An empty image would otherwise yield zero Verify records - just the Setup record, which
        // any board acknowledges trivially - so the upload would report Success without having
        // checked a single byte of flash. Reject it instead of returning a vacuous "match".
        Assert.Throws<ArgumentException>(() =>
            Efm8BootRecordGenerator.VerifyOnly(IntelHexImage.Parse(":00000001FF\n"), Efm8BootOptions.Ub1));
    }

    [Fact]
    public void VerifyOnly_NeverEmitsIdentifyOrLockRecords()
    {
        // Even when the options would ask FromImage to emit them - a verify check has no business
        // identifying a bootloader variant or touching the lock byte.
        var image = ImageFromBytes(0, 1, 2, 3);
        var options = Efm8BootOptions.Ub1 with { Ids = [0x1234], Lock = 0x00FF };
        var records = Efm8Protocol.ParseRecords(Efm8BootRecordGenerator.VerifyOnly(image, options));

        Assert.DoesNotContain(records, r => r.Command is 0x30 or 0x35);
    }

    [Fact]
    public void VerifyOnly_ComputesTheSameCrcAndAddressRangeAsARealFlashsOwnVerifyRecord()
    {
        // The property that actually matters: a verify built independently of a flash must be
        // checking "does the device match a flash of this image," which only holds if its Verify
        // record is byte-for-byte identical to the one a real flash of the same image would send.
        // Reset vector already 0xFF -> the reset-vector failsafe below does not apply, so this is
        // the case where the two really must agree exactly: nothing else makes them diverge.
        var image = ImageFromBytes(0, 0xFF, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77);

        var flashVerify = Efm8Protocol.ParseRecords(
            Efm8BootRecordGenerator.FromImage(image, Efm8BootOptions.Ub1)).Single(r => r.Command == 0x34);
        var checkVerify = Efm8Protocol.ParseRecords(
            Efm8BootRecordGenerator.VerifyOnly(image, Efm8BootOptions.Ub1)).Single(r => r.Command == 0x34);

        Assert.Equal(flashVerify.Frame.ToArray(), checkVerify.Frame.ToArray());
    }

    [Fact]
    public void VerifyOnly_ChecksTheTrueFinalImage_NotTheFlashsTransientlyBlankedFailsafePass()
    {
        // When the reset-vector failsafe DOES apply (a non-0xFF byte at address 0), FromImage's own
        // embedded Verify record deliberately covers a BLANKED address 0 (0xFF) - the failsafe's
        // main write+verify pass never sees the real reset vector; that's written in a separate
        // Write record afterward, once every other byte is already confirmed. A device a completed
        // flash actually leaves behind holds the REAL byte at address 0, not the blanked one. A
        // verify-only check exists to confirm that completed, final state - so it must NOT reuse
        // FromImage's embedded CRC here; the two are legitimately different checks.
        var image = ImageFromBytes(0, 0x02, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77);

        var flashVerify = Efm8Protocol.ParseRecords(
            Efm8BootRecordGenerator.FromImage(image, Efm8BootOptions.Ub1)).Single(r => r.Command == 0x34);
        var checkVerify = Efm8Protocol.ParseRecords(
            Efm8BootRecordGenerator.VerifyOnly(image, Efm8BootOptions.Ub1)).Single(r => r.Command == 0x34);

        Assert.NotEqual(flashVerify.Frame.ToArray(), checkVerify.Frame.ToArray());

        // And the check's CRC is exactly what verifying the raw, un-blanked image by hand computes -
        // i.e. VerifyOnly is checking the real image, not silently reproducing the failsafe's
        // transient view under a different name.
        byte[] whole = image.ToBinary(0, image.MaxAddress + 1);
        ushort expected = Efm8BootRecordGenerator.Crc16Xmodem(whole);
        // Frame: '$'(0) len(1) cmd(2) orgHi(3) orgLo(4) endHi(5) endLo(6) crcHi(7) crcLo(8).
        ushort actualCrc = (ushort)((checkVerify.Frame.Span[7] << 8) | checkVerify.Frame.Span[8]);
        Assert.Equal(expected, actualCrc);
    }

    // Builds a multi-record Intel HEX image from (address, data) pairs, computing each record's
    // checksum for real (the two's-complement of LL+addrHi+addrLo+type+sum(data)) — an extension of
    // ImageFromBytes for the two-region test, which needs data at two disjoint addresses.
    private static IntelHexImage ImageFromRegions(params (int Start, byte[] Data)[] regions)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var (start, data) in regions)
        {
            int sum = data.Length + ((start >> 8) & 0xFF) + (start & 0xFF);
            foreach (byte b in data) sum += b;
            byte checksum = (byte)(-sum & 0xFF);
            sb.Append($":{data.Length:X2}{start:X4}00{string.Concat(data.Select(b => b.ToString("X2")))}{checksum:X2}\n");
        }
        sb.Append(":00000001FF\n");
        return IntelHexImage.Parse(sb.ToString());
    }

    [Fact]
    public void VerifyOnly_OneVerifyRecordPerRegion_WhenImageSpansBothUb1Regions()
    {
        // Ub1's map has two regions ((0x0000,0x3DFF) and (0xF800,0xFBBF)); an image touching both
        // must get one Verify per region, exactly as FromImage would for a real flash.
        var image = ImageFromRegions((0x0000, [0x01, 0x02, 0x03]), (0xF800, [0xAA]));

        var records = Efm8Protocol.ParseRecords(
            Efm8BootRecordGenerator.VerifyOnly(image, Efm8BootOptions.Ub1));

        Assert.Equal(2, records.Count(r => r.Command == 0x34));
    }

    // ── RunAppOnly: the unconditional leave-the-bootloader transfer ─────

    [Fact]
    public void RunAppOnly_IsExactlyOneRunAppRecord()
    {
        var records = Efm8Protocol.ParseRecords(Efm8BootRecordGenerator.RunAppOnly());

        var record = Assert.Single(records);
        Assert.Equal(0x36, record.Command);
    }

    // ── VerifyOnlyFromBlob: rebuilding a verify-only stream from an already-built flash blob ────

    [Fact]
    public void VerifyOnlyFromBlob_EmitsOnlySetupAndVerify()
    {
        var image = ImageFromBytes(0, 0xFF, 0x11, 0x22, 0x33);
        byte[] flashBlob = Efm8BootRecordGenerator.FromImage(image, Efm8BootOptions.Ub1);

        var records = Efm8Protocol.ParseRecords(
            Efm8BootRecordGenerator.VerifyOnlyFromBlob(flashBlob, Efm8BootOptions.Ub1));

        Assert.Equal(new byte[] { 0x31, 0x34 }, records.Select(r => r.Command).ToArray());
    }

    [Fact]
    public void VerifyOnlyFromBlob_MatchesVerifyOnlyOfTheSourceImage_WhenTheFailsafeDoesNotApply()
    {
        // Reset vector already 0xFF -> FromImage's failsafe never blanks anything, so its embedded
        // Verify already covers the true final state. Reconstructing from the blob should agree.
        var image = ImageFromBytes(0, 0xFF, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77);
        byte[] flashBlob = Efm8BootRecordGenerator.FromImage(image, Efm8BootOptions.Ub1);

        var expected = Efm8Protocol.ParseRecords(
            Efm8BootRecordGenerator.VerifyOnly(image, Efm8BootOptions.Ub1)).Single(r => r.Command == 0x34);
        var reconstructed = Efm8Protocol.ParseRecords(
            Efm8BootRecordGenerator.VerifyOnlyFromBlob(flashBlob, Efm8BootOptions.Ub1)).Single(r => r.Command == 0x34);

        Assert.Equal(expected.Frame.ToArray(), reconstructed.Frame.ToArray());
    }

    [Fact]
    public void VerifyOnlyFromBlob_DataLessEraseInvalidatesAnEarlierWriteToTheSamePage()
    {
        // Efm8EraseMode.Separate emits a data-less Erase (Ub1's default WithData mode never does,
        // but the wire format and this reconstruction must still handle it correctly): it resets its
        // WHOLE PAGE to erased flash, not just the address byte the record names. A write to that
        // page followed by such an erase, with no subsequent write to the SAME bytes, must NOT show
        // up in the reconstructed image - only a write to address 5 that lands AFTER the erase does.
        byte[] blob =
        [
            (byte)'$', 6, 0x33, 0x00, 0x00, 0x11, 0x22, 0x33, // Write [0x11,0x22,0x33] at address 0
            (byte)'$', 3, 0x32, 0x00, 0x00,                   // data-less Erase at address 0 (whole 512-byte page)
            (byte)'$', 4, 0x33, 0x00, 0x05, 0x44,             // Write [0x44] at address 5 (survives - after the erase)
        ];

        var expectedImage = IntelHexImage.FromBytes([(5, (byte)0x44)]);
        var expected = Efm8Protocol.ParseRecords(
            Efm8BootRecordGenerator.VerifyOnly(expectedImage, Efm8BootOptions.Ub1)).Single(r => r.Command == 0x34);
        var reconstructed = Efm8Protocol.ParseRecords(
            Efm8BootRecordGenerator.VerifyOnlyFromBlob(blob, Efm8BootOptions.Ub1)).Single(r => r.Command == 0x34);

        Assert.Equal(expected.Frame.ToArray(), reconstructed.Frame.ToArray());
    }

    [Fact]
    public void VerifyOnlyFromBlob_ReconstructsTheTrueFinalImage_NotTheBlobsTransientlyBlankedFailsafeVerify()
    {
        // The case that matters: a real reset vector (never 0xFF - that's erased flash, not a valid
        // vector) triggers FromImage's failsafe. Its OWN embedded Verify record covers address 0
        // transiently blanked to 0xFF; the real byte lands in a separate, later Write record the
        // embedded Verify never re-checks. Naively replaying that embedded Verify (an earlier,
        // buggy revision of this method did exactly that) checks a state the device is never
        // actually left in - every real firmware image would then report a permanent, spurious
        // mismatch. VerifyOnlyFromBlob must instead reconstruct the image from ALL of the blob's
        // Write records (the failsafe's final override included) and match what VerifyOnly computes
        // from the true, original source image.
        var image = ImageFromBytes(0, 0x02, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77);
        byte[] flashBlob = Efm8BootRecordGenerator.FromImage(image, Efm8BootOptions.Ub1);

        var expected = Efm8Protocol.ParseRecords(
            Efm8BootRecordGenerator.VerifyOnly(image, Efm8BootOptions.Ub1)).Single(r => r.Command == 0x34);
        var reconstructed = Efm8Protocol.ParseRecords(
            Efm8BootRecordGenerator.VerifyOnlyFromBlob(flashBlob, Efm8BootOptions.Ub1)).Single(r => r.Command == 0x34);
        var blobsOwnTransientVerify = Efm8Protocol.ParseRecords(flashBlob).Single(r => r.Command == 0x34);

        Assert.Equal(expected.Frame.ToArray(), reconstructed.Frame.ToArray());
        Assert.NotEqual(blobsOwnTransientVerify.Frame.ToArray(), reconstructed.Frame.ToArray());
    }

    [Fact]
    public void VerifyOnlyFromBlob_OneVerifyRecordPerRegion_WhenTheBlobSpansBothUb1Regions()
    {
        var image = ImageFromRegions((0x0000, [0x01, 0x02, 0x03]), (0xF800, [0xAA]));
        byte[] flashBlob = Efm8BootRecordGenerator.FromImage(image, Efm8BootOptions.Ub1);

        var records = Efm8Protocol.ParseRecords(
            Efm8BootRecordGenerator.VerifyOnlyFromBlob(flashBlob, Efm8BootOptions.Ub1));

        Assert.Equal(2, records.Count(r => r.Command == 0x34));
    }

    [Fact]
    public void VerifyOnlyFromBlob_DropsIdentifyAndLockRecordsToo()
    {
        var image = ImageFromBytes(0, 0xFF, 0x11, 0x22, 0x33);
        var options = Efm8BootOptions.Ub1 with { Ids = [0x1234], Lock = 0x00FF };
        byte[] flashBlob = Efm8BootRecordGenerator.FromImage(image, options);

        var records = Efm8Protocol.ParseRecords(Efm8BootRecordGenerator.VerifyOnlyFromBlob(flashBlob, options));

        Assert.DoesNotContain(records, r => r.Command is 0x30 or 0x35);
    }

    [Fact]
    public void VerifyOnlyFromBlob_NoWriteDataInTheBlob_ThrowsViaVerifyOnlysOwnEmptyImageGuard()
    {
        // A hand-assembled Setup-only stream (as if some other tool produced it with no Write
        // records) reconstructs to an empty image - VerifyOnly's own empty-image guard rejects it
        // rather than reporting a trivial match without checking any flash content.
        byte[] setupOnly = [0x24, 0x04, 0x31, 0xA5, 0xF1, 0x00];

        Assert.Throws<ArgumentException>(() =>
            Efm8BootRecordGenerator.VerifyOnlyFromBlob(setupOnly, Efm8BootOptions.Ub1));
    }

    [Fact]
    public void VerifyOnlyFromBlob_MalformedBlob_Throws()
    {
        byte[] malformed = [0x24, 0xFF, 0x31]; // declares 255 bytes but the stream ends immediately

        Assert.Throws<Efm8BootFormatException>(() =>
            Efm8BootRecordGenerator.VerifyOnlyFromBlob(malformed, Efm8BootOptions.Ub1));
    }
}
