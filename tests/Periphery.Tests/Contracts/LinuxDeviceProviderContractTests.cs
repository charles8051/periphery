using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Threading;
using Periphery.Linux;

namespace Periphery.Tests;

/// <summary>
/// Runs the <see cref="DeviceProviderContractTests"/> suite against
/// <see cref="LinuxDeviceProvider"/> on Linux.
/// </summary>
/// <remarks>
/// <see cref="DeviceProviderContractTests.Enumerate"/> ignores the <c>seeds</c>
/// parameter — the real provider returns whatever devices are present on the
/// host machine. <see cref="HardwareDeviceProviderContractTests"/> carries the
/// consequences of that: seed-count assertions are skipped, and the
/// cross-enumeration comparisons run in the <c>Integration</c> tier.
/// <para>
/// Single-enumeration invariants (non-null ID, defined category, empty-result
/// path, cancellation) exercise the real libudev enumeration path, pass on any
/// Linux machine with <c>libudev.so.1</c> available, and stay in the default
/// (PR/release gate) tier.
/// </para>
/// <para>
/// On non-Linux platforms, <see cref="Enumerate"/> yields no devices so the
/// inherited contract assertions pass vacuously. The real validation happens on
/// Linux CI runners.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed class LinuxDeviceProviderContractTests : HardwareDeviceProviderContractTests
{
    protected override async IAsyncEnumerable<DeviceInfo> Enumerate(
        DeviceInfo[] seeds, DeviceFilter filter,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!OperatingSystem.IsLinux())
        {
            ct.ThrowIfCancellationRequested();
            yield break;
        }

        // seeds are ignored — the real provider queries actual hardware.
        await foreach (var d in new LinuxDeviceProvider().EnumerateAsync(filter, ct))
            yield return d;
    }
}
