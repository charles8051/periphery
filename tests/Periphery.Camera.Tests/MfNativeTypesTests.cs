using Periphery.Camera.Windows;

namespace Periphery.Camera.Tests;

/// <summary>
/// Which Media Foundation native type a configuration opens. Pure, so these run on every
/// platform.
/// </summary>
public sealed class MfNativeTypesTests
{
    private static readonly Guid Nv12 = new("3231564E-0000-0010-8000-00AA00389B71");
    private static readonly Guid Uyvy = new("59565955-0000-0010-8000-00AA00389B71");

    // A type whose advertised range is its one rate.
    private static MfNativeType Fixed(Guid subtype, int width, int height, int fps) =>
        new(subtype, width, height, new Rational(fps), new Rational(fps), new Rational(fps), AdvertisesRange: true);

    private static MfNativeType Range(Guid subtype, int width, int height, int min, int max, int own) =>
        new(subtype, width, height, new Rational(min), new Rational(max), new Rational(own), AdvertisesRange: true);

    // A type with MF_MT_FRAME_RATE and no range attributes: the format reports the default minimum of 1.
    private static MfNativeType Bare(Guid subtype, int width, int height, int fps) =>
        new(subtype, width, height, new Rational(1), new Rational(fps), new Rational(fps), AdvertisesRange: false);

    private static CameraFormat Format(MfNativeType type) =>
        new(type.Width, type.Height, CameraPixelFormat.Nv12, type.MinFrameRate, type.MaxFrameRate, CameraTransport.Uncompressed);

    // A webcam that lists each rate as its own type, slowest-but-one first.
    private static readonly MfNativeType[] OneTypePerRate =
    [
        Fixed(Nv12, 1920, 1080, 30),
        Fixed(Nv12, 1920, 1080, 60),
        Fixed(Nv12, 1920, 1080, 15),
        Fixed(Uyvy, 1920, 1080, 60),
        Fixed(Nv12, 1280, 720, 60),
    ];

    [Fact]
    public void Choose_OneTypePerRate_OpensTheFormatsOwnRate()
    {
        Assert.Equal((1, (Rational?)null), MfNativeTypes.Choose(OneTypePerRate, Nv12, Format(OneTypePerRate[1]), null));
        Assert.Equal((2, (Rational?)null), MfNativeTypes.Choose(OneTypePerRate, Nv12, Format(OneTypePerRate[2]), null));
    }

    [Fact]
    public void Choose_FormatRatesNoTypeHas_FallsBackToTheFirstOfThatSize()
    {
        var format = Format(OneTypePerRate[1]) with { MinFrameRate = new Rational(24), MaxFrameRate = new Rational(24) };
        Assert.Equal((0, (Rational?)null), MfNativeTypes.Choose(OneTypePerRate, Nv12, format, null));
    }

    [Fact]
    public void Choose_TargetTheChosenTypeRunsAtAlready_WritesNoRate() =>
        Assert.Equal((1, (Rational?)null), MfNativeTypes.Choose(OneTypePerRate, Nv12, Format(OneTypePerRate[1]), new Rational(60)));

    [Fact]
    public void Choose_TargetAnotherTypeOfTheSameSizeRuns_MovesToIt() =>
        Assert.Equal((2, (Rational?)null), MfNativeTypes.Choose(OneTypePerRate, Nv12, Format(OneTypePerRate[1]), new Rational(15)));

    [Fact]
    public void Choose_TargetInsideARange_WritesTheRate()
    {
        MfNativeType[] types = [Range(Nv12, 1280, 720, min: 5, max: 60, own: 30)];
        Assert.Equal((0, (Rational?)new Rational(60)), MfNativeTypes.Choose(types, Nv12, Format(types[0]), new Rational(60)));
    }

    [Fact]
    public void Choose_TypesWithoutRanges_RunOnlyAtTheirOwnRate()
    {
        MfNativeType[] types = [Bare(Nv12, 1920, 1080, 60), Bare(Nv12, 1920, 1080, 15)];
        Assert.Equal((1, (Rational?)null), MfNativeTypes.Choose(types, Nv12, Format(types[0]), new Rational(15)));
    }

    [Fact]
    public void Choose_TargetBelowATypeWithoutARange_IsNotWritten()
    {
        MfNativeType[] types = [Bare(Nv12, 1920, 1080, 60)];
        Assert.Equal((0, (Rational?)null), MfNativeTypes.Choose(types, Nv12, Format(types[0]), new Rational(15)));
    }

    [Fact]
    public void Choose_TargetNoTypeCanRun_KeepsTheChosenTypesOwnRate() =>
        Assert.Equal((1, (Rational?)null), MfNativeTypes.Choose(OneTypePerRate, Nv12, Format(OneTypePerRate[1]), new Rational(120)));

    [Fact]
    public void Choose_RatesCompareByValue()
    {
        var format = Format(OneTypePerRate[1]) with { MinFrameRate = new Rational(120, 2), MaxFrameRate = new Rational(120, 2) };
        Assert.Equal((1, (Rational?)null), MfNativeTypes.Choose(OneTypePerRate, Nv12, format, new Rational(120, 2)));
    }

    [Fact]
    public void Choose_NoTypeOfThatSubtypeAndSize_ReturnsMinusOne()
    {
        Assert.Equal(-1, MfNativeTypes.Choose(OneTypePerRate, Nv12, Format(Fixed(Nv12, 3840, 2160, 30)), null).Index);
        Assert.Equal(-1, MfNativeTypes.Choose(OneTypePerRate, Uyvy, Format(OneTypePerRate[4]), null).Index);
    }
}
