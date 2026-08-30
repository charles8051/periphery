namespace Periphery.Tests;

/// <summary>
/// ADR-0079 D2/D4 on Linux, cross-validated against real sysfs rows captured from
/// the Linux device rig — see issue #303 and
/// <c>docs/explorations/portpath-linux-controller-breadth-2026-08.md</c>.
/// </summary>
/// <remarks>
/// <para>
/// Every path here was read off the rig, not composed. The point of the capture was breadth: the
/// original QEMU fixture hung everything off one xHCI controller, so nothing established that the
/// nesting shape holds for a controller of a different kind. These rows come from <b>four</b> —
/// OHCI, UHCI, xHCI, and an xHCI sitting behind a two-level PCI bridge chain.
/// </para>
/// <para>
/// The ground truth is the kernel's own <c>devpath</c> attribute, which spells the port chain
/// directly (<c>2.1.1</c>) and is read from the device rather than derived from the string under
/// test. That makes this the Linux analogue of the Windows probe's independent devnode walk: the
/// parser's hop vector must equal what the kernel says the chain is, and the external-hub count
/// must equal its component count minus one (D4).
/// </para>
/// <para>
/// These tests need no rig and no hardware. ADR-0079 D2 dispatches on the shape of the string, so
/// a Linux syspath parses identically on any host — which is the property that lets a Windows
/// developer and a CI runner both hold the platform to its contract.
/// </para>
/// </remarks>
public class PortPathLinuxRigTests
{
    /// <summary>
    /// Real rows: syspath, the kernel's own <c>devpath</c>, and the controller kind they came from.
    /// A root hub spells its <c>devpath</c> <c>"0"</c>, which is one component and therefore zero
    /// external hubs — the same answer the parser reaches from an empty hop vector.
    /// </summary>
    public static TheoryData<string, string, string> RigRows() => new()
    {
        // ── OHCI (bus 1): hub → hub → device. A controller type absent from the original fixture.
        { "/sys/devices/pci0000:00/0000:00:02.0/usb1", "0", "ohci-pci" },
        { "/sys/devices/pci0000:00/0000:00:02.0/usb1/1-2", "2", "ohci-pci" },
        { "/sys/devices/pci0000:00/0000:00:02.0/usb1/1-2/1-2.1", "2.1", "ohci-pci" },
        { "/sys/devices/pci0000:00/0000:00:02.0/usb1/1-2/1-2.1/1-2.1.1", "2.1.1", "ohci-pci" },

        // ── UHCI (bus 4): hub → device.
        { "/sys/devices/pci0000:00/0000:00:03.0/usb4", "0", "uhci_hcd" },
        { "/sys/devices/pci0000:00/0000:00:03.0/usb4/4-2", "2", "uhci_hcd" },
        { "/sys/devices/pci0000:00/0000:00:03.0/usb4/4-2/4-2.1", "2.1", "uhci_hcd" },

        // ── EHCI (buses 2 and 3): root hub only. A full-speed hub cannot attach to EHCI without a
        //    companion controller, so these buses carry no chain — the root-hub row is the whole
        //    of what EHCI contributes, and it is recorded rather than quietly omitted.
        { "/sys/devices/pci0000:00/0000:00:1d.7/usb2", "0", "ehci-pci" },
        { "/sys/devices/pci0000:00/0000:00:1a.7/usb3", "0", "ehci-pci" },

        // ── xHCI (bus 11): the original nested fixture, re-captured alongside the others.
        { "/sys/devices/pci0000:00/0000:00:01.0/usb11", "0", "xhci_hcd" },
        { "/sys/devices/pci0000:00/0000:00:01.0/usb11/11-1", "1", "xhci_hcd" },
        { "/sys/devices/pci0000:00/0000:00:01.0/usb11/11-3", "3", "xhci_hcd" },
        { "/sys/devices/pci0000:00/0000:00:01.0/usb11/11-3/11-3.1", "3.1", "xhci_hcd" },
        { "/sys/devices/pci0000:00/0000:00:01.0/usb11/11-3/11-3.1/11-3.1.1", "3.1.1", "xhci_hcd" },

        // ── xHCI behind a two-level PCI bridge chain (bus 13), carrying a passed-through camera.
        //    The controller prefix here is three PCI components deep rather than one, which is the
        //    closest this rig gets to the non-PCI-rooted shape #303 still wants an SBC for.
        { "/sys/devices/pci0000:00/0000:00:1e.0/0000:05:02.0/0000:07:1b.0/usb13", "0", "xhci_hcd" },
        { "/sys/devices/pci0000:00/0000:00:1e.0/0000:05:02.0/0000:07:1b.0/usb13/13-1", "1", "xhci_hcd" },
    };

    [Theory]
    [MemberData(nameof(RigRows))]
    public void ParsedHops_MatchTheKernelsOwnDevpath(string syspath, string devpath, string controller)
    {
        Assert.True(PortPath.TryParse(syspath, out var path), $"{controller}: {syspath} should parse");

        // A root hub spells devpath "0" and has no hops; every other row's chain is the devpath.
        int[] expected = devpath == "0"
            ? []
            : devpath.Split('.').Select(int.Parse).ToArray();

        Assert.Equal(expected, path.Hops.ToArray());
    }

    [Theory]
    [MemberData(nameof(RigRows))]
    public void ExternalHubCount_IsDevpathComponentsMinusOne(string syspath, string devpath, string controller)
    {
        Assert.True(PortPath.TryParse(syspath, out var path));
        Assert.True(path.TryGetExternalHubCount(out int actual));

        // D4's formula, checked against ground truth read from the device rather than derived from
        // the string under test. "0" is one component, so a root hub lands on zero either way.
        int expected = devpath.Split('.').Length - 1;

        Assert.Equal(expected, actual);

        // The two zeroes D4 keeps apart: a root hub and a directly-attached device both report
        // zero external hubs, and only IsRootHub separates them.
        Assert.True(path.TryGetIsRootHub(out bool isRootHub));
        Assert.Equal(devpath == "0", isRootHub);
    }

    /// <summary>
    /// The nesting shape is identical across controller kinds — which is the whole question #303
    /// asked, and it had never been checked because the original fixture used one controller.
    /// </summary>
    [Theory]
    [InlineData("/sys/devices/pci0000:00/0000:00:02.0/usb1/1-2/1-2.1/1-2.1.1", "ohci-pci")]
    [InlineData("/sys/devices/pci0000:00/0000:00:01.0/usb11/11-3/11-3.1/11-3.1.1", "xhci_hcd")]
    public void TwoExternalHubs_ReadTheSame_WhicheverControllerTheyHangFrom(string syspath, string controller)
    {
        Assert.True(PortPath.TryParse(syspath, out var path), controller);
        Assert.True(path.TryGetExternalHubCount(out int hubs));
        Assert.Equal(2, hubs);
    }

    /// <summary>
    /// Devices on different controllers must never compare as sharing one, however alike their hop
    /// vectors look. The OHCI and xHCI leaves below sit at hops [2,1,1] and [3,1,1] on the same
    /// machine; a parser that ignored the controller prefix would answer Yes to both relations.
    /// </summary>
    [Fact]
    public void DifferentControllers_ShareNothing_EvenOnOneMachine()
    {
        Assert.True(PortPath.TryParse("/sys/devices/pci0000:00/0000:00:02.0/usb1/1-2/1-2.1", out var ohci));
        Assert.True(PortPath.TryParse("/sys/devices/pci0000:00/0000:00:01.0/usb11/11-3/11-3.1", out var xhci));

        Assert.Equal(Tri.No, ohci.SharesControllerWith(xhci));
        Assert.Equal(Tri.No, ohci.SharesRootPortWith(xhci));
        Assert.Equal(Tri.No, ohci.SharesExternalHubWith(xhci));
        Assert.Equal(Tri.No, ohci.IsDownstreamOf(xhci));
        Assert.Equal(Tri.No, ohci.IsSamePortAs(xhci));
    }

    /// <summary>
    /// Devices on one controller relate exactly as ADR-0079 D5 says, on real captured rows.
    /// </summary>
    [Fact]
    public void OnOneController_TheRelationsHold()
    {
        Assert.True(PortPath.TryParse("/sys/devices/pci0000:00/0000:00:02.0/usb1/1-2", out var hub1));
        Assert.True(PortPath.TryParse("/sys/devices/pci0000:00/0000:00:02.0/usb1/1-2/1-2.1", out var hub2));
        Assert.True(PortPath.TryParse("/sys/devices/pci0000:00/0000:00:02.0/usb1/1-2/1-2.1/1-2.1.1", out var leaf));

        Assert.Equal(Tri.Yes, hub2.IsDownstreamOf(hub1));
        Assert.Equal(Tri.Yes, leaf.IsDownstreamOf(hub2));
        Assert.Equal(Tri.Yes, leaf.IsDownstreamOf(hub1));   // transitive, free over a hop vector

        Assert.Equal(Tri.Yes, leaf.SharesRootPortWith(hub2));
        Assert.Equal(Tri.Yes, leaf.SharesControllerWith(hub1));

        // Different depths, so no shared external hub — the same separation the Efm8 board pair
        // produces on Windows.
        Assert.Equal(Tri.No, leaf.SharesExternalHubWith(hub2));
    }

    /// <summary>
    /// Interface nodes are children of the device and end the port chain (D2's Linux analogue of
    /// <c>USBMI</c>). Captured from the rig on three controllers, including a root hub's own
    /// <c>N-0:1.0</c> interface, which must still read as the root hub.
    /// </summary>
    [Theory]
    [InlineData("/sys/devices/pci0000:00/0000:00:02.0/usb1/1-2/1-2:1.0", "2")]
    [InlineData("/sys/devices/pci0000:00/0000:00:02.0/usb1/1-2/1-2.1/1-2.1:1.0", "2.1")]
    [InlineData("/sys/devices/pci0000:00/0000:00:02.0/usb1/1-2/1-2.1/1-2.1.1/1-2.1.1:1.0", "2.1.1")]
    [InlineData("/sys/devices/pci0000:00/0000:00:02.0/usb1/1-0:1.0", "0")]
    [InlineData("/sys/devices/pci0000:00/0000:00:01.0/usb11/11-0:1.0", "0")]
    [InlineData("/sys/devices/pci0000:00/0000:00:1d.2/usb10/10-0:1.0", "0")]
    public void InterfaceNodes_ResolveToTheirDevicesPosition(string syspath, string devpath)
    {
        Assert.True(PortPath.TryParse(syspath, out var path), syspath);

        int[] expected = devpath == "0" ? [] : devpath.Split('.').Select(int.Parse).ToArray();
        Assert.Equal(expected, path.Hops.ToArray());
    }
}
