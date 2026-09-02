using System.Runtime.CompilerServices;

namespace Periphery.Tests;

/// <summary>
/// Fake device provider for deterministic unit testing.
/// Returns a predefined set of devices without OS-level dependencies.
/// </summary>
internal class FakeDeviceProvider : IDeviceProvider
{
    private readonly List<DeviceInfo> _devices;

    /// <summary>
    /// When set, enumeration throws this after yielding
    /// <see cref="FailAfterYielding"/> devices — the snapshot half of a start
    /// attempt, as opposed to the registration half. Cleared once it throws, so
    /// a retry enumerates normally.
    /// </summary>
    public Exception? FailEnumerationWith { get; set; }

    /// <summary>How many devices to yield before <see cref="FailEnumerationWith"/> throws.</summary>
    public int FailAfterYielding { get; set; }

    /// <summary>
    /// How many devices this provider actually produced. The point of a
    /// streaming query is that a consumer which stops early leaves this below
    /// the total, so asserting on a query's results alone would not catch a
    /// regression back to a full walk.
    /// </summary>
    public int Yielded { get; private set; }

    /// <summary>True once enumeration reached the end of the device list.</summary>
    public bool EnumeratedToCompletion { get; private set; }

    public FakeDeviceProvider(params DeviceInfo[] devices)
    {
        _devices = devices.ToList();
    }

    public async IAsyncEnumerable<DeviceInfo> EnumerateAsync(
        DeviceFilter filter,
        [EnumeratorCancellation] CancellationToken ct = default
    )
    {
        ct.ThrowIfCancellationRequested();
        await Task.Yield();

        int yielded = 0;

        foreach (var device in _devices)
        {
            ct.ThrowIfCancellationRequested();

            if (FailEnumerationWith is { } fault && yielded >= FailAfterYielding)
            {
                FailEnumerationWith = null;
                throw fault;
            }

            yielded++;

            // Simulate push-down: filter by category if specified
            if (
                filter.Category.HasValue
                && filter.Category.Value != DeviceCategory.All
                && device.Category != filter.Category.Value
            )
                continue;

            Yielded++;
            yield return device;
        }

        EnumeratedToCompletion = true;
    }

    public static FakeDeviceProvider WithUsbDevices() =>
        new(
            new DeviceInfo
            {
                Id = "USB\\VID_046D&PID_C077\\1",
                Name = "Logitech Mouse",
                Category = DeviceCategory.Usb,
                Manufacturer = "Logitech",
                VendorId = new HardwareId(0x046D),
                ProductId = new HardwareId(0xC077),
                IsActive = true,
                Status = DeviceStatus.OK,
            },
            new DeviceInfo
            {
                Id = "USB\\VID_046D&PID_C52B\\2",
                Name = "Logitech Keyboard",
                Category = DeviceCategory.Usb,
                Manufacturer = "Logitech",
                VendorId = new HardwareId(0x046D),
                ProductId = new HardwareId(0xC52B),
                IsActive = true,
                Status = DeviceStatus.OK,
            },
            new DeviceInfo
            {
                Id = "USB\\VID_8087&PID_0AAA\\3",
                Name = "Intel Bluetooth Adapter",
                Category = DeviceCategory.Usb,
                Manufacturer = "Intel Corporation",
                VendorId = new HardwareId(0x8087),
                ProductId = new HardwareId(0x0AAA),
                IsActive = true,
                Status = DeviceStatus.OK,
            }
        );

    public static FakeDeviceProvider WithMixedDevices() =>
        new(
            new DeviceInfo
            {
                Id = "USB\\VID_046D&PID_C077\\1",
                Name = "Logitech Mouse",
                Category = DeviceCategory.Usb,
                Manufacturer = "Logitech",
                IsActive = true,
                Status = DeviceStatus.OK,
            },
            new DeviceInfo
            {
                Id = "HID\\VID_046D&PID_C077\\2",
                Name = "HID-compliant mouse",
                Category = DeviceCategory.Hid,
                Manufacturer = "Logitech",
                IsActive = true,
                Status = DeviceStatus.OK,
            },
            new DeviceInfo
            {
                Id = "NET\\{12345678-1234-1234-1234-123456789012}",
                Name = "Ethernet Adapter",
                Category = DeviceCategory.Network,
                Manufacturer = "Intel",
                IsActive = true,
                Status = DeviceStatus.OK,
            },
            new DeviceInfo
            {
                Id = "DISPLAY\\Default_Monitor\\1",
                Name = "Generic PnP Monitor",
                Category = DeviceCategory.Display,
                Manufacturer = "Generic",
                IsActive = true,
                Status = DeviceStatus.OK,
            },
            new DeviceInfo
            {
                Id = "USB\\VID_XXXX&PID_YYYY\\5",
                Name = "Disconnected Device",
                Category = DeviceCategory.Usb,
                Manufacturer = "Unknown",
                IsActive = false,
                Status = DeviceStatus.Error,
            }
        );

    public static FakeDeviceProvider Empty() => new();
}
