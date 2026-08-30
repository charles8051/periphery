// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Camera;

/// <summary>
/// An <see cref="ICameraFrameSink"/> that disposes every frame it receives
/// and ignores format-change callbacks. Useful for benchmarks, tests that
/// exercise upstream operators without rendering, and the "tee a branch but
/// don't render its output" pattern.
/// </summary>
/// <remarks>
/// Moved into <c>Periphery.Camera</c> from the now-deleted
/// <c>Periphery.Camera.Pipelines</c> per ADR-0045 §3. Every accepted
/// frame is disposed inside <see cref="PresentAsync"/>, so pool buffers
/// recycle promptly.
/// </remarks>
public sealed class NullCameraFrameSink : ICameraFrameSink
{
    private static readonly IReadOnlyList<CameraFrameMemoryDomain> CpuOnly =
        new[] { CameraFrameMemoryDomain.Cpu };

    private long _accepted;
    private bool _disposed;

    /// <summary>The number of frames this sink has accepted (and disposed) since construction.</summary>
    public long AcceptedFrameCount => Interlocked.Read(ref _accepted);

    /// <inheritdoc />
    public IReadOnlyList<CameraFrameMemoryDomain> SupportedMemoryDomains => CpuOnly;

    /// <inheritdoc />
    public ValueTask PresentAsync(ICameraFrame frame, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (_disposed)
        {
            frame.Dispose();
            return ValueTask.CompletedTask;
        }

        Interlocked.Increment(ref _accepted);
        frame.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask OnFormatChangedAsync(CameraFormatInfo format, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(format);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return ValueTask.CompletedTask;
    }
}
