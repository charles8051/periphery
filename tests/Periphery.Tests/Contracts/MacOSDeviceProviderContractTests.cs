using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Threading;
using Periphery.MacOS;

namespace Periphery.Tests;

/// <summary>
/// Runs the <see cref="DeviceProviderContractTests"/> suite against
/// <see cref="MacOSDeviceProvider"/> on macOS.
/// </summary>
/// <remarks>
/// <see cref="DeviceProviderContractTests.Enumerate"/> ignores the <c>seeds</c>
/// parameter — the real provider returns whatever devices are present on the
/// host machine. <see cref="HardwareDeviceProviderContractTests"/> carries the
/// consequences of that: seed-count assertions are skipped, and the
/// cross-enumeration comparisons run in the <c>Integration</c> tier.
/// <para>
/// Single-enumeration invariants (non-null ID, defined category, empty-result
/// path, cancellation) exercise the real IOKit enumeration path, pass on any
/// macOS machine, and stay in the default (PR/release gate) tier.
/// </para>
/// <para>
/// On non-macOS platforms, <see cref="Enumerate"/> yields no devices so the
/// inherited contract assertions pass vacuously. The real validation happens on
/// macOS CI runners.
/// </para>
/// </remarks>
[SupportedOSPlatform("macos")]
public sealed class MacOSDeviceProviderContractTests : HardwareDeviceProviderContractTests
{
    protected override async IAsyncEnumerable<DeviceInfo> Enumerate(
        DeviceInfo[] seeds, DeviceFilter filter,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!OperatingSystem.IsMacOS())
        {
            ct.ThrowIfCancellationRequested();
            yield break;
        }

        // seeds are ignored — the real provider queries actual hardware.
        await foreach (var d in new MacOSDeviceProvider().EnumerateAsync(filter, ct))
            yield return d;
    }
}
