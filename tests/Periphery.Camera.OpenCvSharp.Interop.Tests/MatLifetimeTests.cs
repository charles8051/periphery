using OpenCvSharp;
using Periphery.Camera.Testing;

namespace Periphery.Camera.OpenCvSharp.Interop.Tests;

/// <summary>
/// Who owns the pixels, and what happens when a caller gets that wrong.
/// </summary>
/// <remarks>
/// The hazard these tests describe is silent by nature: a pooled buffer whose
/// lease has been dropped is handed to the next frame and refilled, so a
/// <c>Mat</c> that outlived its scope reads plausible pixels belonging to a
/// different moment rather than faulting. Nothing here reads such a
/// <c>Mat</c> — that would be a test of undefined behaviour. What is asserted
/// is that the doors are shut: the scope refuses to hand out its <c>Mat</c>
/// after disposal, and the <c>Mat</c> itself refuses to be used.
/// </remarks>
[Trait("Category", "Integration")]
public class MatLifetimeTests
{
    private static readonly byte[] Bgr24Bytes =
    [
        10, 20, 30, 40, 50, 60, 70, 80, 90, 100, 110, 120,
        130, 140, 150, 160, 170, 180, 190, 200, 210, 220, 230, 240,
    ];

    [OpenCvFact]
    public async Task MatScope_RefusesItsMatAfterDisposal()
    {
        await MatAssert.WithFrameAsync(CameraPixelFormat.Bgr24, 4, 2, Bgr24Bytes, frame =>
        {
            var scope = frame.AsMat();
            var escaped = scope.Mat;

            Assert.Equal(10, escaped.At<Vec3b>(0, 0).Item0);

            scope.Dispose();

            // The scope will not hand it out again...
            Assert.Throws<ObjectDisposedException>(() => scope.Mat);

            // ...and the reference that got out before disposal is dead too,
            // because the scope disposes the header as well as the pin.
            Assert.Throws<ObjectDisposedException>(() => escaped.At<Vec3b>(0, 0));
        });
    }

    [OpenCvFact]
    public async Task MatScope_DisposesIdempotently()
    {
        await MatAssert.WithFrameAsync(CameraPixelFormat.Bgr24, 4, 2, Bgr24Bytes, frame =>
        {
            var scope = frame.AsMat();

            scope.Dispose();
            scope.Dispose();
            scope.Dispose();

            // A second release would drive the frame's reference count negative,
            // which in DEBUG throws out of Dispose and in RELEASE silently
            // returns a live buffer to the pool.
            Assert.Throws<ObjectDisposedException>(() => scope.Mat);
        });
    }

    [OpenCvFact]
    public async Task AsMat_RefusesAFrameWhoseLeaseIsAlreadyGone()
    {
        ICameraFrame? escaped = null;

        await MatAssert.WithFrameAsync(CameraPixelFormat.Bgr24, 4, 2, Bgr24Bytes, frame =>
        {
            escaped = frame;
        });

        // The using in the harness dropped the last reference on the way out.
        Assert.Throws<ObjectDisposedException>(() => escaped!.AsMat());
    }

    [OpenCvFact]
    public async Task ToMat_OutlivesTheFrameAndThePoolRecyclingItsBuffer()
    {
        // The counterpart to the scope: a copy is a copy. Take one, let the frame
        // go, then pull enough further frames that the pool has certainly handed
        // that buffer to another one, and check the copy still reads what it read.
        var backend = new InMemoryCameraBackend(
            formats: [new CameraFormat(4, 2, CameraPixelFormat.Bgr24,
                new Rational(15), new Rational(30), CameraTransport.Uncompressed)])
        {
            // Frame 1 carries the bytes under test; every later frame is filled
            // with its own index, so a recycled buffer read through a stale
            // pointer would come back as a block of 2s, 3s, 4s and so on.
            FrameFactory = spec => spec.FrameIndex == 1
                ? (byte[])Bgr24Bytes.Clone()
                : Filled(spec.FrameSize, (byte)spec.FrameIndex),
            MaxFrames = 12,
        };

        var configuration = new CameraConfiguration(
            new CameraFormat(4, 2, CameraPixelFormat.Bgr24,
                new Rational(15), new Rational(30), CameraTransport.Uncompressed));

        // The test needs to see every frame the fake produces, so it asks for
        // the policy that delivers them. Under the default latest-wins the
        // session is lossy by contract (ADR-0082 D1) and the producer would
        // evict most of these before the consumer got to them — which would
        // exercise a different thing than "a copy outlives its buffer".
        var options = new CameraSessionOptions(
            ExhaustionPolicy: BufferExhaustionPolicy.StallProducer);

        Mat? copy = null;
        try
        {
            await using var session = await CameraTestHarness.OpenSessionAsync(
                backend, configuration, options: options);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            int seen = 0;
            await foreach (var frame in session.CaptureAsync(ct: cts.Token))
            {
                using (frame)
                {
                    if (++seen == 1)
                        copy = frame.ToMat();
                }

                // The default pool holds three buffers, so eight frames is
                // several times round.
                if (seen == 8)
                    break;
            }

            Assert.Equal(8, seen);
            Assert.NotNull(copy);
            Assert.Equal(10, copy.At<Vec3b>(0, 0).Item0);
            Assert.Equal(20, copy.At<Vec3b>(0, 0).Item1);
            Assert.Equal(240, copy.At<Vec3b>(1, 3).Item2);
        }
        finally
        {
            copy?.Dispose();
        }

        static byte[] Filled(int length, byte value)
        {
            var bytes = new byte[length];
            Array.Fill(bytes, value);
            return bytes;
        }
    }

    [OpenCvFact]
    public async Task ToBgr_OwnsItsResult()
    {
        Mat? bgr = null;
        try
        {
            await MatAssert.WithFrameAsync(CameraPixelFormat.Rgb24, 4, 2, Bgr24Bytes, frame =>
            {
                bgr = frame.ToBgr();
            });

            // The frame is gone; the conversion's destination was never the
            // frame's memory, so it is still readable.
            Assert.NotNull(bgr);
            MatAssert.Bgr(bgr, 0, 0, 30, 20, 10);
        }
        finally
        {
            bgr?.Dispose();
        }
    }
}
