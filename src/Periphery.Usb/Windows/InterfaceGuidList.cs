// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.Generic;

namespace Periphery.Usb.Windows;

/// <summary>
/// Parses the interface GUIDs a WinUSB INF writes to a device's hardware key. Pure: the
/// registry read lives in <see cref="WinUsbBackend"/>.
/// </summary>
internal static class InterfaceGuidList
{
    /// <summary>
    /// Parses a REG_SZ or REG_MULTI_SZ value: GUIDs separated by NULs, with or without braces,
    /// in either case. Entries that are not GUIDs are skipped. Order is kept and duplicates are
    /// dropped, so the GUID the INF listed first is tried first.
    /// </summary>
    public static IReadOnlyList<Guid> Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var guids = new List<Guid>();
        foreach (var entry in value.Split('\0', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Guid.TryParse(entry, out var guid) && !guids.Contains(guid))
                guids.Add(guid);
        }
        return guids;
    }
}
