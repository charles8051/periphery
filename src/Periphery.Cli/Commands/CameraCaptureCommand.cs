// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Periphery.Camera;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Periphery.Cli.Commands;

/// <summary>
/// <c>periphery camera capture</c> — open one camera, save N frames through
/// <see cref="CameraFrameSinks.SaveToDirectoryAsync"/>, close it. The sink does
/// not re-encode: MJPEG frames are written as <c>.jpg</c>, every other format
/// as <c>.raw</c> with its dimensions and pixel format in the filename.
/// </summary>
/// <remarks>
/// Stdout carries only the saved file paths, one per line, so
/// <c>$(periphery camera capture)</c> is the path of the still. Progress and
/// errors go to stderr.
/// </remarks>
internal sealed class CameraCaptureCommand : AsyncCommand<CameraCaptureCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [Description("Select by name substring (when several cameras are connected).")]
        [CommandOption("--name")]
        public string? Name { get; init; }

        [Description("Select by exact device Id.")]
        [CommandOption("--id")]
        public string? Id { get; init; }

        [Description("Directory to write frames into, created if missing. Default: the current directory.")]
        [CommandOption("-o|--output <DIR>")]
        public string Output { get; init; } = ".";

        [Description("Number of frames to save. Default: 1.")]
        [CommandOption("-n|--frames <N>")]
        public int Frames { get; init; } = 1;

        [Description("Discard this many frames before saving, to let auto-exposure settle. Default: 0.")]
        [CommandOption("--skip <N>")]
        public int Skip { get; init; }

        [Description("Preferred pixel format: mjpeg (saved as .jpg), nv12, yuy2, uyvy, ..., or any. "
                     + "Another format is used when the camera lacks it. Default: mjpeg.")]
        [CommandOption("--format <FORMAT>")]
        public string Format { get; init; } = "mjpeg";

        [Description("Largest resolution to accept, as WIDTHxHEIGHT. Default: the camera's largest.")]
        [CommandOption("--max-resolution <WxH>")]
        public string? MaxResolution { get; init; }

        public override ValidationResult Validate()
        {
            if (Frames < 1)
                return ValidationResult.Error("--frames must be at least 1.");
            if (Skip < 0)
                return ValidationResult.Error("--skip cannot be negative.");
            if (!TryParsePixelFormat(Format, out _))
                return ValidationResult.Error(
                    $"Unknown --format '{Format}'. Use any, or one of: "
                    + string.Join(", ", PixelFormatNames()) + ".");
            if (MaxResolution is not null && !TryParseResolution(MaxResolution, out _, out _))
                return ValidationResult.Error("--max-resolution must be WIDTHxHEIGHT, e.g. 1920x1080.");
            return ValidationResult.Success();
        }
    }

    protected override async Task<int> ExecuteAsync(
        CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var err = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(Console.Error) });

        var target = await DeviceSelection.ResolveAsync(
            err, DeviceCategory.Camera, "camera", "periphery devices list --category Camera",
            settings.Id, settings.Name, cancellationToken);
        if (target is null) return 1;

        var builder = CameraSession.For(target);
        if (TryParsePixelFormat(settings.Format, out var preferred) && preferred is { } pixelFormat)
            builder.PreferPixelFormat(pixelFormat);
        if (settings.MaxResolution is not null
            && TryParseResolution(settings.MaxResolution, out int maxWidth, out int maxHeight))
            builder.MaxResolution(maxWidth, maxHeight);

        string directory = Path.GetFullPath(settings.Output);
        // One prefix per run, so the listing below names this run's files and a
        // second run into the same directory does not overwrite the first.
        string prefix = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

        int exitCode = 0;
        try
        {
            await using var session = await builder.OpenAsync(cancellationToken);
            var format = session.Configuration.Format;
            err.MarkupLine(
                $"[grey]Capturing {settings.Frames} frame(s) from[/] {Markup.Escape(target.Name ?? target.Id)} "
                + $"[grey]at[/] {format.Width}x{format.Height} {format.PixelFormat}");

            await SkipDisposing(session.CaptureAsync(ct: cancellationToken), settings.Skip)
                .Take(settings.Frames)
                .SaveToDirectoryAsync(
                    directory, new CameraFrameWriteOptions(FilenamePrefix: prefix), cancellationToken);
        }
        catch (CameraException ex)
        {
            err.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
            exitCode = 1;
        }

        // Listed on failure too: a stream that stalls after some frames keeps them.
        if (Directory.Exists(directory))
        {
            foreach (var path in Directory.EnumerateFiles(directory, prefix + "-*").Order(StringComparer.Ordinal))
                Console.Out.WriteLine(path);
        }
        return exitCode;
    }

    /// <summary>
    /// Drops the first <paramref name="count"/> items, disposing each. A camera
    /// frame is a pool lease, and <c>Skip</c> would drop it undisposed.
    /// </summary>
    internal static async IAsyncEnumerable<T> SkipDisposing<T>(
        IAsyncEnumerable<T> source, int count,
        [EnumeratorCancellation] CancellationToken ct = default)
        where T : IDisposable
    {
        await foreach (var item in source.WithCancellation(ct).ConfigureAwait(false))
        {
            if (count > 0)
            {
                count--;
                item.Dispose();
                continue;
            }
            yield return item;
        }
    }

    /// <summary>
    /// Parses a <c>--format</c> value. <c>any</c> yields <see langword="null"/>
    /// (no preference); otherwise a <see cref="CameraPixelFormat"/> name, case-insensitive.
    /// </summary>
    internal static bool TryParsePixelFormat(string text, out CameraPixelFormat? format)
    {
        format = null;
        if (string.Equals(text, "any", StringComparison.OrdinalIgnoreCase))
            return true;
        foreach (var candidate in Enum.GetValues<CameraPixelFormat>())
        {
            if (candidate != CameraPixelFormat.Unknown
                && string.Equals(candidate.ToString(), text, StringComparison.OrdinalIgnoreCase))
            {
                format = candidate;
                return true;
            }
        }
        return false;
    }

    /// <summary>Parses <c>WIDTHxHEIGHT</c>, both positive integers.</summary>
    internal static bool TryParseResolution(string text, out int width, out int height)
    {
        width = height = 0;
        var parts = text.Split('x', 'X');
        return parts.Length == 2
            && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out width)
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out height)
            && width > 0 && height > 0;
    }

    private static IEnumerable<string> PixelFormatNames() =>
        Enum.GetValues<CameraPixelFormat>()
            .Where(f => f != CameraPixelFormat.Unknown)
            .Select(f => f.ToString().ToLowerInvariant());
}
