// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Text;

namespace Periphery.Camera.Avalonia;

/// <summary>
/// Picks the format <see cref="CameraPreview"/> opens the camera with, out of
/// what the camera advertises.
/// </summary>
/// <remarks>
/// <para>
/// Pure and total (ADR-0052 grain, functional core): a list of
/// advertised formats and a resolution box in, one format or
/// <see langword="null"/> out. No Avalonia types, no device, no IO — the control
/// hands the result to <c>CameraSessionBuilder.UseFormat</c>, which is the one
/// call that touches a camera.
/// </para>
/// <para>
/// <b>Why not the fluent criteria.</b> <c>AllowOnlyPixelFormats</c> filters and
/// <c>PreferPixelFormat</c> promotes exactly one format; neither expresses a
/// ranked set. <c>UseFormat</c> takes precedence over the fluent criteria, so the
/// resolution box is applied here instead of by <c>MaxResolution</c>.
/// </para>
/// </remarks>
internal static class PreviewFormatChoice
{
    /// <summary>
    /// The best displayable format within <paramref name="maxWidth"/> ×
    /// <paramref name="maxHeight"/>, or <see langword="null"/> when the camera
    /// advertises none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ordered by area, then frame rate, then
    /// <see cref="PreviewPixelFormats.Rank"/>. <b>Format preference is the last
    /// key, deliberately.</b> Leading with it would trade a 1280×720 MJPEG stream
    /// for a 320×240 BGRA32 one because the second needs no decode, which is a
    /// worse preview by every measure a viewer can see. Cameras advertise the
    /// same resolution and frame rate in several formats — that is exactly where
    /// the tie happens and where the preference decides, which is where it is
    /// worth something.
    /// </para>
    /// <para>
    /// The trailing width and format keys break ties no camera should produce
    /// (two entries identical in area, rate and format rank), so the choice is a
    /// function of the list's contents rather than its order.
    /// </para>
    /// </remarks>
    public static CameraFormat? Select(
        IReadOnlyList<CameraFormat> formats, int maxWidth, int maxHeight)
    {
        ArgumentNullException.ThrowIfNull(formats);

        return Displayable(formats, maxWidth, maxHeight)
            .OrderByDescending(f => (long)f.Width * f.Height)
            .ThenByDescending(f => f.MaxFrameRate)
            .ThenBy(f => PreviewPixelFormats.Rank(f.PixelFormat))
            .ThenByDescending(f => f.Width)
            .ThenBy(f => (int)f.PixelFormat)
            .FirstOrDefault();
    }

    /// <summary>
    /// The message a failed <see cref="Select"/> becomes. Names the box, the
    /// formats the control can display, and everything the camera actually
    /// offered — a preview that refuses to open should say what it wanted and
    /// what it was given, in one message, without a second round trip to the
    /// device.
    /// </summary>
    public static string DescribeNoMatch(
        IReadOnlyList<CameraFormat> formats, int maxWidth, int maxHeight)
    {
        ArgumentNullException.ThrowIfNull(formats);

        var sb = new StringBuilder();
        sb.Append("No camera format within ").Append(maxWidth).Append('x').Append(maxHeight)
            .AppendLine(" can be displayed by CameraPreview.");
        sb.Append("Displayable formats, most preferred first: ")
            .AppendLine(string.Join(", ", PreviewPixelFormats.Displayable));
        sb.AppendLine("Available formats:");
        if (formats.Count == 0)
        {
            sb.AppendLine("  (the camera advertised none)");
        }
        else
        {
            foreach (var f in formats)
            {
                sb.Append("  ").Append(f.Width).Append('x').Append(f.Height)
                    .Append("  ").Append(f.PixelFormat)
                    .Append("  (").Append(f.MaxFrameRate).AppendLine(" fps)");
            }
        }
        return sb.ToString();
    }

    private static IEnumerable<CameraFormat> Displayable(
        IReadOnlyList<CameraFormat> formats, int maxWidth, int maxHeight)
    {
        foreach (var format in formats)
        {
            if (format is null)
                continue;
            if (format.Width > maxWidth || format.Height > maxHeight)
                continue;
            if (PreviewPixelFormats.TryGetPath(format.PixelFormat, format.Width, format.Height, out _))
                yield return format;
        }
    }
}
