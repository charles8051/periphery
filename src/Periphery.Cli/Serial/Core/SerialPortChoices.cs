// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Globalization;

namespace Periphery.Cli.Serial.Core;

/// <summary>One row of the <c>serial select</c> picker.</summary>
internal sealed record SerialPortChoice(DeviceInfo Device, string Label);

/// <summary>
/// The rows <c>serial select</c> offers: every enumerated port with an OS port name,
/// in port order, each labelled with its port name and what the device calls itself.
/// </summary>
internal static class SerialPortChoices
{
    /// <param name="candidates">The enumerated <c>Ports</c> devices.</param>
    /// <param name="nameFilter">Keep only devices whose name contains this, ignoring case. Null keeps all.</param>
    internal static IReadOnlyList<SerialPortChoice> From(IEnumerable<DeviceInfo> candidates, string? nameFilter = null)
    {
        var ports = candidates
            .Where(d => d.PortName is not null)
            .Where(d => nameFilter is null || d.Name?.Contains(nameFilter, StringComparison.OrdinalIgnoreCase) == true)
            .OrderBy(d => d.PortName!.Value.Value, PortNameComparer.Instance)
            .ThenBy(d => d.Id.ToString(), StringComparer.Ordinal)
            .ToList();

        int width = ports.Count == 0 ? 0 : ports.Max(d => d.PortName!.Value.Value.Length);
        return ports
            .Select(d => new SerialPortChoice(d, TerminalText.Printable(Label(d, width))))
            .ToList();
    }

    private static string Label(DeviceInfo device, int width)
    {
        string port = device.PortName!.Value.Value;
        string description = Describe(device, port);
        return description.Length == 0 ? port : $"{port.PadRight(width)}  {description}";
    }

    /// <summary>
    /// The device's name without the <c>(COM4)</c> Windows appends, plus its manufacturer
    /// when the name does not already say it.
    /// </summary>
    private static string Describe(DeviceInfo device, string port)
    {
        string name = device.Name ?? "";
        string suffix = $" ({port})";
        if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            name = name[..^suffix.Length];

        string? maker = device.Manufacturer;
        if (string.IsNullOrWhiteSpace(maker) || name.Contains(maker, StringComparison.OrdinalIgnoreCase))
            return name.Trim();
        return name.Length == 0 ? maker : $"{name.Trim()} ({maker})";
    }

    /// <summary>
    /// Orders port names by their text, then by a trailing number as a number, so
    /// <c>COM4</c> comes before <c>COM29</c> and <c>/dev/ttyS2</c> before <c>/dev/ttyS10</c>.
    /// </summary>
    private sealed class PortNameComparer : IComparer<string>
    {
        internal static readonly PortNameComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            var (xStem, xNumber) = Split(x ?? "");
            var (yStem, yNumber) = Split(y ?? "");
            int byStem = StringComparer.OrdinalIgnoreCase.Compare(xStem, yStem);
            if (byStem != 0) return byStem;
            int byNumber = xNumber.CompareTo(yNumber);
            return byNumber != 0 ? byNumber : StringComparer.Ordinal.Compare(x, y);
        }

        private static (string Stem, long Number) Split(string name)
        {
            int start = name.Length;
            while (start > 0 && char.IsAsciiDigit(name[start - 1])) start--;
            string digits = name[start..];
            return digits.Length is > 0 and <= 18
                ? (name[..start], long.Parse(digits, CultureInfo.InvariantCulture))
                : (name, -1);
        }
    }
}
