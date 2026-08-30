// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Text;
using Microsoft.Extensions.Logging;

namespace Periphery.Camera;

/// <summary>
/// Fluent builder for <see cref="CameraSession"/>. Provides discoverable
/// shortcuts for the common cases (pick the highest-resolution MJPEG
/// within 1280×720, etc.) plus a <see cref="UseFormat(Func{CameraSnapshot, CameraFormat})"/>
/// escape hatch for snapshot-aware delegate selection — see ADR-0040
/// Decision 4 / 4a.
/// </summary>
/// <remarks>
/// <para>
/// Obtain via <see cref="CameraSession.For(DeviceInfo)"/>. The builder is
/// purely additive over the records-based factory
/// (<see cref="CameraSession.OpenAsync(DeviceInfo, CameraConfiguration, CameraSessionOptions?, CancellationToken, ILogger{CameraSession}?, TimeProvider?)"/>)
/// — passing a typed <see cref="CameraConfiguration"/> remains the canonical
/// path for callers who construct the format up front.
/// </para>
/// <para>
/// Usage:
/// <code>
/// await using var session = await CameraSession
///     .For(device)
///     .PreferMjpeg()
///     .MaxResolution(1280, 720)
///     .OpenAsync(ct);
/// </code>
/// </para>
/// </remarks>
public sealed class CameraSessionBuilder
{
    private readonly DeviceInfo _device;

    // Filter criteria.
    private CameraPixelFormat? _preferredPixelFormat;
    private CameraPixelFormat[]? _allowedPixelFormats;
    private (int Width, int Height)? _maxResolution;
    private (int Width, int Height)? _minResolution;
    private Rational? _minFrameRate;

    // Configuration extras.
    private Rational? _targetFrameRate;

    // Options.
    private CameraSessionOptions? _sessionOptions;
    private ILogger<CameraSession>? _logger;
    private TimeProvider? _timeProvider;

    // Escape hatches (ADR-0040 §4a).
    private Func<CameraSnapshot, CameraFormat>? _formatSelector;
    private Func<CameraSnapshot, CancellationToken, ValueTask<CameraFormat>>? _asyncFormatSelector;

    internal CameraSessionBuilder(DeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        _device = device;
    }

    // ── Format preference ────────────────────────────────────────────

    /// <summary>
    /// Prefer this pixel format. Formats not matching are still acceptable
    /// fallbacks if no preferred format meets the other criteria — use
    /// <see cref="AllowOnlyPixelFormats"/> for strict filtering.
    /// </summary>
    public CameraSessionBuilder PreferPixelFormat(CameraPixelFormat format)
    {
        _preferredPixelFormat = format;
        return this;
    }

    /// <summary>Convenience for <c>PreferPixelFormat(CameraPixelFormat.Mjpeg)</c>.</summary>
    public CameraSessionBuilder PreferMjpeg() => PreferPixelFormat(CameraPixelFormat.Mjpeg);

    /// <summary>Convenience for <c>PreferPixelFormat(CameraPixelFormat.Nv12)</c>.</summary>
    public CameraSessionBuilder PreferNv12() => PreferPixelFormat(CameraPixelFormat.Nv12);

    /// <summary>Convenience for <c>PreferPixelFormat(CameraPixelFormat.Yuy2)</c>.</summary>
    public CameraSessionBuilder PreferYuy2() => PreferPixelFormat(CameraPixelFormat.Yuy2);

    /// <summary>
    /// Strict filter: reject formats whose pixel format is not in the
    /// supplied set. This is stricter than <see cref="PreferPixelFormat"/>,
    /// which only adjusts ordering.
    /// </summary>
    public CameraSessionBuilder AllowOnlyPixelFormats(params CameraPixelFormat[] formats)
    {
        ArgumentNullException.ThrowIfNull(formats);
        if (formats.Length == 0)
            throw new ArgumentException(
                "At least one pixel format is required.", nameof(formats));
        _allowedPixelFormats = formats;
        return this;
    }

    // ── Resolution and frame rate ────────────────────────────────────

    /// <summary>Reject formats larger than <paramref name="width"/> × <paramref name="height"/>.</summary>
    public CameraSessionBuilder MaxResolution(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        _maxResolution = (width, height);
        return this;
    }

    /// <summary>Reject formats smaller than <paramref name="width"/> × <paramref name="height"/>.</summary>
    public CameraSessionBuilder MinResolution(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        _minResolution = (width, height);
        return this;
    }

    /// <summary>Reject formats whose <see cref="CameraFormat.MaxFrameRate"/> is below <paramref name="fps"/>.</summary>
    public CameraSessionBuilder MinFrameRate(Rational fps)
    {
        _minFrameRate = fps;
        return this;
    }

    // ── Configuration extras ──────────────────────────────────────────

    /// <summary>Sets <see cref="CameraConfiguration.TargetFrameRate"/>.</summary>
    public CameraSessionBuilder TargetFrameRate(Rational fps)
    {
        _targetFrameRate = fps;
        return this;
    }

    // ── Session options ────────────────────────────────────────────────

    /// <summary>Replaces the <see cref="CameraSessionOptions"/> entirely.</summary>
    public CameraSessionBuilder WithSessionOptions(CameraSessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _sessionOptions = options;
        return this;
    }

    /// <summary>
    /// Mutates the current <see cref="CameraSessionOptions"/> via a record
    /// transformer (typically <c>o =&gt; o with { BufferCount = 4 }</c>).
    /// </summary>
    public CameraSessionBuilder WithSessionOptions(
        Func<CameraSessionOptions, CameraSessionOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _sessionOptions = configure(_sessionOptions ?? new CameraSessionOptions());
        return this;
    }

    /// <summary>
    /// Plumbs an <see cref="ILogger{T}"/> through to the opened
    /// <see cref="CameraSession"/>. When omitted, the session uses
    /// <see cref="Microsoft.Extensions.Logging.Abstractions.NullLogger{T}.Instance"/>
    /// — i.e. no logging. See <c>docs/patterns/logging-and-diagnostics.md</c>.
    /// </summary>
    public CameraSessionBuilder WithLogger(ILogger<CameraSession>? logger)
    {
        _logger = logger;
        return this;
    }

    /// <summary>
    /// Injects the <see cref="TimeProvider"/> the opened
    /// <see cref="CameraSession"/> uses for every frame-timeout, bounded-stop
    /// delay, and producer-duration measurement. Defaults to
    /// <see cref="TimeProvider.System"/> when omitted, so callers are
    /// unaffected; tests pass a <c>FakeTimeProvider</c> to drive the
    /// timeout-vs-cancellation decision deterministically (ADR-0052; review
    /// finding 2.2).
    /// </summary>
    public CameraSessionBuilder WithTimeProvider(TimeProvider? timeProvider)
    {
        _timeProvider = timeProvider;
        return this;
    }

    // ── Escape hatches (ADR-0040 §4a) ──────────────────────────────────

    /// <summary>
    /// Snapshot-aware delegate selection. Takes precedence over the fluent
    /// criteria — <see cref="MaxResolution"/>, <see cref="PreferPixelFormat"/>,
    /// etc. are ignored when this is set.
    /// </summary>
    public CameraSessionBuilder UseFormat(Func<CameraSnapshot, CameraFormat> selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        _formatSelector = selector;
        _asyncFormatSelector = null;
        return this;
    }

    /// <summary>
    /// Async snapshot-aware delegate selection — for callers that need to
    /// consult external state (config service, policy lookup) before
    /// choosing a format. Takes precedence over the fluent criteria.
    /// </summary>
    public CameraSessionBuilder UseFormat(
        Func<CameraSnapshot, CancellationToken, ValueTask<CameraFormat>> selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        _asyncFormatSelector = selector;
        _formatSelector = null;
        return this;
    }

    // ── Terminal ───────────────────────────────────────────────────────

    /// <summary>
    /// Reads the camera snapshot, materializes a <see cref="CameraConfiguration"/>
    /// from the configured criteria (or the
    /// <see cref="UseFormat(Func{CameraSnapshot, CameraFormat})"/> delegate),
    /// and opens a session.
    /// </summary>
    /// <exception cref="CameraConfigurationException">
    /// Thrown when no advertised format satisfies the configured criteria.
    /// </exception>
    public async Task<CameraSession> OpenAsync(CancellationToken ct = default)
    {
        var snapshot = await CameraDevice.ReadSnapshotAsync(_device, ct).ConfigureAwait(false);

        CameraFormat format;
        if (_asyncFormatSelector is not null)
            format = await _asyncFormatSelector(snapshot, ct).ConfigureAwait(false);
        else if (_formatSelector is not null)
            format = _formatSelector(snapshot);
        else
            format = SelectFromCriteria(snapshot)
                ?? throw new CameraConfigurationException(
                    BuildNoMatchMessage(snapshot), _device.Id);

        var config = new CameraConfiguration(format, _targetFrameRate);
        return await CameraSession.OpenAsync(_device, config, _sessionOptions, ct, _logger, _timeProvider)
            .ConfigureAwait(false);
    }

    private CameraFormat? SelectFromCriteria(CameraSnapshot snapshot)
    {
        IEnumerable<CameraFormat> candidates = snapshot.Formats;

        if (_allowedPixelFormats is { } allowed)
            candidates = candidates.WithAnyPixelFormat(allowed);
        if (_maxResolution is { } max)
            candidates = candidates.WithinBox(max.Width, max.Height);
        if (_minResolution is { } min)
            candidates = candidates.AtLeastResolution(min.Width, min.Height);
        if (_minFrameRate is { } minFps)
            candidates = candidates.AtLeastFrameRate(minFps);

        IOrderedEnumerable<CameraFormat> ordered =
            _preferredPixelFormat is { } preferred
                ? candidates.PreferPixelFormat(preferred)
                    .ThenByHighestArea()
                    .ThenByHighestFrameRate()
                : candidates.ByHighestArea()
                    .ThenByHighestFrameRate();

        return ordered.FirstOrDefault();
    }

    private string BuildNoMatchMessage(CameraSnapshot snapshot)
    {
        var sb = new StringBuilder("No camera format matches the requested criteria.");
        if (_maxResolution is { } max)
            sb.Append($" max={max.Width}x{max.Height}");
        if (_minResolution is { } min)
            sb.Append($" min={min.Width}x{min.Height}");
        if (_minFrameRate is { } fps)
            sb.Append($" min-fps={fps}");
        if (_preferredPixelFormat is { } pref)
            sb.Append($" prefer={pref}");
        if (_allowedPixelFormats is { Length: > 0 } allowed)
            sb.Append($" allow=[{string.Join(",", allowed)}]");
        sb.AppendLine();
        sb.AppendLine("Available formats:");
        foreach (var f in snapshot.Formats)
            sb.AppendLine($"  {f.Width}x{f.Height}  {f.PixelFormat}  ({f.MaxFrameRate} fps)");
        return sb.ToString();
    }
}
