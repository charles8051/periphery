using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using InTheHand.Net.Bluetooth;
using InTheHand.Net.Sockets;
using Periphery;

// ─────────────────────────────────────────────────────────────────────────────
// ADR-0085 probe — settles three of the four open questions.
//
// Build and run this once per TFM. The source is identical across all three; the
// only variable is which InTheHand.Net.Bluetooth 4.2.1 asset NuGet resolved, so a
// behavioural difference between two runs is attributable to the asset.
//
//   Run A  which asset loaded ....... prints the resolved assembly path, size, and
//                                     MVID. Turns "the bare asset contains a
//                                     bthprops.cpl string" into "the bare asset is
//                                     the one that ran".
//   Run B  radio ................... BluetoothRadio.Default. A throw-only stub
//                                     fails here; a working Win32 or WinRT provider
//                                     returns a radio with a name and an address.
//                                     THIS is the open question.
//   Run C  paired devices .......... BluetoothClient.PairedDevices, with each
//                                     device's Connected / Authenticated.
//   Run D  the D5 join ............. parses BD_ADDR out of Periphery's Bluetooth
//                                     instance ids and joins to Run C both ways.
//                                     Extends the earlier n=1 BR/EDR check, and
//                                     covers BTHLE\DEV_ for the first time.
//   Run E  poll agreement .......... --watch N. Polls 32feet Connected and
//                                     Periphery IsActive together and logs every
//                                     edge, so an LE peripheral can be measured
//                                     the way the BR/EDR keyboard already was.
//   Run F  poll cost ............... times the Run C+D cycle, for the "default or
//                                     opt-in" question.
//
// BD_ADDRs are masked by default. --show-addresses prints them in full; do not use
// it when the output is going anywhere shareable.
// ─────────────────────────────────────────────────────────────────────────────

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("This probe is Windows-only: ADR-0085's open questions are about Windows asset selection.");
    return 2;
}

bool showAddresses = args.Contains("--show-addresses");
int watchSeconds = 0;
for (int i = 0; i < args.Length - 1; i++)
    if (args[i] is "--watch" or "-w" && int.TryParse(args[i + 1], out int w)) watchSeconds = w;

Console.WriteLine($"TFM: {Tfm()}   masking: {(showAddresses ? "OFF" : "on")}");
Console.WriteLine($"     platform symbols defined: {Symbols()}");
Console.WriteLine(new string('─', 78));

// ── Run A — which asset actually loaded ──────────────────────────────────────

Console.WriteLine("\nRun A — resolved 32feet asset\n");

var asm = typeof(BluetoothRadio).Assembly;
string asmPath = asm.Location;
Console.WriteLine($"  path      {(asmPath.Length == 0 ? "<single-file / no location>" : asmPath)}");
Console.WriteLine($"  lib dir   {LibFolderOf(asmPath)}");
Console.WriteLine($"  size      {(asmPath.Length > 0 && File.Exists(asmPath) ? new FileInfo(asmPath).Length.ToString("N0") + " bytes" : "n/a")}");
Console.WriteLine($"  mvid      {asm.ManifestModule.ModuleVersionId}");
Console.WriteLine($"  version   {asm.GetName().Version}  ({asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion})");

// ── Run B — the open question ────────────────────────────────────────────────

Console.WriteLine("\nRun B — BluetoothRadio.Default\n");

BluetoothRadio? radio = null;
try
{
    radio = BluetoothRadio.Default;
    if (radio is null)
    {
        Console.WriteLine("  null — provider present, no radio found (or none powered).");
    }
    else
    {
        Console.WriteLine($"  name      {radio.Name}");
        Console.WriteLine($"  address   {Mask(radio.LocalAddress.ToString())}");
        Console.WriteLine($"  mode      {radio.Mode}");
        Console.WriteLine($"  lmp       {radio.LmpVersion} / sub {radio.LmpSubversion}");
        Console.WriteLine($"  vendor    {radio.Manufacturer}");
        Console.WriteLine("\n  VERDICT: this asset has a working provider on Windows.");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"  {ex.GetType().Name}: {ex.Message}");
    Console.WriteLine("\n  VERDICT: this asset has NO working provider on Windows.");
}

// ── Run C — paired devices ───────────────────────────────────────────────────

Console.WriteLine("\nRun C — BluetoothClient.PairedDevices\n");

var paired = new List<BluetoothDeviceInfo>();
try
{
    using var client = new BluetoothClient();
    paired.AddRange(client.PairedDevices);
    Console.WriteLine($"  {paired.Count} paired device(s)\n");
    foreach (var d in paired)
        Console.WriteLine($"    {Mask(d.DeviceAddress.ToString()),-18}  conn={Yn(d.Connected)}  auth={Yn(d.Authenticated)}  {d.DeviceName}");
    if (paired.Count == 0)
        Console.WriteLine("    (none — an empty list from a working provider is not the same as a throw above)");
}
catch (Exception ex)
{
    Console.WriteLine($"  {ex.GetType().Name}: {ex.Message}");
}

// ── Run D — the ADR-0085 D5 join ─────────────────────────────────────────────

Console.WriteLine("\nRun D — instance id -> BD_ADDR join (ADR-0085 D5)\n");

var bluetoothNodes = await Devices.Enumerate().OfCategory(DeviceCategory.Bluetooth).ToListAsync();
Console.WriteLine($"  {bluetoothNodes.Count} node(s) in DeviceCategory.Bluetooth\n");

var joined = new List<(DeviceInfo Node, string Addr, BluetoothDeviceInfo? Match)>();
var unparsed = new List<DeviceInfo>();

foreach (var node in bluetoothNodes)
{
    var m = Probe.JoinKey.Match(node.Id.Value);
    if (!m.Success) { unparsed.Add(node); continue; }

    string addr = m.Groups["addr"].Value.ToUpperInvariant();
    var match = paired.FirstOrDefault(p =>
        string.Equals(p.DeviceAddress.ToString(), addr, StringComparison.OrdinalIgnoreCase));
    joined.Add((node, addr, match));
}

Console.WriteLine($"  parsed    {joined.Count,3}   (BTHENUM\\DEV_ or BTHLE\\DEV_)");
Console.WriteLine($"  unparsed  {unparsed.Count,3}   (service nodes, radio nodes — expected not to match)\n");

foreach (var (node, addr, match) in joined)
{
    string transport = node.Id.Value.StartsWith("BTHLE", StringComparison.OrdinalIgnoreCase) ? "LE   " : "BR/EDR";
    Console.WriteLine($"    {transport}  {Mask(addr),-18}  IsActive={Yn(node.IsActive)}  " +
                      $"32feet={(match is null ? "NO MATCH" : $"conn={Yn(match.Connected)}")}  {node.Name}");
}

int matched = joined.Count(j => j.Match is not null);
Console.WriteLine($"\n  {matched}/{joined.Count} parsed node(s) matched a paired device.");
Console.WriteLine($"  {paired.Count(p => joined.All(j => j.Match != p))}/{paired.Count} paired device(s) had no node.");

if (joined.Count > 0)
{
    int agree = joined.Count(j => j.Match is not null && j.Node.IsActive == j.Match!.Connected);
    Console.WriteLine($"  {agree}/{matched} matched pair(s) agree on liveness right now (IsActive == Connected).");
}

if (unparsed.Count > 0)
{
    Console.WriteLine("\n  unparsed node id prefixes:");
    foreach (var g in unparsed.GroupBy(n => Prefix(n.Id.Value)).OrderByDescending(g => g.Count()))
        Console.WriteLine($"    {g.Count(),3}  {g.Key}");
}

// ── Run F — poll cost ────────────────────────────────────────────────────────

Console.WriteLine("\nRun F — poll cost\n");

if (paired.Count == 0)
{
    Console.WriteLine("  skipped — nothing paired to poll.");
}
else
{
    var samples = new List<double>(20);
    for (int i = 0; i < 20; i++)
    {
        var sw = Stopwatch.StartNew();
        foreach (var d in paired) { d.Refresh(); _ = d.Connected; }
        sw.Stop();
        samples.Add(sw.Elapsed.TotalMilliseconds);
    }
    samples.Sort();
    Console.WriteLine($"  {paired.Count} device(s) per cycle, 20 cycles");
    Console.WriteLine($"  min {samples[0]:F1} ms   p50 {samples[samples.Count / 2]:F1} ms   " +
                      $"p95 {samples[(int)(samples.Count * 0.95)]:F1} ms   max {samples[^1]:F1} ms");
}

// ── Run E — poll agreement over a link toggle ────────────────────────────────

if (watchSeconds > 0)
{
    Console.WriteLine($"\nRun E — watching for {watchSeconds}s\n");
    Console.WriteLine("  Power-cycle the peripheral now. Every change on either side is logged.");
    Console.WriteLine("  32feet is polled at 1s; Periphery is re-enumerated at 1s.\n");

    var last = new Dictionary<string, (bool Conn, bool Active)>(StringComparer.OrdinalIgnoreCase);
    var ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var deadline = DateTime.UtcNow.AddSeconds(watchSeconds);
    var started = Stopwatch.StartNew();

    while (DateTime.UtcNow < deadline)
    {
        foreach (var d in paired) d.Refresh();
        var nodes = await Devices.Enumerate().OfCategory(DeviceCategory.Bluetooth).ToListAsync();

        foreach (var d in paired)
        {
            string addr = d.DeviceAddress.ToString();

            // One address can carry more than one DEV_ node: a dual-mode peripheral
            // exposes BTHENUM and BTHLE for the same BD_ADDR, and a stale devnode can
            // outlive the link. Their IsActive values are not interchangeable, so
            // picking whichever enumeration returned first would make the comparison
            // below say something about an unknown node. Report ambiguity instead.
            var candidates = nodes.Where(n =>
            {
                var m = Probe.JoinKey.Match(n.Id.Value);
                return m.Success && string.Equals(m.Groups["addr"].Value, addr, StringComparison.OrdinalIgnoreCase);
            }).ToList();

            if (candidates.Count > 1)
            {
                if (ambiguous.Add(addr))
                    Console.WriteLine($"  {started.Elapsed.TotalSeconds,6:F1}s  {Mask(addr),-18}  " +
                                      $"AMBIGUOUS — {candidates.Count} nodes for this address, not compared:");
                foreach (var c in candidates)
                    Console.WriteLine($"           {Prefix(c.Id.Value),-28}  IsActive={Yn(c.IsActive)}  {c.Name}");
                continue;
            }

            var node = candidates.Count == 1 ? candidates[0] : null;
            var now = (Conn: d.Connected, Active: node?.IsActive ?? false);
            if (last.TryGetValue(addr, out var prev) && prev == now) continue;

            string what = !last.ContainsKey(addr) ? "baseline"
                : node is null ? "no node"
                : prev.Conn != now.Conn && prev.Active != now.Active ? "both"
                : prev.Conn != now.Conn ? "32feet only"
                : "Periphery only";

            Console.WriteLine($"  {started.Elapsed.TotalSeconds,6:F1}s  {Mask(addr),-18}  " +
                              $"Connected={Yn(now.Conn)}  IsActive={Yn(now.Active)}  [{what}]");
            last[addr] = now;
        }

        await Task.Delay(1000);
    }

    Console.WriteLine("\n  A transition logged as \"32feet only\" or \"Periphery only\" is a disagreement.");
    Console.WriteLine("  \"both\" on the same line means they moved within the same 1s sample.");
}
else
{
    Console.WriteLine("\nRun E — skipped. Pass --watch 60 and power-cycle the peripheral to measure agreement.");
}

Console.WriteLine();
return 0;

// ── helpers ──────────────────────────────────────────────────────────────────

// Reports which SDK platform symbols are actually defined, rather than leaving it to
// be inferred from which branch Tfm() took. An undefined symbol in #if is simply
// false, so every candidate below compiles whether or not the SDK defines it.
static string Symbols()
{
    var on = new List<string>();
#if WINDOWS
    on.Add("WINDOWS");
#endif
#if WINDOWS7_0
    on.Add("WINDOWS7_0");
#endif
#if WINDOWS10_0_19041
    on.Add("WINDOWS10_0_19041");
#endif
#if WINDOWS10_0_19041_0
    on.Add("WINDOWS10_0_19041_0");
#endif
    return on.Count == 0 ? "(none — bare TFM)" : string.Join(", ", on);
}

static string Tfm() =>
#if WINDOWS10_0_19041_0
    "net10.0-windows10.0.19041  (expects lib/net8.0-windows10.0.19041, WinRT)";
#elif WINDOWS
    "net10.0-windows            (expects lib/net8.0-windows7.0, Win32)";
#else
    "net10.0                    (expects lib/net8.0, the bare asset under test)";
#endif

static string LibFolderOf(string path)
{
    if (path.Length == 0) return "<unknown>";
    var parts = path.Split(Path.DirectorySeparatorChar);
    int i = Array.FindIndex(parts, p => p.Equals("lib", StringComparison.OrdinalIgnoreCase));
    return i >= 0 && i + 1 < parts.Length ? $"lib/{parts[i + 1]}" : "<copied to output; see path>";
}

static string Yn(bool b) => b ? "yes" : "no ";

static string Prefix(string id)
{
    int slash = id.IndexOf('\\');
    if (slash < 0) return id;
    string head = id[..slash];
    string tail = id[(slash + 1)..];
    // Keep the shape, drop anything address-like.
    return $"{head}\\{(tail.StartsWith("DEV_", StringComparison.OrdinalIgnoreCase) ? "DEV_…" : tail.Split('_')[0] + "…")}";
}

string Mask(string addr)
{
    if (showAddresses) return addr;
    if (addr.Length < 4) return "…";
    // Enough to correlate rows within one run, not enough to identify a radio.
    uint h = 2166136261;
    foreach (char c in addr.ToUpperInvariant()) { h ^= c; h *= 16777619; }
    return $"{addr[..2]}…{addr[^2..]}#{h & 0xFFFF:X4}";
}

internal static class Probe
{
    // ADR-0085 D5. BTHENUM covers BR/EDR peripherals, BTHLE covers LE ones.
    // Service nodes (BTHENUM\{uuid}_VID&…, BTHLEDevice\{uuid}_Dev_…) deliberately
    // do not match: they are not the node that carries the link.
    internal static readonly Regex JoinKey =
        new(@"^BTH(?:ENUM|LE)\\DEV_(?<addr>[0-9A-Fa-f]{12})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
}
