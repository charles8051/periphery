// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using OpenCvSharp;

namespace Periphery.Camera.OpenCvSharp;

/// <summary>
/// How one <see cref="CameraPixelFormat"/> at one size maps onto a
/// <c>cv::Mat</c> header, and what it takes to get from there to BGR. A pure
/// value produced by <see cref="CameraMatLayout.Describe"/>.
/// </summary>
/// <remarks>
/// <para>
/// This is the whole mapping table in one type. Everything else in the package
/// is plumbing around it: <see cref="CameraFrameMatExtensions.AsMat"/> pins a
/// frame and builds the header these numbers describe, and
/// <see cref="CameraFrameMatExtensions.ToBgr"/> follows
/// <see cref="BgrPath"/>.
/// </para>
/// <para>
/// <b>The 4:2:0 formats are the interesting rows.</b> NV12, NV21, I420 and YV12
/// have no <c>MatType</c> of their own — OpenCV reads them as a
/// <c>(height * 3 / 2) × width</c> single-channel surface with the chroma
/// stacked under the luma, and <c>COLOR_YUV2BGR_*</c> decodes that shape. This
/// is exact rather than approximate only because Periphery guarantees tight
/// rows (ADR-0081 D1): OpenCV locates NV12 chroma at <c>src + step * height</c>
/// and reads it at the same <c>step</c>, and for I420 it walks each chroma row
/// as <c>width / 2</c> bytes followed by <c>step - width / 2</c>, which
/// coincides with Periphery's uniform half-luma chroma stride exactly when
/// <c>step == width</c>. Before ADR-0081 a padded I420 frame would have
/// produced a plausible image with the colour drifting down it.
/// </para>
/// </remarks>
/// <param name="Rows">Rows in the <c>Mat</c> header. The image height, except
/// for the 4:2:0 formats where it is <c>height * 3 / 2</c>.</param>
/// <param name="Cols">Columns in the <c>Mat</c> header — the image width for
/// every format in the table.</param>
/// <param name="Type">The <c>Mat</c> element type.</param>
/// <param name="Step">
/// Bytes from one <c>Mat</c> row to the next, which for a delivered frame is
/// <see cref="CameraFrameLayout.BytesPerRow"/> and therefore also the frame's
/// plane-0 stride. Equal to <c>Cols * Type.ElemSize()</c> in every row of the
/// table, so <c>Mat.AUTO_STEP</c> would compute the same number; it is stated
/// because a stated number is checkable — <c>Rows * Step</c> must equal
/// <see cref="CameraFrameLayout.FrameSize"/>, and that equation is what stops
/// this table drifting away from the layout it describes.
/// </param>
/// <param name="BgrConversion">
/// The single <c>cvtColor</c> code that takes this shape to CV_8UC3 BGR, or
/// <see langword="null"/> when no single code does. Non-null exactly when
/// <see cref="BgrPath"/> is <see cref="CameraMatBgrPath.CvtColor"/>.
/// </param>
/// <param name="BgrPath">What <see cref="CameraFrameMatExtensions.ToBgr"/> has
/// to do to reach BGR.</param>
public readonly record struct CameraMatShape(
    int Rows,
    int Cols,
    MatType Type,
    int Step,
    ColorConversionCodes? BgrConversion,
    CameraMatBgrPath BgrPath)
{
    /// <summary>
    /// Bytes the <c>Mat</c> header spans: <c>Rows * Step</c>. A frame's buffer
    /// must be at least this long before a header is built over it.
    /// </summary>
    public long ByteLength => (long)Rows * Step;
}

/// <summary>
/// What it takes to get from a frame's own <c>Mat</c> shape to CV_8UC3 BGR.
/// </summary>
public enum CameraMatBgrPath
{
    /// <summary>
    /// The frame is already CV_8UC3 BGR. <see cref="CameraFrameMatExtensions.ToBgr"/>
    /// clones it and nothing else. <see cref="CameraPixelFormat.Bgr24"/> only.
    /// </summary>
    AlreadyBgr,

    /// <summary>
    /// One <c>Cv2.CvtColor</c> call with <see cref="CameraMatShape.BgrConversion"/>.
    /// Every packed RGB format except ARGB32, both packed YUV formats,
    /// <see cref="CameraPixelFormat.Gray8"/>, and all four 4:2:0 formats.
    /// </summary>
    CvtColor,

    /// <summary>
    /// A channel shuffle via <c>Cv2.MixChannels</c>, because OpenCV has no
    /// <c>ARGB2*</c> conversion code at all.
    /// <see cref="CameraPixelFormat.Argb32"/> only.
    /// </summary>
    ArgbShuffle,

    /// <summary>
    /// No conversion this package is willing to choose.
    /// <see cref="CameraPixelFormat.Gray16"/> only: narrowing 16 bits to 8 is a
    /// range decision that belongs to the device, not to a converter. See
    /// <see cref="CameraFrameMatExtensions.ToBgr"/> for what to call instead.
    /// </summary>
    CallerDefined,
}
