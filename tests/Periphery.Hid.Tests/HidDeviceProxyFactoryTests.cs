using System;
using System.Threading.Tasks;

namespace Periphery.Hid.Tests;

/// <summary>
/// Argument validation on <see cref="HidDeviceProxy"/>'s two factories. Both
/// bodies were hand-copied from the other proxies until they were hoisted into
/// <c>DeviceProxyBase</c> (issue #70); these pin that the leaf still validates
/// through the shared body rather than silently dropping the check. Each throws
/// before a tracker or watcher is built, so no watcher is started and no
/// hardware is touched.
/// </summary>
public class HidDeviceProxyFactoryTests
{
    [Fact]
    public async Task OpenAsync_NullProfile_ThrowsArgumentNullException()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            HidDeviceProxy.OpenAsync(null!));
    }

    [Fact]
    public void Create_NullTracker_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            HidDeviceProxy.Create(null!));
    }
}
