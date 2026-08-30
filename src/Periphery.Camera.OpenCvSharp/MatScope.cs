// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using OpenCvSharp;

namespace Periphery.Camera.OpenCvSharp;

/// <summary>
/// A <c>cv::Mat</c> header over a frame's own pixels, with the frame's
/// reference held for as long as the header is live. Returned by
/// <see cref="CameraFrameMatExtensions.AsMat"/>; dispose it before the frame's
/// buffer is needed elsewhere.
/// </summary>
/// <remarks>
/// <para>
/// <b>The type exists to make the lifetime visible in the call-site syntax.</b>
/// <c>AsMat</c> and <c>ToMat</c> could both have returned <c>Mat</c> and the
/// difference between them would then live only in a doc comment — while the
/// failure it guards against is silent. A pooled buffer whose lease has been
/// released is handed to the next frame and overwritten; a <c>Mat</c> still
/// pointing at it keeps reading, and reads somebody else's pixels, or half of
/// them mid-copy. There is no access violation to catch. A scope you have to
/// dispose says <c>using</c> at the call site, and a <c>Mat</c> you own says
/// nothing, which is correct for the one that does not need saying.
/// </para>
/// <para>
/// <b>A class, not a struct</b>, for the reason
/// <see cref="CameraFramePin"/> gives: a copyable value whose <c>Dispose</c>
/// clears only the instance it was called on lets <c>var b = a;</c> plus two
/// disposals release the same pin twice. Against a per-frame path that already
/// allocates a <c>Mat</c>, the object header is not the expensive part.
/// </para>
/// <para>
/// <b>Do not write through the <c>Mat</c>.</b> OpenCV has no const <c>Mat</c>,
/// so the type system cannot say this, but a frame is a shared buffer — a
/// preview, a recorder and an inference leg may all hold references to these
/// same bytes. Writing through the header corrupts every other subscriber's
/// view mid-read. Use <see cref="CameraFrameMatExtensions.ToMat"/> or
/// <see cref="CameraFrameMatExtensions.ToBgr"/> when you need a destination you
/// can edit. This is <see cref="CameraFramePin"/>'s rule; the scope inherits
/// it.
/// </para>
/// <para>
/// <b>One owner.</b> Repeated <see cref="Dispose"/> is safe, but reading
/// <see cref="Mat"/> on one thread while another disposes is a race no guard
/// here can close.
/// </para>
/// </remarks>
public sealed class MatScope : IDisposable
{
    private readonly CameraFramePin _pin;
    private readonly Mat _mat;
    private int _disposed;

    internal MatScope(Mat mat, CameraFramePin pin)
    {
        _mat = mat;
        _pin = pin;
    }

    /// <summary>
    /// The header. Valid for exactly this scope's lifetime; reading it after
    /// disposal throws rather than handing back a <c>Mat</c> whose data pointer
    /// may now belong to a later frame.
    /// </summary>
    /// <remarks>
    /// A caller that copies the <c>Mat</c> reference out and uses it after the
    /// scope ends is past this guard, and gets OpenCvSharp's own
    /// <see cref="ObjectDisposedException"/> from the disposed header instead.
    /// Both doors are shut; only the first one has a message that says why.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The scope has been disposed.</exception>
    public Mat Mat
    {
        get
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return _mat;
        }
    }

    /// <summary>
    /// Releases the <c>Mat</c> header and then the pin — which unpins the bytes
    /// and drops the frame reference the scope was holding. Safe to call more
    /// than once.
    /// </summary>
    /// <remarks>
    /// Header first, in a <c>finally</c>, for the reason
    /// <see cref="CameraFramePin.Dispose"/> orders its own two steps: nothing
    /// that points at the buffer may outlive the reference that justified it,
    /// and a throw from the first step must not strand the second. Disposing a
    /// <c>Mat</c> built by <c>Mat.FromPixelData</c> frees only the header —
    /// OpenCV never owns memory it did not allocate — so the frame's bytes are
    /// untouched by this.
    /// </remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try
        {
            _mat.Dispose();
        }
        finally
        {
            _pin.Dispose();
        }
    }
}
