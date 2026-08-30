using Periphery.Examples.Camera.Common;

namespace Periphery.Examples.Camera.Commands;

/// <summary>
/// Demonstrates <see cref="CameraDevice.ReadSnapshotAsync"/> — the ADR-0026
/// "brief open" path. The camera stack is activated, formats and controls
/// are read, and the device is closed before the call returns. No session,
/// no capture.
/// </summary>
internal static class SnapshotCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var device = await Args.ResolveCameraAsync(args).ConfigureAwait(false);
        if (device is null) return 1;

        Console.WriteLine($"Reading snapshot for: {device.Name ?? "(unnamed)"}");
        Console.WriteLine();

        var snap = await CameraDevice.ReadSnapshotAsync(device).ConfigureAwait(false);

        Console.WriteLine($"Endpoint : {snap.NativeEndpointId}");
        Console.WriteLine($"Formats  : {snap.Formats.Count}");
        Console.WriteLine($"Controls : {snap.Controls.Count}");
        Console.WriteLine();

        Console.WriteLine("Formats");
        Console.WriteLine("───────");
        foreach (var f in snap.Formats)
        {
            var fps = f.MaxFrameRate.ToDouble();
            Console.WriteLine(
                $"  {f.Width,5}x{f.Height,-5}  {f.PixelFormat,-10}  {fps,6:F1} fps  ({f.Transport})");
        }

        Console.WriteLine();
        Console.WriteLine("Controls");
        Console.WriteLine("────────");
        foreach (var c in snap.Controls)
        {
            var range = c.MinValue.HasValue ? $"[{c.MinValue}..{c.MaxValue}]" : "";
            var flags = string.Join(", ", new[]
            {
                c.SupportsAutoMode ? "auto" : null,
                c.IsReadOnly       ? "ro"   : null,
            }.Where(s => s is not null));

            Console.WriteLine(
                $"  {c.Name,-25} {range,18}  default={c.DefaultValue}  {flags}");
        }

        return 0;
    }
}
