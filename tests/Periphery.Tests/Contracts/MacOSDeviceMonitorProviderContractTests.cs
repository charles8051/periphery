using System.Runtime.Versioning;
using Periphery.MacOS;

namespace Periphery.Tests;

/// <summary>
/// Runs the full <see cref="DeviceMonitorProviderContractTests"/> suite against
/// <see cref="MacOSDeviceMonitorProvider"/> on macOS.
/// </summary>
/// <remarks>
/// All inherited contract assertions (double-start, dispose idempotency, event
/// subscription) exercise the real IOKit notification registration path. No
/// hardware events are simulated — lifecycle correctness is verified independently
/// of whether physical devices are present.
/// <para>
/// On non-macOS platforms, a <see cref="FakeDeviceMonitorProvider"/> is returned
/// so the inherited lifecycle assertions pass. The real validation happens on
/// macOS CI runners.
/// </para>
/// </remarks>
[SupportedOSPlatform("macos")]
public sealed class MacOSDeviceMonitorProviderContractTests : DeviceMonitorProviderContractTests
{
    protected override object CreateMonitorCore()
    {
        if (!OperatingSystem.IsMacOS())
            return new FakeDeviceMonitorProvider();

        return new MacOSDeviceMonitorProvider();
    }
}
