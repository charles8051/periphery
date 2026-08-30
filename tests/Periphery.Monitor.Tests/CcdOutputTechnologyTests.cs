using System.Runtime.Versioning;
using Periphery.Monitor.Windows;

namespace Periphery.Monitor.Tests;

/// <summary>
/// Locks the explicit CCD output-technology → <see cref="MonitorOutputTechnology"/>
/// mapping (ADR-0064 / ADR-0070). These are pure value transforms, so they run on
/// any host; the platform attribute only silences the analyzer for the
/// Windows-scoped helper.
/// </summary>
[SupportedOSPlatform("windows")]
public class CcdOutputTechnologyTests
{
    [Theory]
    [InlineData(0x80000000u, MonitorOutputTechnology.Internal)] // INTERNAL
    [InlineData(0u, MonitorOutputTechnology.Vga)]               // HD15
    [InlineData(4u, MonitorOutputTechnology.Dvi)]               // DVI
    [InlineData(5u, MonitorOutputTechnology.Hdmi)]              // HDMI
    [InlineData(10u, MonitorOutputTechnology.DisplayPortExternal)]
    [InlineData(11u, MonitorOutputTechnology.DisplayPortEmbedded)]
    public void FromCcd_MapsKnownTechnologies(uint tech, MonitorOutputTechnology expected)
    {
        Assert.Equal(expected, CcdOutputTechnology.FromCcd(tech));
    }

    [Theory]
    [InlineData(16u, MonitorOutputTechnology.IndirectWired)]
    [InlineData(17u, MonitorOutputTechnology.IndirectVirtual)]
    public void FromCcd_KeepsTheTwoIndirectTechnologiesDistinct(
        uint tech, MonitorOutputTechnology expected)
    {
        // ADR-0070 D2. The pair must NOT collapse: INDIRECT_WIRED (16) is
        // reported both by synthetic IddCx rigs and by DisplayLink / USB-C dock
        // adapters driving REAL panels, so a single "Virtual" member would
        // report real glass as virtual. Periphery reports the platform fact;
        // the consumer owns the policy.
        Assert.Equal(expected, CcdOutputTechnology.FromCcd(tech));
    }

    [Fact]
    public void FromCcd_IndirectWired_IsNotTheSameValueAsIndirectVirtual()
    {
        // The regression this guards is a well-meaning "simplification" that
        // folds the two back together, which silently reintroduces the false
        // positive on DisplayLink / dock-attached panels.
        Assert.NotEqual(CcdOutputTechnology.FromCcd(16u), CcdOutputTechnology.FromCcd(17u));
    }

    // Native values per the Windows SDK (Include/10.0.26100.0/um/wingdi.h
    // lines 2807-2828). The enum skips 7 and is not densely packed.
    [Theory]
    [InlineData(1u)]          // SVIDEO
    [InlineData(2u)]          // COMPOSITE_VIDEO
    [InlineData(3u)]          // COMPONENT_VIDEO
    [InlineData(6u)]          // LVDS
    [InlineData(9u)]          // SDI
    [InlineData(15u)]         // MIRACAST
    [InlineData(0xFFFFFFFFu)] // OTHER (-1, same bit pattern) / _FORCE_UINT32
    [InlineData(999u)]        // never-defined value
    public void FromCcd_UnmappedTechnologies_FallBackToOther(uint tech)
    {
        Assert.Equal(MonitorOutputTechnology.Other, CcdOutputTechnology.FromCcd(tech));
    }
}
