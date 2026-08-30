// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Periphery.Cli.Commands;

/// <summary>
/// <c>periphery hid report read &lt;device-id&gt; [--count N] [--timeout MS] [--ascii]</c>
/// — read input reports from a HID device in a loop. Diagnostic for
/// vendor-defined HID devices that ship their request/response over
/// input/output reports rather than feature reports (e.g. Megatec-clone
/// UPSs on Cypress silicon, where the device has no feature reports at
/// all and Q1 status responses arrive fragmented across multiple
/// 8-byte input reports).
/// </summary>
internal sealed class HidReportReadCommand : AsyncCommand<HidReportReadCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [Description("Device ID (from `devices list -v`) or raw \\\\?\\ interface path.")]
        [CommandArgument(0, "<DEVICE_ID>")]
        public string DeviceId { get; init; } = string.Empty;

        [Description("Number of input reports to read before exiting. Default: 1.")]
        [CommandOption("-n|--count <N>")]
        public int Count { get; init; } = 1;

        [Description("Per-read timeout in milliseconds. Reads that don't return within this window cancel cleanly. Default: 2000.")]
        [CommandOption("-t|--timeout <MS>")]
        public int TimeoutMs { get; init; } = 2000;

        [Description("Print each report's payload as ASCII (with non-printables as '.') in addition to hex.")]
        [CommandOption("--ascii")]
        public bool Ascii { get; init; }
    }

    protected override async Task<int> ExecuteAsync(
        CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var device = await HidCommandHelpers.ResolveDeviceAsync(settings.DeviceId, cancellationToken);

        await using var hid = await HidDevice.OpenAsync(device, cancellationToken);
        AnsiConsole.MarkupLine(
            $"[grey]UsagePage=[/][white]0x{hid.UsagePage:X4}[/] " +
            $"[grey]Usage=[/][white]0x{hid.Usage:X4}[/]");
        AnsiConsole.MarkupLine(
            $"[grey]MaxInput=[/][white]{hid.MaxInputReportLength}[/] " +
            $"[grey]MaxOutput=[/][white]{hid.MaxOutputReportLength}[/] " +
            $"[grey]MaxFeature=[/][white]{hid.MaxFeatureReportLength}[/]");

        AnsiConsole.MarkupLine(
            $"[grey]Reading[/] [white]{settings.Count}[/] [grey]input report(s), timeout=[/][white]{settings.TimeoutMs}ms[/]…");

        int succeeded = 0;
        for (int i = 0; i < settings.Count; i++)
        {
            using var perRead = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            perRead.CancelAfter(settings.TimeoutMs);

            try
            {
                var report = await hid.ReadReportAsync(perRead.Token);
                var bytes = report.Data.ToArray();
                var hex = bytes.Length == 0 ? "(empty)" : BitConverter.ToString(bytes);
                var line = $"[grey]#{i,3}[/] [grey]id=[/][white]0x{report.ReportId:X2}[/] [grey]len=[/][white]{bytes.Length}[/] [white]{Markup.Escape(hex)}[/]";
                if (settings.Ascii)
                {
                    var ascii = new string(bytes.Select(b => b is >= 0x20 and < 0x7F ? (char)b : '.').ToArray());
                    line += $"  [dim]\"{Markup.Escape(ascii)}\"[/]";
                }
                AnsiConsole.MarkupLine(line);
                succeeded++;
            }
            catch (OperationCanceledException) when (perRead.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                AnsiConsole.MarkupLine($"[yellow]#{i,3} timed out after {settings.TimeoutMs}ms[/]");
            }
            catch (HidException ex)
            {
                AnsiConsole.MarkupLine($"[red]#{i,3} ✗ {Markup.Escape(ex.Message)}[/]");
                if (ex.InnerException is not null)
                    AnsiConsole.MarkupLine($"      [grey]{Markup.Escape(ex.InnerException.Message)}[/]");
                return 1;
            }
        }

        AnsiConsole.MarkupLine($"[green]Got {succeeded}/{settings.Count} reports.[/]");
        return 0;
    }
}
