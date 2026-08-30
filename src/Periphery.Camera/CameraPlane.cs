// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Camera;

/// <summary>
/// Describes a single plane within a camera frame. Multi-planar formats such as
/// NV12 or I420 expose multiple planes with their own strides and extents.
/// </summary>
/// <remarks>
/// <para>
/// <b>Rows are tight.</b> For every plane of every uncompressed frame Periphery
/// delivers, <paramref name="Stride"/> is the plane's natural unpadded row width
/// — <c>CameraFrameLayout.BytesPerRow(format, width)</c> for the packed and luma
/// planes, and the same number again for an NV12 chroma plane, whose
/// <paramref name="Width"/> counts half as many two-byte samples. A platform
/// that pads its rows (Media Foundation aligns the NV12 luma stride to 64 bytes)
/// has that padding removed by the frame pool's copy, which happens on every
/// frame regardless. The invariant is asserted in the pool, not assumed
/// (ADR-0081 D1).
/// </para>
/// <para>
/// The field stays on the record even though it is now derivable, because a
/// stated number is checkable where a derived one is an assumption every call
/// site re-derives (ADR-0081 D4). Read it rather than computing
/// <c>Buffer.Length / Height</c>.
/// </para>
/// </remarks>
public readonly record struct CameraPlane(
    ReadOnlyMemory<byte> Buffer,
    int Stride,
    int Width,
    int Height);
