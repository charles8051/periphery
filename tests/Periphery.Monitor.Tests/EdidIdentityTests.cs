using System;
using System.Threading.Tasks;
namespace Periphery.Monitor.Tests;

/// <summary>
/// Pins the EDID manufacturer/product decode (ADR-0073).
/// </summary>
/// <remarks>
/// Every case below is a <b>measured pair</b>: the raw
/// <c>EdidManufactureId</c>/<c>EdidProductCodeId</c> read from
/// <c>DisplayConfigTargetDeviceName</c> on real hardware, and the hardware-id
/// segment Windows independently put in that monitor's device instance path.
/// The two come from different Windows APIs, so agreeing on all four is a real
/// cross-check rather than a restatement of the implementation.
/// <para>This matters because the byte order is the easy thing to get wrong, and
/// getting it wrong yields a plausible-looking three-letter code rather than an
/// obvious failure — <c>ACR</c> decoded without the swap is <c>GD</c>-ish
/// garbage for some inputs but a real-looking vendor for others.</para>
/// </remarks>
public class EdidIdentityTests
{
    [Theory]
    // raw manufacturer, raw product, expected — all four measured on real panels.
    [InlineData((ushort)0x7204, (ushort)0x0507, "ACR", "ACR0507")]
    [InlineData((ushort)0x2D4C, (ushort)0x7089, "SAM", "SAM7089")]
    [InlineData((ushort)0xD109, (ushort)0x7F31, "BNQ", "BNQ7F31")]
    [InlineData((ushort)0x6904, (ushort)0x24C4, "ACI", "ACI24C4")]
    public void Decode_MatchesTheDeviceInstancePathObservedOnHardware(
        ushort manufactureId, ushort productCode, string vendor, string pnpId)
    {
        var id = EdidIdentity.Decode(manufactureId, productCode);

        Assert.NotNull(id);
        Assert.Equal(vendor, id!.VendorId);
        Assert.Equal(productCode, id.ProductCode);
        // The whole point: this string is what a consumer matches against a
        // DeviceId / a known-synthetic list, so it must be byte-identical to the
        // segment Windows puts in DISPLAY\<here>\...
        Assert.Equal(pnpId, id.PnpId);
    }

    [Fact]
    public void Decode_ProducesTheIddSampleDriverFingerprint()
    {
        // An IddSampleDriver rig. Its own test because a consumer's IsVirtual
        // check can key on this exact string, so a regression here silently
        // breaks that classification rather than just a display string.
        //
        // Both sides measured on the same host, from independent sources:
        //   INPUT  — the panel's own EDID blob, bytes 8..9 = 31 D8, read from
        //            HKLM\SYSTEM\CurrentControlSet\Enum\DISPLAY\LNX0000\<inst>\
        //            Device Parameters\EDID. Windows surfaces those two bytes
        //            swapped in DisplayConfigTargetDeviceName, hence 0xD831.
        //   OUTPUT — the device instance path Windows independently assigned,
        //            `DISPLAY\LNX0000\1&28a6823a&0&UID256` (and ...UID257).
        // So this is a cross-check between the raw EDID store and the PnP
        // enumerator, not the decoder agreeing with itself.
        var id = EdidIdentity.Decode(0xD831, 0x0000);

        Assert.NotNull(id);
        Assert.Equal("LNX0000", id!.PnpId);
    }

    [Theory]
    [InlineData((ushort)0x0000)] // all-zero: absent EDID
    [InlineData((ushort)0xFFFF)] // all-ones: unwritten / malformed
    public void Decode_MalformedManufacturerId_IsNull_NotAFabricatedVendor(ushort manufactureId)
    {
        // Returning null beats emitting a nonsense three-letter code that reads
        // like a real vendor to anything matching on it.
        Assert.Null(EdidIdentity.Decode(manufactureId, 0x1234));
    }

    [Fact]
    public void PnpId_PadsTheProductCodeToFourHexDigits()
    {
        // DISPLAY\LNX0000\... — an unpadded "LNX0" would match nothing.
        Assert.Equal("LNX0000", new MonitorPanelIdentity("LNX", 0x0000).PnpId);
        Assert.Equal("ACR0507", new MonitorPanelIdentity("ACR", 0x0507).PnpId);
    }

    /// <summary>
    /// Pins the END-TO-END wiring — <c>CcdLayout.Read</c> →
    /// <c>EdidIdentity.Decode</c> → <see cref="MonitorLayoutEntry.PanelId"/> —
    /// which the pure-function tests above cannot reach.
    /// </summary>
    /// <remarks>
    /// The invariant is a genuine cross-check available at runtime on any real
    /// box: Windows derives the device instance path's hardware-id segment
    /// (<c>DISPLAY\ACR0507\…</c>) from the panel's EDID through the PnP
    /// enumerator, while <c>PanelId</c> is decoded from the EDID fields that
    /// <c>DisplayConfigGetDeviceInfo</c> reports. Two independent Windows paths
    /// from the same physical source, so they must agree — <b>including when
    /// there is no EDID at all</b>.
    /// <para>That negative case is not hypothetical and is what this test
    /// originally got wrong: a display that supplies no EDID (a headless/EDID-less
    /// adapter, a KVM that drops it, a panel that is off) is enumerated by PnP as
    /// <c>DISPLAY\Default_Monitor\…</c> with no <c>EDID</c> value in its registry
    /// <c>Device Parameters</c>, and <c>GET_TARGET_NAME</c> reports
    /// <c>edidIdsValid = 0</c> with zeroed manufacturer/product fields. The
    /// correct <c>PanelId</c> there is <see langword="null"/>, exactly as
    /// <c>EdidIdentity.Decode</c> documents and as
    /// <see cref="MonitorLayoutEntry.PanelId"/>'s nullability says — asserting a
    /// non-null id for every monitor demanded a fabricated vendor code, the very
    /// thing the malformed-input test above forbids. Measured on a Windows 11 box
    /// 2026-08-20: one active path, <c>DISPLAY\Default_Monitor\1&amp;c528b8a&amp;0&amp;UID256</c>,
    /// flags <c>0x2</c> (<c>friendlyNameForced</c>, <c>edidIdsValid</c> clear).</para>
    /// <para>Catches what the pure tests structurally cannot: a byte-order change
    /// in how Windows reports <c>EdidManufactureId</c>, a struct-layout drift in
    /// <c>DisplayConfigTargetDeviceName</c>, or a mis-wired field — each of which
    /// leaves <c>EdidIdentity.Decode</c> passing against its own inputs while the
    /// live path silently produces wrong fingerprints.</para>
    /// <para>Integration-tier: needs a real display, so it runs on the device rig
    /// rather than gating PRs.</para>
    /// </remarks>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task PanelId_MatchesTheHardwareIdSegmentWindowsPutInTheDeviceInstancePath()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var layout = await MonitorLayout.ReadAsync();
        if (layout.Availability != MonitorLayoutAvailability.Available)
            return; // headless, or a session that cannot see displays (#207).

        foreach (var entry in layout.Monitors)
        {
            // DISPLAY\ACR0507\5&30fcbbf1&0&UID397571 -> "ACR0507". Case varies by
            // API (issue #190), hence the ignore-case comparisons below.
            string[] segments = entry.DeviceId.Value.Split('\\');
            Assert.True(
                segments.Length >= 2,
                $@"not an instance id of the form DISPLAY\<hardware-id>\<instance>: {entry.DeviceId.Value}");
            string hardwareId = segments[1];

            if (string.Equals(hardwareId, "Default_Monitor", StringComparison.OrdinalIgnoreCase))
            {
                // The panel supplied no EDID, so PnP had no vendor/product to name
                // the node after and there is nothing to decode. Both sides must
                // say so: a non-null PanelId here would be an invented identity.
                Assert.Null(entry.PanelId);
                continue;
            }

            Assert.NotNull(entry.PanelId);
            // The whole point: this string is what a consumer matches against a
            // DeviceId / a known-synthetic list, so it must equal the segment
            // Windows put in DISPLAY\<here>\...
            Assert.Equal(hardwareId, entry.PanelId!.PnpId, StringComparer.OrdinalIgnoreCase);
        }
    }
}
