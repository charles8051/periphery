using System;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace Periphery.Bootloader.Efm8.Usb.Tests;

/// <summary>
/// Tests the firmware-file resolver / brick-guard: infer the format from the file
/// extension, verify it against the content, convert (.hex) or pass through (.tfi/.efm8).
/// The load-bearing case is the <b>refusal</b> of a file whose content does not match its
/// extension — that is what stops a wrong file from reaching the bootloader.
/// </summary>
public class Efm8FirmwareImageTests
{
    private static byte[] Asset(string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Assets", name));

    // ── Extension inference ─────────────────────────────────────────────

    [Theory]
    [InlineData("fw.hex", Efm8FirmwareFormat.IntelHex)]
    [InlineData("FW.HEX", Efm8FirmwareFormat.IntelHex)]          // case-insensitive
    [InlineData("treehopper.tfi", Efm8FirmwareFormat.BootRecords)]
    [InlineData("image.efm8", Efm8FirmwareFormat.BootRecords)]
    public void FormatFromFileName_Known(string name, Efm8FirmwareFormat expected)
        => Assert.Equal(expected, Efm8FirmwareImage.FormatFromFileName(name));

    [Theory]
    [InlineData("fw.bin")]
    [InlineData("fw")]
    [InlineData("fw.hex.bak")]
    public void FormatFromFileName_Unknown_IsNull(string name)
        => Assert.Null(Efm8FirmwareImage.FormatFromFileName(name));

    // ── Content sniff ───────────────────────────────────────────────────

    [Fact]
    public void Sniff_IntelHex_OnLeadingColon_EvenAfterWhitespace()
    {
        Assert.Equal(Efm8FirmwareFormat.IntelHex, Efm8FirmwareImage.Sniff(":00000001FF\n"u8));
        Assert.Equal(Efm8FirmwareFormat.IntelHex, Efm8FirmwareImage.Sniff("\r\n  :10\n"u8));
    }

    [Fact]
    public void Sniff_BootRecords_OnLeadingDollar()
        => Assert.Equal(Efm8FirmwareFormat.BootRecords, Efm8FirmwareImage.Sniff(new byte[] { 0x24, 0x03, 0x36 }));

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0x00, 0x01 })]
    [InlineData(new byte[] { (byte)'x' })]
    public void Sniff_Neither_IsNull(byte[] content)
        => Assert.Null(Efm8FirmwareImage.Sniff(content));

    // ── ToBootRecords: the happy paths (reuse the hex2boot golden pair) ──

    [Fact]
    public void ToBootRecords_HexFile_ConvertsToBootRecords_MatchingGolden()
    {
        byte[] hex = Asset("synthetic.hex");
        byte[] expected = Asset("synthetic.efm8");

        byte[] records = Efm8FirmwareImage.ToBootRecords(hex, "anything.hex", Efm8BootOptions.Ub1);

        Assert.Equal(expected, records);   // same as converting via the generator directly
    }

    [Fact]
    public void ToBootRecords_BootRecordFile_PassesThroughUnchanged()
    {
        byte[] efm8 = Asset("synthetic.efm8");
        byte[] records = Efm8FirmwareImage.ToBootRecords(efm8, "treehopper.tfi", Efm8BootOptions.Ub1);
        Assert.Equal(efm8, records);
    }

    // ── ToBootRecords: the brick-guard refusals ─────────────────────────

    [Fact]
    public void ToBootRecords_HexContent_NamedTfi_IsRefused()
    {
        // The dangerous case: an Intel HEX image named .tfi. If it were streamed to the
        // bootloader as records it could brick the board. It must be refused.
        byte[] hex = Asset("synthetic.hex");
        var ex = Assert.Throws<Efm8BootFormatException>(
            () => Efm8FirmwareImage.ToBootRecords(hex, "mislabelled.tfi", Efm8BootOptions.Ub1));
        Assert.Contains("brick", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ToBootRecords_BootRecordContent_NamedHex_IsRefused()
    {
        byte[] efm8 = Asset("synthetic.efm8");
        Assert.Throws<Efm8BootFormatException>(
            () => Efm8FirmwareImage.ToBootRecords(efm8, "mislabelled.hex", Efm8BootOptions.Ub1));
    }

    [Fact]
    public void ToBootRecords_UnknownExtension_IsRefused()
    {
        byte[] content = { 0x24, 0x03, 0x36 };   // valid-ish boot record, but the extension is unknown
        var ex = Assert.Throws<Efm8BootFormatException>(
            () => Efm8FirmwareImage.ToBootRecords(content, "fw.bin", Efm8BootOptions.Ub1));
        Assert.Contains("extension", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ToBootRecords_MalformedHex_NamedHex_Throws()
    {
        // Content sniffs as HEX (leading ':') but is not valid Intel HEX -> the generator
        // rejects it. Still caught before any device write.
        byte[] badHex = Encoding.ASCII.GetBytes(":10000000DEADBEEF00\n");   // wrong checksum, no EOF
        Assert.Throws<IntelHexFormatException>(
            () => Efm8FirmwareImage.ToBootRecords(badHex, "fw.hex", Efm8BootOptions.Ub1));
    }

    [Fact]
    public void ToBootRecords_MalformedBootRecords_NamedTfi_Throws()
    {
        // Leading '$' so it sniffs as boot records, but the declared length overruns.
        byte[] badRecords = { 0x24, 0x7F, 0x33, 0x00 };   // says 0x7F bytes follow; only 2 do
        Assert.Throws<Efm8BootFormatException>(
            () => Efm8FirmwareImage.ToBootRecords(badRecords, "fw.tfi", Efm8BootOptions.Ub1));
    }
}
