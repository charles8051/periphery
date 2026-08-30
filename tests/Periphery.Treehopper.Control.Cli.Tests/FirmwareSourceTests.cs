using System.IO;
using System.Text;
using Periphery.Bootloader.Efm8.Usb;
using Periphery.Treehopper.Control.Cli;
using Xunit;

namespace Periphery.Treehopper.Control.Cli.Tests;

/// <summary>
/// Tests the CLI's <c>--file</c> resolution: it infers the format from the extension,
/// verifies it against the content, and converts a <c>.hex</c> to boot records — refusing
/// a mismatched file up front (before any board is touched).
/// </summary>
public class FirmwareSourceTests
{
    // A tiny but valid Intel HEX: 4 bytes (reset vector 0x02 + 3) at address 0, then EOF.
    private const string ValidHex = ":040000000211223394\n:00000001FF\n";

    [Fact]
    public void ResolveImage_HexFile_ReturnsBootRecords()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            string path = Path.Combine(dir.FullName, "firmware.hex");
            File.WriteAllText(path, ValidHex);

            var (bytes, _, error) = FirmwareSource.ResolveImage(path);

            Assert.Null(error);
            Assert.NotNull(bytes);
            // It came back as a boot-record stream that parses (starts with '$').
            Assert.Equal(Efm8Protocol.StartByte, bytes![0]);
            Assert.NotEmpty(Efm8Protocol.ParseRecords(bytes));
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void ResolveImage_HexContentNamedTfi_IsRefused()
    {
        // Brick-guard at the CLI boundary: HEX text in a .tfi-named file is rejected,
        // not flashed.
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            string path = Path.Combine(dir.FullName, "mislabelled.tfi");
            File.WriteAllText(path, ValidHex);

            var (bytes, _, error) = FirmwareSource.ResolveImage(path);

            Assert.Null(bytes);
            Assert.NotNull(error);
            Assert.Contains("brick", error!, System.StringComparison.OrdinalIgnoreCase);
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void ResolveImage_MissingFile_ReturnsError()
    {
        var (bytes, _, error) = FirmwareSource.ResolveImage(
            Path.Combine(Path.GetTempPath(), "definitely-not-here-xyz.hex"));
        Assert.Null(bytes);
        Assert.Contains("not found", error!, System.StringComparison.OrdinalIgnoreCase);
    }
}
