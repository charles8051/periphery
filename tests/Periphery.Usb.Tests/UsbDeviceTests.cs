using System;
using System.Threading.Tasks;
using Periphery.Usb.Tests.Fakes;

namespace Periphery.Usb.Tests;

public class UsbDeviceTests
{
    private static DeviceInfo TestInfo() => new()
    {
        Id = @"\\?\usb#vid_10c4&pid_8a7e#test#{a5dcbf10-6530-11d2-901f-00c04fb951ed}",
        Name = "Test USB Device",
    };

    [Fact]
    public async Task BulkRead_SlicesResultToBytesActuallyRead()
    {
        var backend = new TestUsbBackend { NextReadByteCount = 5 };
        await using var dev = UsbDevice.CreateForTest(TestInfo(), backend);

        var data = await dev.BulkReadAsync(0x81, count: 64);

        Assert.Equal(5, data.Length);
    }

    [Fact]
    public async Task BulkRead_ReturnsFullBuffer_WhenBackendFillsIt()
    {
        var backend = new TestUsbBackend(); // NextReadByteCount null => fills whole buffer
        await using var dev = UsbDevice.CreateForTest(TestInfo(), backend);

        var data = await dev.BulkReadAsync(0x81, count: 32);

        Assert.Equal(32, data.Length);
    }

    [Fact]
    public async Task ControlTransfer_ForwardsSetup_AndReturnsTransferred()
    {
        var backend = new TestUsbBackend { Fill = 0x42 };
        await using var dev = UsbDevice.CreateForTest(TestInfo(), backend);
        var setup = new UsbControlSetup { RequestType = 0x80, Request = 0x06, Value = 0x0100 };
        var buffer = new byte[18];

        int n = await dev.ControlTransferAsync(setup, buffer);

        Assert.Equal(18, n);
        Assert.Equal(0x06, backend.LastControlSetup.Request);
        Assert.Equal(0x80, backend.LastControlSetup.RequestType);
        Assert.All(buffer, b => Assert.Equal(0x42, b));
    }

    [Fact]
    public void ClaimInterface_DelegatesToBackend()
    {
        var backend = new TestUsbBackend();
        var dev = UsbDevice.CreateForTest(TestInfo(), backend);

        dev.ClaimInterface(3);

        Assert.Equal((byte)3, backend.LastClaimedInterface);
    }

    [Fact]
    public void Descriptors_PassThroughFromBackend()
    {
        var backend = new TestUsbBackend();
        var dev = UsbDevice.CreateForTest(TestInfo(), backend);

        Assert.Equal(new HardwareId(0x10C4), dev.Descriptor.VendorId);
        Assert.Equal(new HardwareId(0x8A7E), dev.Descriptor.ProductId);
        Assert.Equal((byte)1, dev.Configuration.ConfigurationValue);
    }

    [Fact]
    public async Task Transfers_ThrowObjectDisposed_AfterDispose()
    {
        var backend = new TestUsbBackend();
        var dev = UsbDevice.CreateForTest(TestInfo(), backend);
        await dev.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => dev.BulkWriteAsync(0x01, new byte[] { 1, 2, 3 }));
        Assert.Throws<ObjectDisposedException>(() => dev.ClaimInterface(0));
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotent_AndDisposesBackendOnce()
    {
        var backend = new TestUsbBackend();
        var dev = UsbDevice.CreateForTest(TestInfo(), backend);

        await dev.DisposeAsync();
        await dev.DisposeAsync();

        Assert.Equal(1, backend.DisposeCount);
    }
}

public class UsbDescriptorTests
{
    [Theory]
    [InlineData((byte)0x81, UsbTransferDirection.DeviceToHost)]
    [InlineData((byte)0x01, UsbTransferDirection.HostToDevice)]
    public void Endpoint_Direction_DecodesBit7(byte address, UsbTransferDirection expected)
    {
        var ep = new UsbEndpointDescriptor
        {
            EndpointAddress = address,
            TransferType = UsbTransferType.Bulk,
            MaxPacketSize = 64,
        };

        Assert.Equal(expected, ep.Direction);
    }

    [Theory]
    [InlineData((byte)0x80, UsbTransferDirection.DeviceToHost)]
    [InlineData((byte)0x00, UsbTransferDirection.HostToDevice)]
    public void ControlSetup_Direction_DecodesBit7(byte requestType, UsbTransferDirection expected)
    {
        var setup = new UsbControlSetup { RequestType = requestType, Request = 0x06 };

        Assert.Equal(expected, setup.Direction);
    }
}
