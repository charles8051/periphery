// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Globalization;

namespace Periphery;

/// <summary>
/// A 48-bit Bluetooth device address (BD_ADDR). Stores the address as a number and renders it as
/// six colon-separated octets, most significant first (<c>"A0:B1:C2:D3:E4:F5"</c>).
/// </summary>
/// <remarks>
/// <para><b>A join key, not an identity</b> (ADR-0083, ADR-0085 D5). It matches a Periphery
/// device node to a Bluetooth library's device object, and it lasts only as long as the address it
/// was read from. On Windows (issue #232) it held across disconnects, peripheral reboots, host
/// reboots and RPA rotations, for a static-random peripheral and for one using private addresses.
/// A re-pair is where it can change. Windows keys a private-address bond by the address it saw
/// at pairing, so a re-paired private-address peripheral comes back under a new address, as
/// does one that picks a new static address when it re-pairs. A peripheral that keeps one static
/// address keeps its key.</para>
/// <para>Only Windows instance IDs carry the address; see <see cref="TryParseInstanceId"/>. On
/// Windows <see cref="DeviceInfo.MacAddress"/> is <see langword="null"/> for a Bluetooth node (issue
/// #301). On Linux a bonded device comes from BlueZ, and its <see cref="DeviceInfo.MacAddress"/>
/// holds the address (ADR-0091).</para>
/// </remarks>
public readonly record struct BluetoothAddress : IFormattable
{
    private const ulong MaxValue = 0xFFFF_FFFF_FFFF;
    private const int Digits = 12;
    private const int SeparatedLength = 17;

    /// <summary>The address as a number, in the low 48 bits.</summary>
    public ulong Value { get; }

    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> does not fit in 48 bits.</exception>
    public BluetoothAddress(ulong value)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value, MaxValue);
        Value = value;
    }

    // ── Parsing ────────────────────────────────────────────────────────

    /// <summary>
    /// Parses six two-digit hex octets separated by <c>:</c> or <c>-</c>
    /// (<c>"A0:B1:C2:D3:E4:F5"</c>), or one to twelve hex digits with no separator
    /// (<c>"A0B1C2D3E4F5"</c>). Throws <see cref="FormatException"/> on failure.
    /// </summary>
    public static BluetoothAddress Parse(string s)
        => TryParse(s, out var address)
            ? address
            : throw new FormatException($"'{s}' is not a valid Bluetooth address.");

    /// <summary>
    /// Parses six two-digit hex octets separated by <c>:</c> or <c>-</c>
    /// (<c>"A0:B1:C2:D3:E4:F5"</c>), or one to twelve hex digits with no separator
    /// (<c>"A0B1C2D3E4F5"</c>). Case-insensitive.
    /// </summary>
    /// <remarks>
    /// The unseparated form accepts fewer than twelve digits because 32feet's Windows
    /// <c>BluetoothDevice.Id</c> drops leading zeros, so an address that begins <c>00:</c> comes
    /// back as ten digits. Parsing both sides before comparing makes them equal.
    /// </remarks>
    public static bool TryParse(string? s, out BluetoothAddress result)
    {
        result = default;
        if (s is null)
            return false;

        if (s.Length == SeparatedLength)
            return TryParseSeparated(s, out result);

        if (s.Length is 0 or > Digits
            || !ulong.TryParse(s, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong value))
        {
            return false;
        }

        result = new BluetoothAddress(value);
        return true;
    }

    private static bool TryParseSeparated(ReadOnlySpan<char> s, out BluetoothAddress result)
    {
        result = default;
        char separator = s[2];
        if (separator is not (':' or '-'))
            return false;

        ulong value = 0;
        for (int i = 0; i < 6; i++)
        {
            int start = i * 3;
            if (i > 0 && s[start - 1] != separator)
                return false;
            if (!byte.TryParse(s.Slice(start, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out byte octet))
                return false;
            value = (value << 8) | octet;
        }

        result = new BluetoothAddress(value);
        return true;
    }

    /// <summary>
    /// Reads the address from the instance ID of the Windows node that represents a remote
    /// Bluetooth device: <c>BTHENUM\DEV_&lt;address&gt;</c> for BR/EDR and
    /// <c>BTHLE\DEV_&lt;address&gt;</c> for LE, optionally followed by <c>\</c> and an instance
    /// suffix (ADR-0085 D5).
    /// </summary>
    /// <remarks>
    /// A peripheral's service and function nodes embed the same address elsewhere in their IDs,
    /// and return <see langword="false"/>. So does every Linux and macOS instance ID: neither
    /// platform names a device node after its Bluetooth address (issues #258, #259).
    /// </remarks>
    /// <param name="instanceId">A device instance ID, usually <see cref="DeviceInfo.Id"/>'s value.</param>
    /// <param name="address">The address, or <see langword="default"/> when this returns <see langword="false"/>.</param>
    /// <param name="transport">The node's transport, or <see cref="BluetoothTransport.Unknown"/> when this returns <see langword="false"/>.</param>
    public static bool TryParseInstanceId(string? instanceId, out BluetoothAddress address, out BluetoothTransport transport)
    {
        address = default;
        transport = BluetoothTransport.Unknown;
        if (instanceId is null)
            return false;

        const string brEdrPrefix = @"BTHENUM\DEV_";
        const string lePrefix = @"BTHLE\DEV_";
        int start;
        BluetoothTransport candidate;
        if (instanceId.StartsWith(brEdrPrefix, StringComparison.OrdinalIgnoreCase))
        {
            start = brEdrPrefix.Length;
            candidate = BluetoothTransport.BrEdr;
        }
        else if (instanceId.StartsWith(lePrefix, StringComparison.OrdinalIgnoreCase))
        {
            start = lePrefix.Length;
            candidate = BluetoothTransport.LowEnergy;
        }
        else
        {
            return false;
        }

        var rest = instanceId.AsSpan(start);
        if (rest.Length < Digits || (rest.Length > Digits && rest[Digits] != '\\'))
            return false;

        if (!ulong.TryParse(rest[..Digits], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong value))
            return false;

        address = new BluetoothAddress(value);
        transport = candidate;
        return true;
    }

    // ── Queries ────────────────────────────────────────────────────────

    /// <summary>Reads the two most significant bits as an LE random address's sub-type.</summary>
    /// <remarks>
    /// Meaningful only for an address already known to be random. The address itself does not say
    /// whether it is public or random, and a public address's top bits mean nothing. The stack
    /// records which: <c>LE_RANDOM_ADDRESS_TYPE</c> in <c>DEVPKEY_Bluetooth_DeviceFlags</c> on
    /// Windows, <c>Device1.AddressType</c> on BlueZ. A BR/EDR address is always public.
    /// </remarks>
    public BluetoothRandomAddressKind ClassifyAsRandom() => (BluetoothRandomAddressKind)(Value >> 46);

    // ── Formatting ─────────────────────────────────────────────────────

    /// <summary>Six colon-separated uppercase hex octets, most significant first (<c>"A0:B1:C2:D3:E4:F5"</c>).</summary>
    public override string ToString()
    {
        Span<char> text = stackalloc char[SeparatedLength];
        for (int i = 0; i < 6; i++)
        {
            if (i > 0)
                text[i * 3 - 1] = ':';
            byte octet = (byte)(Value >> (40 - 8 * i));
            octet.TryFormat(text.Slice(i * 3, 2), out _, "X2", CultureInfo.InvariantCulture);
        }
        return new string(text);
    }

    /// <summary>
    /// A null or empty <paramref name="format"/> gives the colon-separated form. Any other format
    /// applies to <see cref="Value"/>, so <c>"X12"</c> gives twelve unseparated digits.
    /// </summary>
    public string ToString(string? format, IFormatProvider? formatProvider)
        => string.IsNullOrEmpty(format) ? ToString() : Value.ToString(format, formatProvider);
}
