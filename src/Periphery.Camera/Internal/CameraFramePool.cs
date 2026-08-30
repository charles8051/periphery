// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Collections.Concurrent;

namespace Periphery.Camera.Internal;

/// <summary>
/// Manages a bounded pool of reusable byte buffers for frame delivery.
/// Active leases are never revoked — exhaustion policy affects future frames only.
/// </summary>
/// <remarks>
/// Policy enforcement lives in the session: the stall in its producer loop, the
/// eviction in the delivery channel's own <c>itemDropped</c> callback. This class
/// only manages buffer acquisition, frame construction, and return.
/// </remarks>
internal sealed class CameraFramePool
{
    private readonly ConcurrentQueue<byte[]> _available = new();
    private readonly SemaphoreSlim _returnSignal = new(0, int.MaxValue);
    private int _outstandingLeases;
    private long _framesDropped;
    private long _bulkCopies;
    private long _rowCopies;

    internal int OutstandingLeases => Volatile.Read(ref _outstandingLeases);
    internal long FramesDropped => Volatile.Read(ref _framesDropped);

    /// <summary>
    /// Frames delivered by the bulk <c>memcpy</c> fast path, and by the
    /// per-plane row loop that de-pads and flips. Split so a test can tell which
    /// path ran — the fast path's precondition is layout equality (ADR-0081 D2)
    /// and nothing about the delivered frame distinguishes the two, so an
    /// unwatched fast path is one whose precondition can rot silently.
    /// </summary>
    internal long BulkCopies => Volatile.Read(ref _bulkCopies);

    /// <inheritdoc cref="BulkCopies"/>
    internal long RowCopies => Volatile.Read(ref _rowCopies);

    internal void IncrementDropped()
    {
        Interlocked.Increment(ref _framesDropped);
        CameraDiagnostics.FramesDropped.Add(1);
    }

    /// <summary>
    /// Tries to deliver a frame using a pooled buffer. Returns null if no buffer
    /// is available — the caller decides what to do (drop, or stall and retry).
    /// </summary>
    internal LeasedCameraFrame? TryDeliver(in RawCameraFrame raw)
    {
        var plan = PlanAndCheck(in raw);

        if (!_available.TryDequeue(out var buffer))
            return null;

        // Under ADR-0081 D1 the delivered frame is tight, which is exactly what
        // CameraSession seeds the pool with, so this branch is unreachable for
        // every uncompressed format. It stays for MJPEG, whose seed is a
        // worst-case estimate a busy frame can legitimately exceed.
        if (buffer.Length < plan.TargetLength)
            buffer = new byte[plan.TargetLength];

        return Deliver(buffer, in plan, in raw);
    }

    // Everything that can reject a frame runs here, before delivery takes a
    // buffer. Both throw — Plan on a source that cannot be read as its own
    // format, AssertTightRows on a target that would break D1 — and a buffer
    // dequeued before the throw is never put back. A backend emitting malformed
    // metadata transiently would otherwise shrink the pool one buffer per frame
    // until it started dropping (Peanut Gallery turn 1).
    private static FrameCopyPlan PlanAndCheck(in RawCameraFrame raw)
    {
        var plan = FrameCopy.Plan(in raw);
        AssertTightRows(in plan, raw.PixelFormat, raw.Width, raw.Height);
        return plan;
    }

    // The one place a frame's bytes move: the de-pad, the flip, and the
    // tight-row invariant all live here and nowhere else (ADR-0081 D2).
    private LeasedCameraFrame Deliver(byte[] buffer, in FrameCopyPlan plan, in RawCameraFrame raw)
    {
        FrameCopy.Execute(in plan, raw.Data.Span, buffer);
        if (plan.IsBulk)
            Interlocked.Increment(ref _bulkCopies);
        else
            Interlocked.Increment(ref _rowCopies);

        Interlocked.Increment(ref _outstandingLeases);
        CameraDiagnostics.OutstandingLeases.Add(1);

        var planes = BuildPlanes(buffer, in plan);
        return new LeasedCameraFrame(
            buffer.AsMemory(0, plan.TargetLength),
            raw.Width, raw.Height, raw.PixelFormat, raw.Timestamp,
            planes, this);
    }

    /// <summary>
    /// Asynchronously waits for a buffer to be returned to the pool. Used by
    /// <see cref="BufferExhaustionPolicy.StallProducer"/>.
    /// </summary>
    /// <remarks>
    /// The signal counts returns, not free buffers, so a wait can be satisfied
    /// by a return whose buffer another caller has already taken. The caller
    /// must re-try <see cref="TryDeliver"/> and handle a second null rather than
    /// assume a buffer is waiting.
    /// </remarks>
    internal Task WaitForReturnAsync(CancellationToken ct) => _returnSignal.WaitAsync(ct);

    internal void Return(byte[] buffer)
    {
        Interlocked.Decrement(ref _outstandingLeases);
        CameraDiagnostics.OutstandingLeases.Add(-1);
        _available.Enqueue(buffer);
        _returnSignal.Release();
    }

    internal void Seed(int frameSize, int bufferCount)
    {
        for (int i = 0; i < bufferCount; i++)
            _available.Enqueue(new byte[frameSize]);
    }

    // The delivered planes describe the pooled buffer, which the copy just laid
    // out to the plan's target — never the source, whose padding the copy has
    // removed by this point.
    private static CameraPlane[] BuildPlanes(byte[] buffer, in FrameCopyPlan plan)
    {
        var target = plan.Target;
        var planes = new CameraPlane[target.Count];
        for (int i = 0; i < target.Count; i++)
        {
            var p = target[i];
            planes[i] = new CameraPlane(
                buffer.AsMemory(p.Offset, p.Length),
                p.Stride, p.Width, p.Height);
        }
        return planes;
    }

    /// <summary>
    /// ADR-0081 D1, checked rather than assumed: every plane of every
    /// uncompressed frame the pool delivers has tight rows.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Stated against <see cref="CameraFrameLayout"/> and against the planes'
    /// own extents rather than against <see cref="PlaneLayout"/>, which is where
    /// the target layout came from — a check that re-derives its expectation
    /// from the code it is checking passes through any change the two make
    /// together, and that is precisely how the recompute-vs-carry gap in #320
    /// survived a green suite twice.
    /// </para>
    /// <para>
    /// Three independent properties say "tight": the first plane's stride is the
    /// format's natural row width; each plane's rows exactly fill its extent, so
    /// there is no padding between rows; and the planes tile the frame from zero
    /// with no gap between them and no overhang. MJPEG and Unknown are exempt —
    /// compressed or opaque, no rows to pad (D7).
    /// </para>
    /// </remarks>
    private static void AssertTightRows(
        in FrameCopyPlan plan, CameraPixelFormat format, int width, int height)
    {
        if (format is CameraPixelFormat.Mjpeg or CameraPixelFormat.Unknown)
            return;

        var planes = plan.Target;
        int naturalRow = CameraFrameLayout.BytesPerRow(format, width);
        if (planes[0].Stride != naturalRow)
            throw Violated(
                $"plane 0 has a {planes[0].Stride}-byte stride where a {width}-pixel "
                    + $"{format} row is {naturalRow} bytes");

        int expectedOffset = 0;
        for (int i = 0; i < planes.Count; i++)
        {
            var p = planes[i];
            if (p.Stride * p.Height != p.Length)
                throw Violated(
                    $"plane {i} spans {p.Length} bytes for {p.Height} rows of {p.Stride} bytes, "
                        + "so its rows are padded");
            if (p.Offset != expectedOffset)
                throw Violated(
                    $"plane {i} starts at byte {p.Offset} where plane {i - 1} ends at "
                        + $"{expectedOffset}, leaving a gap");
            expectedOffset += p.Length;
        }

        int frameSize = CameraFrameLayout.FrameSize(format, width, height);
        if (expectedOffset != frameSize)
            throw Violated(
                $"the planes cover {expectedOffset} bytes where a {width}x{height} {format} "
                    + $"frame is {frameSize}");

        static InvalidOperationException Violated(string detail) =>
            new($"Tight-row invariant violated: {detail}. Every uncompressed frame the pool "
                + "delivers must have Stride == CameraFrameLayout.BytesPerRow (ADR-0081 D1).");
    }
}
