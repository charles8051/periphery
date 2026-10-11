using Microsoft.Extensions.Time.Testing;
using Periphery.Usb.Browser.Tests.Fakes;

namespace Periphery.Usb.Browser.Tests;

/// <summary>
/// The WebUSB backend over a fake <see cref="IWebUsbDevice"/>: the shell that feeds the endpoint
/// machines (ADR-0092 D6, D7). No browser and no wall clock: a transfer settles when the test
/// completes it, and a deadline expires when the test advances a fake clock.
/// </summary>
public class WebUsbBackendTests
{
    private const string Id = "webusb:10C4:8A7E:TEST";

    // Bounds a failure only. Every awaited task here completes as soon as the test acts.
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private static async Task<(FakeWebUsbDevice, WebUsbBackend)> OpenAsync(FakeWebUsbDevice? device = null)
    {
        device ??= new FakeWebUsbDevice();
        var backend = await WebUsbBackend.OpenAsync(device, Id, CancellationToken.None);
        return (device, backend);
    }

    private static byte[] Bytes(int count, int start = 0)
        => Enumerable.Range(start, count).Select(i => (byte)i).ToArray();

    private static WebUsbRejection Rejection(string name) => new(name, "browser message");

    // ── Open ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Open_claims_interface_0_and_reads_both_descriptors()
    {
        var (device, backend) = await OpenAsync();

        Assert.Equal(
            ["open", "claimInterface(0)", "getDescriptor(0x0100, 18)", "getDescriptor(0x0200, 9)", "getDescriptor(0x0200, 46)"],
            device.Log);
        Assert.Equal(new HardwareId(0x10C4), backend.DeviceDescriptor.VendorId);
        Assert.Equal(new HardwareId(0x8A7E), backend.DeviceDescriptor.ProductId);
        Assert.Equal(100, backend.Configuration.MaxPowerMilliamps);
        Assert.Equal(
            new byte[] { 0x81, 0x82, 0x01, 0x02 },
            Assert.Single(backend.Configuration.Interfaces).Endpoints.Select(e => e.EndpointAddress));
    }

    [Fact]
    public async Task Open_selects_the_first_configuration_when_none_is_active()
    {
        var device = new FakeWebUsbDevice { ActiveConfigurationValue = null, ConfigurationValues = [3, 4] };

        await OpenAsync(device);

        Assert.Equal(["open", "selectConfiguration(3)", "claimInterface(0)"], device.Log.Take(3));
    }

    [Fact]
    public async Task Open_reads_the_active_configurations_descriptor()
    {
        var device = new FakeWebUsbDevice { ActiveConfigurationValue = 2, ConfigurationValues = [1, 2] };

        await OpenAsync(device);

        Assert.DoesNotContain(device.Log, l => l.StartsWith("selectConfiguration", StringComparison.Ordinal));
        Assert.Contains("getDescriptor(0x0201, 9)", device.Log);
    }

    [Fact]
    public async Task An_open_the_browser_denies_is_access_denied_and_closes_nothing()
    {
        var device = new FakeWebUsbDevice { RejectOpen = Rejection("SecurityError") };

        await Assert.ThrowsAsync<UsbAccessDeniedException>(() => OpenAsync(device));

        Assert.DoesNotContain("close", device.Log);
    }

    [Fact]
    public async Task A_claim_another_process_holds_is_access_denied_and_closes_the_device()
    {
        var device = new FakeWebUsbDevice { RejectClaim = Rejection("NetworkError") };

        await Assert.ThrowsAsync<UsbAccessDeniedException>(() => OpenAsync(device));

        Assert.Equal("close", device.Log.Last());
    }

    [Fact]
    public async Task A_short_device_descriptor_fails_the_open_and_closes_the_device()
    {
        var device = new FakeWebUsbDevice { DeviceDescriptor = FakeWebUsbDevice.TreehopperDevice[..8] };

        await Assert.ThrowsAsync<UsbException>(() => OpenAsync(device));

        Assert.Equal("close", device.Log.Last());
    }

    [Fact]
    public async Task An_open_already_cancelled_does_nothing()
    {
        var device = new FakeWebUsbDevice();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => WebUsbBackend.OpenAsync(device, Id, new CancellationToken(canceled: true)));

        Assert.Empty(device.Log);
    }

    [Fact]
    public async Task Only_interface_0_can_be_claimed()
    {
        var (_, backend) = await OpenAsync();

        backend.ClaimInterface(0);
        Assert.Throws<NotSupportedException>(() => backend.ClaimInterface(1));
    }

    // ── Bulk IN ────────────────────────────────────────────────────────

    [Fact]
    public async Task A_read_asks_for_whole_packets_and_gets_the_bytes_the_device_sent()
    {
        var (device, backend) = await OpenAsync();
        var buffer = new byte[10];

        var read = backend.BulkReadAsync(0x81, buffer, CancellationToken.None);
        var transfer = await device.NextTransferAsync().AsTask().WaitAsync(Bound);
        transfer.CompleteIn(Bytes(10));

        Assert.Equal(10, await read.WaitAsync(Bound));
        Assert.Equal(("transferIn", (byte)1, 64), (transfer.Kind, transfer.EndpointNumber, transfer.Length));
        Assert.Equal(Bytes(10), buffer);
    }

    [Fact]
    public async Task Bytes_beyond_the_buffer_are_held_for_the_next_read()
    {
        var (device, backend) = await OpenAsync();
        var first = new byte[10];
        var second = new byte[64];

        var read = backend.BulkReadAsync(0x81, first, CancellationToken.None);
        (await device.NextTransferAsync().AsTask().WaitAsync(Bound)).CompleteIn(Bytes(64));
        await read.WaitAsync(Bound);

        int count = await backend.BulkReadAsync(0x81, second, CancellationToken.None).WaitAsync(Bound);

        Assert.Equal(54, count);
        Assert.Equal(Bytes(54, start: 10), second[..54]);
        Assert.Equal(1, device.TransfersIssued);
    }

    [Fact]
    public async Task A_cancelled_read_leaves_its_transfer_for_the_next_read_to_take_over()
    {
        var (device, backend) = await OpenAsync();
        using var cts = new CancellationTokenSource();

        var abandoned = backend.BulkReadAsync(0x82, new byte[64], cts.Token);
        var transfer = await device.NextTransferAsync().AsTask().WaitAsync(Bound);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned.WaitAsync(Bound));

        var buffer = new byte[64];
        var takeover = backend.BulkReadAsync(0x82, buffer, CancellationToken.None);
        transfer.CompleteIn(Bytes(5));

        Assert.Equal(5, await takeover.WaitAsync(Bound));
        Assert.Equal(1, device.TransfersIssued);
    }

    [Fact]
    public async Task A_stall_on_a_bulk_endpoint_is_cleared_then_reported()
    {
        var (device, backend) = await OpenAsync();

        var read = backend.BulkReadAsync(0x81, new byte[64], CancellationToken.None);
        (await device.NextTransferAsync().AsTask().WaitAsync(Bound)).CompleteIn([], status: "stall");

        await Assert.ThrowsAsync<UsbTransferException>(() => read.WaitAsync(Bound));
        Assert.Contains("clearHalt(in, 1)", device.Log);
    }

    [Fact]
    public async Task A_disconnect_during_a_read_is_a_removal()
    {
        var (device, backend) = await OpenAsync();

        var read = backend.BulkReadAsync(0x81, new byte[64], CancellationToken.None);
        (await device.NextTransferAsync().AsTask().WaitAsync(Bound)).Reject("NotFoundError", "The device was disconnected.");

        var ex = await Assert.ThrowsAsync<UsbDeviceRemovedException>(() => read.WaitAsync(Bound));
        Assert.Equal(Id, ex.DeviceId);
    }

    [Theory]
    [InlineData((byte)0x83)]
    [InlineData((byte)0x01)]
    public async Task A_read_on_an_endpoint_that_is_not_an_IN_endpoint_of_interface_0_is_refused(byte endpoint)
    {
        var (device, backend) = await OpenAsync();

        Assert.Throws<UsbException>(() => { _ = backend.BulkReadAsync(endpoint, new byte[64], CancellationToken.None); });

        Assert.Equal(0, device.TransfersIssued);
    }

    // ── Bulk OUT ───────────────────────────────────────────────────────

    [Fact]
    public async Task A_write_returns_the_bytes_written()
    {
        var (device, backend) = await OpenAsync();

        var write = backend.BulkWriteAsync(0x02, Bytes(7), CancellationToken.None);
        var transfer = await device.NextTransferAsync().AsTask().WaitAsync(Bound);
        transfer.CompleteOut(7);

        Assert.Equal(7, await write.WaitAsync(Bound));
        Assert.Equal(("transferOut", (byte)2), (transfer.Kind, transfer.EndpointNumber));
        Assert.Equal(Bytes(7), transfer.Data);
    }

    [Fact]
    public async Task A_write_after_an_abandoned_one_waits_for_it_to_settle()
    {
        var (device, backend) = await OpenAsync();
        using var cts = new CancellationTokenSource();

        var abandoned = backend.BulkWriteAsync(0x02, Bytes(4), cts.Token);
        var first = await device.NextTransferAsync().AsTask().WaitAsync(Bound);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned.WaitAsync(Bound));

        var next = backend.BulkWriteAsync(0x02, Bytes(4, start: 100), CancellationToken.None);
        Assert.False(device.HasUntakenTransfer);

        first.CompleteOut(4);
        var second = await device.NextTransferAsync().AsTask().WaitAsync(Bound);
        second.CompleteOut(4);

        Assert.Equal(4, await next.WaitAsync(Bound));
        Assert.Equal(Bytes(4, start: 100), second.Data);
    }

    [Theory]
    [InlineData((byte)0x00)]
    [InlineData((byte)0x81)]
    [InlineData((byte)0x03)]
    public async Task A_write_on_an_endpoint_that_is_not_an_OUT_endpoint_of_interface_0_is_refused(byte endpoint)
    {
        var (device, backend) = await OpenAsync();

        Assert.Throws<UsbException>(() => { _ = backend.BulkWriteAsync(endpoint, Bytes(1), CancellationToken.None); });

        Assert.Equal(0, device.TransfersIssued);
    }

    // ── Control ────────────────────────────────────────────────────────

    private static readonly UsbControlSetup VendorIn = new() { RequestType = 0xC0, Request = 0x42, Value = 1, Index = 0 };
    private static readonly UsbControlSetup VendorOut = new() { RequestType = 0x40, Request = 0x43, Value = 1, Index = 0 };

    [Fact]
    public async Task A_control_read_copies_the_reply_into_the_buffer()
    {
        var (device, backend) = await OpenAsync();
        var buffer = new byte[6];

        var control = backend.ControlTransferAsync(VendorIn, buffer, CancellationToken.None);
        var transfer = await device.NextTransferAsync().AsTask().WaitAsync(Bound);
        transfer.CompleteIn(Bytes(4));

        Assert.Equal(4, await control.WaitAsync(Bound));
        Assert.Equal(("controlTransferIn", 6), (transfer.Kind, transfer.Length));
        Assert.Equal(Bytes(4), buffer[..4]);
    }

    [Fact]
    public async Task A_control_write_returns_the_bytes_written()
    {
        var (device, backend) = await OpenAsync();

        var control = backend.ControlTransferAsync(VendorOut, Bytes(3), CancellationToken.None);
        var transfer = await device.NextTransferAsync().AsTask().WaitAsync(Bound);
        transfer.CompleteOut(3);

        Assert.Equal(3, await control.WaitAsync(Bound));
        Assert.Equal(Bytes(3), transfer.Data);
    }

    [Fact]
    public async Task A_control_stall_is_reported_without_a_clear_halt()
    {
        var (device, backend) = await OpenAsync();

        var control = backend.ControlTransferAsync(VendorIn, new byte[6], CancellationToken.None);
        (await device.NextTransferAsync().AsTask().WaitAsync(Bound)).CompleteIn([], status: "stall");

        await Assert.ThrowsAsync<UsbTransferException>(() => control.WaitAsync(Bound));
        Assert.DoesNotContain(device.Log, l => l.StartsWith("clearHalt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_abandoned_control_reads_reply_never_reaches_the_next_control_transfer()
    {
        var (device, backend) = await OpenAsync();
        using var cts = new CancellationTokenSource();

        var abandoned = backend.ControlTransferAsync(VendorIn, new byte[6], cts.Token);
        var first = await device.NextTransferAsync().AsTask().WaitAsync(Bound);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned.WaitAsync(Bound));

        var buffer = new byte[6];
        var next = backend.ControlTransferAsync(VendorIn, buffer, CancellationToken.None);
        first.CompleteIn([0xAA, 0xAA, 0xAA, 0xAA, 0xAA, 0xAA]);
        var second = await device.NextTransferAsync().AsTask().WaitAsync(Bound);
        second.CompleteIn([1, 2]);

        Assert.Equal(2, await next.WaitAsync(Bound));
        Assert.Equal(new byte[] { 1, 2, 0, 0, 0, 0 }, buffer);
    }

    // ── Disposal ───────────────────────────────────────────────────────

    [Fact]
    public async Task Disposal_fails_a_waiting_read_then_closes_the_device()
    {
        var (device, backend) = await OpenAsync();

        var read = backend.BulkReadAsync(0x81, new byte[64], CancellationToken.None);
        await device.NextTransferAsync().AsTask().WaitAsync(Bound);

        await backend.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => read.WaitAsync(Bound));
        Assert.Equal("close", device.Log.Last());
        Assert.Throws<ObjectDisposedException>(() => { _ = backend.BulkReadAsync(0x81, new byte[64], CancellationToken.None); });
    }

    [Fact]
    public async Task Disposal_fails_a_write_waiting_behind_an_abandoned_one()
    {
        var (device, backend) = await OpenAsync();
        using var cts = new CancellationTokenSource();
        var abandoned = backend.BulkWriteAsync(0x02, Bytes(4), cts.Token);
        await device.NextTransferAsync().AsTask().WaitAsync(Bound);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned.WaitAsync(Bound));
        var waiting = backend.BulkWriteAsync(0x02, Bytes(4), CancellationToken.None);

        await backend.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => waiting.WaitAsync(Bound));
        Assert.Equal(1, device.TransfersIssued);
    }

    // ── Through UsbDevice ──────────────────────────────────────────────

    [Fact]
    public async Task A_read_that_times_out_in_UsbDevice_leaves_its_bytes_for_the_next_read()
    {
        // The "abandon and take over" gate of ADR-0092, on a fake device and a fake clock.
        var timeout = TimeSpan.FromMilliseconds(100);
        var time = new FakeTimeProvider();
        var (device, backend) = await OpenAsync();
        await using var usb = UsbDevice.CreateForTest(new DeviceInfo { Id = Id }, backend, transferTimeout: timeout, timeProvider: time);

        var first = usb.BulkReadAsync(0x82, 64);
        var transfer = await device.NextTransferAsync().AsTask().WaitAsync(Bound);
        time.Advance(timeout);
        await Assert.ThrowsAsync<UsbTimeoutException>(() => first.WaitAsync(Bound));

        var second = usb.BulkReadAsync(0x82, 64);
        transfer.CompleteIn(Bytes(9));

        Assert.Equal(Bytes(9), await second.WaitAsync(Bound));
        Assert.Equal(1, device.TransfersIssued);
    }

    [Fact]
    public async Task UsbDevice_opens_through_the_host_factory()
    {
        var (_, backend) = await OpenAsync();
        var info = new DeviceInfo { Id = Id };

        await using var usb = await UsbDevice.OpenThroughHostAsync(
            (_, _) => Task.FromResult<IUsbBackend>(backend), info, Timeout.InfiniteTimeSpan, logger: null, CancellationToken.None);

        Assert.Same(info, usb.DeviceInfo);
        Assert.Equal(new HardwareId(0x8A7E), usb.Descriptor.ProductId);
    }

    [Fact]
    public async Task Without_a_host_factory_UsbDevice_names_the_package_to_initialise()
    {
        var ex = await Assert.ThrowsAsync<PlatformNotSupportedException>(() => UsbDevice.OpenThroughHostAsync(
            null, new DeviceInfo { Id = Id }, Timeout.InfiniteTimeSpan, logger: null, CancellationToken.None));

        Assert.Contains("BrowserUsb.InitializeAsync", ex.Message);
    }
}
