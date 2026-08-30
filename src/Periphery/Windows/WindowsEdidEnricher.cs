// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;

namespace Periphery.Windows;

/// <summary>
/// Reads the EDID block Windows cached at enumeration time from
/// <c>HKLM\SYSTEM\CurrentControlSet\Enum\{instanceId}\Device Parameters\EDID</c>
/// and parses the Display Product Name out of it. Used as a tier-3 fallback for
/// <see cref="DeviceInfo.MonitorName"/> when <see cref="WindowsDisplayConfigEnricher"/>
/// can't populate it — see ADR-0044.
/// </summary>
/// <remarks>
/// <para>
/// EDID 1.x base block layout (128 bytes): bytes <c>0x36..0x7D</c> hold four
/// 18-byte descriptor blocks. A "monitor descriptor" (vs detailed-timing
/// descriptor) is signalled by bytes <c>[0..1]</c> being <c>0x0000</c> and
/// byte <c>[2]</c> + byte <c>[4]</c> also being <c>0x00</c>. The tag at byte
/// <c>[3]</c> identifies the descriptor:
/// </para>
/// <list type="bullet">
///   <item><c>0xFC</c> — Display Product Name (what we extract here)</item>
///   <item><c>0xFE</c> — ASCII data string</item>
///   <item><c>0xFD</c> — Display range limits</item>
///   <item><c>0xFF</c> — Display product serial number</item>
/// </list>
/// <para>
/// When the tag is <c>0xFC</c>, bytes <c>[5..17]</c> hold up to 13 ASCII
/// characters of the friendly name, padded with <c>0x0A</c> (line feed),
/// <c>0x20</c> (space), and/or trailing <c>0x00</c>.
/// </para>
/// <para>
/// This helper reads only the base EDID block. CEA-861 extension blocks
/// can carry additional product-name descriptors but the base block is
/// authoritative when present, and parsing extension blocks isn't worth
/// the complexity for the friendly-name use case.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class WindowsEdidEnricher
{
    private const int EdidBaseBlockSize = 128;
    private const int FirstDescriptorOffset = 0x36;
    private const int DescriptorSize = 18;
    private const int DescriptorCount = 4;
    private const byte TagDisplayProductName = 0xFC;
    private const int NameOffsetWithinDescriptor = 5;
    private const int NameMaxLength = 13;

    /// <summary>
    /// Returns the Display Product Name from the monitor's cached EDID, or
    /// <see langword="null"/> if the registry value is missing, the EDID is
    /// malformed, or no <c>0xFC</c> descriptor is present.
    /// </summary>
    /// <param name="instanceId">
    /// The PnP instance ID of the monitor — what <see cref="DeviceInfo.Id"/>
    /// returns for monitors, e.g. <c>"DISPLAY\\DELA1234\\4&amp;1a2b3c4d&amp;0&amp;UID198147"</c>.
    /// </param>
    internal static string? GetMonitorFriendlyName(string instanceId)
    {
        if (string.IsNullOrEmpty(instanceId))
            return null;

        byte[]? edid = ReadEdidBytes(instanceId);
        if (edid is null || edid.Length < EdidBaseBlockSize)
            return null;

        return ParseDisplayProductName(edid);
    }

    /// <summary>
    /// Reads the raw EDID bytes from the device's <c>Device Parameters</c>
    /// registry key. Same key pattern as
    /// <see cref="WindowsPortsEnricher.GetPortName(string)"/>.
    /// </summary>
    private static byte[]? ReadEdidBytes(string instanceId)
    {
        string keyPath = @"SYSTEM\CurrentControlSet\Enum\" + instanceId + @"\Device Parameters";
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(keyPath);
            return key?.GetValue("EDID") as byte[];
        }
        catch
        {
            // Access denied / key shape unexpected / etc. — treat the same as
            // "EDID not available" and let the caller fall through.
            return null;
        }
    }

    /// <summary>
    /// Walks the four monitor descriptor slots and returns the Display
    /// Product Name from the first <c>0xFC</c>-tagged descriptor found.
    /// </summary>
    internal static string? ParseDisplayProductName(byte[] edid)
    {
        for (int i = 0; i < DescriptorCount; i++)
        {
            int offset = FirstDescriptorOffset + i * DescriptorSize;
            if (offset + DescriptorSize > edid.Length)
                break;

            // Monitor-descriptor sentinel: bytes [0..1] = 0x0000, byte [2] = 0x00,
            // byte [4] = 0x00. Anything else is a detailed-timing descriptor
            // (or invalid) and must be skipped.
            if (edid[offset] != 0x00
                || edid[offset + 1] != 0x00
                || edid[offset + 2] != 0x00
                || edid[offset + 4] != 0x00)
                continue;

            if (edid[offset + 3] != TagDisplayProductName)
                continue;

            int nameStart = offset + NameOffsetWithinDescriptor;
            int length = NameMaxLength;
            // Trim trailing LF / NUL / space padding.
            while (length > 0)
            {
                byte b = edid[nameStart + length - 1];
                if (b == 0x0A || b == 0x00 || b == 0x20)
                    length--;
                else
                    break;
            }
            if (length == 0)
                return null;

            string name = Encoding.ASCII.GetString(edid, nameStart, length);
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }

        return null;
    }
}
