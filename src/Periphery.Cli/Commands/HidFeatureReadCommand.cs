// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Periphery.Cli.Commands;

/// <summary>
/// <c>periphery hid feature read &lt;device-id&gt; --report N</c> — read a
/// HID feature report from a device. Diagnostic for vendor-defined HID
/// devices where the interesting state lives in feature reports rather
/// than input reports (battery snapshots, configuration queries,
/// vendor-protocol responses like Megatec Q1).
/// </summary>
internal sealed class HidFeatureReadCommand : AsyncCommand<HidFeatureReadCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [Description("Device ID (from `devices list -v`). Quote if it contains backslashes.")]
        [CommandArgument(0, "<DEVICE_ID>")]
        public string DeviceId { get; init; } = string.Empty;

        [Description("Report ID to request (decimal or 0x-prefixed hex). Default: 0.")]
        [CommandOption("-r|--report <REPORT_ID>")]
        public string ReportId { get; init; } = "0";

        [Description("Print the payload as an ASCII string instead of a hex dump.")]
        [CommandOption("--ascii")]
        public bool Ascii { get; init; }
    }

    protected override async Task<int> ExecuteAsync(
        CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        if (!TryParseByte(settings.ReportId, out byte reportId))
        {
            AnsiConsole.MarkupLine($"[red]Invalid --report value '{Markup.Escape(settings.ReportId)}'. "
                + "Use decimal (e.g. 0, 12) or hex (e.g. 0x0A).[/]");
            return 1;
        }

        var device = await HidCommandHelpers.ResolveDeviceAsync(settings.DeviceId, cancellationToken);

        await using var hid = await HidDevice.OpenAsync(device, cancellationToken);

        AnsiConsole.MarkupLine(
            $"[grey]UsagePage=[/][white]0x{hid.UsagePage:X4}[/] " +
            $"[grey]Usage=[/][white]0x{hid.Usage:X4}[/]");
        AnsiConsole.MarkupLine(
            $"[grey]MaxInput=[/][white]{hid.MaxInputReportLength}[/] " +
            $"[grey]MaxOutput=[/][white]{hid.MaxOutputReportLength}[/] " +
            $"[grey]MaxFeature=[/][white]{hid.MaxFeatureReportLength}[/]");

        AnsiConsole.MarkupLine($"[grey]GetFeature[/] [white]report=0x{reportId:X2}[/]…");

        try
        {
            var report = await hid.ReadFeatureReportAsync(reportId, cancellationToken);

            AnsiConsole.MarkupLine($"[green]✓[/] [grey]Got report[/] " +
                $"[white]id=0x{report.ReportId:X2}[/] [grey]bytes=[/][white]{report.Data.Length}[/]");

            var payload = report.Data.ToArray();
            if (settings.Ascii)
            {
                // ASCII view — useful for Megatec Q1 dialect responses
                // (e.g. "(220.0 220.0 220.0 000 50.0 27.0 02.0 00001001"
                // followed by 0x0D). Non-printable bytes shown as '.'.
                var ascii = new string(payload.Select(b => b is >= 0x20 and < 0x7F ? (char)b : '.').ToArray());
                AnsiConsole.MarkupLine($"[white]\"{Markup.Escape(ascii)}\"[/]");
            }
            else
            {
                AnsiConsole.MarkupLine($"[white]{Markup.Escape(BitConverter.ToString(payload))}[/]");
            }

            return 0;
        }
        catch (HidException ex)
        {
            return HidCommandHelpers.Fail(ex);
        }
    }

    /// <summary>Parses a byte from decimal or 0x-prefixed hex.</summary>
    private static bool TryParseByte(string s, out byte value)
    {
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return byte.TryParse(s.AsSpan(2), System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out value);
        return byte.TryParse(s, out value);
    }
}
