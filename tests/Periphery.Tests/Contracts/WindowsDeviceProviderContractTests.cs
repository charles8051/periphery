using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Threading;
using Periphery.Windows;

namespace Periphery.Tests;

/// <summary>
/// Runs the <see cref="DeviceProviderContractTests"/> suite against
/// <see cref="WindowsDeviceProvider"/> on Windows.
/// </summary>
/// <remarks>
/// <see cref="DeviceProviderContractTests.Enumerate"/> ignores the <c>seeds</c>
/// parameter — the real provider returns whatever devices are present on the
/// host machine. <see cref="HardwareDeviceProviderContractTests"/> carries the
/// consequences of that: seed-count assertions are skipped, and the
/// cross-enumeration comparisons run in the <c>Integration</c> tier.
/// <para>
/// Single-enumeration invariants (non-null ID, defined category, empty-result
/// path, cancellation) exercise the real SetupAPI/cfgmgr32 enumeration path,
/// pass on any Windows machine, and stay in the default (PR/release gate) tier.
/// </para>
/// <para>
/// Adding a provider for another OS is one method: subclass
/// <see cref="HardwareDeviceProviderContractTests"/>, implement
/// <see cref="DeviceProviderContractTests.Enumerate"/>, and every applicable
/// assertion — in its correct tier — is inherited automatically.
/// </para>
/// <para>
/// On non-Windows platforms, <see cref="Enumerate"/> yields no devices so the
/// inherited contract assertions pass vacuously. The real validation happens on
/// Windows CI runners.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsDeviceProviderContractTests : HardwareDeviceProviderContractTests
{
    protected override async IAsyncEnumerable<DeviceInfo> Enumerate(
        DeviceInfo[] seeds, DeviceFilter filter,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            ct.ThrowIfCancellationRequested();
            yield break;
        }

        // seeds are ignored — the real provider queries actual hardware.
        await foreach (var d in new WindowsDeviceProvider().EnumerateAsync(filter, ct))
            yield return d;
    }
}
