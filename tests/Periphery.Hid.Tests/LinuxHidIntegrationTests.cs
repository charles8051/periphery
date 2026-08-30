using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Periphery;
using Periphery.Hid;
using Periphery.Hid.Codecs;
using Xunit;

namespace Periphery.Hid.Tests;

/// <summary>
/// Device-backed hidraw tests. They run only on the Linux device rig
/// (the Linux device rig), where <c>PERIPHERY_LINUX_DEVICE_TESTS=1</c> and the
/// periphery-uhid-ups systemd service publishes a virtual Megatec Q1 UPS at
/// 0665:5161 over /dev/uhid. On the rig, a missing device is a hard failure —
/// never a skip.
/// </summary>
public class LinuxHidIntegrationTests
{
    private static bool Enabled =>
        OperatingSystem.IsLinux()
        && Environment.GetEnvironmentVariable("PERIPHERY_LINUX_DEVICE_TESTS") == "1";

    private static async Task<DeviceInfo> FindVirtualUpsAsync()
    {
        var devices = await Devices.Enumerate()
            .OfCategory(DeviceCategory.Hid)
            .ToListAsync();

        var ups = devices.FirstOrDefault(d =>
            d.VendorId?.Value == 0x0665
            && d.ProductId?.Value == 0x5161
            && d.Subsystem == "hid");

        Assert.True(ups is not null,
            "virtual UPS (0665:5161) not found — is the periphery-uhid-ups service running? "
            + $"Saw {devices.Count} HID devices.");
        return ups!;
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task VirtualUps_OpensWithDescriptorDerivedCaps()
    {
        if (!Enabled) return;

        var info = await FindVirtualUpsAsync();
        await using var hid = await HidDevice.OpenAsync(info);

        Assert.Equal(0xFF00, hid.UsagePage);
        Assert.Equal(0x01, hid.Usage);
        Assert.Equal(8, hid.MaxInputReportLength);
        Assert.Equal(8, hid.MaxOutputReportLength);
        Assert.Equal(8, hid.MaxFeatureReportLength);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task VirtualUps_MegatecQ1RoundTrip_OverOutputAndInputReports()
    {
        if (!Enabled) return;

        var info = await FindVirtualUpsAsync();
        await using var hid = await HidDevice.OpenAsync(info);

        // Send "Q1\r" zero-padded to the report length, the Megatec framing.
        var command = new byte[8];
        Encoding.ASCII.GetBytes("Q1\r").CopyTo(command, 0);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await hid.WriteReportAsync(new HidReport(0, command), cts.Token);

        // Accumulate the ASCII response across 8-byte input reports until CR.
        var response = new StringBuilder();
        bool terminated = false;
        while (!terminated)
        {
            var report = await hid.ReadReportAsync(cts.Token);
            foreach (byte b in report.Data.ToArray())
            {
                if (b == 0) continue;       // Report padding.
                if (b == (byte)'\r') { terminated = true; break; }
                response.Append((char)b);
            }
        }

        string line = response.ToString();
        Assert.StartsWith("(", line);
        Assert.Contains("229.0", line);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task VirtualUps_FeatureReportRead_RoundTrips()
    {
        if (!Enabled) return;

        var info = await FindVirtualUpsAsync();
        await using var hid = await HidDevice.OpenAsync(info);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var report = await hid.ReadFeatureReportAsync(0, cts.Token);

        Assert.Equal(
            "PERIPH",
            Encoding.ASCII.GetString(report.Data.ToArray(), 0, 6));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task VirtualUps_BatterySnapshot_EndToEndThroughCodec()
    {
        if (!Enabled) return;

        // The full ADR-0048 stack: enumeration identity -> HidQuirks codec
        // lookup (0665:5161 -> MegatecQxCodec) -> open -> Q1 round trip ->
        // parsed snapshot. The canned status line reports utility power OK.
        var info = await FindVirtualUpsAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var snapshot = await HidBattery.ReadSnapshotAsync(info, cts.Token);

        Assert.NotNull(snapshot);
        Assert.True(snapshot!.Value.IsExternalPowerConnected);
        Assert.NotEqual(true, snapshot.Value.IsBatteryLow);
        Assert.NotNull(snapshot.Value.BatteryChargePercent);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task VirtualUps_ReadCancellation_WakesPromptly()
    {
        if (!Enabled) return;

        var info = await FindVirtualUpsAsync();
        await using var hid = await HidDevice.OpenAsync(info);

        // No command sent — the device is silent, so the read blocks in
        // poll(2) until the eventfd wake. Cancel after 250 ms and require
        // prompt observation (well under the 5 s failure budget).
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => hid.ReadReportAsync(cts.Token));
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5),
            $"cancellation took {sw.Elapsed} — the poll wake-up path is broken");
    }
}
