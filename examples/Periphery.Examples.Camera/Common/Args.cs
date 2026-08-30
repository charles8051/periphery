using Periphery;

namespace Periphery.Examples.Camera.Common;

/// <summary>
/// Tiny helpers for parsing the toy CLI args used by the example commands.
/// Real CLIs should use System.CommandLine; this stays dependency-free.
/// </summary>
internal static class Args
{
    public static string? GetOption(string[] args, params string[] names)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            foreach (var name in names)
            {
                if (string.Equals(args[i], name, StringComparison.Ordinal))
                    return args[i + 1];
            }
        }
        return null;
    }

    public static int GetIntOption(string[] args, int defaultValue, params string[] names)
    {
        var raw = GetOption(args, names);
        return int.TryParse(raw, out var n) ? n : defaultValue;
    }

    public static bool HasFlag(string[] args, params string[] names)
    {
        foreach (var arg in args)
        {
            foreach (var name in names)
            {
                if (string.Equals(arg, name, StringComparison.Ordinal))
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Resolves a camera by --device NAME substring, or returns the first camera
    /// found. Writes diagnostic output to stderr and returns null when nothing
    /// matches.
    /// </summary>
    public static async Task<DeviceInfo?> ResolveCameraAsync(string[] args)
    {
        var name = GetOption(args, "--device", "-d");

        var cameras = await Devices.Enumerate()
            .OfCategory(DeviceCategory.Camera)
            .ToListAsync()
            .ConfigureAwait(false);

        if (cameras.Count == 0)
        {
            Console.Error.WriteLine("No cameras detected.");
            return null;
        }

        if (name is null)
        {
            return cameras[0];
        }

        var match = cameras.FirstOrDefault(c =>
            c.Name is not null && c.Name.Contains(name, StringComparison.OrdinalIgnoreCase));

        if (match is null)
        {
            Console.Error.WriteLine($"No camera matched '{name}'. Available:");
            foreach (var c in cameras)
                Console.Error.WriteLine($"  {c.Name ?? "(unnamed)"}");
        }

        return match;
    }
}
