// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Camera;

/// <summary>
/// LINQ-style filters and orderers for picking a <see cref="CameraFormat"/>
/// out of a <see cref="CameraSnapshot.Formats"/> list. These are pure
/// extension methods over <see cref="IEnumerable{CameraFormat}"/> — they
/// introduce no new abstraction and compose with standard LINQ operators.
/// </summary>
/// <example>
/// <code>
/// var format = snapshot.Formats
///     .WithPixelFormat(CameraPixelFormat.Mjpeg)
///     .WithinBox(1280, 720)
///     .ByHighestArea()
///     .ThenByHighestFrameRate()
///     .FirstOrDefault();
/// </code>
/// </example>
public static class CameraFormatSelectors
{
    // ── filters ────────────────────────────────────────────────────────

    /// <summary>Keeps only formats whose <see cref="CameraFormat.PixelFormat"/> matches.</summary>
    public static IEnumerable<CameraFormat> WithPixelFormat(
        this IEnumerable<CameraFormat> source, CameraPixelFormat pixelFormat)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.Where(f => f.PixelFormat == pixelFormat);
    }

    /// <summary>Keeps only formats whose pixel format is in the supplied set. Empty set is a no-op.</summary>
    public static IEnumerable<CameraFormat> WithAnyPixelFormat(
        this IEnumerable<CameraFormat> source, params CameraPixelFormat[] pixelFormats)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(pixelFormats);
        if (pixelFormats.Length == 0) return source;
        var set = new HashSet<CameraPixelFormat>(pixelFormats);
        return source.Where(f => set.Contains(f.PixelFormat));
    }

    /// <summary>
    /// Keeps only formats that fit inside <paramref name="maxWidth"/> ×
    /// <paramref name="maxHeight"/> (inclusive on both axes).
    /// </summary>
    public static IEnumerable<CameraFormat> WithinBox(
        this IEnumerable<CameraFormat> source, int maxWidth, int maxHeight)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxHeight);
        return source.Where(f => f.Width <= maxWidth && f.Height <= maxHeight);
    }

    /// <summary>
    /// Keeps only formats at or above <paramref name="minWidth"/> ×
    /// <paramref name="minHeight"/> on both axes.
    /// </summary>
    public static IEnumerable<CameraFormat> AtLeastResolution(
        this IEnumerable<CameraFormat> source, int minWidth, int minHeight)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minHeight);
        return source.Where(f => f.Width >= minWidth && f.Height >= minHeight);
    }

    /// <summary>
    /// Keeps only formats whose <see cref="CameraFormat.MaxFrameRate"/> is
    /// at least <paramref name="minFps"/>.
    /// </summary>
    public static IEnumerable<CameraFormat> AtLeastFrameRate(
        this IEnumerable<CameraFormat> source, Rational minFps)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.Where(f => f.MaxFrameRate >= minFps);
    }

    // ── orderers ──────────────────────────────────────────────────────

    /// <summary>Orders formats by pixel area (W × H) descending.</summary>
    public static IOrderedEnumerable<CameraFormat> ByHighestArea(
        this IEnumerable<CameraFormat> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.OrderByDescending(AreaKey);
    }

    /// <summary>Continues an existing order by pixel area (W × H) descending.</summary>
    public static IOrderedEnumerable<CameraFormat> ThenByHighestArea(
        this IOrderedEnumerable<CameraFormat> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.ThenByDescending(AreaKey);
    }

    /// <summary>Orders formats by <see cref="CameraFormat.MaxFrameRate"/> descending.</summary>
    public static IOrderedEnumerable<CameraFormat> ByHighestFrameRate(
        this IEnumerable<CameraFormat> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.OrderByDescending(f => f.MaxFrameRate);
    }

    /// <summary>Continues an existing order by <see cref="CameraFormat.MaxFrameRate"/> descending.</summary>
    public static IOrderedEnumerable<CameraFormat> ThenByHighestFrameRate(
        this IOrderedEnumerable<CameraFormat> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.ThenByDescending(f => f.MaxFrameRate);
    }

    /// <summary>
    /// Stable two-tier ordering: formats matching <paramref name="preferred"/>
    /// come first, the rest follow in their existing order. Useful when the
    /// preferred pixel format is desired but a fallback is acceptable.
    /// </summary>
    /// <example>
    /// <code>
    /// // Try MJPEG first; fall back to anything else within the box.
    /// var format = snapshot.Formats
    ///     .WithinBox(1280, 720)
    ///     .PreferPixelFormat(CameraPixelFormat.Mjpeg)
    ///     .ThenByHighestArea()
    ///     .FirstOrDefault();
    /// </code>
    /// </example>
    public static IOrderedEnumerable<CameraFormat> PreferPixelFormat(
        this IEnumerable<CameraFormat> source, CameraPixelFormat preferred)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.OrderBy(f => f.PixelFormat == preferred ? 0 : 1);
    }

    private static long AreaKey(CameraFormat f) => (long)f.Width * f.Height;
}
