// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.Generic;
using System.Linq;

namespace Periphery;

/// <summary>
/// Why <see cref="PortPath.TryParse(string, out PortPath, out ParseFailure)"/> rejected a
/// string. A rejection is a <i>state</i> — "this is not a port path" — and is never "zero hubs"
/// (ADR-0079 D7).
/// </summary>
/// <remarks>
/// Internal on purpose: ADR-0079 D1 fixes the public surface at <c>TryParse</c> plus the seven
/// queries, and no consumer needs the reason — a caller either has a port path or does not. The
/// reason is diagnostic, and it is what the fixture suite asserts on (distinguishing
/// <see cref="NoUsbRoot"/> from <see cref="SegmentOutOfPlace"/> is the whole of D3), so it is
/// visible to <c>Periphery.Tests</c> through <c>InternalsVisibleTo</c> without widening the
/// shipped API.
/// </remarks>
internal enum ParseFailure
{
    /// <summary>No failure — the parse succeeded.</summary>
    None = 0,

    /// <summary>Null, empty, or whitespace.</summary>
    NullOrEmpty,

    /// <summary>A segment kind outside the five this grammar recognises (ADR-0079 D3).</summary>
    UnknownSegmentKind,

    /// <summary>A Windows segment that is not <c>KIND(ARG)</c> at all — an instance-id fallback lands here.</summary>
    MalformedSegment,

    /// <summary>A segment whose argument should be a port number and is not.</summary>
    NonNumericPort,

    /// <summary>No USB root: a non-USB path, or a syspath with no <c>usbN</c> component.</summary>
    NoUsbRoot,

    /// <summary>A known segment kind in a position the grammar does not allow (ADR-0079 D2/D3).</summary>
    SegmentOutOfPlace,

    /// <summary>
    /// A hop argument that parsed as a number but cannot be a hub port. USB numbers ports from
    /// 1, so zero and negatives are not ports the bus can produce. Distinct from
    /// <see cref="NonNumericPort"/>, which is a syntax failure: this one is well-formed and
    /// still impossible.
    /// </summary>
    PortOutOfRange,
}

/// <summary>Which platform grammar matched. Chosen by the shape of the string, never by the host OS.</summary>
internal enum Grammar
{
    /// <summary>Nothing matched; the value is unparsed.</summary>
    None = 0,

    /// <summary><c>#</c>-delimited <c>PCIROOT(x) PCI(x)* USBROOT(n) USB(n)* USBMI(n)?</c>.</summary>
    Windows,

    /// <summary>A sysfs syspath, one nested directory per hop: <c>…/usb9/9-3/9-3.1</c>.</summary>
    Linux,
}

/// <summary>
/// A USB port path — a device's <em>position</em> on the enumerated bus — parsed out of
/// <see cref="DeviceInfo.LocationPath"/> into a controller and an ordered vector of hub-port hops.
/// </summary>
/// <remarks>
/// <para><b>The parse is the decision, not an implementation detail</b> (ADR-0079 D1).
/// <see cref="TryParse(string, out PortPath)"/> produces a representation and <i>every</i> query
/// reads that representation, never the original string:</para>
/// <code>
/// raw       "PCIROOT(0)#PCI(0801)#PCI(0003)#USBROOT(0)#USB(1)#USB(2)#USBMI(0)"
///            └───────────── controller ──────────────┘└─── hops ───┘└dropped┘
/// parsed    Controller = "PCIROOT(0)#PCI(0801)#PCI(0003)#USBROOT(0)"
///           Hops       = [1, 2]
/// </code>
/// <para>Working from the string instead is wrong twice over, and both failures land on the
/// population that motivated the type. <c>USB(2)</c> is a <i>string</i> prefix of <c>USB(21)</c>,
/// so a <c>StartsWith</c> "downstream" test reports that a device on root port 21 sits below one
/// on root port 2. And a trailing <c>USBMI(n)</c> — a composite device's interface, <i>below</i>
/// the device rather than a hub above it — makes two siblings on one external hub compare
/// unequal. Discarding <c>USBROOT</c> and <c>USBMI</c> at parse time is what stops those traps
/// from having to be caught again at every comparison (ADR-0079 D3/D5).</para>
/// <para><b>A path that does not parse is a state, never a zero.</b> A non-USB device, a macOS
/// synthetic <c>IOService:/…</c> path, or an instance-id fallback from <c>ResolveLocationPath</c>
/// are all "not a port path" — none of them is "zero hubs", and none may be silently readable as
/// one. C# cannot stop a <c>readonly struct</c> being default-constructed, so
/// <c>default(PortPath)</c> <i>is</i> that unparsed state and is indistinguishable at the API
/// from a string that failed to parse. That is what forces the shapes below: the interrogatives
/// gate their payload behind a <c>bool</c>, and the relations return <see cref="Tri"/> rather
/// than a bare <c>bool</c> (ADR-0079 D7).</para>
/// <para><b>Both grammars live here and dispatch is on the shape of the string</b> (ADR-0079 D2)
/// — no <c>OperatingSystem.IsWindows()</c>, no conditional compilation, no
/// <c>[SupportedOSPlatform]</c>. That is what lets the Linux grammar be exercised from a Windows
/// host and vice versa, against string literals alone, and it is why a third grammar would be
/// additive rather than a rewrite. macOS is out of scope permanently: its <c>LocationPath</c> is
/// synthesized as <c>IOService:/{class}/{id}</c> and encodes no port topology, so it fails to
/// parse — which is the honest answer, and is not the same as "zero hubs".</para>
/// <para><b>This says what the path is, never what it means</b> (ADR-0079 D8). The count is
/// documented as <i>external hubs on the enumerated path</i>: on tunneled or redirected buses
/// (USB4/Thunderbolt docks, usbipd, VMBus) the enumeration omits hubs that are physically in the
/// power path, so a count of zero can be wrong rather than absent. Parsing a string does not fix
/// a lossy projection — it inherits it.</para>
/// <para><b>A <see cref="PortPath"/> is a position, not a device identity.</b> Because
/// <c>ResolveLocationPath</c> hands a function node its ancestor's path, a non-composite USB node
/// and its <c>HID\</c> child resolve to the identical path. <see cref="IsSamePortAs"/> answering
/// <see cref="Tri.Yes"/> means "the same physical port", which is a true statement about two
/// distinct devices in that case. No caller should read it as "the same device".</para>
/// </remarks>
public readonly struct PortPath : IEquatable<PortPath>
{
    private readonly string? _controller;
    private readonly int[]? _hops;
    private readonly Grammar _grammar;

    private PortPath(string controller, int[] hops, Grammar grammar)
    {
        _controller = controller;
        _hops = hops;
        _grammar = grammar;
    }

    /// <summary>
    /// True only for a value produced by a successful <see cref="TryParse(string, out PortPath)"/>.
    /// <c>default(PortPath)</c> is false, and is the same state as a string that failed to parse.
    /// </summary>
    internal bool IsParsed => _controller is not null;

    /// <summary>Everything through <c>USBROOT(n)</c> (Windows) or <c>usbN</c> (Linux), compared as a whole.</summary>
    internal string Controller => _controller ?? "";

    /// <summary>The ordered hub-port hops, as integers. <c>USBROOT</c> and <c>USBMI</c> are not here (D3).</summary>
    internal ReadOnlySpan<int> Hops => _hops ?? [];

    /// <summary>Which grammar matched. Two paths from different grammars never compare as related.</summary>
    internal Grammar Grammar => _grammar;

    // ── Parsing ────────────────────────────────────────────────────────

    /// <summary>
    /// The only way to obtain a parsed value (ADR-0079 D1).
    /// </summary>
    /// <param name="locationPath">
    /// A <see cref="DeviceInfo.LocationPath"/>: Periphery's <i>resolved</i> path on Windows
    /// (never the raw <c>DEVPKEY_Device_LocationPaths</c>, which is empty on function nodes), or
    /// the raw syspath on Linux.
    /// </param>
    /// <param name="path">
    /// The parsed value on success; <c>default</c> — the unparsed state — otherwise.
    /// </param>
    /// <returns>
    /// <c>false</c> when <paramref name="locationPath"/> is not a port path (D7). That is not the
    /// same fact as "zero hubs" and must never be read as one. There is deliberately no
    /// <c>Parse</c> overload throwing a <c>FormatException</c>, unlike the neighbouring value
    /// types: failure here is a routine state, not an exceptional one.
    /// </returns>
    public static bool TryParse(string? locationPath, out PortPath path)
        => TryParse(locationPath, out path, out _);

    /// <summary>Parses, and reports <b>why</b> on failure so a non-path can be classified rather than counted.</summary>
    internal static bool TryParse(string? locationPath, out PortPath path, out ParseFailure failure)
    {
        path = default;

        if (string.IsNullOrWhiteSpace(locationPath))
        {
            failure = ParseFailure.NullOrEmpty;
            return false;
        }

        // Shape dispatch (D2): a Windows location path is '#'-delimited KIND(ARG); a Linux
        // syspath is an absolute '/'-delimited filesystem path. Nothing else is a port path,
        // and nothing here asks what OS we are running on.
        if (locationPath.StartsWith('/'))
            return TryParseLinux(locationPath, out path, out failure);

        return TryParseWindows(locationPath, out path, out failure);
    }

    // ── Windows: PCIROOT(x) PCI(x)* USBROOT(n) USB(n)* USBMI(n)? ───────

    private static readonly string[] WindowsKinds = ["PCIROOT", "PCI", "USBROOT", "USB", "USBMI"];

    private static bool TryParseWindows(string s, out PortPath path, out ParseFailure failure)
    {
        path = default;

        string[] segments = s.Split('#');
        var kinds = new string[segments.Length];
        var args = new string[segments.Length];

        for (int i = 0; i < segments.Length; i++)
        {
            string seg = segments[i];
            int open = seg.IndexOf('(');
            if (open <= 0 || !seg.EndsWith(')'))
            {
                // Not KIND(ARG) at all — an instance-id fallback from ResolveLocationPath lands here.
                failure = ParseFailure.MalformedSegment;
                return false;
            }

            string kind = seg[..open];
            if (Array.IndexOf(WindowsKinds, kind) < 0)
            {
                // D3: an unrecognised kind fails the WHOLE parse. Skipping it would assert
                // "unknown implies not-a-hub-hop", which is the bet USBMI already won against —
                // once, on a real device class, found only by measurement. Strict costs a
                // missing answer, legible as such under D7; permissive costs a silently wrong
                // count, with no signal that it went wrong.
                failure = ParseFailure.UnknownSegmentKind;
                return false;
            }

            kinds[i] = kind;
            args[i] = seg[(open + 1)..^1];
        }

        // D2/D3: the grammar is an ORDER, not just a set of allowed kinds. Validating kinds
        // globally but ordering only after USBROOT would accept PCIROOT(0)#USB(2)#USBROOT(0)#USB(1)
        // and silently fold the stray USB(2) into the controller, so two malformed paths could
        // compare as sharing a controller instead of being rejected.
        int i2 = 0;
        if (kinds[i2] != "PCIROOT")
        {
            failure = ParseFailure.SegmentOutOfPlace;
            return false;
        }
        i2++;
        while (i2 < kinds.Length && kinds[i2] == "PCI") i2++;

        if (i2 >= kinds.Length || kinds[i2] != "USBROOT")
        {
            // Either a non-USB path (a NIC's PCIROOT(0)#PCI(0200)) or a misplaced root marker.
            failure = i2 >= kinds.Length || !kinds.Contains("USBROOT")
                ? ParseFailure.NoUsbRoot
                : ParseFailure.SegmentOutOfPlace;
            return false;
        }
        int rootIndex = i2;
        i2++;

        var hops = new List<int>();
        while (i2 < kinds.Length && kinds[i2] == "USB")
        {
            if (!int.TryParse(args[i2], out int port))
            {
                failure = ParseFailure.NonNumericPort;
                return false;
            }

            // Hub ports are numbered from 1, so USB(0) and USB(-1) are well-formed strings
            // describing a position the bus cannot produce. Accepting them would let a
            // malformed path answer root-port and downstream comparisons as if it were real
            // — a confident wrong answer, which is what D7 exists to prevent.
            if (port <= 0)
            {
                failure = ParseFailure.PortOutOfRange;
                return false;
            }

            hops.Add(port);
            i2++;
        }

        // D3: USBMI is a composite device's interface, BELOW the device, not a hub above it.
        // At most one, and only as the final segment — a nested USBMI is unmeasured, so it fails
        // rather than guessing. Either way the segment is discarded, never counted.
        if (i2 < kinds.Length && kinds[i2] == "USBMI")
        {
            if (!int.TryParse(args[i2], out _))
            {
                failure = ParseFailure.NonNumericPort;
                return false;
            }
            i2++;
        }

        if (i2 != kinds.Length)
        {
            failure = ParseFailure.SegmentOutOfPlace;
            return false;
        }

        path = new PortPath(string.Join('#', segments[..(rootIndex + 1)]), [.. hops], Grammar.Windows);
        failure = ParseFailure.None;
        return true;
    }

    // ── Linux: …/usbN/N-p/N-p.q/… — one directory per hop (ADR-0079 D2) ─

    private static bool TryParseLinux(string s, out PortPath path, out ParseFailure failure)
    {
        path = default;

        string[] parts = s.Split('/', StringSplitOptions.RemoveEmptyEntries);

        // Locate the root hub: a component spelled usbN.
        int rootIndex = -1;
        int bus = 0;
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length > 3
                && parts[i].StartsWith("usb", StringComparison.Ordinal)
                && int.TryParse(parts[i].AsSpan(3), out bus))
            {
                rootIndex = i;
                break;
            }
        }
        if (rootIndex < 0)
        {
            // LinuxDeviceProvider assigns LocationPath = syspath raw — there is no
            // ResolveLocationPath equivalent normalising the input — so this grammar has to
            // locate the USB chain inside an arbitrary syspath and reject non-USB ones:
            // /sys/devices/virtual/…, a DRM card. Failing those is D7 doing its job, not a gap.
            failure = ParseFailure.NoUsbRoot;
            return false;
        }

        // Walk the port chain: each hop is its own directory, "bus-p[.q[.r]]".
        // The deepest such component carries the whole chain, so the last one wins.
        string prefix = bus.ToString() + "-";
        int[] hops = [];
        for (int i = rootIndex + 1; i < parts.Length; i++)
        {
            string part = parts[i];
            if (!part.StartsWith(prefix, StringComparison.Ordinal))
                break;

            string chain = part[prefix.Length..];

            // An interface node is "bus-chain:config.iface" — a CHILD of the device, the Linux
            // analogue of Windows' USBMI. It ends the port chain rather than extending it.
            //
            // Stopping on the mere presence of a colon would end the walk without ever asking
            // whether the component is a real interface node, so "9-x:1.0" would succeed as a
            // zero-hop ROOT HUB. Being a child, its chain must be the chain we have already
            // walked — or "0" at a root hub, whose own interface is "9-0:1.0" — and its suffix
            // must be "config.iface". Anything else carries the bus prefix while describing no
            // position the bus can produce, so it is rejected rather than guessed at.
            int colon = chain.IndexOf(':');
            if (colon >= 0)
            {
                if (!IsInterfaceNode(chain[..colon], chain[(colon + 1)..], hops))
                {
                    failure = ParseFailure.SegmentOutOfPlace;
                    return false;
                }
                break;
            }

            // A component that carries the bus prefix and is NOT an interface node claims to be
            // a hop, so a malformed one is a malformed path — not a boundary to stop at.
            // Breaking here instead would return the hops gathered so far as a success:
            // .../usb9/9-3/9-x would parse as [3], and .../usb9/9- as a zero-hop ROOT HUB.
            // Both are confident wrong answers about position, which is what D3's
            // reject-rather-than-guess posture exists to prevent.
            string[] components = chain.Split('.');
            var parsed = new int[components.Length];
            for (int c = 0; c < components.Length; c++)
            {
                if (!int.TryParse(components[c], out parsed[c]))
                {
                    failure = ParseFailure.SegmentOutOfPlace;
                    return false;
                }

                // Same domain rule as the Windows grammar: sysfs names device directories
                // with 1-based port numbers, so "9-0" and "9--1" are impossible positions
                // rather than unusual ones. Port 0 belongs to the root hub's own interface
                // node ("9-0:1.0"), which the interface branch below handles and which never
                // reaches here.
                if (parsed[c] <= 0)
                {
                    failure = ParseFailure.PortOutOfRange;
                    return false;
                }
            }

            // sysfs nests one directory per hop, so each must extend the previous chain by
            // exactly one port. Overwriting instead would accept sibling or backtracking
            // paths — .../usb9/9-3/9-4 would yield [4], and .../9-3/9-3.1/9-3 would yield
            // [3] — and those bogus vectors would then answer root-port, external-hub and
            // downstream comparisons as if they described a real device.
            if (parsed.Length != hops.Length + 1
                || !parsed.AsSpan(0, hops.Length).SequenceEqual(hops))
            {
                failure = ParseFailure.SegmentOutOfPlace;
                return false;
            }

            hops = parsed;
        }

        // Controller: everything through usbN, so two devices on one bus share a controller.
        path = new PortPath("/" + string.Join('/', parts[..(rootIndex + 1)]), hops, Grammar.Linux);
        failure = ParseFailure.None;
        return true;
    }

    /// <summary>
    /// Whether "<paramref name="chain"/>:<paramref name="suffix"/>" is the interface node of the
    /// device at <paramref name="hops"/> — the one shape that legitimately ends a Linux port walk.
    /// </summary>
    private static bool IsInterfaceNode(string chain, string suffix, int[] hops)
    {
        // Suffix is "config.iface", and the two halves have DIFFERENT domains — reading them
        // as one range is how "9-3:0.0" got in. Both are single-byte descriptor fields, so both
        // are bounded above as well: bConfigurationValue is 1-based, and 0 is the reserved
        // "unconfigured" address rather than a configuration a node can be named for;
        // bInterfaceNumber is genuinely 0-based — so the root hub's own "9-0:1.0" is well-formed
        // — and tops out at 254, since bNumInterfaces is itself one byte and the numbering
        // starts at zero. "9-3:256.0" names no descriptor any device can carry.
        int dot = suffix.IndexOf('.');
        if (dot < 0
            || !int.TryParse(suffix[..dot], out int config) || config is < 1 or > 255
            || !int.TryParse(suffix[(dot + 1)..], out int iface) || iface is < 0 or > 254)
            return false;

        // A root hub's own interface is "0:1.0" and is the only place port 0 is spelled.
        if (hops.Length == 0)
            return chain == "0";

        // Otherwise the interface belongs to the device we just walked to, so its chain is
        // exactly the hop vector we hold.
        string[] components = chain.Split('.');
        if (components.Length != hops.Length)
            return false;
        for (int c = 0; c < components.Length; c++)
        {
            if (!int.TryParse(components[c], out int port) || port != hops[c])
                return false;
        }
        return true;
    }

    // ── Interrogative: gated, because the payload could be misread (D7) ─

    /// <summary>
    /// External hubs between this device and the root hub, on the <i>enumerated</i> path
    /// (ADR-0079 D4/D8).
    /// </summary>
    /// <param name="count">
    /// Hop count minus one, or <c>0</c> at a root hub. Both zeroes are correct answers: the root
    /// hub itself genuinely has no external hub above it, and neither does a directly-attached
    /// device. Use <see cref="TryGetIsRootHub"/> when a caller needs those two cases separated.
    /// </param>
    /// <returns>
    /// <c>false</c> for exactly one reason — this is not a parsed port path — which is what makes
    /// D7's rule exact. The payload is unreachable without passing this gate, so a non-path can
    /// never be read as zero hubs.
    /// </returns>
    public bool TryGetExternalHubCount(out int count)
    {
        count = 0;
        if (!IsParsed) return false;
        count = _hops!.Length == 0 ? 0 : _hops.Length - 1;
        return true;
    }

    /// <summary>
    /// Whether this path is a root hub — a parsed path with zero hops.
    /// </summary>
    /// <param name="isRootHub">True when the hop vector is empty.</param>
    /// <returns>
    /// <c>false</c> only when this is not a parsed port path. A bare <c>bool IsRootHub</c>
    /// property would fail on its own terms here: <c>false</c> and "this is not a port path"
    /// would be the same bool and opposite facts (ADR-0079 D7).
    /// </returns>
    public bool TryGetIsRootHub(out bool isRootHub)
    {
        isRootHub = false;
        if (!IsParsed) return false;
        isRootHub = _hops!.Length == 0;
        return true;
    }

    // ── Relational: three-valued, so no answer reads as an established negative (D7) ──
    //
    // Controller, root port and external hub are THREE different comparisons, and conflating any
    // two of them is a measured error rather than a hypothetical one (D5). The five also do NOT
    // form a ladder: each carries a depth precondition, and the implications only hold among
    // pairs at two hops or more — which is why these are five predicates and not one ranked enum.
    //
    // Written out rather than routed through a shared lambda helper: a struct cannot capture
    // `this` in a lambda, and spelling each one out keeps the D5 table readable against the code.

    // Ordinal, not OrdinalIgnoreCase. Two reasons, and the first is the ADR's: D5 records
    // that `DeviceWaitState.Correlates` compares OrdinalIgnoreCase while a parsed comparison
    // is ordinal, which is precisely why re-expressing ADR-0063 would be a small behaviour
    // change rather than a pure refactor — case-folding here would erase the difference the
    // ADR documents. The second is that a Linux controller is a filesystem path and sysfs is
    // case-sensitive: /sys/devices/platform/soc/usb1 and .../SOC/usb1 are different nodes.
    private bool SameController(in PortPath o)
        => _grammar == o._grammar
        && string.Equals(_controller, o._controller, StringComparison.Ordinal);

    /// <summary>Both operands must be parsed, or the answer is <see cref="Tri.Unknown"/> — never <c>No</c>.</summary>
    private bool Comparable(in PortPath other) => IsParsed && other.IsParsed;

    private static Tri From(bool yes) => yes ? Tri.Yes : Tri.No;

    /// <summary>
    /// Whether both devices hang off the same USB controller — equivalently, the same root hub.
    /// </summary>
    /// <remarks>
    /// This is <i>not</i> <see cref="SharesRootPortWith"/>, and the machine ADR-0079 was measured
    /// on carries the counterexample: <c>…#USBROOT(0)#USB(2)</c> and <c>…#USBROOT(0)#USB(8)</c>
    /// are identical through <c>USBROOT(0)</c> and plugged into different root ports (D5).
    /// </remarks>
    /// <returns><see cref="Tri.Unknown"/> when either operand is unparsed.</returns>
    public Tri SharesControllerWith(in PortPath other)
        => !Comparable(other) ? Tri.Unknown : From(SameController(other));

    /// <summary>
    /// Whether both paths denote the same physical port: same controller <b>and</b> an
    /// element-wise equal hop vector.
    /// </summary>
    /// <remarks>
    /// A position, not an identity — two distinct devices legitimately answer
    /// <see cref="Tri.Yes"/> when one is the other's function node (D5). This is the parsed
    /// re-expression of ADR-0063's <c>ByLocationPath</c> whole-string equality; whether that call
    /// site adopts it is deliberately left undecided by ADR-0079. It does <b>not</b> imply
    /// <see cref="SharesExternalHubWith"/> at one hop, nor <see cref="SharesRootPortWith"/> at
    /// zero hops.
    /// </remarks>
    /// <returns><see cref="Tri.Unknown"/> when either operand is unparsed — two unparsed values do not compare equal.</returns>
    public Tri IsSamePortAs(in PortPath other)
        => !Comparable(other) ? Tri.Unknown
         : From(SameController(other) && _hops!.AsSpan().SequenceEqual(other._hops!));

    /// <summary>
    /// Whether both devices sit under the same <b>root-hub port</b>: same controller and equal
    /// first hop.
    /// </summary>
    /// <remarks>
    /// Requires at least one hop on both sides, so two root hubs on one controller answer
    /// <see cref="Tri.No"/> — there is no first hop for them to agree on. Defining this as
    /// "prefix through <c>USBROOT(n)</c>" instead would fuse two boards plugged into different
    /// root ports, which is precisely the distinction <c>Efm8HidProgrammer</c> exists to make
    /// when a current collision hits one of them (D5).
    /// </remarks>
    /// <returns><see cref="Tri.Unknown"/> when either operand is unparsed.</returns>
    public Tri SharesRootPortWith(in PortPath other)
        => !Comparable(other) ? Tri.Unknown
         : From(SameController(other)
                && _hops!.Length >= 1 && other._hops!.Length >= 1
                && _hops[0] == other._hops[0]);

    /// <summary>
    /// Whether both devices hang off the same immediate <b>external</b> hub: same controller,
    /// equal hop count of at least two, and equal on all but the last hop.
    /// </summary>
    /// <remarks>
    /// <para><b>The two-hop floor is the point, not an edge case</b> (ADR-0079 D5). Read naively
    /// — "equal on all but the last hop" — two devices on different root ports of one controller
    /// answer yes, because both hang off the root hub. That is true, useless and misleading in
    /// the same breath: redundant, because <see cref="SharesControllerWith"/> already says
    /// everything true about that pair; and wrong for the question actually being asked, which is
    /// whether two boards contend for a shared resource — a hub's upstream port and its power
    /// budget. The root hub is not that resource; an external hub is. The floor also keeps this
    /// consistent with <see cref="TryGetExternalHubCount"/>, which would otherwise report zero
    /// for a pair this predicate called hub-sharing.</para>
    /// <para>Because <c>USBMI</c> was discarded at parse time, the comparison works on composite
    /// devices where string surgery does not: <c>…#USB(1)#USB(2)#USBMI(0)</c> against
    /// <c>…#USB(1)#USB(3)</c> answers yes over <c>[1,2]</c> vs <c>[1,3]</c>, where "equal but for
    /// the last <c>USB(n)</c>" performed on the raw string answers no (D3/D5).</para>
    /// </remarks>
    /// <returns><see cref="Tri.Unknown"/> when either operand is unparsed.</returns>
    public Tri SharesExternalHubWith(in PortPath other)
        => !Comparable(other) ? Tri.Unknown
         : From(SameController(other)
                && _hops!.Length >= 2
                && _hops.Length == other._hops!.Length
                && _hops.AsSpan(0, _hops.Length - 1)
                        .SequenceEqual(other._hops.AsSpan(0, other._hops.Length - 1)));

    /// <summary>
    /// Whether this device sits below <paramref name="other"/>: same controller, and
    /// <paramref name="other"/>'s hop vector is a <b>proper prefix</b> of this one's,
    /// element-wise.
    /// </summary>
    /// <remarks>
    /// Element-wise is load-bearing. A <c>StartsWith</c> over the raw string reports that a
    /// device on root port 21 sits below one on root port 2, because <c>USB(2)</c> is a string
    /// prefix of <c>USB(21)</c> — and root hubs routinely expose more than nine ports (D5).
    /// Disjoint from <see cref="SharesExternalHubWith"/> unconditionally, since a proper prefix
    /// cannot have the same length as the vector it prefixes; and it does not imply
    /// <see cref="SharesRootPortWith"/> when <paramref name="other"/> is the root hub.
    /// </remarks>
    /// <returns><see cref="Tri.Unknown"/> when either operand is unparsed.</returns>
    public Tri IsDownstreamOf(in PortPath other)
        => !Comparable(other) ? Tri.Unknown
         : From(SameController(other)
                && other._hops!.Length < _hops!.Length
                && _hops.AsSpan(0, other._hops.Length).SequenceEqual(other._hops));

    // ── Value identity / formatting ────────────────────────────────────

    /// <summary>
    /// Structural equality over the parsed representation — grammar, controller and hop vector.
    /// </summary>
    /// <remarks>
    /// <para>Overridden by hand because the inherited <see cref="ValueType"/> compares the hop
    /// array <i>by reference</i>, which would make two identically-parsed paths unequal.</para>
    /// <para><b>This is value identity, and it is a weaker question than
    /// <see cref="IsSamePortAs"/>.</b> It is two-valued, so it cannot say "cannot see": two
    /// unparsed values are equal <i>as values</i> where <see cref="IsSamePortAs"/> correctly
    /// answers <see cref="Tri.Unknown"/>. Ask the topological question with
    /// <see cref="IsSamePortAs"/>. There is deliberately no <c>==</c> operator: that is where
    /// the bare-<c>bool</c> confusion D7 rejects would be most tempting to write.</para>
    /// <para><b>Do not use an unparsed value as a dictionary key.</b> Every non-port-path is
    /// <c>default</c> and they are all equal to one another, so a <c>ToDictionary</c> or
    /// <c>GroupBy</c> that ignores <see cref="TryParse"/>'s result folds them into a single
    /// entry reading as "these devices are at the same port" — on the machine ADR-0079 was
    /// measured on, 204 of 300 devices. Equality is deliberately <i>not</i> the guard against
    /// that: an earlier revision made an unparsed value equal to nothing, which broke
    /// reflexivity and with it the <see cref="IEquatable{T}"/> contract, so a value could be
    /// added to a <see cref="HashSet{T}"/> and then not found in it. Trading a language
    /// invariant for a hazard the ADR already concedes is unpreventable was the wrong swap:
    /// D7 states plainly that no shape stops a caller who flattens on purpose, and that what
    /// the design buys is only that the flattening is written down at the call site.</para>
    /// </remarks>
    public bool Equals(PortPath other)
        => _grammar == other._grammar
        && string.Equals(_controller, other._controller, StringComparison.Ordinal)
        && Hops.SequenceEqual(other.Hops);

    /// <inheritdoc cref="Equals(PortPath)"/>
    public override bool Equals(object? obj) => obj is PortPath other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        // Unparsed values all hash alike and are all equal, so they occupy one entry. That is
        // the documented degenerate case above, not an accident.
        if (!IsParsed) return 0;

        var hash = new HashCode();
        hash.Add((int)_grammar);
        hash.Add(Controller, StringComparer.Ordinal);
        foreach (int hop in Hops)
            hash.Add(hop);
        return hash.ToHashCode();
    }

    /// <summary>Diagnostic rendering; <c>&lt;unparsed&gt;</c> for the unparsed state.</summary>
    public override string ToString()
        => IsParsed ? $"{_grammar}:{_controller} hops=[{string.Join(',', _hops!)}]" : "<unparsed>";
}
