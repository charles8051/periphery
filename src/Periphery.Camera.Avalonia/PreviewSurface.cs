// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Periphery.Camera.Avalonia;

/// <summary>
/// What a reusable preview surface has to match to be reused: the geometry and
/// the pixel format of the bitmap Avalonia allocated.
/// </summary>
/// <remarks>
/// <para>
/// <b>Alpha is not on the key</b> because every surface is
/// <see cref="AlphaFormat.Opaque"/> and nothing can vary it. That is not a
/// simplification — it is the one setting that must not vary. Media Foundation
/// maps <c>MFVideoFormat_RGB32</c> to <see cref="CameraPixelFormat.Bgra32"/>, but
/// MF's RGB32 is really BGR<b>X</b>: the fourth byte is padding and arrives as 0.
/// Under <see cref="AlphaFormat.Premul"/> or <see cref="AlphaFormat.Unpremul"/>
/// that zero is read as "fully transparent" and the entire preview renders
/// invisible — a failure that looks like a broken control rather than a wrong
/// enum. The converters write 255 into the channel as well, so the surface is
/// correct under either reading.
/// </para>
/// </remarks>
internal readonly record struct PreviewSurfaceKey(int Width, int Height, PixelFormat Format)
{
    /// <summary>The alpha handling every preview surface is created with.</summary>
    public const AlphaFormat Alpha = AlphaFormat.Opaque;

    /// <summary>
    /// The surface a frame of these dimensions on this path needs.
    /// </summary>
    /// <remarks>
    /// Skia creates a <c>WriteableBitmap</c> natively for <c>Bgra8888</c>,
    /// <c>Rgba8888</c> and <c>Rgb565</c> only; anything else is allocated as
    /// <c>Rgba8888</c> behind a shim that transcodes the whole image every time
    /// the framebuffer is unlocked. Both formats here are in the native set, so
    /// unlocking is free.
    /// </remarks>
    public static PreviewSurfaceKey For(int width, int height, PreviewPixelPath path) => new(
        width,
        height,
        path == PreviewPixelPath.CopyRgba ? PixelFormats.Rgba8888 : PixelFormats.Bgra8888);
}

/// <summary>
/// One bitmap the preview can draw, plus what it takes to write the next frame
/// into it. Either a <see cref="WriteableBitmap"/> the control fills itself, or a
/// <see cref="Bitmap"/> Skia decoded from a JPEG.
/// </summary>
/// <remarks>
/// <para>
/// The imperative shell around <see cref="PreviewPixels"/>: this type owns the
/// framebuffer lock and the pointer, and the pixel work it delegates to is pure.
/// </para>
/// <para>
/// A decoded JPEG is wrapped here too, so the control has one kind of thing in
/// its pending and front slots rather than two. It is never reused —
/// <see cref="Writeable"/> is <see langword="null"/> for it and
/// <see cref="CanReuseFor"/> always says no — because Skia decoded it at a size
/// and format of its own choosing and there is nothing to write into.
/// </para>
/// </remarks>
internal sealed class PreviewSurface : IDisposable
{
    private PreviewSurface(Bitmap image, WriteableBitmap? writeable, PreviewSurfaceKey key)
    {
        Image = image;
        Writeable = writeable;
        Key = key;
    }

    /// <summary>The bitmap to draw. Never null.</summary>
    public Bitmap Image { get; }

    /// <summary>
    /// The same bitmap when the control owns its pixels, or <see langword="null"/>
    /// for a decoded JPEG.
    /// </summary>
    public WriteableBitmap? Writeable { get; }

    /// <summary>The geometry and format <see cref="Writeable"/> was created with.</summary>
    public PreviewSurfaceKey Key { get; }

    /// <summary>Allocates a surface for <paramref name="key"/>.</summary>
    /// <remarks>
    /// 96 dpi, so <c>Bitmap.Size</c> equals the pixel size and the preview's
    /// uniform-fit arithmetic works in camera pixels.
    /// </remarks>
    public static PreviewSurface Create(PreviewSurfaceKey key)
    {
        var bitmap = new WriteableBitmap(
            new PixelSize(key.Width, key.Height),
            new Vector(96, 96),
            key.Format,
            PreviewSurfaceKey.Alpha);
        return new PreviewSurface(bitmap, bitmap, key);
    }

    /// <summary>Wraps a bitmap Skia decoded, which nothing can write into.</summary>
    public static PreviewSurface Decoded(Bitmap image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return new PreviewSurface(image, null, default);
    }

    /// <summary>
    /// Whether this surface can take a frame described by <paramref name="key"/>
    /// without being reallocated.
    /// </summary>
    public bool CanReuseFor(PreviewSurfaceKey key) => Writeable is not null && Key == key;

    /// <summary>
    /// Locks the framebuffer and writes <paramref name="frame"/> into it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The lock is Skia's, and the compositor takes the same one.</b>
    /// <c>WriteableBitmap.Lock()</c> enters a monitor that
    /// <c>WriteableBitmapImpl.Draw</c> also enters, so a write from the capture
    /// thread cannot tear against a composite — but it can block until the
    /// composite finishes, and vice versa. That is why the control never writes
    /// into the surface it most recently published: see
    /// <see cref="CameraPreview"/>'s threading notes.
    /// </para>
    /// <para>
    /// <c>RowBytes</c> is read back from the framebuffer rather than assumed. It
    /// is Avalonia's stride, not the camera's, and the two need not agree even
    /// though every frame Periphery delivers has tight rows (ADR-0081 D1).
    /// </para>
    /// </remarks>
    public void Write(ICameraFrame frame, PreviewPixelPath path)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (Writeable is null)
            throw new InvalidOperationException("A decoded bitmap has no pixels to write into.");

        using var framebuffer = Writeable.Lock();
        int length = checked(framebuffer.RowBytes * framebuffer.Size.Height);
        unsafe
        {
            var destination = new Span<byte>((void*)framebuffer.Address, length);
            PreviewPixels.Write(frame, path, destination, framebuffer.RowBytes);
        }
    }

    /// <summary>
    /// Drops this surface's reference to the bitmap. Not necessarily the last
    /// one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Disposing a bitmap the compositor may still replay is safe, and it is
    /// safe by reference counting rather than by timing.</b> A
    /// <see cref="Bitmap"/> wraps an <c>IRef&lt;IBitmapImpl&gt;</c> — "a
    /// ref-counted wrapper for a disposable object" — and
    /// <c>DrawingContext.DrawBitmap</c> takes that <c>IRef</c>, not the impl, so
    /// the recorded draw node holds a <c>Clone()</c> of it with the refcount
    /// incremented. <see cref="Bitmap.Dispose"/> releases one reference; the
    /// native surface is freed when the last one goes, which is after the
    /// compositor disposes the draw node. There is no fence to wait on here and
    /// none is needed (Peanut Gallery turn 1).
    /// </para>
    /// <para>
    /// This is the same discipline the control had before it reused surfaces:
    /// <c>Render</c> disposed the outgoing front bitmap on the UI thread while
    /// the previous frame's draw list was still outstanding.
    /// </para>
    /// </remarks>
    public void Dispose() => Image.Dispose();
}
