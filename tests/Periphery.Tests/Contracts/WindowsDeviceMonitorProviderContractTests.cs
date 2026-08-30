using System.Runtime.Versioning;
using Periphery.Windows;

namespace Periphery.Tests;

/// <summary>
/// Runs the full <see cref="DeviceMonitorProviderContractTests"/> suite against
/// <see cref="WindowsDeviceMonitorProvider"/> on Windows.
/// </summary>
/// <remarks>
/// All inherited contract assertions (double-start, dispose idempotency, event
/// subscription) exercise the real cfgmgr32 notification registration path. No
/// hardware events are simulated — lifecycle correctness is verified independently
/// of whether physical devices are present.
/// <para>
/// This class is the template for the Linux and macOS monitor provider contract
/// test subclasses: implement <see cref="DeviceMonitorProviderContractTests.CreateMonitorCore"/>
/// and all assertions are inherited automatically.
/// </para>
/// <para>
/// On non-Windows platforms, a <see cref="FakeDeviceMonitorProvider"/> is returned
/// so the inherited lifecycle assertions pass. The real validation happens on
/// Windows CI runners.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsDeviceMonitorProviderContractTests : DeviceMonitorProviderContractTests
{
    protected override object CreateMonitorCore()
    {
        if (!OperatingSystem.IsWindows())
            return new FakeDeviceMonitorProvider();

        return new WindowsDeviceMonitorProvider();
    }
}
