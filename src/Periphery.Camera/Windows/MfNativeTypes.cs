// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Camera.Windows;

/// <summary>
/// A native media type as the source reader lists it. <see cref="MinFrameRate"/> and
/// <see cref="MaxFrameRate"/> are what <see cref="CameraFormat"/> reports for the type;
/// <see cref="FrameRate"/> is <c>MF_MT_FRAME_RATE</c>, the rate the type runs at unless another
/// is written into it, or null when the type does not carry one.
/// </summary>
internal readonly record struct MfNativeType(
    Guid Subtype,
    int Width,
    int Height,
    Rational MinFrameRate,
    Rational MaxFrameRate,
    Rational? FrameRate);

/// <summary>
/// Which native type a <see cref="CameraConfiguration"/> opens. No Media Foundation calls, so it
/// is tested on every platform.
/// </summary>
internal static class MfNativeTypes
{
    /// <summary>
    /// The index of the native type to open, and the frame rate to write into it first, or null to
    /// leave the type's own.
    /// </summary>
    /// <remarks>
    /// A camera commonly lists the same subtype and size once per frame rate, so the size alone
    /// does not choose the rate. The type whose rates match the format's is chosen; without one,
    /// the first of that subtype and size. A target rate the chosen type cannot run at moves to
    /// another type of the same subtype and size that can. A rate no such type can run at is not
    /// written, and the type runs at its own. -1 when no type has the subtype and size.
    /// </remarks>
    internal static (int Index, Rational? FrameRate) Choose(
        IReadOnlyList<MfNativeType> types,
        Guid subtype,
        CameraFormat format,
        Rational? targetFrameRate)
    {
        var candidates = new List<int>();
        var same = -1;
        for (var i = 0; i < types.Count; i++)
        {
            var type = types[i];
            if (type.Subtype != subtype || type.Width != format.Width || type.Height != format.Height)
                continue;
            candidates.Add(i);
            if (same < 0 && Equal(type.MinFrameRate, format.MinFrameRate) && Equal(type.MaxFrameRate, format.MaxFrameRate))
                same = i;
        }

        if (candidates.Count == 0)
            return (-1, null);

        var chosen = same >= 0 ? same : candidates[0];
        if (targetFrameRate is not { Numerator: > 0 } rate)
            return (chosen, null);

        // The chosen type first, then the others in the order the camera lists them.
        foreach (var i in candidates.Where(i => i != chosen).Prepend(chosen))
        {
            var type = types[i];
            if (type.MinFrameRate <= rate && rate <= type.MaxFrameRate)
                return (i, type.FrameRate is { } own && Equal(own, rate) ? null : rate);
        }

        return (chosen, null);
    }

    // 60/1 and 120/2 are the same rate.
    private static bool Equal(Rational a, Rational b) => a.CompareTo(b) == 0;
}
