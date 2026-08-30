using System.Globalization;
using System.Text;
using Periphery;
using PortPathProbe;

// ─────────────────────────────────────────────────────────────────────────────
// ADR-0079 D4 probe — closes the blocking item.
//
// The type under test is Periphery.PortPath itself; the contract fixtures live in tests/, so
// what remains here is purely the measurement against real hardware.
//
//   Run A  HID\* cross-validation .... the blocking item itself: does the parser
//                                      agree with an independent devnode walk on
//                                      the population where ResolveLocationPath
//                                      SYNTHESIZES the path rather than reading it?
//   Run B  USB\VID_* re-run .......... reproduces the original 42-for-42 alongside,
//                                      so both populations come from one snapshot.
//   Run C  instance-id fallbacks ..... D7 requires these to NOT parse. Measures it.
//   Run D  zero-hop split ............ separates root hubs from non-paths, which the
//                                      Context table folded into one row of 5.
//   Run E  ResolveLocationPath depth .. headroom against its maxDepth: 8 bound.
//   Run F  D5 fixture candidates ..... real examples of each relation, for the tests.
//
// Both sides read the same cfgmgr32 tree; see CfgMgr.cs for what that does and does
// not license us to claim.
// ─────────────────────────────────────────────────────────────────────────────

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("This probe is Windows-only: ADR-0079 is scoped to the Windows grammar (D2).");
    return 2;
}

string outDir = Directory.GetCurrentDirectory();
for (int i = 0; i < args.Length - 1; i++)
    if (args[i] is "--out" or "-o") outDir = args[i + 1];
Directory.CreateDirectory(outDir);

Console.WriteLine("Enumerating (DeviceCategory.All)...");
var devices = await Devices.Enumerate().ToListAsync();
Console.WriteLine($"  {devices.Count} devices.\n");

// ── Measure every device once ────────────────────────────────────────────────

var rows = new List<Row>(devices.Count);
foreach (var d in devices)
{
    string id = d.Id.Value;
    string? loc = d.LocationPath;

    bool parsed = PortPath.TryParse(loc, out var path, out var failure);
    int? parsedCount = parsed && path.TryGetExternalHubCount(out int c) ? c : null;
    bool? isRoot = parsed && path.TryGetIsRootHub(out bool r) ? r : null;

    var walk = CfgMgr.WalkExternalHubs(id);
    int? walkCount = walk.TryGetExternalHubCount(out int wc) ? wc : null;

    // Did ResolveLocationPath fall back to the instance id? Then LocationPath is not a
    // path at all, and D7 says it must not parse.
    bool isFallback = loc is not null && string.Equals(loc, id, StringComparison.OrdinalIgnoreCase);

    var (ownStatus, _) = CfgMgr.OwnLocationPaths(id);
    var (depth, resolveOutcome) = CfgMgr.DepthToFirstLocationPath(id);

    rows.Add(new Row(id, Plane(id), loc, parsed, failure, parsedCount, isRoot,
                     path.IsParsed ? path.Hops.Length : 0, walk.Termination, walkCount,
                     isFallback, ownStatus, depth, resolveOutcome, path));
}

// ── Runs A and B: the cross-validation, per plane ────────────────────────────

Console.WriteLine("── Run A/B: parsed count vs independent DEVPKEY_Device_Parent walk ──\n");
var agreementByPlane = new Dictionary<string, Agreement>();
foreach (var plane in new[] { "HID", "USB" })
{
    var subset = rows.Where(r => r.Plane == plane).ToList();
    var a = Agreement.Of(subset);
    agreementByPlane[plane] = a;
    Console.WriteLine($"  {plane,-4} n={subset.Count,4}   agree={a.Agree,4}  DISAGREE={a.Disagree,4}   " +
                      $"parser-only={a.ParserOnly,3}  walk-only={a.WalkOnly,3}  neither={a.Neither,3}");
}
Console.WriteLine();

foreach (var r in rows.Where(r => r.Disagrees))
    Console.WriteLine($"  DISAGREEMENT  {r.Id}\n      loc={r.LocationPath}\n" +
                      $"      parsed={r.ParsedCount}  walk={r.WalkCount} ({r.WalkTermination})");

// ── Run C: instance-id fallbacks must not parse (D7) ─────────────────────────

var fallbacks = rows.Where(r => r.IsFallback).ToList();
var fallbacksThatParsed = fallbacks.Where(r => r.Parsed).ToList();
Console.WriteLine($"\n── Run C: ResolveLocationPath instance-id fallbacks ──\n");
Console.WriteLine($"  fallbacks: {fallbacks.Count}   of which parsed (MUST BE 0): {fallbacksThatParsed.Count}");
foreach (var r in fallbacksThatParsed.Take(10))
    Console.WriteLine($"    VIOLATION  {r.Id}");

// ── Run D: the zero-hop split the Context table folded together ──────────────

var rootHubs = rows.Where(r => r.Parsed && r.HopCount == 0).ToList();
var nonPaths = rows.Where(r => !r.Parsed).ToList();
Console.WriteLine($"\n── Run D: zero-hop split ──\n");
Console.WriteLine($"  parsed with 0 hops (root hubs):        {rootHubs.Count}");
Console.WriteLine($"  did not parse (not port paths at all): {nonPaths.Count}");
foreach (var g in nonPaths.GroupBy(r => r.Failure).OrderByDescending(g => g.Count()))
    Console.WriteLine($"      {g.Key,-32} {g.Count(),5}");

Console.WriteLine($"\n  hop-count distribution over parsed paths:");
foreach (var g in rows.Where(r => r.Parsed).GroupBy(r => r.HopCount).OrderBy(g => g.Key))
    Console.WriteLine($"      {g.Key} hop(s): {g.Count(),4}   → {(g.Key == 0 ? 0 : g.Key - 1)} external hub(s)");

// ── Run E: headroom against ResolveLocationPath's maxDepth: 8 ────────────────

// Only PropertyRead.Absent is "genuinely empty". Unreadable and NodeNotFound are NOT
// evidence of synthesis — conflating them is what let an earlier version of this probe
// claim 21/21 HID paths were synthesized when some may not have been readable at all.
var resolved = rows.Where(r => r.OwnPath == CfgMgr.PropertyRead.Absent
                            && r.Resolve == CfgMgr.ResolveOutcome.Found).ToList();
Console.WriteLine($"\n── Run E: ResolveLocationPath ancestor depth (bound is 8) ──\n");
Console.WriteLine($"  nodes with a genuinely empty own LocationPaths, resolved via an ancestor: {resolved.Count}");
if (resolved.Count > 0)
    Console.WriteLine($"  depth to first path-carrying ancestor: max={resolved.Max(r => r.Depth)}  " +
                      $"mean={resolved.Average(r => r.Depth):F2}");
Console.WriteLine($"  nodes where NO ancestor carried a path (the fallback case): " +
                  $"{rows.Count(r => r.Resolve == CfgMgr.ResolveOutcome.NoAncestorCarriesPath)}");
Console.WriteLine("  own-LocationPaths read outcomes: " +
                  string.Join(", ", rows.GroupBy(r => r.OwnPath).OrderBy(g => g.Key)
                                        .Select(g => $"{g.Key}={g.Count()}")));

var hidRows = rows.Where(r => r.Plane == "HID").ToList();
Console.WriteLine($"  HID nodes with a genuinely empty own LocationPaths: " +
                  $"{hidRows.Count(r => r.OwnPath == CfgMgr.PropertyRead.Absent)} of {hidRows.Count}" +
                  $"  (unreadable: {hidRows.Count(r => r.OwnPath is CfgMgr.PropertyRead.Unreadable or CfgMgr.PropertyRead.NodeNotFound)})");

// ── Run F: real fixtures for D5's relation tests ─────────────────────────────

Console.WriteLine($"\n── Run F: D5 fixture candidates found on this machine ──\n");
var parsedRows = rows.Where(r => r.Parsed).ToList();
EmitFixture("same controller, DIFFERENT root ports (the D5 counterexample)",
    Pairs(parsedRows, (a, b) => a.Path.SharesControllerWith(b.Path) == Tri.Yes
                             && a.Path.SharesRootPortWith(b.Path) == Tri.No));
EmitFixture("same EXTERNAL hub (both >= 2 hops, differ in last)",
    Pairs(parsedRows, (a, b) => a.Path.SharesExternalHubWith(b.Path) == Tri.Yes));
EmitFixture("downstream-of (proper prefix)",
    Pairs(parsedRows, (a, b) => a.Path.IsDownstreamOf(b.Path) == Tri.Yes));
EmitFixture("same port, one hop — IsSamePortAs=Yes but SharesExternalHubWith=No",
    Pairs(parsedRows, (a, b) => a.Path.IsSamePortAs(b.Path) == Tri.Yes
                             && a.Path.SharesExternalHubWith(b.Path) == Tri.No
                             && a.HopCount == 1));
EmitFixture("different controllers",
    Pairs(parsedRows, (a, b) => a.Path.SharesControllerWith(b.Path) == Tri.No));

// ── Output ───────────────────────────────────────────────────────────────────

// Deliberately NOT Environment.MachineName. This output gets committed as
// exploration evidence, and naming it after the machine put a real hostname
// into five filenames under docs/explorations/ - where no content grep could
// see it - and into the markdown body as well.
// Second-resolution, not date-only: two runs on the same day would otherwise
// write the same two files, and a reader comparing a CSV against a .md could be
// looking at different runs.
string label = DateTime.UtcNow.ToString("yyyy-MM-dd-HHmmss");
string csvPath = Path.Combine(outDir, $"portpath-probe-{label}.csv");
var csv = new StringBuilder();
csv.AppendLine("instance_id,plane,location_path,parsed,parse_failure,hop_count,parsed_external_hubs," +
               "is_root_hub,walk_termination,walk_external_hubs,agrees,is_instanceid_fallback," +
               "own_locationpaths_read,ancestor_depth_to_path,resolve_outcome");
foreach (var r in rows) csv.AppendLine(r.ToCsv());
File.WriteAllText(csvPath, csv.ToString());

string mdPath = Path.Combine(outDir, $"portpath-probe-{label}.md");
File.WriteAllText(mdPath, Markdown(label, devices.Count, rows, agreementByPlane, rootHubs.Count,
                                   nonPaths.Count, fallbacks.Count, fallbacksThatParsed.Count));

Console.WriteLine($"\nWrote:\n  {csvPath}\n  {mdPath}");

// ── Pass condition ───────────────────────────────────────────────────────────
//
// An earlier version only failed on Disagrees, which requires BOTH counts to exist. A
// cfgmgr32 failure or an unexpected parent chain therefore produced "parser-only" and still
// exited 0 — the probe reporting an unvalidated row as a validated one, which is exactly the
// failure mode ADR-0079 D7 forbids the API from committing. Every parsed row must now be
// independently corroborated, with one narrow and explicitly justified exception.

var problems = new List<string>();

// The retirement tripwire that used to stand here has fired and been removed: the scratch
// transcription is gone, its fixtures live in tests/ against the shipping type, and the probe
// now measures Periphery.PortPath itself.

foreach (var r in rows.Where(r => r.Disagrees))
    problems.Add($"count disagreement: {r.Id} parsed={r.ParsedCount} walk={r.WalkCount}");

foreach (var r in fallbacksThatParsed)
    problems.Add($"instance-id fallback parsed as a port path (D7): {r.Id}");

foreach (var r in rows.Where(r => r.Parsed))
{
    // THE EXCEPTION, stated narrowly: for a root hub the independent walk cannot corroborate,
    // because it looks for a root hub AMONG THE ANCESTORS and this node IS one. It terminates
    // NoParent by construction. Any other inconclusive termination is a real gap in coverage.
    if (r.IsRootHub == true)
    {
        if (r.WalkTermination != CfgMgr.Termination.NoParent)
            problems.Add($"root hub walked to {r.WalkTermination}, expected NoParent: {r.Id}");
        continue;
    }

    if (r.WalkTermination != CfgMgr.Termination.ReachedRootHub)
        problems.Add($"parsed row not independently corroborated ({r.WalkTermination}): {r.Id}");
}

// A row whose own-property read failed cannot support any claim about synthesis.
foreach (var r in rows.Where(r => r.OwnPath == CfgMgr.PropertyRead.Unreadable))
    problems.Add($"own LocationPaths unreadable, so synthesis is unestablished: {r.Id}");
foreach (var r in rows.Where(r => r.Resolve == CfgMgr.ResolveOutcome.Unreadable && r.Parsed))
    problems.Add($"ancestor walk hit an unreadable node: {r.Id}");

bool clean = problems.Count == 0;
if (!clean)
{
    Console.WriteLine($"\n── {problems.Count} problem(s) ──");
    foreach (var problem in problems.Take(25)) Console.WriteLine($"  {problem}");
    if (problems.Count > 25) Console.WriteLine($"  … and {problems.Count - 25} more");
}

Console.WriteLine(clean
    ? "\nRESULT: every parsed row independently corroborated. D4's re-run passes."
    : "\nRESULT: FAILURES ABOVE — ADR-0079 D4 is not discharged. Do not accept the ADR on this run.");
return clean ? 0 : 1;

// ── Helpers ──────────────────────────────────────────────────────────────────

static string Plane(string instanceId)
{
    int slash = instanceId.IndexOf('\\');
    return slash <= 0 ? "?" : instanceId[..slash].ToUpperInvariant();
}

static List<(Row A, Row B)> Pairs(List<Row> rows, Func<Row, Row, bool> pred)
{
    var found = new List<(Row, Row)>();
    for (int i = 0; i < rows.Count && found.Count < 2; i++)
        for (int j = i + 1; j < rows.Count && found.Count < 2; j++)
            if (pred(rows[i], rows[j])) found.Add((rows[i], rows[j]));
    return found;
}

static void EmitFixture(string label, List<(Row A, Row B)> pairs)
{
    Console.WriteLine($"  {label}:");
    if (pairs.Count == 0) { Console.WriteLine("      (none on this machine)"); return; }
    foreach (var (a, b) in pairs)
    {
        Console.WriteLine($"      {a.LocationPath}");
        Console.WriteLine($"      {b.LocationPath}");
        Console.WriteLine();
    }
}

static string Markdown(string host, int total, List<Row> rows, Dictionary<string, Agreement> byPlane,
                       int rootHubs, int nonPaths, int fallbacks, int fallbacksParsed)
{
    var sb = new StringBuilder();
    sb.AppendLine($"### ADR-0079 D4 re-run — `{host}`");
    sb.AppendLine();
    sb.AppendLine($"`{total}` devices enumerated with `DeviceCategory.All`.");
    sb.AppendLine();
    sb.AppendLine("| Plane | n | Agree | Disagree | Parser only | Walk only | Neither |");
    sb.AppendLine("| --- | --- | --- | --- | --- | --- | --- |");
    foreach (var (plane, a) in byPlane)
        sb.AppendLine($"| `{plane}\\*` | {a.Total} | {a.Agree} | **{a.Disagree}** | {a.ParserOnly} | {a.WalkOnly} | {a.Neither} |");
    sb.AppendLine();
    sb.AppendLine($"- Instance-id fallbacks: **{fallbacks}**, of which parsed: **{fallbacksParsed}** (D7 requires 0).");
    sb.AppendLine($"- Zero-hop split: **{rootHubs}** root hubs, **{nonPaths}** not port paths at all.");
    sb.AppendLine();
    sb.AppendLine("| Hops | Devices | External hubs |");
    sb.AppendLine("| --- | --- | --- |");
    foreach (var g in rows.Where(r => r.Parsed).GroupBy(r => r.HopCount).OrderBy(g => g.Key))
        sb.AppendLine($"| {g.Key} | {g.Count()} | {(g.Key == 0 ? 0 : g.Key - 1)} |");
    return sb.ToString();
}

internal readonly record struct Agreement(int Total, int Agree, int Disagree, int ParserOnly, int WalkOnly, int Neither)
{
    public static Agreement Of(List<Row> rows) => new(
        rows.Count,
        rows.Count(r => r.ParsedCount is { } p && r.WalkCount is { } w && p == w),
        rows.Count(r => r.Disagrees),
        rows.Count(r => r.ParsedCount is not null && r.WalkCount is null),
        rows.Count(r => r.ParsedCount is null && r.WalkCount is not null),
        rows.Count(r => r.ParsedCount is null && r.WalkCount is null));
}

internal readonly record struct Row(
    string Id,
    string Plane,
    string? LocationPath,
    bool Parsed,
    ParseFailure Failure,
    int? ParsedCount,
    bool? IsRootHub,
    int HopCount,
    CfgMgr.Termination WalkTermination,
    int? WalkCount,
    bool IsFallback,
    CfgMgr.PropertyRead OwnPath,
    int Depth,
    CfgMgr.ResolveOutcome Resolve,
    PortPath Path)
{
    public bool Disagrees => ParsedCount is { } p && WalkCount is { } w && p != w;

    public string ToCsv()
    {
        static string Q(string? s) => s is null ? "" : "\"" + s.Replace("\"", "\"\"") + "\"";
        static string N(int? v) => v?.ToString(CultureInfo.InvariantCulture) ?? "";
        return string.Join(',',
            Q(Id), Q(Plane), Q(LocationPath), Parsed, Failure, HopCount, N(ParsedCount),
            IsRootHub?.ToString() ?? "", WalkTermination, N(WalkCount),
            ParsedCount is null || WalkCount is null ? "" : (!Disagrees).ToString(),
            IsFallback, OwnPath, Depth, Resolve);
    }
}
