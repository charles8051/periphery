// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Camera;

/// <summary>Per-capture-call options controlling frame delivery behavior.</summary>
/// <remarks>
/// <para>
/// <b><see cref="FrameTimeout"/> is the session's only frame-arrival deadline,
/// deliberately.</b> ADR-0084 D5 proposed a second one — a session-scoped
/// <c>StallTimeout</c> with its own <c>CameraStallException</c> — for the wedged
/// stream a lifecycle owner has to recover from. It was cut (issue #219). This
/// option already turns a stream that stops delivering into a typed
/// <see cref="CameraTimeoutException"/>, by default, on the streaming path
/// <c>CameraDeviceProxy</c> drives; a second deadline would answer the same
/// question in a second spelling and need a precedence rule to keep the two from
/// drifting.
/// </para>
/// <para>
/// <b>What no deadline can express is a stream that is slow rather than dead.</b>
/// A session configured at 30 fps and delivering half a frame a second never
/// trips a five-second wait and never will, and tearing down a camera that is
/// already struggling tends to make it worse — so that condition wants
/// reporting, not recovery. It is answerable from
/// <see cref="CameraSessionMetrics"/> today: delivered rate from
/// <see cref="CameraSessionMetrics.FramesProduced"/> and
/// <see cref="CameraSessionMetrics.LastFrameTimestamp"/>, against the configured
/// <see cref="CameraFormat.MaxFrameRate"/>. Adding a second binary deadline would
/// not have covered it either.
/// </para>
/// </remarks>
/// <param name="FrameTimeout">
/// Maximum time to wait for the next frame from the backend before treating
/// the stream as stalled and throwing <see cref="CameraTimeoutException"/>.
/// <para>
/// <see langword="null"/> (the default) applies
/// <see cref="DefaultFrameTimeout"/> — long enough to accommodate slow
/// first-frame spin-up on most USB cameras, short enough to surface real
/// driver-level stalls rather than hanging the consumer indefinitely.
/// </para>
/// <para>
/// Pass <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> to disable
/// the timeout entirely. Not recommended outside of debugging — some USB
/// cameras stall ReadSample silently and there is no other signal.
/// </para>
/// </param>
public sealed record CameraCaptureOptions(
    TimeSpan? FrameTimeout = null)
{
    /// <summary>Default frame-arrival timeout when <see cref="FrameTimeout"/> is null.</summary>
    public static readonly TimeSpan DefaultFrameTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Resolves <see cref="FrameTimeout"/> to a concrete value:
    /// <see langword="null"/> → <see cref="DefaultFrameTimeout"/>;
    /// <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> → no timeout.
    /// </summary>
    internal TimeSpan? EffectiveFrameTimeout
    {
        get
        {
            var t = FrameTimeout ?? DefaultFrameTimeout;
            return t == System.Threading.Timeout.InfiniteTimeSpan ? null : t;
        }
    }
}
