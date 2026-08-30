// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Camera;

/// <summary>
/// Terminal consumer of camera frames. Sinks receive frames via
/// <see cref="PresentAsync"/>, take ownership of each frame they accept,
/// and dispose it when presentation is complete.
/// </summary>
/// <remarks>
/// <para>
/// Moved into <c>Periphery.Camera</c> proper from the now-deleted
/// <c>Periphery.Camera.Pipelines</c> sub-package (ADR-0045 §3). The
/// interface has no substrate dependency — it's a pure camera-side
/// consumer contract — so it lives with the rest of the camera surface
/// rather than in a separate sub-package.
/// </para>
/// <para>
/// <b>Frame ownership.</b> When a sink's <see cref="PresentAsync"/> accepts
/// a frame, it takes ownership. The sink must dispose the frame when it's
/// done with it — synchronously inside <see cref="PresentAsync"/> for
/// fire-and-forget sinks, or later for double-buffered sinks that hold a
/// pending frame between calls. The pipeline runtime (when one is present)
/// never disposes a frame after handing it to a sink.
/// </para>
/// <para>
/// <b>Substrate bridges.</b> A FrameFlow.Graph
/// <c>SinkNode&lt;CameraFrameAdapter&gt;</c> that wraps an
/// <see cref="ICameraFrameSink"/> lives in <c>FrameFlow.Camera</c>
/// (the substrate-consumer side). Periphery does not ship its own
/// substrate bridges per ADR-0045's "no graphs in Periphery" stance.
/// </para>
/// </remarks>
public interface ICameraFrameSink : IAsyncDisposable
{
    /// <summary>
    /// The memory domains this sink can accept. Pipeline operators that
    /// produce frames in incompatible domains must convert before delivery.
    /// </summary>
    IReadOnlyList<CameraFrameMemoryDomain> SupportedMemoryDomains { get; }

    /// <summary>
    /// Presents a frame to the sink. The sink takes ownership of
    /// <paramref name="frame"/> and is responsible for disposing it when
    /// presentation is complete.
    /// </summary>
    /// <param name="frame">The frame to present. Sink owns disposal.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask PresentAsync(ICameraFrame frame, CancellationToken ct);

    /// <summary>
    /// Notifies the sink that the upstream stream's format has changed
    /// (e.g. resolution or pixel format switch). Called before any subsequent
    /// <see cref="PresentAsync"/> with frames in the new format. Sinks
    /// typically use this to reconfigure render surfaces, reset converters,
    /// or invalidate cached state.
    /// </summary>
    /// <param name="format">The new stream format.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask OnFormatChangedAsync(CameraFormatInfo format, CancellationToken ct);
}
