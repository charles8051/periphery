// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Camera.Internal;

/// <summary>
/// How one backend buffer becomes one delivered frame: which source rows land
/// where in the pooled buffer, and whether the whole thing can go as a single
/// <c>memcpy</c>.
/// </summary>
/// <remarks>
/// A pure value produced by <see cref="FrameCopy.Plan(in RawCameraFrame)"/> and
/// consumed by <see cref="FrameCopy.Execute"/> — the split ADR-0052 asks for,
/// with the layout arithmetic in a total function and only the execution
/// touching memory.
/// </remarks>
internal readonly struct FrameCopyPlan
{
    /// <summary>Where each plane sits in the backend's buffer.</summary>
    public required IReadOnlyList<RawPlaneDescriptor> Source { get; init; }

    /// <summary>
    /// Where each plane sits in the pooled buffer — the tight layout, per
    /// ADR-0081 D1. Also the descriptor set the delivered
    /// <see cref="CameraPlane"/> list is built from.
    /// </summary>
    public required IReadOnlyList<RawPlaneDescriptor> Target { get; init; }

    /// <summary>Bytes the pooled buffer must hold, and the length of the
    /// delivered <see cref="ICameraFrame.ContiguousBuffer"/>.</summary>
    public required int TargetLength { get; init; }

    /// <summary>Whether the source stores each plane's rows bottom-to-top and
    /// the copy has to flip them (ADR-0081 D8).</summary>
    public required bool BottomUp { get; init; }

    /// <summary>
    /// Whether the source and target layouts are identical and top-down, so the
    /// copy is one bulk run rather than a row loop.
    /// </summary>
    public required bool IsBulk { get; init; }
}

/// <summary>
/// Plans and performs the pool's frame copy. Removes row padding and normalises
/// row order so that every uncompressed frame the pool delivers has tight rows
/// (ADR-0081 D1, D2, D8).
/// </summary>
internal static class FrameCopy
{
    /// <inheritdoc cref="Plan(CameraPixelFormat, int, int, int, IReadOnlyList{RawPlaneDescriptor}, bool)"/>
    internal static FrameCopyPlan Plan(in RawCameraFrame raw) =>
        Plan(raw.PixelFormat, raw.Width, raw.Height, raw.Data.Length, raw.Planes, raw.BottomUp);

    /// <summary>
    /// Works out the copy from the source's own description of itself. Pure:
    /// scalars and descriptors in, a plan out, no buffer touched.
    /// </summary>
    /// <param name="format">The frame's pixel format.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="sourceLength">Bytes the backend delivered.</param>
    /// <param name="sourcePlanes">
    /// The source's per-plane layout. Null or empty is read as "already the
    /// tight layout" — the only interpretation available, and the one every
    /// caller in the repo satisfies explicitly since ADR-0081 D3 made
    /// <see cref="RawCameraFrame.Planes"/> mandatory for uncompressed frames.
    /// </param>
    /// <param name="bottomUp">Whether the source stores rows bottom-to-top.</param>
    /// <exception cref="ArgumentException">
    /// The source layout cannot be read as the described format at the described
    /// size — a plane count, extent, stride, or offset the copy could not honour
    /// without running off the end of the buffer or silently truncating a row.
    /// </exception>
    internal static FrameCopyPlan Plan(
        CameraPixelFormat format,
        int width,
        int height,
        int sourceLength,
        IReadOnlyList<RawPlaneDescriptor>? sourcePlanes,
        bool bottomUp)
    {
        var target = PlaneLayout.DescribeTightPlanes(format, width, height);

        if (target is null)
        {
            // MJPEG / Unknown: an opaque run with no rows to de-pad and no
            // direction to normalise (ADR-0081 D7). It goes across verbatim, at
            // whatever length the encoder produced, and the single delivered
            // plane spans it — the shape consumers have always seen.
            var opaque = new RawPlaneDescriptor[]
            {
                new()
                {
                    Offset = 0,
                    Length = sourceLength,
                    Stride = CameraFrameLayout.BytesPerRow(format, width),
                    Width = width,
                    Height = height,
                },
            };
            return new FrameCopyPlan
            {
                Source = opaque,
                Target = opaque,
                TargetLength = sourceLength,
                BottomUp = false,
                IsBulk = true,
            };
        }

        RejectOddChromaGeometry(format, width, height);

        var source = sourcePlanes is { Count: > 0 } ? sourcePlanes : target;
        if (source.Count != target.Count)
            throw new ArgumentException(
                $"A {format} frame has {target.Count} plane(s) but the source described "
                    + $"{source.Count}.", nameof(sourcePlanes));

        for (int i = 0; i < source.Count; i++)
            ValidatePlane(format, sourceLength, i, source[i], target[i]);

        return new FrameCopyPlan
        {
            Source = source,
            Target = target,
            TargetLength = target[^1].Offset + target[^1].Length,
            BottomUp = bottomUp,
            // ADR-0081 D2: the precondition for the bulk copy is layout
            // equality, not tight strides. Tight rows alone let two frames
            // through that a bulk copy corrupts without erroring — a source
            // whose second plane sits past the end of the first (an inter-plane
            // gap the destination has no room for), and a bottom-up frame whose
            // |pitch| happens to equal the tight row width.
            IsBulk = !bottomUp && LayoutsMatch(source, target),
        };
    }

    /// <summary>
    /// Copies <paramref name="source"/> into <paramref name="destination"/>
    /// according to <paramref name="plan"/>. The only part of the frame copy
    /// that touches memory.
    /// </summary>
    internal static void Execute(
        in FrameCopyPlan plan, ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (plan.IsBulk)
        {
            source[..plan.TargetLength].CopyTo(destination);
            return;
        }

        for (int i = 0; i < plan.Target.Count; i++)
        {
            var s = plan.Source[i];
            var t = plan.Target[i];

            // The target stride is the tight row width by construction, so it is
            // also exactly how many bytes of each source row carry image data.
            int rowBytes = t.Stride;
            for (int row = 0; row < t.Height; row++)
            {
                int sourceRow = plan.BottomUp ? t.Height - 1 - row : row;
                source.Slice(s.Offset + (sourceRow * s.Stride), rowBytes)
                    .CopyTo(destination.Slice(t.Offset + (row * t.Stride), rowBytes));
            }
        }
    }

    // 4:2:0 chroma is half-resolution in both axes, and every layout in this
    // library floors that division: PlaneLayout gives the chroma plane
    // `height / 2` rows and `width / 2` samples, while CameraFrameLayout charges
    // the frame `lumaSize + lumaSize / 2` bytes. At an odd dimension those two
    // roundings disagree, and there is no answer to "which half-sample does the
    // last column hold" that both agree on. Neither UVC nor either backend can
    // negotiate such a mode, so this rejects rather than inventing a convention
    // — and it rejects here, with the dimension named, rather than a few lines
    // later as a tight-row invariant violation that says only that the numbers
    // did not add up.
    private static void RejectOddChromaGeometry(CameraPixelFormat format, int width, int height)
    {
        if (format is not (CameraPixelFormat.Nv12 or CameraPixelFormat.Nv21
            or CameraPixelFormat.I420 or CameraPixelFormat.Yv12))
            return;

        if ((width & 1) != 0 || (height & 1) != 0)
            throw new ArgumentException(
                $"A {format} frame needs even dimensions for its half-resolution chroma plane, "
                    + $"but this one is {width}x{height}.");
    }

    private static bool LayoutsMatch(
        IReadOnlyList<RawPlaneDescriptor> source, IReadOnlyList<RawPlaneDescriptor> target)
    {
        for (int i = 0; i < source.Count; i++)
        {
            var s = source[i];
            var t = target[i];
            if (s.Offset != t.Offset || s.Stride != t.Stride
                || s.Width != t.Width || s.Height != t.Height)
                return false;
        }
        return true;
    }

    // A source that disagrees with the format's geometry, or that points past
    // the bytes the backend actually handed over, is a backend bug. Saying so
    // here beats the alternatives: a narrower-than-tight stride would read the
    // next row's bytes as this row's, and an out-of-range offset would surface
    // as an ArgumentOutOfRangeException from a Span slice with nothing in the
    // message about which plane or which frame.
    private static void ValidatePlane(
        CameraPixelFormat format, int sourceLength, int index,
        in RawPlaneDescriptor source, in RawPlaneDescriptor target)
    {
        if (source.Width != target.Width || source.Height != target.Height)
            throw new ArgumentException(
                $"Plane {index} of a {format} frame is {target.Width}x{target.Height} samples but "
                    + $"the source described {source.Width}x{source.Height}.");

        if (source.Stride < target.Stride)
            throw new ArgumentException(
                $"Plane {index} of a {format} frame needs at least {target.Stride} bytes per row "
                    + $"but the source reported a {source.Stride}-byte stride.");

        // Offset + Length is the slice the descriptor claims. It has to exist.
        if (source.Offset < 0 || source.Length < 0
            || (long)source.Offset + source.Length > sourceLength)
            throw new ArgumentException(
                $"Plane {index} of a {format} frame claims bytes {source.Offset}.."
                    + $"{(long)source.Offset + source.Length} of the {sourceLength} the source "
                    + "delivered.");

        // And the rows have to fit inside that slice. Only (Height - 1) whole
        // strides plus one tight row are read, because a producer is entitled to
        // stop after the last row's meaningful bytes rather than pad past them —
        // but a descriptor that names a slice shorter than its own rows would
        // otherwise have the copy read into whatever plane comes next.
        long read = ((long)(source.Height - 1) * source.Stride) + target.Stride;
        if (read > source.Length)
            throw new ArgumentException(
                $"Plane {index} of a {format} frame needs {read} bytes for {source.Height} rows "
                    + $"of {source.Stride} but declares a {source.Length}-byte extent.");
    }
}
