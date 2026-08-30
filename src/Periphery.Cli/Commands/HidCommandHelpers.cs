// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Periphery.Hid;
using Spectre.Console;

namespace Periphery.Cli.Commands;

/// <summary>
/// Shared device-resolve + error-epilogue for the raw-HID command group
/// (<c>hid feature read|write</c>, <c>hid report read|write</c>) and the
/// <c>devices reset</c> diagnostic. Mirrors the shape
/// <see cref="MonitorCommandHelpers"/> proves for the monitor group
/// (<c>ResolveMonitorAsync</c> + <c>Fail</c>): the helper <em>is</em> the
/// imperative shell, so the console writes live here (ADR-0043's
/// "extract once, reuse" — five commands hand-rolled the identical
/// enumerate / match-or-raw-fallback block, six the identical
/// <see cref="HidException"/> catch).
/// </summary>
internal static class HidCommandHelpers
{
    /// <summary>
    /// Resolve <paramref name="deviceId"/> to an enriched <see cref="DeviceInfo"/>
    /// snapshot when <c>Devices.Enumerate</c> surfaces a matching entity, else
    /// fall back to a raw <see cref="DeviceInfo"/> carrying only the id so an
    /// unenumerable / GaveUp device can still be targeted directly (per the
    /// spike findings: <c>Periphery.Hid</c> can't yet resolve every SetupAPI
    /// instance id into a <c>CreateFile</c>-able interface path).
    /// </summary>
    /// <param name="deviceId">The id string from the command argument.</param>
    /// <param name="ct">Enumeration cancellation token.</param>
    /// <param name="announceOpen">
    /// When <c>true</c> (the HID commands), print the grey
    /// <c>Opening &lt;name&gt;…</c> line on a match. The reset command passes
    /// <c>false</c> because it prints its own <c>Device:</c> line afterwards.
    /// </param>
    /// <param name="rawVerb">
    /// Verb in the no-match note. HID commands open a handle (<c>"opening"</c>);
    /// reset uses the id as a raw instance id (<c>"using"</c>).
    /// </param>
    /// <param name="rawNoun">
    /// Noun in the no-match note: a HID command treats the string as a raw
    /// device path (<c>"device path"</c>), reset as a raw instance id
    /// (<c>"instance id"</c>).
    /// </param>
    internal static async Task<DeviceInfo> ResolveDeviceAsync(
        string deviceId,
        CancellationToken ct,
        bool announceOpen = true,
        string rawVerb = "opening",
        string rawNoun = "device path")
    {
        // Resolve via Devices.Enumerate when possible — gives us the enriched
        // DeviceInfo (name / class GUID) for nicer output. Falls back to the
        // raw id so the spike can validate hardware behaviour even when the id
        // can't be resolved to an enriched snapshot.
        // WithId, not a hand-rolled Ordinal compare: device instance ids are
        // case-insensitive by contract and the same device re-enumerates in
        // different casing (issue #231), so an ordinal filter drops a device the
        // operator just copied out of `periphery devices list`.
        var matches = await Devices.Enumerate()
            .WithId(deviceId)
            .ToListAsync(ct);

        if (matches.Count > 0)
        {
            var device = matches[0];
            if (announceOpen)
                AnsiConsole.MarkupLine(
                    $"[grey]Opening[/] [white]{Markup.Escape(device.Name ?? device.Id)}[/]…");
            return device;
        }

        AnsiConsole.MarkupLine(
            $"[yellow](no enumeration match — {rawVerb} '{Markup.Escape(deviceId)}' as a raw {rawNoun})[/]");
        return new DeviceInfo { Id = deviceId };
    }

    /// <summary>
    /// Render a failed HID operation — the red message plus the grey inner
    /// (the original OS-level exception, always present per
    /// <see cref="HidException"/>) — and return the process exit code
    /// <c>1</c> so a command can <c>return HidCommandHelpers.Fail(ex);</c>.
    /// </summary>
    internal static int Fail(HidException ex)
    {
        AnsiConsole.MarkupLine($"[red]✗ {Markup.Escape(ex.Message)}[/]");
        if (ex.InnerException is not null)
            AnsiConsole.MarkupLine($"  [grey]{Markup.Escape(ex.InnerException.Message)}[/]");
        return 1;
    }
}
