using Periphery.Examples.Camera.Common;

namespace Periphery.Examples.Camera.Commands;

/// <summary>
/// Demonstrates the streaming capture path: <c>CameraSession.OpenAsync</c>
/// followed by <c>session.CaptureAsync()</c> piped into a frame sink.
///
/// Format selection uses the <c>CameraFormatSelectors</c> LINQ extensions
/// (ADR-0040). Frame-to-disk uses <c>SaveToDirectoryAsync</c> — no
/// hand-rolled file naming or extension logic in user code.
/// </summary>
internal static class CaptureCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var device = await Args.ResolveCameraAsync(args).ConfigureAwait(false);
        if (device is null) return 1;

        int maxFrames = Args.GetIntOption(args, defaultValue: 30, "--frames", "-n");
        string? saveDir = Args.GetOption(args, "--save");
        string preferredFormat =
            Args.GetOption(args, "--format") is { } f ? f.ToLowerInvariant() : "mjpeg";

        // Cap default resolution so the example streams reliably on every camera.
        // High-resolution USB modes (4K MJPEG) can take many seconds to start
        // producing frames on commodity webcams. Override with --max-width / --max-height.
        int maxWidth = Args.GetIntOption(args, defaultValue: 1280, "--max-width");
        int maxHeight = Args.GetIntOption(args, defaultValue: 720, "--max-height");

        var snap = await CameraDevice.ReadSnapshotAsync(device).ConfigureAwait(false);

        var preferredEnum = preferredFormat switch
        {
            "mjpeg" or "jpg" => CameraPixelFormat.Mjpeg,
            "nv12"           => CameraPixelFormat.Nv12,
            "yuy2"           => CameraPixelFormat.Yuy2,
            _                => CameraPixelFormat.Unknown,
        };

        var chosen = snap.Formats
            .WithinBox(maxWidth, maxHeight)
            .PreferPixelFormat(preferredEnum)
            .ThenByHighestArea()
            .ThenByHighestFrameRate()
            .FirstOrDefault();

        if (chosen is null)
        {
            Console.Error.WriteLine(
                $"Camera does not advertise any format within {maxWidth}x{maxHeight}. Available:");
            foreach (var fmt in snap.Formats)
                Console.Error.WriteLine($"  {fmt.Width}x{fmt.Height}  {fmt.PixelFormat}");
            return 1;
        }

        Console.WriteLine($"Using {device.Name ?? "(unnamed)"}");
        Console.WriteLine(
            $"Capturing {maxFrames} frame(s) at {chosen.Width}x{chosen.Height} " +
            $"{chosen.PixelFormat} ({chosen.MaxFrameRate} fps)");
        if (saveDir is not null)
            Console.WriteLine($"Saving frames to: {Path.GetFullPath(saveDir)}");
        Console.WriteLine();

        var config = new CameraConfiguration(chosen);
        await using var session = await CameraSession.OpenAsync(device, config).ConfigureAwait(false);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        Console.WriteLine("  waiting for first frame… (camera may take a few seconds to spin up)");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool stalled = false;
        int saved = 0;

        try
        {
            // Stream frames through (a) progress reporting and a max-frames cap,
            // then optionally (b) the disk sink. Reporting and the cap are example
            // concerns; the sink is a one-liner thanks to ADR-0040.
            var stream = session
                .CaptureAsync(ct: cts.Token)
                .WithProgress(session, maxFrames, cts);

            if (saveDir is not null)
                saved = await stream.SaveToDirectoryAsync(saveDir, ct: cts.Token).ConfigureAwait(false);
            else
                await foreach (var frame in stream.ConfigureAwait(false)) frame.Dispose();
        }
        catch (CameraTimeoutException ex)
        {
            // The camera stopped delivering frames mid-stream. Some USB cameras
            // (notably MJPEG modes on certain chipsets) wedge after a few hundred
            // ms of streaming and don't recover. Surface what happened, keep what
            // we captured. Try --format yuy2 if MJPEG is unreliable on a camera.
            stalled = true;
            Console.WriteLine();
            Console.WriteLine($"  ⚠  {ex.Message}");
        }

        sw.Stop();

        var final = session.Metrics;
        Console.WriteLine();
        Console.WriteLine($"{(stalled ? "Stopped early" : "Done")}. " +
            $"{final.FramesProduced} frame(s) produced, {sw.Elapsed.TotalSeconds:F2}s elapsed.");
        if (saveDir is not null)
            Console.WriteLine($"Saved {saved} file(s) to {Path.GetFullPath(saveDir)}.");
        Console.WriteLine(
            $"Final metrics: produced={final.FramesProduced}  dropped={final.FramesDropped}  " +
            $"last_ts={final.LastFrameTimestamp?.TotalMilliseconds:F1}ms");

        return 0;
    }

    /// <summary>
    /// Wraps the capture stream with progress logging and a maxFrames-based cancel.
    /// Pure example concern — production callers wouldn't write this.
    /// </summary>
    private static async IAsyncEnumerable<LeasedCameraFrame> WithProgress(
        this IAsyncEnumerable<LeasedCameraFrame> source,
        CameraSession session,
        int maxFrames,
        CancellationTokenSource cts)
    {
        int count = 0;
        await foreach (var frame in source.ConfigureAwait(false))
        {
            count++;
            if (count == 1 || count % 10 == 0 || count == maxFrames)
            {
                var m = session.Metrics;
                Console.WriteLine(
                    $"  frame {count,4}  {frame.ContiguousBuffer.Length,8} bytes  " +
                    $"ts={frame.Timestamp.TotalMilliseconds,8:F1}ms  " +
                    $"produced={m.FramesProduced}  dropped={m.FramesDropped}  " +
                    $"leases={m.OutstandingLeases}");
            }

            yield return frame;

            if (count >= maxFrames)
            {
                cts.Cancel();
                yield break;
            }
        }
    }
}
