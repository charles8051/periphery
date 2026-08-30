// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.ComponentModel;
using System.Globalization;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Periphery.Cli.Commands;

/// <summary>
/// <c>periphery hid report write &lt;device-id&gt; --report N (--bytes HEX | --ascii STRING) [--pad-to N]</c>
/// — send a single output report. Diagnostic for vendor-defined HID
/// devices that take commands via output reports (Megatec-clone UPSs
/// on Cypress 0665 silicon expect <c>Q1\r</c> as an output report,
/// padded to the device's output-report length).
/// </summary>
internal sealed class HidReportWriteCommand : AsyncCommand<HidReportWriteCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [Description("Device ID (from `devices list -v`) or raw \\\\?\\ interface path.")]
        [CommandArgument(0, "<DEVICE_ID>")]
        public string DeviceId { get; init; } = string.Empty;

        [Description("Report ID to send (decimal or 0x-prefixed hex). Default: 0.")]
        [CommandOption("-r|--report <REPORT_ID>")]
        public string ReportId { get; init; } = "0";

        [Description("Payload as hex string (e.g. '5132 0D' or '51320D'). Mutually exclusive with --ascii.")]
        [CommandOption("--bytes <HEX>")]
        public string? Bytes { get; init; }

        [Description("Payload as ASCII string. Supports the escape sequences \\r, \\n, \\t. " +
                     "Mutually exclusive with --bytes.")]
        [CommandOption("--ascii <STRING>")]
        public string? Ascii { get; init; }

        [Description("Pad the payload with zero bytes up to this length. " +
                     "Default: pad to the device's MaxOutputReportLength so the write fills a full report. " +
                     "Use 0 to disable padding (send the payload as-is).")]
        [CommandOption("--pad-to <N>")]
        public int? PadTo { get; init; }
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

        if ((settings.Bytes is null) == (settings.Ascii is null))
        {
            AnsiConsole.MarkupLine("[red]Specify exactly one of --bytes or --ascii.[/]");
            return 1;
        }

        byte[] payload;
        if (settings.Bytes is not null)
        {
            if (!TryParseHexBytes(settings.Bytes, out payload!))
            {
                AnsiConsole.MarkupLine($"[red]Invalid --bytes value. Expected hex digits, "
                    + "optionally separated by spaces or dashes.[/]");
                return 1;
            }
        }
        else
        {
            payload = System.Text.Encoding.ASCII.GetBytes(UnescapeAscii(settings.Ascii!));
        }

        var device = await HidCommandHelpers.ResolveDeviceAsync(settings.DeviceId, cancellationToken);

        await using var hid = await HidDevice.OpenAsync(device, cancellationToken);
        AnsiConsole.MarkupLine(
            $"[grey]MaxInput=[/][white]{hid.MaxInputReportLength}[/] " +
            $"[grey]MaxOutput=[/][white]{hid.MaxOutputReportLength}[/] " +
            $"[grey]MaxFeature=[/][white]{hid.MaxFeatureReportLength}[/]");

        // Pad the payload up to the device's output-report length so the
        // write fills a full report (some firmwares ignore short writes).
        // --pad-to overrides; --pad-to 0 disables padding entirely.
        int padTo = settings.PadTo ?? hid.MaxOutputReportLength;
        if (padTo > 0 && payload.Length < padTo)
        {
            var padded = new byte[padTo];
            Array.Copy(payload, padded, payload.Length);
            payload = padded;
        }

        AnsiConsole.MarkupLine(
            $"[grey]SetOutput[/] [white]report=0x{reportId:X2}[/] " +
            $"[grey]payload=[/][white]{Markup.Escape(BitConverter.ToString(payload))}[/] " +
            $"[grey]({payload.Length} bytes)[/]…");

        try
        {
            await hid.WriteReportAsync(new HidReport(reportId, payload), cancellationToken);
            AnsiConsole.MarkupLine("[green]✓ Sent.[/]");
            return 0;
        }
        catch (HidException ex)
        {
            return HidCommandHelpers.Fail(ex);
        }
    }

    private static bool TryParseByte(string s, out byte value)
    {
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return byte.TryParse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        return byte.TryParse(s, out value);
    }

    private static bool TryParseHexBytes(string s, out byte[]? result)
    {
        result = null;
        var compact = new string(s.Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray());
        if (compact.Length == 0 || compact.Length % 2 != 0)
            return false;
        var bytes = new byte[compact.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            if (!byte.TryParse(compact.AsSpan(i * 2, 2), NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out bytes[i]))
                return false;
        }
        result = bytes;
        return true;
    }

    private static string UnescapeAscii(string input)
    {
        if (!input.Contains('\\')) return input;
        var sb = new System.Text.StringBuilder(input.Length);
        for (int i = 0; i < input.Length; i++)
        {
            if (input[i] == '\\' && i + 1 < input.Length)
            {
                switch (input[i + 1])
                {
                    case 'r': sb.Append('\r'); i++; continue;
                    case 'n': sb.Append('\n'); i++; continue;
                    case 't': sb.Append('\t'); i++; continue;
                    case '\\': sb.Append('\\'); i++; continue;
                }
            }
            sb.Append(input[i]);
        }
        return sb.ToString();
    }
}
