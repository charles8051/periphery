// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;

namespace Periphery.Monitor;

/// <summary>
/// Parsed view of an MCCS capabilities string — the parenthesized grammar a
/// DDC/CI monitor returns for the capabilities request, e.g.
/// <c>(prot(monitor)type(lcd)model(U2723QE)vcp(02 10 12 60(0F 11 12) D6(01 04 05))mccs_ver(2.1))</c>.
/// </summary>
/// <remarks>
/// Pure and best-effort: real-world capability strings are frequently sloppy
/// (unbalanced parentheses, vendor groups, stray whitespace), so
/// <see cref="Parse"/> never throws — it recovers what it can and always
/// preserves <see cref="Raw"/>. Functional-core/imperative-shell per the
/// functional-core convention; the backends own the I/O that fetches the string.
/// </remarks>
public sealed class MccsCapabilities
{
    private readonly ImmutableDictionary<byte, ImmutableArray<ushort>> _vcp;

    private MccsCapabilities(
        string raw,
        string? model,
        string? mccsVersion,
        ImmutableDictionary<byte, ImmutableArray<ushort>> vcp)
    {
        Raw = raw;
        Model = model;
        MccsVersion = mccsVersion;
        _vcp = vcp;
    }

    /// <summary>The capabilities string exactly as the monitor returned it.</summary>
    public string Raw { get; }

    /// <summary>The <c>model(…)</c> group's value, when present.</summary>
    public string? Model { get; }

    /// <summary>The <c>mccs_ver(…)</c> group's value, when present.</summary>
    public string? MccsVersion { get; }

    /// <summary>Every VCP code the monitor declares.</summary>
    public IReadOnlyCollection<byte> SupportedVcpCodes => _vcp.Keys.ToImmutableArray();

    /// <summary>True when the monitor declares <paramref name="vcpCode"/>.</summary>
    public bool Supports(byte vcpCode) => _vcp.ContainsKey(vcpCode);

    /// <summary>
    /// The allowed values a non-continuous feature declares (e.g. the input
    /// sources after <c>60(0F 11 12)</c>), or empty when the feature is
    /// continuous / declared without a value list.
    /// </summary>
    public ImmutableArray<ushort> AllowedValues(byte vcpCode) =>
        _vcp.TryGetValue(vcpCode, out var values) ? values : ImmutableArray<ushort>.Empty;

    /// <summary>Parses a capabilities string. Never throws; see class remarks.</summary>
    public static MccsCapabilities Parse(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        string? model = null;
        string? mccsVersion = null;
        var vcp = ImmutableDictionary.CreateBuilder<byte, ImmutableArray<ushort>>();

        // Walk top-level "name(body)" groups. The grammar is a single outer
        // parenthesis wrapping a sequence of named groups whose bodies may
        // nest one level (vcp value lists).
        ReadOnlySpan<char> s = raw.AsSpan();
        int i = 0;
        if (i < s.Length && s[i] == '(') i++; // Outer wrapper.

        while (i < s.Length)
        {
            // Group name.
            while (i < s.Length && (s[i] == ' ' || s[i] == ')')) i++;
            int nameStart = i;
            while (i < s.Length && s[i] != '(' && s[i] != ')') i++;
            if (i >= s.Length || s[i] != '(') break;
            string name = s[nameStart..i].Trim().ToString().ToLowerInvariant();
            i++; // Consume '('.

            // Group body, tracking one nesting level.
            int bodyStart = i;
            int depth = 1;
            while (i < s.Length && depth > 0)
            {
                if (s[i] == '(') depth++;
                else if (s[i] == ')') depth--;
                if (depth > 0) i++;
            }
            string body = s[bodyStart..Math.Min(i, s.Length)].ToString();
            if (i < s.Length) i++; // Consume closing ')'.

            switch (name)
            {
                case "model":
                    model = body.Trim();
                    break;
                case "mccs_ver":
                    mccsVersion = body.Trim();
                    break;
                case "vcp":
                    ParseVcpBody(body, vcp);
                    break;
                    // Unknown groups (prot, type, cmds, mswhql, vendor ones)
                    // are skipped — the raw string preserves them.
            }
        }

        return new MccsCapabilities(raw, model, mccsVersion, vcp.ToImmutable());
    }

    private static void ParseVcpBody(
        string body, ImmutableDictionary<byte, ImmutableArray<ushort>>.Builder vcp)
    {
        int i = 0;
        while (i < body.Length)
        {
            while (i < body.Length && !IsHexDigit(body[i])) i++;
            int start = i;
            while (i < body.Length && IsHexDigit(body[i])) i++;
            if (start == i) break;

            if (!byte.TryParse(body.AsSpan(start, i - start),
                    NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte code))
                continue; // Token longer than two hex digits — malformed; skip.

            // Optional "(v1 v2 …)" allowed-value list.
            while (i < body.Length && body[i] == ' ') i++;
            var values = ImmutableArray<ushort>.Empty;
            if (i < body.Length && body[i] == '(')
            {
                int close = body.IndexOf(')', i);
                if (close < 0) close = body.Length;
                var list = ImmutableArray.CreateBuilder<ushort>();
                foreach (var token in body[(i + 1)..close].Split(' ',
                             StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (ushort.TryParse(token, NumberStyles.HexNumber,
                            CultureInfo.InvariantCulture, out ushort v))
                        list.Add(v);
                }
                values = list.ToImmutable();
                i = Math.Min(close + 1, body.Length);
            }

            vcp[code] = values;
        }
    }

    private static bool IsHexDigit(char c) =>
        c is (>= '0' and <= '9') or (>= 'A' and <= 'F') or (>= 'a' and <= 'f');
}
