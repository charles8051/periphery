// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Globalization;
using System.Runtime.CompilerServices;

namespace Periphery.Camera;

/// <summary>
/// Filename pattern for <see cref="CameraFrameSinks.SaveToDirectoryAsync"/>.
/// </summary>
public enum CameraFrameNaming
{
    /// <summary>Zero-padded sequential index, e.g. <c>frame-0001.jpg</c>.</summary>
    Sequential = 0,

    /// <summary>Frame timestamp in milliseconds, e.g. <c>frame-54542709.jpg</c>.</summary>
    Timestamp,
}

/// <summary>Options controlling how <see cref="CameraFrameSinks.SaveToDirectoryAsync"/> writes frame files.</summary>
/// <param name="Naming">Filename pattern. Defaults to <see cref="CameraFrameNaming.Sequential"/>.</param>
/// <param name="FilenamePrefix">Prefix prepended to every filename. Defaults to <c>"frame"</c>.</param>
/// <param name="SequentialPadding">
/// Width of the zero-padded sequential index when
/// <see cref="Naming"/> is <see cref="CameraFrameNaming.Sequential"/>. Defaults to 4.
/// </param>
public sealed record CameraFrameWriteOptions(
    CameraFrameNaming Naming = CameraFrameNaming.Sequential,
    string? FilenamePrefix = null,
    int SequentialPadding = 4)
{
    /// <summary>Default options: sequential naming, <c>"frame"</c> prefix, 4-digit padding.</summary>
    public static readonly CameraFrameWriteOptions Default = new();
}

/// <summary>
/// Byte-level sinks for camera frames. These write <see cref="ICameraFrame.ContiguousBuffer"/>
/// without inspecting or re-encoding pixels — they are appropriate for diagnostic capture
/// and for raw streaming. Encoded video output (H.264, MP4, RTSP, etc.) belongs in
/// <c>Periphery.Camera.Pipelines</c> per ADR-0036; the rule for this layer is
/// "no pixel interpretation in core".
/// </summary>
public static class CameraFrameSinks
{
    /// <summary>
    /// Writes each frame's <see cref="ICameraFrame.ContiguousBuffer"/> to its own file
    /// in <paramref name="directory"/>. The directory is created if missing. File extension is
    /// <c>.jpg</c> for <see cref="CameraPixelFormat.Mjpeg"/> (already JPEG-encoded bytes) and
    /// <c>.raw</c> for everything else, with the resolution and pixel format encoded in the
    /// filename so a viewer can interpret it.
    /// </summary>
    /// <returns>The number of frames written.</returns>
    /// <remarks>
    /// Each enumerated frame is disposed by the sink — if the source produces leased frames,
    /// their pool buffers are returned promptly.
    /// </remarks>
    public static async Task<int> SaveToDirectoryAsync(
        this IAsyncEnumerable<ICameraFrame> source,
        string directory,
        CameraFrameWriteOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrEmpty(directory);
        options ??= CameraFrameWriteOptions.Default;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.SequentialPadding);

        Directory.CreateDirectory(directory);

        int count = 0;
        await foreach (var frame in source.WithCancellation(ct).ConfigureAwait(false))
        {
            using (frame)
            {
                count++;
                var path = BuildPath(directory, frame, options, count);
                await using var fs = new FileStream(
                    path, FileMode.Create, FileAccess.Write, FileShare.None,
                    bufferSize: 4096, useAsync: true);
                await fs.WriteAsync(frame.ContiguousBuffer, ct).ConfigureAwait(false);
            }
        }
        return count;
    }

    /// <summary>
    /// Concatenates each frame's <see cref="ICameraFrame.ContiguousBuffer"/> to
    /// <paramref name="destination"/>. Useful for raw streaming into a memory stream,
    /// pipe, or single packed file.
    /// </summary>
    /// <returns>The number of frames written.</returns>
    /// <remarks>
    /// Each enumerated frame is disposed by the sink. The destination stream is not
    /// flushed or disposed by the sink — that remains the caller's responsibility.
    /// </remarks>
    public static async Task<int> WriteContiguousToAsync(
        this IAsyncEnumerable<ICameraFrame> source,
        Stream destination,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
            throw new ArgumentException("Destination stream is not writable.", nameof(destination));

        int count = 0;
        await foreach (var frame in source.WithCancellation(ct).ConfigureAwait(false))
        {
            using (frame)
            {
                count++;
                await destination.WriteAsync(frame.ContiguousBuffer, ct).ConfigureAwait(false);
            }
        }
        return count;
    }

    /// <summary>
    /// Promotes each leased frame to an <see cref="OwnedCameraFrame"/> at the
    /// enumerator boundary. The lease is disposed by this method as soon as the copy
    /// is made — downstream consumers receive owned frames that can be retained
    /// independently of the capture loop's pool budget.
    /// </summary>
    public static async IAsyncEnumerable<OwnedCameraFrame> ToOwnedAsync(
        this IAsyncEnumerable<LeasedCameraFrame> source,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        await foreach (var frame in source.WithCancellation(ct).ConfigureAwait(false))
        {
            OwnedCameraFrame owned;
            using (frame) { owned = frame.Copy(); }
            yield return owned;
        }
    }

    private static string BuildPath(
        string directory, ICameraFrame frame, CameraFrameWriteOptions options, int sequence)
    {
        var inv = CultureInfo.InvariantCulture;
        string ext = ExtensionFor(frame.PixelFormat);
        string prefix = options.FilenamePrefix ?? "frame";
        string body = options.Naming switch
        {
            CameraFrameNaming.Timestamp =>
                prefix + "-" + frame.Timestamp.TotalMilliseconds.ToString("F0", inv),
            _ => prefix + "-" + sequence.ToString(inv).PadLeft(options.SequentialPadding, '0'),
        };

        // Raw byte dumps are uninterpretable without dimensions and format —
        // encode them in the filename so a viewer (ffplay, ImageMagick) can parse them.
        if (ext == ".raw")
            body = body + "-" +
                frame.Width.ToString(inv) + "x" + frame.Height.ToString(inv) +
                "-" + frame.PixelFormat;

        return Path.Combine(directory, body + ext);
    }

    private static string ExtensionFor(CameraPixelFormat pixelFormat) => pixelFormat switch
    {
        CameraPixelFormat.Mjpeg => ".jpg",
        _ => ".raw",
    };
}
