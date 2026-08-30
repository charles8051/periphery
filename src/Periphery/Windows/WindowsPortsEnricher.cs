// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Periphery.Windows;

/// <summary>
/// Reads the OS COM port name for serial/ports-category device nodes from the
/// registry key <c>HKLM\SYSTEM\CurrentControlSet\Enum\{instanceId}\Device Parameters\PortName</c>.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsPortsEnricher
{
    /// <summary>
    /// Returns the <see cref="SerialPortName"/> for <paramref name="instanceId"/>,
    /// or <see langword="null"/> if the registry key is absent or the value is empty.
    /// </summary>
    internal static SerialPortName? GetPortName(string instanceId)
    {
        // Instance IDs use backslashes: USB\VID_0403&PID_6001\SNxxxxxx
        string keyPath = @"SYSTEM\CurrentControlSet\Enum\" + instanceId + @"\Device Parameters";
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(keyPath);

        if (key?.GetValue("PortName") is not string portName)
            return null;

        return SerialPortName.TryParse(portName, out var name) ? name : null;
    }
}
