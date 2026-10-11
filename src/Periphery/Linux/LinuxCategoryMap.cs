// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Versioning;

namespace Periphery.Linux;

/// <summary>
/// Maps <see cref="DeviceCategory"/> values to Linux udev subsystem strings
/// and resolves subsystems (with optional udev property hints) back to categories.
/// Parallel to <see cref="Windows.WindowsCategoryMap"/> on Windows.
/// </summary>
[SupportedOSPlatform("linux")]
internal static class LinuxCategoryMap
{
    /// <summary>
    /// Returns the udev subsystem strings that correspond to the given <paramref name="category"/>,
    /// or an empty array for <see cref="DeviceCategory.All"/> (no filter — enumerate all subsystems).
    /// </summary>
    internal static string[] GetSubsystems(DeviceCategory category) => category switch
    {
        DeviceCategory.All       => [],
        DeviceCategory.Usb       => ["usb"],
        DeviceCategory.Bluetooth => ["bluetooth"],
        DeviceCategory.Network   => ["net"],
        DeviceCategory.Display   => ["drm"],
        DeviceCategory.Monitor   => ["drm"],
        DeviceCategory.Hid       => ["hid", "input"],
        DeviceCategory.Keyboard  => ["input"],
        DeviceCategory.Mouse     => ["input"],
        DeviceCategory.Audio     => ["sound"],
        DeviceCategory.Storage   => ["block"],
        DeviceCategory.Ports     => ["tty"],
        DeviceCategory.Battery   => ["power_supply"],
        DeviceCategory.Camera    => ["video4linux"],
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, "Unknown device category.")
    };

    /// <summary>
    /// ADR-0091 D7: a Bluetooth <c>link</c> node, <c>hciN:&lt;handle&gt;</c>, left out of every result.
    /// It repeats its bond's connection state, which BlueZ reports on the device, with no address.
    /// </summary>
    internal static bool IsBluetoothLink(string? subsystem, string? devtype) =>
        subsystem == "bluetooth" && devtype == "link";

    /// <summary>
    /// The OS port name of a <c>tty</c> device: its device node, which udev reports as
    /// <c>DEVNAME</c> (<c>/dev/ttyUSB0</c>). A kernel uevent carries the name relative to
    /// <c>/dev</c> (<c>ttyUSB0</c>), so a relative name is rooted there. Anything that is
    /// not a <c>tty</c>, or has no node, has no port name.
    /// </summary>
    /// <remarks>
    /// The <c>tty</c> subsystem also holds the kernel's console nodes: <c>tty</c> (the
    /// caller's controlling terminal), <c>console</c>, <c>ptmx</c>, <c>ttyprintk</c>, and the
    /// virtual consoles <c>tty0</c> to <c>tty63</c>. They are not serial ports and get none. The
    /// test is by name, not by a <c>/devices/virtual</c> syspath, because an RFCOMM tty is
    /// a serial port that lives there until it connects.
    /// <para>
    /// Issue #332: <paramref name="uartType"/> is the tty's <c>type</c> attribute, which only a
    /// <c>serial_core</c> port has. The 8250 driver registers
    /// <c>CONFIG_SERIAL_8250_RUNTIME_UARTS</c> ports whether or not a UART sits behind them, and a
    /// placeholder reports <c>0</c>, <c>PORT_UNKNOWN</c>. It gets no port name. A tty outside
    /// <c>serial_core</c>, such as <c>ttyUSB</c>, <c>ttyACM</c> or <c>rfcomm</c>, has no
    /// <c>type</c> and passes <see langword="null"/>.
    /// </para>
    /// </remarks>
    internal static SerialPortName? GetPortName(string? subsystem, string? devname, string? uartType)
    {
        if (subsystem != "tty" || string.IsNullOrWhiteSpace(devname)) return null;
        if (uartType?.Trim() == "0") return null;
        string path = devname.StartsWith('/') ? devname : "/dev/" + devname;
        return IsConsoleNode(path) ? null : new SerialPortName(path);
    }

    private static bool IsConsoleNode(string path)
    {
        string name = path[(path.LastIndexOf('/') + 1)..];
        return name is "tty" or "console" or "ptmx" or "ttyprintk" || IsVirtualConsole(name);
    }

    /// <summary><c>tty0</c> to <c>tty63</c>: the kernel numbers no more (<c>MAX_NR_CONSOLES</c>).</summary>
    private static bool IsVirtualConsole(string name)
    {
        string digits = name.StartsWith("tty", StringComparison.Ordinal) ? name[3..] : "";
        return digits.Length is 1 or 2
            && digits.All(char.IsAsciiDigit)
            && (digits.Length == 1 || digits[0] != '0')
            && int.Parse(digits, System.Globalization.CultureInfo.InvariantCulture) <= 63;
    }

    /// <summary>
    /// Resolves a udev subsystem and device properties to a <see cref="DeviceCategory"/>.
    /// Uses property hints (<c>ID_INPUT_KEYBOARD</c>, <c>ID_INPUT_MOUSE</c>, etc.) to
    /// disambiguate shared subsystems (e.g. <c>input</c> → Keyboard vs. Mouse vs. Hid).
    /// Returns <see cref="DeviceCategory.All"/> if no mapping is found.
    /// </summary>
    internal static DeviceCategory ResolveCategory(string? subsystem, IntPtr device)
    {
        if (subsystem is null) return DeviceCategory.All;

        return subsystem switch
        {
            "usb"          => DeviceCategory.Usb,
            "bluetooth"    => DeviceCategory.Bluetooth,
            "net"          => DeviceCategory.Network,
            "drm"          => DeviceCategory.Display,
            "hid"          => DeviceCategory.Hid,
            "input"        => ResolveInputCategory(device),
            "sound"        => DeviceCategory.Audio,
            "block"        => DeviceCategory.Storage,
            "tty"          => DeviceCategory.Ports,
            "power_supply" => DeviceCategory.Battery,
            "video4linux"  => ResolveVideoCategory(
                PropertyOf(device, "DEVNAME"), PropertyOf(device, "ID_V4L_CAPABILITIES")),
            _              => DeviceCategory.All,
        };
    }

    /// <summary>
    /// Issue #331: a <c>video4linux</c> node is a <see cref="DeviceCategory.Camera"/> only if it
    /// can capture video. Anything else is <see cref="DeviceCategory.All"/>.
    /// </summary>
    /// <remarks>
    /// Only a <c>video</c> node can be a camera. The subsystem also holds <c>v4l-subdev</c>,
    /// <c>v4l-touch</c>, <c>vbi</c>, <c>radio</c> and <c>swradio</c> nodes (ADR-0003 names
    /// <c>video4linux/video*</c>). A <c>video</c> node can still be a memory-to-memory codec, a
    /// video output, or a UVC camera's metadata node. udev's <c>v4l_id</c> builtin runs
    /// <c>VIDIOC_QUERYCAP</c> and records <c>ID_V4L_CAPABILITIES</c>, such as <c>:capture:</c> or
    /// <c>:capture:video_output:</c>; a codec records <c>:</c>. A node without the property is kept,
    /// as before: there is no udev database in a container, and <c>v4l_id</c> can fail.
    /// </remarks>
    internal static DeviceCategory ResolveVideoCategory(string? devname, string? v4lCapabilities)
    {
        string? name = devname is null ? null : devname[(devname.LastIndexOf('/') + 1)..];
        if (name is not null && !name.StartsWith("video", StringComparison.Ordinal))
            return DeviceCategory.All;
        return v4lCapabilities is null || v4lCapabilities.Contains(":capture:", StringComparison.Ordinal)
            ? DeviceCategory.Camera
            : DeviceCategory.All;
    }

    private static string? PropertyOf(IntPtr device, string key) =>
        device == IntPtr.Zero ? null : UdevInterop.GetPropertyValue(device, key);

    /// <summary>
    /// Disambiguates <c>input</c> subsystem devices using udev property hints.
    /// </summary>
    private static DeviceCategory ResolveInputCategory(IntPtr device)
    {
        if (device == IntPtr.Zero) return DeviceCategory.Hid;

        if (UdevInterop.GetPropertyValue(device, "ID_INPUT_KEYBOARD") == "1")
            return DeviceCategory.Keyboard;
        if (UdevInterop.GetPropertyValue(device, "ID_INPUT_MOUSE") == "1")
            return DeviceCategory.Mouse;

        return DeviceCategory.Hid;
    }

    /// <summary>
    /// Infers the bus type from the udev <c>ID_BUS</c> property or subsystem.
    /// </summary>
    internal static BusType InferBusType(string? idBus, string? subsystem)
    {
        // Prefer explicit ID_BUS property when available
        if (idBus is not null)
        {
            return idBus.ToLowerInvariant() switch
            {
                "usb"       => BusType.USB,
                "pci"       => BusType.PCI,
                "bluetooth" => BusType.Bluetooth,
                "hid"       => BusType.HID,
                "scsi"      => BusType.SCSI,
                "ide"       => BusType.IDE,
                "acpi"      => BusType.ACPI,
                _           => BusType.Unknown,
            };
        }

        // Fall back to subsystem inference
        return subsystem switch
        {
            "usb"       => BusType.USB,
            "bluetooth" => BusType.Bluetooth,
            "hid"       => BusType.HID,
            "input"     => BusType.HID,
            "drm"       => BusType.Display,
            "sound"     => BusType.HDAudio,
            _           => BusType.Unknown,
        };
    }
}
