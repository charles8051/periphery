// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Camera;

/// <summary>
/// Pixel formats that <see cref="CameraDevice"/> backends may report and produce.
/// </summary>
public enum CameraPixelFormat
{
    Unknown = 0,

    // ── Compressed ─────────────────────────────────────────────────────
    Mjpeg,

    // ── Packed YUV ─────────────────────────────────────────────────────
    Yuy2,
    Uyvy,

    // ── Planar YUV ─────────────────────────────────────────────────────
    Nv12,
    I420,
    Yv12,
    Nv21,

    // ── RGB / BGR packed ───────────────────────────────────────────────
    Rgb24,
    Bgr24,
    Rgba32,
    Bgra32,
    Argb32,

    // ── Grayscale ──────────────────────────────────────────────────────
    Gray8,
    Gray16,
}
