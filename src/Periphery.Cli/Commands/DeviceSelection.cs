// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Spectre.Console;

namespace Periphery.Cli.Commands;

/// <summary>
/// Target selection for commands that act on one device of a category: pick
/// by exact Id, by name substring, or implicitly when exactly one is
/// connected. <see cref="Select"/> is the pure rule; <see cref="ResolveAsync"/>
/// enumerates and reports.
/// </summary>
internal static class DeviceSelection
{
    /// <summary>The chosen device, or the reason none was chosen.</summary>
    internal readonly record struct Result(DeviceInfo? Device, string? Error);

    internal static async Task<DeviceInfo?> ResolveAsync(
        IAnsiConsole console, DeviceCategory category, string noun, string listCommand,
        string? id, string? name, CancellationToken ct)
    {
        var candidates = await Devices.Enumerate()
            .OfCategory(category)
            .ToListAsync(ct);

        var result = Select(candidates, noun, listCommand, id, name);
        if (result.Error is not null)
            console.MarkupLine($"[red]{Markup.Escape(result.Error)}[/]");
        return result.Device;
    }

    internal static Result Select(
        IReadOnlyList<DeviceInfo> candidates, string noun, string listCommand,
        string? id, string? name)
    {
        if (id is not null)
        {
            var byId = candidates.FirstOrDefault(d =>
                string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));
            return byId is not null
                ? new(byId, null)
                : new(null, $"No {noun} with Id '{id}'. Run `{listCommand}` for the connected set.");
        }

        if (name is not null)
        {
            var byName = candidates
                .Where(d => d.Name?.Contains(name, StringComparison.OrdinalIgnoreCase) == true)
                .ToList();
            return byName.Count switch
            {
                1 => new(byName[0], null),
                0 => new(null, $"No {noun} name contains '{name}'."),
                _ => new(null, $"'{name}' matches {byName.Count} {noun}s — narrow it or use --id."),
            };
        }

        return candidates.Count switch
        {
            1 => new(candidates[0], null),
            0 => new(null, $"No {noun}s enumerated."),
            _ => new(null, $"{candidates.Count} {noun}s connected — pick one with --name or --id "
                           + $"(see `{listCommand}`)."),
        };
    }
}
