// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Periphery.Cli.Commands;

namespace Periphery.Cli.Serial.Core;

/// <summary>
/// Picks the serial port a terminal opens. An OS port name wins, then
/// <c>--id</c> and <c>--name</c>, then the only connected port.
/// </summary>
internal static class SerialPortSelection
{
    internal const string ListCommand = "periphery devices list --category Ports";

    /// <summary>
    /// <see cref="Device"/> is the enumerated port to supervise. <see cref="Unsupervised"/>
    /// is a port name that matched no enumerated device and is opened directly, without
    /// reconnect. At most one of the three is set.
    /// </summary>
    internal readonly record struct Result(DeviceInfo? Device, SerialPortName? Unsupervised, string? Error);

    internal static Result Select(
        IReadOnlyList<DeviceInfo> candidates, string? port, string? id, string? name)
    {
        if (port is not null)
        {
            var byPort = candidates.FirstOrDefault(d =>
                d.PortName is { } p && string.Equals(p.Value, port, StringComparison.OrdinalIgnoreCase));
            if (byPort is not null)
                return new(byPort, null, null);

            // Linux does not report port names for enumerated devices yet, and a pty
            // or a port behind an unrecognised driver never enumerates at all. The
            // user named the port, so open it; it just cannot be followed across a
            // replug.
            return SerialPortName.TryParse(port, out var unsupervised)
                ? new(null, unsupervised, null)
                : new(null, null, "The port name is empty.");
        }

        var result = DeviceSelection.Select(candidates, "serial port", ListCommand, id, name);
        if (result.Device is { PortName: null } device)
            return new(null, null, $"'{device.Name ?? device.Id}' has no OS port name to open.");
        return new(result.Device, null, result.Error);
    }
}
