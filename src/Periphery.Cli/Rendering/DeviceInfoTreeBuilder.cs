// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Collections;
using System.Collections.Immutable;
using System.Net;
using System.Reflection;
using Spectre.Console;

namespace Periphery.Cli.Rendering;

/// <summary>
/// One row of the verbose <see cref="DeviceInfo"/> dump: a label, an optional
/// already-formatted <see cref="Value"/>, and optional <see cref="Children"/>.
/// A leaf has a non-null <see cref="Value"/> and no children; a group node
/// (today only <see cref="DeviceInfo.Properties"/>) has a null
/// <see cref="Value"/> and one child per bag entry.
/// </summary>
/// <remarks>
/// Strings here are <em>presentation values</em>, not markup — they are the
/// output of <see cref="DeviceFieldProjection.FormatValue"/> with no Spectre
/// escaping or colour applied. Escaping/colour is the rendering shell's job
/// (<see cref="DeviceInfoTreeBuilder.Build"/>), which keeps this projection a
/// pure value transform that is testable without a console.
/// </remarks>
internal sealed record DeviceField(
    string Label,
    string? Value,
    IReadOnlyList<DeviceField> Children)
{
    /// <summary>A leaf row: a label and its formatted value, no children.</summary>
    public static DeviceField Leaf(string label, string value)
        => new(label, value, []);

    /// <summary>A group row: a label with child rows and no value of its own.</summary>
    public static DeviceField Group(string label, IReadOnlyList<DeviceField> children)
        => new(label, null, children);
}

/// <summary>
/// Pure projection of a <see cref="DeviceInfo"/> into the rows the verbose
/// <c>devices list --verbose</c> dump renders — the <em>decision</em> half
/// (which properties survive null/empty elision, how each value is formatted,
/// how the property bag nests), with no <c>Spectre.Console</c> dependency so
/// the regression-prone logic is unit-testable (functional core, ADR-0052).
/// </summary>
/// <remarks>
/// Reflection-driven so the output stays in sync with <see cref="DeviceInfo"/>'s
/// property surface automatically — new properties show up the next build
/// without a CLI edit. Null / empty values are omitted; the goal is "verbose
/// for this specific device," not "blank rows for every nullable field." The
/// <see cref="DeviceInfo.Properties"/> bag becomes a group row of key/value
/// children rather than one flattened line so the platform-specific extras
/// stay scannable.
/// </remarks>
internal static class DeviceFieldProjection
{
    // Properties are emitted in metadata order, which matches source
    // declaration order on Roslyn-compiled assemblies — the same
    // grouping (Identity → Hardware IDs → Status → Bus → … → Battery
    // → Platform Identifiers → Classification → Extensibility) that
    // DeviceInfo.cs uses for its section comments.
    private static readonly PropertyInfo[] _props = typeof(DeviceInfo)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance);

    /// <summary>
    /// Project every populated property of <paramref name="device"/> into rows.
    /// Pure: same input → same output, no IO, no console.
    /// </summary>
    public static IReadOnlyList<DeviceField> Project(DeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        var rows = new List<DeviceField>();

        foreach (var prop in _props)
        {
            object? value;
            try { value = prop.GetValue(device); }
            catch { continue; }

            if (IsEmptyOrNull(value)) continue;

            // Property bag — a group row of key/value children so the
            // platform-specific extras stay scannable.
            if (value is IReadOnlyDictionary<string, object?> bag)
            {
                var children = new List<DeviceField>(bag.Count);
                foreach (var (k, v) in bag)
                    children.Add(DeviceField.Leaf(k, FormatValue(v)));
                rows.Add(DeviceField.Group(prop.Name, children));
                continue;
            }

            rows.Add(DeviceField.Leaf(prop.Name, FormatValue(value)));
        }

        return rows;
    }

    private static bool IsEmptyOrNull(object? value) => value switch
    {
        null => true,
        string s => string.IsNullOrEmpty(s),
        // Catches ImmutableHashSet<string>, ImmutableDictionary, regular
        // dictionaries, and most other collection types in one check.
        ICollection c => c.Count == 0,
        IReadOnlyCollection<object?> roc => roc.Count == 0,
        _ => false,
    };

    /// <summary>
    /// Render one property/bag value to its display string. Per-type rules
    /// match the verbose dump's historical formatting exactly (IP arrays and
    /// tag sets as bracketed comma lists, booleans lower-cased, everything
    /// else via <see cref="object.ToString"/>).
    /// </summary>
    public static string FormatValue(object? value) => value switch
    {
        null => "(null)",
        ImmutableHashSet<string> tags =>
            tags.Count == 0 ? "(empty)" : $"[{string.Join(", ", tags)}]",
        ImmutableArray<IPAddress> ips =>
            ips.IsDefaultOrEmpty ? "(empty)" : $"[{string.Join(", ", ips)}]",
        string[] strings =>
            strings.Length == 0 ? "(empty)" : $"[{string.Join(", ", strings)}]",
        // Generic collection fallback — Periphery doesn't have any today
        // beyond the ones above, but a future ImmutableArray<T> would
        // land here without breaking.
        IEnumerable e and not string =>
            $"[{string.Join(", ", e.Cast<object?>().Select(x => x?.ToString() ?? "null"))}]",
        bool b => b ? "true" : "false",
        _ => value.ToString() ?? "(null)",
    };
}

/// <summary>
/// Builds a Spectre <see cref="Tree"/> dump of every populated
/// <see cref="DeviceInfo"/> property — the per-device shape used by
/// <c>periphery devices list --verbose</c>. A thin imperative shell over
/// the pure <see cref="DeviceFieldProjection"/>: it owns only the Spectre
/// markup (colour + <see cref="Markup.Escape(string)"/>), walking the
/// projected rows into tree nodes.
/// </summary>
internal static class DeviceInfoTreeBuilder
{
    /// <summary>
    /// Build a tree for <paramref name="device"/> with the given header
    /// markup at the root.
    /// </summary>
    public static Tree Build(DeviceInfo device, string headerMarkup)
    {
        ArgumentNullException.ThrowIfNull(device);
        var tree = new Tree(headerMarkup);

        foreach (var field in DeviceFieldProjection.Project(device))
        {
            if (field.Value is null)
            {
                // Group row (property bag) — a [bold grey] parent with dim
                // key / white value children.
                var groupNode = tree.AddNode($"[bold grey]{field.Label}[/]");
                foreach (var child in field.Children)
                    groupNode.AddNode(
                        $"[dim]{Markup.Escape(child.Label)}[/]: [white]{Markup.Escape(child.Value ?? string.Empty)}[/]");
                continue;
            }

            tree.AddNode(
                $"[grey]{field.Label}[/]: [white]{Markup.Escape(field.Value)}[/]");
        }

        return tree;
    }
}
