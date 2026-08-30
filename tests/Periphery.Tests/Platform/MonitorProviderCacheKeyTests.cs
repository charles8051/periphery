using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Periphery.Tests.Platform;

/// <summary>
/// Guardrail for issue #231 on the device-monitor providers' <c>_lastKnownDevices</c>
/// caches. Each provider keeps a snapshot of the devices it last saw and raises
/// appeared / disappeared / property-changed by diffing against it. If that cache is
/// keyed case-<b>sensitively</b>, a device that re-enumerates with different casing in
/// its instance id (observed on this bench: <c>…\CDYHINBH</c> -> <c>…\cDYhINBh</c>, same
/// board, same port, 224 ms apart) splits into two entries and the diff emits a phantom
/// disappeared/appeared pair for a device that never left.
///
/// <para><b>Where it is live, and where it is not.</b> On Windows the id is a PnP instance
/// id, which does flip case — that is the observed failure, and that provider already held
/// the invariant in an explicit <c>StringComparer.OrdinalIgnoreCase</c>. On Linux the id is
/// the udev syspath and on macOS it is a decimal IOKit registry entry id, so their ordinal
/// comparers were not producing the bug today. What this guardrail pins is that the
/// invariant lives in the <see cref="DeviceId"/> key <i>type</i> for all three: Windows
/// carried it in a comparer argument and neither of the other two picked it up, so the one
/// provider that needs it is the one provider that has it, by accident of who wrote what
/// first. Key type is not forgettable; a comparer argument demonstrably is.</para>
///
/// <para>This asserts on the field's key <i>type</i> rather than on behaviour because the
/// providers need a live OS device pump to exercise; the key type is precisely what
/// regressed, and a reflection check cannot be satisfied by a passing-but-wrong comparer.
/// Runs in the gate tier (no <c>Category=Integration</c>).</para>
/// </summary>
public class MonitorProviderCacheKeyTests
{
    public static TheoryData<string> ProviderTypeNames => new()
    {
        "Periphery.Windows.WindowsDeviceMonitorProvider",
        "Periphery.Linux.LinuxDeviceMonitorProvider",
        "Periphery.MacOS.MacOSDeviceMonitorProvider",
    };

    [Theory]
    [MemberData(nameof(ProviderTypeNames))]
    public void LastKnownDevicesCache_IsKeyedByDeviceId_NotRawString(string typeName)
    {
        // All three providers live in the same assembly as DeviceInfo (src/Periphery); there
        // are no separate per-platform assemblies. throwOnError is deliberate: if a provider is
        // ever moved out, this must fail loudly rather than silently skip that platform.
        var type = typeof(DeviceInfo).Assembly.GetType(typeName, throwOnError: true)!;

        var field = type
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            // SingleOrDefault, not FirstOrDefault: if a second cache field ever appears whose
            // name matches, this must fail loudly rather than silently guard only one of them.
            .SingleOrDefault(f => f.Name.Contains("lastKnownDevices", StringComparison.OrdinalIgnoreCase));

        Assert.True(field is not null,
            $"{typeName} has no _lastKnownDevices field — if the cache was renamed or removed, "
            + "update this guardrail rather than deleting it; the hazard it pins is real.");

        Assert.True(
            field!.FieldType.IsGenericType
            && field.FieldType.GetGenericTypeDefinition() == typeof(Dictionary<,>),
            $"{typeName}.{field.Name} is {field.FieldType}, expected a Dictionary<,>.");

        var keyType = field.FieldType.GetGenericArguments()[0];

        Assert.True(keyType == typeof(DeviceId),
            $"{typeName}.{field.Name} is keyed by {keyType.Name}, not DeviceId. A device instance "
            + "id keyed as a raw string compares ordinally unless every construction site "
            + "remembers StringComparer.OrdinalIgnoreCase — which only the Windows provider "
            + "did (issue #231). Key it by DeviceId so the invariant cannot be dropped.");
    }

    [Fact]
    public void DeviceIdKeyedDictionary_NeedsNoComparer_ToBeCaseInsensitive()
    {
        // Why the assertion above is sufficient: a Dictionary<DeviceId,_> with no comparer
        // argument is already case-insensitive, because DeviceId hashes OrdinalIgnoreCase.
        var cache = new Dictionary<DeviceId, string>
        {
            [new DeviceId(@"USB\VID_10C4&PID_8A7E\CDYHINBH")] = "first",
        };

        cache[new DeviceId(@"USB\VID_10C4&PID_8A7E\cDYhINBh")] = "second";

        Assert.Single(cache);
        Assert.Equal("second", cache.Single().Value);
    }
}
