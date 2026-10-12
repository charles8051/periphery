// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Globalization;
using System.Xml.Linq;

namespace Periphery.DebugProbe.AlgorithmGen;

/// <summary>
/// Reads an <c>&lt;algorithm&gt;</c> element from a CMSIS pack description (.pdsc). Used by
/// <c>flash-algorithm-gen extract</c> when an algorithm is added; the result goes into the
/// pack's manifest, and the .pdsc itself is not checked in.
/// </summary>
internal static class PdscReader
{
    /// <summary>
    /// The first <c>&lt;algorithm&gt;</c> whose <c>name</c> is <paramref name="flmPath"/>, compared
    /// without regard to case or slash direction. Every entry for one FLM must agree.
    /// </summary>
    public static PackAlgorithm Find(string pdscXml, string flmPath)
    {
        string want = Normalize(flmPath);
        var entries = XDocument.Parse(pdscXml).Descendants()
            .Where(e => e.Name.LocalName == "algorithm" && Normalize((string?)e.Attribute("name") ?? "") == want)
            .Select(e => new PackAlgorithm(
                Hex(e, "start"), Hex(e, "size"), Hex(e, "RAMstart"), Hex(e, "RAMsize")))
            .Distinct()
            .ToList();

        return entries.Count switch
        {
            0 => throw new InvalidDataException($"The pack lists no algorithm {flmPath}."),
            1 => entries[0],
            _ => throw new InvalidDataException($"The pack lists {flmPath} with {entries.Count} different ranges."),
        };
    }

    private static string Normalize(string path) => path.Replace('\\', '/').ToUpperInvariant();

    private static uint Hex(XElement element, string attribute)
    {
        string text = (string?)element.Attribute(attribute)
            ?? throw new InvalidDataException($"An algorithm entry has no {attribute}.");
        return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? uint.Parse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : uint.Parse(text, CultureInfo.InvariantCulture);
    }
}
