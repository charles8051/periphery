#:property TargetFramework=net10.0
#:package Periphery.Camera@1.0.0-alpha.*

// Demonstrates the Periphery.Camera API: device discovery, snapshot
// inspection, and live frame capture with metrics.
//
// Run: dotnet run camera.cs [--snapshot-only] [--frames N]

using Periphery;
using Periphery.Camera;

bool snapshotOnly = false;
int maxFrames = 30;

foreach (var arg in args)
{
    if (arg is "--snapshot-only" or "-s") snapshotOnly = true;
    else if (int.TryParse(arg, out var n)) maxFrames = n;
    else if (arg is "--frames" or "-n") { /* next arg is the count */ }
}
for (int i = 0; i < args.Length - 1; i++)
{
    if (args[i] is "--frames" or "-n" && int.TryParse(args[i + 1], out var n))
        maxFrames = n;
}

Console.WriteLine("Periphery.Camera — dotnet script example");
Console.WriteLine(new string('─', 45));
Console.WriteLine();

// ── 1. Discover cameras ───────────────────────────────────────────────
Console.Write("Discovering cameras...");

var cameras = await Devices.Enumerate()
    .OfCategory(DeviceCategory.Camera)
    .ToListAsync();

Console.WriteLine($" {cameras.Count} found.");

if (cameras.Count == 0)
{
    Console.Error.WriteLine("No cameras detected. Plug one in and try again.");
    return 1;
}

foreach (var cam in cameras)
    Console.WriteLine($"  [{cam.Id}] {cam.Name}");

Console.WriteLine();

// Use the first camera.
var target = cameras[0];
Console.WriteLine($"Using: {target.Name}");
Console.WriteLine();

// ── 2. Read snapshot (formats + controls, no persistent open) ─────────
Console.WriteLine("Reading snapshot (brief open)...");
var snapshot = await CameraDevice.ReadSnapshotAsync(target);

Console.WriteLine($"  Endpoint : {snapshot.NativeEndpointId}");
Console.WriteLine($"  Formats  : {snapshot.Formats.Count}");
Console.WriteLine($"  Controls : {snapshot.Controls.Count}");
Console.WriteLine();

Console.WriteLine("  Formats:");
foreach (var fmt in snapshot.Formats)
{
    var fps = fmt.MaxFrameRate.ToDouble();
    Console.WriteLine($"    {fmt.Width,5}x{fmt.Height,-5}  {fmt.PixelFormat,-10}  {fps,6:F1} fps  ({fmt.Transport})");
}

Console.WriteLine();
Console.WriteLine("  Controls:");
foreach (var ctrl in snapshot.Controls)
{
    var range = ctrl.MinValue.HasValue ? $"[{ctrl.MinValue}..{ctrl.MaxValue}]" : "";
    var flags = string.Join(", ",
        new[] { ctrl.SupportsAutoMode ? "auto" : null, ctrl.IsReadOnly ? "ro" : null }
            .Where(f => f is not null));
    Console.WriteLine($"    {ctrl.Name,-25} {range,20}  default={ctrl.DefaultValue}  {flags}");
}

Console.WriteLine();

if (snapshotOnly)
{
    Console.WriteLine("Snapshot-only mode. Done.");
    return 0;
}

// ── 3. Open device and capture frames ─────────────────────────────────
// Pick the highest-resolution MJPEG format, or fall back to first available.
var chosenFormat = snapshot.Formats
    .Where(f => f.PixelFormat == CameraPixelFormat.Mjpeg)
    .OrderByDescending(f => f.Width * f.Height)
    .ThenByDescending(f => f.MaxFrameRate.ToDouble())
    .FirstOrDefault() ?? snapshot.Formats[0];

Console.WriteLine($"Capturing {maxFrames} frames at {chosenFormat.Width}x{chosenFormat.Height} " +
    $"{chosenFormat.PixelFormat} ({chosenFormat.MaxFrameRate} fps)...");
Console.WriteLine();

var config = new CameraConfiguration(chosenFormat);

await using var session = await CameraSession.OpenAsync(target, config);

int count = 0;
long totalBytes = 0;

using var cts = new CancellationTokenSource();

await foreach (var frame in session.CaptureAsync(ct: cts.Token))
{
    using (frame)
    {
        totalBytes += frame.ContiguousBuffer.Length;
        count++;

        if (count % 10 == 0 || count == 1)
        {
            var m = session.Metrics;
            Console.WriteLine($"  Frame {count,4}: {frame.Width}x{frame.Height}  " +
                $"{frame.PixelFormat}  {frame.ContiguousBuffer.Length,8} bytes  " +
                $"ts={frame.Timestamp.TotalMilliseconds:F1}ms  " +
                $"produced={m.FramesProduced}  dropped={m.FramesDropped}  leases={m.OutstandingLeases}");
        }

        if (count >= maxFrames)
            cts.Cancel();
    }
}

Console.WriteLine();
Console.WriteLine($"Done. {count} frames captured, {totalBytes:N0} bytes total.");

var final = session.Metrics;
Console.WriteLine($"Metrics: produced={final.FramesProduced}  dropped={final.FramesDropped}  " +
    $"last_ts={final.LastFrameTimestamp?.TotalMilliseconds:F1}ms");

return 0;
