// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Globalization;
using System.Text.Json;

namespace Periphery.DebugProbe.AlgorithmGen;

/// <summary>An algorithm's flash range and RAM window, as its pack description lists them.</summary>
internal sealed record PackAlgorithm(uint Start, uint Size, uint RamStart, uint RamSize);

/// <summary>One algorithm in a pack manifest, and the record it becomes.</summary>
internal sealed record ManifestAlgorithm(string Field, string Flm, string FlmSha256, PackAlgorithm Window, string Notice);

/// <summary>
/// What the generator needs from one pack release (ADR-0094 D9, 2026-10-12 amendment). It
/// replaces the checked-in <c>.pdsc</c>: <c>flash-algorithm-gen extract</c> reads the pack
/// description once, when an algorithm is added, and its SHA-256 is pinned here so the window
/// can be checked against the tag.
/// </summary>
internal sealed record PackManifest(
    string Pack, string Version, string Tag, string Pdsc, string PdscSha256, IReadOnlyList<ManifestAlgorithm> Algorithms)
{
    /// <summary>Parses a manifest. Addresses and sizes are hex strings, <c>0x</c>-prefixed.</summary>
    public static PackManifest Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var algorithms = root.GetProperty("algorithms").EnumerateArray()
            .Select(a => new ManifestAlgorithm(
                Field: Text(a, "field"),
                Flm: Text(a, "flm"),
                FlmSha256: Text(a, "flmSha256"),
                Window: new PackAlgorithm(Hex(a, "start"), Hex(a, "size"), Hex(a, "ramStart"), Hex(a, "ramSize")),
                Notice: Text(a, "notice")))
            .ToList();
        if (algorithms.Count == 0)
            throw new InvalidDataException("The manifest lists no algorithms.");

        return new PackManifest(
            Text(root, "pack"), Text(root, "version"), Text(root, "tag"), Text(root, "pdsc"), Text(root, "pdscSha256"),
            algorithms);
    }

    /// <summary>Formats a value the way the manifest stores it.</summary>
    public static string FormatHex(uint value) => string.Create(CultureInfo.InvariantCulture, $"0x{value:X8}");

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.GetString() is { Length: > 0 } text
            ? text
            : throw new InvalidDataException($"The manifest has no {name}.");

    private static uint Hex(JsonElement element, string name)
    {
        string text = Text(element, name);
        return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && uint.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value)
            ? value
            : throw new InvalidDataException($"The manifest's {name} \"{text}\" is not a 0x-prefixed hex number.");
    }
}
