using System.Text;
using Periphery.Firmware;
using Xunit;

namespace Periphery.Bootloader.Efm8.Usb.Tests;

/// <summary>The Intel HEX -> EFM8 boot-records conversion seam (slice 3): a thin, part-parameterized wrapper over the generator.</summary>
public class Efm8IntelHexConverterTests
{
    private static readonly byte[] MinimalHex = Encoding.ASCII.GetBytes(":00000001FF\n"); // just the EOF record

    [Fact]
    public void Source_and_target_formats()
    {
        var converter = new Efm8IntelHexConverter(Efm8BootOptions.Ub1);
        Assert.Equal(FirmwareFormat.IntelHex, converter.Source);
        Assert.Equal(FirmwareFormat.Efm8BootRecords, converter.Target);
    }

    [Fact]
    public void Convert_produces_a_well_formed_boot_record_blob()
    {
        var payload = new Efm8IntelHexConverter(Efm8BootOptions.Ub1).Convert(MinimalHex);

        Assert.Equal(FirmwareFormat.Efm8BootRecords, payload.Format);
        Assert.Equal(FirmwareKind.PackagedBlob, payload.Kind);
        Assert.NotEmpty(payload.Blob.ToArray());
        // The output is exactly what the generator yields, and it parses as boot records (no throw).
        Assert.Equal(Efm8BootRecordGenerator.FromIntelHex(Encoding.ASCII.GetString(MinimalHex), Efm8BootOptions.Ub1),
            payload.Blob.ToArray());
        Efm8Protocol.ParseRecords(payload.Blob); // throws Efm8BootFormatException if malformed
    }
}
