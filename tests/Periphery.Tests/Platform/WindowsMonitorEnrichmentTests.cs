using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.Versioning;
using Periphery.Windows;

namespace Periphery.Tests;

/// <summary>
/// Unit tests for the pure Windows monitor-enrichment helpers (issue #149):
/// <see cref="WindowsMonitorEnrichment.MergeArrival"/> (the relocated
/// arrival-time guardrail) and <see cref="WindowsMonitorEnrichment.ComputeDeltas"/>
/// (the enrich→diff step of the WM_DISPLAYCHANGE refresh). Both are total value
/// transforms — no display hardware, no OS calls — so they run anywhere.
/// </summary>
[SupportedOSPlatform("windows")]
public class WindowsMonitorEnrichmentTests
{
    private const string MonitorId = "DISPLAY\\DELA1234\\5&1a2b3c4d&0&UID256";

    private static DeviceInfo Monitor(
        string id = MonitorId,
        string? monitorName = null,
        Size? resolution = null,
        Rectangle? bounds = null,
        DisplayConnector? connector = null,
        DisplayConnectionKind? kind = null,
        bool isActive = true,
        DeviceCategory category = DeviceCategory.Monitor) => new()
    {
        Id = id,
        Name = "Generic PnP Monitor",
        Category = category,
        IsActive = isActive,
        MonitorName = monitorName,
        DisplayResolution = resolution,
        DisplayBounds = bounds,
        DisplayPhysicalConnector = connector,
        DisplayConnectionKind = kind,
    };

    // ── MergeArrival ──────────────────────────────────────────────────────

    [Fact]
    public void MergeArrival_FillsNullDisplayFields_FromPrior()
    {
        var prior = Monitor(
            monitorName: "DELL U2720Q",
            resolution: new Size(3840, 2160),
            bounds: new Rectangle(0, 0, 3840, 2160),
            connector: DisplayConnector.DisplayPort,
            kind: DisplayConnectionKind.Wired);
        var bareArrival = Monitor(monitorName: null); // DisplayConfig tier all null

        var merged = WindowsMonitorEnrichment.MergeArrival(bareArrival, prior);

        Assert.Equal("DELL U2720Q", merged.MonitorName);
        Assert.Equal(new Size(3840, 2160), merged.DisplayResolution);
        Assert.Equal(new Rectangle(0, 0, 3840, 2160), merged.DisplayBounds);
        Assert.Equal(DisplayConnector.DisplayPort, merged.DisplayPhysicalConnector);
        Assert.Equal(DisplayConnectionKind.Wired, merged.DisplayConnectionKind);
    }

    [Fact]
    public void MergeArrival_ArrivalNonNullField_Wins_OverPrior()
    {
        var prior = Monitor(monitorName: "OLD NAME");
        var arrival = Monitor(monitorName: "NEW NAME");

        var merged = WindowsMonitorEnrichment.MergeArrival(arrival, prior);

        Assert.Equal("NEW NAME", merged.MonitorName);
    }

    [Fact]
    public void MergeArrival_NonDisplayFields_AlwaysFollowArrival()
    {
        // Only the DisplayConfig tier is carried forward; everything else (here,
        // IsActive) is authoritative from the arrival.
        var prior = Monitor(monitorName: "DELL U2720Q", isActive: true);
        var arrival = Monitor(monitorName: null, isActive: false);

        var merged = WindowsMonitorEnrichment.MergeArrival(arrival, prior);

        Assert.Equal("DELL U2720Q", merged.MonitorName); // carried
        Assert.False(merged.IsActive);                   // arrival wins
    }

    [Fact]
    public void MergeArrival_NonMonitorCategory_ReturnsArrivalUnchanged()
    {
        var prior = Monitor(monitorName: "DELL U2720Q", category: DeviceCategory.Usb);
        var arrival = Monitor(monitorName: null, category: DeviceCategory.Usb);

        var merged = WindowsMonitorEnrichment.MergeArrival(arrival, prior);

        Assert.Same(arrival, merged);
        Assert.Null(merged.MonitorName);
    }

    /// <summary>
    /// The monitor-tier fields on <see cref="DeviceInfo"/>, derived by reflection
    /// rather than restated as a literal — this is what makes the drift guard below
    /// actually bind. Any new nullable <c>Display*</c>/<c>MonitorName</c> property
    /// automatically enters the expected set and fails the test until
    /// <see cref="WindowsMonitorEnrichment.MergeArrival"/> carries it.
    /// </summary>
    private static string[] MonitorTierProperties() =>
        typeof(DeviceInfo).GetProperties()
            .Where(p => p.Name == nameof(DeviceInfo.MonitorName) || p.Name.StartsWith("Display", StringComparison.Ordinal))
            .Where(p => Nullable.GetUnderlyingType(p.PropertyType) is not null || !p.PropertyType.IsValueType)
            .Select(p => p.Name)
            .ToArray();

    [Fact]
    public void MergeArrival_CarriesEveryMonitorTierField()
    {
        // Drift guard (issue #149). The previous version of this test compared
        // against a hardcoded list, so it could only catch edits to MergeArrival —
        // it was blind to a NEW monitor field being added to DeviceInfo (or newly
        // populated by an enricher), which is the scenario that actually reopens
        // the bug. The expected set is now derived from DeviceInfo by reflection,
        // and `prior` below must populate every one of them.
        var bare = Monitor(monitorName: null);
        var prior = bare with
        {
            MonitorName                  = "DELL U2720Q",
            DisplayResolution            = new Size(3840, 2160),
            DisplayBounds                = new Rectangle(0, 0, 3840, 2160),
            DisplayOrientation           = DisplayOrientation.Landscape,
            DisplayPhysicalConnector     = DisplayConnector.DisplayPort,
            DisplayConnectionKind        = DisplayConnectionKind.Wired,
            DisplayUsageKind             = DisplayUsageKind.Standard,
            DisplayPhysicalSizeInInches  = 27f,
            DisplayDpi                   = new SizeF(163f, 163f),
            DisplayMaxLuminanceInNits    = 400f,
            DisplayMaxAvgLuminanceInNits = 350f,
            DisplayMinLuminanceInNits    = 0.5f,
        };

        var merged = WindowsMonitorEnrichment.MergeArrival(bare, prior);

        // Everything the reflection-derived set names must have been carried across.
        var carried = DeviceInfoDiff.Compute(bare, merged);
        Assert.Equal(
            MonitorTierProperties().OrderBy(x => x, StringComparer.Ordinal),
            carried.OrderBy(x => x, StringComparer.Ordinal));
    }

    // ── ComputeDeltas ─────────────────────────────────────────────────────

    [Fact]
    public void ComputeDeltas_ReturnsPair_WhenEnrichChangesMonitor()
    {
        var cached = new[] { Monitor(monitorName: null) };

        var deltas = WindowsMonitorEnrichment.ComputeDeltas(
            cached, d => d with { MonitorName = "DELL U2720Q" });

        var (previous, current) = Assert.Single(deltas);
        Assert.Null(previous.MonitorName);
        Assert.Equal("DELL U2720Q", current.MonitorName);
    }

    [Fact]
    public void ComputeDeltas_SkipsMonitor_WhenEnrichIsNoOp()
    {
        var cached = new[] { Monitor(monitorName: "DELL U2720Q") };

        var deltas = WindowsMonitorEnrichment.ComputeDeltas(cached, d => d); // identity

        Assert.Empty(deltas);
    }

    [Fact]
    public void ComputeDeltas_SkipsNonMonitorEntries()
    {
        var cached = new[] { Monitor(id: "USB\\1", category: DeviceCategory.Usb) };

        var deltas = WindowsMonitorEnrichment.ComputeDeltas(
            cached, d => d with { MonitorName = "should not be applied" });

        Assert.Empty(deltas);
    }

    [Fact]
    public void ComputeDeltas_DetectsResolutionOnlyChange_WithStableMonitorName()
    {
        // ADR-0066's marquee case: a mode/resolution change on an already-attached
        // panel, where MonitorName does NOT change. Previously every test drove the
        // delta through MonitorName, leaving this path unpinned.
        var cached = new[]
        {
            Monitor(monitorName: "DELL U2720Q", resolution: new Size(1920, 1080),
                    bounds: new Rectangle(0, 0, 1920, 1080)),
        };

        var deltas = WindowsMonitorEnrichment.ComputeDeltas(cached, d => d with
        {
            DisplayResolution = new Size(3840, 2160),
            DisplayBounds = new Rectangle(0, 0, 3840, 2160),
        });

        var (previous, current) = Assert.Single(deltas);
        Assert.Equal(previous.MonitorName, current.MonitorName); // name unchanged
        Assert.Equal(new Size(3840, 2160), current.DisplayResolution);

        var changed = DeviceInfoDiff.Compute(previous, current);
        Assert.Contains(nameof(DeviceInfo.DisplayResolution), changed);
        Assert.Contains(nameof(DeviceInfo.DisplayBounds), changed);
        Assert.DoesNotContain(nameof(DeviceInfo.MonitorName), changed);
    }

    [Fact]
    public void ComputeDeltas_DetectsRotationOnlyChange_OnAnImmovablePrimary()
    {
        // Issue #163's silent case: the primary panel at (0,0) rotated. Its origin
        // cannot move, and here its footprint is square so the bounds do not move
        // either — DisplayOrientation is the only signal that a change occurred,
        // and without it ComputeDeltas returned nothing.
        var cached = new[]
        {
            Monitor(monitorName: "Linux FHD", bounds: new Rectangle(0, 0, 1280, 1280))
                with { DisplayOrientation = DisplayOrientation.Landscape },
        };

        var deltas = WindowsMonitorEnrichment.ComputeDeltas(cached, d => d with
        {
            DisplayOrientation = DisplayOrientation.Portrait,
        });

        var (previous, current) = Assert.Single(deltas);
        var changed = DeviceInfoDiff.Compute(previous, current);
        Assert.Equal(nameof(DeviceInfo.DisplayOrientation), Assert.Single(changed));
    }

    [Fact]
    public void ComputeDeltas_MultipleMonitors_ReturnsOnlyChanged()
    {
        var unchanged = Monitor(id: "DISPLAY\\A\\1", monitorName: "Already Named");
        var toEnrich = Monitor(id: "DISPLAY\\B\\2", monitorName: null);

        var deltas = WindowsMonitorEnrichment.ComputeDeltas(
            new[] { unchanged, toEnrich },
            d => d.MonitorName is null ? d with { MonitorName = "Freshly Enriched" } : d);

        var (previous, current) = Assert.Single(deltas);
        Assert.Equal("DISPLAY\\B\\2", current.Id);
        Assert.Equal("Freshly Enriched", current.MonitorName);
    }
}
