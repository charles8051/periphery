// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Spectre.Console;

namespace Periphery.Cli.Rendering;

internal static class CategoryMeta
{
    public static (string Label, string Hex) Get(DeviceCategory cat) => cat switch
    {
        DeviceCategory.Usb => ("USB", "#00ff87"),
        DeviceCategory.Bluetooth => ("Bluetooth", "#00afff"),
        DeviceCategory.Network => ("Network", "#00d75f"),
        DeviceCategory.Display => ("Display", "#ffd700"),
        DeviceCategory.Monitor => ("Monitor", "#ffaf00"),
        DeviceCategory.Hid => ("HID", "#ff5faf"),
        DeviceCategory.Keyboard => ("Keyboard", "#af87ff"),
        DeviceCategory.Mouse => ("Mouse", "#ff87af"),
        DeviceCategory.Audio => ("Audio", "#5fd7ff"),
        DeviceCategory.Storage => ("Storage", "#ffaf5f"),
        DeviceCategory.Camera => ("Camera", "#ff5fd7"),
        DeviceCategory.Battery => ("Battery", "#87ff00"),
        DeviceCategory.Ports => ("Ports", "#d7875f"),
        _ => ("Other", "#808080"),
    };

    public static Color HexColor(string hex)
    {
        var h = hex.TrimStart('#');
        return new Color(
            Convert.ToByte(h[0..2], 16),
            Convert.ToByte(h[2..4], 16),
            Convert.ToByte(h[4..6], 16)
        );
    }

    public static string Detail(DeviceInfo d)
    {
        if (d.Category is DeviceCategory.Network or DeviceCategory.Bluetooth && d.MacAddress is not null)
            return d.MacAddress.ToString();
        if (d.Category == DeviceCategory.Network && d.IPAddresses is { Length: > 0 })
            return d.IPAddresses.Value[0].ToString();
        if (d.Category is DeviceCategory.Display or DeviceCategory.Monitor && d.DisplayResolution is { } res)
            return $"{res.Width}×{res.Height}";
        if (d.Category == DeviceCategory.Storage && d.DriveType is { } dt)
            return dt.ToString();
        if (d.VendorId is not null && d.ProductId is not null)
            return $"{d.VendorId}:{d.ProductId}";
        if (d.SerialNumber is not null)
            return d.SerialNumber;
        if (d.DriverVersion is not null)
            return $"v{d.DriverVersion}";
        return string.Empty;
    }
}
