// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Periphery.Cli.Serial.Core;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Periphery.Cli.Commands;

/// <summary>
/// <c>periphery serial select</c>: list the serial ports, pick one with the arrow keys and
/// Enter, then run the same terminal as <c>periphery serial</c>.
/// </summary>
internal sealed class SerialSelectCommand : AsyncCommand<SerialCommand.Settings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context, SerialCommand.Settings settings, CancellationToken cancellationToken)
    {
        var err = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(Console.Error) });

        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            err.MarkupLine("[red]serial select needs an interactive terminal. Name the port instead: "
                           + "periphery serial <port>.[/]");
            return 1;
        }

        if (settings.Port is not null || settings.Id is not null)
        {
            err.MarkupLine("[red]serial select picks the port itself. Drop the port name and --id, "
                           + "or use periphery serial <port>.[/]");
            return 1;
        }

        var candidates = await Devices.Enumerate()
            .OfCategory(DeviceCategory.Ports)
            .ToListAsync(cancellationToken);
        var choices = SerialPortChoices.From(candidates, settings.Name);
        if (choices.Count == 0)
        {
            err.MarkupLine(settings.Name is null
                ? "[red]No serial ports are connected.[/]"
                : $"[red]No serial port name contains '{Markup.Escape(settings.Name)}'.[/]");
            return 1;
        }

        var choice = await PromptAsync(AnsiConsole.Console, choices, cancellationToken);
        return await SerialCommand.RunSessionAsync(
            new SerialPortSelection.Result(choice.Device, null, null), settings, err, cancellationToken);
    }

    internal static Task<SerialPortChoice> PromptAsync(
        IAnsiConsole console, IReadOnlyList<SerialPortChoice> choices, CancellationToken ct) =>
        new SelectionPrompt<SerialPortChoice>()
            .Title("Serial port")
            .PageSize(15)
            .MoreChoicesText("[grey](more below)[/]")
            // Labels are device-reported text; a bracket in one would be read as markup.
            .UseConverter(c => Markup.Escape(c.Label))
            .AddChoices(choices)
            .ShowAsync(console, ct);
}
