// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.ComponentModel;
using System.Globalization;
using Periphery.Monitor;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Periphery.Cli.Commands;

/// <summary>
/// <c>periphery monitor vcp &lt;get|set&gt; &lt;code&gt; [value]</c> — the raw
/// MCCS escape hatch for any VCP code, including vendor-specific ones.
/// Code and value accept decimal or 0x-prefixed hex.
/// </summary>
internal sealed class MonitorVcpCommand : AsyncCommand<MonitorVcpCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [Description("'get' or 'set'.")]
        [CommandArgument(0, "<OPERATION>")]
        public string Operation { get; init; } = string.Empty;

        [Description("VCP code (e.g. 0x10 for luminance, 0xD6 for power).")]
        [CommandArgument(1, "<CODE>")]
        public string Code { get; init; } = string.Empty;

        [Description("Value for 'set' (decimal or 0x hex).")]
        [CommandArgument(2, "[VALUE]")]
        public string? Value { get; init; }

        [CommandOption("--name")]
        public string? Name { get; init; }

        [CommandOption("--id")]
        public string? Id { get; init; }
    }

    protected override async Task<int> ExecuteAsync(
        CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        if (!TryParseNumber(settings.Code, out ushort codeWide) || codeWide > 0xFF)
        {
            AnsiConsole.MarkupLine("[red]CODE must be a byte (0-255 / 0x00-0xFF).[/]");
            return 1;
        }
        byte code = (byte)codeWide;

        bool isSet = settings.Operation.Equals("set", StringComparison.OrdinalIgnoreCase);
        if (!isSet && !settings.Operation.Equals("get", StringComparison.OrdinalIgnoreCase))
        {
            AnsiConsole.MarkupLine("[red]OPERATION must be 'get' or 'set'.[/]");
            return 1;
        }

        ushort value = 0;
        if (isSet && (settings.Value is null || !TryParseNumber(settings.Value, out value)))
        {
            AnsiConsole.MarkupLine("[red]'set' requires a VALUE (decimal or 0x hex, 0-65535).[/]");
            return 1;
        }

        var target = await MonitorCommandHelpers.ResolveMonitorAsync(
            settings.Id, settings.Name, cancellationToken);
        if (target is null) return 1;

        try
        {
            await using var monitor = await MonitorDevice.OpenAsync(target, cancellationToken);
            if (isSet)
            {
                await monitor.SetVcpFeatureAsync(code, value, cancellationToken);
                AnsiConsole.MarkupLine($"[green]VCP 0x{code:X2} = {value} written.[/]");
            }
            else
            {
                var read = await monitor.GetVcpFeatureAsync(code, cancellationToken);
                AnsiConsole.MarkupLine(
                    $"[white]VCP 0x{code:X2}[/]: current=[white]{read.Current}[/] "
                    + $"(0x{read.Current:X4}) max=[white]{read.Maximum}[/]");
            }
            return 0;
        }
        catch (MonitorException ex)
        {
            return MonitorCommandHelpers.Fail(ex);
        }
    }

    private static bool TryParseNumber(string text, out ushort value)
    {
        return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? ushort.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value)
            : ushort.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }
}
