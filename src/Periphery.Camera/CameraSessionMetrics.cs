// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Camera;

/// <summary>
/// Counters that the session exposes for higher-level supervision. These are
/// observable facts — the session does not interpret them as health policy.
/// </summary>
/// <param name="FramesProduced">Frames handed to the consumer.</param>
/// <param name="FramesDropped">Frames Periphery discarded, either evicted from the
/// delivery queue under <see cref="BufferExhaustionPolicy.LatestWins"/> or refused
/// because no pooled buffer was free. Frames the platform discarded upstream while
/// the producer was parked are <em>not</em> here; nothing counts those.</param>
/// <param name="OutstandingLeases">Pooled buffers currently held — queued for the
/// consumer, or leased to it and not yet disposed.</param>
/// <param name="LastFrameTimestamp">Timestamp of the most recent delivered frame.</param>
/// <param name="ProducerStalls">Times the producer parked because the pipeline was
/// full. Counted on entry, so a producer parked right now is already in this number.
/// Only <see cref="BufferExhaustionPolicy.StallProducer"/> parks.</param>
/// <param name="ProducerStallTime">Total time the producer spent parked. This is
/// capture time during which the platform's read was not being called, so it is the
/// number that separates a stalled pipeline from a genuinely slow camera — a
/// distinction that had no signal at all before (ADR-0082 D5). Covers completed
/// stalls only; time in a stall still in progress lands here when it ends.</param>
public sealed record CameraSessionMetrics(
    long FramesProduced,
    long FramesDropped,
    int OutstandingLeases,
    TimeSpan? LastFrameTimestamp,
    long ProducerStalls,
    TimeSpan ProducerStallTime);
