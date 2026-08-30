using Periphery.Examples.Camera.Common;

namespace Periphery.Examples.Camera.Commands;

/// <summary>
/// Demonstrates control read/write through <see cref="CameraDevice"/>:
/// <c>GetControlsAsync</c>, <c>SetControlAsync</c>, <c>ResetControlAsync</c>.
/// On Windows these map to IAMCameraControl/IAMVideoProcAmp; on V4L2 they
/// map to V4L2 control IDs; on AVFoundation they map to capture-device
/// properties. The CameraControlKind enum is the cross-platform vocabulary.
/// </summary>
internal static class ControlsCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var device = await Args.ResolveCameraAsync(args).ConfigureAwait(false);
        if (device is null) return 1;

        var setExpr = Args.GetOption(args, "--set");
        var resetExpr = Args.GetOption(args, "--reset");

        await using var camera = await CameraDevice.OpenAsync(device).ConfigureAwait(false);

        if (setExpr is not null)
        {
            var (kind, value) = ParseSet(setExpr);
            await camera.SetControlAsync(kind, value).ConfigureAwait(false);
            Console.WriteLine($"Set {kind} = {value}");
            Console.WriteLine();
        }

        if (resetExpr is not null)
        {
            if (!Enum.TryParse<CameraControlKind>(resetExpr, ignoreCase: true, out var kind))
            {
                Console.Error.WriteLine($"Unknown control kind: {resetExpr}");
                return 1;
            }
            await camera.ResetControlAsync(kind).ConfigureAwait(false);
            Console.WriteLine($"Reset {kind} to default");
            Console.WriteLine();
        }

        // Always print the current snapshot of controls so the user sees
        // the post-mutation state.
        var controls = await camera.GetControlsAsync().ConfigureAwait(false);
        Console.WriteLine($"Controls on {device.Name ?? "(unnamed)"}:");
        Console.WriteLine();

        foreach (var c in controls)
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

    private static (CameraControlKind Kind, double Value) ParseSet(string expr)
    {
        var parts = expr.Split('=', 2);
        if (parts.Length != 2)
            throw new ArgumentException($"Expected --set KIND=VALUE, got '{expr}'");

        if (!Enum.TryParse<CameraControlKind>(parts[0], ignoreCase: true, out var kind))
            throw new ArgumentException($"Unknown control kind: {parts[0]}");

        if (!double.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var value))
            throw new ArgumentException($"Value must be numeric: {parts[1]}");

        return (kind, value);
    }
}
