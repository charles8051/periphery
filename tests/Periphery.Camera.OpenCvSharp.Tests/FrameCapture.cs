using Periphery.Camera.Testing;

namespace Periphery.Camera.OpenCvSharp.Tests;

/// <summary>
/// Captures one frame of a chosen format and content through a real
/// <see cref="CameraSession"/> over <see cref="InMemoryCameraBackend"/>.
/// </summary>
/// <remarks>
/// <para>
/// The frames these tests assert against have to come through the pool, not out
/// of a constructor: the tight-row invariant the mapping table depends on is
/// something the pool establishes and asserts (ADR-0081 D1 / D2), so a
/// hand-built frame would be testing the test's own idea of the layout. Going
/// through <see cref="CameraTestHarness"/> also means an
/// <see cref="InMemoryCameraBackend.OverrideStride"/> frame exercises the
/// de-padding copy rather than skipping it.
/// </para>
/// <para>
/// <see cref="CameraTestHarness"/> rather than <c>CameraTestScope</c>: the
/// harness touches no process-global state (ADR-0065), so these tests run in
/// parallel with each other.
/// </para>
/// </remarks>
internal static class FrameCapture
{
    /// <summary>
    /// Opens a session, pulls exactly one frame, and hands it to
    /// <paramref name="assert"/>. The frame is disposed when the callback
    /// returns and the session after that.
    /// </summary>
    /// <remarks>
    /// The session stays open for the callback because the frame's buffer
    /// belongs to that session's pool. Disposing the session first and then
    /// reading the frame is the exact use-after-release this package's scope
    /// type exists to prevent, and a test harness should not model it by
    /// accident.
    /// </remarks>
    internal static async Task WithOneFrameAsync(
        CameraPixelFormat format,
        int width,
        int height,
        Func<CameraFrameSpec, byte[]> pattern,
        Action<ICameraFrame> assert,
        int? overrideStride = null)
    {
        var backend = NewBackend(format, width, height, pattern, overrideStride);
        var configured = Configuration(format, width, height);

        await using var session = await CameraTestHarness.OpenSessionAsync(backend, configured);

        bool ran = false;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await foreach (var frame in session.CaptureAsync(ct: cts.Token))
        {
            using (frame)
            {
                ran = true;
                assert(frame);
            }
            break;
        }

        AssertRan(ran, format);
    }

    // A capture loop that produced nothing leaves every assertion inside it
    // unexecuted, and the test passes. That is the failure mode a hardware-free
    // suite is most likely to acquire and least likely to notice, so the
    // positive control is part of the helper rather than something each test
    // remembers.
    private static void AssertRan(bool ran, CameraPixelFormat format)
    {
        Assert.True(ran, $"The session delivered no {format} frame, so nothing was asserted.");
    }

    private static InMemoryCameraBackend NewBackend(
        CameraPixelFormat format,
        int width,
        int height,
        Func<CameraFrameSpec, byte[]> pattern,
        int? overrideStride) =>
        new(formats: [Format(format, width, height)])
        {
            FrameFactory = pattern,
            OverrideStride = overrideStride,
            // The producer keeps running while the consumer asserts; capping it
            // stops a fast fake from spinning the pool for the duration.
            MaxFrames = 4,
        };

    private static CameraConfiguration Configuration(CameraPixelFormat format, int width, int height) =>
        new(Format(format, width, height));

    private static CameraFormat Format(CameraPixelFormat format, int width, int height) =>
        new(width, height, format, new Rational(15), new Rational(30),
            format == CameraPixelFormat.Mjpeg
                ? CameraTransport.Compressed
                : CameraTransport.Uncompressed);
}
