namespace Periphery.Camera.Avalonia.Tests;

/// <summary>
/// The YUV samples every conversion test uses, and the BGRA each one must
/// produce.
/// </summary>
/// <remarks>
/// <para>
/// <b>Derived by hand from the published coefficients, not from the converter.</b>
/// BT.601 limited range, with <c>C = Y - 16</c>, <c>D = U - 128</c>,
/// <c>E = V - 128</c>:
/// </para>
/// <code>
/// B = (298C + 516D        + 128) >> 8
/// G = (298C - 100D - 208E + 128) >> 8
/// R = (298C        + 409E + 128) >> 8
/// </code>
/// <para>Worked, once, for each sample below.</para>
/// <para>
/// <b>Black</b> Y=16 U=128 V=128 → C=D=E=0. Every channel is 128 >> 8 = 0.
/// </para>
/// <para>
/// <b>White</b> Y=235 U=128 V=128 → C=219, D=E=0. 298·219 = 65,262; +128 =
/// 65,390; >> 8 = 255 (65,390 / 256 = 255.4). All three channels.
/// </para>
/// <para>
/// <b>Grey</b> Y=126 U=128 V=128 → C=110, D=E=0. 298·110 = 32,780; +128 =
/// 32,908; >> 8 = 128 (32,908 / 256 = 128.5). All three channels.
/// </para>
/// <para>
/// <b>Red</b> Y=81 U=90 V=240 → C=65, D=-38, E=112. 298·65 = 19,370.
/// B = 19,370 - 19,608 + 128 = -110, which shifts to -1 and clamps to 0.
/// G = 19,370 + 3,800 - 23,296 + 128 = 2 → 0.
/// R = 19,370 + 45,808 + 128 = 65,306 → 255.
/// </para>
/// <para>
/// <b>Green</b> Y=145 U=54 V=34 → C=129, D=-74, E=-94. 298·129 = 38,442.
/// B = 38,442 - 38,184 + 128 = 386 → 1 — <i>not</i> zero, which is what makes
/// this sample worth using.
/// G = 38,442 + 7,400 + 19,552 + 128 = 65,522 → 255.
/// R = 38,442 - 38,446 + 128 = 124 → 0.
/// </para>
/// <para>
/// <b>Blue</b> Y=41 U=240 V=110 → C=25, D=112, E=-18. 298·25 = 7,450.
/// B = 7,450 + 57,792 + 128 = 65,370 → 255.
/// G = 7,450 - 11,200 + 3,744 + 128 = 122 → 0.
/// R = 7,450 - 7,362 + 128 = 216 → 0.
/// </para>
/// </remarks>
internal static class Bt601
{
    public const byte BlackY = 16;
    public const byte WhiteY = 235;
    public const byte GreyY = 126;
    public const byte NeutralU = 128;
    public const byte NeutralV = 128;

    public const byte RedY = 81;
    public const byte RedU = 90;
    public const byte RedV = 240;

    public const byte GreenY = 145;
    public const byte GreenU = 54;
    public const byte GreenV = 34;

    public const byte BlueY = 41;
    public const byte BlueU = 240;
    public const byte BlueV = 110;

    /// <summary>B, G, R, A — the order the converters write.</summary>
    public static readonly byte[] Black = [0, 0, 0, 255];
    public static readonly byte[] White = [255, 255, 255, 255];
    public static readonly byte[] Grey = [128, 128, 128, 255];
    public static readonly byte[] Red = [0, 0, 255, 255];
    public static readonly byte[] Green = [1, 255, 0, 255];
    public static readonly byte[] Blue = [255, 0, 0, 255];

    /// <summary>
    /// Asserts the four bytes at <paramref name="offset"/> are
    /// <paramref name="expected"/>, naming the pixel when they are not.
    /// </summary>
    public static void AssertPixel(byte[] actual, int offset, byte[] expected, string what)
    {
        var slice = actual.AsSpan(offset, 4).ToArray();
        Assert.True(
            slice.AsSpan().SequenceEqual(expected),
            $"{what} at byte {offset}: expected B,G,R,A = {string.Join(",", expected)} "
                + $"but found {string.Join(",", slice)}");
    }
}
