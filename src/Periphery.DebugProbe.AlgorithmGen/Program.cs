// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0
//
// flash-algorithm-gen: writes the flash algorithm records for Periphery.DebugProbe.StLink from the
// pinned CMSIS-Pack inputs in Packs/ (ADR-0094 D9).
//
//   flash-algorithm-gen generate --manifest <Packs/<pack>/manifest.json> --out-dir <dir>
//       Writes FlashAlgorithms.<field>.g.cs for every algorithm the manifest lists.
//
//   flash-algorithm-gen extract --pdsc <file> --flm <file> --flm-path <path in pack>
//       Prints the manifest values for one algorithm, read from a downloaded .pdsc and FLM.
//
// Packs/README.md has the exact commands for each checked-in record.

using System.Security.Cryptography;
using Periphery.DebugProbe.AlgorithmGen;

var options = new Dictionary<string, string>(StringComparer.Ordinal);
for (int i = 1; i + 1 < args.Length; i += 2)
    options[args[i]] = args[i + 1];

switch (args.FirstOrDefault())
{
    case "generate" when Require(options, "--manifest", "--out-dir"):
        foreach (var input in Generator.LoadAll(options["--manifest"]))
        {
            string path = Path.Combine(options["--out-dir"], $"FlashAlgorithms.{input.FieldName}.g.cs");
            File.WriteAllText(path, RecordWriter.Write(input));
            Console.WriteLine($"{input.FieldName}: {input.Flm.Name}, {input.Flm.Code.Length} bytes of code -> {path}");
        }
        return 0;

    case "extract" when Require(options, "--pdsc", "--flm", "--flm-path"):
        byte[] pdsc = File.ReadAllBytes(options["--pdsc"]);
        byte[] flm = File.ReadAllBytes(options["--flm"]);
        var window = PdscReader.Find(System.Text.Encoding.UTF8.GetString(pdsc), options["--flm-path"]);
        Console.WriteLine($"pdscSha256 {Convert.ToHexString(SHA256.HashData(pdsc))}");
        Console.WriteLine($"flm        {options["--flm-path"]}");
        Console.WriteLine($"flmSha256  {Convert.ToHexString(SHA256.HashData(flm))}");
        Console.WriteLine($"start      {PackManifest.FormatHex(window.Start)}");
        Console.WriteLine($"size       {PackManifest.FormatHex(window.Size)}");
        Console.WriteLine($"ramStart   {PackManifest.FormatHex(window.RamStart)}");
        Console.WriteLine($"ramSize    {PackManifest.FormatHex(window.RamSize)}");
        return 0;

    default:
        Console.Error.WriteLine("usage: flash-algorithm-gen generate --manifest <file> --out-dir <dir>");
        Console.Error.WriteLine("       flash-algorithm-gen extract --pdsc <file> --flm <file> --flm-path <path in pack>");
        return 2;
}

static bool Require(Dictionary<string, string> options, params string[] names)
{
    var missing = names.Where(n => !options.ContainsKey(n)).ToList();
    if (missing.Count > 0)
        Console.Error.WriteLine($"missing {string.Join(", ", missing)}");
    return missing.Count == 0;
}

namespace Periphery.DebugProbe.AlgorithmGen
{
    /// <summary>Gathers each record's inputs from a pack manifest and the files beside it.</summary>
    internal static class Generator
    {
        /// <summary>
        /// One input per algorithm the manifest lists. Refuses an FLM whose SHA-256 is not the one
        /// the manifest pins.
        /// </summary>
        public static IReadOnlyList<RecordInput> LoadAll(string manifestPath)
        {
            string packDir = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
            var manifest = PackManifest.Parse(File.ReadAllText(manifestPath));

            return manifest.Algorithms.Select(algorithm =>
            {
                byte[] flm = File.ReadAllBytes(Path.Combine(packDir, Path.GetFileName(algorithm.Flm)));
                string sha256 = Convert.ToHexString(SHA256.HashData(flm));
                if (!string.Equals(sha256, algorithm.FlmSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        $"{algorithm.Flm} has SHA-256 {sha256}; the manifest pins {algorithm.FlmSha256}.");

                return new RecordInput(
                    FieldName: algorithm.Field,
                    Flm: FlmReader.Read(flm),
                    Window: algorithm.Window,
                    Pack: manifest.Pack,
                    Version: manifest.Version,
                    FlmPath: algorithm.Flm,
                    Sha256: sha256,
                    Notice: File.ReadAllText(Path.Combine(packDir, algorithm.Notice)));
            }).ToList();
        }
    }
}
