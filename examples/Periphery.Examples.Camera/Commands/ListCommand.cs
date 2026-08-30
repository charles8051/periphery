using Periphery;

namespace Periphery.Examples.Camera.Commands;

/// <summary>
/// Discovery only — uses the zero-I/O <see cref="Devices.Enumerate"/> path
/// from the core Periphery package and filters to <c>DeviceCategory.Camera</c>.
/// No camera handles are opened.
/// </summary>
internal static class ListCommand
{
    public static async Task<int> RunAsync(string[] _)
    {
        var cameras = await Devices.Enumerate()
            .OfCategory(DeviceCategory.Camera)
            .ToListAsync()
            .ConfigureAwait(false);

        if (cameras.Count == 0)
        {
            Console.WriteLine("No cameras detected.");
            return 1;
        }

        Console.WriteLine($"{cameras.Count} camera(s) detected:");
        Console.WriteLine();

        for (int i = 0; i < cameras.Count; i++)
        {
            var c = cameras[i];
            Console.WriteLine($"  [{i}] {c.Name ?? "(unnamed)"}");
            Console.WriteLine($"      id:     {c.Id}");
            if (!string.IsNullOrWhiteSpace(c.Manufacturer))
                Console.WriteLine($"      vendor: {c.Manufacturer}");
            Console.WriteLine($"      bus:    {c.BusType}");
            Console.WriteLine();
        }

        return 0;
    }
}
