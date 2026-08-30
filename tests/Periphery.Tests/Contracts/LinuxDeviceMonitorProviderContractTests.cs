using System.Runtime.Versioning;
using Periphery.Linux;

namespace Periphery.Tests;

/// <summary>
/// Runs the full <see cref="DeviceMonitorProviderContractTests"/> suite against
/// <see cref="LinuxDeviceMonitorProvider"/> on Linux.
/// </summary>
/// <remarks>
/// All inherited contract assertions (double-start, dispose idempotency, event
/// subscription) exercise the real libudev monitor registration path. No
/// hardware events are simulated — lifecycle correctness is verified independently
/// of whether physical devices are present.
/// <para>
/// On non-Linux platforms, a <see cref="FakeDeviceMonitorProvider"/> is returned
/// so the inherited lifecycle assertions pass. The real validation happens on
/// Linux CI runners.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed class LinuxDeviceMonitorProviderContractTests : DeviceMonitorProviderContractTests
{
    protected override object CreateMonitorCore()
    {
        if (!OperatingSystem.IsLinux())
            return new FakeDeviceMonitorProvider();

        return new LinuxDeviceMonitorProvider();
    }
}
