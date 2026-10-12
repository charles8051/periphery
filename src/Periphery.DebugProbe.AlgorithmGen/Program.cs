// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0
//
// flash-algorithm-gen: writes one flash algorithm record for Periphery.DebugProbe.StLink from a
// CMSIS-Pack FLM file and its pack description (ADR-0094 D9).
//
//   flash-algorithm-gen --pack-dir <dir> --pack <name> --version <v> --flm <path-in-pack>
//                       --pdsc <file-in-pack-dir> --notice <file> --field <CSharpName> --out <file>
//
// Packs/README.md lists the command that produced each checked-in record.

using System.Security.Cryptography;
using Periphery.DebugProbe.AlgorithmGen;

var options = new Dictionary<string, string>(StringComparer.Ordinal);
for (int i = 0; i + 1 < args.Length; i += 2)
    options[args[i]] = args[i + 1];

string[] required = ["--pack-dir", "--pack", "--version", "--flm", "--pdsc", "--notice", "--field", "--out"];
if (required.FirstOrDefault(r => !options.ContainsKey(r)) is { } missing)
{
    Console.Error.WriteLine($"missing {missing}");
    return 2;
}

var input = Generator.Load(
    options["--pack-dir"], options["--pack"], options["--version"], options["--flm"],
    options["--pdsc"], options["--notice"], options["--field"]);
File.WriteAllText(options["--out"], RecordWriter.Write(input));
Console.WriteLine($"{input.FieldName}: {input.Flm.Name}, {input.Flm.Code.Length} bytes of code, SHA-256 {input.Sha256}");
return 0;

namespace Periphery.DebugProbe.AlgorithmGen
{
    /// <summary>Gathers a record's inputs from a pack directory.</summary>
    internal static class Generator
    {
        public static RecordInput Load(
            string packDir, string pack, string version, string flmPath, string pdscFile, string noticeFile, string field)
        {
            byte[] flm = File.ReadAllBytes(Path.Combine(packDir, Path.GetFileName(flmPath)));
            return new RecordInput(
                FieldName: field,
                Flm: FlmReader.Read(flm),
                Pdsc: PdscReader.Find(File.ReadAllText(Path.Combine(packDir, pdscFile)), flmPath),
                Pack: pack,
                Version: version,
                FlmPath: flmPath,
                Sha256: Convert.ToHexString(SHA256.HashData(flm)),
                Notice: File.ReadAllText(Path.Combine(packDir, noticeFile)));
        }
    }
}
