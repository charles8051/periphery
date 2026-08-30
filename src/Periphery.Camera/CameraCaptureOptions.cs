// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Camera;

/// <summary>Per-capture-call options controlling frame delivery behavior.</summary>
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
