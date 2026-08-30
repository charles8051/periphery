// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Camera;

/// <summary>
/// What the session does when a frame arrives and the pipeline is full — no
/// pooled buffer free, or the delivery queue already at
/// <see cref="CameraSessionOptions.QueueDepth"/>.
/// </summary>
/// <remarks>
/// <para>
/// Neither value makes a session lossless. A camera is a real-time source and
/// the sensor keeps exposing whether or not anyone is reading, so a pipeline
/// that cannot keep up loses frames somewhere — see
/// <see cref="CameraSession"/>'s delivery contract (ADR-0082 D1). This enum
/// only chooses <em>which</em> frames are lost and whether Periphery can count
/// them.
/// </para>
/// <para>
/// The enum has two members because it has two behaviours. Earlier revisions
/// declared four; the other two were never reachable, and one of them
/// (<c>DropIncoming</c>) kept the stalest queued frame while its documentation
/// promised low latency. They were deleted rather than implemented (ADR-0082 D2).
/// </para>
/// </remarks>
public enum BufferExhaustionPolicy
{
    /// <summary>
    /// Discard the oldest undelivered frame and keep the newest (default). The
    /// discarded frame is disposed, so its pooled buffer returns immediately,
    /// and every discard is counted in
    /// <see cref="CameraSessionMetrics.FramesDropped"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For a live source the newest frame is almost always the useful one, and
    /// a consumer that falls behind gets current pixels rather than a backlog.
    /// The name matches <c>FrameFlow.Graph</c>'s <c>EdgeOptions.LatestWins()</c>,
    /// which has the same semantics — a consumer bridging frames into that
    /// runtime should not have to translate between two names for one idea.
    /// </para>
    /// <para>
    /// <b>The one exception, stated rather than left to be found.</b> A frame the
    /// consumer is already holding cannot be taken back (ADR-0035 D9). So a
    /// consumer holding <em>more</em> than
    /// <see cref="CameraSessionOptions.BufferCount"/> frames at once — by
    /// <see cref="LeasedCameraFrame.AddRef"/> fan-out, or by not disposing —
    /// leaves no buffer to copy the newest frame into, and it is refused
    /// instead. Holding up to <c>BufferCount</c> against a full queue does
    /// <em>not</em> do this: the queue has its own reservation and the producer
    /// its own spare, so it can always copy the newest frame and trade the
    /// oldest queued one for it. Both kinds of loss are counted the same way.
    /// </para>
    /// </remarks>
    LatestWins,

    /// <summary>
    /// Park the producer until the consumer takes a frame or returns a lease.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is not a delivery guarantee.</b> While the producer is parked it
    /// is not calling the platform's read (<c>IMFSourceReader::ReadSample</c>,
    /// <c>VIDIOC_DQBUF</c>), so the driver's own queue fills and the driver
    /// begins discarding — uncounted, where Periphery cannot see it. Stalling
    /// converts countable drops into uncountable ones.
    /// </para>
    /// <para>
    /// It is the right choice for a burst shorter than the platform's own queue:
    /// a consumer that stalls briefly and then catches up loses nothing, because
    /// the frames wait in the driver and arrive late rather than never. It does
    /// not survive sustained overload. Watch
    /// <see cref="CameraSessionMetrics.ProducerStalls"/> and
    /// <see cref="CameraSessionMetrics.ProducerStallTime"/> to tell the two
    /// apart.
    /// </para>
    /// </remarks>
    StallProducer,
}
