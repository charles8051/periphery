namespace Periphery.Tests;

/// <summary>
/// Pins the ADR-0079 contract on the shipping <see cref="PortPath"/>. Hardware-free,
/// platform-free, deterministic.
/// </summary>
/// <remarks>
/// <para>
/// The exploration probe measured one machine; this pins the decision. Without it a future
/// implementation could return the wrong <see cref="Tri"/> for any of the five relations —
/// including the shallow cases D5 turns on — and the probe would still exit 0, because agreement
/// on hub counts says nothing about relation semantics.
/// </para>
/// <para>
/// These cases are the ones the ADR argues from, and most of the paths are real rows lifted out
/// of the probe's own CSVs. Both grammars are exercised in every CI leg, which is why this class
/// carries no <c>Category</c> trait: the Linux grammar has to be tested from Windows.
/// </para>
/// </remarks>
public class PortPathTests
{
    // Real paths measured on a Windows workstation (see docs/explorations/portpath-parse-vs-devnode-walk-2026-08.md).
    private const string CtlA = "PCIROOT(20)#PCI(0301)#PCI(0000)#USBROOT(0)";
    private const string CtlB = "PCIROOT(0)#PCI(0801)#PCI(0003)#USBROOT(0)";

    // The sysfs controller prefix, measured on the Linux device rig against the QEMU fixture.
    private const string Sys = "/sys/devices/pci0000:00/0000:00:01.0";

    // ── D2/D3: what parses, and what must not ──────────────────────────

    [Theory]
    [InlineData(CtlA, new int[0])]                                        // root hub
    [InlineData(CtlA + "#USB(6)", new[] { 6 })]                           // directly attached
    [InlineData(CtlA + "#USB(6)#USB(4)", new[] { 6, 4 })]
    [InlineData(CtlA + "#USB(6)#USB(4)#USB(2)", new[] { 6, 4, 2 })]
    [InlineData(CtlA + "#USB(6)#USB(2)#USB(4)#USB(4)", new[] { 6, 2, 4, 4 })]
    public void TryParse_WindowsGrammar_YieldsOneHopPerUsbSegment(string locationPath, int[] expectedHops)
    {
        Assert.True(PortPath.TryParse(locationPath, out var path));
        Assert.Equal(expectedHops, path.Hops.ToArray());
    }

    [Theory]
    [InlineData(CtlA + "#USB(6)#USB(4)#USB(2)#USBMI(2)", new[] { 6, 4, 2 })]
    [InlineData(CtlB + "#USB(1)#USB(2)#USBMI(0)", new[] { 1, 2 })]
    public void TryParse_WindowsUsbmiTail_IsDiscardedNotCountedAsAHop(string locationPath, int[] expectedHops)
    {
        // D3: USBMI is a composite device's interface, below the device rather than a hub above
        // it — the trap that motivated the whole decision.
        Assert.True(PortPath.TryParse(locationPath, out var path));
        Assert.Equal(expectedHops, path.Hops.ToArray());
    }

    [Fact]
    public void TryParse_MultiDigitWindowsPort_SurvivesAsASingleHop()
    {
        // Multi-digit ports must survive; a string-prefix parser conflates USB(2) and USB(21).
        Assert.True(PortPath.TryParse(CtlA + "#USB(21)", out var path));
        Assert.Equal(new[] { 21 }, path.Hops.ToArray());
    }

    [Theory]
    [InlineData(Sys + "/usb9", new int[0])]                               // root hub
    [InlineData(Sys + "/usb9/9-1", new[] { 1 })]
    [InlineData(Sys + "/usb9/9-3", new[] { 3 })]
    [InlineData(Sys + "/usb9/9-3/9-3.1", new[] { 3, 1 })]
    [InlineData(Sys + "/usb9/9-3/9-3.1/9-3.1.1", new[] { 3, 1, 1 })]
    public void TryParse_LinuxGrammar_YieldsOneHopPerNestedDirectory(string locationPath, int[] expectedHops)
    {
        // ADR-0079 D2, measured on the Linux device rig against the QEMU fixture: one directory
        // per hop. The numbers here are exactly main's cross-validation table.
        Assert.True(PortPath.TryParse(locationPath, out var path));
        Assert.Equal(expectedHops, path.Hops.ToArray());
    }

    [Fact]
    public void TryParse_LinuxInterfaceNode_EndsTheChainTheWayUsbmiDoes()
    {
        // An interface node is a CHILD of the device — Linux's USBMI. It ends the chain.
        Assert.True(PortPath.TryParse(Sys + "/usb9/9-3/9-3.1/9-3.1.1/9-3.1.1:1.0", out var path));
        Assert.Equal(new[] { 3, 1, 1 }, path.Hops.ToArray());
    }

    [Fact]
    public void TryParse_LinuxDescendantsBelowTheInterface_AreNotHopsEither()
    {
        // Driver-specific descendants below the interface are not hops either.
        Assert.True(PortPath.TryParse(
            "/sys/devices/pci0000:00/0000:00:14.0/usb1/1-2/1-2:1.0/0003:046D:C077.0001/input/input20",
            out var path));
        Assert.Equal(new[] { 2 }, path.Hops.ToArray());
    }

    // ── D7: rejection is a state, and it is never "zero hubs" ──────────

    // None of the paths below is "zero hubs". Each must fail to parse, and the reason is part of
    // what is pinned: distinguishing NoUsbRoot from SegmentOutOfPlace is the whole of D3.

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void TryParse_NullOrEmpty_IsRejectedAsNullOrEmpty(string? locationPath)
        => AssertRejected(locationPath, ParseFailure.NullOrEmpty);

    [Theory]
    [InlineData(@"HID\VID_046D&PID_C092&MI_00\A&2C3FA571&0&0000")]
    [InlineData("IOService:/IOUSBHostDevice/0x100000abc")]
    public void TryParse_InstanceIdOrMacOsSyntheticPath_IsRejectedAsMalformed(string locationPath)
    {
        // Neither is KIND(ARG) at all: an instance-id fallback from ResolveLocationPath, and a
        // macOS IOService path, which encodes no port topology and never will.
        AssertRejected(locationPath, ParseFailure.MalformedSegment);
    }

    [Theory]
    [InlineData("PCIROOT(0)#PCI(0200)")]                                  // a NIC
    [InlineData("/sys/devices/virtual/misc/uhid")]
    [InlineData("/sys/devices/pci0000:00/0000:00:02.0/drm/card0")]
    public void TryParse_NonUsbDevice_IsRejectedForHavingNoUsbRoot(string locationPath)
        => AssertRejected(locationPath, ParseFailure.NoUsbRoot);

    [Fact]
    public void TryParse_UnrecognisedSegmentKind_FailsTheWholeParse()
    {
        // D3 strictness: an unknown kind is never skipped-and-continued.
        AssertRejected("ACPI(_SB_)#ACPI(AERR)", ParseFailure.UnknownSegmentKind);
    }

    [Theory]
    [InlineData(Sys + "/usb9/9-3/9-4")]
    [InlineData(Sys + "/usb9/9-3/9-3.1/9-3")]
    [InlineData(Sys + "/usb9/9-3.1")]
    // A component carrying the bus prefix claims to be a hop, so a malformed one is a
    // malformed path, not a boundary to stop at. Truncating the walk instead returned the
    // hops gathered so far as a SUCCESS: "9-3/9-x" as [3], and a bare "9-" as a zero-hop
    // ROOT HUB. Both are confident wrong answers about position.
    [InlineData(Sys + "/usb9/9-3/9-x")]
    [InlineData(Sys + "/usb9/9-3/9-3.x")]
    [InlineData(Sys + "/usb9/9-")]
    [InlineData(Sys + "/usb9/9-3/9-3.")]
    public void TryParse_LinuxSiblingOrBacktrackingChain_IsRejected(string locationPath)
    {
        // Linux nests one directory per hop, so a sibling or a backtrack is malformed: each
        // component must extend the previous chain by exactly one port.
        AssertRejected(locationPath, ParseFailure.SegmentOutOfPlace);
    }

    [Theory]
    // Hub ports are numbered from 1, so these parse as numbers and still describe a position
    // the bus cannot produce. Accepting them let a malformed path answer root-port, external-hub
    // and downstream comparisons as if it were a real device.
    [InlineData("PCIROOT(0)#USBROOT(0)#USB(0)")]
    [InlineData("PCIROOT(0)#USBROOT(0)#USB(-1)")]
    [InlineData("PCIROOT(0)#USBROOT(0)#USB(3)#USB(0)")]
    [InlineData(Sys + "/usb9/9-0")]
    [InlineData(Sys + "/usb9/9--1")]
    [InlineData(Sys + "/usb9/9-3/9-3.0")]
    public void TryParse_NonPositivePort_IsRejected(string locationPath)
    {
        // Well-formed syntax is not yet a port: the domain rule is what makes it one.
        AssertRejected(locationPath, ParseFailure.PortOutOfRange);
    }

    [Theory]
    // A colon alone did not make a component an interface node. Stopping on its mere presence
    // ended the walk without asking whether the component described a real position, so
    // "9-x:1.0" succeeded as a zero-hop ROOT HUB.
    [InlineData(Sys + "/usb9/9-x:1.0")]
    [InlineData(Sys + "/usb9/9-3:bogus")]
    [InlineData(Sys + "/usb9/9-3/9-3:1")]
    [InlineData(Sys + "/usb9/9-3/9-4:1.0")]      // interface of a device we did not walk to
    [InlineData(Sys + "/usb9/9-3/9-3.1:1.0")]    // one hop too deep for the chain we hold
    // config and iface have different domains: bConfigurationValue is 1-based and 0 is the
    // reserved "unconfigured" address, while bInterfaceNumber is 0-based. Reading both as one
    // non-negative range accepted "9-3:0.0" as a real interface node.
    [InlineData(Sys + "/usb9/9-3/9-3:0.0")]
    [InlineData(Sys + "/usb9/9-3/9-3:-1.0")]
    [InlineData(Sys + "/usb9/9-3/9-3:1.-1")]
    // Both halves are single-byte descriptor fields, so both are bounded above too.
    [InlineData(Sys + "/usb9/9-3/9-3:256.0")]
    [InlineData(Sys + "/usb9/9-3/9-3:1.255")]
    public void TryParse_MalformedLinuxInterfaceNode_IsRejected(string locationPath)
    {
        AssertRejected(locationPath, ParseFailure.SegmentOutOfPlace);
    }

    [Theory]
    // The shapes that ARE interface nodes still end the walk and keep the chain they belong to.
    [InlineData(Sys + "/usb9/9-0:1.0", 0)]           // the root hub's own interface
    [InlineData(Sys + "/usb9/9-3/9-3:1.0", 1)]
    [InlineData(Sys + "/usb9/9-3/9-3.1/9-3.1:1.0", 2)]
    [InlineData(Sys + "/usb9/9-3/9-3:255.254", 1)]   // the edges of both fields are still real
    public void TryParse_RealLinuxInterfaceNode_EndsTheWalk(string locationPath, int expectedHops)
    {
        Assert.True(PortPath.TryParse(locationPath, out var path));
        Assert.True(path.TryGetExternalHubCount(out _));
        Assert.True(path.TryGetIsRootHub(out bool isRootHub));
        Assert.Equal(expectedHops == 0, isRootHub);
    }

    [Theory]
    [InlineData("PCIROOT(0)#USB(2)#USBROOT(0)#USB(1)")]
    [InlineData("PCIROOT(0)#USBMI(1)#USBROOT(0)")]
    [InlineData("USB(9)#USBROOT(0)")]
    [InlineData(CtlA + "#USBMI(0)#USB(2)")]     // USBMI must be last
    [InlineData(CtlA + "#USB(2)#USBROOT(0)")]   // duplicate root marker
    public void TryParse_KnownSegmentKindInTheWrongPosition_IsRejected(string locationPath)
    {
        // D3, generalized: ordering is part of the grammar, not just the set of kinds.
        AssertRejected(locationPath, ParseFailure.SegmentOutOfPlace);
    }

    // ── D4: the count ──────────────────────────────────────────────────

    [Theory]
    [InlineData(CtlA, 0)]                               // root hub: 0 external hubs
    [InlineData(CtlA + "#USB(6)", 0)]                   // directly attached: also 0
    [InlineData(CtlA + "#USB(6)#USB(4)", 1)]
    [InlineData(CtlA + "#USB(6)#USB(4)#USB(2)", 2)]
    [InlineData(CtlA + "#USB(6)#USB(2)#USB(4)#USB(4)", 3)]
    [InlineData(Sys + "/usb9/9-3/9-3.1/9-3.1.1", 2)]
    public void TryGetExternalHubCount_IsHopsMinusOneAndZeroAtARootHub(string locationPath, int expected)
    {
        var path = Parse(locationPath);

        Assert.True(path.TryGetExternalHubCount(out int actual));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void TryGetExternalHubCount_UsbmiTail_DoesNotInflateTheCount()
    {
        var path = Parse(CtlA + "#USB(6)#USB(4)#USB(2)#USBMI(2)");

        Assert.True(path.TryGetExternalHubCount(out int actual));
        Assert.Equal(2, actual);
    }

    [Theory]
    [InlineData(CtlA, true)]
    [InlineData(CtlA + "#USB(6)", false)]
    public void TryGetIsRootHub_SeparatesTheTwoZeroHubCounts(string locationPath, bool expected)
    {
        var path = Parse(locationPath);

        Assert.True(path.TryGetIsRootHub(out bool actual));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void TryGetExternalHubCount_NonPortPath_IsUnreachableWithoutPassingTheGate()
    {
        // D7: a NIC has no port path, and must not be readable as zero hubs.
        Assert.False(Parse("PCIROOT(0)#PCI(0200)").TryGetExternalHubCount(out _));
    }

    [Fact]
    public void TryGetIsRootHub_NonPortPath_IsUnreachableWithoutPassingTheGate()
    {
        Assert.False(Parse("PCIROOT(0)#PCI(0200)").TryGetIsRootHub(out _));
    }

    // ── D5: the five relations ─────────────────────────────────────────

    [Fact]
    public void Relations_SameControllerDifferentRootPorts_ShareTheControllerAndNothingElse()
    {
        // The measured counterexample: same controller, DIFFERENT root ports.
        AssertRelations(CtlA + "#USB(2)", CtlA + "#USB(8)",
            controller: Tri.Yes, rootPort: Tri.No, extHub: Tri.No, downstream: Tri.No, samePort: Tri.No);
    }

    [Fact]
    public void Relations_SiblingsOnOneExternalHub_ShareItDespiteAUsbmiTail()
    {
        // Same external hub, with the USBMI tail that a string-prefix comparison gets wrong.
        AssertRelations(CtlA + "#USB(6)#USB(4)#USB(2)#USBMI(2)", CtlA + "#USB(6)#USB(4)#USB(3)",
            controller: Tri.Yes, rootPort: Tri.Yes, extHub: Tri.Yes, downstream: Tri.No, samePort: Tri.No);
    }

    [Fact]
    public void Relations_Efm8PairAtDifferentDepths_SharesTheRootPortButNoExternalHub()
    {
        // The Efm8HidProgrammer pair: same VID/PID, same root port, different depths.
        AssertRelations(CtlA + "#USB(6)#USB(2)#USB(3)", CtlA + "#USB(6)#USB(3)",
            controller: Tri.Yes, rootPort: Tri.Yes, extHub: Tri.No, downstream: Tri.No, samePort: Tri.No);
    }

    [Fact]
    public void Relations_ProperHopPrefix_IsDownstreamAndNotHubSharing()
    {
        // Downstream: proper prefix, element-wise.
        AssertRelations(CtlA + "#USB(6)#USB(2)#USB(4)", CtlA + "#USB(6)#USB(2)",
            controller: Tri.Yes, rootPort: Tri.Yes, extHub: Tri.No, downstream: Tri.Yes, samePort: Tri.No);
    }

    [Fact]
    public void Relations_MultiDigitRootPort_IsNotDownstreamOfItsStringPrefix()
    {
        // The multi-digit trap: USB(2) is a STRING prefix of USB(21) but not a hop prefix.
        AssertRelations(CtlA + "#USB(21)", CtlA + "#USB(2)",
            controller: Tri.Yes, rootPort: Tri.No, extHub: Tri.No, downstream: Tri.No, samePort: Tri.No);
    }

    [Fact]
    public void Relations_DifferentControllersWithTheSamePciShape_ShareNothing()
    {
        AssertRelations(CtlA + "#USB(1)", CtlB + "#USB(1)",
            controller: Tri.No, rootPort: Tri.No, extHub: Tri.No, downstream: Tri.No, samePort: Tri.No);
    }

    [Fact]
    public void Relations_IdenticalPaths_AreTheSamePortAndNotDownstreamOfEachOther()
    {
        // Position is not identity: a USB node and its function child share a path exactly.
        AssertRelations(CtlA + "#USB(6)#USB(2)#USB(4)#USB(4)", CtlA + "#USB(6)#USB(2)#USB(4)#USB(4)",
            controller: Tri.Yes, rootPort: Tri.Yes, extHub: Tri.Yes, downstream: Tri.No, samePort: Tri.Yes);
    }

    [Fact]
    public void Relations_PathsFromDifferentGrammars_NeverCompareAsRelated()
    {
        AssertRelations(CtlA + "#USB(1)", Sys + "/usb9/9-1",
            controller: Tri.No, rootPort: Tri.No, extHub: Tri.No, downstream: Tri.No, samePort: Tri.No);
    }

    // ── D5's conditional invariant ─────────────────────────────────────
    //
    // The implication chain holds at two hops or more, and the three shallow cases below are
    // exactly where it does not. An ordered enum would misreport all three.

    [Fact]
    public void Relations_SamePortAtOneHop_DoesNotImplySharingAnExternalHub()
    {
        AssertRelations(CtlA + "#USB(2)", CtlA + "#USB(2)",
            controller: Tri.Yes, rootPort: Tri.Yes, extHub: Tri.No, downstream: Tri.No, samePort: Tri.Yes);
    }

    [Fact]
    public void Relations_SamePortAtZeroHops_DoesNotImplySharingARootPort()
    {
        // Two root hubs on one controller: there is no first hop for them to agree on.
        AssertRelations(CtlA, CtlA,
            controller: Tri.Yes, rootPort: Tri.No, extHub: Tri.No, downstream: Tri.No, samePort: Tri.Yes);
    }

    [Fact]
    public void Relations_DownstreamOfARootHub_DoesNotImplySharingARootPort()
    {
        AssertRelations(CtlA + "#USB(2)", CtlA,
            controller: Tri.Yes, rootPort: Tri.No, extHub: Tri.No, downstream: Tri.Yes, samePort: Tri.No);
    }

    [Fact]
    public void Relations_AtTwoHopsOrMore_TheImplicationChainDoesHold()
    {
        var deep = Parse(CtlA + "#USB(6)#USB(4)#USB(2)");
        var sib = Parse(CtlA + "#USB(6)#USB(4)#USB(9)");

        Assert.Equal(Tri.Yes, deep.SharesExternalHubWith(sib));
        Assert.Equal(Tri.Yes, deep.SharesRootPortWith(sib));
        Assert.Equal(Tri.Yes, deep.SharesControllerWith(sib));
    }

    // ── D7: default is the unparsed state, and never a negative ────────

    [Fact]
    public void Default_IsTheUnparsedState()
    {
        PortPath unparsed = default;

        Assert.False(unparsed.IsParsed);
    }

    [Fact]
    public void Default_YieldsNeitherACountNorIsRootHub()
    {
        PortPath unparsed = default;

        Assert.False(unparsed.TryGetExternalHubCount(out _));
        Assert.False(unparsed.TryGetIsRootHub(out _));
    }

    [Fact]
    public void Default_EveryRelationAgainstARealPath_IsUnknownRatherThanNo()
    {
        // A bare false here would assert "different root ports" when the truth is "cannot see",
        // which is the failure D7 exists to prevent.
        PortPath unparsed = default;
        var real = Parse(CtlA + "#USB(6)#USB(4)");

        Assert.Equal(Tri.Unknown, unparsed.SharesControllerWith(real));
        Assert.Equal(Tri.Unknown, unparsed.SharesRootPortWith(real));
        Assert.Equal(Tri.Unknown, unparsed.SharesExternalHubWith(real));
        Assert.Equal(Tri.Unknown, unparsed.IsDownstreamOf(real));
        Assert.Equal(Tri.Unknown, unparsed.IsSamePortAs(real));
    }

    [Fact]
    public void Default_UnknownHoldsWithTheUnparsedOperandOnEitherSide()
    {
        var real = Parse(CtlA + "#USB(6)#USB(4)");

        Assert.Equal(Tri.Unknown, real.SharesRootPortWith(default));
    }

    [Fact]
    public void Default_ComparedWithAnotherDefault_DoesNotCompareAsTheSamePort()
    {
        PortPath unparsed = default;

        Assert.Equal(Tri.Unknown, unparsed.IsSamePortAs(default));
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private static PortPath Parse(string locationPath)
    {
        PortPath.TryParse(locationPath, out var path);
        return path;
    }

    /// <summary>Asserts that <paramref name="locationPath"/> is not a port path, and why.</summary>
    private static void AssertRejected(string? locationPath, ParseFailure expected)
    {
        Assert.False(PortPath.TryParse(locationPath, out _, out var actual));
        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// Asserts all five relations at once — a failure prints the whole row against the D5 table —
    /// then re-asserts the symmetric ones with the operands swapped.
    /// </summary>
    // ── Value identity (D7: "a test pins every member against default(PortPath)") ──

    [Fact]
    public void Equals_TwoIdenticallyParsedPaths_AreEqualAndHashAlike()
    {
        // The reason the overrides exist: the inherited ValueType equality compares the hop
        // array BY REFERENCE, so two separate parses of one string would be unequal. Delete the
        // overrides and this is the test that notices.
        var a = Parse($"{CtlA}#USB(6)#USB(4)#USB(2)");
        var b = Parse($"{CtlA}#USB(6)#USB(4)#USB(2)");

        Assert.True(a.Equals(b));
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.Single(new HashSet<PortPath> { a, b });
    }

    [Theory]
    [InlineData("#USB(6)#USB(4)#USB(2)", "#USB(6)#USB(4)#USB(3)")]   // differing last hop
    [InlineData("#USB(2)", "#USB(21)")]                              // the multi-digit trap
    [InlineData("#USB(6)#USB(4)", "#USB(6)#USB(4)#USB(2)")]          // prefix, not equal
    public void Equals_DifferentPositions_AreNotEqual(string tailA, string tailB)
    {
        Assert.False(Parse(CtlA + tailA).Equals(Parse(CtlA + tailB)));
    }

    [Fact]
    public void Equals_DiffersOnlyByControllerCase_IsNotEqual()
    {
        // Ordinal, not OrdinalIgnoreCase — ADR-0079 D5 records that a parsed comparison is
        // ordinal, which is why re-expressing ADR-0063's OrdinalIgnoreCase correlation would be a
        // behaviour change rather than a pure refactor. On Linux it is also a correctness matter:
        // sysfs is case-sensitive, so these are genuinely different nodes.
        const string Lower = "/sys/devices/platform/soc/usb1";
        const string Upper = "/sys/devices/platform/SOC/usb1";

        Assert.False(Parse(Lower).Equals(Parse(Upper)));
        Assert.Equal(Tri.No, Parse(Lower).SharesControllerWith(Parse(Upper)));
    }

    [Fact]
    public void Equals_IsReflexive_SoHashCollectionsBehave()
    {
        // IEquatable<T> requires reflexivity, and hash collections rely on it: a value added to a
        // set must be findable in that set. An earlier revision made an unparsed value equal to
        // nothing - NaN semantics - to stop non-port-paths collapsing into one dictionary key.
        // That traded a language invariant for a hazard ADR-0079 D7 already concedes is
        // unpreventable, so it was reverted. This test is why it stays reverted.
        PortPath unparsed = default;
        var real = Parse($"{CtlA}#USB(6)");

        Assert.True(unparsed.Equals(unparsed));
        Assert.True(real.Equals(real));

        Assert.Contains(unparsed, new HashSet<PortPath> { unparsed });
        Assert.Contains(real, new HashSet<PortPath> { real });
    }

    [Fact]
    public void Equals_UnparsedIsNeverEqualToAParsedPath_AndIsADegenerateKey()
    {
        // The collapse the previous revision over-reached to prevent is real, documented, and
        // pinned here so nobody rediscovers it as a surprise: every non-port-path is `default`, so
        // they are all one another's equal. On the machine ADR-0079 measured, that is 204 of 300
        // devices folding into one entry if a caller keys on the parse result without reading
        // TryParse's bool. The guard against that is IsSamePortAs, which answers Unknown.
        PortPath unparsed = default;
        var alsoUnparsed = ParseExpectingFailure("PCIROOT(0)#PCI(0200)");   // a NIC, not a port path
        var real = Parse($"{CtlA}#USB(6)");

        Assert.False(unparsed.Equals(real));
        Assert.False(real.Equals(unparsed));

        Assert.Single(new HashSet<PortPath> { unparsed, alsoUnparsed, default });
        Assert.Equal(Tri.Unknown, unparsed.IsSamePortAs(alsoUnparsed));
    }

    /// <summary>Parses a string expected NOT to be a port path, returning the unparsed value.</summary>
    private static PortPath ParseExpectingFailure(string s)
    {
        Assert.False(PortPath.TryParse(s, out var p), $"expected '{s}' not to parse");
        return p;
    }

    [Fact]
    public void Equals_AgainstANonPortPathObject_IsFalse()
    {
        Assert.False(Parse($"{CtlA}#USB(6)").Equals("not a PortPath"));
        Assert.False(Parse($"{CtlA}#USB(6)").Equals(null));
    }

    [Fact]
    public void ToString_RendersTheHopVector_AndNamesTheUnparsedState()
    {
        // The Efm8HidProgrammer log (D6) emits this, and it is what lets two log lines answer the
        // root-port question a single open cannot: the hop vector has to be legible.
        string rendered = Parse($"{CtlA}#USB(6)#USB(3)").ToString();
        Assert.Contains("6,3", rendered, StringComparison.Ordinal);

        // Never something a reader could mistake for a position.
        string unparsed = default(PortPath).ToString();
        Assert.Equal("<unparsed>", unparsed);
        Assert.DoesNotContain("0", unparsed, StringComparison.Ordinal);
    }

    /// <summary>Parses a string that is expected NOT to be a port path, returning the unparsed value.</summary>
    private static PortPath Parse2(string s)
    {
        Assert.False(PortPath.TryParse(s, out var p), $"expected '{s}' not to parse");
        return p;
    }

    private static void AssertRelations(
        string a, string b, Tri controller, Tri rootPort, Tri extHub, Tri downstream, Tri samePort)
    {
        var x = Parse(a);
        var y = Parse(b);

        Assert.Equal(
            (controller, rootPort, extHub, downstream, samePort),
            (x.SharesControllerWith(y), x.SharesRootPortWith(y), x.SharesExternalHubWith(y),
             x.IsDownstreamOf(y), x.IsSamePortAs(y)));

        // Symmetric relations must actually be symmetric. IsDownstreamOf is deliberately absent:
        // it is the one relation whose answer is allowed to change when the operands swap.
        //
        // SharesRootPortWith is included, and its absence here was a live hole rather than a
        // stylistic one: the implementation guards BOTH operands for a non-empty hop vector, and
        // without a swapped assertion the left-hand guard is untested. Drop it and
        // rootHub.SharesRootPortWith(deviceOnPort2) indexes [0] on an empty vector and throws — in
        // production, at the one call site ADR-0079 D6 names, with a fully green suite.
        Assert.Equal(
            (controller, rootPort, extHub, samePort),
            (y.SharesControllerWith(x), y.SharesRootPortWith(x), y.SharesExternalHubWith(x),
             y.IsSamePortAs(x)));
    }
}
