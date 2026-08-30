using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Periphery;

namespace Periphery.Monitor.Tests;

/// <summary>
/// Regression coverage for issue #190: a <see cref="MonitorLayoutEntry"/> must join
/// to the <see cref="DeviceInfo"/> core enumeration surfaces for the same monitor.
///
/// <para><b>The bug.</b> The two packages derive the PnP instance id from different
/// Windows APIs and the results <b>differ in case</b>: the layout reader transforms
/// the CCD <c>monitorDevicePath</c> (case-preserving, lower-case in practice), while
/// core's <see cref="DeviceInfo.Id"/> comes from the device-instance enumeration
/// path (upper-case). Measured on a 4-monitor box:
/// <c>5&amp;30fcbbf1&amp;0</c> vs <c>5&amp;30FCBBF1&amp;0</c> — an ordinal join
/// matched <b>0 of 4</b>.</para>
///
/// <para><b>The fix.</b> <see cref="MonitorLayoutEntry.DeviceId"/> and
/// <see cref="MonitorConfiguration.DeviceId"/> are typed
/// <see cref="Periphery.DeviceId"/>, whose equality and hashing are
/// <see cref="StringComparison.OrdinalIgnoreCase"/> — so the join is correct by
/// construction, the same way core already keys every device map.</para>
///
/// <para><b>Why the obvious alternative was rejected.</b> Resolving the extension's
/// id through <c>CM_Get_Device_Interface_Property(DEVPKEY_Device_InstanceId)</c> —
/// "use the same lookup core uses" — was implemented and measured: it returns the
/// <b>same lower-case string</b> as the path transform, so it fixes nothing. Windows
/// genuinely reports one instance id in different case from different APIs; core's
/// own snapshot and change-notification paths disagree with each other for exactly
/// this reason (see <c>WindowsDeviceMonitorProvider</c>'s cache comment). Case
/// normalisation is chasing an OS property; the typed id is the designed answer.</para>
///
/// <para>Deterministic, hardware-free coverage of the same regression lives in
/// <c>LayoutIdentityCaseTests</c> and runs in the CI gate tier; these two prove the
/// divergence is real on a live machine, which fabricated strings cannot.</para>
///
/// <para>Marked <c>Category=Integration</c>: it reads real hardware, so the repo's
/// default <c>--filter "Category!=Integration"</c> excludes it from CI. Both tests
/// no-op on a headless/non-interactive session (ADR-0059 D4) and off-Windows.</para>
/// </summary>
public class LayoutIdentityJoinTests
{
    private static async Task<(MonitorLayout Layout, List<DeviceInfo> Devices)> ReadBothAsync()
    {
        var layout = await MonitorLayout.ReadAsync();
        var devices = new List<DeviceInfo>();
        await foreach (var device in Devices.Enumerate().OfCategory(DeviceCategory.Monitor))
            devices.Add(device);
        return (layout, devices);
    }

    /// <summary>
    /// The contract ADR-0059 D2 and <see cref="MonitorLayoutEntry"/>'s XML doc state:
    /// every layout entry joins to an enumerated <see cref="DeviceInfo"/>.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task EveryLayoutEntry_JoinsADeviceInfo_ByDeviceId()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var (layout, devices) = await ReadBothAsync();
        if (layout.Monitors.IsEmpty)
            return; // Headless / non-interactive session (ADR-0059 D4).

        var unjoined = layout.Monitors
            .Where(entry => !devices.Any(d => d.Id == entry.DeviceId))
            .Select(entry => entry.DeviceId.Value)
            .ToArray();

        Assert.True(
            unjoined.Length == 0,
            $"{unjoined.Length} of {layout.Monitors.Length} layout entries did not join a "
                + $"DeviceInfo (issue #190): {string.Join(", ", unjoined)}");
    }

    /// <summary>
    /// Pins <i>why</i> the typed id is load-bearing: the underlying strings still
    /// differ in case, so this join is only correct because <see cref="DeviceId"/>
    /// compares <see cref="StringComparison.OrdinalIgnoreCase"/>. If this ever
    /// starts finding an ordinal match on every monitor, the OS or a resolver
    /// changed — worth knowing, but it does not make the typed id removable, since
    /// core's own two paths can still disagree.
    /// <para>The assertion is deliberately <c>ordinal &lt;= typed</c> rather than
    /// <c>ordinal &lt; typed</c>: this machine shows 0-of-4 ordinal, but a machine
    /// where the two Windows APIs happen to agree in case is not buggy, and a strict
    /// inequality would fail there for no reason. The name says what is actually
    /// proven. The deterministic proof that the divergence exists and is bridged by
    /// the typed id lives in <c>LayoutIdentityCaseTests</c>, which needs no
    /// hardware and runs in the CI gate tier.</para>
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task TypedJoin_IsAtLeastAsPermissiveAsAnOrdinalStringJoin()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var (layout, devices) = await ReadBothAsync();
        if (layout.Monitors.IsEmpty)
            return;

        int typed = layout.Monitors.Count(e => devices.Any(d => d.Id == e.DeviceId));
        int ordinal = layout.Monitors.Count(
            e => devices.Any(d => string.Equals(d.Id.Value, e.DeviceId.Value, StringComparison.Ordinal)));

        Assert.Equal(layout.Monitors.Length, typed);
        Assert.True(
            ordinal <= typed,
            $"An ordinal string join ({ordinal}) matched more than the typed join ({typed}), "
                + "which should be impossible — DeviceId equality is strictly weaker.");
    }
}
