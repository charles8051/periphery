namespace Periphery.Tests;

internal static class SessionHostTestHelpers
{
    internal sealed class FakeSession
    {
        public int Id { get; init; }
    }

    internal static DeviceInfo MakeDevice(
        string id = "TEST\\VID_0001&PID_0002\\1",
        bool isActive = true) => new()
    {
        Id = id,
        Name = "Test Device",
        Category = DeviceCategory.Usb,
        IsActive = isActive,
        VendorId = new HardwareId(0x0001),
        ProductId = new HardwareId(0x0002),
    };

    internal static (DeviceTracker tracker, DeviceWatcher watcher) CreateTestInfra()
    {
        var tracker = new DeviceTracker(new DeviceFilter());
        var watcher = Devices.Watch().AddTracker(tracker);
        return (tracker, watcher);
    }

    internal static void SimulateConnect(DeviceTracker tracker, DeviceInfo device)
    {
        tracker.OnDeviceAppeared(device);
        tracker.OnDeviceConnected(device);
    }

    internal static void SimulateDisconnect(DeviceTracker tracker, DeviceInfo device)
    {
        var inactive = device with { IsActive = false };
        tracker.OnDeviceDisconnected(inactive);
        tracker.OnDeviceDisappeared(inactive);
    }

    internal static async Task<TStatus> WaitForStatusAsync<TSession, TStatus>(
        DeviceSessionHost<TSession> host,
        TimeSpan? timeout = null)
        where TSession : class
        where TStatus : HostStatus<TSession>
    {
        if (host.Status is TStatus existing)
            return existing;

        var tcs = new TaskCompletionSource<TStatus>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        EventHandler<HostStatus<TSession>>? handler = null;
        handler = (_, status) =>
        {
            if (status is TStatus typed)
            {
                host.StatusChanged -= handler;
                tcs.TrySetResult(typed);
            }
        };

        host.StatusChanged += handler;
        return await tcs.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(5));
    }
}
