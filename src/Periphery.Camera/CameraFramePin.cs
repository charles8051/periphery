// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Buffers;

namespace Periphery.Camera;

/// <summary>
/// A frame's pixels held at a fixed address, with the frame's reference held
/// alongside them. This is the <c>IntPtr</c> + stride that OpenCV's <c>Mat</c>,
/// SkiaSharp's <c>SKBitmap.InstallPixels</c>, ONNX Runtime's
/// <c>OrtValue.CreateTensorValueWithData</c> and Avalonia's
/// <c>ILockedFramebuffer</c> all want, taken from a frame that otherwise exposes
/// its bytes only as <see cref="ReadOnlyMemory{T}"/>.
/// </summary>
/// <remarks>
/// <para>
/// Create one with <see cref="CameraFramePinning.Pin"/> or
/// <see cref="CameraFramePinning.PinPlane"/> and dispose it before the native
/// object built over it stops being used. <see cref="Scan0"/> is valid for
/// exactly the pin's lifetime and not one instruction longer.
/// </para>
/// <para>
/// <b>Why the library owns this rather than each consumer.</b> Frames are pooled
/// and reference-counted (ADR-0035 §8b), so a pointer that outlives its frame
/// does not fault. The buffer goes back to the pool, a later frame is copied
/// into it, and the consumer keeps reading the same address — now holding
/// somebody else's pixels, or half of them mid-copy. The failure is silently
/// wrong data, not an access violation, and only <c>Periphery.Camera</c> knows
/// the reference discipline that prevents it. A pin holds a reference for its
/// whole life, so the buffer cannot be recycled underneath it.
/// </para>
/// <para>
/// <b>Writing through <see cref="Scan0"/> is undefined, and the reason is
/// sharing rather than constness.</b> A raw pointer is writable because C has no
/// other kind, while <see cref="ICameraFrame.ContiguousBuffer"/> is
/// <see cref="ReadOnlyMemory{T}"/> because the frame does not hand its bytes out
/// for editing. Neither OpenCV binding has a const <c>Mat</c>, so the mismatch
/// cannot be closed by the type system. There is no <c>IsReadOnly</c> flag here,
/// because a flag nothing can enforce is a worse contract than a stated one, and
/// it would always read the same value.
/// </para>
/// <para>
/// The hazard is concrete and it is worse than editing your own data. A frame is
/// a shared buffer: a multicast fan-out has preview, record and inference all
/// holding references to the same bytes. A consumer that writes through its pin
/// corrupts every other subscriber's view of that frame, mid-read, and the pool
/// then hands the same buffer to a later frame. If you need to mutate, copy
/// first — <see cref="LeasedCameraFrame.Copy"/>, or a destination you allocated
/// — and write there.
/// </para>
/// <para>
/// <b>A class, not a struct.</b> A <c>readonly struct</c> would save the
/// allocation and cost correctness: it is copyable, and
/// <see cref="MemoryHandle.Dispose"/> clears the handle only on the instance it
/// is called on, so <c>var b = a;</c> followed by disposing both frees the same
/// <c>GCHandle</c> twice. <c>ref struct</c> does not fix that — it blocks
/// escaping, not copying. Against a per-frame path that until recently did
/// <c>ContiguousBuffer.ToArray()</c>, ~64 bytes is not the expensive part.
/// </para>
/// <para>
/// <b>Not thread-safe for concurrent dispose plus use.</b> Repeated
/// <see cref="Dispose"/> is safe and idempotent, but reading
/// <see cref="Scan0"/> on one thread while another disposes is a race the guard
/// cannot close: the check and the native call are not atomic. One owner per
/// pin.
/// </para>
/// </remarks>
public sealed class CameraFramePin : IDisposable
{
    private readonly ICameraFrame _frame;
    private readonly nint _scan0;
    private MemoryHandle _handle;
    private int _disposed;

    private CameraFramePin(
        ICameraFrame frame,
        MemoryHandle handle,
        nint scan0,
        int length,
        int stride,
        int width,
        int height,
        CameraPixelFormat pixelFormat)
    {
        _frame = frame;
        _handle = handle;
        _scan0 = scan0;
        Length = length;
        Stride = stride;
        Width = width;
        Height = height;
        PixelFormat = pixelFormat;
    }

    /// <summary>
    /// Address of the pinned bytes' first byte, valid until <see cref="Dispose"/>.
    /// Reading it after disposal throws rather than handing back a dangling
    /// pointer.
    /// </summary>
    /// <remarks>
    /// This is the one member whose meaning expires. The geometry below
    /// describes the frame and stays readable after disposal, so a caller can
    /// still log what it had.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The pin has been disposed.</exception>
    public nint Scan0
    {
        get
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return _scan0;
        }
    }

    /// <summary>
    /// Bytes pinned: the whole frame for <see cref="CameraFramePinning.Pin"/>,
    /// one plane's extent for <see cref="CameraFramePinning.PinPlane"/>.
    /// </summary>
    /// <remarks>
    /// On the record rather than left to be recomputed because two cases need
    /// it and neither can derive it. MJPEG has no <see cref="Stride"/> to
    /// multiply, and a planar frame's plane extents are not
    /// <c>Length / PlaneCount</c>.
    /// </remarks>
    public int Length { get; }

    /// <summary>
    /// Bytes from one row's start to the next, or 0 when the pinned bytes have
    /// no rows.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For an uncompressed frame this is the stride the frame states, which
    /// under ADR-0081 D1 is always the natural unpadded row width for that
    /// format and width. A whole-frame <see cref="CameraFramePinning.Pin"/>
    /// reports plane 0's stride: the packed row for a packed format, the luma
    /// row for a 4:2:0 one. I420 chroma rows are half that, so a consumer
    /// wrapping planes separately wants
    /// <see cref="CameraFramePinning.PinPlane"/>, which reports each plane's
    /// own.
    /// </para>
    /// <para>
    /// <b>MJPEG reports 0, not <c>null</c>.</b> A compressed frame is one opaque
    /// run and a stride has nothing to mean for it (ADR-0081 D7). <c>int?</c>
    /// was the alternative and it is worse at every call site, because every
    /// native API this type exists to feed takes a plain <c>int</c>: the caller
    /// writes <c>pin.Stride ?? 0</c> and arrives back at the sentinel having
    /// paid an unwrap for it. 0 also states something true — there are no rows,
    /// and no image has a zero-byte row — where <c>null</c> states only that
    /// nobody filled the field in. Branch on <see cref="PixelFormat"/>, which
    /// names the reason; <see cref="Length"/> is the number a decoder needs.
    /// </para>
    /// </remarks>
    public int Stride { get; }

    /// <summary>
    /// Width in samples of the image the pinned bytes carry: the frame's width
    /// for <see cref="CameraFramePinning.Pin"/>, the plane's for
    /// <see cref="CameraFramePinning.PinPlane"/> — half the frame's on a 4:2:0
    /// chroma plane, which carries half as many samples per row.
    /// </summary>
    /// <inheritdoc cref="Height" path="/remarks"/>
    public int Width { get; }

    /// <summary>
    /// Height in rows of the image the pinned bytes carry: the frame's height
    /// for <see cref="CameraFramePinning.Pin"/>, the plane's for
    /// <see cref="CameraFramePinning.PinPlane"/>.
    /// </summary>
    /// <remarks>
    /// <b>For MJPEG this is the image the blob decodes to, and the blob itself
    /// has no rows.</b> Zeroing the geometry to match
    /// <see cref="Stride"/> was the alternative and it throws away the one thing
    /// a decoder consumer wants before it decodes, which is how big the result
    /// will be — while <see cref="ICameraFrame.Width"/> and
    /// <see cref="ICameraFrame.Height"/> go on reporting it anyway, so the pin
    /// would only be disagreeing with its own frame. <see cref="Stride"/> is the
    /// member that says whether the geometry describes the bytes: non-zero and
    /// it does, 0 and <see cref="Length"/> is all that describes them.
    /// </remarks>
    public int Height { get; }

    /// <summary>The frame's pixel format. A plane pin reports the frame's
    /// format, not a per-plane one — there is no such thing.</summary>
    public CameraPixelFormat PixelFormat { get; }

    /// <summary>
    /// Unpins the memory and drops the reference this pin took, in that order.
    /// Safe to call more than once.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        // The order is the whole point of the type. Unpinning first means no
        // pinned handle ever outlives the reference that justified it. Drop the
        // reference first and the buffer can be back in the pool and refilled by
        // the next frame while this object still holds it fixed and still hands
        // out its address — the exact silent-stale-pixels failure the pin
        // exists to prevent, reintroduced by getting two lines the wrong way
        // round.
        //
        // The guard above is not just tidiness: MemoryHandle.Dispose frees a
        // GCHandle and clears only its own copy of it, and the frame's Dispose
        // decrements a reference count that would go negative.
        //
        // finally, not two statements: MemoryHandle.Dispose calls Unpin on a
        // MemoryManager-backed region, and a manager is entitled to throw there.
        // Without this the reference would be leaked past a guard that now
        // refuses to try again, and the pool would wait on that lease forever
        // (Peanut Gallery turn 1).
        try
        {
            _handle.Dispose();
        }
        finally
        {
            _frame.Dispose();
        }
    }

    /// <summary>
    /// Takes a reference on <paramref name="frame"/>, pins
    /// <paramref name="region"/>, and holds both until disposal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// AddRef comes first because it is the operation that can refuse. A frame
    /// whose buffer has already gone back to the pool throws
    /// <see cref="ObjectDisposedException"/> here, before any address is taken,
    /// so a pin can never form over a region a different frame now owns.
    /// </para>
    /// <para>
    /// <see cref="ReadOnlyMemory{T}.Pin"/> rather than
    /// <c>GCHandle.Alloc(…, Pinned)</c>: the pool's buffers are plain
    /// <c>byte[]</c>, LOH-sized at any real resolution and allocated once per
    /// session, so pinning them costs approximately nothing — but the V4L2
    /// backend has a <see cref="MemoryManager{T}"/>-backed path, and
    /// <c>GCHandle</c> cannot pin that at all.
    /// </para>
    /// </remarks>
    internal static CameraFramePin Create(
        ICameraFrame frame, ReadOnlyMemory<byte> region, int stride, int width, int height)
    {
        frame.AddRef();
        MemoryHandle handle = default;
        bool pinned = false;
        try
        {
            handle = region.Pin();
            pinned = true;
            nint scan0;
            unsafe
            {
                scan0 = (nint)handle.Pointer;
            }
            return new CameraFramePin(
                frame, handle, scan0, region.Length, stride, width, height, frame.PixelFormat);
        }
        catch
        {
            // Two things can fail here and the rollback has to cover both.
            // Pinning itself can refuse (a MemoryManager may, and a GCHandle
            // allocation can run out), which leaves only the reference to give
            // back. But the pin can also succeed and the construction after it
            // throw — an allocation failure, or a frame whose PixelFormat
            // getter throws — and then the handle is live and owned by nobody,
            // holding a GCHandle or a manager's pin for the life of the process
            // (Peanut Gallery turn 1).
            //
            // Unwound in the same order Dispose uses, and with the same finally,
            // for the same reason: an Unpin that throws during the rollback must
            // not take the reference release with it (Peanut Gallery turn 2).
            // A throw from the cleanup does replace the original failure, which
            // is the ordinary cost of unwinding and is preferable to a lease the
            // pool waits on forever.
            try
            {
                if (pinned)
                    handle.Dispose();
            }
            finally
            {
                frame.Dispose();
            }
            throw;
        }
    }
}
