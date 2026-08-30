using Periphery.Camera.Testing;
using Periphery.Camera.Tests.Fakes;

namespace Periphery.Camera.Tests;

/// <summary>
/// Verifies the mid-capture device loss contract: CameraDeviceLostException
/// is thrown on the active capture operation; capture never silently completes.
/// </summary>
/// <remarks>
/// Because the producer reads from the backend on a background thread, there
/// may be buffered frames between the moment the fault is injected and the
/// moment the consumer sees the exception. Tests assert that the exception
/// is eventually thrown, not the exact frame count before it.
/// </remarks>
[Collection("Camera")]
public sealed class DeviceLossTests
{
    [Fact]
    public async Task CaptureAsync_DeviceLost_ThrowsCameraDeviceLostException()
    {
        var backend = new InMemoryCameraBackend();
        await using var session = await TestHelpers.CreateSessionWithBackend(backend);

        int count = 0;
        await Assert.ThrowsAsync<CameraDeviceLostException>(async () =>
        {
            await foreach (var frame in session.CaptureAsync())
            {
                frame.Dispose();
                if (++count == 3)
                {
                    backend.FaultOnNextRead = new CameraDeviceLostException(
                        "Device disconnected", "test");
                }
            }
        });

        Assert.True(count >= 3, $"Expected at least 3 frames before fault, got {count}");
    }

    [Fact]
    public async Task ReadFrameAsync_DeviceLost_ThrowsCameraDeviceLostException()
    {
        var backend = new InMemoryCameraBackend();
        await using var session = await TestHelpers.CreateSessionWithBackend(backend);

        await session.StartCaptureAsync();

        using (var frame = await session.ReadFrameAsync())
        {
            Assert.NotNull(frame);
        }

        backend.FaultOnNextRead = new CameraDeviceLostException("Gone", "test");

        await Assert.ThrowsAsync<CameraDeviceLostException>(async () =>
        {
            // Keep reading until the fault propagates through the channel.
            for (int i = 0; i < 100; i++)
                using (var f = await session.ReadFrameAsync()) { }
        });
    }

    [Fact]
    public async Task CaptureAsync_DeviceLost_DoesNotSilentlyComplete()
    {
        var backend = new InMemoryCameraBackend();
        await using var session = await TestHelpers.CreateSessionWithBackend(backend);

        backend.FaultOnNextRead = new CameraDeviceLostException("Gone", "test");

        bool didThrow = false;
        try
        {
            await foreach (var frame in session.CaptureAsync())
                frame.Dispose();
        }
        catch (CameraDeviceLostException)
        {
            didThrow = true;
        }

        Assert.True(didThrow, "Device loss must throw, not silently yield break");
    }

    [Fact]
    public async Task CaptureAsync_GenericBackendError_Propagates()
    {
        var backend = new InMemoryCameraBackend();
        await using var session = await TestHelpers.CreateSessionWithBackend(backend);

        backend.FaultOnNextRead = new CameraException("Hardware error", "test");

        await Assert.ThrowsAsync<CameraException>(async () =>
        {
            await foreach (var frame in session.CaptureAsync())
                frame.Dispose();
        });
    }
}
