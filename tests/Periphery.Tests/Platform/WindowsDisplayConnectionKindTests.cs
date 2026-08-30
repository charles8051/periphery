using System.Runtime.Versioning;
using Periphery.Windows;
using static Periphery.Windows.DisplayConfigInterop;

namespace Periphery.Tests;

/// <summary>
/// Unit tests for <see cref="WindowsDisplayConfigEnricher.MapConnectionKind"/> and
/// <see cref="WindowsDisplayConfigEnricher.MapConnector"/> — the pure total mappings
/// from a CCD <c>DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY</c> value to the
/// platform-neutral <see cref="DisplayConnectionKind"/> / <see cref="DisplayConnector"/>.
/// No display hardware and no OS calls, so they run on any Windows host (the class is
/// Windows-gated only because the enricher it exercises is).
/// </summary>
[SupportedOSPlatform("windows")]
public class WindowsDisplayConnectionKindTests
{
    // ── MapConnectionKind ─────────────────────────────────────────────────

    [Theory]
    [InlineData(OUTPUT_TECH_LVDS)]
    [InlineData(OUTPUT_TECH_DP_EMBEDDED)]
    [InlineData(OUTPUT_TECH_UDI_EMBEDDED)]
    [InlineData(OUTPUT_TECH_INTERNAL)]
    public void MapConnectionKind_EmbeddedTechnologies_AreInternal(int tech) =>
        Assert.Equal(DisplayConnectionKind.Internal, WindowsDisplayConfigEnricher.MapConnectionKind(tech));

    [Theory]
    [InlineData(OUTPUT_TECH_HD15)]
    [InlineData(OUTPUT_TECH_SVIDEO)]
    [InlineData(OUTPUT_TECH_COMPOSITE_VIDEO)]
    [InlineData(OUTPUT_TECH_COMPONENT_VIDEO)]
    [InlineData(OUTPUT_TECH_DVI)]
    [InlineData(OUTPUT_TECH_HDMI)]
    [InlineData(OUTPUT_TECH_D_JPN)]
    [InlineData(OUTPUT_TECH_SDI)]
    [InlineData(OUTPUT_TECH_DP_EXTERNAL)]
    [InlineData(OUTPUT_TECH_UDI_EXTERNAL)]
    [InlineData(OUTPUT_TECH_SDTVDONGLE)]
    [InlineData(OUTPUT_TECH_DP_USB_TUNNEL)]
    public void MapConnectionKind_PhysicalCableTechnologies_AreWired(int tech) =>
        Assert.Equal(DisplayConnectionKind.Wired, WindowsDisplayConfigEnricher.MapConnectionKind(tech));

    [Fact]
    public void MapConnectionKind_Miracast_IsWireless() =>
        Assert.Equal(DisplayConnectionKind.Wireless, WindowsDisplayConfigEnricher.MapConnectionKind(OUTPUT_TECH_MIRACAST));

    [Fact]
    public void MapConnectionKind_IndirectVirtual_IsVirtual() =>
        Assert.Equal(
            DisplayConnectionKind.Virtual,
            WindowsDisplayConfigEnricher.MapConnectionKind(OUTPUT_TECH_INDIRECT_VIRTUAL));

    [Fact]
    public void MapConnectionKind_IndirectWired_IsIndirect_NotVirtualAndNotWired()
    {
        // ADR-0072 (superseding ADR-0071 D1). INDIRECT_WIRED is the general
        // indirect-display path: DisplayLink adapters and USB-C / Thunderbolt
        // docks drive REAL panels through it, alongside synthetic IddCx rigs,
        // and Windows does not distinguish them. Virtual would assert "no panel"
        // about real glass; Wired would assert a cable about a synthetic rig.
        var kind = WindowsDisplayConfigEnricher.MapConnectionKind(OUTPUT_TECH_INDIRECT_WIRED);

        Assert.Equal(DisplayConnectionKind.Indirect, kind);
        Assert.NotEqual(DisplayConnectionKind.Virtual, kind);
        Assert.NotEqual(DisplayConnectionKind.Wired, kind);
    }

    [Fact]
    public void MapConnectionKind_TheTwoIndirectTechnologies_DoNotCollapse()
    {
        // The regression this guards is a well-meaning "simplification" that
        // folds the pair back together, reintroducing the false positive on
        // DisplayLink / dock-attached panels.
        Assert.NotEqual(
            WindowsDisplayConfigEnricher.MapConnectionKind(OUTPUT_TECH_INDIRECT_WIRED),
            WindowsDisplayConfigEnricher.MapConnectionKind(OUTPUT_TECH_INDIRECT_VIRTUAL));
    }

    [Theory]
    [InlineData(OUTPUT_TECH_OTHER)]        // -1, the bit pattern _FORCE_UINT32 also carries
    [InlineData(7)]                        // unused slot in the SDK enum
    [InlineData(19)]                       // one past the last technology we know of
    [InlineData(999)]
    [InlineData(int.MaxValue)]
    public void MapConnectionKind_UnrecognisedTechnology_IsUnknown_NotWired(int tech)
    {
        // Pins the default arm. It answered Wired before, which is how INDIRECT_WIRED
        // came to be reported as a physical cable: an unrecognised value was asserted
        // to be cabled instead of admitted to be unknown. A future Windows technology
        // must land here, not silently claim a cable.
        Assert.Equal(DisplayConnectionKind.Unknown, WindowsDisplayConfigEnricher.MapConnectionKind(tech));
    }

    // ── MapConnector ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(OUTPUT_TECH_HD15,            DisplayConnector.Vga)]
    [InlineData(OUTPUT_TECH_DVI,             DisplayConnector.Dvi)]
    [InlineData(OUTPUT_TECH_HDMI,            DisplayConnector.Hdmi)]
    [InlineData(OUTPUT_TECH_SDI,             DisplayConnector.Sdi)]
    [InlineData(OUTPUT_TECH_DP_EXTERNAL,     DisplayConnector.DisplayPort)]
    [InlineData(OUTPUT_TECH_DP_USB_TUNNEL,   DisplayConnector.DisplayPort)]
    [InlineData(OUTPUT_TECH_LVDS,            DisplayConnector.Internal)]
    [InlineData(OUTPUT_TECH_DP_EMBEDDED,     DisplayConnector.Internal)]
    [InlineData(OUTPUT_TECH_UDI_EMBEDDED,    DisplayConnector.Internal)]
    [InlineData(OUTPUT_TECH_INTERNAL,        DisplayConnector.Internal)]
    public void MapConnector_MapsKnownTechnologies(int tech, DisplayConnector expected) =>
        Assert.Equal(expected, WindowsDisplayConfigEnricher.MapConnector(tech));

    [Theory]
    [InlineData(OUTPUT_TECH_SVIDEO)]
    [InlineData(OUTPUT_TECH_COMPOSITE_VIDEO)]
    [InlineData(OUTPUT_TECH_COMPONENT_VIDEO)]
    [InlineData(OUTPUT_TECH_D_JPN)]
    [InlineData(OUTPUT_TECH_SDTVDONGLE)]
    public void MapConnector_AnalogueTelevisionFamily_IsAnalogTv(int tech)
    {
        // DisplayConnector.AnalogTv was unreachable before: every technology that
        // should produce it fell through to Unknown.
        Assert.Equal(DisplayConnector.AnalogTv, WindowsDisplayConfigEnricher.MapConnector(tech));
    }

    [Theory]
    [InlineData(OUTPUT_TECH_OTHER)]
    [InlineData(OUTPUT_TECH_UDI_EXTERNAL)] // no DisplayConnector member models UDI
    [InlineData(OUTPUT_TECH_MIRACAST)]     // wireless — not a physical connector
    [InlineData(999)]
    public void MapConnector_TechnologiesWithNoConnectorMember_AreUnknown(int tech) =>
        Assert.Equal(DisplayConnector.Unknown, WindowsDisplayConfigEnricher.MapConnector(tech));
}
