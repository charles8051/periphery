// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Camera.Avalonia;

/// <summary>
/// The pixel work behind <see cref="CameraPreview"/>: a strided row copy and the
/// two YUV → BGRA conversions, plus the one function that picks between them for
/// a frame.
/// </summary>
/// <remarks>
/// <para>
/// <b>No Avalonia here, on purpose.</b> Everything in this class is a total
/// function over spans and scalars — same bytes in, same bytes out, no surface,
/// no lock, no clock, no IO (functional core; ADR-0052 grain). The
/// imperative shell is <see cref="PreviewSurface.Write"/>, which locks the
/// framebuffer and hands the address here as a <see cref="Span{T}"/>.
/// </para>
/// <para>
/// That split is what makes the pixels testable. Avalonia's default headless
/// render interface reports every pixel format as supported and hands out a
/// throwaway <c>Rgba8888</c> buffer at <c>width * 4</c> whatever it was asked
/// for, so a headless test of NV12 support passes without converting anything. A
/// test against this class calls the same code the control calls, with no
/// Avalonia platform in the process at all.
/// </para>
/// <para>
/// <b>Colour.</b> BT.601 limited range ("video range": Y 16–235, chroma 16–240),
/// in the integer form the coefficients are usually published in:
/// </para>
/// <code>
/// C = Y - 16   D = U - 128   E = V - 128
/// R = (298C           + 409E + 128) >> 8
/// G = (298C - 100D    - 208E + 128) >> 8
/// B = (298C + 516D           + 128) >> 8
/// </code>
/// <para>
/// This is a preview, and BT.601 is the right guess for it: UVC cameras
/// overwhelmingly tag SD-range 601, and <see cref="CameraFormat"/> carries no
/// colorimetry to do better with. A 709 source rendered through 601 is slightly
/// oversaturated, which is visible only side by side against a correct decode.
/// Choosing per-frame colorimetry is a job for a real conversion library, and the
/// point at which one is needed is the point at which these two converters should
/// leave this package (issue #318).
/// </para>
/// </remarks>
internal static class PreviewPixels
{
    /// <summary>Bytes per pixel in every surface this class writes into.</summary>
    private const int DestinationBytesPerPixel = 4;

    /// <summary>
    /// Writes <paramref name="frame"/> into <paramref name="destination"/> the way
    /// <paramref name="path"/> says to.
    /// </summary>
    /// <param name="frame">The frame to read. Not disposed, not retained.</param>
    /// <param name="path">
    /// The path <see cref="PreviewPixelFormats.TryGetPath"/> resolved for this
    /// frame's format and dimensions.
    /// </param>
    /// <param name="destination">
    /// The surface's pixels — <paramref name="destinationStride"/> ×
    /// <c>frame.Height</c> bytes, or more.
    /// </param>
    /// <param name="destinationStride">
    /// Bytes from one destination row's start to the next. This is Avalonia's
    /// number (<c>ILockedFramebuffer.RowBytes</c>), not the camera's, and it need
    /// not equal <c>width * 4</c> — a surface is free to pad its rows even though
    /// every frame Periphery delivers has tight ones (ADR-0081 D1).
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="path"/> is <see cref="PreviewPixelPath.DecodeJpeg"/>, which
    /// is not a raw-pixel path, or the frame's planes do not hold the bytes the
    /// path needs.
    /// </exception>
    public static void Write(
        ICameraFrame frame, PreviewPixelPath path, Span<byte> destination, int destinationStride)
    {
        ArgumentNullException.ThrowIfNull(frame);

        int width = frame.Width;
        int height = frame.Height;

        switch (path)
        {
            case PreviewPixelPath.CopyBgra:
            case PreviewPixelPath.CopyRgba:
            {
                // Both are a straight row copy: the camera's byte order already is
                // the surface's, so the only thing that differs is which Avalonia
                // pixel format the surface was created with.
                var plane = frame.GetPlane(0);
                CopyRows(
                    plane.Buffer.Span, plane.Stride, destination, destinationStride,
                    checked(width * DestinationBytesPerPixel), height);
                return;
            }

            case PreviewPixelPath.ConvertYuy2:
            {
                var plane = frame.GetPlane(0);
                Yuy2ToBgra(
                    plane.Buffer.Span, plane.Stride, destination, destinationStride, width, height);
                return;
            }

            case PreviewPixelPath.ConvertNv12:
            {
                if (frame.PlaneCount < 2)
                    throw new ArgumentException(
                        $"An NV12 frame needs a luma and a chroma plane; this one reports "
                            + $"{frame.PlaneCount}.", nameof(frame));
                var luma = frame.GetPlane(0);
                var chroma = frame.GetPlane(1);
                Nv12ToBgra(
                    luma.Buffer.Span, luma.Stride, chroma.Buffer.Span, chroma.Stride,
                    destination, destinationStride, width, height);
                return;
            }

            default:
                throw new ArgumentException(
                    $"{path} does not write raw pixels into a surface.", nameof(path));
        }
    }

    /// <summary>
    /// Copies <paramref name="height"/> rows of <paramref name="rowBytes"/> bytes,
    /// honouring both strides and leaving each destination row's padding untouched.
    /// </summary>
    /// <remarks>
    /// Both strides are honoured even though Periphery's are always tight
    /// (ADR-0081 D1). The source invariant is Periphery's to keep; the destination
    /// stride is Avalonia's to choose, and it is read back from the locked
    /// framebuffer rather than assumed.
    /// </remarks>
    public static void CopyRows(
        ReadOnlySpan<byte> source, int sourceStride,
        Span<byte> destination, int destinationStride,
        int rowBytes, int height)
    {
        ValidateRows(source.Length, sourceStride, destination.Length, destinationStride, rowBytes, height);

        for (int row = 0; row < height; row++)
        {
            source.Slice(row * sourceStride, rowBytes)
                .CopyTo(destination.Slice(row * destinationStride, rowBytes));
        }
    }

    /// <summary>
    /// Converts a packed YUY2 image to BGRA. YUY2 stores one macropixel per four
    /// bytes — <c>Y0 U Y1 V</c> — so two output pixels share a chroma pair and
    /// <paramref name="width"/> must be even.
    /// </summary>
    public static void Yuy2ToBgra(
        ReadOnlySpan<byte> source, int sourceStride,
        Span<byte> destination, int destinationStride,
        int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        if (width % 2 != 0)
            throw new ArgumentException("YUY2 packs two pixels per macropixel; width must be even.", nameof(width));

        int sourceRowBytes = checked(width * 2);
        int destinationRowBytes = checked(width * DestinationBytesPerPixel);
        ValidateRows(
            source.Length, sourceStride, destination.Length, destinationStride,
            sourceRowBytes, height, destinationRowBytes);

        for (int row = 0; row < height; row++)
        {
            var sourceRow = source.Slice(row * sourceStride, sourceRowBytes);
            var destinationRow = destination.Slice(row * destinationStride, destinationRowBytes);

            for (int x = 0, s = 0, d = 0; x < width; x += 2, s += 4, d += 8)
            {
                int u = sourceRow[s + 1];
                int v = sourceRow[s + 3];
                WriteBgra(destinationRow, d, sourceRow[s], u, v);
                WriteBgra(destinationRow, d + DestinationBytesPerPixel, sourceRow[s + 2], u, v);
            }
        }
    }

    /// <summary>
    /// Converts a planar NV12 image to BGRA. NV12 is a full-resolution luma plane
    /// followed by one interleaved <c>U V</c> chroma plane at half resolution in
    /// both axes, so one chroma pair covers a 2×2 block of pixels and both
    /// <paramref name="width"/> and <paramref name="height"/> must be even.
    /// </summary>
    /// <remarks>
    /// <paramref name="chromaStride"/> is bytes from one chroma row to the next.
    /// For a tight NV12 frame that equals the luma stride, not half of it: the
    /// plane holds <c>width / 2</c> two-byte samples per row (ADR-0081 D1, and
    /// the <c>CameraPlane.Width</c> discrepancy documented there).
    /// </remarks>
    public static void Nv12ToBgra(
        ReadOnlySpan<byte> luma, int lumaStride,
        ReadOnlySpan<byte> chroma, int chromaStride,
        Span<byte> destination, int destinationStride,
        int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);
        if (width % 2 != 0)
            throw new ArgumentException("NV12 chroma is subsampled 2:1 horizontally; width must be even.", nameof(width));
        if (height % 2 != 0)
            throw new ArgumentException("NV12 chroma is subsampled 2:1 vertically; height must be even.", nameof(height));

        int destinationRowBytes = checked(width * DestinationBytesPerPixel);
        ValidateRows(
            luma.Length, lumaStride, destination.Length, destinationStride,
            width, height, destinationRowBytes);

        // The chroma plane is its own image: height / 2 rows of width bytes.
        int chromaRowBytes = width;
        int chromaHeight = height / 2;
        if (chromaStride < chromaRowBytes)
            throw new ArgumentException(
                $"Chroma stride {chromaStride} is narrower than a {chromaRowBytes}-byte row.",
                nameof(chromaStride));
        if (chromaHeight > 0 && chroma.Length < ((chromaHeight - 1) * chromaStride) + chromaRowBytes)
            throw new ArgumentException(
                $"Chroma plane holds {chroma.Length} bytes, short of the "
                    + $"{((chromaHeight - 1) * chromaStride) + chromaRowBytes} a {width}x{height} "
                    + "NV12 frame needs.", nameof(chroma));

        for (int row = 0; row < height; row++)
        {
            var lumaRow = luma.Slice(row * lumaStride, width);
            var chromaRow = chroma.Slice((row / 2) * chromaStride, chromaRowBytes);
            var destinationRow = destination.Slice(row * destinationStride, destinationRowBytes);

            for (int x = 0, c = 0, d = 0; x < width; x += 2, c += 2, d += 8)
            {
                int u = chromaRow[c];
                int v = chromaRow[c + 1];
                WriteBgra(destinationRow, d, lumaRow[x], u, v);
                WriteBgra(destinationRow, d + DestinationBytesPerPixel, lumaRow[x + 1], u, v);
            }
        }
    }

    /// <summary>
    /// One pixel, BT.601 limited range, alpha forced opaque. See the class remarks
    /// for the coefficients.
    /// </summary>
    /// <remarks>
    /// Alpha is written as 255 even though the surface is created
    /// <c>AlphaFormat.Opaque</c> and Skia is entitled to ignore the channel. A
    /// surface is reused across frames and across paths, so leaving the byte at
    /// whatever the last write left there is a hazard for the cost of one store.
    /// </remarks>
    private static void WriteBgra(Span<byte> row, int index, int y, int u, int v)
    {
        int c = y - 16;
        int d = u - 128;
        int e = v - 128;

        row[index] = Clamp8(((298 * c) + (516 * d) + 128) >> 8);                    // B
        row[index + 1] = Clamp8(((298 * c) - (100 * d) - (208 * e) + 128) >> 8);    // G
        row[index + 2] = Clamp8(((298 * c) + (409 * e) + 128) >> 8);                // R
        row[index + 3] = 255;                                                       // A
    }

    private static byte Clamp8(int value) => (byte)(value < 0 ? 0 : value > 255 ? 255 : value);

    /// <summary>
    /// Checks that both spans hold the rows about to be walked, so a short buffer
    /// is an <see cref="ArgumentException"/> naming the shortfall rather than an
    /// <see cref="IndexOutOfRangeException"/> from inside a loop.
    /// </summary>
    private static void ValidateRows(
        int sourceLength, int sourceStride,
        int destinationLength, int destinationStride,
        int sourceRowBytes, int height,
        int? destinationRowBytesOrNull = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(height);
        ArgumentOutOfRangeException.ThrowIfNegative(sourceRowBytes);
        int destinationRowBytes = destinationRowBytesOrNull ?? sourceRowBytes;

        if (sourceStride < sourceRowBytes)
            throw new ArgumentException(
                $"Source stride {sourceStride} is narrower than a {sourceRowBytes}-byte row.",
                nameof(sourceStride));
        if (destinationStride < destinationRowBytes)
            throw new ArgumentException(
                $"Destination stride {destinationStride} is narrower than a "
                    + $"{destinationRowBytes}-byte row.", nameof(destinationStride));
        if (height == 0)
            return;

        int sourceNeeded = ((height - 1) * sourceStride) + sourceRowBytes;
        if (sourceLength < sourceNeeded)
            throw new ArgumentException(
                $"Source holds {sourceLength} bytes, short of the {sourceNeeded} that "
                    + $"{height} rows at stride {sourceStride} need.", "source");

        int destinationNeeded = ((height - 1) * destinationStride) + destinationRowBytes;
        if (destinationLength < destinationNeeded)
            throw new ArgumentException(
                $"Destination holds {destinationLength} bytes, short of the {destinationNeeded} "
                    + $"that {height} rows at stride {destinationStride} need.",
                "destination");
    }
}
