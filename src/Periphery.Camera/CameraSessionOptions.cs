// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Camera;

/// <summary>Session-scoped options that shape buffer pool and capture infrastructure.</summary>
/// <remarks>
/// <para>
/// A frame is free, queued, or leased — one budget, three states. The session
/// pre-allocates <c>BufferCount + QueueDepth + 1</c> buffers to cover all three: the
/// consumer's allowance, the queue's own reservation, and one spare for the producer
/// to copy the next frame into.
/// </para>
/// <para>
/// The spare is what makes <see cref="BufferExhaustionPolicy.LatestWins"/> hold under
/// load rather than degrading into keeping the stalest frame. At the defaults the pool
/// is 5 buffers rather than 3, which is one frame of memory more than the sum of the
/// two knobs — about 3 MB at 1080p NV12, 600 KB at VGA YUY2.
/// </para>
/// </remarks>
/// <param name="BufferCount">How many frames the consumer may hold at once. Holding more
/// than this — by <see cref="LeasedCameraFrame.AddRef"/> fan-out, or by not disposing — is
/// the only thing that empties the pool, and the resulting loss is the one no policy can
/// prevent, because active leases are never revoked (ADR-0035 D9).</param>
/// <param name="QueueDepth">Bounded channel capacity between the producer thread and consumer,
/// and the number of buffers reserved for it. A depth of 1 (default) keeps one frame ready for
/// the consumer while the producer reads the next. Higher values absorb consumer jitter at the
/// cost of latency and of that many more pre-allocated buffers.</param>
/// <param name="ExhaustionPolicy">Which frames are lost when the pipeline is full. Defaults to
/// <see cref="BufferExhaustionPolicy.LatestWins"/>. Neither value makes the session lossless —
/// see <see cref="CameraSession"/>'s delivery contract.</param>
public sealed record CameraSessionOptions(
    int BufferCount = 3,
    int QueueDepth = 1,
    BufferExhaustionPolicy ExhaustionPolicy = BufferExhaustionPolicy.LatestWins);
