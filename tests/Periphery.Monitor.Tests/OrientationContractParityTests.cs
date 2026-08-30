using System;
using System.Linq;

namespace Periphery.Monitor.Tests;

/// <summary>
/// Build-time guard for ADR-0068 Decision 4: the discovery-plane
/// <see cref="DisplayOrientation"/> (core <c>Periphery</c>) and the control-plane
/// <see cref="MonitorOrientation"/> (<c>Periphery.Monitor</c>, ADR-0064) are two
/// types only because core must not depend on the optional monitor-control
/// extension. They are committed to identical members and identical ordinals so a
/// consumer holding both maps member-for-member.
///
/// <para>Nothing but discipline kept them aligned. This pins it: adding,
/// removing, renaming, or renumbering a member on either side fails here.</para>
///
/// <para>It also pins the ordinals themselves. Both enums document their numeric
/// values as a stable, opaque serialization contract; inserting a member in the
/// middle would silently break every persisted <c>DeviceInfo</c> / layout
/// snapshot that stored the number rather than the name.</para>
///
/// <para>This test lives in <c>Periphery.Monitor.Tests</c> because it is the only
/// test project that sees both assemblies — core's own suite cannot reference the
/// extension, which is precisely the layering the ADR is describing.</para>
/// </summary>
public class OrientationContractParityTests
{
    [Fact]
    public void DisplayOrientation_And_MonitorOrientation_HaveIdenticalMembers()
    {
        var discovery = Enum.GetNames<DisplayOrientation>().OrderBy(n => n, StringComparer.Ordinal);
        var control = Enum.GetNames<MonitorOrientation>().OrderBy(n => n, StringComparer.Ordinal);

        Assert.Equal(control, discovery);
    }

    [Fact]
    public void DisplayOrientation_And_MonitorOrientation_HaveIdenticalOrdinals()
    {
        foreach (var name in Enum.GetNames<DisplayOrientation>())
        {
            var discovery = (int)Enum.Parse<DisplayOrientation>(name);
            var control = (int)Enum.Parse<MonitorOrientation>(name);

            Assert.Equal(control, discovery);
        }
    }

    [Theory]
    [InlineData(DisplayOrientation.Landscape, 0)]
    [InlineData(DisplayOrientation.Portrait, 1)]
    [InlineData(DisplayOrientation.LandscapeFlipped, 2)]
    [InlineData(DisplayOrientation.PortraitFlipped, 3)]
    public void DisplayOrientation_OrdinalsArePinned(DisplayOrientation orientation, int expected)
    {
        // Restated as literals on purpose: the parity test above would happily
        // pass if BOTH enums were renumbered together, which is equally breaking
        // for anything that persisted the number.
        Assert.Equal(expected, (int)orientation);
    }
}
