// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Text;

namespace Periphery.Linux.DBus.Core;

/// <summary>
/// Where the system bus listens (ADR-0091 D2).
/// </summary>
internal static class DBusAddress
{
    /// <summary>The specification's well-known system bus address, as a socket path.</summary>
    internal const string DefaultSystemBusPath = "/var/run/dbus/system_bus_socket";

    /// <summary>
    /// The socket path from <c>DBUS_SYSTEM_BUS_ADDRESS</c>: the first <c>unix:path=</c> entry.
    /// Anything else, or no value, gives <see cref="DefaultSystemBusPath"/>.
    /// </summary>
    internal static string SystemBusSocketPath(string? environmentValue)
    {
        if (string.IsNullOrEmpty(environmentValue))
            return DefaultSystemBusPath;

        foreach (var entry in environmentValue.Split(';'))
        {
            if (!entry.StartsWith("unix:", StringComparison.Ordinal))
                continue;

            foreach (var pair in entry["unix:".Length..].Split(','))
            {
                if (pair.StartsWith("path=", StringComparison.Ordinal)
                    && Unescape(pair["path=".Length..]) is { Length: > 0 } path)
                    return path;
            }
        }
        return DefaultSystemBusPath;
    }

    // Address values escape bytes as %XX. A malformed escape makes the entry unusable.
    private static string? Unescape(string value)
    {
        if (!value.Contains('%'))
            return value;

        var bytes = new List<byte>(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] != '%')
            {
                bytes.Add((byte)value[i]);
                continue;
            }
            if (i + 2 >= value.Length
                || !byte.TryParse(value.AsSpan(i + 1, 2), System.Globalization.NumberStyles.HexNumber, null, out byte b))
                return null;
            bytes.Add(b);
            i += 2;
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }
}
