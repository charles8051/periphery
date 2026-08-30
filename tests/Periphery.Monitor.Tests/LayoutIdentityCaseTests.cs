using System.Collections.Immutable;
using Periphery;

namespace Periphery.Monitor.Tests;

/// <summary>
/// Hardware-free regression coverage for issue #190. Runs in the **gate tier** (no
/// <c>Category=Integration</c> trait), so CI catches a re-introduction on every
/// build — the hardware-backed <c>LayoutIdentityJoinTests</c> prove the divergence
/// is real on a live machine, but they are excluded by the default
/// <c>--filter "Category!=Integration"</c> and so cannot guard the logic.
///
/// <para>The bug: the CCD layout reader yields the PnP instance id in the case the
/// device-interface path carries (lower), while core's <see cref="DeviceInfo.Id"/>
/// comes from the device-instance enumeration path (upper). Every value below is
/// fabricated from the real strings observed on a 4-monitor box, so no display is
/// needed to reproduce the join failure.</para>
///
/// <para>What this pins: the join must work <b>because the ids are typed</b>
/// <see cref="Periphery.DeviceId"/>, whose equality is
/// <see cref="System.StringComparison.OrdinalIgnoreCase"/>. If someone reverts
/// either property to <c>string</c>, or makes <c>DeviceId</c> compare ordinally,
/// these fail immediately and locally.</para>
/// </summary>
public class LayoutIdentityCaseTests
{
    // The exact divergence measured on hardware: identical but for the hex case.
    private const string LayoutCase = @"DISPLAY\ACR0507\5&30fcbbf1&0&UID397571";
    private const string CoreCase   = @"DISPLAY\ACR0507\5&30FCBBF1&0&UID397571";

    private static MonitorLayoutEntry Entry(DeviceId id) =>
        new(id, "Fake", true, new DisplayMode(1920, 1080, 60), null,
            MonitorOrientation.Landscape, MonitorOutputTechnology.Other,
            new DisplayPosition(0, 0), ImmutableArray<DisplayMode>.Empty);

    [Fact]
    public void LayoutEntry_JoinsDeviceInfo_AcrossTheCaseDivergence()
    {
        var entry = Entry(LayoutCase);
        var device = new DeviceInfo { Id = CoreCase, Category = DeviceCategory.Monitor };

        // The documented ADR-0059 D2 join.
        Assert.True(entry.DeviceId == device.Id);
        Assert.Equal(device.Id, entry.DeviceId);
    }

    [Fact]
    public void TheUnderlyingStrings_ReallyDoDiffer_SoTheTypeIsLoadBearing()
    {
        // Guards against someone "fixing" this by normalising the constants and
        // concluding the typed id is unnecessary. The strings differ; only the
        // DeviceId semantics bridge them.
        Assert.NotEqual(LayoutCase, CoreCase, StringComparer.Ordinal);
        Assert.Equal(LayoutCase, CoreCase, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void LayoutEntry_IsFoundByDictionaryLookup_KeyedByDeviceInfoId()
    {
        // The shape a consumer actually writes: index the layout, look up by the id
        // core handed them. A Dictionary<string,…> with the default comparer is what
        // failed before; DeviceId hashes OrdinalIgnoreCase so this works untouched.
        var byId = new[] { Entry(LayoutCase) }.ToDictionary(e => e.DeviceId);
        var device = new DeviceInfo { Id = CoreCase, Category = DeviceCategory.Monitor };

        Assert.True(byId.TryGetValue(device.Id, out var found));
        Assert.Equal("Fake", found!.FriendlyName);
    }

    [Fact]
    public void Applier_MatchesConfigToEntry_AcrossTheCaseDivergence()
    {
        // The apply path joins MonitorConfiguration.DeviceId to
        // MonitorLayoutEntry.DeviceId. Before #193 both were string and the match
        // used an explicit OrdinalIgnoreCase; the typed ids make it structural.
        var current = new MonitorLayout(
            [Entry(LayoutCase)], MonitorLayoutAvailability.Available);
        var desired = new[] { new MonitorConfiguration(CoreCase, new DisplayMode(1920, 1080, 60)) };

        var positions = LayoutDiff.ResolvePositions(current, desired);

        // Resolved against the entry, not treated as an unknown monitor.
        Assert.Single(positions);
        Assert.True(positions.ContainsKey(CoreCase));
        Assert.True(positions.ContainsKey(LayoutCase));
    }
}
