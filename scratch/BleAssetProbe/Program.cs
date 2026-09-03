using System.Reflection;
using InTheHand.Bluetooth;

// ─────────────────────────────────────────────────────────────────────────────
// ADR-0085 D3 check — which InTheHand.BluetoothLE asset each TFM resolves, and
// whether it has a working provider.
//
// D3 decides Periphery.Ble.InTheHand's TFM set from assembly inspection: the bare
// asset carries BlueZ, the netstandard2.0 asset is a throw-only stub, and only
// net9.0-windows10.0.19041 carries WinRT GATT. Inspection is not execution, so
// this runs the three cases.
//
// The one that matters is net10.0-windows. If it resolves the bare asset, a
// consumer on the common unversioned Windows TFM gets the Linux provider from a
// package that compiled fine — which D3 must then answer for rather than assume
// away.
//
// No hardware, no pairing, no GATT traffic. Availability only.
// ─────────────────────────────────────────────────────────────────────────────

Console.WriteLine($"TFM: {Tfm()}");
Console.WriteLine(new string('─', 72));

var asm = typeof(GattCharacteristic).Assembly;
string path = asm.Location;
Console.WriteLine($"  path       {(path.Length == 0 ? "<no location>" : path)}");
Console.WriteLine($"  size       {(path.Length > 0 && File.Exists(path) ? new FileInfo(path).Length.ToString("N0") + " bytes" : "n/a")}");
Console.WriteLine($"  mvid       {asm.ManifestModule.ModuleVersionId}");
Console.WriteLine($"  version    {asm.GetName().Version}");

// Which provider is compiled in. The bare asset references the BlueZ stack; the
// WinRT asset does not, and vice versa. Referenced assemblies say which.
var refs = asm.GetReferencedAssemblies().Select(a => a.Name ?? "").ToArray();
string provider =
    refs.Any(n => n.Contains("Linux.Bluetooth", StringComparison.OrdinalIgnoreCase)
               || n.Contains("Tmds.DBus", StringComparison.OrdinalIgnoreCase)) ? "BlueZ (Linux)"
    : refs.Any(n => n.Contains("WinRT", StringComparison.OrdinalIgnoreCase)
               || n.Contains("Windows.SDK", StringComparison.OrdinalIgnoreCase)) ? "WinRT"
    : "neither — stub, or provider is in-assembly";
Console.WriteLine($"  provider   {provider}");
Console.WriteLine($"  refs       {string.Join(", ", refs.Where(n => !n.StartsWith("System") && n != "netstandard"))}");

// Does it actually work here? A stub throws; the wrong-platform provider throws or
// reports unavailable.
Console.Write("\n  Bluetooth.GetAvailabilityAsync()  ->  ");
try
{
    bool available = await Bluetooth.GetAvailabilityAsync();
    Console.WriteLine(available ? "true  (working provider, radio present)"
                                : "false (provider ran, reports no radio)");
}
catch (Exception ex)
{
    Console.WriteLine($"{ex.GetType().Name}: {ex.Message}");
}

Console.WriteLine();
return 0;

static string Tfm() =>
#if WINDOWS10_0_19041_0
    "net10.0-windows10.0.19041  (expects lib/net9.0-windows10.0.19041, WinRT)";
#elif WINDOWS
    "net10.0-windows            (expects lib/net9.0 — the D3 problem case)";
#else
    "net10.0                    (expects lib/net9.0, BlueZ)";
#endif
