// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;

namespace Periphery.Monitor;

/// <summary>
/// What a panel <b>claims to be</b>: the manufacturer and product code from its
/// EDID. This is the display's self-declared identity, not a fact the OS
/// verified — see the remarks, which matter more than the type.
/// </summary>
/// <param name="VendorId">
/// The three-letter PNP vendor code (<c>ACR</c>, <c>SAM</c>, <c>BNQ</c>, …),
/// decoded from EDID's packed manufacturer id.
/// </param>
/// <param name="ProductCode">The manufacturer-assigned product code.</param>
/// <remarks>
/// <para>
/// <b>This is evidence, not a verdict</b> (ADR-0073). It is the cheapest signal
/// that distinguishes a synthetic display from a real one — an
/// <c>IddSampleDriver</c> rig reports <c>LNX0000</c> / "Linux FHD" — but it
/// distinguishes them only because that driver's author chose that EDID.
/// A different virtual driver bakes a different one, and nothing stops a real
/// panel from claiming anything. Treat a match as a <b>fingerprint</b>, and say
/// so where you use it.
/// </para>
/// <para>
/// <see cref="PnpId"/> is deliberately formatted to equal the hardware-id segment
/// Windows puts in the device instance path (<c>DISPLAY\ACR0507\…</c>), so a
/// consumer can match this against <see cref="Periphery.DeviceInfo.Id"/> — or a
/// maintained list of known-synthetic panels — without reformatting.
/// </para>
/// <para>
/// Why the earlier "EDID cannot help" reasoning was wrong: ADR-0070 excluded EDID
/// because a dual-rig box's two virtual displays share one baked EDID, so it
/// cannot tell rig A from rig B. True, and irrelevant — identifying them
/// <i>as a class</i> is a different question from telling them apart, and it is
/// the one a "is this screen synthetic" check actually asks (ADR-0073).
/// </para>
/// </remarks>
public sealed record MonitorPanelIdentity(string VendorId, ushort ProductCode)
{
    /// <summary>
    /// Vendor and product as one token, formatted exactly like the hardware-id
    /// segment in a Windows device instance path — e.g. <c>ACR0507</c> for the
    /// device <c>DISPLAY\ACR0507\5&amp;30fcbbf1&amp;0&amp;UID397571</c>.
    /// </summary>
    public string PnpId => $"{VendorId}{ProductCode:X4}";

    /// <inheritdoc/>
    public override string ToString() => PnpId;
}

/// <summary>
/// The pure decoder for EDID's packed manufacturer id. No IO, total.
/// </summary>
/// <remarks>
/// EDID packs three uppercase letters into 15 bits (five bits each, <c>1</c> =
/// <c>A</c>). Windows surfaces the field in <c>DisplayConfigTargetDeviceName</c>
/// with the two bytes in the opposite order to EDID's own big-endian layout, so
/// the value must be byte-swapped before unpacking — getting this backwards
/// yields plausible-looking garbage rather than an obvious failure, which is why
/// the unit tests pin real measured values.
/// </remarks>
internal static class EdidIdentity
{
    /// <summary>
    /// Decodes a Windows <c>EdidManufactureId</c> / <c>EdidProductCodeId</c> pair.
    /// Returns <see langword="null"/> when the manufacturer id does not decode to
    /// three A–Z letters, which is how an absent or malformed EDID presents —
    /// preferred over emitting a nonsense vendor code that reads as real.
    /// </summary>
    internal static MonitorPanelIdentity? Decode(ushort manufactureId, ushort productCode)
    {
        // Byte-swap into EDID's own order, then unpack three 5-bit letters.
        int packed = ((manufactureId & 0xFF) << 8) | (manufactureId >> 8);

        Span<char> letters = stackalloc char[3];
        for (int i = 0; i < 3; i++)
        {
            int value = (packed >> (10 - (i * 5))) & 0x1F;
            if (value is < 1 or > 26)
                return null;
            letters[i] = (char)('A' + value - 1);
        }

        return new MonitorPanelIdentity(new string(letters), productCode);
    }
}
